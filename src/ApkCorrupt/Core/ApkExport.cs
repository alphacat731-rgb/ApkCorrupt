using Android.Content;
using Android.Provider;
using Android.OS;

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
        values.Put(MediaStore.MediaColumns.DisplayName, displayName);
        values.Put(MediaStore.MediaColumns.MimeType, "application/vnd.android.package-archive");
        values.Put(MediaStore.MediaColumns.RelativePath, Environment.DirectoryDownloads + "/");
        values.Put(MediaStore.MediaColumns.IsPending, 1);

        var collection = MediaStore.Files.GetContentUri(MediaStore.VolumeExternalPrimary);
        var uri = resolver.Insert(collection, values);

        if (uri is null)
            throw new InvalidOperationException("Could not create the Downloads entry.");

        try
        {
            await using var input = File.OpenRead(sourcePath);
            await using var output = resolver.OpenOutputStream(uri)
                ?? throw new InvalidOperationException("Could not open the Downloads output.");

            await input.CopyToAsync(output, cancellationToken);

            var published = new ContentValues();
            published.Put(MediaStore.MediaColumns.IsPending, 0);
            resolver.Update(uri, published, null, null);

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
