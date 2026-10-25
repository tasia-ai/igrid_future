/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using System.Linq;

using Autofac.Core;
using Autofac.Core.Registration;

namespace OpenSim.Server.Base;

public static class RegisterServices
{
    public static void Register(
    IComponentRegistryBuilder builder, 
    string directoryPath, 
    string searchPattern = "*.dll"
    )
    {
        if (Directory.Exists(directoryPath) is false)
            return;
        
        // Load assemblies from the directory
        var assemblies = Directory.GetFiles(directoryPath, searchPattern)
            .Select(Assembly.LoadFrom)
            .ToArray();

        // Register services from the modules in the assemblies
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Some types in the assembly may have unresolved references
                // (e.g. types moved to another assembly). Load only resolvable types.
                types = ex.Types.Where(t => t != null).ToArray();
            }

            var moduleTypes = types
                .Where(t => typeof(IModule).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract)
                .ToList();

            foreach (var moduleType in moduleTypes)
            {
                var moduleInstance = (IModule)Activator.CreateInstance(moduleType);
                moduleInstance?.Configure(builder);
            }
        }
    }
}

