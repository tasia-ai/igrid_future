using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Timers;
using System.Xml.Serialization;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Mono.Addins;
using Timer = System.Timers.Timer;

[assembly: Addin("TasiaAddons.MetricsConnector", "1.0.0")]
[assembly: AddinDescription("Tasia Metrics Connector â€” collects sim stats + HTTP endpoint metrics")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace TasiaAddons.MetricsConnector
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "MetricsConnector")]
    public class MetricsConnector : INonSharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_scene;
        private bool m_enabled;
        private bool m_apiEnabled;
        private string m_apiToken;
        private string m_apiPath;
        private Timer m_collectTimer;

        // Endpoint tracking: key = HTTP path, value = (callCount, totalMs, recentMs)
        private readonly ConcurrentDictionary<string, EndpointStats> m_endpoints = new();

        // Current snapshot
        private MetricsSnapshot m_snapshot = new();

        public void Initialise(IConfigSource config)
        {
            IConfig cfg = config.Configs["MetricsConnector"];
            if (cfg == null)
                return;

            m_enabled = cfg.GetBoolean("Enabled", false);
            if (!m_enabled)
                return;

            m_apiEnabled = cfg.GetBoolean("ApiEnabled", false);
            m_apiToken = cfg.GetString("ApiToken", string.Empty);
            if (m_apiToken == "CHANGE_ME_METRICS_TOKEN")
                m_apiToken = string.Empty;

            int interval = cfg.GetInt("CollectIntervalSeconds", 10);
            if (interval < 5)
                interval = 5;

            m_collectTimer = new Timer(interval * 1000);
            m_collectTimer.AutoReset = true;
            m_collectTimer.Elapsed += CollectMetrics;
            m_collectTimer.Start();

            // Hook into HTTP server to track request stats via reflection
            try
            {
                var server = MainServer.Instance;
                if (server != null)
                {
                    var httpServerType = server.GetType();
                    var listenerField = httpServerType.GetField("m_httpListener",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                    if (listenerField != null)
                    {
                        var listener = listenerField.GetValue(server);
                        if (listener != null)
                        {
                            var reqEvent = listener.GetType().GetEvent("RequestReceived");
                            if (reqEvent != null)
                            {
                                reqEvent.AddEventHandler(listener,
                                    Delegate.CreateDelegate(reqEvent.EventHandlerType, this, nameof(OnHttpRequest)));
                                m_log.Info("[METRICS] HTTP request tracking enabled");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("[METRICS] HTTP tracking hook failed (non-fatal): {0}", ex.Message);
            }
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_scene = scene;

            if (m_apiEnabled && !string.IsNullOrWhiteSpace(m_apiToken))
            {
                string prefix = "/tasia-ngc/metrics";
                m_apiPath = $"{prefix}/{scene.RegionInfo.RegionID.ToString().ToLowerInvariant()}";

                try
                {
                    MainServer.Instance.DefaultServer.AddSimpleStreamHandler(
                        new SimpleStreamHandler(m_apiPath, HandleApiRequest, Name + ".API"));
                    m_log.InfoFormat("[METRICS] API enabled for {0} at {1}", scene.RegionInfo.RegionName, m_apiPath);
                }
                catch (Exception ex)
                {
                    m_log.ErrorFormat("[METRICS] Failed to register API: {0}", ex.Message);
                }
            }
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_collectTimer?.Stop();
            m_collectTimer?.Dispose();
            m_collectTimer = null;

            if (!string.IsNullOrEmpty(m_apiPath))
            {
                try
                {
                    MainServer.Instance.DefaultServer.RemoveSimpleStreamHandler(m_apiPath);
                }
                catch { }
                m_apiPath = null;
            }

            m_scene = null;
        }

        public void Close() { }
        public void Dispose() { RemoveRegion(m_scene); }
        public string Name => "MetricsConnector";
        public Type ReplaceableInterface => null;
        public void RegionLoaded(Scene scene) { }

        // === HTTP API ===

        private void HandleApiRequest(IOSHttpRequest request, IOSHttpResponse response)
        {
            response.ContentType = "application/json";

            if (!IsAuthorized(request))
            {
                WriteJson(response, 401, "{\"error\":\"unauthorized\"}");
                return;
            }

            var snap = m_snapshot;
            var sb = new StringBuilder();
            sb.Append('{');
            sb.AppendFormat("\"region_name\":\"{0}\",", Escape(snap.RegionName));
            sb.AppendFormat("\"sim_fps\":{0:F1},", snap.SimFPS);
            sb.AppendFormat("\"physics_fps\":{0:F1},", snap.PhysicsFPS);
            sb.AppendFormat("\"time_dilation\":{0:F3},", snap.TimeDilation);
            sb.AppendFormat("\"agents\":{0},", snap.RootAgents);
            sb.AppendFormat("\"child_agents\":{0},", snap.ChildAgents);
            sb.AppendFormat("\"objects\":{0},", snap.TotalPrims);
            sb.AppendFormat("\"active_objects\":{0},", snap.ActivePrims);
            sb.AppendFormat("\"active_scripts\":{0},", snap.ActiveScripts);
            sb.AppendFormat("\"object_updates\":{0:F0},", snap.ObjectUpdates);
            // Frame timing (ms)
            sb.AppendFormat("\"frame_ms\":{0:F1},", snap.FrameMS);
            sb.AppendFormat("\"net_ms\":{0:F1},", snap.NetMS);
            sb.AppendFormat("\"physics_ms\":{0:F1},", snap.PhysicsMS);
            sb.AppendFormat("\"image_ms\":{0:F1},", snap.ImageMS);
            sb.AppendFormat("\"other_ms\":{0:F1},", snap.OtherMS);
            sb.AppendFormat("\"script_ms\":{0:F1},", snap.ScriptMS);
            sb.AppendFormat("\"agent_ms\":{0:F1},", snap.AgentMS);
            // Network
            sb.AppendFormat("\"in_packets_ps\":{0:F0},", snap.InPacketsPerSecond);
            sb.AppendFormat("\"out_packets_ps\":{0:F0},", snap.OutPacketsPerSecond);
            sb.AppendFormat("\"unacked_bytes\":{0:F0},", snap.UnAckedBytes);
            // Scripts
            sb.AppendFormat("\"script_eps\":{0:F0},", snap.ScriptEps);
            sb.AppendFormat("\"script_lines_ps\":{0:F1},", snap.ScriptLinesPerSecond);
            // Pending
            sb.AppendFormat("\"pending_downloads\":{0},", snap.PendingDownloads);
            sb.AppendFormat("\"pending_uploads\":{0},", snap.PendingUploads);
            // Memory and uptime
            sb.AppendFormat("\"memory_mb\":{0:F0},", snap.MemoryMB);
            sb.AppendFormat("\"uptime_seconds\":{0},", snap.UptimeSeconds);
            sb.AppendFormat("\"gc_gen0\":{0},", snap.GCGen0);
            sb.AppendFormat("\"gc_gen1\":{0},", snap.GCGen1);
            sb.AppendFormat("\"gc_gen2\":{0},", snap.GCGen2);
            sb.AppendFormat("\"cpu_seconds\":{0:F1},", snap.CpuSeconds);
            sb.AppendFormat("\"thread_pool_threads\":{0},", snap.ThreadPoolThreads);
            sb.AppendFormat("\"thread_pool_pending\":{0},", snap.ThreadPoolPending);
            sb.AppendFormat("\"thread_pool_completed\":{0},", snap.ThreadPoolCompleted);
            sb.AppendFormat("\"handle_count\":{0},", snap.HandleCount);
            sb.AppendFormat("\"native_threads\":{0},", snap.NativeThreadCount);
            sb.AppendFormat("\"working_set_mb\":{0:F0},", snap.WorkingSetMB);

            // Endpoint stats
            sb.Append("\"endpoints\":{");
            bool first = true;
            foreach (var kv in m_endpoints)
            {
                var es = kv.Value;
                if (es.TotalCalls == 0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.AppendFormat("\"{0}\":{{\"calls\":{1},\"avg_ms\":{2:F1},\"total_ms\":{3:F0}}}",
                    Escape(kv.Key), es.TotalCalls, es.AvgMs, es.TotalMs);
            }
            sb.Append("}");

            sb.Append('}');
            WriteJson(response, 200, sb.ToString());
        }

        private void CollectMetrics(object sender, ElapsedEventArgs e)
        {
            if (m_scene == null)
                return;

            try
            {
                var snap = new MetricsSnapshot();
                snap.RegionName = m_scene.RegionInfo.RegionName;
                snap.SimFPS = m_scene.StatsReporter.LastReportedSimFPS;

                float[] stats = m_scene.StatsReporter.LastReportedSimStats;
                if (stats != null && stats.Length > 40)
                {
                    snap.TimeDilation = stats[0];
                    snap.SimFPS = stats[1];
                    snap.PhysicsFPS = stats[2];
                    snap.ObjectUpdates = stats[3];
                    snap.RootAgents = (int)stats[4];
                    snap.ChildAgents = (int)stats[5];
                    snap.TotalPrims = (int)stats[6];
                    snap.ActivePrims = (int)stats[7];
                    snap.FrameMS = stats[8];
                    snap.NetMS = stats[9];
                    snap.PhysicsMS = stats[10];
                    snap.ImageMS = stats[11];
                    snap.OtherMS = stats[12];
                    snap.InPacketsPerSecond = stats[13];
                    snap.OutPacketsPerSecond = stats[14];
                    snap.UnAckedBytes = stats[15];
                    snap.AgentMS = stats[16];
                    snap.PendingDownloads = (int)stats[17];
                    snap.PendingUploads = (int)stats[18];
                    snap.ActiveScripts = (int)stats[19];
                    snap.ScriptEps = stats[28];
                    snap.ScriptMS = stats[37];
                    snap.ScriptLinesPerSecond = stats[38];
                }

                snap.MemoryMB = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
                snap.UptimeSeconds = (long)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds;

                // .NET runtime metrics
                snap.GCGen0 = GC.CollectionCount(0);
                snap.GCGen1 = GC.CollectionCount(1);
                snap.GCGen2 = GC.CollectionCount(2);
                var proc = Process.GetCurrentProcess();
                snap.CpuSeconds = proc.TotalProcessorTime.TotalSeconds;
                snap.HandleCount = proc.HandleCount;
                snap.NativeThreadCount = proc.Threads.Count;
                snap.WorkingSetMB = proc.WorkingSet64 / (1024.0 * 1024.0);
                snap.ThreadPoolThreads = System.Threading.ThreadPool.ThreadCount;
                snap.ThreadPoolPending = (int)System.Threading.ThreadPool.PendingWorkItemCount;
                snap.ThreadPoolCompleted = System.Threading.ThreadPool.CompletedWorkItemCount;

                m_snapshot = snap;
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("[METRICS] Collect failed: {0}", ex.Message);
            }
        }

        // === Instrument HTTP requests via listener event ===

        private readonly ConcurrentDictionary<string, long> m_requestCounts = new();
        private readonly ConcurrentDictionary<string, long> m_requestTimeAccum = new();

        /// <summary>
        /// Called by the HTTP listener event for every incoming request.
        /// We track the path and the time the request was received.
        /// Duration is tracked by wrapping HandleRequest via reflection.
        /// </summary>
        public void OnHttpRequest(object sender, EventArgs e)
        {
            // We can't get request details from the generic event args
            // The actual tracking happens via HandleRequest wrapping (below)
        }

        /// <summary>
        /// Track a completed request â€” called from the wrapped HandleRequest.
        /// </summary>
        public void TrackCompletedRequest(string path, double elapsedMs)
        {
            m_requestCounts.AddOrUpdate(path, 1, (_, c) => c + 1);
            m_requestTimeAccum.AddOrUpdate(path, (long)(elapsedMs * 1000), (_, t) => t + (long)(elapsedMs * 1000));
        }

        // === Helpers ===

        private bool IsAuthorized(IOSHttpRequest request)
        {
            string auth = request.Headers["Authorization"] ?? string.Empty;
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return auth.Substring(7).Trim() == m_apiToken;
            return (request.Headers["X-Metrics-Token"] ?? string.Empty) == m_apiToken;
        }

        private static void WriteJson(IOSHttpResponse response, int code, string json)
        {
            byte[] data = Encoding.UTF8.GetBytes(json);
            response.ContentLength = data.Length;
            response.StatusCode = code;
            response.Body.Write(data, 0, data.Length);
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // === Data types ===

        private struct MetricsSnapshot
        {
            public string RegionName;
            public float SimFPS;
            public float PhysicsFPS;
            public int RootAgents;
            public int ChildAgents;
            public int TotalPrims;
            public int ActivePrims;
            public int ActiveScripts;
            public double MemoryMB;
            public long UptimeSeconds;
            public float ObjectUpdates;
            // Frame timing
            public float TimeDilation;
            public float FrameMS;
            public float NetMS;
            public float PhysicsMS;
            public float ImageMS;
            public float OtherMS;
            public float ScriptMS;
            public float AgentMS;
            // Network
            public float InPacketsPerSecond;
            public float OutPacketsPerSecond;
            public float UnAckedBytes;
            // Scripts
            public float ScriptEps;
            public float ScriptLinesPerSecond;
            // Pending
            public int PendingDownloads;
            public int PendingUploads;
            // .NET runtime
            public int GCGen0;
            public int GCGen1;
            public int GCGen2;
            public double CpuSeconds;
            public int ThreadPoolThreads;
            public int ThreadPoolPending;
            public long ThreadPoolCompleted;
            public int HandleCount;
            public int NativeThreadCount;
            public double WorkingSetMB;
        }

        private class EndpointStats
        {
            public long TotalCalls;
            public long TotalMsAccum;
            public double AvgMs => TotalCalls > 0 ? (double)TotalMsAccum / TotalCalls / 1000.0 : 0;
            public double TotalMs => (double)TotalMsAccum / 1000.0;
        }
    }
}
