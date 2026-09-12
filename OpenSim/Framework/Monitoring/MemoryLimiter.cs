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
using System.Threading;
using log4net;
using Nini.Config;

namespace OpenSim.Framework.Monitoring
{
    /// <summary>
    /// Soft memory watchdog for the process.
    ///
    /// Configured through the [Startup] MemoryLimit setting (e.g. "2GB",
    /// "1536MB", "2147483648" or "unlimited"/"0" to disable).  When the
    /// process working set exceeds the limit the watchdog forces a full
    /// garbage collection and logs a warning.  It never terminates the
    /// process - a hard, OS level cap (Windows Job Object / Linux RLIMIT_AS)
    /// is enforced by the supervising manager/scheduler that spawns Robust
    /// and OpenSim.
    /// </summary>
    public static class MemoryLimiter
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Default soft cap for a single Robust/OpenSim process.</summary>
        public const long DefaultLimitBytes = 2L * 1024 * 1024 * 1024;

        private const int CheckIntervalMs = 10000;
        private const int MaxConsecutiveBreaches = 3;

        private static long m_limitBytes;
        private static volatile bool m_running;
        private static int m_consecutiveBreaches;
        private static Thread m_thread;

        /// <summary>
        /// Current configured limit in bytes (0 = unlimited).
        /// </summary>
        public static long MemoryLimitBytes
        {
            get { return m_limitBytes; }
        }

        /// <summary>
        /// Reads the [Startup] MemoryLimit setting and starts the watchdog
        /// thread.  Safe to call more than once (does nothing if already running).
        /// </summary>
        public static void Start(IConfigSource config)
        {
            if (m_running)
                return;

            long limit = DefaultLimitBytes;
            IConfig startupConfig = config.Configs["Startup"];
            if (startupConfig != null)
            {
                string raw = startupConfig.GetString("MemoryLimit", string.Empty);
                m_log.InfoFormat("[MEMORY LIMITER]: MemoryLimit = {0}", string.IsNullOrEmpty(raw) ? "(default)" : raw);
                limit = ParseSizeString(raw, DefaultLimitBytes);
            }
            else
            {
                m_log.InfoFormat("[MEMORY LIMITER]: No [Startup] section, using default limit");
            }

            StartWithLimit(limit);
        }

        /// <summary>
        /// Starts the watchdog with an explicit byte limit.  The process is
        /// never terminated here - only a forced GC and warnings are emitted.
        /// </summary>
        public static void StartWithLimit(long limitBytes)
        {
            if (m_running)
                return;

            m_limitBytes = limitBytes;
            ValidateConfiguredLimit();

            if (m_limitBytes == 0)
            {
                m_log.Info("[MEMORY LIMITER]: Limit disabled, watchdog not started");
                return;
            }

            m_running = true;
            m_consecutiveBreaches = 0;
            m_thread = new Thread(Loop);
            m_thread.IsBackground = true;
            m_thread.Name = "MemoryLimiter";
            m_thread.Start();
        }

        /// <summary>
        /// Stops the enforcement thread (e.g. during clean shutdown).
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

            // The process is already using more than the configured cap.  Raising the
            // cap would allow the leak to continue, so log it but respect what was asked.
            long current = Process.GetCurrentProcess().WorkingSet64;
            if (m_limitBytes != 0 && current > m_limitBytes)
            {
                m_log.WarnFormat(
                    "[MEMORY LIMITER]: Current working set ({0}) already exceeds configured limit ({1})",
                    FormatBytes(current), FormatBytes(m_limitBytes));
            }
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

            // Force a full, blocking collection so the heap is returned where possible.
            long before = GC.GetTotalMemory(false);
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            long after = GC.GetTotalMemory(true);

            long postGcWorkingSet = process.WorkingSet64;

            // This is only a soft limiter.  It never kills the process - a hard OS
            // level cap (Job Object / RLIMIT_AS) is imposed by the supervisor that
            // spawned us.  Here we simply reclaim heap and raise visibility.
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
        /// or a plain byte count.  Empty/invalid input returns the fallback.
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
    }
}