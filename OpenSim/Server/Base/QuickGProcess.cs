using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using log4net;
using Nini.Config;

namespace OpenSim.Server.Base
{
    /// <summary>Owns the optional Quick-G compatibility sidecar for ROBUST.</summary>
    internal static class QuickGProcess
    {
        private static readonly ILog m_log = LogManager.GetLogger(typeof(QuickGProcess));
        private static Process m_process;

        internal static void Start(IConfigSource source)
        {
            IConfig config = source.Configs["QuickG"];
            string enabled = config?.GetString("Enabled", "auto") ?? "auto";
            bool requested = enabled.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                enabled.Equals("yes", StringComparison.OrdinalIgnoreCase);
            if (!requested && !enabled.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return;

            // Windows reports 10.0 for both releases; Windows 11 starts at build 22000.
            Version version = Environment.OSVersion.Version;
            bool windows10 = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                version.Major == 10 && version.Build < 22000;
            if (!requested && !windows10)
                return;

            string executable = config?.GetString("Executable", "Quick-G.exe") ?? "Quick-G.exe";
            executable = Path.GetFullPath(executable);
            if (!File.Exists(executable))
            {
                m_log.WarnFormat("[QUICK-G]: Compatibility helper not found at {0}; continuing with native QUIC", executable);
                return;
            }

            string arguments = string.Format(
                "--parent-pid {0} --listen-port {1} --control-port {2} --cert \"{3}\" --key \"{4}\" --alpn \"{5}\"",
                Process.GetCurrentProcess().Id,
                config?.GetInt("Port", 9001) ?? 9001,
                config?.GetInt("ControlPort", 19001) ?? 19001,
                config?.GetString("CertificatePath", "SSL/quic/quic-cert.pem") ?? "SSL/quic/quic-cert.pem",
                config?.GetString("PrivateKeyPath", "SSL/quic/quic-key.pem") ?? "SSL/quic/quic-key.pem",
                config?.GetString("ALPN", "opensim-ll/1") ?? "opensim-ll/1");

            m_process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                WorkingDirectory = AppContext.BaseDirectory
            });
            if (m_process == null || m_process.WaitForExit(500))
            {
                int exitCode = m_process?.ExitCode ?? -1;
                m_process?.Dispose();
                m_process = null;
                m_log.WarnFormat("[QUICK-G]: Helper failed during startup (exit {0}); continuing with native QUIC", exitCode);
                return;
            }
            Environment.SetEnvironmentVariable("OPENSIM_QUICKG_CONTROL_PORT", (config?.GetInt("ControlPort", 19001) ?? 19001).ToString());
            m_log.InfoFormat("[QUICK-G]: Started compatibility helper (pid {0})", m_process.Id);
        }

        internal static void Stop()
        {
            Environment.SetEnvironmentVariable("OPENSIM_QUICKG_CONTROL_PORT", null);
            Process process = m_process;
            m_process = null;
            if (process == null)
                return;
            try
            {
                if (!process.HasExited && !process.WaitForExit(3000))
                    process.Kill(true);
                process.WaitForExit(2000);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[QUICK-G]: Error stopping helper: {0}", e.Message);
            }
            finally { process.Dispose(); }
        }
    }
}
