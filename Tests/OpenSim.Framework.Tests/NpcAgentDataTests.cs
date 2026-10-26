using OpenMetaverse;
using Xunit;

namespace OpenSim.Framework.Tests;

public class NpcAgentDataTests
{
    [Fact]
    public void TargetAgentIdSurvivesTransferPayloadRoundTrip()
    {
        UUID target = UUID.Random();
        var original = new NpcAgentData
        {
            AgentID = UUID.Random(),
            OwnerID = UUID.Random(),
            TargetAgentID = target
        };

        var decoded = new NpcAgentData();
        Assert.True(decoded.Unpack(original.Pack(new EntityTransferContext())));
        Assert.Equal(target, decoded.TargetAgentID);
    }

    [Fact]
    public void MissingTargetAgentIdRemainsBackwardCompatible()
    {
        var oldPayload = new NpcAgentData { AgentID = UUID.Random() }
            .Pack(new EntityTransferContext());
        oldPayload.Remove("target_agent_id");

        var decoded = new NpcAgentData();
        Assert.True(decoded.Unpack(oldPayload));
        Assert.True(decoded.TargetAgentID.IsZero());
    }
}
