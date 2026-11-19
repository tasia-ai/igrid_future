using System;
using System.Net;
using NUnit.Framework;
using OpenSim.Framework;
using OpenSim.Services.LLLoginService;
using OpenSim.Services.Interfaces;

namespace OpenSim.Server.Handlers.Tests;

[TestFixture]
public class QuicLoginPreRegistrationTests
{
    private static GridRegion QuicDestination() => new()
    {
        RegionName = "TestRegion",
        ExternalHostName = "127.0.0.1",
        InternalEndPoint = new IPEndPoint(IPAddress.Any, 19000),
        QuicHost = "proxy.example.invalid",
        QuicPort = 9001
    };

    [Test]
    public void LocalLogin_RegistersFallbackBeforeViewerConnects()
    {
        uint code = (uint)Random.Shared.Next(1, int.MaxValue);
        AgentCircuitData circuit = new() { circuitcode = code };
        try
        {
            Assert.That(LLLoginService.TryPreRegisterQuicLoginCircuit(
                QuicDestination(), circuit, true, true), Is.True);
            Assert.That(QuicCircuitRegistry.TryGetCircuit(code, out IPEndPoint endpoint), Is.True);
            Assert.That(endpoint.Address, Is.EqualTo(IPAddress.Loopback));
            Assert.That(endpoint.Port, Is.EqualTo(19000));
            Assert.That(QuicCircuitRegistry.TryGetQUIC(code, out _), Is.False,
                "Only the simulator may announce its actual native QUIC port.");
        }
        finally
        {
            QuicCircuitRegistry.Unregister(code);
        }
    }

    [Test]
    public void DisabledProxy_ForeignRegion_AndNoQuicAdvertisement_DoNotRegister()
    {
        uint code = (uint)Random.Shared.Next(1, int.MaxValue);
        AgentCircuitData circuit = new() { circuitcode = code };
        try
        {
            GridRegion destination = QuicDestination();
            Assert.That(LLLoginService.TryPreRegisterQuicLoginCircuit(destination, circuit, true, false), Is.False);
            Assert.That(LLLoginService.TryPreRegisterQuicLoginCircuit(destination, circuit, false, true), Is.False);
            destination.QuicPort = 0;
            Assert.That(LLLoginService.TryPreRegisterQuicLoginCircuit(destination, circuit, true, true), Is.False);
            Assert.That(QuicCircuitRegistry.TryGetCircuit(code, out _), Is.False);
        }
        finally
        {
            QuicCircuitRegistry.Unregister(code);
        }
    }
}
