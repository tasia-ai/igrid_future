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
 *     * Neither the name of the OpenSimulator Project nor the names of its
 *       contributors may be used to endorse or promote products derived from
 *       this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS AND CONTRIBUTORS "AS IS" AND
 * ANY EXPRESS OR IMPLIED WARRANTIES ARE DISCLAIMED.
 */

using System;
using System.IO;
using System.Reflection;
using McMaster.NETCore.Plugins;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.PluginLoadContext.Tests;

/// <summary>
/// Regression test for the QUIC transport never binding its listener.
///
/// DotNetCorePluginsDiscovery scans the program home and, for every DLL it
/// decides to probe, calls McMaster's PluginLoader.CreateFromAssemblyFile.
/// That creates a SEPARATE AssemblyLoadContext per plugin DLL.
///
/// In a region that matters twice over, because LLUDPServerShim is itself
/// discovered as a region module plugin:
///
///   ALC A  <- OpenSim.Region.ClientStack.LindenUDP.dll  (registered as the
///              LLUDPServerShim region module; the scene holds THIS instance)
///   ALC B  <- TasiaAddons.Quic.dll  (QuicServerModule; needs to reach
///              LLUDPServerShim, and resolves it from its OWN copy)
///
/// The same file is therefore loaded once per context. Both instances have the
/// identical path and the identical full type name, so everything looks right,
/// but they are different Assembly objects and `is LLUDPServerShim` is false.
///
/// QuicServerModule therefore could not see the shim that the scene
/// demonstrably had attached, skipped binding its QUIC listener, and still
/// advertised the endpoint in RegionInfo - so viewers connected to a dead
/// port and were dropped with a QUIC transport-initiated shutdown.
///
/// This test reproduces that in seconds and without starting a region.
/// </summary>
public class PluginLoadContextIdentityTests
{
    private const string ClientStackAssemblyFile = "OpenSim.Region.ClientStack.LindenUDP.dll";
    private const string ClientStackShimTypeName = "OpenSim.Region.ClientStack.LindenUDP.LLUDPServerShim";
    private const string QuicModuleTypeName = "TasiaAddons.Quic.QuicServerModule";

    private readonly ITestOutputHelper m_output;

    public PluginLoadContextIdentityTests(ITestOutputHelper output)
    {
        m_output = output;
    }

    [Fact]
    public void QuicAddonAndClientStackPluginShareOneAssemblyInstance()
    {
        string appDir = AppContext.BaseDirectory;
        string scratch = Path.Combine(Path.GetTempPath(), "opensim-pluginalc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        // The deployed program home is flat: every plugin and every host
        // assembly sit in one directory. Reproduce that layout exactly.
        foreach (string file in new[] { "TasiaAddons.Quic.dll", ClientStackAssemblyFile })
        {
            string source = Path.Combine(appDir, file);
            Assert.True(File.Exists(source), $"{file} not found in {appDir}");
            File.Copy(source, Path.Combine(scratch, file), overwrite: true);
        }

        // STEP 1: the discovery scan reaches the client stack and registers
        // LLUDPServerShim as a region module plugin. This is the instance the
        // scene will hold.
        Assembly sceneShimAssembly;
        using (PluginLoader clientStackLoader = CreateLikeProduction(Path.Combine(scratch, ClientStackAssemblyFile)))
        {
            Assembly loaded = clientStackLoader.LoadDefaultAssembly();
            Type? shim = loaded.GetType(ClientStackShimTypeName, throwOnError: false);
            Assert.NotNull(shim);
            sceneShimAssembly = shim!.Assembly;

            m_output.WriteLine("STEP 1 - client stack as a region module plugin (the scene's instance)");
            m_output.WriteLine($"  type    : {shim.FullName}");
            m_output.WriteLine($"  assembly: {Identity(sceneShimAssembly)}");
        }

        // STEP 2: the same scan reaches TasiaAddons.Quic in its OWN context and
        // resolves the client stack again.
        Assembly addonSeesAssembly;
        using (PluginLoader addonLoader = CreateLikeProduction(Path.Combine(scratch, "TasiaAddons.Quic.dll")))
        {
            Assembly addonAssembly = addonLoader.LoadDefaultAssembly();
            Type? module = addonAssembly.GetType(QuicModuleTypeName, throwOnError: false);
            Assert.NotNull(module);

            // m_udpServer is typed as the client stack server. Reading the field
            // type reflects the addon's own view of that assembly, with no
            // compile-time reference that would pre-load it.
            FieldInfo? field = module!.GetField("m_udpServer", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            addonSeesAssembly = field!.FieldType.Assembly;

            m_output.WriteLine("STEP 2 - Quic addon in its own load context");
            m_output.WriteLine($"  type    : {field.FieldType.FullName}");
            m_output.WriteLine($"  assembly: {Identity(addonSeesAssembly)}");
        }

        m_output.WriteLine($"SAME Assembly object: {ReferenceEquals(sceneShimAssembly, addonSeesAssembly)}");

        Assert.True(
            ReferenceEquals(sceneShimAssembly, addonSeesAssembly),
            "Two plugin load contexts hold SEPARATE Assembly instances of "
            + $"{ClientStackAssemblyFile} - the same file, the same type name, different objects. "
            + "TasiaAddons.Quic can therefore never match LLUDPServerShim with `is`, silently skips "
            + "binding its QUIC listener, and viewers are dropped with a transport-initiated shutdown. "
            + $"Scene side: {Identity(sceneShimAssembly)}; addon side: {Identity(addonSeesAssembly)}.");
    }

    /// <summary>
    /// Mirrors DotNetCorePluginsDiscovery.GetAssemblies: one loader, one load
    /// context, lazy loading, and PreferSharedTypes.
    /// </summary>
    private static PluginLoader CreateLikeProduction(string assemblyPath)
    {
        return PluginLoader.CreateFromAssemblyFile(
            assemblyPath,
            sharedTypes: new[] { typeof(OpenSim.Framework.IPlugin) },
            config =>
            {
                config.IsLazyLoaded = true;
                config.PreferSharedTypes = true;
            });
    }

    private static string Identity(Assembly assembly)
    {
        string name = assembly.GetName().Name ?? "?";
        string version = assembly.GetName().Version?.ToString() ?? "?";
        string location;
        try
        {
            location = string.IsNullOrEmpty(assembly.Location) ? "<no location>" : assembly.Location;
        }
        catch
        {
            location = "<unavailable>";
        }

        return $"{name} v{version} [{location}]";
    }
}