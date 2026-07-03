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
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;

namespace TasiaAddon.WoWonder
{
    internal sealed class WoWonderTokenStore
    {
        private readonly Dictionary<string, TokenRecord> m_accessTokens = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TokenRecord> m_refreshTokens = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AuthorizationCodeRecord> m_authorizationCodes = new(StringComparer.Ordinal);
        private readonly object m_lock = new();

        private readonly TimeSpan m_accessTokenLifetime;
        private readonly TimeSpan m_refreshTokenLifetime;
        private readonly TimeSpan m_authorizationCodeLifetime;

        public WoWonderTokenStore(TimeSpan accessTokenLifetime, TimeSpan refreshTokenLifetime, TimeSpan authorizationCodeLifetime)
        {
            m_accessTokenLifetime = accessTokenLifetime;
            m_refreshTokenLifetime = refreshTokenLifetime;
            m_authorizationCodeLifetime = authorizationCodeLifetime;
        }

        public TimeSpan AccessTokenLifetime => m_accessTokenLifetime;

        public TimeSpan RefreshTokenLifetime => m_refreshTokenLifetime;

        public TokenRecord Issue(UUID principalId, string clientId, string scope)
        {
            string normalizedScope = NormalizeScope(scope);

            TokenRecord record = new(principalId, clientId, normalizedScope, m_accessTokenLifetime, m_refreshTokenLifetime);

            lock (m_lock)
            {
                m_accessTokens[record.AccessToken] = record;
                if (!string.IsNullOrEmpty(record.RefreshToken))
                    m_refreshTokens[record.RefreshToken] = record;
            }

            return record;
        }

        public bool TryGetByAccessToken(string token, out TokenRecord record)
        {
            lock (m_lock)
            {
                if (!m_accessTokens.TryGetValue(token, out record))
                    return false;

                if (record.AccessExpires <= DateTimeOffset.UtcNow)
                {
                    Remove(record);
                    record = null;
                    return false;
                }

                return true;
            }
        }

        public TokenRecord Refresh(string refreshToken, string clientId, string scopeOverride)
        {
            lock (m_lock)
            {
                if (!m_refreshTokens.TryGetValue(refreshToken, out TokenRecord record))
                    return null;

                if (record.RefreshExpires <= DateTimeOffset.UtcNow)
                {
                    Remove(record);
                    return null;
                }

                if (!string.Equals(record.ClientId, clientId, StringComparison.Ordinal))
                    return null;

                string scope = string.IsNullOrEmpty(scopeOverride)
                    ? record.Scope
                    : NormalizeScope(scopeOverride, record.Scope);

                Remove(record);

                return Issue(record.PrincipalId, clientId, scope);
            }
        }

        public string CreateAuthorizationCode(UUID principalId, string clientId, string scope)
        {
            string normalizedScope = NormalizeScope(scope);
            AuthorizationCodeRecord record = new(principalId, clientId, normalizedScope, DateTimeOffset.UtcNow + m_authorizationCodeLifetime);
            string code = UUID.Random().ToString();

            lock (m_lock)
            {
                m_authorizationCodes[code] = record;
            }

            return code;
        }

        public bool TryRedeemAuthorizationCode(string code, string clientId, out UUID principalId, out string scope)
        {
            lock (m_lock)
            {
                principalId = UUID.Zero;
                scope = string.Empty;

                if (!m_authorizationCodes.TryGetValue(code, out AuthorizationCodeRecord record))
                    return false;

                m_authorizationCodes.Remove(code);

                if (!string.Equals(record.ClientId, clientId, StringComparison.Ordinal))
                    return false;

                if (record.Expires <= DateTimeOffset.UtcNow)
                    return false;

                principalId = record.PrincipalId;
                scope = record.Scope;
                return true;
            }
        }

        public void RevokeTokensForPrincipal(UUID principalId)
        {
            lock (m_lock)
            {
                foreach (TokenRecord token in m_accessTokens.Values.Where(t => t.PrincipalId == principalId).ToList())
                    Remove(token);
            }
        }

        private void Remove(TokenRecord record)
        {
            m_accessTokens.Remove(record.AccessToken);

            if (!string.IsNullOrEmpty(record.RefreshToken))
                m_refreshTokens.Remove(record.RefreshToken);
        }

        private static string NormalizeScope(string scope, string fallback = null)
        {
            HashSet<string> entries = new(StringComparer.OrdinalIgnoreCase);

            void add(string source)
            {
                if (string.IsNullOrWhiteSpace(source))
                    return;

                foreach (string part in source.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    entries.Add(part.ToLowerInvariant());
            }

            add(scope);
            add(fallback);

            return string.Join(' ', entries);
        }

        internal sealed class TokenRecord
        {
            public TokenRecord(UUID principalId, string clientId, string scope, TimeSpan accessLifetime, TimeSpan refreshLifetime)
            {
                PrincipalId = principalId;
                ClientId = clientId;
                Scope = scope;
                AccessToken = UUID.Random().ToString();
                RefreshToken = UUID.Random().ToString();
                AccessExpires = DateTimeOffset.UtcNow + accessLifetime;
                RefreshExpires = DateTimeOffset.UtcNow + refreshLifetime;
            }

            public UUID PrincipalId { get; }

            public string ClientId { get; }

            public string Scope { get; }

            public string AccessToken { get; }

            public string RefreshToken { get; }

            public DateTimeOffset AccessExpires { get; }

            public DateTimeOffset RefreshExpires { get; }
        }

        private sealed class AuthorizationCodeRecord
        {
            public AuthorizationCodeRecord(UUID principalId, string clientId, string scope, DateTimeOffset expires)
            {
                PrincipalId = principalId;
                ClientId = clientId;
                Scope = scope;
                Expires = expires;
            }

            public UUID PrincipalId { get; }

            public string ClientId { get; }

            public string Scope { get; }

            public DateTimeOffset Expires { get; }
        }
    }
}
