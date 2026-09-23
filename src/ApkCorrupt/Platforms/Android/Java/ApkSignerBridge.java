package com.alphacat.apkcorrupt;

import com.android.apksig.ApkSigner;
import com.iyxan23.zipalignjava.ZipAlign;

import java.io.File;
import java.io.FileOutputStream;
import java.io.RandomAccessFile;
import java.io.InputStream;
import java.io.FileInputStream;
import java.io.IOException;
import java.nio.file.Files;
import java.security.KeyFactory;
import java.security.PrivateKey;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.security.spec.PKCS8EncodedKeySpec;
import java.util.Collections;

public final class ApkSignerBridge {
    private ApkSignerBridge() {}

    public static boolean alignAndSign(
            String input,
            String output,
            String keyPath,
            String certPath) throws Exception {

        File inFile = new File(input);
        File alignedFile = new File(input + ".aligned");

        try (RandomAccessFile raf = new RandomAccessFile(inFile, "r");
             FileOutputStream alignedOut = new FileOutputStream(alignedFile)) {

            ZipAlign.alignZip(raf, alignedOut);
        }

        byte[] keyBytes = Files.readAllBytes(new File(keyPath).toPath());
        PKCS8EncodedKeySpec spec = new PKCS8EncodedKeySpec(keyBytes);
        PrivateKey key = KeyFactory.getInstance("RSA").generatePrivate(spec);

        CertificateFactory factory = CertificateFactory.getInstance("X.509");
        X509Certificate cert;
        try (InputStream certIn = new FileInputStream(certPath)) {
            cert = (X509Certificate) factory.generateCertificate(certIn);
        }

        ApkSigner.SignerConfig signerConfig =
                new ApkSigner.SignerConfig.Builder(
                "APKCorrupt",
                key,
                Collections.singletonList(cert))
                .build();

        ApkSigner apkSigner = new ApkSigner.Builder(
                Collections.singletonList(signerConfig))
                .setInputApk(alignedFile)
                .setOutputApk(new File(output))
                .setV1SigningEnabled(true)
                .setV2SigningEnabled(true)
                .setV3SigningEnabled(true)
                .setV4SigningEnabled(false)
                .build();

        apkSigner.sign();

        if (!alignedFile.delete()) {
            alignedFile.deleteOnExit();
        }

        return new File(output).isFile() && new File(output).length() > 0;
    }
}
