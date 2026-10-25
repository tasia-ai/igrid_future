using System.Reflection;
using OpenSim.Framework;

namespace OpenSim.Region.OptionalModules.AI;

/// <summary>Registers the SmartNPC/OpenSimAI region module in Tranquillity's plugin registry.</summary>
public sealed class PluginRegistration : IPluginRegistryProvider
{
    public void RegisterPlugins(PluginRegistry registry)
    {
        var type = typeof(PluginRegistration).Assembly.GetType(
            "OpenSim.Region.OptionalModules.AI.OpenSimAIModule", throwOnError: false);
        if (type != null)
            registry.Register("/OpenSim/RegionModules",
                new PluginDescriptor("OpenSimAIModule", type, "OpenSim AI Module", "1.0"));
    }
}
