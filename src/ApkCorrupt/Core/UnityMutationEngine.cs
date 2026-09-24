using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace ApkCorrupt.Core;

public static class UnityMutationEngine
{
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
                var baseField = manager.GetBaseField(instance, info);
                var texture = TextureFile.ReadTextureFile(baseField);

                var data = texture.FillPictureData(instance);
                if (data is not { Length: > 0 })
                    continue;

                var decoded = TextureFile.DecodeManagedData(
                    data,
                    (TextureFormat)texture.m_TextureFormat,
                    texture.m_Width,
                    texture.m_Height,
                    useBgra: true);

                if (decoded is not { Length: > 0 })
                    continue;

                CorruptPixels(decoded, texture.m_Width, texture.m_Height, options.Intensity, rng);

                var encoded = TextureFile.EncodeManagedData(
                    decoded,
                    TextureFormat.RGBA32,
                    texture.m_Width,
                    texture.m_Height,
                    useBgra: true);

                if (encoded is not { Length: > 0 })
                    continue;

                texture.SetPictureData(
                    encoded,
                    texture.m_Width,
                    texture.m_Height);

                texture.WriteTo(baseField);
                info.SetNewData(baseField);
                textures++;
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

                MutateEncodedRegion(bytes, options.Intensity, rng);
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
                    var baseField = manager.GetBaseField(assets, info);
                    var texture = TextureFile.ReadTextureFile(baseField);
                    var data = texture.FillPictureData(assets);

                    if (data is not { Length: > 0 })
                        continue;

                    var decoded = TextureFile.DecodeManagedData(
                        data,
                        (TextureFormat)texture.m_TextureFormat,
                        texture.m_Width,
                        texture.m_Height,
                        useBgra: true);

                    if (decoded is not { Length: > 0 })
                        continue;

                    CorruptPixels(decoded, texture.m_Width, texture.m_Height, options.Intensity, rng);

                    var encoded = TextureFile.EncodeManagedData(
                        decoded,
                        TextureFormat.RGBA32,
                        texture.m_Width,
                        texture.m_Height,
                        useBgra: true);

                    if (encoded is not { Length: > 0 })
                        continue;

                    texture.SetPictureData(
                        encoded,
                        texture.m_Width,
                        texture.m_Height,
                        TextureFormat.RGBA32);

                    texture.WriteTo(baseField);
                    info.SetNewData(baseField);
                    textures++;
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
                        if (bytes.Length > 32)
                        {
                            MutateEncodedRegion(bytes, options.Intensity, rng);
                            audioData.AsByteArray = bytes;
                            info.SetNewData(baseField);
                            audio++;
                            continue;
                        }
                    }

                    // Many Unity AudioClips keep the encoded bytes in a .resS/.resource
                    // entry instead of m_AudioData. We update that referenced range in-place
                    // without changing its size.
                    if (!TryMutateExternalAudio(
                        bundle.file,
                        baseField,
                        options.Intensity,
                        rng))
                    {
                        continue;
                    }

                    audio++;
                }
                catch
                {
                    // A single AudioClip must never abort the entire bundle.
                }
            }

            // AT3 uses a content replacer attached directly to the bundle directory.
            dirInfo.SetNewData(assets.file);
        }

        // External resource entries are handled in-place through their directory replacer.
        // TryMutateExternalAudio stores mutations directly on those entries.
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

        var segment = new byte[targetSize];
        Buffer.BlockCopy(bytes, (int)offset, segment, 0, targetSize);

        MutateEncodedRegion(
            segment,
            intensity,
            rng,
            preservePrefix: 24);

        Buffer.BlockCopy(
            segment,
            0,
            bytes,
            (int)offset,
            targetSize);

        // Replacing the complete resource entry keeps all offsets valid.
        resourceInfo.SetNewData(bytes);
        return true;
    }

    private static int ResolveResourceIndex(AssetBundleFile bundle, string name)
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

    private static bool ShouldHit(int intensity, Random rng)
        => rng.Next(0, 100) < Math.Clamp(intensity, 1, 100);

    private static void CorruptPixels(
        byte[] pixels,
        int width,
        int height,
        int intensity,
        Random rng)
    {
        if (width <= 0 || height <= 0 || pixels.Length < 4)
            return;

        var pixelCount = Math.Min(
            (long)width * height,
            pixels.Length / 4L);

        var touches = (int)Math.Clamp(
            pixelCount * Math.Clamp(intensity, 1, 100) / 900L,
            8,
            50000);

        for (var i = 0; i < touches; i++)
        {
            var x = rng.Next(width);
            var y = rng.Next(height);
            var index = checked((y * width + x) * 4);

            if (index + 3 >= pixels.Length)
                continue;

            switch (rng.Next(6))
            {
                case 0:
                    pixels[index] = 0;
                    break;
                case 1:
                    pixels[index + 1] = 0;
                    break;
                case 2:
                    pixels[index + 2] = 0;
                    break;
                case 3:
                    pixels[index] ^= 0xFF;
                    break;
                case 4:
                    pixels[index + 3] = (byte)rng.Next(40, 256);
                    break;
                default:
                    var nearby = Math.Clamp(
                        index + rng.Next(-8, 9) * 4,
                        0,
                        pixels.Length - 4);

                    pixels[index] = pixels[nearby];
                    pixels[index + 1] = pixels[nearby + 1];
                    pixels[index + 2] = pixels[nearby + 2];
                    break;
            }
        }
    }

    private static void MutateEncodedRegion(
        byte[] bytes,
        int intensity,
        Random rng,
        int preservePrefix = 16)
    {
        if (bytes.Length <= preservePrefix + 4)
            return;

        var start = Math.Clamp(preservePrefix, 0, bytes.Length - 1);
        var available = bytes.Length - start;

        var count = (int)Math.Clamp(
            available * Math.Clamp(intensity, 1, 100) / 1800.0,
            1,
            25000);

        for (var i = 0; i < count; i++)
        {
            var index = rng.Next(start, bytes.Length);
            bytes[index] ^= (byte)rng.Next(1, 256);
        }
    }
}
