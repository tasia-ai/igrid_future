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
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using log4net;
using Nwc.XmlRpc;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace TasiaAddons.WoWonder
{
    internal sealed class WoWonderMoneyHandler : BaseStreamHandler
    {
        private static readonly ILog m_log = LogManager.GetLogger(typeof(WoWonderMoneyHandler));

        private readonly WoWonderTokenStore m_tokenStore;
        private readonly IUserAccountService m_userAccounts;
        private readonly IUserProfilesService m_profiles;
        private readonly string m_moneyServerUrl;
        private readonly int m_timeout;
        private readonly UUID m_scopeId;

        public WoWonderMoneyHandler(WoWonderTokenStore tokenStore, IUserAccountService userAccounts, IUserProfilesService profiles, string moneyServerUrl, int timeout, UUID scopeId)
            : base("GET", "/wowonder/money/balance")
        {
            m_tokenStore = tokenStore;
            m_userAccounts = userAccounts;
            m_profiles = profiles;
            m_moneyServerUrl = moneyServerUrl;
            m_timeout = timeout;
            m_scopeId = scopeId;
        }

        protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            if (!TryAuthorize(httpRequest, out WoWonderTokenStore.TokenRecord token))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.Unauthorized;
                return Array.Empty<byte>();
            }

            if (!ScopeContains(token.Scope, "balance"))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.Forbidden;
                return Array.Empty<byte>();
            }

            string sessionId = UUID.Random().ToString();

            try
            {
                Hashtable loginPayload = new()
                {
                    ["userID"] = token.PrincipalId.ToString(),
                    ["sessionID"] = sessionId
                };

                if (!SendXmlRpc("WebLogin", loginPayload, out Hashtable loginResponse) || !IsSuccess(loginResponse))
                {
                    httpResponse.StatusCode = (int)HttpStatusCode.BadGateway;
                    return Array.Empty<byte>();
                }

                Hashtable balancePayload = new()
                {
                    ["userID"] = token.PrincipalId.ToString(),
                    ["sessionID"] = sessionId
                };

                if (!SendXmlRpc("WebGetBalance", balancePayload, out Hashtable balanceResponse) || !IsSuccess(balanceResponse))
                {
                    httpResponse.StatusCode = (int)HttpStatusCode.BadGateway;
                    return Array.Empty<byte>();
                }

                int balance = 0;
                if (balanceResponse.ContainsKey("balance"))
                    balance = Convert.ToInt32(balanceResponse["balance"]);

                string userName = balanceResponse.ContainsKey("userName") ? balanceResponse["userName"].ToString() : string.Empty;

                SendXmlRpc("WebLogout", balancePayload, out _);

                string customerType = string.Empty;
                if (m_profiles != null)
                {
                    UserProfileProperties props = new() { UserId = token.PrincipalId };
                    string result = string.Empty;
                    if (m_profiles.AvatarPropertiesRequest(ref props, ref result))
                        customerType = ""; // customer_type not available in current OpenSim
                }

                httpResponse.StatusCode = (int)HttpStatusCode.OK;
                httpResponse.ContentType = "application/json";

                var payload = new
                {
                    user_id = token.PrincipalId.ToString(),
                    user_name = string.IsNullOrEmpty(userName) ? GetAccountName(token.PrincipalId) : userName,
                    balance,
                    customer_type = customerType
                };

                return JsonSerializer.SerializeToUtf8Bytes(payload);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[WOWONDER]: Error querying money balance: {0}", ex.Message);
                httpResponse.StatusCode = (int)HttpStatusCode.BadGateway;
                return Array.Empty<byte>();
            }
        }

        private string GetAccountName(UUID principalId)
        {
            UserAccount account = m_userAccounts.GetUserAccount(m_scopeId, principalId);
            return account != null ? account.Name : principalId.ToString();
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

        private bool SendXmlRpc(string method, Hashtable payload, out Hashtable response)
        {
            response = null;
            ArrayList parameters = new() { payload };
            XmlRpcRequest xmlRequest = new(method, parameters);

            try
            {
                using HttpClient client = WebUtil.GetNewGlobalHttpClient(m_timeout);
                XmlRpcResponse xmlResponse = xmlRequest.Send(m_moneyServerUrl, client);
                response = xmlResponse.Value as Hashtable;
                return response != null;
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("[WOWONDER]: Money server {0} call failed: {1}", method, ex.Message);
                return false;
            }
        }

        private static bool IsSuccess(Hashtable response)
        {
            if (response == null)
                return false;

            if (response.ContainsKey("success") && response["success"] is bool boolValue)
                return boolValue;

            if (response.ContainsKey("success"))
            {
                string successValue = response["success"].ToString();
                return successValue.Equals("true", StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        private static bool ScopeContains(string scope, string required)
        {
            if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(required))
                return false;

            foreach (string entry in scope.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(entry, required, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
