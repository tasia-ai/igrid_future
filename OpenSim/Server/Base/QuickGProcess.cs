using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using log4net;
using Nini.Config;

namespace OpenSim.Server.Base
{
    /// <summary>Owns the optional Quick-G compatibility sidecar for ROBUST.</summary>
    internal static class QuickGProcess
    {
        private static readonly ILog m_log = LogManager.GetLogger(typeof(QuickGProcess));
        private static Process m_process;
        private static readonly HttpClient s_healthClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(250) };

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

            try
            {
                m_process = Process.Start(new ProcessStartInfo(executable, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    WorkingDirectory = AppContext.BaseDirectory
                });
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[QUICK-G]: Could not launch helper: {0}; continuing with native QUIC", e.Message);
                return;
            }

            int controlPort = config?.GetInt("ControlPort", 19001) ?? 19001;
            if (!WaitUntilReady(controlPort))
            {
                int exitCode = m_process.HasExited ? m_process.ExitCode : -1;
                Stop();
                m_log.WarnFormat("[QUICK-G]: Helper failed its startup health check (exit {0}); continuing with native QUIC", exitCode);
                return;
            }

            Environment.SetEnvironmentVariable("OPENSIM_QUICKG_CONTROL_PORT", controlPort.ToString());
            m_log.InfoFormat("[QUICK-G]: Started compatibility helper (pid {0})", m_process.Id);
        }

        private static bool WaitUntilReady(int controlPort)
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (timeout.ElapsedMilliseconds < 5000 && m_process != null && !m_process.HasExited)
            {
                try
                {
                    using HttpResponseMessage response = s_healthClient.GetAsync(
                        $"http://127.0.0.1:{controlPort}/health").GetAwaiter().GetResult();
                    if (response.IsSuccessStatusCode)
                        return true;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                System.Threading.Thread.Sleep(100);
            }
            return false;
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
                // Closing ROBUST's end of the anonymous pipe is Quick-G's normal
                // shutdown signal. Do this before waiting for graceful exit.
                try { process.StandardInput.Close(); } catch { }
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
