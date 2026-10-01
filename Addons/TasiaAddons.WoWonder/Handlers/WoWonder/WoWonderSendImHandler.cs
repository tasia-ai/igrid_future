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

namespace TasiaAddons.WoWonder
{
    internal sealed class WoWonderSendImHandler : BaseStreamHandler
    {
        private readonly WoWonderTokenStore m_tokenStore;
        private readonly IUserAccountService m_userAccounts;
        private readonly IInstantMessage m_imService;
        private readonly UUID m_scopeId;
        private readonly bool m_allowOffline;

        public WoWonderSendImHandler(WoWonderTokenStore tokenStore, IUserAccountService userAccounts, IInstantMessage imService, UUID scopeId, bool allowOffline)
            : base("POST", "/wowonder/send_im")
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

            if (!ScopeContains(token.Scope, "im", "im.write"))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.Forbidden;
                return Array.Empty<byte>();
            }

            using StreamReader reader = new(request, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            string json = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(json))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                return Array.Empty<byte>();
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (!TryResolveTarget(root, out UUID targetId))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                return JsonError(httpResponse, "target", "Target avatar not provided or unknown");
            }

            if (!root.TryGetProperty("message", out JsonElement messageElement) || string.IsNullOrWhiteSpace(messageElement.GetString()))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                return JsonError(httpResponse, "message", "Message cannot be empty");
            }

            string message = messageElement.GetString();
            string fromName = TryGetString(root, "from_name");
            bool offline = !root.TryGetProperty("offline", out JsonElement offlineElement) || offlineElement.ValueKind != JsonValueKind.False;
            if (!m_allowOffline)
                offline = false;

            byte dialog = 0;
            if (root.TryGetProperty("dialog", out JsonElement dialogElement) && dialogElement.ValueKind == JsonValueKind.Number)
            {
                if (dialogElement.TryGetByte(out byte parsedDialog))
                    dialog = parsedDialog;
                else if (dialogElement.TryGetInt32(out int dialogInt))
                    dialog = (byte)dialogInt;
            }

            bool fromGroup = root.TryGetProperty("from_group", out JsonElement fromGroupElement) && fromGroupElement.ValueKind == JsonValueKind.True;

            UUID sessionId = token.PrincipalId ^ targetId;
            if (root.TryGetProperty("session_id", out JsonElement sessionElement) && sessionElement.ValueKind == JsonValueKind.String && UUID.TryParse(sessionElement.GetString(), out UUID providedSession))
                sessionId = providedSession;

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

            if (string.IsNullOrWhiteSpace(fromName))
            {
                UserAccount account = m_userAccounts.GetUserAccount(m_scopeId, token.PrincipalId);
                fromName = account != null ? account.Name : token.PrincipalId.ToString();
            }

            GridInstantMessage gim = new()
            {
                fromAgentID = token.PrincipalId.Guid,
                fromAgentName = fromName,
                toAgentID = targetId.Guid,
                dialog = dialog,
                fromGroup = fromGroup,
                message = message,
                imSessionID = sessionId.Guid,
                offline = offline ? (byte)1 : (byte)0,
                Position = Vector3.Zero,
                binaryBucket = bucket,
                ParentEstateID = 0,
                RegionID = UUID.Zero.Guid,
                timestamp = (uint)Util.UnixTimeSinceEpoch()
            };

            bool delivered = m_imService.IncomingInstantMessage(gim);

            httpResponse.ContentType = "application/json";

            if (!delivered)
            {
                httpResponse.StatusCode = (int)HttpStatusCode.BadGateway;
                return JsonSerializer.SerializeToUtf8Bytes(new { status = "failed" });
            }

            httpResponse.StatusCode = (int)HttpStatusCode.OK;
            return JsonSerializer.SerializeToUtf8Bytes(new { status = "ok" });
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
            if (root.TryGetProperty("target", out JsonElement element) || root.TryGetProperty("target_id", out element))
            {
                if (element.ValueKind == JsonValueKind.String && UUID.TryParse(element.GetString(), out targetId))
                    return true;
            }

            if (root.TryGetProperty("target_name", out JsonElement nameElement) && nameElement.ValueKind == JsonValueKind.String)
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

        private static byte[] JsonError(IOSHttpResponse response, string field, string message)
        {
            response.ContentType = "application/json";
            return JsonSerializer.SerializeToUtf8Bytes(new { error = field, error_description = message });
        }
    }
}
