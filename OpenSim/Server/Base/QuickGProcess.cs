using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
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
        private static bool m_dllStarted;
        private static readonly HttpClient s_healthClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(350) };

        [DllImport("Quick-G.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int QuickGStart(
            int listenPort,
            int controlPort,
            int brainPort,
            int regionPortStart,
            int regionPortEnd,
            int brainLeaseSeconds,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string certPath,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string keyPath,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string alpn,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string brainBind,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string regionPortExclude);

        [DllImport("Quick-G.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int QuickGStop();

        [DllImport("Quick-G.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int QuickGIsRunning();

        [DllImport("Quick-G.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr QuickGLastError();

        [DllImport("Quick-G.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void QuickGFreeString(IntPtr value);

        internal static void Start(IConfigSource source)
        {
            IConfig config = source.Configs["QuickG"];
            string enabled = config?.GetString("Enabled", "auto") ?? "auto";
            bool requested = enabled.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                enabled.Equals("yes", StringComparison.OrdinalIgnoreCase);
            bool automatic = enabled.Equals("auto", StringComparison.OrdinalIgnoreCase);
            if (!requested && !automatic)
                return;

            // Windows reports 10.0 for both releases; Windows 11 starts at build 22000.
            Version version = Environment.OSVersion.Version;
            bool windows10 = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                version.Major == 10 && version.Build < 22000;
            if (!requested && !windows10)
                return;

            int controlPort = config?.GetInt("ControlPort", 19001) ?? 19001;
            int brainPort = config?.GetInt("BrainPort", 19002) ?? 19002;
            bool allowNative = config?.GetBoolean(
                "AllowNativeQuicFallback",
                !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                ?? !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            // A manually started Quick-G may already be running before ROBUST.
            if (IsReady(controlPort))
            {
                MarkConnected(controlPort, brainPort);
                m_log.Info("[QUICK-G]: Connected to already-running Quick-G");
                return;
            }

            bool preferDll = config?.GetBoolean("UseDll", true) ?? true;
            if (preferDll && TryStartDll(config, controlPort, brainPort))
                return;

            string executable = config?.GetString("Executable", "Quick-G.exe") ?? "Quick-G.exe";
            if (!Path.IsPathRooted(executable))
                executable = Path.Combine(AppContext.BaseDirectory, executable);
            executable = Path.GetFullPath(executable);

            string arguments = BuildArguments(config, controlPort, brainPort);
            int exitCode = -1;

            if (File.Exists(executable))
            {
                try
                {
                    m_process = Process.Start(new ProcessStartInfo(executable, arguments)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = AppContext.BaseDirectory
                    });

                    if (WaitUntilReady(controlPort, 5000))
                    {
                        MarkConnected(controlPort, brainPort);
                        m_log.InfoFormat("[QUICK-G]: Started compatibility helper (pid {0})", m_process?.Id ?? 0);
                        return;
                    }

                    if (m_process != null && m_process.HasExited)
                        exitCode = m_process.ExitCode;
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[QUICK-G]: Could not launch helper: {0}", e.Message);
                }
                finally
                {
                    if (!IsReady(controlPort))
                        DisposeFailedChild();
                }
            }
            else
            {
                m_log.ErrorFormat("[QUICK-G]: Compatibility helper not found at {0}", executable);
            }

            if (allowNative)
            {
                m_log.Warn("[QUICK-G]: Continuing with explicitly allowed native QUIC fallback");
                return;
            }

            WaitForManualQuickG(controlPort, brainPort, executable, exitCode);
        }

        private static bool TryStartDll(IConfig config, int controlPort, int brainPort)
        {
            try
            {
                int listenPort = config?.GetInt("Port", 9001) ?? 9001;
                string brainBind = config?.GetString("BrainBind", "127.0.0.1") ?? "127.0.0.1";
                int regionPortStart = config?.GetInt("RegionPortStart", 22000) ?? 22000;
                int regionPortEnd = config?.GetInt("RegionPortEnd", 22500) ?? 22500;
                string regionPortExclude = config?.GetString("RegionPortExclude", "") ?? "";
                int leaseSeconds = config?.GetInt("BrainLeaseSeconds", 90) ?? 90;
                string cert = ResolveBasePath(config?.GetString("CertificatePath", "SSL/quic/quic-cert.pem") ?? "SSL/quic/quic-cert.pem");
                string key = ResolveBasePath(config?.GetString("PrivateKeyPath", "SSL/quic/quic-key.pem") ?? "SSL/quic/quic-key.pem");
                string alpn = config?.GetString("ALPN", "opensim-ll/1") ?? "opensim-ll/1";

                int started = QuickGStart(
                    listenPort,
                    controlPort,
                    brainPort,
                    regionPortStart,
                    regionPortEnd,
                    leaseSeconds,
                    cert,
                    key,
                    alpn,
                    brainBind,
                    regionPortExclude);

                if (started == 0)
                {
                    m_log.WarnFormat("[QUICK-G]: DLL start declined: {0}", GetDllLastError());
                    return false;
                }

                if (WaitUntilReady(controlPort, 5000))
                {
                    m_dllStarted = true;
                    MarkConnected(controlPort, brainPort);
                    m_log.Info("[QUICK-G]: Started in-process DLL compatibility helper");
                    return true;
                }

                string error = GetDllLastError();
                try { QuickGStop(); } catch { }
                m_log.WarnFormat("[QUICK-G]: DLL did not become ready{0}", string.IsNullOrEmpty(error) ? string.Empty : ": " + error);
            }
            catch (DllNotFoundException e)
            {
                m_log.WarnFormat("[QUICK-G]: DLL not found, falling back to EXE: {0}", e.Message);
            }
            catch (EntryPointNotFoundException e)
            {
                m_log.WarnFormat("[QUICK-G]: DLL export missing, falling back to EXE: {0}", e.Message);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[QUICK-G]: DLL start failed, falling back to EXE: {0}", e.Message);
            }
            return false;
        }

        private static string ResolveBasePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
                return path;
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        }

        private static string GetDllLastError()
        {
            IntPtr value = IntPtr.Zero;
            try
            {
                value = QuickGLastError();
                return value == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(value) ?? string.Empty;
            }
            catch { return string.Empty; }
            finally
            {
                if (value != IntPtr.Zero)
                {
                    try { QuickGFreeString(value); } catch { }
                }
            }
        }

        private static string BuildArguments(IConfig config, int controlPort, int brainPort)
        {
            int listenPort = config?.GetInt("Port", 9001) ?? 9001;
            string brainBind = config?.GetString("BrainBind", "127.0.0.1") ?? "127.0.0.1";
            int regionPortStart = config?.GetInt("RegionPortStart", 22000) ?? 22000;
            int regionPortEnd = config?.GetInt("RegionPortEnd", 22500) ?? 22500;
            string regionPortExclude = config?.GetString("RegionPortExclude", "") ?? "";
            int leaseSeconds = config?.GetInt("BrainLeaseSeconds", 90) ?? 90;
            string cert = config?.GetString("CertificatePath", "SSL/quic/quic-cert.pem") ?? "SSL/quic/quic-cert.pem";
            string key = config?.GetString("PrivateKeyPath", "SSL/quic/quic-key.pem") ?? "SSL/quic/quic-key.pem";
            string alpn = config?.GetString("ALPN", "opensim-ll/1") ?? "opensim-ll/1";

            return string.Format(
                "--parent-pid {0} --listen-port {1} --control-port {2} --brain-bind \"{3}\" --brain-port {4} " +
                "--region-port-start {5} --region-port-end {6} --region-port-exclude \"{7}\" --brain-lease-seconds {8} " +
                "--cert \"{9}\" --key \"{10}\" --alpn \"{11}\"",
                Process.GetCurrentProcess().Id,
                listenPort,
                controlPort,
                brainBind,
                brainPort,
                regionPortStart,
                regionPortEnd,
                regionPortExclude,
                leaseSeconds,
                cert,
                key,
                alpn);
        }

        private static void WaitForManualQuickG(int controlPort, int brainPort, string executable, int exitCode)
        {
            if (Console.IsInputRedirected)
                throw new InvalidOperationException(
                    "Quick-G is required and native QUIC fallback is disabled, but interactive retry is unavailable");

            while (true)
            {
                string message =
                    $"[QUICK-G]: Quick-G is REQUIRED but is not running (last exit {exitCode}).\n" +
                    $"           Start Quick-G manually: {executable}\n" +
                    "           Then press SPACE to check again. ROBUST QUIC startup is paused.";

                m_log.Error(message.Replace('\n', ' '));
                Console.Error.WriteLine();
                Console.Error.WriteLine("============================================================");
                Console.Error.WriteLine(" QUICK-G REQUIRED - ROBUST QUIC STARTUP PAUSED");
                Console.Error.WriteLine("============================================================");
                Console.Error.WriteLine(message);
                Console.Error.WriteLine("============================================================");

                while (Console.ReadKey(true).Key != ConsoleKey.Spacebar) { }

                if (WaitUntilReady(controlPort, 5000))
                {
                    MarkConnected(controlPort, brainPort);
                    m_log.Info("[QUICK-G]: Manual Quick-G detected; ROBUST QUIC startup continues");
                    return;
                }

                Console.Error.WriteLine("[QUICK-G]: Still unavailable. Start Quick-G, then press SPACE again.");
            }
        }

        private static bool IsReady(int controlPort)
        {
            try
            {
                using HttpResponseMessage response = s_healthClient.GetAsync(
                    $"http://127.0.0.1:{controlPort}/health").GetAwaiter().GetResult();
                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            catch (Exception) { }
            return false;
        }

        private static bool WaitUntilReady(int controlPort, int timeoutMs)
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (timeout.ElapsedMilliseconds < timeoutMs)
            {
                if (IsReady(controlPort))
                    return true;
                Thread.Sleep(100);
            }
            return false;
        }

        private static void MarkConnected(int controlPort, int brainPort)
        {
            Environment.SetEnvironmentVariable("OPENSIM_QUICKG_CONTROL_PORT", controlPort.ToString());
            Environment.SetEnvironmentVariable("OPENSIM_QUICKG_BRAIN_PORT", brainPort > 0 ? brainPort.ToString() : null);
        }

        private static void DisposeFailedChild()
        {
            Process process = m_process;
            m_process = null;
            if (process == null)
                return;

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(2000);
                }
            }
            catch { }
            finally
            {
                process.Dispose();
            }
        }

        internal static void Stop()
        {
            string controlPort = Environment.GetEnvironmentVariable("OPENSIM_QUICKG_CONTROL_PORT");
            Environment.SetEnvironmentVariable("OPENSIM_QUICKG_CONTROL_PORT", null);
            Environment.SetEnvironmentVariable("OPENSIM_QUICKG_BRAIN_PORT", null);

            Process process = m_process;
            m_process = null;
            try
            {
                if (m_dllStarted)
                {
                    m_dllStarted = false;
                    try { QuickGStop(); } catch { }
                }

                // This also shuts down a manually started helper that ROBUST adopted.
                if (!string.IsNullOrEmpty(controlPort))
                {
                    using StringContent content = new StringContent(string.Empty);
                    try
                    {
                        s_healthClient.PostAsync(
                            $"http://127.0.0.1:{controlPort}/shutdown", content).GetAwaiter().GetResult();
                    }
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
            finally
            {
                process?.Dispose();
            }
        }
    }
}
