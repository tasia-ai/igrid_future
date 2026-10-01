using System.Reflection;
using OpenSim.Framework;

namespace TasiaAddons.RemoteSound;

/// <summary>
/// Registers RemoteSoundModule as a region module.
///
/// i-Grid declared this with Mono.Addins [Extension]/[assembly: Addin]
/// attributes. Tranquillity retired Mono.Addins outright - PluginManager is a
/// stub and repository management is gone - so those attributes still compile but
/// are never read, and the module would silently never be constructed.
/// Nothing in Tranquillity reads plugin.json either. This provider is the
/// mechanism that actually works; see Addons/Gloebit.GloebitMoneyModule for the
/// same pattern in a stock addon.
/// </summary>
public class PluginRegistration : IPluginRegistryProvider
{
    public void RegisterPlugins(PluginRegistry registry)
    {
        RegisterByName(registry, "/OpenSim/RegionModules", "RemoteSoundModule", "TasiaAddons.RemoteSound.RemoteSoundModule", "RemoteSoundModule", "0.9");
    }

    private static void RegisterByName(PluginRegistry registry, string extensionPath, string id, string typeName, string displayName, string version)
    {
        Assembly assembly = typeof(PluginRegistration).Assembly;
        Type type = assembly.GetType(typeName, false);
        if (type == null)
            return;

        registry.Register(
            extensionPath,
            new PluginDescriptor(id, type, displayName, version));
    }
}
