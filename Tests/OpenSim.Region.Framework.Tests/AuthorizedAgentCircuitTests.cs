using System;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using Xunit;

namespace OpenSim.Region.Framework.Tests;

public class AuthorizedAgentCircuitTests
{
    [Fact]
    public void TeleportFlagsAreDeliveredToTransportModules()
    {
        EventManager manager = new();
        uint receivedFlags = 0;
        AgentCircuitData circuit = new() { circuitcode = 12347, child = true };
        manager.OnAuthorizedAgentCircuit += (_, flags) => receivedFlags = flags;

        manager.TriggerOnAuthorizedAgentCircuit(circuit, 16);

        Assert.Equal((uint)16, receivedFlags);
    }

    [Fact]
    public void AcceptedCircuitIsPassedToListeners()
    {
        EventManager manager = new();
        AgentCircuitData expected = new() { circuitcode = 12345, child = false };
        AgentCircuitData received = null;
        manager.OnAuthorizedAgentCircuit += (data, flags) => received = data;

        manager.TriggerOnAuthorizedAgentCircuit(expected, 0);

        Assert.Same(expected, received);
    }

    [Fact]
    public void OneFaultyListenerDoesNotPreventOtherTransportModules()
    {
        EventManager manager = new();
        AgentCircuitData expected = new() { circuitcode = 12346, child = true };
        AgentCircuitData received = null;
        manager.OnAuthorizedAgentCircuit += (_, flags) => throw new InvalidOperationException("test handler");
        manager.OnAuthorizedAgentCircuit += (data, flags) => received = data;

        manager.TriggerOnAuthorizedAgentCircuit(expected, 0);

        Assert.Same(expected, received);
    }
}
