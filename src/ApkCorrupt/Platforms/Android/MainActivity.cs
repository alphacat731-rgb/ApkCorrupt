using Android.App;
using Android.Content;
using Android.Graphics;
using AndroidColor = Android.Graphics.Color;
using AndroidScrollView = Android.Widget.ScrollView;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AndroidView = Android.Views.View;
using AndroidButton = Android.Widget.Button;
using AndroidSwitch = Android.Widget.Switch;
using ApkCorrupt.Core;
using SystemPath = System.IO.Path;

namespace ApkCorrupt;

[Activity(
    Label = "APK Corrupt",
    Theme = "@android:style/Theme.Material.NoActionBar",
    MainLauncher = true,
    LaunchMode = Android.Content.PM.LaunchMode.SingleTop,
    ConfigurationChanges = Android.Content.PM.ConfigChanges.ScreenSize
        | Android.Content.PM.ConfigChanges.Orientation
        | Android.Content.PM.ConfigChanges.UiMode
        | Android.Content.PM.ConfigChanges.ScreenLayout
        | Android.Content.PM.ConfigChanges.SmallestScreenSize
        | Android.Content.PM.ConfigChanges.Density,
    Exported = true)]
public sealed class MainActivity : Activity
{
    private const int PickApkRequest = 7001;

    private LinearLayout _root = null!;
    private TextView _fileName = null!;
    private TextView _fileMeta = null!;
    private TextView _status = null!;
    private TextView _stats = null!;
    private SeekBar _intensity = null!;
    private TextView _intensityValue = null!;
    private EditText _seed = null!;
    private AndroidSwitch _textures = null!;
    private AndroidSwitch _audio = null!;
    private AndroidSwitch _loose = null!;
    private AndroidButton _corrupt = null!;
    private AndroidButton _openLast = null!;

    private string? _sourcePath;
    private string? _lastOutputPath;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BuildNativeUi();
    }

    private void BuildNativeUi()
    {
        _root = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        _root.SetBackgroundColor(AndroidColor.ParseColor("#0B0A10"));

        var scroll = new AndroidScrollView(this);
        scroll.FillViewport = true;

        var content = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        content.SetPadding(Dp(20), Dp(26), Dp(20), Dp(32));

        content.AddView(Text("APK CORRUPT", 28, "#F6F2FF"));
        content.AddView(Text("Unity mutation laboratory", 14, "#A8A1B6"));

        var version = Text("v0.1 • native Android UI", 12, "#A96BFF");
        version.SetPadding(0, Dp(6), 0, Dp(18));
        content.AddView(version);

        content.AddView(BuildSourceCard());
        content.AddView(BuildCorruptionCard());
        content.AddView(BuildSeedCard());
        content.AddView(BuildReportCard());

        _corrupt = Button("CORRUPT APK");
        _corrupt.SetTextColor(AndroidColor.White);
        _corrupt.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 16);
        _corrupt.SetBackgroundColor(AndroidColor.ParseColor("#A96BFF"));
        _corrupt.Click += async (_, _) => await CorruptClickedAsync();
        content.AddView(_corrupt, new LinearLayout.LayoutParams(-1, Dp(58))
        {
            BottomMargin = Dp(10)
        });

        _openLast = Button("OPEN LAST APK");
        _openLast.Visibility = ViewStates.Gone;
        _openLast.SetTextColor(AndroidColor.White);
        _openLast.SetBackgroundColor(AndroidColor.ParseColor("#34204A"));
        _openLast.Click += (_, _) => OpenLast();
        content.AddView(_openLast, new LinearLayout.LayoutParams(-1, Dp(52))
        {
            BottomMargin = Dp(16)
        });

        var footer = Text(
            "The mutation engine avoids Unity managed code, Android manifest entries, and native libraries.",
            11,
            "#7F778C");
        footer.Gravity = GravityFlags.Center;
        content.AddView(footer);

        scroll.AddView(content);
        _root.AddView(scroll, new LinearLayout.LayoutParams(-1, -1));
        SetContentView(_root);
    }

    private AndroidView BuildSourceCard()
    {
        var card = Card();
        var title = Text("SOURCE APK", 11, "#A8A1B6");
        card.AddView(title);

        _fileName = Text("No APK selected", 16, "#F6F2FF");
        card.AddView(_fileName, new LinearLayout.LayoutParams(-1, -2)
        {
            TopMargin = Dp(10)
        });

        _fileMeta = Text("Pick an APK to inspect its Unity layout", 12, "#A8A1B6");
        card.AddView(_fileMeta);

        var pick = Button("PICK APK");
        pick.SetTextColor(AndroidColor.White);
        pick.SetBackgroundColor(AndroidColor.ParseColor("#1D1A29"));
        pick.Click += (_, _) => PickApk();
        card.AddView(pick, new LinearLayout.LayoutParams(-1, Dp(48))
        {
            TopMargin = Dp(12)
        });

        return card;
    }

    private AndroidView BuildCorruptionCard()
    {
        var card = Card();
        card.AddView(Text("CORRUPTION", 11, "#A8A1B6"));

        var row = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };
        _intensityValue = Text("55%", 13, "#A96BFF");
        row.AddView(_intensityValue, new LinearLayout.LayoutParams(0, -2, 1f));
        row.AddView(Text("Intensity", 12, "#A8A1B6"));
        card.AddView(row, new LinearLayout.LayoutParams(-1, -2)
        {
            TopMargin = Dp(10)
        });

        _intensity = new SeekBar(this)
        {
            Max = 95,
            Progress = 30
        };
        _intensity.ProgressChanged += (_, e) =>
        {
            var value = e.Progress + 5;
            _intensityValue.Text = $"{value}%";
        };
        card.AddView(_intensity, new LinearLayout.LayoutParams(-1, -2));

        var hint = Text("100% covers every audio bank plus every parsed texture, material, light and video; intensity controls how crunchy they get.", 11, "#7F778C");
        card.AddView(hint, new LinearLayout.LayoutParams(-1, -2)
        {
            BottomMargin = Dp(8)
        });

        var presets = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };

        AddIntensityPreset(presets, "LIGHT", 10);
        AddIntensityPreset(presets, "MEDIUM", 35);
        AddIntensityPreset(presets, "HEAVY", 65);
        AddIntensityPreset(presets, "CHAOS", 90);

        card.AddView(presets, new LinearLayout.LayoutParams(-1, -2)
        {
            BottomMargin = Dp(10)
        });

        _textures = SwitchControl("Visual corruption (textures / materials / lights / video)", true);
        _audio = SwitchControl("Audio crunch (all FSB5 samples)", true);
        _loose = SwitchControl("Loose media fallback (including MP4)", true);

        card.AddView(_textures);
        card.AddView(_audio);
        card.AddView(_loose);

        return card;
    }

    private AndroidView BuildSeedCard()
    {
        var card = Card();
        card.AddView(Text("DETERMINISTIC SEED", 11, "#A8A1B6"));

        _seed = new EditText(this)
        {
            Text = "48291371",
            InputType = global::Android.Text.InputTypes.ClassNumber
                | global::Android.Text.InputTypes.NumberFlagSigned
        };
        _seed.SetTextColor(AndroidColor.ParseColor("#F6F2FF"));
        _seed.SetHintTextColor(AndroidColor.ParseColor("#7F778C"));
        _seed.Hint = "8 digit seed";
        card.AddView(_seed, new LinearLayout.LayoutParams(-1, Dp(52))
        {
            TopMargin = Dp(10)
        });

        var randomize = Button("RANDOMIZE SEED");
        randomize.SetTextColor(AndroidColor.White);
        randomize.SetBackgroundColor(AndroidColor.ParseColor("#1D1A29"));
        randomize.Click += (_, _) => _seed.Text = Random.Shared.Next(10000000, 99999999).ToString();
        card.AddView(randomize);

        card.AddView(Text("Same APK + same settings + same seed = same mutation.", 12, "#A8A1B6"));

        return card;
    }

    private AndroidView BuildReportCard()
    {
        var card = Card();
        card.AddView(Text("CORRUPTION REPORT", 11, "#A8A1B6"));

        _status = Text("Idle. Nothing has been sacrificed yet.", 13, "#F6F2FF");
        card.AddView(_status, new LinearLayout.LayoutParams(-1, -2)
        {
            TopMargin = Dp(10)
        });

        _stats = Text("Audio 0 · Sources 0 · Tex 0 · Mat 0 · Light 0 · Video 0 · Icon 0", 12, "#A8A1B6");
        card.AddView(_stats);

        return card;
    }

    private LinearLayout Card()
    {
        var card = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        card.SetPadding(Dp(16), Dp(16), Dp(16), Dp(16));
        card.SetBackgroundColor(AndroidColor.ParseColor("#15131E"));
        card.LayoutParameters = new LinearLayout.LayoutParams(-1, -2)
        {
            BottomMargin = Dp(14)
        };
        return card;
    }

    private void AddIntensityPreset(LinearLayout row, string label, int value)
    {
        var button = Button(label);
        button.SetTextColor(AndroidColor.ParseColor("#D8D0E5"));
        button.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11);
        button.SetBackgroundColor(AndroidColor.ParseColor("#1D1A29"));
        button.Click += (_, _) => SetIntensity(value);

        row.AddView(button, new LinearLayout.LayoutParams(0, Dp(42), 1f)
        {
            LeftMargin = Dp(3),
            RightMargin = Dp(3)
        });
    }

    private void SetIntensity(int value)
    {
        _intensity.Progress = Math.Clamp(value, 5, 100) - 5;
        _intensityValue.Text = $"{value}%";
    }

    private AndroidSwitch SwitchControl(string label, bool checkedState)
    {
        var sw = new AndroidSwitch(this)
        {
            Text = label,
            Checked = checkedState
        };
        sw.SetTextColor(AndroidColor.ParseColor("#F6F2FF"));
        return sw;
    }

    private TextView Text(string value, float size, string color)
    {
        var tv = new TextView(this)
        {
            Text = value
        };
        tv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, size);
        tv.SetTextColor(AndroidColor.ParseColor(color));
        return tv;
    }

    private AndroidButton Button(string value)
    {
        var button = new AndroidButton(this)
        {
            Text = value,
        };
        return button;
    }

    private int Dp(int value) =>
        (int)Math.Round(value * Resources!.DisplayMetrics!.Density);

    private void PickApk()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/vnd.android.package-archive");
        StartActivityForResult(Intent.CreateChooser(intent, "Select a Unity APK"), PickApkRequest);
    }

    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode != PickApkRequest || resultCode != Result.Ok || data?.Data is null)
            return;

        try
        {
            var uri = data.Data;
            var name = uri.LastPathSegment?.Split('/').LastOrDefault();
            var localPath = SystemPath.Combine(CacheDir!.AbsolutePath, $"source-{Guid.NewGuid():N}.apk");

            using var input = ContentResolver!.OpenInputStream(uri!);
            await using var output = File.Create(localPath);

            if (input is null)
                throw new InvalidOperationException("Android could not open the selected APK.");

            await input.CopyToAsync(output);

            _sourcePath = localPath;
            _fileName.Text = string.IsNullOrWhiteSpace(name) ? "Selected APK" : name;
            _fileMeta.Text = "APK copied to app cache and ready.";
            _status.Text = "APK loaded. Hit CORRUPT APK when you're ready.";
            _openLast.Visibility = ViewStates.Gone;
        }
        catch (Exception ex)
        {
            _status.Text = $"Picker error: {ex.Message}";
        }
    }

    private async Task CorruptClickedAsync()
    {
        if (_sourcePath is null || !File.Exists(_sourcePath))
        {
            _status.Text = "Pick an APK first.";
            return;
        }

        if (!int.TryParse(_seed.Text, out var seed))
        {
            _status.Text = "Seed must be numeric.";
            return;
        }

        _corrupt.Enabled = false;
        _openLast.Visibility = ViewStates.Gone;

        try
        {
            var intensity = _intensity.Progress + 5;
            var options = new CorruptionOptions(
                seed,
                intensity,
                _textures.Checked,
                _audio.Checked,
                _loose.Checked);

            var progress = new Progress<CorruptionProgress>(p =>
            {
                RunOnUiThread(() =>
                {
                    _status.Text = p.Message;
                    _stats.Text = $"Audio {p.AudioChanged} · Sources {p.AudioSourcesChanged} · Tex {p.TexturesChanged} · Mat {p.MaterialsChanged} · Light {p.LightsChanged} · Video {p.VideosChanged} · Icon {p.IconsChanged}";
                });
            });

            var result = await ApkCorruptor.CorruptAsync(
                _sourcePath,
                options,
                progress,
                CancellationToken.None);

            _lastOutputPath = result.OutputPath;
            _status.Text = $"Done. {SystemPath.GetFileName(result.OutputPath)}";
            _stats.Text = $"Audio {result.AudioChanged} · Sources {result.AudioSourcesChanged} · Tex {result.TexturesChanged} · Mat {result.MaterialsChanged} · Light {result.LightsChanged} · Video {result.VideosChanged} · Icon {result.IconsChanged} · Files {result.FilesChanged}";
            _openLast.Visibility = ViewStates.Visible;

            new AlertDialog.Builder(this)
                .SetTitle("APK ready")
                .SetMessage($"Saved to Downloads as {SystemPath.GetFileName(result.OutputPath)}")
                .SetPositiveButton("Nice", (s, e) => { })
                .Show();
        }
        catch (Exception ex)
        {
            _status.Text = $"Corruption failed: {ex.Message}";
        }
        finally
        {
            _corrupt.Enabled = true;
        }
    }

    private void OpenLast()
    {
        if (string.IsNullOrWhiteSpace(_lastOutputPath))
            return;

        try
        {
            _ = ApkExport.OpenForInstallAsync(_lastOutputPath);
        }
        catch (Exception ex)
        {
            _status.Text = $"Couldn't open installer: {ex.Message}";
        }
    }
}
