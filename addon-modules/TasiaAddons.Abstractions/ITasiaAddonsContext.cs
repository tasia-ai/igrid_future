using Nini.Config;
using OpenMetaverse;

namespace TasiaAddons.Abstractions;

public interface ITasiaAddonsContext
{
    ITasiaAddonsAuditService AuditService { get; }
    IConfigSource ConfigSource { get; }
}

public interface ITasiaAddonsFeature
{
    void Configure(ITasiaAddonsContext context);
}

public interface ITasiaAddonsAuditService
{
    void RecordEvent(
        string component,
        string action,
        UUID? agentId,
        string? agentName,
        string? details = null,
        ulong? regionHandle = null,
        string? regionName = null);
}

public interface IRemoteSoundModule
{
    string PlaySoundUrl(UUID hostId, UUID scriptId, string url, double volume, UUID target, double cacheOverride);
}

public class PluginNotInitialisedException : Exception
{
    public PluginNotInitialisedException(string name) : base($"Plugin {name} was not initialized. Call Initialise() first.") { }
}
