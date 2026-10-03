/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace OpenSim.Framework;

public sealed class PluginDiscoveryCapabilities
{
    public bool SupportsAddinRegistryMetadata { get; }

    public PluginDiscoveryCapabilities(bool supportsAddinRegistryMetadata)
    {
        SupportsAddinRegistryMetadata = supportsAddinRegistryMetadata;
    }
}

/// <summary>
/// Abstraction for plugin discovery backends.
/// </summary>
public interface IPluginDiscovery : IDisposable
{
    PluginDiscoveryCapabilities Capabilities { get; }
    void Initialize(string pluginDirectory);
    IReadOnlyList<PluginExtensionNode> GetExtensionNodes(string extensionPoint, Type requiredTypeHint = null);
    int GetExtensionNodeCount(string extensionPoint, Type requiredTypeHint = null);
}

public static class PluginDiscoveryFactory
{
    public static IPluginDiscovery Create(ILogger log)
    {
        log.LogInformation("[PLUGINS]: Using DotNetCorePlugins discovery backend");
        return new DotNetCorePluginsDiscovery(log);
    }
}

public class DotNetCorePluginsDiscovery : IPluginDiscovery
{
    private readonly ILogger m_log;

    // Static helper TryAddType needs a logger of its own; m_log is per-instance
    // and is not available from static context.
    private static readonly ILogger s_log =
        LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private string m_pluginDirectory = ".";
    private Type m_cachedRequiredType;
    private List<Assembly> m_assemblies = new List<Assembly>();
    private PluginRegistry m_registeredPlugins = new PluginRegistry();
    private int m_lastScannedAssemblyCount;
    private int m_lastSkippedAssemblyCount;
    private int m_lastLoadFailureCount;
    private readonly List<McMaster.NETCore.Plugins.PluginLoader> m_pluginLoaders = new List<McMaster.NETCore.Plugins.PluginLoader>();
    private SharedPluginLoadContext m_pluginLoadContext;
    private static readonly string[] s_skippedAssemblyPrefixes =
    {
        "System.",
        "Microsoft.",
        "Autofac",
        "BouncyCastle",
        "BulletXNA",
        "C5",
        "CoreJ2K",
        "DotNetOpenId",
        "ICSharpCode",
        "log4net",
        "LukeSkywalker",
        "MailKit",
        "McMaster.NETCore.Plugins",
        "MimeKit",
        "MySqlConnector",
        "NDesk.Options",
        "netcd",
        "Nini",
        "Npgsql",
        "OpenMetaverse",
        "RestSharp",
        "SkiaSharp",
        "SmartThreadPool",
        "Warp3D",
        "xmlrpc"
    };

    public PluginDiscoveryCapabilities Capabilities { get; } =
        new PluginDiscoveryCapabilities(supportsAddinRegistryMetadata: false);

    public DotNetCorePluginsDiscovery(ILogger log)
    {
        m_log = log;
    }

    public void Initialize(string pluginDirectory)
    {
        m_pluginDirectory = string.IsNullOrWhiteSpace(pluginDirectory) ? "." : pluginDirectory;
        m_cachedRequiredType = null;
        m_assemblies = new List<Assembly>();
        m_registeredPlugins = new PluginRegistry();
        DisposePluginLoaders();
    }

    public IReadOnlyList<PluginExtensionNode> GetExtensionNodes(string extensionPoint, Type requiredTypeHint = null)
    {
        List<PluginExtensionNode> nodes = new List<PluginExtensionNode>();
        HashSet<string> seenTypes = new HashSet<string>(StringComparer.Ordinal);

        if (requiredTypeHint == null)
        {
            m_log.LogWarning("[PLUGINS]: DotNetCorePlugins discovery for {0} requires a plugin type hint.", extensionPoint);
            return nodes;
        }

        IReadOnlyList<PluginDescriptor> explicitRegistrations =
            m_registeredPlugins.GetPlugins(extensionPoint);

        int explicitCount = 0;
        int reflectionCount = 0;

        if (explicitRegistrations.Count > 0)
        {
            foreach (PluginDescriptor descriptor in explicitRegistrations)
            {
                Type type = descriptor.PluginType;

                if (type == null || type.IsAbstract || type.IsInterface)
                    continue;

                if (!requiredTypeHint.IsAssignableFrom(type))
                    continue;

                Assembly assembly = type.Assembly;
                string provider = assembly.GetName().Name ?? string.Empty;
                string path = string.IsNullOrEmpty(assembly.Location)
                    ? provider
                    : string.Format("{0}:{1}", assembly.Location, type.FullName);

                nodes.Add(new PluginExtensionNode(
                    descriptor.Id ?? type.Name,
                    provider,
                    path,
                    type,
                    () => Activator.CreateInstance(type, true)));

                seenTypes.Add(type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
                explicitCount++;
            }
        }

        foreach (Assembly assembly in GetAssemblies(requiredTypeHint))
        {
            foreach (Type type in GetLoadableTypes(assembly))
            {
                if (type.IsAbstract || type.IsInterface)
                    continue;

                if (!requiredTypeHint.IsAssignableFrom(type))
                    continue;

                string typeKey = type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
                if (seenTypes.Contains(typeKey))
                    continue;

                string provider = assembly.GetName().Name ?? string.Empty;
                string path = string.IsNullOrEmpty(assembly.Location)
                    ? provider
                    : string.Format("{0}:{1}", assembly.Location, type.FullName);

                nodes.Add(new PluginExtensionNode(
                    type.Name,
                    provider,
                    path,
                    type,
                    () => Activator.CreateInstance(type, true)));

                seenTypes.Add(typeKey);
                reflectionCount++;
            }
        }

        m_log.LogInformation(
            "[PLUGINS]: Discovery summary [{0}] scanned={1}, skipped={2}, loadFailures={3}, code={4}, reflected={5}, candidates={6} using {7}",
            extensionPoint,
            m_lastScannedAssemblyCount,
            m_lastSkippedAssemblyCount,
            m_lastLoadFailureCount,
            explicitCount,
            reflectionCount,
            nodes.Count,
            nameof(DotNetCorePluginsDiscovery));

        return nodes;
    }

    public int GetExtensionNodeCount(string extensionPoint, Type requiredTypeHint = null)
    {
        return GetExtensionNodes(extensionPoint, requiredTypeHint).Count;
    }

    public void Dispose()
    {
        m_cachedRequiredType = null;
        m_assemblies.Clear();
        DisposePluginLoaders();
    }

    private IReadOnlyList<Assembly> GetAssemblies(Type requiredTypeHint)
    {
        if (m_cachedRequiredType == requiredTypeHint && m_assemblies.Count > 0)
            return m_assemblies;

        m_cachedRequiredType = requiredTypeHint;
        m_assemblies.Clear();
        m_registeredPlugins.Clear();
        m_lastScannedAssemblyCount = 0;
        m_lastSkippedAssemblyCount = 0;
        m_lastLoadFailureCount = 0;
        DisposePluginLoaders();

        if (!Directory.Exists(m_pluginDirectory))
        {
            m_log.LogWarning("[PLUGINS]: Plugin discovery directory does not exist: {0}", m_pluginDirectory);
            return m_assemblies;
        }

        foreach (string dllPath in Directory.GetFiles(m_pluginDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            m_lastScannedAssemblyCount++;

            if (!ShouldProbeAssembly(dllPath, requiredTypeHint))
            {
                m_lastSkippedAssemblyCount++;
                continue;
            }

            try
            {
                string assemblyPath = Path.IsPathRooted(dllPath)? dllPath : Path.GetFullPath(dllPath);
                Type[] sharedTypes = BuildSharedTypes(requiredTypeHint);

                // Reuse an instance the host already has before loading our own.
                //
                // The scan probes anything starting with "OpenSim.", so host
                // assemblies such as OpenSim.Region.ClientStack.LindenUDP are
                // themselves loaded as if they were plugins - LLUDPServerShim is
                // a region module discovered exactly that way. Loading a second
                // copy here gives the scene one Assembly instance while plugins
                // referencing it see another: same path, same type name, but
                // `is` is false and casts throw. That is how QuicServerModule
                // failed to find the shim it needed, skipped binding its QUIC
                // listener, and left viewers dropped at transport level.
                string simpleName = Path.GetFileNameWithoutExtension(assemblyPath) ?? string.Empty;
                Assembly existing = FindAlreadyLoaded(simpleName);

                if (existing != null)
                {
                    m_assemblies.Add(existing);
                }
                else
                {
                    m_pluginLoadContext ??= new SharedPluginLoadContext(m_pluginDirectory, m_log);
                    m_assemblies.Add(m_pluginLoadContext.LoadFromAssemblyPath(assemblyPath));
                }
            }
            catch (BadImageFormatException)
            {
                // Ignore native or incompatible binaries.
            }
            catch (Exception e)
            {
                m_lastLoadFailureCount++;
                m_log.LogWarning("[PLUGINS]: Unable to load assembly {0}: {1}", dllPath, e.Message);
            }
        }

        m_registeredPlugins = PluginRegistry.FromProviders(m_assemblies, m_log);

        return m_assemblies;
    }

    private static Type[] BuildSharedTypes(Type requiredTypeHint)
    {
        HashSet<Type> sharedTypes = new HashSet<Type>();

        if (requiredTypeHint != null)
            sharedTypes.Add(requiredTypeHint);

        // Keep framework/plugin-registry contracts unified with the host context.
        sharedTypes.Add(typeof(IPlugin));
        sharedTypes.Add(typeof(IPluginRegistryProvider));

        // Ensure singleton server state is shared instead of duplicated per plugin load context.
        TryAddType(sharedTypes, "OpenSim.Framework.Servers.MainServer, OpenSim.Framework.Servers");
        TryAddType(sharedTypes, "OpenSim.Framework.Servers.IMainServer, OpenSim.Framework.Servers");
        TryAddType(sharedTypes, "OpenSim.Framework.Servers.HttpServer.IHttpServer, OpenSim.Framework.Servers.HttpServer");

        // Client stack types that region addons must interoperate with directly.
        //
        // Every plugin gets its own AssemblyLoadContext. Any host assembly a
        // plugin references is therefore resolved and loaded a SECOND time
        // unless its type is declared shared here. The result is two distinct
        // Assembly objects for one file: identical path, identical full type
        // name, but `is` is false and casts throw. TasiaAddons.Quic needs the
        // live LLUDPServer to bind a QUIC circuit to it, and silently found
        // nothing because the shim in the scene was a different type object
        // than the one it had compiled against - so it skipped binding its
        // listener while still advertising the endpoint, and viewers were
        // dropped at transport level.
        TryAddType(sharedTypes, "OpenSim.Region.ClientStack.LindenUDP.LLUDPServerShim, OpenSim.Region.ClientStack.LindenUDP");
        TryAddType(sharedTypes, "OpenSim.Region.ClientStack.LindenUDP.LLUDPServer, OpenSim.Region.ClientStack.LindenUDP");

        return sharedTypes.ToArray();
    }

    private static void TryAddType(HashSet<Type> sharedTypes, string assemblyQualifiedTypeName)
    {
        Type resolvedType = Type.GetType(assemblyQualifiedTypeName, false);
        if (resolvedType != null)
        {
            sharedTypes.Add(resolvedType);
            return;
        }

        // Type.GetType only searches assemblies already reachable from this
        // assembly's own dependency closure, and it fails SILENTLY. A client
        // stack assembly like OpenSim.Region.ClientStack.LindenUDP is not a
        // dependency of OpenSim.Framework, so the lookup returns null and the
        // type is never shared - leaving every plugin load context with its own
        // private copy. Fall back to loading by simple name, which resolves
        // against the host's already-loaded assemblies.
        var parts = assemblyQualifiedTypeName.Split(',');
        if (parts.Length >= 2)
        {
            string typeName = parts[0].Trim();
            string assemblyName = parts[1].Trim();
            try
            {
                var loaded = Assembly.Load(new AssemblyName(assemblyName));
                resolvedType = loaded.GetType(typeName, false);
                if (resolvedType != null)
                {
                    sharedTypes.Add(resolvedType);
                    return;
                }
            }
            catch (Exception e)
            {
                s_log.LogWarning("[PLUGINS]: Could not resolve shared type {0}: {1}",
                    assemblyQualifiedTypeName, e.Message);
                return;
            }
        }

        s_log.LogWarning(
            "[PLUGINS]: Shared type {0} could not be resolved. Any plugin that uses it " +
            "will see a SEPARATE type instance from the host, so `is` and casts against it fail.",
            assemblyQualifiedTypeName);
    }

    /// <summary>
    /// Find an already loaded instance of an assembly by simple name, preferring
    /// the host's own load context so plugin types unify with host types.
    /// </summary>
    private static Assembly FindAlreadyLoaded(string simpleName)
    {
        if (string.IsNullOrEmpty(simpleName))
            return null;

        foreach (Assembly loaded in AssemblyLoadContext.Default.Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                return loaded;
        }

        foreach (System.Runtime.Loader.AssemblyLoadContext context in AssemblyLoadContext.All)
        {
            foreach (Assembly loaded in context.Assemblies)
            {
                if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return loaded;
            }
        }

        return null;
    }

    private static bool ShouldProbeAssembly(string dllPath, Type requiredTypeHint)
    {
        string assemblySimpleName = Path.GetFileNameWithoutExtension(dllPath) ?? string.Empty;

        if (requiredTypeHint != null)
        {
            if (requiredTypeHint.Name.Equals("IApplicationPlugin", StringComparison.Ordinal))
            {
                if (assemblySimpleName.StartsWith("OpenSim.ApplicationPlugins.", StringComparison.OrdinalIgnoreCase) ||
                    assemblySimpleName.Contains(".ApplicationPlugins.", StringComparison.OrdinalIgnoreCase) ||
                    assemblySimpleName.EndsWith("ApplicationPlugin", StringComparison.OrdinalIgnoreCase) ||
                    assemblySimpleName.EndsWith("ApplicationPlugins", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return false;
            }
        }

        if (assemblySimpleName.StartsWith("OpenSim.", StringComparison.OrdinalIgnoreCase))
            return true;

        if (assemblySimpleName.StartsWith("WebRtcVoice", StringComparison.OrdinalIgnoreCase))
            return true;

        if (assemblySimpleName.EndsWith("Plugin", StringComparison.OrdinalIgnoreCase) ||
            assemblySimpleName.EndsWith("Plugins", StringComparison.OrdinalIgnoreCase) ||
            assemblySimpleName.EndsWith("Module", StringComparison.OrdinalIgnoreCase) ||
            assemblySimpleName.EndsWith("Modules", StringComparison.OrdinalIgnoreCase) ||
            assemblySimpleName.Contains(".Plugin.", StringComparison.OrdinalIgnoreCase) ||
            assemblySimpleName.Contains(".Module.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string prefix in s_skippedAssemblyPrefixes)
        {
            if (assemblySimpleName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Keep probing unknown assemblies so external plugin names still work by default.
        return true;
    }

    private void DisposePluginLoaders()
    {
        foreach (McMaster.NETCore.Plugins.PluginLoader loader in m_pluginLoaders)
        {
            loader.Dispose();
        }

        m_pluginLoaders.Clear();

        // The shared context is not collectible: plugin instances stay alive for
        // the lifetime of the process and are handed to scenes and modules. Drop
        // the reference only so a rescan starts from a clean slate.
        m_pluginLoadContext = null;
    }

    /// <summary>
    /// A single AssemblyLoadContext shared by every plugin discovered in one
    /// scan, so each file is loaded exactly once and every plugin sees the same
    /// Assembly object for shared host assemblies.
    /// </summary>
    private sealed class SharedPluginLoadContext : AssemblyLoadContext
    {
        private readonly string m_directory;
        private readonly ILogger m_log;

        public SharedPluginLoadContext(string directory, ILogger log)
            : base("OpenSimPlugins")
        {
            m_directory = directory;
            m_log = log;
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            string simpleName = assemblyName.Name;
            if (string.IsNullOrEmpty(simpleName))
                return null;

            // Already loaded here (a plugin pulled in as a dependency of another
            // plugin). This is the case that used to produce duplicates.
            foreach (Assembly loaded in Assemblies)
            {
                if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return loaded;
            }

            // Always let the DEFAULT context resolve it. It is the context the
            // host itself runs in, so this both reuses the copy the host already
            // has and forces anything missing to be loaded THERE rather than
            // here. Two copies of one file can never result.
            try
            {
                Assembly fromDefault = AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
                if (fromDefault != null)
                    return fromDefault;
            }
            catch (Exception e)
            {
                m_log?.LogDebug("[PLUGINS]: Default context could not resolve {0}: {1}",
                    simpleName, e.Message);
            }

            // Plugin-only assembly: keep it in this context.
            try
            {
                string candidate = Path.Combine(m_directory, simpleName + ".dll");
                if (File.Exists(candidate))
                    return LoadFromAssemblyPath(candidate);
            }
            catch (Exception e)
            {
                m_log?.LogDebug("[PLUGINS]: Could not load {0} from {1}: {2}",
                    simpleName, m_directory, e.Message);
            }

            return null;
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException rtle)
        {
            return rtle.Types.Where(t => t != null);
        }
    }
}
