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
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace TasiaAddon.WoWonder
{
    internal sealed class InstantMessageSendApiHandler : BaseStreamHandler
    {
        private readonly WoWonderTokenStore m_tokenStore;
        private readonly IUserAccountService m_userAccounts;
        private readonly IInstantMessage m_imService;
        private readonly UUID m_scopeId;
        private readonly bool m_allowOffline;

        public InstantMessageSendApiHandler(
            WoWonderTokenStore tokenStore,
            IUserAccountService userAccounts,
            IInstantMessage imService,
            UUID scopeId,
            bool allowOffline)
            : base("POST", "/api/v1/im/send")
        {
            m_tokenStore = tokenStore;
            m_userAccounts = userAccounts;
            m_imService = imService;
            m_scopeId = scopeId;
            m_allowOffline = allowOffline;
        }

        protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            if (!httpRequest.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return Array.Empty<byte>();
            }

            if (!TryAuthorize(httpRequest, out WoWonderTokenStore.TokenRecord token))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.Unauthorized;
                return Array.Empty<byte>();
            }

            if (!ScopeContains(token.Scope, "im.write", "im"))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.Forbidden;
                return JsonError(httpResponse, "scope", "im.write scope required");
            }

            string json;
            using (StreamReader reader = new(request, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
            {
                json = reader.ReadToEnd();
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                return JsonError(httpResponse, "body", "Request body missing");
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                return JsonError(httpResponse, "body", "Malformed JSON payload");
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;

                if (!TryGetUuid(root, "from_agent_id", out UUID fromAgentId))
                {
                    httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                    return JsonError(httpResponse, "from_agent_id", "from_agent_id is required");
                }

                if (fromAgentId != token.PrincipalId)
                {
                    httpResponse.StatusCode = (int)HttpStatusCode.Forbidden;
                    return JsonError(httpResponse, "from_agent_id", "Token subject mismatch");
                }

                if (!TryResolveTarget(root, out UUID toAgentId))
                {
                    httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                    return JsonError(httpResponse, "to_agent_id", "Target avatar not provided or unknown");
                }

                if (!root.TryGetProperty("message", out JsonElement messageElement) || string.IsNullOrWhiteSpace(messageElement.GetString()))
                {
                    httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                    return JsonError(httpResponse, "message", "Message cannot be empty");
                }

                string message = messageElement.GetString();
                string fromName = TryGetString(root, "from_agent_name");
                bool fromGroup = root.TryGetProperty("from_group", out JsonElement fromGroupElement) && fromGroupElement.ValueKind == JsonValueKind.True;

                byte dialog = 0;
                if (root.TryGetProperty("dialog", out JsonElement dialogElement) && dialogElement.ValueKind == JsonValueKind.Number)
                {
                    if (dialogElement.TryGetByte(out byte parsedDialog))
                        dialog = parsedDialog;
                    else if (dialogElement.TryGetInt32(out int dialogInt))
                        dialog = (byte)dialogInt;
                }

                UUID sessionId = UUID.Zero;
                if (TryGetUuid(root, "session_id", out UUID providedSession))
                    sessionId = providedSession;

                UUID regionId = UUID.Zero;
                if (TryGetUuid(root, "region_id", out UUID providedRegion))
                    regionId = providedRegion;

                Vector3 position = Vector3.Zero;
                if (root.TryGetProperty("position", out JsonElement positionElement) && positionElement.ValueKind == JsonValueKind.Object)
                {
                    float x = TryGetFloat(positionElement, "x");
                    float y = TryGetFloat(positionElement, "y");
                    float z = TryGetFloat(positionElement, "z");
                    position = new Vector3(x, y, z);
                }

                byte[] bucket = Array.Empty<byte>();
                if (root.TryGetProperty("binary_bucket", out JsonElement bucketElement) && bucketElement.ValueKind == JsonValueKind.String)
                {
                    string bucketString = bucketElement.GetString();
                    if (!string.IsNullOrEmpty(bucketString))
                    {
                        try
                        {
                            bucket = Convert.FromBase64String(bucketString);
                        }
                        catch
                        {
                            bucket = Array.Empty<byte>();
                        }
                    }
                }

                bool offlineRequested = false;
                if (m_allowOffline && root.TryGetProperty("offline", out JsonElement offlineElement))
                    offlineRequested = offlineElement.ValueKind == JsonValueKind.True;

                if (string.IsNullOrWhiteSpace(fromName))
                {
                    UserAccount account = m_userAccounts.GetUserAccount(m_scopeId, token.PrincipalId);
                    fromName = account != null ? account.Name : token.PrincipalId.ToString();
                }

                GridInstantMessage gim = new()
                {
                    fromAgentID = fromAgentId.Guid,
                    fromAgentName = fromName,
                    fromGroup = fromGroup,
                    toAgentID = toAgentId.Guid,
                    dialog = dialog,
                    message = message,
                    imSessionID = sessionId.Guid,
                    Position = position,
                    RegionID = regionId.Guid,
                    binaryBucket = bucket,
                    ParentEstateID = 0,
                    offline = offlineRequested ? (byte)1 : (byte)0,
                    timestamp = (uint)Util.UnixTimeSinceEpoch()
                };

                bool delivered = m_imService.IncomingInstantMessage(gim);
                bool offlineDelivery = offlineRequested && !delivered;

                httpResponse.ContentType = "application/json";
                httpResponse.StatusCode = delivered || offlineDelivery
                    ? (int)HttpStatusCode.OK
                    : (int)HttpStatusCode.BadGateway;

                var payload = new
                {
                    delivered,
                    offline = offlineDelivery,
                    timestamp = Util.UnixTimeSinceEpoch()
                };

                return JsonSerializer.SerializeToUtf8Bytes(payload);
            }
        }

        private bool TryAuthorize(IOSHttpRequest httpRequest, out WoWonderTokenStore.TokenRecord token)
        {
            token = null;
            string header = httpRequest.Headers["Authorization"] ?? httpRequest.Headers["authorization"];
            if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return false;

            string accessToken = header.Substring(7).Trim();
            if (string.IsNullOrEmpty(accessToken))
                return false;

            return m_tokenStore.TryGetByAccessToken(accessToken, out token);
        }

        private bool TryResolveTarget(JsonElement root, out UUID targetId)
        {
            targetId = UUID.Zero;
            if (TryGetUuid(root, "to_agent_id", out UUID toAgentId))
            {
                targetId = toAgentId;
                return true;
            }

            if (root.TryGetProperty("to_agent_name", out JsonElement nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                string identifier = nameElement.GetString();
                if (!string.IsNullOrWhiteSpace(identifier))
                {
                    if (UUID.TryParse(identifier, out targetId))
                        return true;

                    string first = identifier;
                    string last = "Resident";
                    if (identifier.Contains('.'))
                    {
                        string[] parts = identifier.Split('.', 2);
                        first = parts[0];
                        last = parts[1];
                    }
                    else if (identifier.Contains(' '))
                    {
                        string[] parts = identifier.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        first = parts[0];
                        last = parts.Length > 1 ? parts[1] : "Resident";
                    }

                    UserAccount account = m_userAccounts.GetUserAccount(m_scopeId, first, last);
                    if (account != null)
                    {
                        targetId = account.PrincipalID;
                        return true;
                    }
                }
            }

            return false;
        }

        private static string TryGetString(JsonElement root, string property)
        {
            if (root.TryGetProperty(property, out JsonElement element) && element.ValueKind == JsonValueKind.String)
                return element.GetString();

            return string.Empty;
        }

        private static bool TryGetUuid(JsonElement root, string property, out UUID id)
        {
            id = UUID.Zero;
            if (root.TryGetProperty(property, out JsonElement element) && element.ValueKind == JsonValueKind.String)
                return UUID.TryParse(element.GetString(), out id);

            return false;
        }

        private static float TryGetFloat(JsonElement root, string property)
        {
            if (root.TryGetProperty(property, out JsonElement element) && element.ValueKind == JsonValueKind.Number)
            {
                if (element.TryGetSingle(out float value))
                    return value;

                if (element.TryGetDouble(out double doubleValue))
                    return (float)doubleValue;
            }

            return 0f;
        }

        private static bool ScopeContains(string scope, params string[] required)
        {
            if (string.IsNullOrEmpty(scope) || required == null || required.Length == 0)
                return false;

            foreach (string entry in scope.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string req in required)
                {
                    if (string.Equals(entry, req, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private static byte[] JsonError(IOSHttpResponse response, string field, string error)
        {
            response.ContentType = "application/json";
            var payload = new
            {
                error,
                field
            };
            return JsonSerializer.SerializeToUtf8Bytes(payload);
        }
    }
}
