using OpenSim.Framework;

namespace TasiaAddons.GridFunctions;

public sealed class PluginRegistration : IPluginRegistryProvider
{
    public void RegisterPlugins(PluginRegistry registry) => registry.Register(
        "/OpenSim/RegionModules",
        new PluginDescriptor("TasiaGridFunctionsModule", typeof(TasiaGridFunctionsModule), "Tasia grid script functions", "1.0"));
}
