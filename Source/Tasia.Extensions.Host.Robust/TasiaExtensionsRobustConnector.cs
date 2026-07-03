using System;
using System.IO;
using log4net;
using Nini.Config;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using Tasia.Extensions.Loader;
using Tasia.Extensions.SDK;

namespace Tasia.Extensions.Host.Robust;

public class TasiaExtensionsRobustConnector : ServiceConnector
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(TasiaExtensionsRobustConnector));
    
    private ExtensionLoader? _loader;

    public TasiaExtensionsRobustConnector(IConfigSource config, IHttpServer server, string configName)
        : base(config, server, configName)
    {
        string section = string.IsNullOrEmpty(configName) ? "TasiaExtensions" : configName;
        IConfig? extConfig = config.Configs[section];
        
        if (extConfig == null)
        {
            Log.Info("[TASIA-ROBasiaExtensions section missingUST]: T; host disabled.");
            return;
        }

        if (!extConfig.GetBoolean("Enabled", true))
        {
            Log.Info("[TASIA-ROBUST]: Extension host disabled by configuration.");
            return;
        }

        string extensionsPath = extConfig.GetString("ExtensionsPath", "bin/extensions");
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        var fullPath = Path.Combine(basePath, extensionsPath);

        if (!Directory.Exists(fullPath))
        {
            Log.Warn($"[TASIA-ROBUST]: Extensions directory not found: {fullPath}");
            return;
        }

        var logger = new RobustExtensionLogger(Log);
        var context = new RobustExtensionContext(fullPath, logger, config, server);
        
        _loader = new ExtensionLoader(fullPath, logger, context);
        _loader.ScanAndLoad(ExtensionTarget.Robust);
        _loader.StartAll();

        Log.Info($"[TASIA-ROBUST]: Loaded {_loader.LoadedExtensions.Count} extensions");
    }

    public void Close()
    {
        if (_loader != null)
        {
            _loader.StopAll();
            _loader = null;
        }
    }
}

internal class RobustExtensionLogger : IExtensionLogger
{
    private readonly ILog _log;

    public RobustExtensionLogger(ILog log) => _log = log;

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

internal class RobustExtensionContext : IExtensionContext
{
    private readonly string _basePath;
    private readonly IExtensionLogger _logger;
    private readonly IConfigSource _config;
    private readonly IHttpServer _server;

    public string BasePath => _basePath;
    public IExtensionLogger Logger => _logger;

    public RobustExtensionContext(string basePath, IExtensionLogger logger, IConfigSource config, IHttpServer server)
    {
        _basePath = basePath;
        _logger = logger;
        _config = config;
        _server = server;
    }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IConfigSource))
            return _config;
        if (serviceType == typeof(IHttpServer))
            return _server;
        return null;
    }

    public T? GetService<T>() where T : class
    {
        return GetService(typeof(T)) as T;
    }
}
