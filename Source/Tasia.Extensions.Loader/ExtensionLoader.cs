using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Tasia.Extensions.SDK;

namespace Tasia.Extensions.Loader;

public class ExtensionLoader
{
    private readonly string _extensionsPath;
    private readonly IExtensionLogger _logger;
    private readonly List<LoadedExtension> _loadedExtensions = new();
    private readonly IExtensionContext _context;

    public IReadOnlyList<LoadedExtension> LoadedExtensions => _loadedExtensions;

    public ExtensionLoader(string extensionsPath, IExtensionLogger logger, IExtensionContext context)
    {
        _extensionsPath = extensionsPath ?? throw new ArgumentNullException(nameof(extensionsPath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public void ScanAndLoad(ExtensionTarget target)
    {
        if (!Directory.Exists(_extensionsPath))
        {
            _logger.Warn($"Extensions directory does not exist: {_extensionsPath}");
            return;
        }

        var pluginDirs = Directory.GetDirectories(_extensionsPath);
        _logger.Info($"Found {pluginDirs.Length} potential extension directories");

        foreach (var dir in pluginDirs)
        {
            try
            {
                LoadExtensionFromDirectory(dir, target);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load extension from {dir}", ex);
            }
        }
    }

    private void LoadExtensionFromDirectory(string dir, ExtensionTarget target)
    {
        var jsonPath = Path.Combine(dir, "plugin.json");
        if (!File.Exists(jsonPath))
        {
            _logger.Debug($"No plugin.json found in {dir}, skipping");
            return;
        }

        var json = File.ReadAllText(jsonPath);
        var manifest = JsonConvert.DeserializeObject<ExtensionManifest>(json);

        if (manifest == null)
        {
            _logger.Error($"Failed to parse plugin.json in {dir}");
            return;
        }

        if (!manifest.Enabled)
        {
            _logger.Info($"Extension {manifest.Name} is disabled in manifest");
            return;
        }

        if (!MatchesTarget(manifest.Target, target))
        {
            _logger.Debug($"Extension {manifest.Name} target {manifest.Target} does not match current target {target}, skipping");
            return;
        }

        LoadPluginDependencies(dir, manifest);
        LoadPluginAssembly(dir, manifest);
    }

    private bool MatchesTarget(ExtensionTarget manifestTarget, ExtensionTarget currentTarget)
    {
        return manifestTarget == ExtensionTarget.Both 
            || manifestTarget == currentTarget;
    }

    private void LoadPluginDependencies(string dir, ExtensionManifest manifest)
    {
        var depsPath = Path.Combine(dir, "deps");
        if (!Directory.Exists(depsPath))
            return;

        var deps = Directory.GetFiles(depsPath, "*.dll");
        foreach (var dep in deps)
        {
            try
            {
                Assembly.LoadFrom(dep);
                _logger.Debug($"Loaded dependency: {Path.GetFileName(dep)}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load dependency {dep}", ex);
            }
        }
    }

    private void LoadPluginAssembly(string dir, ExtensionManifest manifest)
    {
        var dllPath = Path.Combine(dir, $"{manifest.Name}.dll");
        if (!File.Exists(dllPath))
        {
            var altPath = Path.Combine(dir, "bin", $"{manifest.Name}.dll");
            if (File.Exists(altPath))
                dllPath = altPath;
            else
            {
                _logger.Error($"Assembly not found for extension {manifest.Name}: {dllPath}");
                return;
            }
        }

        try
        {
            var asm = Assembly.LoadFrom(dllPath);
            var type = asm.GetType(manifest.EntryPoint);
            
            if (type == null)
            {
                _logger.Error($"Entry point type not found: {manifest.EntryPoint} in {manifest.Name}");
                return;
            }

            var instance = Activator.CreateInstance(type) as IGridExtension;
            if (instance == null)
            {
                _logger.Error($"Type {manifest.EntryPoint} does not implement IGridExtension");
                return;
            }

            instance.Initialize(_context);
            
            var loaded = new LoadedExtension
            {
                Manifest = manifest,
                Instance = instance,
                Assembly = asm
            };

            _loadedExtensions.Add(loaded);
            _logger.Info($"Successfully loaded extension: {manifest.Name} v{manifest.Version}");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to load extension assembly {manifest.Name}", ex);
        }
    }

    public void StartAll()
    {
        foreach (var ext in _loadedExtensions)
        {
            try
            {
                ext.Instance.PostInitialise();
                ext.Instance.Start();
                _logger.Info($"Started extension: {ext.Manifest.Name}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start extension {ext.Manifest.Name}", ex);
            }
        }
    }

    public void StopAll()
    {
        foreach (var ext in _loadedExtensions.AsEnumerable().Reverse())
        {
            try
            {
                ext.Instance.Stop();
                ext.Instance.Dispose();
                _logger.Info($"Stopped extension: {ext.Manifest.Name}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to stop extension {ext.Manifest.Name}", ex);
            }
        }
        _loadedExtensions.Clear();
    }
}

public class LoadedExtension
{
    public ExtensionManifest Manifest { get; set; } = new();
    public IGridExtension Instance { get; set; } = null!;
    public Assembly Assembly { get; set; } = null!;
}
