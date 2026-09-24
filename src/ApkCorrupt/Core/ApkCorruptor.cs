using System.IO.Compression;
using Android.Graphics;

namespace ApkCorrupt.Core;

public static class ApkCorruptor
{
    private static readonly HashSet<string> SignatureSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".RSA", ".DSA", ".EC", ".SF"
    };

    private static readonly HashSet<string> UnityLooseExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg",
        ".wav", ".ogg", ".mp3", ".m4a", ".aif", ".aiff",
        ".mp4", ".m4v", ".mov", ".webm"
    };

    private static bool IsSignatureEntry(string name)
    {
        // Old APKs can contain this 32-byte signing stamp. It belongs to the
        // original signing metadata and must not be copied into the rebuilt APK.
        if (name.Equals("stamp-cert-sha256", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
            return false;

        var upper = name.ToUpperInvariant();
        if (upper.EndsWith("/MANIFEST.MF"))
            return true;

        foreach (var suffix in SignatureSuffixes)
        {
            if (upper.EndsWith(suffix))
                return true;
        }

        return false;
    }

    private static bool IsUnityCandidate(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("/bundles/")
            || lower.Contains("/streamingassets/")
            || lower.EndsWith("/data.unity3d")
            || lower.EndsWith(".assets")
            || lower.EndsWith(".sharedassets")
            || lower.EndsWith(".unity3d")
            || lower.EndsWith(".bundle")
            // Unity uses .resource for FMOD/FSB5 audio banks. Treat these
            // as Unity candidates so the structured FSB5 mutator can edit
            // the audio payload without touching unrelated APK files.
            || lower.EndsWith(".resource");
    }

    public static async Task<CorruptionResult> CorruptAsync(
        string sourceApk,
        CorruptionOptions options,
        IProgress<CorruptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourceApk))
            throw new FileNotFoundException("Source APK not found.", sourceApk);

        var workDir = Path.Combine(FileSystem.CacheDirectory, $"corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        var unsigned = Path.Combine(workDir, "unsigned.apk");
        var signed = Path.Combine(workDir, "signed.apk");
        var keyPath = Path.Combine(workDir, "signing.pk8");
        var certPath = Path.Combine(workDir, "signing.der");

        var rng = new Random(options.Seed);
        var textureCount = 0;
        var audioCount = 0;
        var materialCount = 0;
        var textAssetCount = 0;
        var meshCount = 0;
        var videoCount = 0;
        var lightCount = 0;
        var iconCount = 0;
        var filesChanged = 0;
        var candidateIndex = 0;

        try
        {
            using (var input = ZipFile.OpenRead(sourceApk))
            using (var outputStream = File.Create(unsigned))
            using (var output = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var entry in input.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrEmpty(entry.Name))
                        continue;

                    if (IsSignatureEntry(entry.FullName))
                        continue;

                    var isUnity = IsUnityCandidate(entry.FullName);
                    var isLoose = options.LooseAssets
                        && UnityLooseExtensions.Contains(Path.GetExtension(entry.FullName));

                    byte[]? mutated = null;

                    if (isUnity)
                    {
                        // Use a unique working filename: Unity APKs commonly contain
                        // repeated basenames across different bundle/resource folders.
                        var safeName = string.Concat(
                            Path.GetFileName(entry.FullName)
                                .Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'
                                    ? c
                                    : '_'));

                        if (string.IsNullOrWhiteSpace(safeName))
                            safeName = "candidate.bin";

                        var candidate = Path.Combine(
                            workDir,
                            $"candidate-{candidateIndex++:D6}-{safeName}");

                        await using (var src = entry.Open())
                        await using (var dst = File.Create(candidate))
                        {
                            await src.CopyToAsync(dst, cancellationToken);
                        }

                        if (options.Textures || options.Audio)
                        {
                            var mutation = await UnityMutationEngine.TryMutateAsync(
                                candidate,
                                options,
                                rng,
                                cancellationToken);

                            if (mutation.Changed)
                            {
                                mutated = await File.ReadAllBytesAsync(
                                    mutation.OutputPath,
                                    cancellationToken);

                                textureCount += mutation.TexturesChanged;
                                audioCount += mutation.AudioChanged;
                                materialCount += mutation.MaterialsChanged;
                                textAssetCount += mutation.TextAssetsChanged;
                                meshCount += mutation.MeshesChanged;
                                videoCount += mutation.VideosChanged;
                                lightCount += mutation.LightsChanged;
                                filesChanged++;
                            }
                        }
                    }
                    else if (IsGameIcon(entry.FullName))
                    {
                        await using var stream = entry.Open();
                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms, cancellationToken);
                        var bytes = ms.ToArray();

                        var icon = MutateGameIcon(bytes, options.Intensity, rng);
                        if (icon is not null)
                        {
                            mutated = icon;
                            iconCount++;
                            filesChanged++;
                        }
                    }
                    else if (isLoose)
                    {
                        await using var stream = entry.Open();
                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms, cancellationToken);
                        var bytes = ms.ToArray();

                        if (ShouldMutateLoose(entry.FullName))
                        {
                            if (IsVideo(entry.FullName))
                            {
                                if (MutateVideoBytesInPlace(bytes, options.Intensity, rng))
                                {
                                    mutated = bytes;
                                    videoCount++;
                                    filesChanged++;
                                }
                            }
                            else
                            {
                                MutateBytesInPlace(bytes, options.Intensity, rng);
                                mutated = bytes;

                                if (IsAudio(entry.FullName))
                                    audioCount++;
                                else
                                    textureCount++;

                                filesChanged++;
                            }
                        }
                    }

                    var compression = NeedsStore(entry.FullName)
                        ? CompressionLevel.NoCompression
                        : CompressionLevel.Fastest;

                    var outEntry = output.CreateEntry(entry.FullName, compression);

                    await using var outStream = outEntry.Open();
                    if (mutated is not null)
                    {
                        await outStream.WriteAsync(mutated, cancellationToken);
                    }
                    else
                    {
                        await using var inStream = entry.Open();
                        await inStream.CopyToAsync(outStream, cancellationToken);
                    }

                    progress?.Report(new CorruptionProgress(
                        $"Processing {entry.FullName}",
                        textureCount,
                        audioCount,
                        filesChanged,
                        materialCount,
                        textAssetCount,
                        meshCount,
                        videoCount,
                        lightCount,
                        iconCount));
                }
            }

            await ApkSignerBridge.GenerateKeyMaterialAsync(
                keyPath,
                certPath,
                cancellationToken);

            await ApkSignerBridge.AlignAndSignAsync(
                unsigned,
                signed,
                keyPath,
                certPath,
                cancellationToken);

            var filename = $"APKCorrupt_{DateTime.Now:yyyyMMdd_HHmmss}.apk";
            var finalPath = await ApkExport.CopyToDownloadsAsync(
                signed,
                filename,
                cancellationToken);

            return new CorruptionResult(
                finalPath,
                textureCount,
                audioCount,
                filesChanged,
                materialCount,
                textAssetCount,
                meshCount,
                videoCount,
                lightCount,
                iconCount);
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    private static bool IsGameIcon(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.StartsWith("res/mipmap-")
            && lower.EndsWith(".png")
            && (lower.Contains("/app_icon.png")
                || lower.Contains("/app_icon_round.png"));
    }

    private static bool ShouldMutateLoose(string name)
    {
        var ext = Path.GetExtension(name);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".aif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".aiff", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAudio(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".wav" or ".ogg" or ".mp3" or ".m4a" or ".aif" or ".aiff";
    }

    private static bool NeedsStore(string name)
    {
        var lower = name.ToLowerInvariant();

        // Unity's binary data is intentionally stored uncompressed in APKs.
        // Compressing these entries forces Android/Unity to inflate large
        // AssetBundle/resource files before they can be consumed, which causes
        // dramatically slower loading times on device.
        if (lower.StartsWith("assets/bin/data/")
            && (lower.EndsWith(".unity3d")
                || lower.EndsWith(".assets")
                || lower.EndsWith(".sharedassets")
                || lower.EndsWith(".resource")
                || lower.EndsWith(".ress")
                || lower.EndsWith(".bundle")))
            return true;

        return lower.Equals("resources.arsc")
            || lower.EndsWith(".so")
            || lower.Contains("/lib/");
    }

    private static bool IsVideo(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".mp4" or ".m4v" or ".mov" or ".webm";
    }

    private static byte[]? MutateGameIcon(
        byte[] bytes,
        int intensity,
        Random rng)
    {
        if (bytes.Length < 32)
            return null;

        try
        {
            using var bitmap = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
            if (bitmap is null || bitmap.Width == 0 || bitmap.Height == 0)
                return null;

            var pixels = new int[bitmap.Width * bitmap.Height];
            bitmap.GetPixels(pixels, 0, bitmap.Width, 0, 0, bitmap.Width, bitmap.Height);

            var level = Math.Clamp(intensity, 1, 100) / 100.0;

            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var p = pixels[(y * bitmap.Width) + x];
                    var a = (byte)((uint)p >> 24);
                    var r = (byte)((uint)p >> 16);
                    var g = (byte)((uint)p >> 8);
                    var b = (byte)p;

                    // Preserve the same source icon while making it look visibly
                    // corrupted: channel swaps, posterization and small color
                    // jitter. Alpha is preserved.
                    if (((x / 6) + (y / 6)) % 3 == 0)
                        (r, b) = (b, r);

                    var posterize = intensity >= 85 ? 32 : 16;
                    r = (byte)((r / posterize) * posterize);
                    g = (byte)((g / posterize) * posterize);
                    b = (byte)((b / posterize) * posterize);

                    var jitter = Math.Max(1, (int)(level * 42));
                    if (rng.Next(100) < (int)(8 + 22 * level))
                    {
                        r = (byte)Math.Clamp(r + rng.Next(-jitter, jitter + 1), 0, 255);
                        g = (byte)Math.Clamp(g + rng.Next(-jitter, jitter + 1), 0, 255);
                        b = (byte)Math.Clamp(b + rng.Next(-jitter, jitter + 1), 0, 255);
                    }

                    pixels[(y * bitmap.Width) + x] =
                        unchecked((int)((uint)a << 24 | (uint)r << 16 | (uint)g << 8 | b));
                }
            }

            bitmap.SetPixels(
                pixels,
                0,
                bitmap.Width,
                0,
                0,
                bitmap.Width,
                bitmap.Height);

            using var output = new MemoryStream();
            if (!bitmap.Compress(Bitmap.CompressFormat.Png, 100, output))
                return null;

            var encoded = output.ToArray();
            return encoded.Length == 0 ? null : encoded;
        }
        catch
        {
            return null;
        }
    }


    private static bool MutateVideoBytesInPlace(byte[] bytes, int intensity, Random rng)
    {
        if (bytes.Length < 32)
            return false;

        var ranges = new List<(int Start, int End)>();
        var pos = 0;

        while (pos + 8 <= bytes.Length)
        {
            var size = ReadUInt32BE(bytes, pos);
            var type = System.Text.Encoding.ASCII.GetString(bytes, pos + 4, 4);

            long end;
            var header = 8;

            if (size == 1 && pos + 16 <= bytes.Length)
            {
                var large = ReadUInt64BE(bytes, pos + 8);
                if (large < 16 || large > (ulong)(bytes.Length - pos))
                    break;

                end = pos + (long)large;
                header = 16;
            }
            else if (size >= 8 && size <= bytes.Length - pos)
            {
                end = pos + size;
            }
            else
            {
                break;
            }

            if (type == "mdat" && end > pos + header)
                ranges.Add((pos + header, (int)end));

            pos = (int)end;
        }

        if (ranges.Count == 0)
            return false;

        var level = Math.Clamp(intensity, 1, 100) / 100.0;
        foreach (var range in ranges)
        {
            var available = range.End - range.Start;
            var touches = (int)Math.Clamp(
                available * (0.003 + 0.025 * level),
                8,
                200000);

            for (var i = 0; i < touches; i++)
            {
                var idx = rng.Next(range.Start, range.End);
                bytes[idx] &= intensity >= 85 ? (byte)0xC0 : (byte)0xE0;

                if (rng.Next(100) < 12)
                    bytes[idx] ^= (byte)(1 << rng.Next(8));
            }

            if (level >= 0.6)
            {
                var bursts = Math.Clamp(available / 262144, 1, 16);
                for (var i = 0; i < bursts; i++)
                {
                    var burstLen = Math.Min(160, Math.Max(12, available / 96));
                    var start = rng.Next(range.Start, Math.Max(range.Start + 1, range.End - burstLen + 1));
                    for (var j = 0; j < burstLen; j++)
                        bytes[start + j] ^= (byte)rng.Next(1, 24);
                }
            }
        }

        return true;
    }

    private static uint ReadUInt32BE(byte[] bytes, int offset)
    {
        return ((uint)bytes[offset] << 24)
            | ((uint)bytes[offset + 1] << 16)
            | ((uint)bytes[offset + 2] << 8)
            | bytes[offset + 3];
    }

    private static ulong ReadUInt64BE(byte[] bytes, int offset)
    {
        return ((ulong)ReadUInt32BE(bytes, offset) << 32)
            | ReadUInt32BE(bytes, offset + 4);
    }

    private static void MutateBytesInPlace(byte[] bytes, int intensity, Random rng)
    {
        if (bytes.Length < 32)
            return;

        var count = Math.Max(
            1,
            (bytes.Length * Math.Clamp(intensity, 1, 100)) / 500);

        var start = bytes.Length / 20;
        var end = bytes.Length - 4;

        for (var i = 0; i < count; i++)
        {
            var index = rng.Next(start, Math.Max(start + 1, end));
            var mode = rng.Next(4);

            switch (mode)
            {
                case 0:
                    bytes[index] ^= (byte)(1 << rng.Next(0, 8));
                    break;
                case 1:
                    bytes[index] = (byte)rng.Next(0, 256);
                    break;
                case 2:
                    bytes[index] = unchecked(
                        (byte)(bytes[index] + rng.Next(17, 97)));
                    break;
                default:
                    bytes[index] = unchecked(
                        (byte)(bytes[index] - rng.Next(17, 97)));
                    break;
            }
        }
    }
}
