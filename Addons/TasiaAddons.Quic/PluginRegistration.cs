using OpenSim.Framework;

namespace TasiaAddons.Quic;

/// <summary>
/// Registers QuicServerModule as a region module.
///
/// Tranquillity does not load region modules from Mono.Addins [Extension]
/// attributes; the [Extension] that i-Grid relied on would compile here and
/// then silently never fire. This provider is the Tranquillity mechanism - the
/// same one Addons/Gloebit.GloebitMoneyModule uses.
///
/// Note the member name is RegisterPlugins, not Register: that is what
/// IPluginRegistryProvider actually declares.
/// </summary>
public class PluginRegistration : IPluginRegistryProvider
{
    public void RegisterPlugins(PluginRegistry registry)
    {
        registry.Register("/OpenSim/RegionModules",
            new PluginDescriptor("QuicServerModule", typeof(QuicServerModule),
                "QuicServerModule", "0.9"));
    }
}
