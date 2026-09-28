using System.Net;
using Nini.Config;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;
using OpenSim.Tests.Common;

namespace OpenSim.Server.Handlers.Tests
{
    [TestFixture]
    public class ControlPlaneAccessTests
    {
        [Test]
        public void LoopbackIsTrustedAndUnconfiguredHostsAreDenied()
        {
            var access = new ControlPlaneAccess(new IniConfigSource());

            Assert.That(access.IsTrustedAddress(IPAddress.Loopback), Is.True);
            Assert.That(access.IsTrustedAddress(IPAddress.IPv6Loopback), Is.True);
            Assert.That(access.Authorize(new IPEndPoint(IPAddress.Parse("203.0.113.10"), 9000)), Is.False);
        }

        [Test]
        public void ExplicitTrustedHostAndLegacyKeyAreAccepted()
        {
            var config = new IniConfigSource();
            config.AddConfig("Network");
            config.Configs["Network"].Set("ControlPlaneTrustedHosts", "203.0.113.10");

            var access = new ControlPlaneAccess(config);
            Assert.That(access.Authorize(new IPEndPoint(IPAddress.Parse("203.0.113.10"), 9000)), Is.True);
            Assert.That(access.Authorize(new IPEndPoint(IPAddress.Parse("203.0.113.11"), 9000)), Is.False);

            var legacy = new IniConfigSource();
            legacy.AddConfig("Security");
            legacy.Configs["Security"].Set("TrustedControlPlaneHosts", "203.0.113.12");
            Assert.That(new ControlPlaneAccess(legacy).Authorize(new IPEndPoint(IPAddress.Parse("203.0.113.12"), 9000)), Is.True);
        }

        [Test]
        public void MappedIpv6AddressIsNormalized()
        {
            var access = new ControlPlaneAccess(new IniConfigSource());

            Assert.That(access.IsTrustedAddress(IPAddress.Parse("::ffff:127.0.0.1")), Is.True);
        }

        [Test]
        public void ScriptHttpMarkerIsRejectedEvenFromTrustedPeer()
        {
            var request = new TestOSHttpRequest
            {
                HttpMethod = "POST",
                UriPath = "/agent/test",
                RemoteIPEndPoint = new IPEndPoint(IPAddress.Loopback, 9000)
            };
            request.Headers["X-SecondLife-Shard"] = "OpenSim";
            var response = new TestOSHttpResponse();

            Assert.That(new ControlPlaneAccess(new IniConfigSource()).Authorize(request, response), Is.False);
            Assert.That(response.StatusCode, Is.EqualTo((int)HttpStatusCode.Forbidden));
            Assert.That(response.RawBuffer, Is.Empty);
        }

        [Test]
        public void JsonRpcRequiresServerPopulatedTrustedAddress()
        {
            var access = new ControlPlaneAccess(new IniConfigSource());
            var response = new JsonRpcResponse();
            var request = new OSDMap
            {
                [ControlPlaneAccess.JsonRpcRemoteAddressKey] = OSD.FromString("203.0.113.10")
            };

            Assert.That(access.AuthorizeJsonRpc(request, ref response), Is.False);
            Assert.That(response.Error.Code, Is.EqualTo(ErrorCode.MethodNotFound));

            response = new JsonRpcResponse();
            request[ControlPlaneAccess.JsonRpcRemoteAddressKey] = OSD.FromString("127.0.0.1");
            request[ControlPlaneAccess.JsonRpcLlHttpRequestKey] = OSD.FromBoolean(false);
            Assert.That(access.AuthorizeJsonRpc(request, ref response), Is.True);
        }

        [Test]
        public void PrivilegedInstantMessagesRequireTrustedPeer()
        {
            var config = new IniConfigSource();
            config.AddConfig("Network");
            config.Configs["Network"].Set("ControlPlaneTrustedHosts", "203.0.113.10");
            var access = new ControlPlaneAccess(config);
            var untrusted = new IPEndPoint(IPAddress.Parse("203.0.113.11"), 9000);
            var trusted = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 9000);

            Assert.That(access.AuthorizePrivilegedInstantMessage(0, untrusted), Is.True);
            Assert.That(access.AuthorizePrivilegedInstantMessage(250, untrusted), Is.False);
            Assert.That(access.AuthorizePrivilegedInstantMessage(250, trusted), Is.True);
            Assert.That(access.AuthorizePrivilegedInstantMessage((byte)InstantMessageDialog.GodLikeRequestTeleport, untrusted), Is.False);
        }
    }
}
