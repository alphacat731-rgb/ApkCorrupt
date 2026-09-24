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
        // IMPORTANT: JNIEnv is thread-local on Android. Do not move this JNI
        // call onto Task.Run: the pool thread may not be attached to the JVM,
        // which can terminate the process with a native JNI abort instead of
        // producing a catchable managed exception. The caller already awaits
        // this method after all APK processing is complete.
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

        // Do not call JNIEnv.DeleteLocalRef(clazz) here.
        // On this Android/.NET runtime the class handle returned by FindClass
        // is managed as a global reference. Deleting it as a local reference
        // triggers ART's fatal "Attempt to delete global reference as local
        // JNI reference" abort, which matches the device crash log.
        return Task.CompletedTask;
    }
}
