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
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;

namespace TasiaAddon.WoWonder
{
    public class WoWonderServiceConnector : ServiceConnector
    {
        private static readonly ILog m_log = LogManager.GetLogger(typeof(WoWonderServiceConnector));

        public WoWonderServiceConnector(IConfigSource config, IHttpServer server, string configName)
            : base(config, server, configName)
        {
            IConfig moduleConfig = config.Configs["WoWonderService"];
            if (moduleConfig == null)
            {
                m_log.Info("[WOWONDER]: WoWonderService section not found; skipping connector initialisation.");
                return;
            }

            if (!moduleConfig.GetBoolean("Enabled", false))
            {
                m_log.Info("[WOWONDER]: WoWonderService disabled in configuration.");
                return;
            }

            Dictionary<string, string> clients = ParseClients(moduleConfig.GetString("AllowedClients", string.Empty));
            if (clients.Count == 0)
            {
                m_log.Warn("[WOWONDER]: AllowedClients is empty; WoWonder endpoints will not start.");
                return;
            }

            UUID scopeId = UUID.Zero;
            UUID.TryParse(moduleConfig.GetString("ScopeID", UUID.Zero.ToString()), out scopeId);

            TimeSpan accessLifetime = TimeSpan.FromSeconds(Math.Max(30, moduleConfig.GetInt("AccessTokenLifetime", 3600)));
            TimeSpan refreshLifetime = TimeSpan.FromSeconds(Math.Max(60, moduleConfig.GetInt("RefreshTokenLifetime", 604800)));
            TimeSpan codeLifetime = TimeSpan.FromSeconds(Math.Max(30, moduleConfig.GetInt("AuthorizationCodeLifetime", 300)));

            WoWonderTokenStore tokenStore = new(accessLifetime, refreshLifetime, codeLifetime);

            object[] args = [ config ];

            string userAccountService = moduleConfig.GetString("UserAccountService", string.Empty);
            string authenticationService = moduleConfig.GetString("AuthenticationService", string.Empty);
            string userProfilesService = moduleConfig.GetString("UserProfilesService", string.Empty);
            string instantMessageService = moduleConfig.GetString("InstantMessageService", string.Empty);

            IUserAccountService accounts = LoadService<IUserAccountService>(userAccountService, args, "UserAccountService");
            IAuthenticationService auth = LoadService<IAuthenticationService>(authenticationService, args, "AuthenticationService");
            IUserProfilesService profiles = LoadService<IUserProfilesService>(userProfilesService, args, "UserProfilesService", false);
            IInstantMessage imService = LoadService<IInstantMessage>(instantMessageService, args, "InstantMessageService", false);

            if (accounts == null || auth == null)
            {
                m_log.Error("[WOWONDER]: Required services missing - WoWonder endpoints disabled.");
                return;
            }

            string defaultScope = moduleConfig.GetString("DefaultScope", "im im.write profile balance");

            WoWonderOAuthHandler oauthHandler = new(
                tokenStore,
                clients,
                accounts,
                auth,
                defaultScope,
                scopeId);

            server.AddStreamHandler(oauthHandler);

            if (imService != null)
            {
                bool allowOffline = moduleConfig.GetBoolean("AllowOfflineDelivery", true);
                server.AddStreamHandler(new WoWonderSendImHandler(tokenStore, accounts, imService, scopeId, allowOffline));
                server.AddStreamHandler(new InstantMessageSendApiHandler(tokenStore, accounts, imService, scopeId, allowOffline));
            }
            else
            {
                m_log.Warn("[WOWONDER]: InstantMessageService not configured; send_im endpoint disabled.");
            }

            string moneyServerUrl = moduleConfig.GetString("MoneyServerUrl", string.Empty);
            int moneyTimeout = moduleConfig.GetInt("MoneyServerTimeout", 10000);

            if (!string.IsNullOrEmpty(moneyServerUrl))
            {
                server.AddStreamHandler(new WoWonderMoneyHandler(tokenStore, accounts, profiles, moneyServerUrl, moneyTimeout, scopeId));
            }
            else
            {
                m_log.Warn("[WOWONDER]: MoneyServerUrl missing; money_balance endpoint disabled.");
            }

            server.AddStreamHandler(new WoWonderUserInfoHandler(tokenStore, accounts, profiles, scopeId));

            m_log.Info("[WOWONDER]: WoWonder endpoints registered.");
        }

        private static Dictionary<string, string> ParseClients(string configValue)
        {
            Dictionary<string, string> clients = new(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(configValue))
                return clients;

            string[] entries = configValue.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string entry in entries)
            {
                string[] parts = entry.Split(new[] { ':' }, 2);
                if (parts.Length != 2)
                    continue;

                string clientId = parts[0].Trim();
                string secret = parts[1].Trim();

                if (clientId.Length == 0 || secret.Length == 0)
                    continue;

                clients[clientId] = secret;
            }

            return clients;
        }

        private static T LoadService<T>(string setting, object[] args, string name, bool required = true) where T : class
        {
            if (string.IsNullOrEmpty(setting))
            {
                if (required)
                    LogManager.GetLogger(typeof(WoWonderServiceConnector)).ErrorFormat("[WOWONDER]: {0} not configured.", name);
                return null;
            }

            try
            {
                return ServerUtils.LoadPlugin<T>(setting, args);
            }
            catch (Exception ex)
            {
                ILog log = LogManager.GetLogger(typeof(WoWonderServiceConnector));
                log.ErrorFormat("[WOWONDER]: Failed to load {0} '{1}': {2}", name, setting, ex.Message);
                return null;
            }
        }
    }
}
