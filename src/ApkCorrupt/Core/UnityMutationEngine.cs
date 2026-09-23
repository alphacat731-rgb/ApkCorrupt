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
        var lower = inputPath.ToLowerInvariant();

        if (lower.EndsWith(".assets") ||
            lower.EndsWith(".sharedassets") ||
            lower.EndsWith(".resS".ToLowerInvariant()) ||
            lower.Contains("globalgamemanagers"))
        {
            var changed = await MutateStandaloneAssetsAsync(inputPath, outputPath, options, rng, cancellationToken);
            return changed;
        }

        if (options.Textures || options.Audio)
        {
            try
            {
                return await MutateBundleAsync(inputPath, outputPath, options, rng, cancellationToken);
            }
            catch
            {
                // Not every Unity-looking APK entry is an AssetBundle.
            }
        }

        return new MutationResult(false, inputPath, 0, 0);
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
                if (texture.FillPictureData(instance) is not { Length: > 0 } data)
                    continue;

                var decoded = TextureFile.DecodeManagedData(
                    data,
                    (TextureFormat)texture.m_TextureFormat,
                    texture.m_Width,
                    texture.m_Height,
                    useBgra: true);

                if (decoded is null || decoded.Length < 4)
                    continue;

                CorruptPixels(decoded, texture.m_Width, texture.m_Height, options.Intensity, rng);

                var encoded = TextureFile.EncodeManagedData(
                    decoded,
                    TextureFormat.RGBA32,
                    texture.m_Width,
                    texture.m_Height,
                    useBgra: true);

                texture.SetPictureData(
                    encoded,
                    texture.m_Width,
                    texture.m_Height,
                    TextureFormat.RGBA32,
                    1);

                texture.WriteTo(baseField);
                info.SetNewData(baseField);
                textures++;
            }
            catch
            {
                // Skip individual unsupported textures; keep the game buildable.
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
                if (!audioData.IsDummy && audioData.TemplateField.ValueType == AssetValueType.ByteArray)
                {
                    var bytes = audioData.AsByteArray;
                    if (bytes.Length > 24)
                    {
                        MutateEncodedRegion(bytes, options.Intensity, rng);
                        audioData.AsByteArray = bytes;
                        info.SetNewData(baseField);
                        audio++;
                    }
                }
            }
            catch
            {
                // External .resS audio is handled by bundle mutation; embedded audio is best effort.
            }
        }

        if (textures == 0 && audio == 0)
            return new MutationResult(false, inputPath, 0, 0);

        cancellationToken.ThrowIfCancellationRequested();

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
        var bundleReplacers = new List<BundleReplacer>();

        var textures = 0;
        var audio = 0;

        var directoryInfos = bundle.file.BlockAndDirInfo.DirectoryInfos;

        for (var i = 0; i < directoryInfos.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dirName = directoryInfos[i].Name;

            AssetsFileInstance? assets = null;
            try
            {
                assets = manager.LoadAssetsFileFromBundle(bundle, i, false);
            }
            catch
            {
                continue;
            }

            var assetReplacers = new List<AssetsReplacer>();

            foreach (var info in assets.file.GetAssetsOfType(AssetClassID.Texture2D))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!options.Textures || !ShouldHit(options.Intensity, rng))
                    continue;

                try
                {
                    var baseField = manager.GetBaseField(assets, info);
                    var texture = TextureFile.ReadTextureFile(baseField);
                    if (texture.FillPictureData(assets) is not { Length: > 0 } data)
                        continue;

                    var decoded = TextureFile.DecodeManagedData(
                        data,
                        (TextureFormat)texture.m_TextureFormat,
                        texture.m_Width,
                        texture.m_Height,
                        useBgra: true);

                    if (decoded is null || decoded.Length < 4)
                        continue;

                    CorruptPixels(decoded, texture.m_Width, texture.m_Height, options.Intensity, rng);

                    var encoded = TextureFile.EncodeManagedData(
                        decoded,
                        TextureFormat.RGBA32,
                        texture.m_Width,
                        texture.m_Height,
                        useBgra: true);

                    texture.SetPictureData(
                        encoded,
                        texture.m_Width,
                        texture.m_Height,
                        TextureFormat.RGBA32,
                        1);

                    texture.WriteTo(baseField);
                    assetReplacers.Add(new AssetsReplacerFromMemory(
                        assets.file,
                        info,
                        baseField));

                    textures++;
                }
                catch
                {
                    // Unsupported texture format/type tree: skip safely.
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

                    if (!audioData.IsDummy && audioData.TemplateField.ValueType == AssetValueType.ByteArray)
                    {
                        var bytes = audioData.AsByteArray;
                        if (bytes.Length > 24)
                        {
                            MutateEncodedRegion(bytes, options.Intensity, rng);
                            audioData.AsByteArray = bytes;
                            assetReplacers.Add(new AssetsReplacerFromMemory(
                                assets.file,
                                info,
                                baseField));
                            audio++;
                        }
                    }
                }
                catch
                {
                    // External .resS audio is intentionally left to the next pipeline pass.
                }
            }

            if (assetReplacers.Count > 0)
            {
                bundleReplacers.Add(new BundleReplacerFromAssets(
                    dirName,
                    null,
                    assets.file,
                    assetReplacers));
            }
        }

        if (bundleReplacers.Count == 0)
            return new MutationResult(false, inputPath, 0, 0);

        await Task.Run(() =>
        {
            using var writer = new AssetsFileWriter(outputPath);
            bundle.file.Write(writer, bundleReplacers);
        }, cancellationToken);

        return new MutationResult(true, outputPath, textures, audio);
    }

    private static bool ShouldHit(int intensity, Random rng)
        => rng.Next(0, 100) < Math.Clamp(intensity, 1, 100);

    private static void CorruptPixels(byte[] bgra, int width, int height, int intensity, Random rng)
    {
        if (width <= 0 || height <= 0)
            return;

        var pixels = Math.Max(1, width * height);
        var touched = Math.Max(1, pixels * Math.Clamp(intensity, 1, 100) / 80);

        for (var i = 0; i < touched; i++)
        {
            var x = rng.Next(width);
            var y = rng.Next(height);
            var index = checked((y * width + x) * 4);

            if (index + 3 >= bgra.Length)
                continue;

            var mode = rng.Next(5);
            switch (mode)
            {
                case 0:
                    bgra[index] = 0;
                    break;
                case 1:
                    bgra[index + 1] = 0;
                    break;
                case 2:
                    bgra[index + 2] = 0;
                    break;
                case 3:
                    bgra[index] ^= 0xFF;
                    break;
                default:
                    bgra[index + 3] = (byte)rng.Next(40, 256);
                    break;
            }
        }
    }

    private static void MutateEncodedRegion(byte[] bytes, int intensity, Random rng)
    {
        if (bytes.Length < 24)
            return;

        var start = Math.Max(8, bytes.Length / 10);
        var count = Math.Max(1, bytes.Length * Math.Clamp(intensity, 1, 100) / 1200);

        for (var i = 0; i < count; i++)
        {
            var index = rng.Next(start, bytes.Length);
            bytes[index] ^= (byte)rng.Next(1, 256);
        }
    }
}
