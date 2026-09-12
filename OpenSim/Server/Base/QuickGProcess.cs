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
        private static bool m_retrying;
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

            int controlPort = config?.GetInt("ControlPort", 19001) ?? 19001;
            string executable = config?.GetString("Executable", "Quick-G.exe") ?? "Quick-G.exe";
            // Config paths are distribution-relative, independent of the shell's
            // current working directory when ROBUST is launched as a service.
            if (!Path.IsPathRooted(executable))
                executable = Path.Combine(AppContext.BaseDirectory, executable);
            executable = Path.GetFullPath(executable);
            if (!File.Exists(executable))
            {
                m_log.ErrorFormat("[QUICK-G]: Compatibility helper not found at {0}", executable);
                HandleStartupFailure(source, config, controlPort, -1);
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
                    WorkingDirectory = AppContext.BaseDirectory
                });
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[QUICK-G]: Could not launch helper: {0}", e.Message);
                HandleStartupFailure(source, config, controlPort, -1);
                return;
            }

            if (!WaitUntilReady(controlPort))
            {
                int exitCode = m_process.HasExited ? m_process.ExitCode : -1;
                Stop();
                HandleStartupFailure(source, config, controlPort, exitCode);
                return;
            }

            Environment.SetEnvironmentVariable("OPENSIM_QUICKG_CONTROL_PORT", controlPort.ToString());
            m_log.InfoFormat("[QUICK-G]: Started compatibility helper (pid {0})", m_process.Id);
        }

        private static void HandleStartupFailure(IConfigSource source, IConfig config, int controlPort, int exitCode)
        {
            bool allowNative = config?.GetBoolean("AllowNativeQuicFallback", !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                ?? !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            if (allowNative)
            {
                m_log.Warn("[QUICK-G]: Continuing with explicitly allowed native QUIC fallback");
                return;
            }
            if (m_retrying)
                throw new InvalidOperationException("Quick-G failed twice and native QUIC fallback is disabled");

            WaitForRetry(exitCode);
            if (WaitUntilReady(controlPort))
            {
                Environment.SetEnvironmentVariable("OPENSIM_QUICKG_CONTROL_PORT", controlPort.ToString());
                m_log.Info("[QUICK-G]: Connected to the manually started compatibility helper");
                return;
            }

            m_retrying = true;
            try { Start(source); }
            finally { m_retrying = false; }
        }

        private static void WaitForRetry(int exitCode)
        {
            string message = $"[QUICK-G]: Startup failed (exit {exitCode}). Native QUIC fallback is disabled. " +
                "Start Quick-G manually if desired, then press SPACE to check again and retry.";
            m_log.Error(message);
            Console.Error.WriteLine(message);
            if (Console.IsInputRedirected)
                throw new InvalidOperationException("Quick-G is required, but interactive retry is unavailable");

            while (Console.ReadKey(true).Key != ConsoleKey.Spacebar) { }
        }

        private static bool WaitUntilReady(int controlPort)
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (timeout.ElapsedMilliseconds < 5000 && (m_process == null || !m_process.HasExited))
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
            string controlPort = Environment.GetEnvironmentVariable("OPENSIM_QUICKG_CONTROL_PORT");
            Environment.SetEnvironmentVariable("OPENSIM_QUICKG_CONTROL_PORT", null);
            Process process = m_process;
            m_process = null;
            try
            {
                // Request graceful cancellation through the loopback-only control
                // endpoint. Parent-handle monitoring covers abnormal ROBUST exits.
                if (!string.IsNullOrEmpty(controlPort))
                {
                    using StringContent content = new StringContent(string.Empty);
                    try { s_healthClient.PostAsync($"http://127.0.0.1:{controlPort}/shutdown", content).GetAwaiter().GetResult(); }
                    catch { }
                }
                if (process == null)
                    return;
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
