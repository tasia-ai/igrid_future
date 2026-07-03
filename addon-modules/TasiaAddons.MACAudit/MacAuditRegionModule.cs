#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using TasiaAddons.Abstractions;

[assembly: Addin("TasiaAddons.MACAudit", "1.0.0")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace TasiaAddons.MACAudit;

[Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "MacAuditRegionModule")]
public class MacAuditRegionModule : ISharedRegionModule, ITasiaAddonsAuditService, ITasiaAddonsFeature
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MacAuditRegionModule));

    private readonly object m_sync = new();
    private readonly ConcurrentDictionary<UUID, DateTime> m_recordedHgSessions = new();
    private readonly ConcurrentDictionary<string, DateTime> m_welcomeDedup = new();
    private int m_pruneTick;
    private MacAuditSettings? m_settings;
    private MacAuditWriter? m_writer;
    private bool m_enabled;
    private bool m_welcomeEnabled;
    private string m_welcomeUrl = string.Empty;
    private int m_welcomeTimeoutMs = 1500;
    private string m_welcomeFallback = "Welcome to I-Grid, <USERNAME>!";
    public string Name => "TasiaAddon.MACAudit.RegionModule";

    public Type ReplaceableInterface => null!;

    public void Initialise(IConfigSource source)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));

        m_settings = MacAuditSettings.FromConfig(source);
        ConfigureWelcome(source);
        m_enabled = m_settings.Enable;

        bool active = m_enabled || m_welcomeEnabled;

        if (!active)
        {
            Log.Info("[NGC.MACAUDIT]: Region module disabled by configuration");
            return;
        }

        if (m_enabled)
            m_writer = new MacAuditWriter(m_settings);

        Log.InfoFormat("[NGC.MACAUDIT]: Region module initialised (audit={0}, welcome={1})", m_enabled, m_welcomeEnabled);
    }

    public void PostInitialise()
    {
    }

    public void Close()
    {
        lock (m_sync)
        {
            m_writer = null;
        }
        m_recordedHgSessions.Clear();
        m_welcomeDedup.Clear();
    }

    public void AddRegion(Scene scene)
    {
        if (scene is null)
            return;

        if (!m_enabled && !m_welcomeEnabled)
            return;

        scene.RegisterModuleInterface<ITasiaAddonsAuditService>(this);
        scene.AddRegionModule(Name, this);
        scene.EventManager.OnMakeRootAgent += OnMakeRootAgent;
    }

    public void RemoveRegion(Scene scene)
    {
        if (scene is null)
            return;

        if (!m_enabled && !m_welcomeEnabled)
            return;

        scene.EventManager.OnMakeRootAgent -= OnMakeRootAgent;
        scene.UnregisterModuleInterface<ITasiaAddonsAuditService>(this);
    }

    public void RegionLoaded(Scene scene)
    {
    }

    public void Configure(ITasiaAddonsContext context)
    {
        _ = context ?? throw new ArgumentNullException(nameof(context));
    }

    public void RecordEvent(
        string component,
        string action,
        UUID? agentId,
        string? agentName,
        string? details = null,
        ulong? regionHandle = null,
        string? regionName = null)
    {
        if (!m_enabled)
            return;

        MacAuditWriter? writer;
        lock (m_sync)
        {
            writer = m_writer;
        }

        if (writer is null)
            return;

        MacAuditRecord record = new()
        {
            Timestamp = DateTime.UtcNow,
            UserId = agentId,
            Username = agentName ?? string.Empty,
            RegionHandle = regionHandle,
            RegionCoordinates = regionName,
            Viewer = component,
            ViewerChannel = action,
            ViewerVersion = details
        };

        try
        {
            writer.Write(record);
        }
        catch (Exception ex)
        {
            Log.Error("[NGC.MACAUDIT]: Failed to write audit record", ex);
        }
    }

    private void OnMakeRootAgent(ScenePresence presence)
    {
        if (presence is null || presence.IsChildAgent || presence.IsNPC)
            return;

        if (!m_enabled && !m_welcomeEnabled)
            return;

        Scene scene = presence.Scene;
        if (scene is null)
            return;

        IUserManagement userManagement = scene.UserManagementModule;
        if (userManagement is null)
            return;

        if (userManagement.IsLocalGridUser(presence.UUID))
        {
            SendDynamicWelcome(presence, scene);
            return;
        }

        MacAuditWriter? writer = null;
        if (m_enabled)
        {
            lock (m_sync)
            {
                writer = m_writer;
            }
        }

        string? homeUri = userManagement.GetUserHomeURL(presence.UUID);
        IClientAPI client = presence.ControllingClient;
        UUID sessionId = client?.SessionId ?? UUID.Zero;
        if (sessionId != UUID.Zero && !m_recordedHgSessions.TryAdd(sessionId, DateTime.UtcNow))
            return;

        MaybePruneCaches();

        MacAuditRecord record = new()
        {
            Timestamp = DateTime.UtcNow,
            UserId = presence.UUID,
            Username = presence.Name ?? string.Empty,
            SessionId = client?.SessionId,
            SecureSessionId = client?.SecureSessionId,
            RegionHandle = scene.RegionInfo.RegionHandle,
            RegionCoordinates = scene.RegionInfo.RegionName,
            SimAddress = scene.RegionInfo.ExternalHostName,
            SimPort = (uint)scene.RegionInfo.HttpPort,
            Viewer = "hypergrid",
            ViewerChannel = "arrival",
            ViewerVersion = string.IsNullOrWhiteSpace(homeUri) ? "unknown-home" : homeUri,
            IpAddress = m_settings?.IncludeIp == true && client?.RemoteEndPoint != null
                ? client.RemoteEndPoint.Address.ToString()
                : null
        };

        if (writer is not null)
        {
            try
            {
                writer.Write(record);
                Log.InfoFormat("[NGC.MACAUDIT]: Recorded hypergrid arrival for {0} in {1}", record.Username, scene.RegionInfo.RegionName);
            }
            catch (Exception ex)
            {
                Log.Error("[NGC.MACAUDIT]: Failed to record hypergrid arrival", ex);
            }
        }

        SendDynamicWelcome(presence, scene);
    }

    private void ConfigureWelcome(IConfigSource source)
    {
        IConfig? cfg = source.Configs["NGC.Welcome"];
        if (cfg is null)
            return;

        m_welcomeEnabled = cfg.GetBoolean("Enable", false);
        m_welcomeUrl = cfg.GetString("MessageUrl", string.Empty) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(m_welcomeUrl))
            m_welcomeUrl = cfg.GetString("MessageURL", string.Empty) ?? string.Empty;

        m_welcomeTimeoutMs = cfg.GetInt("TimeoutMs", 1500);
        if (m_welcomeTimeoutMs < 250)
            m_welcomeTimeoutMs = 250;
        if (m_welcomeTimeoutMs > 10000)
            m_welcomeTimeoutMs = 10000;

        string fallback = cfg.GetString("FallbackMessage", m_welcomeFallback) ?? m_welcomeFallback;
        if (!string.IsNullOrWhiteSpace(fallback))
            m_welcomeFallback = fallback;
    }

    private void SendDynamicWelcome(ScenePresence presence, Scene scene)
    {
        if (!m_welcomeEnabled || string.IsNullOrWhiteSpace(m_welcomeUrl))
            return;

        IClientAPI client = presence.ControllingClient;
        if (client is null)
            return;

        UUID sessionId = client.SessionId;
        if (sessionId == UUID.Zero)
            return;

        string dedupKey = string.Concat(sessionId.ToString(), ":", scene.RegionInfo.RegionHandle.ToString());
        DateTime now = DateTime.UtcNow;

        if (m_welcomeDedup.TryGetValue(dedupKey, out DateTime seenAt) && (now - seenAt).TotalSeconds < 3)
            return;
        m_welcomeDedup[dedupKey] = now;

        MaybePruneCaches();

        string username = presence.Name ?? string.Empty;
        string message = FetchWelcomeText();
        if (string.IsNullOrWhiteSpace(message))
            message = m_welcomeFallback;

        message = message.Replace("<USERNAME>", username).Replace("\\n", "\n");

        try
        {
            client.SendAgentAlertMessage(message, false);
        }
        catch (Exception ex)
        {
            Log.WarnFormat("[NGC.MACAUDIT]: Failed sending welcome message in {0}: {1}", scene.RegionInfo.RegionName, ex.Message);
        }
    }

    private string FetchWelcomeText()
    {
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(m_welcomeUrl);
            req.Timeout = m_welcomeTimeoutMs;
            req.ReadWriteTimeout = m_welcomeTimeoutMs;
            req.AllowAutoRedirect = true;
            req.UserAgent = "TasiaNGC-RegionWelcome";
            using HttpWebResponse resp = (HttpWebResponse)req.GetResponse();
            using Stream stream = resp.GetResponseStream();
            if (stream is null)
                return string.Empty;
            using StreamReader reader = new(stream);
            return reader.ReadToEnd().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private void MaybePruneCaches()
    {
        int tick = Interlocked.Increment(ref m_pruneTick);
        if ((tick & 0xFF) != 0)
            return;

        DateTime now = DateTime.UtcNow;

        foreach (KeyValuePair<UUID, DateTime> kv in m_recordedHgSessions)
        {
            if ((now - kv.Value).TotalHours > 24 || m_recordedHgSessions.Count > 200000)
                m_recordedHgSessions.TryRemove(kv.Key, out _);
        }

        foreach (KeyValuePair<string, DateTime> kv in m_welcomeDedup)
        {
            if ((now - kv.Value).TotalMinutes > 10 || m_welcomeDedup.Count > 50000)
                m_welcomeDedup.TryRemove(kv.Key, out _);
        }
    }
}
