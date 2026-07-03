using System;
using System.Collections.Generic;
using System.IO;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenSim;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Tasia.Extensions.Loader;
using Tasia.Extensions.SDK;

namespace Tasia.Extensions.Host.Sim;

[Extension(Path = "/OpenSim/Startup", NodeName = "Plugin", Id = "TasiaExtensionsHost")]
public class TasiaExtensionsHost : IApplicationPlugin
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(TasiaExtensionsHost));

    private OpenSimBase? _application;
    private ExtensionLoader? _loader;
    private readonly List<object> _simHooks = new();
    private string _extensionsPath = string.Empty;

    public string Name => "TasiaExtensionsHost";
    public string Version => "1.0.0";

    public void Initialise()
    {
    }

    public void Initialise(OpenSimBase openSim)
    {
        _application = openSim ?? throw new ArgumentNullException(nameof(openSim));
        
        IConfigSource config = openSim.ConfigSource.Source;
        IConfig? extConfig = config.Configs["TasiaExtensions"];
        
        if (extConfig != null && !extConfig.GetBoolean("Enabled", true))
        {
            Log.Info("[TASIA-HOST]: Extension host disabled in configuration");
            return;
        }

        _extensionsPath = extConfig?.GetString("ExtensionsPath", "bin/extensions") ?? "bin/extensions";
        
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        var fullPath = Path.Combine(basePath, _extensionsPath);
        
        if (!Directory.Exists(fullPath))
        {
            Log.Warn($"[TASIA-HOST]: Extensions directory not found: {fullPath}");
            return;
        }

        var logger = new SimExtensionLogger(Log);
        var context = new SimExtensionContext(fullPath, logger);
        
        _loader = new ExtensionLoader(fullPath, logger, context);
        _loader.ScanAndLoad(ExtensionTarget.Sim);
        _loader.StartAll();

        Log.Info($"[TASIA-HOST]: Loaded {_loader.LoadedExtensions.Count} extensions");
    }

    public void PostInitialise()
    {
        if (_application == null)
            return;

        var sceneManager = _application.SceneManager;
        if (sceneManager != null)
        {
            sceneManager.OnRegionsReadyStatusChange += OnRegionsReady;
            AttachToExistingScenes();
        }
    }

    private void OnRegionsReady(SceneManager mgr)
    {
        AttachToExistingScenes();
    }

    private void AttachToExistingScenes()
    {
        if (_application == null || _loader == null)
            return;

        foreach (var scene in _application.SceneManager.Scenes)
        {
            NotifyRegionAdded(scene);
        }
    }

    private void NotifyRegionAdded(Scene scene)
    {
        if (_loader == null)
            return;

        foreach (var ext in _loader.LoadedExtensions)
        {
            if (ext.Instance is ISimRegionHooks hooks)
            {
                try
                {
                    hooks.OnRegionAdded(scene);
                }
                catch (Exception ex)
                {
                    Log.Error($"[TASIA-HOST]: Error notifying extension {ext.Manifest.Name} of region add", ex);
                }
            }
        }

        foreach (var loadedScene in _application.SceneManager.Scenes)
        {
            NotifyRegionLoaded(loadedScene);
        }
    }

    private void NotifyRegionLoaded(Scene scene)
    {
        if (_loader == null)
            return;

        foreach (var ext in _loader.LoadedExtensions)
        {
            if (ext.Instance is ISimRegionHooks hooks)
            {
                try
                {
                    hooks.OnRegionLoaded(scene);
                }
                catch (Exception ex)
                {
                    Log.Error($"[TASIA-HOST]: Error notifying extension {ext.Manifest.Name} of region load", ex);
                }
            }
        }
    }

    public void Close()
    {
        Stop();
    }

    public void Dispose()
    {
        Stop();
    }

    private void Stop()
    {
        if (_loader != null)
        {
            _loader.StopAll();
            _loader = null;
        }
    }
}

internal class SimExtensionLogger : IExtensionLogger
{
    private readonly ILog _log;

    public SimExtensionLogger(ILog log) => _log = log;

    public void Debug(string message) => _log.Debug(message);
    public void Info(string message) => _log.Info(message);
    public void Warn(string message) => _log.Warn(message);
    public void Error(string message, Exception? ex = null)
    {
        if (ex != null)
            _log.Error(message, ex);
        else
            _log.Error(message);
    }
}

internal class SimExtensionContext : IExtensionContext
{
    private readonly string _basePath;
    private readonly IExtensionLogger _logger;

    public string BasePath => _basePath;
    public IExtensionLogger Logger => _logger;

    public SimExtensionContext(string basePath, IExtensionLogger logger)
    {
        _basePath = basePath;
        _logger = logger;
    }

    public object? GetService(Type serviceType)
    {
        return null;
    }

    public T? GetService<T>() where T : class
    {
        return null;
    }
}
