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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.PluginLoadContext.Tests;

/// <summary>
/// Regression test for the QUIC transport never binding its listener.
///
/// DotNetCorePluginsDiscovery used to call McMaster's
/// PluginLoader.CreateFromAssemblyFile once per probed DLL, and each of those
/// creates its own AssemblyLoadContext. In a region that bites twice over,
/// because LLUDPServerShim is itself discovered as a region module plugin:
///
///   ALC A <- OpenSim.Region.ClientStack.LindenUDP.dll  (the instance the scene holds)
///   ALC B <- TasiaAddons.Quic.dll                      (needs to reach that shim)
///
/// The same file was therefore loaded once per context. Both copies share the
/// path and the full type name, so nothing looks wrong, but they are distinct
/// Assembly objects and `is LLUDPServerShim` is false. QuicServerModule could
/// not see the shim the scene demonstrably had attached, skipped binding its
/// QUIC listener, and still advertised the endpoint in RegionInfo, so viewers
/// connected to a dead port and were dropped with a QUIC transport-initiated
/// shutdown.
///
/// This exercises the real production discovery class over a flat copy of a
/// program home, and asserts the invariant that makes the bug impossible:
/// after a scan, each assembly exists EXACTLY ONCE across all load contexts.
/// </summary>
public class PluginLoadContextIdentityTests
{
    private const string ClientStackAssemblyName = "OpenSim.Region.ClientStack.LindenUDP";
    private const string RegionModuleExtensionPoint = "/OpenSim/RegionModules";

    private readonly ITestOutputHelper m_output;

    public PluginLoadContextIdentityTests(ITestOutputHelper output)
    {
        m_output = output;
    }

    [Fact]
    public void ProductionScanLoadsEachPluginAssemblyExactlyOnce()
    {
        string programHome = CreateFlatProgramHome();
        m_output.WriteLine($"scan directory: {programHome}");

        var log = new CapturingLogger(m_output);
        var discovery = new DotNetCorePluginsDiscovery(log);
        discovery.Initialize(programHome);

        var nodes = discovery.GetExtensionNodes(RegionModuleExtensionPoint, typeof(INonSharedRegionModule));

        m_output.WriteLine($"region module plugins discovered: {nodes.Count}");
        m_output.WriteLine($"DLLs in scan dir: {Directory.GetFiles(programHome, "*.dll").Length}");
        m_output.WriteLine("--- loader log ---");
        foreach (string line in log.Lines)
            m_output.WriteLine("  " + line);
        m_output.WriteLine("--- end loader log ---");

        m_output.WriteLine("--- load contexts ---");
        foreach (System.Runtime.Loader.AssemblyLoadContext context in System.Runtime.Loader.AssemblyLoadContext.All)
        {
            var names = context.Assemblies
                .Select(a => a.GetName().Name)
                .Where(n => n != null && (n.StartsWith("OpenSim", StringComparison.Ordinal) || n.StartsWith("Tasia", StringComparison.Ordinal)))
                .OrderBy(n => n)
                .ToArray();

            m_output.WriteLine($"  [{context.Name ?? "Default"}] {names.Length} assemblies: {string.Join(", ", names)}");
        }
        m_output.WriteLine("--- end load contexts ---");

        m_output.WriteLine($"requiredTypeHint assembly: {typeof(INonSharedRegionModule).Assembly.GetName().Name}");
        foreach (var node in nodes.Where(n => n.TypeName.Contains("LLUDPServerShim") || n.TypeName.Contains("QuicServerModule")))
            m_output.WriteLine($"  found: {node.TypeName}");

        Assert.Contains(nodes, n => n.TypeName.Contains("LLUDPServerShim"));
        Assert.Contains(nodes, n => n.TypeName.Contains("QuicServerModule"));

        // The invariant. A duplicate here means one plugin resolved the client
        // stack from a second Assembly instance, so `is` and casts between the
        // addon and the scene can never succeed.
        Assembly[] clientStackCopies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => string.Equals(a.GetName().Name, ClientStackAssemblyName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (Assembly copy in clientStackCopies)
            m_output.WriteLine($"  loaded copy: {Describe(copy)}");

        Assert.True(
            clientStackCopies.Length == 1,
            $"{ClientStackAssemblyName} was loaded {clientStackCopies.Length} times across load "
            + "contexts. Every extra copy is a plugin that cannot match a host type with `is`, which is "
            + "how the QUIC listener silently never bound. Copies: "
            + string.Join(" | ", clientStackCopies.Select(Describe)));

        // And the addon must resolve the very same Assembly the scene holds.
        Type addonSeesShim = ResolveClientStackTypeAsSeenByQuicAddon(clientStackCopies[0]);
        Assert.True(
            ReferenceEquals(addonSeesShim.Assembly, clientStackCopies[0]),
            "TasiaAddons.Quic does not share the host's client stack Assembly instance.");
    }

    private static Type ResolveClientStackTypeAsSeenByQuicAddon(Assembly hostClientStack)
    {
        Assembly addon = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "TasiaAddons.Quic", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(addon);

        Type? module = addon!.GetType("TasiaAddons.Quic.QuicServerModule", throwOnError: false);
        Assert.NotNull(module);

        FieldInfo? field = module!.GetField("m_udpServer", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);

        Assert.Equal(hostClientStack.GetName().Name, field!.FieldType.Assembly.GetName().Name);
        return field.FieldType;
    }

    /// <summary>
    /// Builds a flat directory that looks like a deployed program home: every
    /// assembly next to each other, which is what the region loader scans.
    /// </summary>
    private static string CreateFlatProgramHome()
    {
        string source = AppContext.BaseDirectory;
        string home = Path.Combine(Path.GetTempPath(), "opensim-programhome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);

        foreach (string file in Directory.GetFiles(source, "*.dll"))
        {
            string name = Path.GetFileName(file);

            // Leave the test runner's own assemblies behind; they are not plugins.
            if (name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("OpenSim.PluginLoadContext.Tests", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Microsoft.TestPlatform", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(file, Path.Combine(home, name), overwrite: true);
        }

        foreach (string sub in new[] { "lib64", "runtimes" })
        {
            string dir = Path.Combine(source, sub);
            if (Directory.Exists(dir))
                CopyDirectory(dir, Path.Combine(home, sub));
        }

        return home;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (string dir in Directory.GetDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    private static string Describe(Assembly assembly)
    {
        string location;
        try
        {
            location = string.IsNullOrEmpty(assembly.Location) ? "<no location>" : assembly.Location;
        }
        catch
        {
            location = "<unavailable>";
        }

        return $"{assembly.GetName().Name} v{assembly.GetName().Version} [{location}]";
    }

    /// <summary>
    /// Keeps the discovery loader's own diagnostics so a failed scan explains
    /// itself instead of just reporting zero candidates.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly ITestOutputHelper m_output;

        public List<string> Lines { get; } = new();

        public CapturingLogger(ITestOutputHelper output)
        {
            m_output = output;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < Microsoft.Extensions.Logging.LogLevel.Information)
                return;

            string text = formatter(state, exception);
            if (exception != null)
                text += " -> " + exception.Message;

            Lines.Add($"[{logLevel}] {text}");
        }
    }
}