using System.IO.Compression;

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
        ".wav", ".ogg", ".mp3", ".m4a", ".aif", ".aiff"
    };

    private static bool IsSignatureEntry(string name)
    {
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
            || lower.Contains("/bin/data/")
            || lower.EndsWith(".assets")
            || lower.EndsWith(".sharedassets")
            || lower.EndsWith(".unity3d")
            || lower.EndsWith(".bundle");
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
                                filesChanged++;
                            }
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
                            MutateBytesInPlace(bytes, options.Intensity, rng);
                            mutated = bytes;

                            if (IsAudio(entry.FullName))
                                audioCount++;
                            else
                                textureCount++;

                            filesChanged++;
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
                        filesChanged));
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
                filesChanged);
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
            || ext.Equals(".aiff", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAudio(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".wav" or ".ogg" or ".mp3" or ".m4a" or ".aif" or ".aiff";
    }

    private static bool NeedsStore(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Equals("resources.arsc")
            || lower.EndsWith(".so")
            || lower.Contains("/lib/");
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
