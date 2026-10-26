using System.Security.Cryptography.X509Certificates;

namespace TasiaAddons.Quic
{
    /// <summary>
    /// Windows Schannel cannot perform TLS handshakes with an ephemeral
    /// certificate private key. Persist the imported key in the current
    /// service user's key store on Windows; keep ephemeral imports elsewhere.
    /// </summary>
    internal static class QuicCertificateKeyStorage
    {
        internal static X509KeyStorageFlags ImportFlags =>
            X509KeyStorageFlags.Exportable |
            (System.OperatingSystem.IsWindows()
                ? X509KeyStorageFlags.PersistKeySet
                : X509KeyStorageFlags.EphemeralKeySet);
    }
}
