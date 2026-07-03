#nullable enable

using System;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using TasiaAddons.Abstractions;

namespace TasiaAddon.MACAudit;

public class MacAuditRegionModule : ISharedRegionModule, ITasiaAddonsAuditService, ITasiaAddonsFeature
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MacAuditRegionModule));

    private readonly object m_sync = new();
    private MacAuditSettings? m_settings;
    private MacAuditWriter? m_writer;
    private bool m_enabled;
    public string Name => "TasiaAddon.MACAudit.RegionModule";

    public Type ReplaceableInterface => null!;

    public void Initialise(IConfigSource source)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));

        m_settings = MacAuditSettings.FromConfig(source);
        m_enabled = m_settings.Enable;

        if (!m_enabled)
        {
            Log.Info("[NGC.MACAUDIT]: Region audit module disabled by configuration");
            return;
        }

        m_writer = new MacAuditWriter(m_settings);
        Log.Info("[NGC.MACAUDIT]: Region audit module initialised");
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
    }

    public void AddRegion(Scene scene)
    {
        if (!m_enabled || scene is null)
            return;

        scene.RegisterModuleInterface<ITasiaAddonsAuditService>(this);
        scene.AddRegionModule(Name, this);
    }

    public void RemoveRegion(Scene scene)
    {
        if (!m_enabled || scene is null)
            return;

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
}
