using OpenMetaverse;

namespace TasiaAddons.Abstractions;

public interface IRemoteSoundModule
{
    string PlaySoundUrl(UUID hostId, UUID scriptId, string url, double volume, UUID target, double cacheOverride);
}
