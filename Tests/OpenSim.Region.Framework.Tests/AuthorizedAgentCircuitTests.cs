using System;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using Xunit;

namespace OpenSim.Region.Framework.Tests;

public class AuthorizedAgentCircuitTests
{
    [Fact]
    public void AcceptedCircuitIsPassedToListeners()
    {
        EventManager manager = new();
        AgentCircuitData expected = new() { circuitcode = 12345, child = false };
        AgentCircuitData received = null;
        manager.OnAuthorizedAgentCircuit += data => received = data;

        manager.TriggerOnAuthorizedAgentCircuit(expected);

        Assert.Same(expected, received);
    }

    [Fact]
    public void OneFaultyListenerDoesNotPreventOtherTransportModules()
    {
        EventManager manager = new();
        AgentCircuitData expected = new() { circuitcode = 12346, child = true };
        AgentCircuitData received = null;
        manager.OnAuthorizedAgentCircuit += _ => throw new InvalidOperationException("test handler");
        manager.OnAuthorizedAgentCircuit += data => received = data;

        manager.TriggerOnAuthorizedAgentCircuit(expected);

        Assert.Same(expected, received);
    }
}
