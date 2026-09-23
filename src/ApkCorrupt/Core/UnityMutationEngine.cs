using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace ApkCorrupt.Core;

public static class UnityMutationEngine
{
    private const uint Fsb5Vorbis = 15;

    public sealed record MutationResult(
        bool Changed,
        string OutputPath,
        int TexturesChanged,
        int AudioChanged);

    public static async Task<MutationResult> TryMutateAsync(
        string inputPath,
        CorruptionOptions options,
        Random rng,
        CancellationToken cancellationToken)
    {
        var outputPath = inputPath + ".mutated";

        if (IsFsb5(inputPath))
        {
            if (!options.Audio)
                return new MutationResult(false, inputPath, 0, 0);

            return await MutateFsb5Async(
                inputPath, outputPath, options, rng, cancellationToken);
        }

        if (IsStandaloneAssets(inputPath))
        {
            return await MutateStandaloneAssetsAsync(
                inputPath, outputPath, options, rng, cancellationToken);
        }

        if (options.Textures || options.Audio)
        {
            try
            {
                if (IsUnityBundle(inputPath))
                {
                    return await MutateBundleAsync(
                        inputPath, outputPath, options, rng, cancellationToken);
                }
            }
            catch
            {
                // An unsupported Unity bundle is left untouched.
            }
        }

        return new MutationResult(false, inputPath, 0, 0);
    }

    private static bool IsFsb5(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4
                && magic.SequenceEqual("FSB5"u8);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsStandaloneAssets(string path)
    {
        var lower = path.ToLowerInvariant();
        return lower.EndsWith(".assets")
            || lower.EndsWith(".sharedassets")
            || lower.Contains("globalgamemanagers");
    }

    private static bool IsUnityBundle(string path)
    {
        var lower = path.ToLowerInvariant();

        if (lower.EndsWith(".bundle") || lower.EndsWith(".unity3d"))
            return true;

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[8];
            var read = stream.Read(magic);

            if (read < 7)
                return false;

            var signature = System.Text.Encoding.ASCII.GetString(magic[..7]);
            return signature is "UnityFS" or "UnityRaw" or "UnityWeb";
        }
        catch
        {
            return false;
        }
    }

    private static async Task<MutationResult> MutateFsb5Async(
        string inputPath,
        string outputPath,
        CorruptionOptions options,
        Random rng,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(inputPath, cancellationToken);

        var position = 0;
        var sampleCount = 0;
        var containerCount = 0;
        var changed = false;

        // Unity's .resource files can contain many FSB5 containers concatenated
        // back-to-back. A previous implementation only touched the first one,
        // which is why audio often appeared completely unchanged.
        while (position + 0x3C <= bytes.Length
               && bytes.AsSpan(position, 4).SequenceEqual("FSB5"u8))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryParseFsb5Container(bytes, position, out var fsb))
                break;

            foreach (var sample in fsb.Samples)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (sample.End <= sample.Start)
                    continue;

                if (fsb.Mode == Fsb5Vorbis)
                {
                    if (MutateFsb5VorbisSample(
                        bytes,
                        sample.Start,
                        sample.End,
                        options.Intensity,
                        rng))
                    {
                        sampleCount++;
                        changed = true;
                    }
                }
                else
                {
                    if (MutateEncodedRegion(
                        bytes,
                        sample.Start,
                        sample.End - sample.Start,
                        options.Intensity,
                        rng,
                        preservePrefix: 16))
                    {
                        sampleCount++;
                        changed = true;
                    }
                }
            }

            containerCount++;
            position = fsb.End;
        }

        if (!changed)
            return new MutationResult(false, inputPath, 0, 0);

        await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);

        return new MutationResult(
            true,
            outputPath,
            0,
            sampleCount);
    }

    private static bool TryParseFsb5Container(
        byte[] bytes,
        int containerStart,
        out Fsb5Info fsb)
    {
        fsb = default;

        if (containerStart < 0
            || containerStart + 0x3C > bytes.Length
            || !bytes.AsSpan(containerStart, 4).SequenceEqual("FSB5"u8))
            return false;

        try
        {
            var version = ReadUInt32LE(bytes, containerStart + 4);
            if (version is not (0u or 1u))
                return false;

            var numSamples = ReadUInt32LE(bytes, containerStart + 8);
            var sampleHeadersSize = ReadUInt32LE(bytes, containerStart + 12);
            var nameTableSize = ReadUInt32LE(bytes, containerStart + 16);
            var dataSize = ReadUInt32LE(bytes, containerStart + 20);
            var mode = ReadUInt32LE(bytes, containerStart + 24);

            if (numSamples == 0 || numSamples > 100000)
                return false;

            var headerSize = version == 0 ? 0x40 : 0x3C;

            var sampleHeadersStart = checked(
                containerStart + headerSize);

            var sampleHeadersEnd = checked(
                (long)sampleHeadersStart + sampleHeadersSize);

            if (sampleHeadersEnd > bytes.Length)
                return false;

            var dataStart = checked(
                sampleHeadersEnd + nameTableSize);

            var dataEnd = checked(
                dataStart + dataSize);

            if (dataEnd > bytes.Length)
                return false;

            var sampleHeaderPos = sampleHeadersStart;
            var offsets = new List<ulong>(
                checked((int)numSamples));

            for (var i = 0u; i < numSamples; i++)
            {
                if ((long)sampleHeaderPos + 8 > sampleHeadersEnd)
                    return false;

                var raw = ReadUInt64LE(bytes, sampleHeaderPos);
                sampleHeaderPos += 8;

                // FSB5 sample header:
                // bit 0 = more metadata chunks
                // bits 1..4 = frequency index
                // bit 5 = stereo flag
                // bits 6..33 = data offset in 16-byte units
                // bits 34..63 = decoded sample count
                var hasChunks = (raw & 1UL) != 0;
                var dataOffset = ((raw >> 6) & 0x0FFFFFFFUL) * 16UL;
                offsets.Add(dataOffset);

                while (hasChunks)
                {
                    if ((long)sampleHeaderPos + 4 > sampleHeadersEnd)
                        return false;

                    var chunkHeader = ReadUInt32LE(bytes, sampleHeaderPos);
                    sampleHeaderPos += 4;

                    hasChunks = (chunkHeader & 1u) != 0;
                    var chunkSize = (chunkHeader >> 1) & 0x00FFFFFFu;

                    if ((long)sampleHeaderPos + chunkSize > sampleHeadersEnd)
                        return false;

                    sampleHeaderPos += checked((int)chunkSize);
                }

                // FSB5 sample headers are padded to 16-byte boundaries.
                sampleHeaderPos = Align16(
                    sampleHeaderPos,
                    sampleHeadersStart);

                if ((long)sampleHeaderPos > sampleHeadersEnd)
                    return false;
            }

            var samples = new List<Fsb5Sample>(offsets.Count);

            for (var i = 0; i < offsets.Count; i++)
            {
                var startOffset = offsets[i];
                var endOffset = (ulong)dataSize;

                if (i + 1 < offsets.Count)
                    endOffset = offsets[i + 1];

                if (startOffset >= (ulong)dataSize
                    || endOffset > (ulong)dataSize
                    || endOffset <= startOffset)
                    continue;

                var start = checked((int)((ulong)dataStart + startOffset));
                var end = checked((int)((ulong)dataStart + endOffset));

                samples.Add(new Fsb5Sample(start, end));
            }

            if (samples.Count == 0)
                return false;

            fsb = new Fsb5Info(
                mode,
                samples,
                checked((int)dataEnd));

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int Align16(
        int value,
        int origin)
    {
        var relative = value - origin;
        return origin + ((relative + 15) / 16 * 16);
    }

    private static bool MutateFsb5VorbisSample(
        byte[] bytes,
        int start,
        int end,
        int intensity,
        Random rng)
    {
        var ranges = new List<(int Start, int End)>();

        var pos = start;
        var packetIndex = 0;

        while (pos + 2 <= end)
        {
            var packetSize = ReadUInt16LE(bytes, pos);
            if (packetSize == 0)
                break;

            // The FSB5 Vorbis packet size includes the one-byte audio/continuation
            // flag that follows the uint16 size.
            var packetEnd = checked(pos + 2 + packetSize);
            if (packetEnd > end)
                break;

            var payloadStart = pos + 3;
            if (packetIndex >= 3 && payloadStart < packetEnd)
                ranges.Add((payloadStart, packetEnd));

            pos = packetEnd;
            packetIndex++;
        }

        if (ranges.Count == 0)
            return false;

        var level = Math.Clamp(intensity, 1, 100) / 100.0;
        var totalMutable = ranges.Sum(r => (long)r.End - r.Start);

        // Make the effect unmistakable. We deliberately preserve packet
        // framing and the first three Vorbis packets (identification/comments/
        // setup), but aggressively damage the actual audio packets.
        var fraction = 0.04 + (0.36 * level);
        var touches = (int)Math.Clamp(
            totalMutable * fraction,
            8,
            250000);

        for (var i = 0; i < touches; i++)
        {
            var range = ranges[rng.Next(ranges.Count)];
            var index = rng.Next(range.Start, range.End);

            switch (rng.Next(5))
            {
                case 0:
                    bytes[index] ^= 0xFF;
                    break;

                case 1:
                    bytes[index] ^= (byte)(1 << rng.Next(8));
                    break;

                case 2:
                    bytes[index] = (byte)rng.Next(0, 256);
                    break;

                case 3:
                    bytes[index] = unchecked(
                        (byte)(bytes[index] + rng.Next(32, 224)));
                    break;

                default:
                    bytes[index] = unchecked(
                        (byte)(bytes[index] - rng.Next(32, 224)));
                    break;
            }
        }

        // Add occasional contiguous glitches at higher intensities. This tends
        // to create audible clicks/tears rather than a mostly transparent bit
        // error that a Vorbis decoder can conceal.
        if (level >= 0.50)
        {
            var glitchCount = Math.Clamp(
                ranges.Count / 12,
                1,
                120);

            for (var i = 0; i < glitchCount; i++)
            {
                var range = ranges[rng.Next(ranges.Count)];
                var maxLength = Math.Min(
                    512,
                    Math.Max(8, range.End - range.Start));

                var glitchLength = Math.Min(
                    maxLength,
                    8 + rng.Next(Math.Max(1, maxLength - 7)));

                if (glitchLength <= 0)
                    continue;

                var startIndex = rng.Next(
                    range.Start,
                    range.End - glitchLength + 1);

                for (var j = 0; j < glitchLength; j++)
                {
                    bytes[startIndex + j] ^= (byte)rng.Next(1, 256);
                }
            }
        }

        return touches > 0;
    }

    private static async Task<MutationResult> MutateStandaloneAssetsAsync(
        string inputPath,
        string outputPath,
        CorruptionOptions options,
        Random rng,
        CancellationToken cancellationToken)
    {
        var manager = new AssetsManager();
        var instance = manager.LoadAssetsFile(inputPath, false);

        var textures = 0;
        var audio = 0;

        foreach (var info in instance.file.GetAssetsOfType(AssetClassID.Texture2D))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!options.Textures || !ShouldHit(options.Intensity, rng))
                continue;

            try
            {
                if (TryMutateTexture(
                    manager,
                    instance,
                    info,
                    null,
                    options.Intensity,
                    rng))
                {
                    textures++;
                }
            }
            catch
            {
                // Unsupported texture: skip it and keep the file usable.
            }
        }

        foreach (var info in instance.file.GetAssetsOfType(AssetClassID.AudioClip))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!options.Audio || !ShouldHit(options.Intensity, rng))
                continue;

            try
            {
                var baseField = manager.GetBaseField(instance, info);
                var audioData = baseField["m_AudioData"];

                if (audioData.IsDummy
                    || audioData.TemplateField.ValueType != AssetValueType.ByteArray)
                    continue;

                var bytes = audioData.AsByteArray;
                if (bytes.Length <= 32)
                    continue;

                if (!MutateEncodedRegion(
                    bytes,
                    0,
                    bytes.Length,
                    options.Intensity,
                    rng,
                    preservePrefix: 32))
                    continue;

                audioData.AsByteArray = bytes;
                info.SetNewData(baseField);
                audio++;
            }
            catch
            {
                // External streamed audio requires the resource container.
            }
        }

        if (textures == 0 && audio == 0)
            return new MutationResult(false, inputPath, 0, 0);

        await Task.Run(() =>
        {
            using var writer = new AssetsFileWriter(outputPath);
            instance.file.Write(writer);
        }, cancellationToken);

        return new MutationResult(true, outputPath, textures, audio);
    }

    private static async Task<MutationResult> MutateBundleAsync(
        string inputPath,
        string outputPath,
        CorruptionOptions options,
        Random rng,
        CancellationToken cancellationToken)
    {
        var manager = new AssetsManager();
        var bundle = manager.LoadBundleFile(inputPath, true);

        var textures = 0;
        var audio = 0;

        for (var i = 0; i < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dirInfo = bundle.file.BlockAndDirInfo.DirectoryInfos[i];
            if (!dirInfo.IsSerialized)
                continue;

            AssetsFileInstance assets;
            try
            {
                assets = manager.LoadAssetsFileFromBundle(bundle, i, false);
            }
            catch
            {
                continue;
            }

            foreach (var info in assets.file.GetAssetsOfType(AssetClassID.Texture2D))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!options.Textures || !ShouldHit(options.Intensity, rng))
                    continue;

                try
                {
                    if (TryMutateTexture(
                        manager,
                        assets,
                        info,
                        bundle.file,
                        options.Intensity,
                        rng))
                    {
                        textures++;
                    }
                }
                catch
                {
                    // Unsupported texture/type tree: skip.
                }
            }

            foreach (var info in assets.file.GetAssetsOfType(AssetClassID.AudioClip))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!options.Audio || !ShouldHit(options.Intensity, rng))
                    continue;

                try
                {
                    var baseField = manager.GetBaseField(assets, info);
                    var audioData = baseField["m_AudioData"];

                    if (!audioData.IsDummy
                        && audioData.TemplateField.ValueType == AssetValueType.ByteArray)
                    {
                        var bytes = audioData.AsByteArray;
                        if (bytes.Length > 32
                            && MutateEncodedRegion(
                                bytes,
                                0,
                                bytes.Length,
                                options.Intensity,
                                rng,
                                preservePrefix: 32))
                        {
                            audioData.AsByteArray = bytes;
                            info.SetNewData(baseField);
                            audio++;
                            continue;
                        }
                    }

                    if (TryMutateExternalAudio(
                        bundle.file,
                        baseField,
                        options.Intensity,
                        rng))
                    {
                        audio++;
                    }
                }
                catch
                {
                    // A single AudioClip must never abort the entire bundle.
                }
            }

            dirInfo.SetNewData(assets.file);
        }

        var anyBundleChanges = textures > 0 || audio > 0;
        if (!anyBundleChanges)
            return new MutationResult(false, inputPath, 0, 0);

        await Task.Run(() =>
        {
            using var writer = new AssetsFileWriter(outputPath);
            bundle.file.Write(writer);
        }, cancellationToken);

        return new MutationResult(true, outputPath, textures, audio);
    }

    private static bool TryMutateTexture(
        AssetsManager manager,
        AssetsFileInstance assets,
        AssetFileInfo info,
        AssetBundleFile? bundle,
        int intensity,
        Random rng)
    {
        var baseField = manager.GetBaseField(assets, info);
        var texture = TextureFile.ReadTextureFile(baseField);

        var hasExternalStream =
            !string.IsNullOrWhiteSpace(texture.m_StreamData.path)
            && texture.m_StreamData.size != 0;

        // When the image bytes live in a bundle-side .resS/resource entry,
        // mutate that exact byte range and leave Texture2D's stream reference
        // untouched. This is the important path for textures that previously
        // made the counter stall at a small number.
        if (hasExternalStream && bundle is not null)
        {
            if (TryMutateExternalTexture(
                bundle,
                texture.m_StreamData.path,
                texture.m_StreamData.offset,
                texture.m_StreamData.size,
                intensity,
                rng))
            {
                return true;
            }
        }

        var data = texture.FillPictureData(assets);
        if (data is not { Length: > 0 })
            return false;

        // Do not decode/re-encode here. Directly corrupting the texture's
        // existing encoded format avoids losing ETC/ASTC/BC/crunched formats
        // and avoids turning a compressed texture into a different format.
        if (!MutateTextureEncodedData(
            data,
            (TextureFormat)texture.m_TextureFormat,
            intensity,
            rng))
            return false;

        texture.SetPictureData(
            data,
            texture.m_Width,
            texture.m_Height);

        texture.WriteTo(baseField);
        info.SetNewData(baseField);

        return true;
    }

    private static bool TryMutateExternalTexture(
        AssetBundleFile bundle,
        string sourcePath,
        ulong offset,
        uint size,
        int intensity,
        Random rng)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)
            || size == 0
            || offset > int.MaxValue)
            return false;

        var normalized = sourcePath.Replace('\\', '/');
        var name = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var resourceIndex = ResolveResourceIndex(bundle, name);
        if (resourceIndex < 0)
            return false;

        var resourceInfo = bundle.BlockAndDirInfo.DirectoryInfos[resourceIndex];
        bundle.GetFileRange(resourceIndex, out var fileOffset, out var fileLength);

        if (fileLength <= 0 || fileLength > int.MaxValue)
            return false;

        bundle.DataReader.Position = fileOffset;
        var bytes = bundle.DataReader.ReadBytes((int)fileLength);

        if (offset >= (ulong)bytes.Length)
            return false;

        var available = bytes.Length - (long)offset;
        var targetSize = (int)Math.Min(size, (ulong)available);

        if (targetSize < 8)
            return false;

        if (!MutateTextureEncodedData(
            bytes,
            (TextureFormat)TextureFormat.RGBA32,
            intensity,
            rng,
            startOffset: (int)offset,
            length: targetSize))
            return false;

        resourceInfo.SetNewData(bytes);
        return true;
    }

    private static bool MutateTextureEncodedData(
        byte[] data,
        TextureFormat format,
        int intensity,
        Random rng,
        int startOffset = 0,
        int length = -1)
    {
        if (data.Length < 8)
            return false;

        startOffset = Math.Clamp(startOffset, 0, data.Length - 1);

        if (length < 0)
            length = data.Length - startOffset;

        length = Math.Clamp(length, 1, data.Length - startOffset);
        if (length < 8)
            return false;

        var level = Math.Clamp(intensity, 1, 100) / 100.0;
        var endExclusive = startOffset + length;
        var blockSize = GetTextureBlockSize(format);

        // Corrupt a substantial portion of the encoded data at high intensity.
        // The format/header metadata is untouched, but enough block bytes are
        // changed to produce obvious texture artifacts instead of a visually
        // identical texture with a few hidden bit errors.
        var fraction = intensity >= 90
            ? 0.12 + (0.23 * level)
            : 0.01 + (0.14 * level);

        var touches = (int)Math.Clamp(
            length * fraction,
            24,
            700000);

        if (blockSize > 0 && intensity >= 65)
        {
            var stride = intensity >= 90
                ? Math.Max(1, blockSize / 2)
                : blockSize;

            var changed = false;

            for (var blockStart = startOffset;
                 blockStart < endExclusive;
                 blockStart += stride)
            {
                var blockEnd = Math.Min(
                    blockStart + blockSize,
                    endExclusive);

                if (blockEnd <= blockStart)
                    continue;

                var index = blockStart + rng.Next(
                    blockEnd - blockStart);

                data[index] ^= (byte)rng.Next(1, 256);
                changed = true;

                if (intensity >= 95 && blockEnd - blockStart >= 2)
                {
                    var second = blockStart + rng.Next(
                        blockEnd - blockStart);

                    data[second] ^= (byte)rng.Next(1, 256);
                }
            }

            // Layer additional random damage over the block sweep.
            for (var i = 0; i < touches; i++)
            {
                var index = rng.Next(
                    startOffset,
                    endExclusive);

                data[index] = unchecked(
                    (byte)(data[index] ^ (byte)rng.Next(1, 256)));
            }

            return changed || touches > 0;
        }

        for (var i = 0; i < touches; i++)
        {
            var index = rng.Next(startOffset, endExclusive);

            switch (rng.Next(5))
            {
                case 0:
                    data[index] ^= (byte)(1 << rng.Next(8));
                    break;

                case 1:
                    data[index] ^= (byte)rng.Next(1, 256);
                    break;

                case 2:
                    data[index] = (byte)rng.Next(0, 256);
                    break;

                case 3:
                    data[index] = unchecked(
                        (byte)(data[index] + rng.Next(17, 180)));
                    break;

                default:
                    data[index] = unchecked(
                        (byte)(data[index] - rng.Next(17, 180)));
                    break;
            }
        }

        return touches > 0;
    }

    private static int GetTextureBlockSize(TextureFormat format)
    {
        return format switch
        {
            TextureFormat.DXT1
                or TextureFormat.BC4
                or TextureFormat.BC5 => 8,

            TextureFormat.DXT3
                or TextureFormat.DXT5
                or TextureFormat.BC6H
                or TextureFormat.BC7
                or TextureFormat.ETC_RGB4
                or TextureFormat.ETC2_RGB4
                or TextureFormat.ETC2_RGBA1
                or TextureFormat.ETC2_RGBA8
                or TextureFormat.EAC_R
                or TextureFormat.EAC_R_SIGNED
                or TextureFormat.EAC_RG
                or TextureFormat.EAC_RG_SIGNED
                or TextureFormat.ASTC_RGB_4x4
                or TextureFormat.ASTC_RGBA_4x4
                or TextureFormat.ASTC_RGB_5x5
                or TextureFormat.ASTC_RGBA_5x5
                or TextureFormat.ASTC_RGB_6x6
                or TextureFormat.ASTC_RGBA_6x6
                or TextureFormat.ASTC_RGB_8x8
                or TextureFormat.ASTC_RGBA_8x8
                or TextureFormat.ASTC_RGB_10x10
                or TextureFormat.ASTC_RGBA_10x10
                or TextureFormat.ASTC_RGB_12x12
                or TextureFormat.ASTC_RGBA_12x12 => 16,

            _ => 0
        };
    }

    private static bool TryMutateExternalAudio(
        AssetBundleFile bundle,
        AssetTypeValueField baseField,
        int intensity,
        Random rng)
    {
        var resource = baseField["m_Resource"];
        if (resource.IsDummy)
            return false;

        var sourceField = resource["m_Source"];
        var offsetField = resource["m_Offset"];
        var sizeField = resource["m_Size"];

        if (sourceField.IsDummy || offsetField.IsDummy || sizeField.IsDummy)
            return false;

        var source = sourceField.AsString;
        var offset = offsetField.AsULong;
        var size = sizeField.AsULong;

        if (string.IsNullOrWhiteSpace(source)
            || size == 0
            || size > int.MaxValue
            || offset > int.MaxValue)
            return false;

        var normalized = source.Replace('\\', '/');
        var name = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var resourceIndex = ResolveResourceIndex(bundle, name);
        if (resourceIndex < 0)
            return false;

        var resourceInfo = bundle.BlockAndDirInfo.DirectoryInfos[resourceIndex];
        bundle.GetFileRange(resourceIndex, out var fileOffset, out var fileLength);

        if (fileLength <= 0 || fileLength > int.MaxValue)
            return false;

        bundle.DataReader.Position = fileOffset;
        var bytes = bundle.DataReader.ReadBytes((int)fileLength);

        if (offset >= (ulong)bytes.Length)
            return false;

        var available = bytes.Length - (long)offset;
        var targetSize = (int)Math.Min(size, (ulong)available);
        if (targetSize <= 24)
            return false;

        if (!MutateEncodedRegion(
            bytes,
            (int)offset,
            targetSize,
            intensity,
            rng,
            preservePrefix: 24))
            return false;

        resourceInfo.SetNewData(bytes);
        return true;
    }

    private static int ResolveResourceIndex(
        AssetBundleFile bundle,
        string name)
    {
        for (var i = 0; i < bundle.BlockAndDirInfo.DirectoryInfos.Count; i++)
        {
            if (bundle.BlockAndDirInfo.DirectoryInfos[i].Name.Equals(
                name,
                StringComparison.OrdinalIgnoreCase))
                return i;
        }

        if (Path.GetExtension(name).Length == 0)
        {
            var resS = name + ".resS";
            var resource = name + ".resource";

            for (var i = 0; i < bundle.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                var candidate = bundle.BlockAndDirInfo.DirectoryInfos[i].Name;

                if (candidate.Equals(resS, StringComparison.OrdinalIgnoreCase)
                    || candidate.Equals(resource, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        return -1;
    }

    private static bool ShouldHit(
        int intensity,
        Random rng)
        => rng.Next(0, 100) < Math.Clamp(intensity, 1, 100);

    private static bool MutateEncodedRegion(
        byte[] bytes,
        int startOffset,
        int length,
        int intensity,
        Random rng,
        int preservePrefix = 16)
    {
        if (length <= preservePrefix + 4
            || startOffset < 0
            || startOffset >= bytes.Length)
            return false;

        var endExclusive = Math.Min(
            bytes.Length,
            startOffset + length);

        var start = Math.Clamp(
            startOffset + preservePrefix,
            startOffset,
            endExclusive - 1);

        var available = endExclusive - start;

        if (available <= 0)
            return false;

        var count = (int)Math.Clamp(
            available * (0.002 + (0.028 * Math.Clamp(intensity, 1, 100) / 100.0)),
            1,
            60000);

        for (var i = 0; i < count; i++)
        {
            var index = rng.Next(start, endExclusive);
            bytes[index] ^= (byte)rng.Next(1, 256);
        }

        return count > 0;
    }

    private static uint ReadUInt32LE(byte[] bytes, int offset)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(offset, 4));

    private static ushort ReadUInt16LE(byte[] bytes, int offset)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.AsSpan(offset, 2));

    private static ulong ReadUInt64LE(byte[] bytes, int offset)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
            bytes.AsSpan(offset, 8));

    private readonly record struct Fsb5Sample(int Start, int End);

    private readonly record struct Fsb5Info(
        uint Mode,
        List<Fsb5Sample> Samples,
        int End);
}
