using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LifeReplay.Core;

namespace LifeReplay.Sync;

/// <summary>
/// Self-signed TLS certificate for the sync endpoint. The private key lives in the current user's Windows
/// certificate store (protected by DPAPI). The phone pins the certificate's SHA-256 fingerprint, delivered
/// in the pairing QR code, so no public CA is involved.
/// </summary>
public static class CertificateManager
{
    private const string SubjectPrefix = "CN=LifeReplay Sync ";

    public static X509Certificate2 GetOrCreate(string syncDir, string deviceId)
    {
        Directory.CreateDirectory(syncDir);
        var thumbFile = Path.Combine(syncDir, "certificate-thumbprint.txt");
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        if (File.Exists(thumbFile))
        {
            var thumb = File.ReadAllText(thumbFile).Trim();
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumb, validOnly: false);
            foreach (var c in found)
                if (c.HasPrivateKey && c.NotAfter > DateTime.Now.AddDays(30)) return c;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(SubjectPrefix + deviceId[..8], key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName);
        san.AddDnsName("localhost");
        req.CertificateExtensions.Add(san.Build());
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        // Re-import so the key is persisted in the user key store (non-exportable).
        var persisted = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        if (OperatingSystem.IsWindows()) persisted.FriendlyName = "LifeReplay local sync";
        store.Add(persisted);
        File.WriteAllText(thumbFile, persisted.Thumbprint);
        Log.Info($"Sync: created TLS certificate {persisted.Thumbprint}");
        return persisted;
    }

    /// <summary>SHA-256 of the DER certificate, lowercase hex. This is what the phone pins.</summary>
    public static string Fingerprint(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();
}
