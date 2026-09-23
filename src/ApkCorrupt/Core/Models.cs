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
    int FilesChanged);

public sealed record CorruptionResult(
    string OutputPath,
    int TexturesChanged,
    int AudioChanged,
    int FilesChanged);
