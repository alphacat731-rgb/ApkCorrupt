using Android.Content;
using Android.Provider;

namespace ApkCorrupt.Core;

public static class ApkExport
{
    public static async Task<string> CopyToDownloadsAsync(
        string sourcePath,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
            throw new PlatformNotSupportedException(
                "APK Corrupt now targets Android 10+ for modern scoped-storage APK export.");

        var resolver = Android.App.Application.Context.ContentResolver
            ?? throw new InvalidOperationException("No ContentResolver available.");

        var values = new ContentValues();
        values.Put("display_name", displayName);
        values.Put("mime_type", "application/vnd.android.package-archive");
        values.Put("relative_path", "Download/");

        var uri = resolver.Insert(
            MediaStore.Downloads.ExternalContentUri,
            values);

        if (uri is null)
            throw new InvalidOperationException("Could not create the Downloads entry.");

        try
        {
            await using var input = File.OpenRead(sourcePath);
            await using var output = resolver.OpenOutputStream(uri)
                ?? throw new InvalidOperationException("Could not open the Downloads output.");

            await input.CopyToAsync(output, cancellationToken);
            return uri.ToString()!;
        }
        catch
        {
            resolver.Delete(uri, null, null);
            throw;
        }
    }

    public static Task OpenForInstallAsync(string pathOrUri)
    {
        if (!OperatingSystem.IsAndroid())
            return Task.CompletedTask;

        var uri = global::Android.Net.Uri.Parse(pathOrUri)
            ?? throw new InvalidOperationException("Invalid APK URI.");

        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(
            uri,
            "application/vnd.android.package-archive");
        intent.AddFlags(
            ActivityFlags.NewTask |
            ActivityFlags.GrantReadUriPermission);

        Android.App.Application.Context.StartActivity(intent);
        return Task.CompletedTask;
    }
}
