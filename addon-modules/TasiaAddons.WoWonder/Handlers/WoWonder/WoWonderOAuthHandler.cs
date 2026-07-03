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
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using log4net;
using OpenMetaverse;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;

namespace TasiaAddons.WoWonder
{
    internal sealed class WoWonderOAuthHandler : BaseStreamHandler
    {
        private static readonly ILog m_log = LogManager.GetLogger(typeof(WoWonderOAuthHandler));

        private readonly WoWonderTokenStore m_tokenStore;
        private readonly Dictionary<string, string> m_clients;
        private readonly IUserAccountService m_userAccounts;
        private readonly IAuthenticationService m_authentication;
        private readonly string m_defaultScope;
        private readonly UUID m_scopeId;
        private readonly string m_mode;
        private readonly string m_httpMethod;

        public WoWonderOAuthHandler(
            WoWonderTokenStore tokenStore,
            Dictionary<string, string> clients,
            IUserAccountService userAccounts,
            IAuthenticationService authentication,
            string defaultScope,
            UUID scopeId)
            : this(tokenStore, clients, userAccounts, authentication, defaultScope, scopeId, "/wowonder/oauth", "token", "POST")
        {
        }

        public WoWonderOAuthHandler(
            WoWonderTokenStore tokenStore,
            Dictionary<string, string> clients,
            IUserAccountService userAccounts,
            IAuthenticationService authentication,
            string defaultScope,
            UUID scopeId,
            string path,
            string mode,
            string httpMethod)
            : base(httpMethod, path)
        {
            m_tokenStore = tokenStore;
            m_clients = clients;
            m_userAccounts = userAccounts;
            m_authentication = authentication;
            m_defaultScope = defaultScope;
            m_scopeId = scopeId;
            m_mode = mode ?? "auto";
            m_httpMethod = string.IsNullOrWhiteSpace(httpMethod) ? "POST" : httpMethod;
        }

        public override string HttpMethod => m_httpMethod;

        protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            if (string.Equals(m_mode, "token", StringComparison.OrdinalIgnoreCase))
                return HandleToken(request, httpRequest, httpResponse);

            if (string.Equals(m_mode, "authorize", StringComparison.OrdinalIgnoreCase))
                return HandleAuthorize(httpRequest, httpResponse);

            string[] segments = SplitParams(path);
            if (segments.Length == 0)
            {
                if (httpRequest.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    return HandleToken(request, httpRequest, httpResponse);

                if (httpRequest.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                    return HandleAuthorize(httpRequest, httpResponse);

                httpResponse.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return Array.Empty<byte>();
            }

            string action = segments[0].ToLowerInvariant();

            return action switch
            {
                "token" => HandleToken(request, httpRequest, httpResponse),
                "authorize" => HandleAuthorize(httpRequest, httpResponse),
                _ => HandleNotFound(httpResponse)
            };
        }

        private byte[] HandleNotFound(IOSHttpResponse httpResponse)
        {
            httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
            return Array.Empty<byte>();
        }

        private byte[] HandleAuthorize(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            Dictionary<string, string> query = httpRequest.QueryAsDictionary ?? new Dictionary<string, string>();
            if (!query.TryGetValue("client_id", out string clientId) || !m_clients.ContainsKey(clientId))
            {
                return WriteAuthorizeError(httpResponse, query, "unauthorized_client", "Unknown client");
            }

            if (!query.TryGetValue("response_type", out string responseType) || !responseType.Equals("code", StringComparison.OrdinalIgnoreCase))
            {
                return WriteAuthorizeError(httpResponse, query, "unsupported_response_type", "Only the authorization code flow is supported.");
            }

            if (!TryParseBasicCredentials(httpRequest, out string username, out string password))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.Unauthorized;
                httpResponse.AddHeader("WWW-Authenticate", "Basic realm=\"WoWonder OAuth\"");
                return Array.Empty<byte>();
            }

            if (!TryResolveUser(username, out UserAccount account))
            {
                return WriteAuthorizeError(httpResponse, query, "access_denied", "User not found");
            }

            if (!ValidatePassword(account.PrincipalID, password))
            {
                return WriteAuthorizeError(httpResponse, query, "access_denied", "Invalid credentials");
            }

            string scope = query.TryGetValue("scope", out string requestedScope) ? requestedScope : m_defaultScope;
            string code = m_tokenStore.CreateAuthorizationCode(account.PrincipalID, clientId, scope);

            string redirectUri = query.TryGetValue("redirect_uri", out string ru) ? ru : string.Empty;
            string state = query.TryGetValue("state", out string st) ? st : string.Empty;

            if (!string.IsNullOrEmpty(redirectUri))
            {
                string separator = redirectUri.Contains('?') ? "&" : "?";
                string location = string.Concat(redirectUri, separator, "code=", code);
                if (!string.IsNullOrEmpty(state))
                    location = string.Concat(location, "&state=", Uri.EscapeDataString(state));

                httpResponse.StatusCode = (int)HttpStatusCode.Found;
                httpResponse.AddHeader("Location", location);
                return Array.Empty<byte>();
            }

            httpResponse.StatusCode = (int)HttpStatusCode.OK;
            httpResponse.ContentType = "application/json";
            var payload = new Dictionary<string, string>
            {
                ["code"] = code,
                ["state"] = state
            };
            return JsonSerializer.SerializeToUtf8Bytes(payload);
        }

        private byte[] WriteAuthorizeError(IOSHttpResponse httpResponse, Dictionary<string, string> query, string error, string description)
        {
            if (query.TryGetValue("redirect_uri", out string redirect) && !string.IsNullOrEmpty(redirect))
            {
                string separator = redirect.Contains('?') ? "&" : "?";
                string location = string.Concat(redirect, separator, "error=", Uri.EscapeDataString(error));
                if (query.TryGetValue("state", out string state) && !string.IsNullOrEmpty(state))
                    location = string.Concat(location, "&state=", Uri.EscapeDataString(state));

                if (!string.IsNullOrEmpty(description))
                    location = string.Concat(location, "&error_description=", Uri.EscapeDataString(description));

                httpResponse.StatusCode = (int)HttpStatusCode.Found;
                httpResponse.AddHeader("Location", location);
                return Array.Empty<byte>();
            }

            httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
            httpResponse.ContentType = "application/json";
            var payload = new Dictionary<string, string>
            {
                ["error"] = error,
                ["error_description"] = description ?? string.Empty
            };
            return JsonSerializer.SerializeToUtf8Bytes(payload);
        }

        private byte[] HandleToken(Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            if (!httpRequest.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                httpResponse.ContentType = "application/json";
                return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
                {
                    ["error"] = "invalid_request",
                    ["error_description"] = "Use POST for token requests."
                });
            }

            Dictionary<string, string> body = ParseBody(request, httpRequest);

            if (!TryParseClientCredentials(httpRequest, body, out string clientId, out string clientSecret))
            {
                return WriteTokenError(httpResponse, HttpStatusCode.Unauthorized, "invalid_client", "Missing client credentials");
            }

            if (!ValidateClient(clientId, clientSecret))
            {
                return WriteTokenError(httpResponse, HttpStatusCode.Unauthorized, "invalid_client", "Invalid client credentials");
            }

            if (!body.TryGetValue("grant_type", out string grantType))
            {
                return WriteTokenError(httpResponse, HttpStatusCode.BadRequest, "invalid_request", "grant_type missing");
            }

            return grantType switch
            {
                "password" => HandlePasswordGrant(body, httpResponse, clientId),
                "refresh_token" => HandleRefreshGrant(body, httpResponse, clientId),
                "authorization_code" => HandleAuthorizationCodeGrant(body, httpResponse, clientId),
                _ => WriteTokenError(httpResponse, HttpStatusCode.BadRequest, "unsupported_grant_type", "Grant type not supported")
            };
        }

        private byte[] HandlePasswordGrant(Dictionary<string, string> body, IOSHttpResponse httpResponse, string clientId)
        {
            if (!body.TryGetValue("username", out string username) || !body.TryGetValue("password", out string password))
                return WriteTokenError(httpResponse, HttpStatusCode.BadRequest, "invalid_request", "username/password required");

            if (!TryResolveUser(username, out UserAccount account))
                return WriteTokenError(httpResponse, HttpStatusCode.BadRequest, "invalid_grant", "Unknown user");

            if (!ValidatePassword(account.PrincipalID, password))
                return WriteTokenError(httpResponse, HttpStatusCode.BadRequest, "invalid_grant", "Invalid credentials");

            string scope = body.TryGetValue("scope", out string requestedScope) && !string.IsNullOrWhiteSpace(requestedScope)
                ? requestedScope
                : m_defaultScope;

            WoWonderTokenStore.TokenRecord token = m_tokenStore.Issue(account.PrincipalID, clientId, scope);
            return WriteTokenResponse(httpResponse, token);
        }

        private byte[] HandleRefreshGrant(Dictionary<string, string> body, IOSHttpResponse httpResponse, string clientId)
        {
            if (!body.TryGetValue("refresh_token", out string refreshToken))
                return WriteTokenError(httpResponse, HttpStatusCode.BadRequest, "invalid_request", "refresh_token missing");

            string scopeOverride = body.TryGetValue("scope", out string requestedScope) ? requestedScope : null;

            WoWonderTokenStore.TokenRecord token = m_tokenStore.Refresh(refreshToken, clientId, scopeOverride);
            if (token == null)
                return WriteTokenError(httpResponse, HttpStatusCode.BadRequest, "invalid_grant", "Invalid refresh token");

            return WriteTokenResponse(httpResponse, token);
        }

        private byte[] HandleAuthorizationCodeGrant(Dictionary<string, string> body, IOSHttpResponse httpResponse, string clientId)
        {
            if (!body.TryGetValue("code", out string code))
                return WriteTokenError(httpResponse, HttpStatusCode.BadRequest, "invalid_request", "code missing");

            if (!m_tokenStore.TryRedeemAuthorizationCode(code, clientId, out UUID principalId, out string scope))
                return WriteTokenError(httpResponse, HttpStatusCode.BadRequest, "invalid_grant", "Invalid authorization code");

            WoWonderTokenStore.TokenRecord token = m_tokenStore.Issue(principalId, clientId, scope);
            return WriteTokenResponse(httpResponse, token);
        }

        private byte[] WriteTokenResponse(IOSHttpResponse httpResponse, WoWonderTokenStore.TokenRecord token)
        {
            httpResponse.StatusCode = (int)HttpStatusCode.OK;
            httpResponse.ContentType = "application/json";

            var payload = new Dictionary<string, object>
            {
                ["access_token"] = token.AccessToken,
                ["token_type"] = "Bearer",
                ["expires_in"] = (int)Math.Round((token.AccessExpires - DateTimeOffset.UtcNow).TotalSeconds),
                ["refresh_token"] = token.RefreshToken,
                ["scope"] = token.Scope
            };

            return JsonSerializer.SerializeToUtf8Bytes(payload);
        }

        private byte[] WriteTokenError(IOSHttpResponse httpResponse, HttpStatusCode status, string error, string description)
        {
            httpResponse.StatusCode = (int)status;
            httpResponse.ContentType = "application/json";
            var payload = new Dictionary<string, string>
            {
                ["error"] = error,
                ["error_description"] = description ?? string.Empty
            };
            return JsonSerializer.SerializeToUtf8Bytes(payload);
        }

        private bool TryParseClientCredentials(IOSHttpRequest request, Dictionary<string, string> body, out string clientId, out string clientSecret)
        {
            clientId = string.Empty;
            clientSecret = string.Empty;

            string header = request.Headers["Authorization"] ?? request.Headers["authorization"];
            if (!string.IsNullOrEmpty(header) && header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string encoded = header.Substring(6).Trim();
                    string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                    int idx = decoded.IndexOf(':');
                    if (idx >= 0)
                    {
                        clientId = decoded[..idx];
                        clientSecret = decoded[(idx + 1)..];
                        return true;
                    }
                }
                catch (FormatException ex)
                {
                    m_log.WarnFormat("[WOWONDER]: Failed to parse client credentials: {0}", ex.Message);
                }
            }

            if (body.TryGetValue("client_id", out clientId) && body.TryGetValue("client_secret", out clientSecret))
                return true;

            return false;
        }

        private bool TryParseBasicCredentials(IOSHttpRequest request, out string username, out string password)
        {
            username = string.Empty;
            password = string.Empty;

            string header = request.Headers["Authorization"] ?? request.Headers["authorization"];
            if (string.IsNullOrEmpty(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                string encoded = header.Substring(6).Trim();
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                int idx = decoded.IndexOf(':');
                if (idx < 0)
                    return false;

                username = decoded[..idx];
                password = decoded[(idx + 1)..];
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool ValidateClient(string clientId, string clientSecret)
        {
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
                return false;

            if (!m_clients.TryGetValue(clientId, out string storedSecret))
                return false;

            return storedSecret.Equals(clientSecret, StringComparison.Ordinal);
        }

        private Dictionary<string, string> ParseBody(Stream request, IOSHttpRequest httpRequest)
        {
            using StreamReader reader = new(request, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            string text = reader.ReadToEnd();

            Dictionary<string, string> result = new(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(text))
                return result;

            string contentType = httpRequest.ContentType ?? string.Empty;
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                using JsonDocument doc = JsonDocument.Parse(text);
                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                    result[prop.Name] = prop.Value.GetString();
            }
            else
            {
                foreach (KeyValuePair<string, object> kv in ServerUtils.ParseQueryString(text))
                    result[kv.Key] = kv.Value?.ToString();
            }

            return result;
        }

        private bool TryResolveUser(string identifier, out UserAccount account)
        {
            account = null;
            if (string.IsNullOrWhiteSpace(identifier))
                return false;

            identifier = identifier.Trim();
            if (UUID.TryParse(identifier, out UUID principalId))
            {
                account = m_userAccounts.GetUserAccount(m_scopeId, principalId);
                return account != null;
            }

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

            account = m_userAccounts.GetUserAccount(m_scopeId, first, last);
            return account != null;
        }

        private bool ValidatePassword(UUID principalId, string password)
        {
            string token = m_authentication.Authenticate(principalId, password, 60);
            if (string.IsNullOrEmpty(token))
                return false;

            m_authentication.Release(principalId, token);
            return true;
        }
    }
}
