namespace ApkCorrupt.Core;

public sealed record CorruptionOptions(
    int Seed,
    int Intensity,
    bool Textures,
    bool Audio,
    bool LooseAssets);

public sealed record CorruptionProgress(
    string Message,
    int TexturesChanged,
    int AudioChanged,
    int FilesChanged,
    int MaterialsChanged = 0,
    int TextAssetsChanged = 0,
    int MeshesChanged = 0,
    int VideosChanged = 0,
    int LightsChanged = 0,
    int IconsChanged = 0,
    int AudioSourcesChanged = 0);

public sealed record CorruptionResult(
    string OutputPath,
    int TexturesChanged,
    int AudioChanged,
    int FilesChanged,
    int MaterialsChanged = 0,
    int TextAssetsChanged = 0,
    int MeshesChanged = 0,
    int VideosChanged = 0,
    int LightsChanged = 0,
    int IconsChanged = 0);
