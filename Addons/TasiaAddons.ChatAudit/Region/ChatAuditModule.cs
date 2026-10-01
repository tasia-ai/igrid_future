using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Net;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using TasiaAddons.Abstractions;
using Mono.Addins;

[assembly: Addin("TasiaAddons.ChatAudit", "1.0.0")]
[assembly: AddinDescription("Chat audit region module")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace TasiaAddons.ChatAudit
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "ChatAuditModule")]
    public class ChatAuditModule : ISharedRegionModule, ITasiaAddonsFeature
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(ChatAuditModule));

        private readonly ConcurrentQueue<ChatAuditRecord> m_recentRecords = new();
        private readonly object m_fileLock = new();
        private readonly JsonSerializerOptions m_jsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

        private readonly List<Scene> m_scenes = new();
        private readonly Dictionary<string, ulong> m_regionHandles = new(StringComparer.OrdinalIgnoreCase);
        private bool m_enabled;
        private string m_logFilePath = Path.Combine("Data", "chat-audit.log");
        private string m_apiPath = "/addons/chat-audit";
        private string m_apiToken = string.Empty;
        private int m_recentLimit = 500;
        private bool m_httpHandlerRegistered;
        private ITasiaAddonsContext? m_context;

        public void Configure(ITasiaAddonsContext context)
        {
            m_context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public void Initialise(IConfigSource config)
        {
            IConfig auditConfig = config.Configs["ChatAudit"];
            if (auditConfig == null)
            {
                Log.Info("[CHAT-AUDIT]: No ChatAudit section in configuration; module disabled.");
                m_enabled = false;
                return;
            }

            m_enabled = auditConfig.GetBoolean("Enabled", false);
            if (!m_enabled)
            {
                Log.Info("[CHAT-AUDIT]: Module disabled via configuration.");
                return;
            }

            m_logFilePath = auditConfig.GetString("LogFile", m_logFilePath);
            m_apiPath = auditConfig.GetString("ApiPath", m_apiPath);
            m_apiToken = auditConfig.GetString("ApiToken", m_apiToken);
            m_recentLimit = Math.Max(10, auditConfig.GetInt("RecentEntryLimit", m_recentLimit));

            try
            {
                string? directory = Path.GetDirectoryName(Path.GetFullPath(m_logFilePath));
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[CHAT-AUDIT]: Failed to prepare log directory.", ex);
                m_enabled = false;
                return;
            }

            LoadExistingLog();

            MainServer.Instance.DefaultServer.AddHTTPHandler(m_apiPath, HandleHttpRequest);
            m_httpHandlerRegistered = true;

            Log.InfoFormat("[CHAT-AUDIT]: Enabled. Recent buffer size {0}. API path {1}.", m_recentLimit, m_apiPath);
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            lock (m_scenes)
            {
                if (m_scenes.Contains(scene))
                    return;

                m_scenes.Add(scene);
                m_regionHandles[scene.RegionInfo.RegionName] = scene.RegionInfo.RegionHandle;
            }

            scene.EventManager.OnChatFromClient += OnChatFromClient;
            scene.EventManager.OnChatFromWorld += OnChatFromWorld;
            scene.EventManager.OnIncomingInstantMessage += OnIncomingInstantMessage;
            scene.EventManager.OnUnhandledInstantMessage += OnIncomingInstantMessage;
            scene.EventManager.OnNewClient += OnNewClient;
            scene.EventManager.OnClientClosed += OnClientClosed;

            Log.InfoFormat("[CHAT-AUDIT]: Region {0} subscribed.", scene.RegionInfo.RegionName);
        }

        public void RegionLoaded(Scene scene) { }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            scene.EventManager.OnChatFromClient -= OnChatFromClient;
            scene.EventManager.OnChatFromWorld -= OnChatFromWorld;
            scene.EventManager.OnIncomingInstantMessage -= OnIncomingInstantMessage;
            scene.EventManager.OnUnhandledInstantMessage -= OnIncomingInstantMessage;
            scene.EventManager.OnNewClient -= OnNewClient;
            scene.EventManager.OnClientClosed -= OnClientClosed;

            lock (m_scenes)
            {
                m_scenes.Remove(scene);
                m_regionHandles.Remove(scene.RegionInfo.RegionName);
            }
        }

        public void Close()
        {
            if (m_httpHandlerRegistered)
            {
                MainServer.Instance.DefaultServer.RemoveHTTPHandler("GET", m_apiPath);
                m_httpHandlerRegistered = false;
            }

            lock (m_scenes)
            {
                m_scenes.Clear();
            }
        }

        public void PostInitialise() { }

        public string Name => "ChatAuditModule";

        public Type ReplaceableInterface => null;

        private void OnNewClient(IClientAPI client)
        {
            if (client == null)
                return;

            client.OnInstantMessage += OnViewerInstantMessage;
        }

        private void OnClientClosed(UUID agentId, Scene scene)
        {
            if (scene != null && scene.TryGetClient(agentId, out IClientAPI client))
            {
                client.OnInstantMessage -= OnViewerInstantMessage;
            }
        }

        private void OnViewerInstantMessage(IClientAPI client, GridInstantMessage msg)
        {
            string regionName = client?.Scene?.RegionInfo.RegionName ?? string.Empty;
            RecordInstantMessage("viewer", msg, regionName);
        }

        private void OnIncomingInstantMessage(GridInstantMessage msg)
        {
            RecordInstantMessage("incoming", msg, string.Empty);
        }

        private void OnChatFromWorld(object sender, OSChatMessage chat)
        {
            RecordChat("world", chat);
        }

        private void OnChatFromClient(object sender, OSChatMessage chat)
        {
            RecordChat("client", chat);
        }

        private void RecordChat(string source, OSChatMessage chat)
        {
            if (chat == null)
                return;

            ChatAuditRecord record = new()
            {
                Timestamp = DateTime.UtcNow,
                EventType = "chat",
                Source = source,
                Region = (chat.Scene as Scene)?.RegionInfo.RegionName ?? string.Empty,
                FromName = chat.From,
                FromAgent = chat.SenderUUID.ToString(),
                Target = chat.Destination.ToString(),
                Message = chat.Message,
                Channel = chat.Channel,
                ChatType = chat.Type.ToString(),
                Position = AuditVector.FromVector(chat.Position),
                Session = string.Empty,
                Dialog = string.Empty
            };

            AppendRecord(record);
        }

        private void RecordInstantMessage(string source, GridInstantMessage message, string regionName)
        {
            if (message == null)
                return;

            ChatAuditRecord record = new()
            {
                Timestamp = DateTime.UtcNow,
                EventType = "im",
                Source = source,
                Region = regionName,
                FromName = message.fromAgentName,
                FromAgent = new UUID(message.fromAgentID).ToString(),
                Target = new UUID(message.toAgentID).ToString(),
                Message = message.message,
                Session = message.imSessionID.ToString(),
                Dialog = ((InstantMessageDialog)message.dialog).ToString(),
                Position = AuditVector.FromVector(message.Position)
            };

            AppendRecord(record);
        }

        private void AppendRecord(ChatAuditRecord record)
        {
            m_recentRecords.Enqueue(record);
            while (m_recentRecords.Count > m_recentLimit && m_recentRecords.TryDequeue(out _))
            {
            }

            string line = JsonSerializer.Serialize(record, m_jsonOptions);

            lock (m_fileLock)
            {
                File.AppendAllText(m_logFilePath, line + Environment.NewLine, Encoding.UTF8);
            }

            PublishAuditEvent(record);
        }

        private void PublishAuditEvent(ChatAuditRecord record)
        {
            if (m_context?.AuditService is null)
                return;

            UUID? agentId = null;
            if (!string.IsNullOrEmpty(record.FromAgent) && UUID.TryParse(record.FromAgent, out UUID parsed))
                agentId = parsed;

            ulong? regionHandle = null;
            if (!string.IsNullOrEmpty(record.Region) && m_regionHandles.TryGetValue(record.Region, out ulong handle))
                regionHandle = handle;

            m_context.AuditService.RecordEvent(
                "ChatAudit",
                record.EventType ?? "chat",
                agentId,
                record.FromName,
                record.Message,
                regionHandle,
                record.Region);
        }

        private void LoadExistingLog()
        {
            if (!File.Exists(m_logFilePath))
                return;

            try
            {
                string[] lines;
                lock (m_fileLock)
                {
                    lines = File.ReadAllLines(m_logFilePath);
                }

                foreach (string line in lines.TakeLast(m_recentLimit))
                {
                    try
                    {
                        ChatAuditRecord? record = JsonSerializer.Deserialize<ChatAuditRecord>(line, m_jsonOptions);
                        if (record != null)
                            m_recentRecords.Enqueue(record);
                    }
                    catch (JsonException)
                    {
                        // Skip malformed entries
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[CHAT-AUDIT]: Failed to load existing audit log.", ex);
            }
        }

        private Hashtable HandleHttpRequest(Hashtable request)
        {
            Hashtable response = new();
            response["content_type"] = "application/json";
            response["keepalive"] = false;

            if (!m_enabled)
            {
                response["int_response_code"] = (int)HttpStatusCode.ServiceUnavailable;
                response["str_response_string"] = "{\"error\":\"disabled\"}";
                return response;
            }

            string providedToken = string.Empty;
            if (request.ContainsKey("token"))
            {
                providedToken = request["token"]?.ToString() ?? string.Empty;
            }

            if (string.IsNullOrEmpty(providedToken) && request.ContainsKey("headers") && request["headers"] is Hashtable headers)
            {
                providedToken = headers["X-Chat-Audit-Token"]?.ToString() ?? string.Empty;
            }

            if (!string.IsNullOrEmpty(m_apiToken)
                && !string.Equals(providedToken, m_apiToken, StringComparison.Ordinal))
            {
                response["int_response_code"] = (int)HttpStatusCode.Unauthorized;
                response["str_response_string"] = "{\"error\":\"unauthorized\"}";
                return response;
            }

            int limit = m_recentLimit;
            if (request.ContainsKey("limit"))
            {
                string? requestedValue = request["limit"]?.ToString();
                if (!string.IsNullOrEmpty(requestedValue)
                    && int.TryParse(requestedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int requested))
                {
                    limit = Math.Clamp(requested, 1, m_recentLimit);
                }
            }

            string? eventFilter = null;
            if (request.ContainsKey("type"))
            {
                string? typeValue = request["type"]?.ToString();
                if (!string.IsNullOrEmpty(typeValue))
                    eventFilter = typeValue.ToLowerInvariant();
            }

            IEnumerable<ChatAuditRecord> records = m_recentRecords.Reverse();
            if (!string.IsNullOrEmpty(eventFilter))
            {
                records = records.Where(r => r.EventType.Equals(eventFilter, StringComparison.OrdinalIgnoreCase));
            }

            ChatAuditEnvelope envelope = new()
            {
                Generated = DateTime.UtcNow,
                Count = 0,
                Records = new List<ChatAuditRecord>()
            };

            foreach (ChatAuditRecord record in records.Take(limit))
            {
                envelope.Records.Add(record);
            }

            envelope.Count = envelope.Records.Count;

            string json = JsonSerializer.Serialize(envelope, m_jsonOptions);
            response["int_response_code"] = (int)HttpStatusCode.OK;
            response["str_response_string"] = json;
            return response;
        }

        private record ChatAuditEnvelope
        {
            public DateTime Generated { get; init; }
            public int Count { get; set; }
            public List<ChatAuditRecord> Records { get; init; }
        }

        private record ChatAuditRecord
        {
            public DateTime Timestamp { get; init; }
            public string EventType { get; init; } = string.Empty;
            public string Source { get; init; } = string.Empty;
            public string Region { get; init; } = string.Empty;
            public string FromName { get; init; } = string.Empty;
            public string FromAgent { get; init; } = string.Empty;
            public string Target { get; init; } = string.Empty;
            public string Message { get; init; } = string.Empty;
            public string Session { get; init; } = string.Empty;
            public string Dialog { get; init; } = string.Empty;
            public int Channel { get; init; }
            public string ChatType { get; init; } = string.Empty;
            public AuditVector Position { get; init; } = AuditVector.Zero;
        }

        private readonly record struct AuditVector(float X, float Y, float Z)
        {
            public static AuditVector Zero { get; } = new(0f, 0f, 0f);

            public static AuditVector FromVector(Vector3 v) => new(v.X, v.Y, v.Z);
        }
    }
}
