using ApkCorrupt.Core;

namespace ApkCorrupt;

public partial class MainPage : ContentPage
{
    private string? _sourcePath;
    private string? _lastOutputPath;

    public MainPage()
    {
        InitializeComponent();
    }

    private void OnIntensityChanged(object? sender, ValueChangedEventArgs e)
    {
        IntensityLabel.Text = $"{e.NewValue:0}%";
    }

    private async void OnPickClicked(object? sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Select a Unity APK"
            });

            if (result is null)
                return;

            var localPath = Path.Combine(FileSystem.CacheDirectory, $"source-{Guid.NewGuid():N}.apk");
            await using var input = await result.OpenReadAsync();
            await using var output = File.Create(localPath);
            await input.CopyToAsync(output);

            _sourcePath = localPath;
            FileNameLabel.Text = result.FileName;
            FileMetaLabel.Text = "Ready for Unity layout analysis";
            StatusLabel.Text = "APK loaded. Hit CORRUPT APK when you're ready.";
            InstallButton.IsVisible = false;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Picker error: {ex.Message}";
        }
    }

    private void OnRandomizeSeedClicked(object? sender, EventArgs e)
    {
        SeedEntry.Text = Random.Shared.Next(10000000, 99999999).ToString();
    }

    private async void OnCorruptClicked(object? sender, EventArgs e)
    {
        if (_sourcePath is null || !File.Exists(_sourcePath))
        {
            StatusLabel.Text = "Pick an APK first.";
            return;
        }

        if (!int.TryParse(SeedEntry.Text, out var seed))
        {
            StatusLabel.Text = "Seed must be numeric.";
            return;
        }

        CorruptButton.IsEnabled = false;
        InstallButton.IsVisible = false;

        try
        {
            var intensity = (int)Math.Round(IntensitySlider.Value);
            var options = new CorruptionOptions(
                Seed: seed,
                Intensity: intensity,
                Textures: TexturesSwitch.IsToggled,
                Audio: AudioSwitch.IsToggled,
                LooseAssets: LooseSwitch.IsToggled);

            var progress = new Progress<CorruptionProgress>(p =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    StatusLabel.Text = p.Message;
                    StatsLabel.Text = $"Textures {p.TexturesChanged} · Audio {p.AudioChanged} · Files {p.FilesChanged}";
                });
            });

            var result = await ApkCorruptor.CorruptAsync(_sourcePath, options, progress, CancellationToken.None);

            _lastOutputPath = result.OutputPath;
            StatusLabel.Text = $"Done. {Path.GetFileName(result.OutputPath)}";
            StatsLabel.Text = $"Textures {result.TexturesChanged} · Audio {result.AudioChanged} · Files {result.FilesChanged}";
            InstallButton.IsVisible = true;

            await DisplayAlertAsync("APK ready", $"Saved to Downloads as {Path.GetFileName(result.OutputPath)}", "Nice");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Corruption failed: {ex.Message}";
        }
        finally
        {
            CorruptButton.IsEnabled = true;
        }
    }

    private async void OnOpenLastClicked(object? sender, EventArgs e)
    {
        if (_lastOutputPath is null)
            return;

        try
        {
            await ApkExport.OpenForInstallAsync(_lastOutputPath);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Couldn't open installer: {ex.Message}";
        }
    }
}
