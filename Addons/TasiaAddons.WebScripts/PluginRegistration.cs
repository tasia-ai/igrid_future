using OpenSim.Framework;

namespace TasiaAddons.WebScripts;

public sealed class PluginRegistration : IPluginRegistryProvider
{
    public void RegisterPlugins(PluginRegistry registry) => registry.Register("/OpenSim/RegionModules",
        new PluginDescriptor("WebScriptsModule", typeof(WebScriptsModule), "Signed web LSL scripts", "1.0"));
}
