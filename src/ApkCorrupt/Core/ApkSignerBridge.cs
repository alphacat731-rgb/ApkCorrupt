using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Android.Runtime;

namespace ApkCorrupt.Core;

public static class ApkSignerBridge
{
    private const string JavaClass = "com/alphacat/apkcorrupt/ApkSignerBridge";

    public static async Task GenerateKeyMaterialAsync(
        string keyPath,
        string certPath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(keyPath) && File.Exists(certPath))
            return;

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=APKCorrupt",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, false));

            using var cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(10));

            File.WriteAllBytes(keyPath, rsa.ExportPkcs8PrivateKey());
            File.WriteAllBytes(certPath, cert.Export(X509ContentType.Cert));
        }, cancellationToken);
    }

    public static Task AlignAndSignAsync(
        string input,
        string output,
        string keyPath,
        string certPath,
        CancellationToken cancellationToken)
    {
        // JNI local references belong to the Android thread's local reference
        // table. Do not move this call to Task.Run: the worker thread has a
        // different JNIEnv. Also, FindClass returns a local/global-managed
        // reference that the Xamarin runtime owns here; deleting it manually
        // can trigger "Attempt to delete global reference as local JNI reference".
        cancellationToken.ThrowIfCancellationRequested();

        var clazz = JNIEnv.FindClass(JavaClass);
        if (clazz == IntPtr.Zero)
            throw new InvalidOperationException("ApkSignerBridge Java class was not found.");

        var method = JNIEnv.GetStaticMethodID(
            clazz,
            "alignAndSign",
            "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)Z");

        using var jInput = new Java.Lang.String(input);
        using var jOutput = new Java.Lang.String(output);
        using var jKey = new Java.Lang.String(keyPath);
        using var jCert = new Java.Lang.String(certPath);

        var ok = JNIEnv.CallStaticBooleanMethod(
            clazz,
            method,
            new JValue(jInput.Handle),
            new JValue(jOutput.Handle),
            new JValue(jKey.Handle),
            new JValue(jCert.Handle));

        if (!ok)
            throw new InvalidOperationException("APK signing bridge returned false.");

        return Task.CompletedTask;
    }
}
