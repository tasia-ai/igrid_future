using System;
using System.Collections.Generic;
using Nini.Config;
using OpenSim.Region.Framework.Scenes;
using Tasia.Extensions.SDK;

namespace Tasia.Extensions.Compat.OpenSimFork;

public static class OpenSimForkAdapters
{
    public static Scene GetSceneFromObject(object sceneLike)
    {
        if (sceneLike is Scene scene)
            return scene;
        
        throw new NotSupportedException("sceneLike is not a Scene");
    }

    public static Dictionary<UUID, Scene> GetScenesFromManager(object sceneManagerLike)
    {
        throw new NotImplementedException("TODO: Adapt to exact OpenSim fork SceneManager type");
    }
}

public class OpenSimForkConfigHelper
{
    private readonly IConfigSource _config;

    public OpenSimForkConfigHelper(IConfigSource config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public T? GetConfigSection<T>(string sectionName) where T : class
    {
        var config = _config.Configs[sectionName];
        if (config == null)
            return null;
        
        return null;
    }

    public bool GetBool(string section, string key, bool defaultValue)
    {
        var config = _config.Configs[section];
        return config?.GetBoolean(key, defaultValue) ?? defaultValue;
    }

    public string GetString(string section, string key, string defaultValue)
    {
        var config = _config.Configs[section];
        return config?.GetString(key, defaultValue) ?? defaultValue;
    }
}

public static class ForkSpecificRegistry
{
    public static void RegisterRegionModule(Scene scene, string moduleName, object module)
    {
    }

    public static void UnregisterRegionModule(Scene scene, string moduleName)
    {
    }

    public static T? GetRegionModule<T>(Scene scene) where T : class
    {
        return null;
    }
}
