using OpenSim.Framework;

namespace TasiaAddons.InternalScriptRpc;

public sealed class PluginRegistration : IPluginRegistryProvider
{
    public void RegisterPlugins(PluginRegistry registry) => registry.Register(
        "/OpenSim/RegionModules",
        new PluginDescriptor("InternalScriptRpcModule", typeof(InternalScriptRpcModule), "Authenticated internal script RPC", "1.0"));
}
