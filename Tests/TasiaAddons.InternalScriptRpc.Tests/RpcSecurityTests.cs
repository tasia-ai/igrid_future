using System.Collections.Concurrent;
using System.Text.Json;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using TasiaAddons.InternalScriptRpc;
using Xunit;

namespace TasiaAddons.InternalScriptRpc.Tests;

public sealed class RpcSecurityTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void HmacAuthenticatesExactBodyAndRejectsTampering()
    {
        string signature = RpcSecurity.Sign(Key, "2000000000", "nonce", "{\"a\":1}");
        Assert.True(RpcSecurity.Verify(Key, "2000000000", "nonce", "{\"a\":1}", signature));
        Assert.False(RpcSecurity.Verify(Key, "2000000000", "nonce", "{\"a\":2}", signature));
        Assert.False(RpcSecurity.Verify(Key, "2000000001", "nonce", "{\"a\":1}", signature));
        Assert.False(RpcSecurity.Verify(Key, "2000000000", "nonce", "{\"a\":1}", "bad"));
    }

    [Fact]
    public void TimestampWindowAcceptsNowAndRejectsExpiredOrFuture()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(RpcSecurity.TimestampValid(now));
        Assert.False(RpcSecurity.TimestampValid(now - 121));
        Assert.False(RpcSecurity.TimestampValid(now + 121));
    }

    [Fact]
    public void NonceConsumptionIsAtomicUnderConcurrentReplay()
    {
        var cache = new ConcurrentDictionary<string, long>();
        int accepted = 0;
        System.Threading.Tasks.Parallel.For(0, 64, _ => { if (RpcSecurity.ConsumeNonce(cache, "the-same-nonce")) Interlocked.Increment(ref accepted); });
        Assert.Equal(1, accepted);
    }

    [Fact]
    public void RequestEnvelopeRequiresExactFieldsAndBoundedOperation()
    {
        string body = "{\"version\":1,\"requestId\":\"efec3e66-a98b-45c2-a24a-b3f03a6e9118\",\"timestamp\":2000000000,\"nonce\":\"0123456789abcdef0123456789abcdef\",\"operation\":\"inventory.lookup\",\"owner\":\"efec3e66-a98b-45c2-a24a-b3f03a6e9118\",\"payload\":{}}";
        Assert.True(RpcSecurity.TryParseRequest(body, out _));
        Assert.False(RpcSecurity.TryParseRequest(body.Replace("\"payload\":{}", "\"extra\":0,\"payload\":{}"), out _));
        Assert.False(RpcSecurity.TryParseRequest(body.Replace("inventory.lookup", "bad operation"), out _));
    }

    [Fact]
    public void OwnerAllowlistFailsClosedOnEmptyOrMalformedValues()
    {
        Assert.Empty(RpcSecurity.ParseOwners(" , "));
        Assert.Throws<ArgumentException>(() => RpcSecurity.ParseOwners("not-a-uuid"));
        Assert.Throws<InvalidOperationException>(() => RpcSecurity.ValidateSecret("short"));
    }

    [Theory]
    [InlineData("http://os.tasia.work.gd:22041/", 10000, "https://os.tasia.work.gd:32041/internal-script-rpc/execute/")]
    [InlineData("https://os.tasia.work.gd:32041/", 10000, "https://os.tasia.work.gd:32041/internal-script-rpc/execute/")]
    public void RpcEndpointUsesDedicatedHttpsPort(string serverUri, int offset, string expectedPrefix)
    {
        UUID regionId = UUID.Random();
        Assert.True(InternalScriptRpcConnector.TryGetRegionRpcUri(serverUri, regionId, offset, out Uri uri));
        Assert.StartsWith(expectedPrefix, uri.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://user@os.tasia.work.gd:22041/")]
    [InlineData("http://os.tasia.work.gd:22041/?redirect=1")]
    [InlineData("ftp://os.tasia.work.gd:22041/")]
    public void RpcEndpointRejectsUnsafeOrUnsupportedRegionUris(string serverUri)
    {
        Assert.False(InternalScriptRpcConnector.TryGetRegionRpcUri(serverUri, UUID.Random(), 10000, out _));
    }

    [Fact]
    public void SmartNpcPackedStateSurvivesRpcEnvelopeRoundTrip()
    {
        UUID agent = UUID.Random();
        UUID owner = UUID.Random();
        UUID target = UUID.Random();
        var npc = new NpcAgentData { AgentID = agent, OwnerID = owner, TargetAgentID = target };
        var transfer = new NpcTransferRpcRequest
        {
            PackedNpcJson = OSDParser.SerializeJsonString(npc.Pack(new EntityTransferContext()))
        };
        JsonElement arguments = JsonSerializer.SerializeToElement(transfer);
        JsonElement payload = JsonSerializer.SerializeToElement(new { targetRegion = UUID.Random().ToString(), arguments });
        string requestBody = RpcSecurity.CreateRequest("npc.transfer", owner, payload,
            out string timestamp, out string nonce, out _);

        Assert.True(RpcSecurity.TryParseRequest(requestBody, out RpcRequest decoded));
        Assert.Equal(timestamp, decoded.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(nonce, decoded.Nonce);
        NpcTransferRpcRequest decodedTransfer = decoded.Payload.GetProperty("arguments").Deserialize<NpcTransferRpcRequest>()!;
        OSDMap packed = Assert.IsType<OSDMap>(OSDParser.DeserializeJson(decodedTransfer.PackedNpcJson));
        var restored = new NpcAgentData();
        Assert.True(restored.Unpack(packed));
        Assert.Equal(agent, restored.AgentID);
        Assert.Equal(owner, restored.OwnerID);
        Assert.Equal(target, restored.TargetAgentID);
    }
}
