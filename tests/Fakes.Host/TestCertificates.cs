using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Retroscope.Fakes.Host;

/// <summary>A throwaway CA and one server certificate it signs, valid for 30 days from start-up.</summary>
public sealed class TestCertificates {
    private const string SystemRoots = "/etc/ssl/certs/ca-certificates.crt";

    private readonly X509Certificate2 _authority;

    private TestCertificates(X509Certificate2 authority, X509Certificate2 server) {
        _authority = authority;
        Server = server;
    }

    public X509Certificate2 Server { get; }

    public static TestCertificates Create(IReadOnlyList<string> dnsNames) {
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = notBefore.AddDays(31);

        using var authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityRequest = new CertificateRequest("CN=Retroscope test CA", authorityKey, HashAlgorithmName.SHA256);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        authorityRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(authorityRequest.PublicKey, false));
        var authority = authorityRequest.CreateSelfSigned(notBefore, notAfter);

        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var serverRequest = new CertificateRequest($"CN={dnsNames[0]}", serverKey, HashAlgorithmName.SHA256);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        serverRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
        var names = new SubjectAlternativeNameBuilder();
        foreach (var name in dnsNames) {
            names.AddDnsName(name);
        }
        serverRequest.CertificateExtensions.Add(names.Build());

        using var signed = serverRequest.Create(authority, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
        // Exported and re-imported as PKCS#12 so Kestrel on Linux gets a certificate whose key it can use for TLS.
        using var withKey = signed.CopyWithPrivateKey(serverKey);
        var server = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        return new TestCertificates(authority, server);
    }

    /// <summary>Writes the CA followed by the system roots, through a temporary file so a reader never sees half of it.</summary>
    public async Task WriteBundleAsync(string path) {
        var bundle = new StringBuilder(_authority.ExportCertificatePem()).Append('\n');
        if (File.Exists(SystemRoots)) {
            bundle.Append(await File.ReadAllTextAsync(SystemRoots));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, bundle.ToString());
        File.Move(temporary, path, overwrite: true);
    }
}
