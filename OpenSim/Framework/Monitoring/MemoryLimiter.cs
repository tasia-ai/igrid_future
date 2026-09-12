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
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
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
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using log4net;
using Nini.Config;

namespace OpenSim.Framework.Monitoring
{
    /// <summary>
    /// Per-process memory guard for Robust and OpenSim.
    ///
    /// [Startup] MemoryLimit accepts values such as "2GB", "1536MB",
    /// "2147483648" or "unlimited"/"0".  The watchdog checks the process every
    /// ten seconds, forces a full GC after a soft-limit breach and logs the
    /// result.
    ///
    /// By default the same value is also installed as a hard operating-system
    /// limit.  Windows uses a Job Object process-memory limit.  Linux uses
    /// RLIMIT_AS.  Set MemoryHardLimit = false to retain only the soft watchdog.
    /// Failure to install the OS limit is deliberately non-fatal: a warning is
    /// logged and the soft watchdog remains active.
    /// </summary>
    public static class MemoryLimiter
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Default cap for a single Robust/OpenSim process.</summary>
        public const long DefaultLimitBytes = 2L * 1024 * 1024 * 1024;

        private const int CheckIntervalMs = 10000;
        private const int MaxConsecutiveBreaches = 3;

        // Windows Job Object constants.
        private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
        private const int JobObjectExtendedLimitInformation = 9;

        // Linux resource limit constant from sys/resource.h.
        private const int RLIMIT_AS = 9;

        private static long m_limitBytes;
        private static volatile bool m_running;
        private static int m_consecutiveBreaches;
        private static Thread m_thread;

        // Keep the Job Object handle alive for the lifetime of the process.
        // Closing it would remove the hard limit from the current process.
        private static IntPtr m_windowsJob = IntPtr.Zero;

        /// <summary>
        /// Current configured limit in bytes (0 = unlimited).
        /// </summary>
        public static long MemoryLimitBytes
        {
            get { return m_limitBytes; }
        }

        /// <summary>
        /// Reads [Startup] MemoryLimit and MemoryHardLimit and starts the guard.
        /// Safe to call more than once (does nothing if already running).
        /// </summary>
        public static void Start(IConfigSource config)
        {
            if (m_running)
                return;

            long limit = DefaultLimitBytes;
            bool hardLimit = true;

            IConfig startupConfig = config.Configs["Startup"];
            if (startupConfig != null)
            {
                string raw = startupConfig.GetString("MemoryLimit", string.Empty);
                hardLimit = startupConfig.GetBoolean("MemoryHardLimit", true);

                m_log.InfoFormat(
                    "[MEMORY LIMITER]: MemoryLimit = {0}, MemoryHardLimit = {1}",
                    string.IsNullOrEmpty(raw) ? "(default)" : raw,
                    hardLimit);

                limit = ParseSizeString(raw, DefaultLimitBytes);
            }
            else
            {
                m_log.InfoFormat(
                    "[MEMORY LIMITER]: No [Startup] section, using default limit {0} with OS hard limit enabled",
                    FormatBytes(DefaultLimitBytes));
            }

            StartWithLimit(limit, hardLimit);
        }

        /// <summary>
        /// Starts the guard with an explicit byte limit and the OS hard limit enabled.
        /// </summary>
        public static void StartWithLimit(long limitBytes)
        {
            StartWithLimit(limitBytes, true);
        }

        /// <summary>
        /// Starts the guard with an explicit byte limit.
        /// </summary>
        public static void StartWithLimit(long limitBytes, bool enforceHardLimit)
        {
            if (m_running)
                return;

            m_limitBytes = limitBytes;
            ValidateConfiguredLimit();

            if (m_limitBytes == 0)
            {
                m_log.Info("[MEMORY LIMITER]: Limit disabled, watchdog and OS hard cap not started");
                return;
            }

            if (enforceHardLimit)
                ApplyOperatingSystemHardLimit(m_limitBytes);
            else
                m_log.Info("[MEMORY LIMITER]: OS hard limit disabled by MemoryHardLimit=false");

            m_running = true;
            m_consecutiveBreaches = 0;
            m_thread = new Thread(Loop);
            m_thread.IsBackground = true;
            m_thread.Name = "MemoryLimiter";
            m_thread.Start();
        }

        /// <summary>
        /// Stops the watchdog thread.  An already-installed OS hard limit remains
        /// in force until the process exits.
        /// </summary>
        public static void Stop()
        {
            m_running = false;
            try
            {
                if (m_thread != null && m_thread.IsAlive)
                    m_thread.Join(2000);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[MEMORY LIMITER]: Error stopping watchdog: {0}", e.Message);
            }
        }

        private static void ValidateConfiguredLimit()
        {
            if (m_limitBytes < 0)
            {
                m_log.Warn("[MEMORY LIMITER]: Negative MemoryLimit, disabling watchdog");
                m_limitBytes = 0;
                return;
            }

            long current = Process.GetCurrentProcess().WorkingSet64;
            if (m_limitBytes != 0 && current > m_limitBytes)
            {
                m_log.WarnFormat(
                    "[MEMORY LIMITER]: Current working set ({0}) already exceeds configured limit ({1})",
                    FormatBytes(current), FormatBytes(m_limitBytes));
            }
        }

        private static void ApplyOperatingSystemHardLimit(long limitBytes)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    ApplyWindowsJobLimit(limitBytes);
                    RefreshGcMemoryLimit();
                    return;
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    ApplyLinuxAddressSpaceLimit(limitBytes);
                    RefreshGcMemoryLimit();
                    return;
                }

                m_log.WarnFormat(
                    "[MEMORY LIMITER]: No in-process hard-limit implementation for {0}; soft watchdog only",
                    RuntimeInformation.OSDescription);
            }
            catch (Exception e)
            {
                m_log.WarnFormat(
                    "[MEMORY LIMITER]: Could not install OS hard limit {0}: {1}. Soft watchdog will continue.",
                    FormatBytes(limitBytes), e.Message);
            }
        }

        private static void RefreshGcMemoryLimit()
        {
            try
            {
                // The Job Object/cgroup-style environment can change after the CLR
                // starts.  Ask .NET 8 to refresh the GC's view of available memory.
                GC.RefreshMemoryLimit();
                long gcAvailable = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                m_log.InfoFormat(
                    "[MEMORY LIMITER]: GC available-memory view after OS limit: {0}",
                    FormatBytes(gcAvailable));
            }
            catch (Exception e)
            {
                // The OS limit is still valid even if the GC cannot refresh its view.
                m_log.WarnFormat(
                    "[MEMORY LIMITER]: OS hard limit is active but GC memory-limit refresh failed: {0}",
                    e.Message);
            }
        }

        private static void ApplyWindowsJobLimit(long limitBytes)
        {
            if (m_windowsJob != IntPtr.Zero)
                return;

            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                throw new InvalidOperationException(
                    "CreateJobObject failed with Win32 error " + Marshal.GetLastWin32Error());

            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr infoPtr = Marshal.AllocHGlobal(size);

            try
            {
                JOBOBJECT_EXTENDED_LIMIT_INFORMATION info =
                    new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_PROCESS_MEMORY;
                info.ProcessMemoryLimit = ToUIntPtr(limitBytes);

                Marshal.StructureToPtr(info, infoPtr, false);

                if (!SetInformationJobObject(
                        job,
                        JobObjectExtendedLimitInformation,
                        infoPtr,
                        (uint)size))
                {
                    throw new InvalidOperationException(
                        "SetInformationJobObject failed with Win32 error " +
                        Marshal.GetLastWin32Error());
                }

                using Process current = Process.GetCurrentProcess();
                if (!AssignProcessToJobObject(job, current.Handle))
                {
                    throw new InvalidOperationException(
                        "AssignProcessToJobObject failed with Win32 error " +
                        Marshal.GetLastWin32Error() +
                        " (the process may already belong to a non-nestable Job Object)");
                }

                m_windowsJob = job;
                job = IntPtr.Zero;

                m_log.InfoFormat(
                    "[MEMORY LIMITER]: Windows Job Object hard process-memory limit applied: {0}",
                    FormatBytes(limitBytes));
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
                if (job != IntPtr.Zero)
                    CloseHandle(job);
            }
        }

        private static void ApplyLinuxAddressSpaceLimit(long limitBytes)
        {
            RLimit existing;
            if (getrlimit(RLIMIT_AS, out existing) != 0)
            {
                throw new InvalidOperationException(
                    "getrlimit(RLIMIT_AS) failed with errno " + Marshal.GetLastWin32Error());
            }

            ulong requested = checked((ulong)limitBytes);
            ulong currentSoft = existing.Current.ToUInt64();
            ulong currentHard = existing.Maximum.ToUInt64();
            ulong infinity = UIntPtr.Size == 8 ? ulong.MaxValue : uint.MaxValue;

            // Never loosen a limit that was already stricter than our configuration.
            ulong effective = requested;
            if (currentSoft != infinity && currentSoft < effective)
                effective = currentSoft;
            if (currentHard != infinity && currentHard < effective)
                effective = currentHard;

            RLimit target = new RLimit
            {
                Current = ToUIntPtr(effective),
                Maximum = ToUIntPtr(effective)
            };

            if (setrlimit(RLIMIT_AS, ref target) != 0)
            {
                throw new InvalidOperationException(
                    "setrlimit(RLIMIT_AS) failed with errno " + Marshal.GetLastWin32Error());
            }

            m_log.InfoFormat(
                "[MEMORY LIMITER]: Linux RLIMIT_AS hard address-space limit applied: {0}{1}",
                FormatBytes((long)effective),
                effective == requested ? string.Empty : " (existing OS limit was stricter)");
        }

        private static UIntPtr ToUIntPtr(long value)
        {
            return ToUIntPtr(checked((ulong)value));
        }

        private static UIntPtr ToUIntPtr(ulong value)
        {
            if (UIntPtr.Size == 4 && value > uint.MaxValue)
                throw new OverflowException("Memory limit does not fit the native 32-bit size type");

            return new UIntPtr(value);
        }

        private static void Loop()
        {
            m_log.InfoFormat(
                "[MEMORY LIMITER]: Watchdog started, limit = {0}, check interval = {1}s",
                FormatBytes(m_limitBytes), CheckIntervalMs / 1000);

            while (m_running)
            {
                try
                {
                    Thread.Sleep(CheckIntervalMs);
                    if (!m_running)
                        break;

                    CheckAndEnforce();
                }
                catch (ThreadAbortException)
                {
                    break;
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[MEMORY LIMITER]: Watchdog error: {0}", e);
                }
            }
        }

        private static void CheckAndEnforce()
        {
            Process process = Process.GetCurrentProcess();
            long workingSet = process.WorkingSet64;
            long privateBytes = process.PrivateMemorySize64;

            if (workingSet <= m_limitBytes && privateBytes <= m_limitBytes)
            {
                m_consecutiveBreaches = 0;
                return;
            }

            m_consecutiveBreaches++;

            long before = GC.GetTotalMemory(false);
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            long after = GC.GetTotalMemory(true);

            long postGcWorkingSet = process.WorkingSet64;

            m_log.WarnFormat(
                "[MEMORY LIMITER]: Working set {0} / limit {1} (private {2}). " +
                "Forced GC: managed heap {3} -> {4}. Breach {5}/{6}.",
                FormatBytes(workingSet), FormatBytes(m_limitBytes),
                FormatBytes(privateBytes), FormatBytes(before), FormatBytes(after),
                m_consecutiveBreaches, MaxConsecutiveBreaches);

            if (postGcWorkingSet <= m_limitBytes)
            {
                m_log.Info("[MEMORY LIMITER]: Forced GC brought memory back under the limit");
                m_consecutiveBreaches = 0;
            }
        }

        /// <summary>
        /// Parses a human readable size string such as "2GB", "1536MB", "4096K"
        /// or a plain byte count. Empty/invalid input returns the fallback.
        /// "0", "none" and "unlimited" return 0 (disabled).
        /// </summary>
        public static long ParseSizeString(string value, long fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            string s = value.Trim().ToLowerInvariant();
            if (s == "0" || s == "none" || s == "unlimited" || s == "off" || s == "false")
                return 0;

            double multiplier = 1;
            if (s.EndsWith("kb"))
            {
                multiplier = 1024;
                s = s.Substring(0, s.Length - 2);
            }
            else if (s.EndsWith("mb"))
            {
                multiplier = 1024 * 1024;
                s = s.Substring(0, s.Length - 2);
            }
            else if (s.EndsWith("gb"))
            {
                multiplier = 1024L * 1024 * 1024;
                s = s.Substring(0, s.Length - 2);
            }
            else if (s.EndsWith("b"))
            {
                s = s.Substring(0, s.Length - 1);
            }

            s = s.Trim();
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount) || amount <= 0)
                return fallback;

            double total = amount * multiplier;
            if (total > long.MaxValue)
                return long.MaxValue;

            return (long)total;
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return string.Format("{0:F2} GiB", bytes / (1024.0 * 1024 * 1024));
            if (bytes >= 1024 * 1024)
                return string.Format("{0:F1} MiB", bytes / (1024.0 * 1024));
            if (bytes >= 1024)
                return string.Format("{0:F0} KiB", bytes / 1024.0);
            return bytes + " B";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RLimit
        {
            public UIntPtr Current;
            public UIntPtr Maximum;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob,
            int infoType,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("libc", SetLastError = true)]
        private static extern int getrlimit(int resource, out RLimit rlim);

        [DllImport("libc", SetLastError = true)]
        private static extern int setrlimit(int resource, ref RLimit rlim);
    }
}
