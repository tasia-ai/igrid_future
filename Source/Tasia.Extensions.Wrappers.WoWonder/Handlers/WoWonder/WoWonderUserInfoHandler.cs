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
using System.Net;
using System.Text.Json;
using UUID = OpenMetaverse.UUID;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace TasiaAddon.WoWonder
{
    internal sealed class WoWonderUserInfoHandler : BaseStreamHandler
    {
        private readonly WoWonderTokenStore m_tokenStore;
        private readonly IUserAccountService m_userAccounts;
        private readonly IUserProfilesService m_profiles;
        private readonly UUID m_scopeId;

        public WoWonderUserInfoHandler(WoWonderTokenStore tokenStore, IUserAccountService userAccounts, IUserProfilesService profiles, UUID scopeId)
            : base("GET", "/wowonder/oauth/userinfo")
        {
            m_tokenStore = tokenStore;
            m_userAccounts = userAccounts;
            m_profiles = profiles;
            m_scopeId = scopeId;
        }

        protected override byte[] ProcessRequest(string path, System.IO.Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            if (!TryAuthorize(httpRequest, out WoWonderTokenStore.TokenRecord token))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.Unauthorized;
                return Array.Empty<byte>();
            }

            if (!ScopeContains(token.Scope, "profile"))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.Forbidden;
                return Array.Empty<byte>();
            }

            UserAccount account = m_userAccounts.GetUserAccount(m_scopeId, token.PrincipalId);
            if (account == null)
            {
                httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
                return Array.Empty<byte>();
            }

            string customerType = string.Empty;
            if (m_profiles != null)
            {
                UserProfileProperties props = new() { UserId = token.PrincipalId };
                string result = string.Empty;
                if (m_profiles.AvatarPropertiesRequest(ref props, ref result))
                    customerType = props.customer_type ?? string.Empty;
            }

            var payload = new
            {
                id = account.PrincipalID.ToString(),
                username = account.Name,
                display_name = account.DisplayName ?? account.Name,
                email = account.Email ?? string.Empty,
                scope = token.Scope,
                customer_type = customerType
            };

            httpResponse.StatusCode = (int)HttpStatusCode.OK;
            httpResponse.ContentType = "application/json";
            return JsonSerializer.SerializeToUtf8Bytes(payload);
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
