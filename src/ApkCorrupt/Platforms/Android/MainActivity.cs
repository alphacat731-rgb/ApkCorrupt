using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using ApkCorrupt.Core;

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
    private Switch _textures = null!;
    private Switch _audio = null!;
    private Switch _loose = null!;
    private Button _corrupt = null!;
    private Button _openLast = null!;

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
        _root.SetBackgroundColor(Color.ParseColor("#0B0A10"));

        var scroll = new ScrollView(this);
        scroll.SetFillViewport(true);

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
        _corrupt.SetTextColor(Color.White);
        _corrupt.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 16);
        _corrupt.SetBackgroundColor(Color.ParseColor("#A96BFF"));
        _corrupt.Click += async (_, _) => await CorruptClickedAsync();
        content.AddView(_corrupt, new LinearLayout.LayoutParams(-1, Dp(58))
        {
            BottomMargin = Dp(10)
        });

        _openLast = Button("OPEN LAST APK");
        _openLast.Visibility = ViewStates.Gone;
        _openLast.SetTextColor(Color.White);
        _openLast.SetBackgroundColor(Color.ParseColor("#34204A"));
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

    private View BuildSourceCard()
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
        pick.SetTextColor(Color.White);
        pick.SetBackgroundColor(Color.ParseColor("#1D1A29"));
        pick.Click += (_, _) => PickApk();
        card.AddView(pick, new LinearLayout.LayoutParams(-1, Dp(48))
        {
            TopMargin = Dp(12)
        });

        return card;
    }

    private View BuildCorruptionCard()
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
            Progress = 50
        };
        _intensity.ProgressChanged += (_, e) =>
        {
            var value = e.Progress + 5;
            _intensityValue.Text = $"{value}%";
        };
        card.AddView(_intensity, new LinearLayout.LayoutParams(-1, -2));

        _textures = SwitchControl("Texture surgery", true);
        _audio = SwitchControl("Audio mangling", true);
        _loose = SwitchControl("Raw audio fallback", true);

        card.AddView(_textures);
        card.AddView(_audio);
        card.AddView(_loose);

        return card;
    }

    private View BuildSeedCard()
    {
        var card = Card();
        card.AddView(Text("DETERMINISTIC SEED", 11, "#A8A1B6"));

        _seed = new EditText(this)
        {
            Text = "48291371",
            InputType = global::Android.Text.InputTypes.ClassNumber
                | global::Android.Text.InputTypes.NumberFlagSigned
        };
        _seed.SetTextColor(Color.ParseColor("#F6F2FF"));
        _seed.SetHintTextColor(Color.ParseColor("#7F778C"));
        _seed.SetHint("8 digit seed");
        card.AddView(_seed, new LinearLayout.LayoutParams(-1, Dp(52))
        {
            TopMargin = Dp(10)
        });

        var randomize = Button("RANDOMIZE SEED");
        randomize.SetTextColor(Color.White);
        randomize.SetBackgroundColor(Color.ParseColor("#1D1A29"));
        randomize.Click += (_, _) => _seed.Text = Random.Shared.Next(10000000, 99999999).ToString();
        card.AddView(randomize);

        card.AddView(Text("Same APK + same settings + same seed = same mutation.", 12, "#A8A1B6"));

        return card;
    }

    private View BuildReportCard()
    {
        var card = Card();
        card.AddView(Text("CORRUPTION REPORT", 11, "#A8A1B6"));

        _status = Text("Idle. Nothing has been sacrificed yet.", 13, "#F6F2FF");
        card.AddView(_status, new LinearLayout.LayoutParams(-1, -2)
        {
            TopMargin = Dp(10)
        });

        _stats = Text("Textures 0 · Audio 0 · Files 0", 12, "#A8A1B6");
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
        card.SetBackgroundColor(Color.ParseColor("#15131E"));

        var lp = new LinearLayout.LayoutParams(-1, -2)
        {
            BottomMargin = Dp(14)
        };
        _root ??= null!;
        return WrapCard(card, lp);
    }

    private View WrapCard(LinearLayout card, ViewGroup.LayoutParams lp)
    {
        var container = new FrameLayout(this);
        container.AddView(card, new FrameLayout.LayoutParams(-1, -2));
        return container;
    }

    private Switch SwitchControl(string label, bool checkedState)
    {
        var sw = new Switch(this)
        {
            Text = label,
            Checked = checkedState
        };
        sw.SetTextColor(Color.ParseColor("#F6F2FF"));
        return sw;
    }

    private TextView Text(string value, float size, string color)
    {
        var tv = new TextView(this)
        {
            Text = value
        };
        tv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, size);
        tv.SetTextColor(Color.ParseColor(color));
        return tv;
    }

    private Button Button(string value)
    {
        var button = new Button(this)
        {
            Text = value,
            AllCaps = false
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
            var localPath = Path.Combine(CacheDir!.AbsolutePath, $"source-{Guid.NewGuid():N}.apk");

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
                    _stats.Text = $"Textures {p.TexturesChanged} · Audio {p.AudioChanged} · Files {p.FilesChanged}";
                });
            });

            var result = await ApkCorruptor.CorruptAsync(
                _sourcePath,
                options,
                progress,
                CancellationToken.None);

            _lastOutputPath = result.OutputPath;
            _status.Text = $"Done. {Path.GetFileName(result.OutputPath)}";
            _stats.Text = $"Textures {result.TexturesChanged} · Audio {result.AudioChanged} · Files {result.FilesChanged}";
            _openLast.Visibility = ViewStates.Visible;

            new AlertDialog.Builder(this)
                .SetTitle("APK ready")
                .SetMessage($"Saved to Downloads as {Path.GetFileName(result.OutputPath)}")
                .SetPositiveButton("Nice", null)
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
