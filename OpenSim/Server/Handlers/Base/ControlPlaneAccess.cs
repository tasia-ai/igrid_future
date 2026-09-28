/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 */

using System;
using System.Collections.Generic;
using System.Net;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;

namespace OpenSim.Server.Handlers.Base
{
    /// <summary>
    /// Fail-closed authorization for region and service control-plane requests.
    /// Public viewer and federation handlers must not use this for ordinary user traffic.
    /// </summary>
    public class ControlPlaneAccess
    {
        private static readonly ILog m_log = LogManager.GetLogger(typeof(ControlPlaneAccess));

        public const string JsonRpcRemoteAddressKey = "__opensim_remote_address";
        public const string JsonRpcLlHttpRequestKey = "__opensim_llhttprequest";

        private readonly HashSet<IPAddress> m_trustedHosts = new HashSet<IPAddress>();

        public ControlPlaneAccess(IConfigSource config)
        {
            AddTrustedAddress(IPAddress.Loopback);
            AddTrustedAddress(IPAddress.IPv6Loopback);

            string hosts = GetConfiguredHosts(config);
            foreach (string host in hosts.Split(new[] { ',', ';', '|', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                AddTrustedHost(host.Trim());

            AddConfiguredLocalHost(config);

            // Never log the configured addresses themselves; they can disclose deployment topology.
            m_log.InfoFormat("[CONTROL PLANE ACCESS]: Trusted control-plane address count: {0}", m_trustedHosts.Count);
        }

        public bool Authorize(IOSHttpRequest request, IOSHttpResponse response, HttpStatusCode blockedStatus = HttpStatusCode.Forbidden)
        {
            if (request.Headers["X-SecondLife-Shard"] != null)
            {
                m_log.WarnFormat("[CONTROL PLANE ACCESS]: Refusing {0} {1}: script HTTP marker is not allowed on control-plane endpoints",
                    request.HttpMethod, request.UriPath);
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                response.RawBuffer = Array.Empty<byte>();
                return false;
            }

            if (IsTrustedAddress(request.RemoteIPEndPoint.Address))
                return true;

            m_log.WarnFormat("[CONTROL PLANE ACCESS]: Refusing {0} {1} from {2}: source address is not trusted",
                request.HttpMethod, request.UriPath, request.RemoteIPEndPoint);
            response.StatusCode = (int)blockedStatus;
            response.RawBuffer = Array.Empty<byte>();
            return false;
        }

        public bool Authorize(IPEndPoint remoteClient, HttpStatusCode blockedStatus = HttpStatusCode.Forbidden)
        {
            if (remoteClient != null && IsTrustedAddress(remoteClient.Address))
                return true;

            return false;
        }

        public bool AuthorizeJsonRpc(OSDMap json, ref JsonRpcResponse response)
        {
            if (json.TryGetValue(JsonRpcLlHttpRequestKey, out OSD llHttpRequest) && llHttpRequest.AsBoolean())
                return MethodNotFound(ref response);

            if (!json.TryGetValue(JsonRpcRemoteAddressKey, out OSD remoteAddress) ||
                !IPAddress.TryParse(remoteAddress.AsString(), out IPAddress address))
                return MethodNotFound(ref response);

            if (IsTrustedAddress(address))
                return true;

            return MethodNotFound(ref response);
        }

        public bool IsTrustedAddress(IPAddress address)
        {
            if (address == null)
                return false;

            address = NormalizeAddress(address);
            return IPAddress.IsLoopback(address) || m_trustedHosts.Contains(address);
        }

        public bool AuthorizePrivilegedInstantMessage(byte dialog, IPEndPoint remoteClient)
        {
            if (!IsPrivilegedInstantMessageDialog(dialog))
                return true;

            return remoteClient != null && IsTrustedAddress(remoteClient.Address);
        }

        public static bool IsPrivilegedInstantMessageDialog(byte dialog)
        {
            return dialog == 250 || dialog == (byte)InstantMessageDialog.GodLikeRequestTeleport;
        }

        private static bool MethodNotFound(ref JsonRpcResponse response)
        {
            response.Error.Code = ErrorCode.MethodNotFound;
            response.Error.Message = "Method not found";
            return false;
        }

        private static string GetConfiguredHosts(IConfigSource config)
        {
            string[] sections = { "Security", "Network" };
            string[] keys = { "ControlPlaneTrustedHosts", "TrustedControlPlaneHosts" };

            foreach (string sectionName in sections)
            {
                IConfig section = config?.Configs[sectionName];
                if (section == null)
                    continue;

                foreach (string key in keys)
                {
                    string value = section.GetString(key, string.Empty);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            return string.Empty;
        }

        private void AddConfiguredLocalHost(IConfigSource config)
        {
            IConfig network = config?.Configs["Network"];
            if (network != null)
                AddTrustedHost(network.GetString("hostname", string.Empty));
        }

        private void AddTrustedHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return;

            if (Uri.TryCreate(host, UriKind.Absolute, out Uri uri))
                host = uri.Host;
            else if (host[0] == '[')
            {
                int endBracket = host.IndexOf(']');
                if (endBracket > 0)
                    host = host.Substring(1, endBracket - 1);
            }
            else
            {
                int colon = host.LastIndexOf(':');
                if (colon > 0 && host.IndexOf(':') == colon)
                    host = host.Substring(0, colon);
            }

            if (IPAddress.TryParse(host, out IPAddress address))
            {
                AddTrustedAddress(address);
                return;
            }

            try
            {
                foreach (IPAddress resolvedAddress in Dns.GetHostAddresses(host))
                    AddTrustedAddress(resolvedAddress);
            }
            catch
            {
                // An unresolvable configured hostname must not make the control plane permissive.
            }
        }

        private void AddTrustedAddress(IPAddress address)
        {
            if (address != null)
                m_trustedHosts.Add(NormalizeAddress(address));
        }

        private static IPAddress NormalizeAddress(IPAddress address)
        {
            return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        }
    }
}
