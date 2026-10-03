/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS "AS IS" AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Net.Security;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nini.Config;

using OpenSim.Region.ClientStack.LindenUDP;
namespace TasiaAddons.Quic
{
    /// <summary>
    /// Configuration for the QUIC server transport.
    /// Reads from the [ClientStack.Quic] config section.
    /// </summary>
    public class QuicServerConfig
    {
        public bool Enabled { get; private set; } = false;

        /// <summary>
        /// Simulator-local QUIC listener port. Set to 0 to ask the Quick-G brain
        /// for an automatically leased port before the region listener starts.
        /// </summary>
        public int Port { get; private set; } = 9001;

        /// <summary>
        /// Host and port to publish in RegionInfo for viewers.
        ///
        /// These are separate from the listener on purpose. A region listens on
        /// an internal per-region port from the Quick-G pool (22200-22400),
        /// which is not reachable from outside. Viewers must be pointed at the
        /// public endpoint instead - Robust's QUIC proxy on 22002 - which routes
        /// back to the right region through the brain. Publishing the listener
        /// port makes the viewer connect somewhere it cannot reach, and the
        /// Tasia viewer then waits for a connection that never completes instead
        /// of falling back to LLUDP.
        ///
        /// AdvertisePort 0 means "publish the listener port", which is only
        /// correct when the listener port is itself reachable.
        /// </summary>
        public string AdvertiseHost { get; private set; } = "";
        public int AdvertisePort { get; private set; } = 0;

        public string Alpn { get; private set; } = "opensim-ll/1";
        public int IdleTimeoutMs { get; private set; } = 60000;
        public int KeepaliveMs { get; private set; } = 30000;
        public int MaxBidirectionalStreams { get; private set; } = 1024;
        public int MaxDatagramSize { get; private set; } = 1200;
        public bool RequireTasiaViewer { get; private set; } = false;
        public bool AllowLegacyLLUDP { get; private set; } = true;
        public bool LogPackets { get; private set; } = false;
        public bool LogHandshake { get; private set; } = true;
        public string CertificatePath { get; private set; } = "";
        public string PrivateKeyPath { get; private set; } = "";
        /// <summary>
        /// Password for PKCS#12 (.p12/.pfx) certificate files. Empty for
        /// passwordless files. Needed for OpenSSL-based MsQuic on Windows 10.
        /// </summary>
        public string CertificatePassword { get; private set; } = "";

        /// <summary>
        /// Direct Quick-G brain URL used only when Port=0. When blank, the region
        /// derives the host from ProxyRegistrationURL and uses BrainPort.
        /// </summary>
        public string BrainURL { get; private set; } = "";
        public int BrainPort { get; private set; } = 19002;
        public int BrainHeartbeatSeconds { get; private set; } = 30;

        /// <summary>
        /// Load configuration from the [ClientStack.Quic] section.
        /// </summary>
        public static QuicServerConfig FromConfig(IConfigSource configSource)
        {
            var config = new QuicServerConfig();
            IConfig quicConfig = configSource.Configs["ClientStack.Quic"];

            if (quicConfig == null)
                return config;

            config.Enabled = quicConfig.GetBoolean("Enabled", config.Enabled);
            config.Port = quicConfig.GetInt("Port", config.Port);
            config.AdvertiseHost = quicConfig.GetString("AdvertiseHost", config.AdvertiseHost);
            config.AdvertisePort = quicConfig.GetInt("AdvertisePort", config.AdvertisePort);
            config.Alpn = quicConfig.GetString("ALPN", config.Alpn);
            config.IdleTimeoutMs = quicConfig.GetInt("IdleTimeoutMs", config.IdleTimeoutMs);
            config.KeepaliveMs = quicConfig.GetInt("KeepaliveMs", config.KeepaliveMs);
            config.MaxBidirectionalStreams = quicConfig.GetInt("MaxBidirectionalStreams", config.MaxBidirectionalStreams);
            config.MaxDatagramSize = quicConfig.GetInt("MaxDatagramSize", config.MaxDatagramSize);
            config.RequireTasiaViewer = quicConfig.GetBoolean("RequireTasiaViewer", config.RequireTasiaViewer);
            config.AllowLegacyLLUDP = quicConfig.GetBoolean("AllowLegacyLLUDP", config.AllowLegacyLLUDP);
            config.LogPackets = quicConfig.GetBoolean("LogPackets", config.LogPackets);
            config.LogHandshake = quicConfig.GetBoolean("LogHandshake", config.LogHandshake);
            config.CertificatePath = quicConfig.GetString("CertificatePath", config.CertificatePath);
            config.PrivateKeyPath = quicConfig.GetString("PrivateKeyPath", config.PrivateKeyPath);
            config.CertificatePassword = quicConfig.GetString("CertificatePassword", config.CertificatePassword);
            config.BrainURL = quicConfig.GetString("BrainURL", config.BrainURL);
            config.BrainPort = quicConfig.GetInt("BrainPort", config.BrainPort);
            config.BrainHeartbeatSeconds = Math.Max(5, quicConfig.GetInt("BrainHeartbeatSeconds", config.BrainHeartbeatSeconds));

            return config;
        }

        internal void UseAssignedPort(int port)
        {
            if (port <= 0 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));
            Port = port;
        }

        /// <summary>
        /// Load or generate a TLS certificate for QUIC.
        /// If CertificatePath is set, loads from file.
        /// Otherwise generates a self-signed cert for localhost.
        /// The returned certificate always carries an exportable ephemeral
        /// private key, which OpenSSL-based MsQuic requires on Windows 10
        /// (SChannel-backed keys are not usable there).
        /// </summary>
        public X509Certificate2 LoadCertificate()
        {
            const X509KeyStorageFlags exportableEphemeral =
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet;

            string effCert = EffectiveCertPath();
            string effKey = EffectiveKeyPath();
            string effP12 = EffectiveP12Path();
            if (!string.IsNullOrEmpty(effCert) && File.Exists(effCert))
            {
                if (!string.IsNullOrEmpty(effKey) && File.Exists(effKey))
                {
                    using (X509Certificate2 pem = X509Certificate2.CreateFromPemFile(effCert, effKey))
                        return new X509Certificate2(pem.Export(X509ContentType.Pkcs12), (string)null, exportableEphemeral);
                }

                // P12 fallback ( central bin/SSL/quic/quic-cert.p12 ) if no PEM key
                if (!string.IsNullOrEmpty(effP12) && File.Exists(effP12))
                    return new X509Certificate2(effP12, CertificatePassword, exportableEphemeral);
                return new X509Certificate2(effCert, CertificatePassword, exportableEphemeral);
            }

            // Generate a self-signed certificate for development
            return GenerateSelfSignedCertificate();
        }

        private SslStreamCertificateContext m_cachedContext;
        private DateTime m_certLastWriteUtc = DateTime.MinValue;
        private DateTime m_keyLastWriteUtc = DateTime.MinValue;
        private readonly object m_certLock = new();

        // Centralized cert fallback — allows all 30 sims + Robust to share one file in bin/SSL/quic
        // Renewal then writes once and every handshake picks it up via mtime check. No restart.
        private static readonly string s_centralCert = @"H:\grid\igrid-package\bin\SSL\quic\quic-cert.pem";
        private static readonly string s_centralKey = @"H:\grid\igrid-package\bin\SSL\quic\quic-key.pem";
        private static readonly string s_centralP12 = @"H:\grid\igrid-package\bin\SSL\quic\quic-cert.p12";

        private string EffectiveCertPath()
        {
            // Prefer central bin/SSL/quic — single renewal writes once for all 31 processes
            if (File.Exists(s_centralCert)) return s_centralCert;
            try { string alt3 = Path.Combine(@"H:\grid\igrid-package\bin", "SSL", "quic", "quic-cert.pem"); if (File.Exists(alt3)) return alt3; } catch { }
            if (!string.IsNullOrEmpty(CertificatePath) && File.Exists(CertificatePath)) return CertificatePath;
            try { string alt = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SSL", "quic", "quic-cert.pem"); if (File.Exists(alt)) return alt; } catch { }
            try { string alt2 = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "bin", "SSL", "quic", "quic-cert.pem")); if (File.Exists(alt2)) return Path.GetFullPath(alt2); } catch { }
            return CertificatePath;
        }
        private string EffectiveKeyPath()
        {
            if (File.Exists(s_centralKey)) return s_centralKey;
            try { string alt3 = Path.Combine(@"H:\grid\igrid-package\bin", "SSL", "quic", "quic-key.pem"); if (File.Exists(alt3)) return alt3; } catch { }
            if (!string.IsNullOrEmpty(PrivateKeyPath) && File.Exists(PrivateKeyPath)) return PrivateKeyPath;
            try { string alt = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SSL", "quic", "quic-key.pem"); if (File.Exists(alt)) return alt; } catch { }
            try { string alt2 = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "bin", "SSL", "quic", "quic-key.pem")); if (File.Exists(alt2)) return Path.GetFullPath(alt2); } catch { }
            return PrivateKeyPath;
        }
        private string EffectiveP12Path()
        {
            if (!string.IsNullOrEmpty(CertificatePath) && File.Exists(CertificatePath) && CertificatePath.EndsWith(".p12", StringComparison.OrdinalIgnoreCase)) return CertificatePath;
            if (File.Exists(s_centralP12)) return s_centralP12;
            return CertificatePath;
        }

        /// <summary>
        /// Load the TLS certificate context for QUIC, including intermediate
        /// certificates when CertificatePath points at a PEM fullchain file.
        /// Cached by file mtime — new QUIC handshakes automatically pick up a
        /// rotated Let's Encrypt cert (monthly) without restarting the sim.
        /// Thread-safe: OnConnectionOptions is called concurrently per handshake.
        /// </summary>
        public SslStreamCertificateContext LoadCertificateContext()
        {
            string effCert = EffectiveCertPath();
            string effKey = EffectiveKeyPath();
            DateTime curCertWrite = DateTime.MinValue;
            DateTime curKeyWrite = DateTime.MinValue;
            try { if (!string.IsNullOrEmpty(effCert) && File.Exists(effCert)) curCertWrite = File.GetLastWriteTimeUtc(effCert); } catch { }
            try { if (!string.IsNullOrEmpty(effKey) && File.Exists(effKey)) curKeyWrite = File.GetLastWriteTimeUtc(effKey); } catch { }

            lock (m_certLock)
            {
                if (m_cachedContext != null && curCertWrite == m_certLastWriteUtc && curKeyWrite == m_keyLastWriteUtc)
                    return m_cachedContext;
            }

            // Build fresh outside lock (file I/O + crypto) — if it fails we keep serving old cert.
            X509Certificate2 certificate;
            X509Certificate2Collection intermediates = new X509Certificate2Collection();
            try
            {
                certificate = LoadCertificate();
                string effForChain = EffectiveCertPath();
                if (!string.IsNullOrEmpty(effForChain) && File.Exists(effForChain))
                {
                    try
                    {
                        X509Certificate2Collection pemCertificates = new X509Certificate2Collection();
                        pemCertificates.ImportFromPemFile(effForChain);
                        foreach (X509Certificate2 cert in pemCertificates)
                            if (!string.Equals(cert.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                                intermediates.Add(cert);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                lock (m_certLock)
                {
                    if (m_cachedContext != null)
                        return m_cachedContext; // keep serving old cert on transient read error
                    throw; // no cached cert — bubble up
                }
            }

            var ctx = SslStreamCertificateContext.Create(certificate, intermediates, false);
            lock (m_certLock)
            {
                // Another thread may have already refreshed while we were building — keep newest.
                if (m_cachedContext == null || curCertWrite != m_certLastWriteUtc || curKeyWrite != m_keyLastWriteUtc)
                {
                    m_cachedContext = ctx;
                    m_certLastWriteUtc = curCertWrite;
                    m_keyLastWriteUtc = curKeyWrite;
                }
                else
                {
                    // We raced — return the winner's context and let ours be GC'd.
                    return m_cachedContext;
                }
            }
            return ctx;
        }

        private static X509Certificate2 GenerateSelfSignedCertificate()
        {
            using (var rsa = System.Security.Cryptography.RSA.Create(2048))
            {
                var request = new CertificateRequest(
                    "CN=localhost",
                    rsa,
                    System.Security.Cryptography.HashAlgorithmName.SHA256,
                    System.Security.Cryptography.RSASignaturePadding.Pkcs1);

                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(false, false, 0, false));

                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection
                        {
                            new Oid("1.3.6.1.5.5.7.3.1") // Server Authentication
                        }, true));

                var certificate = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddYears(10));

                return new X509Certificate2(certificate.Export(X509ContentType.Pkcs12), (string)null,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
            }
        }
    }
}
