using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace OpenSim.Framework;

/// <summary>Loads plugins against the caller's actual contract, never just a matching type name.</summary>
public static class StoragePluginLoader
{
    public static T Load<T>(string path, string className, object[] args, ILogger logger) where T : class
    {
        args ??= Array.Empty<object>();
        try
        {
            Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
            Type contract = typeof(T);
            Type[] types = assembly.GetTypes(); // Fail the whole load on broken dependencies.
            foreach (Type type in types)
            {
                if (!type.IsPublic || type.IsAbstract || type.ContainsGenericParameters)
                    continue;
                if (!string.IsNullOrEmpty(className) && type.FullName != className && type.Name != className)
                    continue;
                if (!contract.IsAssignableFrom(type))
                {
                    if (type.GetInterfaces().Any(i => i.FullName == contract.FullName))
                        logger.LogError("Plugin {Type} has a different contract identity. Expected {Contract} in {Location}; plugin assembly {Assembly}",
                            type.FullName, contract.AssemblyQualifiedName, contract.Assembly.Location, assembly.FullName);
                    continue;
                }
                // Choose only a public constructor compatible with the requested arguments.
                // This also permits callers to try the legacy two-argument signature.
                if (!type.GetConstructors().Any(c => c.GetParameters().Length == args.Length &&
                    c.GetParameters().Select((p, i) => args[i] == null
                        ? !p.ParameterType.IsValueType || Nullable.GetUnderlyingType(p.ParameterType) != null
                        : p.ParameterType.IsInstanceOfType(args[i])).All(matches => matches)))
                    continue;
                return (T)Activator.CreateInstance(type, args);
            }
            logger.LogError("No public concrete plugin for {Contract} with {ArgumentCount} arguments in {Path} ({Assembly}); rebuild and deploy the complete application together",
                contract.AssemblyQualifiedName, args.Length, assembly.Location, assembly.FullName);
        }
        catch (Exception exception)
        {
            LogFailure(exception, path, typeof(T), logger);
        }
        return null;
    }

    private static void LogFailure(Exception exception, string path, Type contract, ILogger logger)
    {
        // Never log constructor argument values: storage arguments contain credentials.
        logger.LogError(exception, "Failed to load {Contract} from {Path}", contract.AssemblyQualifiedName, path);
        if (exception is ReflectionTypeLoadException reflection)
            foreach (Exception loader in reflection.LoaderExceptions)
                if (loader != null)
                    LogFailure(loader, path, contract, logger);
        if (exception.InnerException != null)
            LogFailure(exception.InnerException, path, contract, logger);
    }
}
