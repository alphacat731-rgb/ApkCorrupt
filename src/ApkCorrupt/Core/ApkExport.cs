using Android.Content;
using Android.OS;
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
        {
            var legacyDir = Environment.GetExternalStoragePublicDirectory(
                Environment.DirectoryDownloads)!;
            Directory.CreateDirectory(legacyDir.AbsolutePath);
            var legacyPath = Path.Combine(legacyDir.AbsolutePath, displayName);
            await using var input = File.OpenRead(sourcePath);
            await using var output = File.Create(legacyPath);
            await input.CopyToAsync(output, cancellationToken);
            return legacyPath;
        }

        var resolver = Android.App.Application.Context.ContentResolver
            ?? throw new InvalidOperationException("No ContentResolver available.");

        var values = new ContentValues();
        values.Put(MediaStore.MediaColumns.DisplayName, displayName);
        values.Put(MediaStore.MediaColumns.MimeType, "application/vnd.android.package-archive");
        values.Put(MediaStore.MediaColumns.RelativePath, Environment.DirectoryDownloads);

        var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri, values)
            ?? throw new InvalidOperationException("Could not create Downloads entry.");

        try
        {
            await using var input = File.OpenRead(sourcePath);
            await using var output = resolver.OpenOutputStream(uri)
                ?? throw new InvalidOperationException("Could not open Downloads output.");

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

        var context = Android.App.Application.Context;
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(
            global::Android.Net.Uri.Parse(pathOrUri),
            "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
        context.StartActivity(intent);
        return Task.CompletedTask;
    }
}
