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

namespace OpenSim.Region.ClientStack.LindenUDP
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
        /// </summary>
        public X509Certificate2 LoadCertificate()
        {
            if (!string.IsNullOrEmpty(CertificatePath) && File.Exists(CertificatePath))
            {
                if (!string.IsNullOrEmpty(PrivateKeyPath) && File.Exists(PrivateKeyPath))
                    return X509Certificate2.CreateFromPemFile(CertificatePath, PrivateKeyPath);

                return new X509Certificate2(CertificatePath);
            }

            // Generate a self-signed certificate for development
            return GenerateSelfSignedCertificate();
        }

        /// <summary>
        /// Load the TLS certificate context for QUIC, including intermediate
        /// certificates when CertificatePath points at a PEM fullchain file.
        /// </summary>
        public SslStreamCertificateContext LoadCertificateContext()
        {
            X509Certificate2 certificate = LoadCertificate();
            X509Certificate2Collection intermediates = new X509Certificate2Collection();

            if (!string.IsNullOrEmpty(CertificatePath) && File.Exists(CertificatePath))
            {
                try
                {
                    X509Certificate2Collection pemCertificates = new X509Certificate2Collection();
                    pemCertificates.ImportFromPemFile(CertificatePath);
                    foreach (X509Certificate2 cert in pemCertificates)
                    {
                        if (!string.Equals(cert.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                            intermediates.Add(cert);
                    }
                }
                catch
                {
                    // Fall back to the leaf certificate only.
                }
            }

            return SslStreamCertificateContext.Create(certificate, intermediates, false);
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

                return new X509Certificate2(certificate.Export(X509ContentType.Pkcs12));
            }
        }
    }
}
