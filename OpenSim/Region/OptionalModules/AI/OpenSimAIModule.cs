/*
* MIT License
*
* Copyright (c) 2026 Adil El Farissi @ https://github.com/AdilElFarissi
*
* Permission is hereby granted, free of charge, to any person obtaining a copy
* of this software and associated documentation files (the "Software"), to deal
* in the Software without restriction, including without limitation the rights
* to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
* copies of the Software, and to permit persons to whom the Software is
* furnished to do so, subject to the following conditions:
*
* The above copyright notice, this permission notice and the "Credits" variable (see the * end of code) shall be included in all copies or substantial portions of the Software.
*
* THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
* IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
* FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
* AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
* LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
* OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
* SOFTWARE.
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Mono.Addins;
using Npgsql;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

[assembly: Addin("OpenSimAIModule", "1.0")]
[assembly: AddinDependency("OpenSim.Region.Framework", "0.9.3")]

namespace OpenSim.Region.OptionalModules.AI
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "OpenSimAIModule")]
    public class OpenSimAIModule : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly HttpClient m_httpClient = new();
        private readonly List<Scene> m_scenes = [];

        private string m_apiUrl = "https://openrouter.ai/api/v1/chat/completions";
        private string m_apiKey = string.Empty;
        private string m_modelName = "openrouter/free";
        private string m_fallbackModelName = string.Empty;
        private bool m_enabled = false;
        private bool m_isMonetized = false;
        private int m_pricePerRequest = 0;
        private bool m_isPrivate = true;
        private UUID m_bankerUuid = UUID.Zero;

        private IScriptModuleComms m_scriptComms;
        private IMessageTransferModule m_msgTransferModule;
        private INPCModule m_npcModule = null;
        private IMoneyModule m_money = null;
        private static readonly UUID chatBotID = new(UUID.Random());
        private static readonly string chatBotName = "OpenSim AI";
        private class ChatTurn
        {
            public string role { get; set; }
            public string content { get; set; }
        }

        private sealed class SmartNpcProfile
        {
            public string AboutText { get; set; } = string.Empty;
            public UUID ProfileImage { get; set; } = UUID.Zero;
            public string FirstLifeText { get; set; } = string.Empty;
            public UUID FirstLifeImage { get; set; } = UUID.Zero;
            public string WebUrl { get; set; } = string.Empty;
            public string UserTitle { get; set; } = string.Empty;
            public string BornOn { get; set; } = string.Empty;
        }

        private readonly ConcurrentDictionary<UUID, string> m_npcList = new();
        private readonly ConcurrentDictionary<UUID, string> m_npcAliases = new();
        private readonly ConcurrentDictionary<string, UUID> m_npcAliasIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<UUID, SmartNpcProfile> m_npcProfiles = new();
        private string m_profileConnectionString = string.Empty;
        private static readonly ConcurrentDictionary<UUID, List<ChatTurn>> m_userHistories = new();
        private const int MAX_HISTORY_TURNS = 10;
        private DateTime m_suspendUntil = DateTime.MinValue;
        private int remainingLimit = 50;
        private readonly object m_lockSuspend = new();

        public string Name => "OpenSimAIModule";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["OpenSimAI"];
            if (config == null) return;

            m_enabled = config.GetBoolean("Enabled", false);
            if (!m_enabled) return;

            m_profileConnectionString = GetProfileConnectionString(source);
            m_apiKey = config.GetString("ApiKey", "");
            if(string.IsNullOrEmpty(m_apiKey))
            {
                m_log.Error("[OpenSimAI]: Missing API key!");
                return;
            }
            m_apiUrl = config.GetString("ApiUrl", m_apiUrl);
            m_modelName = config.GetString("ModelName", m_modelName);
            m_fallbackModelName = config.GetString("FallbackModelName", string.Empty);
            m_isMonetized = config.GetBoolean("EnableMonetization", false);
            m_pricePerRequest = config.GetInt("PricePerRequest", 0);
            m_isPrivate = config.GetBoolean("IsPrivate", true);

            string bankerUuidStr = config.GetString("BankerUUID", UUID.ZeroString);
            if(bankerUuidStr == UUID.ZeroString || !UUID.TryParse(bankerUuidStr, out m_bankerUuid))
            {
                m_isMonetized = false;
            }

            m_httpClient.Timeout = TimeSpan.FromSeconds(300);
            m_httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {m_apiKey}");
        }

        public void AddRegion(Scene scene)
        {

            if (!m_enabled) return;

            lock (m_scenes)
            {
                if (!m_scenes.Contains(scene))
                {
                    m_scenes.Add(scene);
                }
            }

            scene.EventManager.OnChatFromClient += OnNewPublicChatMessage;
            scene.EventManager.OnNewClient += OnNewClient;
            scene.EventManager.OnClientClosed  += CleanMemoryOnClientClosed;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled) return;
            lock (m_scenes)
            {
                if (m_scenes.Contains(scene))
                {
                    m_scenes.Remove(scene);
                }
            }
            scene.EventManager.OnChatFromClient -= OnNewPublicChatMessage;
            scene.EventManager.OnNewClient -= OnNewClient;
            scene.EventManager.OnClientClosed -= CleanMemoryOnClientClosed;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled) return;

            m_msgTransferModule = scene.RequestModuleInterface<IMessageTransferModule>();

            m_npcModule = scene.RequestModuleInterface<INPCModule>();

            m_scriptComms = scene.RequestModuleInterface<IScriptModuleComms>();
			m_scriptComms?.RegisterScriptInvocations(this);

            m_money = scene.RequestModuleInterface<IMoneyModule>();
            RegisterExistingSmartNpcs(scene);
        }

        public void PostInitialise() { }

        public void Close() { m_httpClient.Dispose(); }

        #region  Events handlers

        private void OnNewClient(IClientAPI client)
        {
            client.OnInstantMessage += OnClientInstantMessage;
        }

        private void CleanMemoryOnClientClosed(UUID agentId, Scene scene)
        {
            m_userHistories.TryRemove(agentId, out _);
            m_npcList.TryRemove(agentId, out _);
            m_npcProfiles.TryRemove(agentId, out _);
            m_npcAliases.TryRemove(agentId, out _);
            foreach (KeyValuePair<string, UUID> alias in m_npcAliasIds)
            {
                if (alias.Value == agentId)
                    m_npcAliasIds.TryRemove(alias.Key, out _);
            }
        }

        private void OnClientInstantMessage(IClientAPI client, GridInstantMessage im)
        {
            if (m_npcModule == null) return;

            UUID agent = new UUID(im.toAgentID);
            UUID senderUuid = new UUID(im.fromAgentID);
            bool isEstateOwner = IsEstateOwner((Scene)client.Scene, senderUuid);
            if (m_isPrivate && !isEstateOwner) return;

            if (m_npcList.ContainsKey(agent) && im.dialog == (byte)InstantMessageDialog.MessageFromAgent)
            {
                string userPrompt = im.message.Trim();

                if (string.IsNullOrEmpty(userPrompt)) return;

                // kung-fu to bypass the presence detector...
                im.dialog = (byte)InstantMessageDialog.StartTyping;

                if (userPrompt.Equals("clear", StringComparison.CurrentCultureIgnoreCase) || userPrompt.Equals("reset", StringComparison.CurrentCultureIgnoreCase))
                {
                    m_userHistories.TryRemove(senderUuid, out _);
                    if (m_npcList.ContainsKey(agent))
                    {
                        SendInstantMessageFromNPC(agent, senderUuid, "\nThis private discussion cache was successfully removed.");
                        return;
                    }

                    SendInstantMessage(senderUuid, "\nThis private discussion cache was successfully removed.");
                    return;
                }

                if (userPrompt.Equals("credits", StringComparison.CurrentCultureIgnoreCase))
                {
                    if (m_npcList.ContainsKey(agent))
                    {
                        SendInstantMessageFromNPC(agent, senderUuid, $"{Credits}");
                        return;
                    }

                    SendInstantMessage(senderUuid, $"{Credits}");
                    return;
                }

                UUID npcOwner = m_npcModule.GetOwner(agent);

                if (npcOwner == senderUuid && userPrompt.StartsWith("#expertise:", StringComparison.CurrentCultureIgnoreCase))
                {
                    string expertise = userPrompt.Substring(11).Trim().ToLower();

                    if (expertise == "list")
                    {
                        SendInstantMessageFromNPC(agent, senderUuid, $"{ExpertiseList}");
                        return;
                    }

                    if (SystemPrompts.ContainsKey(expertise))
                    {
                        if (UpdateNpcExpertise(agent, expertise))
                            SendInstantMessageFromNPC(agent, senderUuid, $"Expertise successfully changed to: {expertise}.");

                        return;
                    }
                    else
                    {
                        SendInstantMessageFromNPC(agent, senderUuid,$"\nUnknown expertise: {expertise}.\n\n {ExpertiseList}");
                        return;
                    }
                }

                if (m_money != null && m_isMonetized && !isEstateOwner)
                {
                    if (!m_money.AmountCovered(client.AgentId, m_pricePerRequest))
                    {
                        client.SendAgentAlertMessage("You do not have enough money to use AI service! Please, load some funds and retry...", false);
                        return;
                    }
                }

                Task.Run(async () =>
                {
                    try
                    {
                        if (!m_npcList.TryGetValue(agent, out string expertise))
                        {
                            expertise = "default";
                        }

                        string systemInstructions = GetSystemPromptForNpc(agent, expertise);

                        string aiResponse = await GenerateTextAsync(senderUuid, userPrompt, systemInstructions, 0, 0.1);

                        List<string> blocks = SplitTextIntoBlocks(aiResponse, 1000);

                        foreach (string block in blocks)
                        {
                            if(m_npcList.ContainsKey(agent))
                                SendInstantMessageFromNPC(agent, senderUuid, "\n" + block);
                            else
                                SendInstantMessage(senderUuid, "\n" + block);

                            await Task.Delay(300);
                        }

                        if (m_isMonetized && !isEstateOwner && blocks.Count > 0 && !aiResponse.Contains("The AI service is temporarily cooling down."))
                            m_money?.MoveMoney(senderUuid, m_bankerUuid, m_pricePerRequest, "AI Service Request Fee");
                    }
                    catch (Exception ex)
                    {
                        m_log.Error($"[OpenSim AI] Error during asynchronous processing : {ex.Message} \n {ex.StackTrace}");
                    }
                });
            }
        }

        private void OnNewPublicChatMessage(object sender, OSChatMessage e)
        {
            Scene scene = (Scene)e.Scene;
            if (m_isPrivate && !IsEstateOwner((Scene)e.Scene, e.Sender.AgentId)) return;

            string rawMessage = e.Message.Trim();
            if (e.Channel != 0 || !rawMessage.StartsWith("@", StringComparison.Ordinal)) return;

            int space = rawMessage.IndexOf(' ');
            string alias = (space < 0 ? rawMessage : rawMessage.Substring(0, space)).TrimStart('@').Trim();
            if (string.IsNullOrWhiteSpace(alias)) return;
            if (!m_npcAliasIds.TryGetValue(alias, out UUID npcKey) || !m_npcList.ContainsKey(npcKey)) return;

            string userPrompt = space < 0 ? string.Empty : rawMessage.Substring(space + 1).Trim();
            if (string.IsNullOrEmpty(userPrompt)) return;
            if (userPrompt.Equals("private", StringComparison.CurrentCultureIgnoreCase))
            {
                SendInstantMessageFromNPC(npcKey, e.Sender.AgentId, $"\nYou are now talking privately with {m_npcAliases[npcKey]}.");
                return;
            }
            if (userPrompt.Equals("credits", StringComparison.CurrentCultureIgnoreCase))
            {
                scene.SimChat(Utils.StringToBytes(Credits), ChatTypeEnum.Owner, 0, e.Position, m_npcAliases[npcKey], e.SenderUUID, false);
                return;
            }

            Task.Run(async () =>
            {
                string systemInstructions = GetSystemPromptForNpc(npcKey, m_npcList[npcKey]);
                string aiResponse = await GenerateTextAsync(UUID.Zero, userPrompt, systemInstructions, 1024, 0.5);
                List<string> blocks = SplitTextIntoBlocks(aiResponse, 1000);
                foreach (string block in blocks)
                {
                    scene.SimChat(Utils.StringToBytes("\n" + block), ChatTypeEnum.Owner, 0, e.Position, m_npcAliases[npcKey], e.SenderUUID, false);
                    await Task.Delay(300);
                }
            });
        }

        #endregion
        #region Script Functions

        [ScriptInvocation]
        public string osCreateSmartNPC(UUID hostID, UUID scriptID, string firstName, string lastName, Vector3 position, string notecardName, string expertise)
        {
            if (!m_enabled) return "OpenSim AI Module disabled.";
            if (m_npcModule == null) return "OpenSim NPC module is unavailable.";

            var objectData = GetObjectData(hostID);
            UUID owner = UUID.Zero;
            Scene activeScene = objectData[hostID].Scene ?? null;
            SceneObjectPart sop = objectData[hostID].SceneObjectPart ?? null;
            if (activeScene == null || sop == null) return UUID.ZeroString;
            string appearanceLines = string.Empty;

            if (string.IsNullOrEmpty(expertise)){ expertise = "default"; }

            try
            {
                owner = objectData[hostID].OwnerID;

                if (m_isPrivate && !objectData[hostID].isOwnerEstateOwner) return UUID.ZeroString;
                if (string.IsNullOrWhiteSpace(firstName)) return UUID.ZeroString;
                if (!activeScene.Permissions.CanRezObject(1, owner, position))
                {
                    return UUID.ZeroString;
                }

                TaskInventoryItem item = sop.Inventory.GetInventoryItem(notecardName);

                if (item != null && item.Type == (int)AssetType.Notecard)
                {
                    AssetBase asset = activeScene.AssetService.Get(item.AssetID.ToString());
                    if (asset != null)
                    {
                        string[] lines = SLUtil.ParseNotecardToArray(asset.Data);
                        if (lines != null)
                        {
                            appearanceLines = string.Join("\n", lines);
                        }
                    }
                }

                OSDMap appOsd = OSDParser.DeserializeLLSDXml(appearanceLines) as OSDMap;

                AvatarAppearance appearance = new();
                appearance.Unpack(appOsd);

                UUID npcKey = GetVirtualAvatarUUID(firstName, lastName);
                string fullName = $"{firstName} {lastName}".Trim();
                if ((m_npcAliasIds.TryGetValue(firstName, out UUID firstAlias) && firstAlias != npcKey) ||
                    (m_npcAliasIds.TryGetValue(fullName, out UUID fullAlias) && fullAlias != npcKey))
                    return UUID.ZeroString;
                if (SceneManager.Instance.TryGetScenePresence(npcKey, out ScenePresence _))
                    return UUID.ZeroString;

                npcKey = m_npcModule.CreateNPC(firstName, lastName, position, npcKey, owner, string.Empty, UUID.Zero, true, activeScene, appearance);

                if (npcKey != UUID.Zero && !m_npcList.ContainsKey(npcKey) && activeScene.TryGetScenePresence(npcKey, out ScenePresence sp))
                {
                    if (!m_npcList.TryAdd(npcKey, expertise.ToLower()))
                    {
                        m_npcModule.DeleteNPC(npcKey, activeScene);
                        return UUID.ZeroString;
                    }
                    if (TryLoadVirtualProfile(npcKey, sp.Name, owner, out SmartNpcProfile savedProfile))
                    {
                        m_npcProfiles[npcKey] = savedProfile;
                        ApplySmartNpcProfile(npcKey, activeScene, sp, savedProfile);
                    }
                    m_npcAliasIds[sp.Firstname] = npcKey;
                    m_npcAliasIds[sp.Name] = npcKey;
                    m_npcAliases[npcKey] = sp.Name;
                    sp.SendAvatarDataToAllAgents();
                    return npcKey.ToString();
                }
            }
            catch (Exception ex)
            {
                m_log.Error($"[OpenSim AI] Error while executing osCreateSmartNPC in {hostID} - {owner}: \n- {ex.Message}\n- {ex.StackTrace}");
            }

            return UUID.ZeroString;
        }

        [ScriptInvocation]
        public void osSetSmartNPC(UUID hostID, UUID scriptID, UUID npcKey, string expertise)
        {
            if (!m_enabled || m_npcModule == null || !m_npcList.ContainsKey(npcKey)) return;

            var objectData = GetObjectData(hostID);

            if (string.IsNullOrEmpty(expertise)){ expertise = "default"; }

            try
            {
                if (m_isPrivate && !objectData[hostID].isOwnerEstateOwner) return;

                UUID npcOwner = m_npcModule.GetOwner(npcKey);

                if (npcOwner != UUID.Zero && npcOwner == objectData[hostID].OwnerID)
                {
                    if (UpdateNpcExpertise(npcKey, expertise))
                        SendInstantMessageFromNPC(npcKey, npcOwner,$"Expertise successfully changed to: {expertise}.");
                        m_userHistories.TryRemove(npcOwner, out _);
                }
            }
            catch (Exception ex)
            {
                m_log.Error($"[OpenSim AI] osSetSmartNPC in {hostID} failed to change expertise for {npcKey}: \n- {ex.Message}\n- {ex.StackTrace}");
            }
        }

        [ScriptInvocation]
        public int osSetSmartNPCProfile(UUID hostID, UUID scriptID, UUID npcKey,
            string aboutText, string profileImage, string firstLifeText, string firstLifeImage,
            string webUrl, string accountTitle, string bornOn)
        {
            if (!m_enabled || m_npcModule == null || !m_npcList.ContainsKey(npcKey)) return 0;

            var objectData = GetObjectData(hostID);
            if (objectData[hostID].Scene == null || objectData[hostID].SceneObjectPart == null) return 0;
            if (m_isPrivate && !objectData[hostID].isOwnerEstateOwner) return 0;
            if (m_npcModule.GetOwner(npcKey) != objectData[hostID].OwnerID) return 0;
            if (!TryGetNpcPresence(npcKey, out Scene npcScene, out ScenePresence npcPresence)) return 0;

            var profile = new SmartNpcProfile
            {
                AboutText = aboutText ?? string.Empty,
                ProfileImage = ParseUuid(profileImage),
                FirstLifeText = firstLifeText ?? string.Empty,
                FirstLifeImage = ParseUuid(firstLifeImage),
                WebUrl = webUrl ?? string.Empty,
                UserTitle = accountTitle ?? string.Empty,
                BornOn = bornOn ?? string.Empty
            };

            INPC npc = m_npcModule.GetNPC(npcKey, npcScene);
            if (npc == null) return 0;

            ApplySmartNpcProfile(npcKey, npcScene, npcPresence, profile);
            m_npcProfiles[npcKey] = profile;
            bool profileSaved = SaveVirtualProfile(npcKey, npcPresence, objectData[hostID].OwnerID, profile);

            return profileSaved ? 1 : 0;
        }

        [ScriptInvocation]
        public int osSetSmartNPCAppearance(UUID hostID, UUID scriptID, UUID npcKey, string notecardName)
        {
            if (!m_enabled || m_npcModule == null || !m_npcList.ContainsKey(npcKey)) return 0;

            var objectData = GetObjectData(hostID);
            if (objectData[hostID].Scene == null || objectData[hostID].SceneObjectPart == null) return 0;
            if (m_isPrivate && !objectData[hostID].isOwnerEstateOwner) return 0;
            if (m_npcModule.GetOwner(npcKey) != objectData[hostID].OwnerID) return 0;
            if (!TryGetNpcPresence(npcKey, out Scene npcScene, out _)) return 0;
            if (!TryLoadAppearance(objectData[hostID].Scene, objectData[hostID].SceneObjectPart, notecardName, out AvatarAppearance appearance)) return 0;

            return m_npcModule.SetNPCAppearance(npcKey, appearance, npcScene) ? 1 : 0;
        }

        [ScriptInvocation]
        public int osTeleportSmartNPC(UUID hostID, UUID scriptID, UUID npcKey, UUID targetAgent)
        {
            if (!m_enabled || m_npcModule == null || !m_npcList.ContainsKey(npcKey)) return 0;

            var objectData = GetObjectData(hostID);
            if (objectData[hostID].Scene == null || objectData[hostID].SceneObjectPart == null) return 0;
            if (m_isPrivate && !objectData[hostID].isOwnerEstateOwner) return 0;
            if (m_npcModule.GetOwner(npcKey) != objectData[hostID].OwnerID) return 0;
            if (targetAgent == UUID.Zero) targetAgent = objectData[hostID].OwnerID;
            if (!TryGetNpcPresence(npcKey, out Scene npcScene, out ScenePresence npcPresence)) return 0;
            if (!SceneManager.Instance.TryGetScenePresence(targetAgent, out ScenePresence targetPresence)) return 0;
            if (targetAgent == npcKey || targetPresence == null || targetPresence.IsDeleted || targetPresence.IsInTransit) return 0;
            if (targetPresence.Scene != npcScene) return 0;

            IEntityTransferModule transfer = npcScene.RequestModuleInterface<IEntityTransferModule>();
            if (transfer == null) return 0;

            Vector3 targetPosition = targetPresence.AbsolutePosition + new Vector3(1.0f, 0.0f, 0.0f);
            Vector3 targetLookAt = targetPosition + new Vector3(0.0f, 1.0f, 0.0f);
            transfer.Teleport(npcPresence, targetPresence.Scene.RegionInfo.RegionHandle, targetPosition, targetLookAt,
                (uint)Constants.TeleportFlags.ViaLocation);

            return 1;
        }

        [ScriptInvocation]
        public void osNpcInstantMessage(UUID hostID, UUID scriptID, UUID npcKey, UUID destination, string message)
        {
            if (!m_enabled || m_npcModule == null || destination == UUID.Zero || string.IsNullOrEmpty(message)) return;

            var objectData = GetObjectData(hostID);

            try
            {
                if (m_isPrivate && !objectData[hostID].isOwnerEstateOwner) return;

                UUID npcOwner = m_npcModule.GetOwner(npcKey);

                if (npcOwner != UUID.Zero && npcOwner == objectData[hostID].OwnerID)
                {
                    SendInstantMessageFromNPC(npcKey, destination, message);
                }
            }
            catch (Exception ex)
            {
                m_log.Error($"[OpenSim AI] osNpcInstantMessage in {hostID} failed to send IM from {npcKey} to {destination}:\n- {ex.Message}\n- {ex.StackTrace}");
            }
        }

        [ScriptInvocation]
        public string osAI(UUID hostID, UUID scriptID, string systemPrompt, string userPrompt, int maxTokens, float temperature)
        {
            if (!m_enabled) return "OpenSim AI Module disabled.";

            var objectData = GetObjectData(hostID);

            if (m_isPrivate && !objectData[hostID].isOwnerEstateOwner) return "OpenSim AI Module is in Private Mode.";

            if (string.IsNullOrEmpty(userPrompt)) return string.Empty;

            string systemInstructions = !string.IsNullOrEmpty(systemPrompt)
                                     ? systemPrompt
                                     : GetSystemPromptByExpertise("scripting");

            string result = Task.Run(async () => await GenerateTextAsync(UUID.Zero, userPrompt, systemInstructions, maxTokens, temperature)).GetAwaiter().GetResult();

            return result;
        }

        #endregion
        #region InstantMessage Communications

        private void SendInstantMessage(UUID targetUser, string message)
        {
            if (m_msgTransferModule == null || targetUser == UUID.Zero || string.IsNullOrEmpty(message)) return;

            GridInstantMessage im = new()
            {
                fromAgentID = chatBotID.Guid,
                fromAgentName = chatBotName,
                toAgentID = targetUser.Guid,
                dialog = (byte)InstantMessageDialog.MessageFromAgent,
                message = message,
                timestamp = (uint)Util.UnixTimeSinceEpoch(),
                fromGroup = false,
                offline = (byte)0,
                ParentEstateID = 0,
                Position = Vector3.Zero,
                RegionID = UUID.Zero.Guid,
                binaryBucket = []
            };

            m_msgTransferModule.SendInstantMessage(im, delegate(bool success) {});
        }

        private void SendInstantMessageFromNPC(UUID npcId, UUID targetUser, string message)
        {
            Scene activeScene = null;
            ScenePresence sp = null;

            if (m_msgTransferModule == null) return;

            lock (m_scenes)
            {
                foreach (Scene scene in m_scenes)
                {
                    sp = scene.GetScenePresence(npcId);
                    if (sp != null)
                    {
                        activeScene = scene;
                        break;
                    }
                }
            }

            if (sp == null) { return; }

            GridInstantMessage im = new()
            {
                fromAgentID = npcId.Guid,
                fromAgentName = sp.Name,
                toAgentID = targetUser.Guid,
                dialog = (byte)InstantMessageDialog.MessageFromAgent,
                message = message,
                RegionID = activeScene.RegionInfo.RegionID.Guid,
            };

            m_msgTransferModule.SendInstantMessage(im, delegate(bool success) {});
        }

        #endregion
        #region AI Communications

        private async Task<string> GenerateTextAsync(UUID avatarId, string userPrompt, string systemPrompt, int maxTokens, double temperature)
        {
            string currentModel = m_modelName;
            if (DateTime.UtcNow < m_suspendUntil)
            {
                double remainingSeconds = Math.Ceiling((m_suspendUntil - DateTime.UtcNow).TotalSeconds);

                if (currentModel == m_modelName && remainingLimit == 0 && m_fallbackModelName != "")
                {
                    currentModel = m_fallbackModelName;
                }
                else
                {
                    m_log.Warn($"[OpenSim AI] Request rejected locally. Service suspended for {remainingSeconds} secondes.");

                    return $"The AI service is temporarily cooling down. Please try again in {remainingSeconds} seconds...";
                }
            }

            int maxAttempts = 3;
            int currentAttempt = 0;

            while (currentAttempt < maxAttempts)
            {
                currentAttempt++;

                List<object> messageList = [];
                object[] message =
                [
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                ];

                List<ChatTurn> history = m_userHistories.GetOrAdd(avatarId, new List<ChatTurn>());

                if(avatarId != UUID.Zero)
                {
                    messageList.Add(new { role = "system", content = systemPrompt });
                    lock (history)
                    {
                        foreach (var turn in history)
                        {
                            messageList.Add(new { role = turn.role, content = turn.content });
                        }
                    }

                    messageList.Add(new { role = "user", content = userPrompt });
                    message = [.. messageList];
                }

                var payload = new
                {
                    model = currentModel,
                    messages = message,
                    temperature = temperature < 2.0 && temperature > 0 ? temperature : 0.1,
                    max_tokens = maxTokens > 0 ? maxTokens : 4096
                };

                JsonSerializerOptions serializeOptions = new()
                {
                    PropertyNamingPolicy = null,
                    WriteIndented = false
                };

                string jsonPayload = JsonSerializer.Serialize(payload, serializeOptions);

                try
                {
                    HttpRequestMessage request = new(HttpMethod.Post, m_apiUrl);
                    request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", m_apiKey);
                    request.Headers.Add("X-Title", "OpenSim AI Module");

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    using var response = await m_httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                    if (response.IsSuccessStatusCode)
                    {
                        string responseJson = await response.Content.ReadAsStringAsync();

                        if (responseJson.Contains("User Safety:") ||
                            responseJson.Contains("Response Safety:"))
                        {
                            m_log.Warn($"[OpenSim AI] Security false positive detected (Attempts {currentAttempt}/{maxAttempts}). Request resubmit...");
                            await Task.Delay(1000);
                            continue;
                        }

                        using JsonDocument doc = JsonDocument.Parse(responseJson);
                        JsonElement root = doc.RootElement;
                        string contentStr = string.Empty;

                        if (root.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0)
                        {
                            JsonElement firstChoice = choices[0];

                            if (firstChoice.TryGetProperty("message", out JsonElement aiMessage) &&
                                aiMessage.TryGetProperty("content", out JsonElement aiContent))
                            {
                                contentStr = aiContent.GetString().Trim();

                                if (string.IsNullOrEmpty(contentStr))
                                {
                                    m_log.Warn($"[OpenSim AI] Empty response or invalid JSON structure (Attempts: {currentAttempt}/{maxAttempts}). New attempt...");
                                    await Task.Delay(1000);
                                    continue;
                                }
                            }
                        }

                        lock (history)
                        {
                            history.Add(new ChatTurn { role = "user", content = userPrompt });
                            history.Add(new ChatTurn { role = "assistant", content = contentStr });

                            while (history.Count > MAX_HISTORY_TURNS * 2)
                            {
                                history.RemoveAt(0);
                            }
                        }

                        return contentStr;
                    }

                    if ((int)response.StatusCode == 429)
                    {
                        int retryAfterSeconds = 10;

                        if (response.Headers.TryGetValues("Retry-After", out var values) && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining))
                        {
                            if (int.TryParse(values.FirstOrDefault(), out int parsedSeconds) && parsedSeconds > 0)
                            {
                                retryAfterSeconds = parsedSeconds;
                            }

                            if (int.TryParse(remaining.FirstOrDefault(), out int limit))
                            {
                                remainingLimit = limit;
                            }
                        }

                        lock (m_lockSuspend)
                        {
                            m_suspendUntil = DateTime.UtcNow.AddSeconds(retryAfterSeconds);
                        }

                        m_log.Error($"[OpenSim AI] Request quota or limit reached. Service suspended for {retryAfterSeconds} seconds.");

                        return $"The AI service is temporarily unavailable due to rate limits. Service will resume in {retryAfterSeconds} seconds...";
                    }

                    else if (!response.IsSuccessStatusCode)
                    {
                        string responseerror = await response.Content.ReadAsStringAsync();

                        m_log.Warn($"[OpenSim AI] API Error: {response.StatusCode} - Details: {responseerror}");
                        return "Sorry, a technical error has occurred. Please try again...";
                    }
                }
                catch (Exception e)
                {
                    m_log.Warn($"[OpenSim AI] API Exception: {e.Message} \n {e.StackTrace}");
                    return "Sorry, a technical error has occurred. Please try again...";
                }
            }

            return string.Empty;
        }

        #endregion
        #region Helpers

        private Dictionary<UUID, (Scene Scene, SceneObjectPart SceneObjectPart, UUID OwnerID, bool isOwnerEstateOwner)> GetObjectData(UUID hostID)
        {
            var objectData = new Dictionary<UUID, (Scene Scene, SceneObjectPart SceneObjectPart, UUID OwnerID, bool isOwnerEstateOwner)>();
            Scene activeScene = null;
            SceneObjectPart sop = null;

            lock (m_scenes)
            {
                foreach (Scene scene in m_scenes)
                {
                    SceneObjectPart found = scene.GetSceneObjectPart(hostID);
                    if (found != null)
                    {
                        activeScene = scene;
                        sop = found;
                        break;
                    }
                }
            }

            if (activeScene == null || sop == null)
            {
                objectData[hostID] = (null, null, UUID.Zero, false);
                return objectData;
            }

            objectData[hostID] = (activeScene, sop, sop.OwnerID, sop.OwnerID == activeScene.RegionInfo.EstateSettings.EstateOwner);
            return objectData;
        }

        private static bool IsEstateOwner(Scene scene, UUID avatar)
        {
            return avatar == scene.RegionInfo.EstateSettings.EstateOwner;
        }

        private static List<string> SplitTextIntoBlocks(string text, int maxBytes)
        {
            List<string> blocks = [];
            string[] words = text.Split(' ');
            StringBuilder currentBlock = new StringBuilder();

            foreach (string word in words)
            {
                string testStr = currentBlock.Length == 0
                               ? word
                               : currentBlock.ToString() + " " + word;

                if (Encoding.UTF8.GetByteCount(testStr) > maxBytes)
                {
                    blocks.Add(currentBlock.ToString());
                    currentBlock.Clear();
                    currentBlock.Append(word);
                }
                else
                {
                    if (currentBlock.Length > 0)
                        currentBlock.Append(' ');

                    currentBlock.Append(word);
                }
            }

            if (currentBlock.Length > 0)
                blocks.Add(currentBlock.ToString());

            return blocks;
        }

        private string GetProfileConnectionString(IConfigSource source)
        {
            IConfig aiConfig = source?.Configs["OpenSimAI"];
            string configuredProfileConnection = aiConfig?.GetString("ProfileConnectionString", string.Empty) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(configuredProfileConnection))
            {
                if (IsPgsqlConnection(configuredProfileConnection))
                    return configuredProfileConnection;

                m_log.Warn("[OpenSim AI] OpenSimAI/ProfileConnectionString must be a PostgreSQL connection string.");
            }

            IConfig dbConfig = source?.Configs["DatabaseService"];
            string connectionString = dbConfig?.GetString("ConnectionString", string.Empty) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(connectionString))
                return string.Empty;

            // This grid uses the PGSQL provider. Keep the existing connection settings
            // and only switch the database name used for virtual-avatar profiles.
            if (IsPgsqlConnection(connectionString))
            {
                return System.Text.RegularExpressions.Regex.Replace(
                    connectionString, @"Database\s*=\s*[^;]+", "Database=robust",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            m_log.Warn("[OpenSim AI] SmartNPC profile persistence requires a PostgreSQL connection string.");
            return string.Empty;
        }

        private static bool IsPgsqlConnection(string connectionString)
        {
            return connectionString.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
                   connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase);
        }

        private static UUID ParseUuid(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && UUID.TryParse(value, out UUID id) ? id : UUID.Zero;
        }

        private static UUID GetVirtualAvatarUUID(string firstName, string lastName)
        {
            string avatarName = $"{firstName} {lastName}".Trim();
            return Util.ComputeASCIISHA1UUID("igrid-virtual-avatar:" + avatarName.ToLowerInvariant());
        }

        private void RegisterExistingSmartNpcs(Scene scene)
        {
            if (scene == null || m_npcModule == null)
                return;

            foreach (ScenePresence presence in scene.GetScenePresences())
            {
                if (presence == null || !presence.IsNPC || presence.IsDeleted || presence.IsChildAgent || presence.IsInTransit)
                    continue;

                UUID npcKey = GetVirtualAvatarUUID(presence.Firstname, presence.Lastname);
                if (npcKey != presence.UUID)
                    continue;

                if ((m_npcAliasIds.TryGetValue(presence.Firstname, out UUID firstAlias) && firstAlias != npcKey) ||
                    (m_npcAliasIds.TryGetValue(presence.Name, out UUID fullAlias) && fullAlias != npcKey))
                    continue;
                if (!m_npcList.TryAdd(npcKey, "default"))
                    continue;

                m_npcAliasIds[presence.Firstname] = npcKey;
                m_npcAliasIds[presence.Name] = npcKey;
                m_npcAliases[npcKey] = presence.Name;
                UUID owner = m_npcModule.GetOwner(npcKey);
                if (TryLoadVirtualProfile(npcKey, presence.Name, owner, out SmartNpcProfile profile))
                {
                    m_npcProfiles[npcKey] = profile;
                    ApplySmartNpcProfile(npcKey, scene, presence, profile);
                }
            }
        }

        private bool TryGetNpcPresence(UUID npcKey, out Scene scene, out ScenePresence presence)
        {
            lock (m_scenes)
            {
                foreach (Scene candidate in m_scenes)
                {
                    if (candidate.TryGetScenePresence(npcKey, out presence) &&
                        presence != null && presence.IsNPC && !presence.IsDeleted &&
                        !presence.IsChildAgent && !presence.IsInTransit)
                    {
                        scene = candidate;
                        return true;
                    }
                }
            }

            scene = null;
            presence = null;
            return false;
        }

        private bool TryLoadAppearance(Scene scene, SceneObjectPart sop, string notecardName, out AvatarAppearance appearance)
        {
            appearance = null;
            if (scene == null || sop == null || string.IsNullOrWhiteSpace(notecardName))
                return false;

            try
            {
                TaskInventoryItem item = sop.Inventory.GetInventoryItem(notecardName);
                if (item == null || item.Type != (int)AssetType.Notecard)
                    return false;

                AssetBase asset = scene.AssetService.Get(item.AssetID.ToString());
                if (asset == null)
                    return false;

                string[] lines = SLUtil.ParseNotecardToArray(asset.Data);
                OSDMap appOsd = OSDParser.DeserializeLLSDXml(string.Join("\n", lines ?? [])) as OSDMap;
                if (appOsd == null)
                    return false;

                appearance = new AvatarAppearance();
                appearance.Unpack(appOsd);
                return true;
            }
            catch (Exception ex)
            {
                m_log.Warn($"[OpenSim AI] Unable to load NPC appearance notecard: {ex.Message}");
                return false;
            }
        }

        private void ApplySmartNpcProfile(UUID npcKey, Scene scene, ScenePresence presence, SmartNpcProfile profile)
        {
            INPC npc = m_npcModule.GetNPC(npcKey, scene);
            if (npc == null)
                return;

            npc.profileAbout = profile.AboutText;
            npc.profileImage = profile.ProfileImage;
            npc.Born = profile.BornOn;
            if (npc is INPCProfileMembership profileMembership)
                profileMembership.ProfileMembership = string.IsNullOrWhiteSpace(profile.UserTitle)
                    ? "Non Player Character (NPC)"
                    : profile.UserTitle;
            presence.SendAvatarDataToAllAgents();
        }

        private bool TryLoadVirtualProfile(UUID npcKey, string avatarName, UUID owner, out SmartNpcProfile profile)
        {
            profile = null;
            if (string.IsNullOrEmpty(m_profileConnectionString))
                return false;

            try
            {
                const string query = @"SELECT ""AboutText"", ""ProfileImage"", ""FirstLifeImage"", ""FirstLifeText"", ""WebUrl"", ""UserTitle"", ""BornOn""
                    FROM ""TasiaVirtualAvatars""
                    WHERE ""Enabled"" = 1 AND ""OwnerID"" = @Owner
                    AND (""VirtualID"" = @Id OR ""AvatarName"" = @Name)
                    ORDER BY CASE WHEN ""VirtualID"" = @Id THEN 0 ELSE 1 END, ""Updated"" DESC LIMIT 1";
                using NpgsqlConnection connection = new(m_profileConnectionString);
                connection.Open();
                using NpgsqlCommand command = new(query, connection);
                command.Parameters.AddWithValue("@Id", NpgsqlTypes.NpgsqlDbType.Uuid, npcKey.Guid);
                command.Parameters.AddWithValue("@Name", avatarName ?? string.Empty);
                command.Parameters.AddWithValue("@Owner", NpgsqlTypes.NpgsqlDbType.Uuid, owner.Guid);
                using NpgsqlDataReader reader = command.ExecuteReader();
                if (!reader.Read())
                    return false;

                profile = new SmartNpcProfile
                {
                    AboutText = ReadDatabaseString(reader, "AboutText"),
                    ProfileImage = ParseUuid(ReadDatabaseString(reader, "ProfileImage")),
                    FirstLifeImage = ParseUuid(ReadDatabaseString(reader, "FirstLifeImage")),
                    FirstLifeText = ReadDatabaseString(reader, "FirstLifeText"),
                    WebUrl = ReadDatabaseString(reader, "WebUrl"),
                    UserTitle = ReadDatabaseString(reader, "UserTitle"),
                    BornOn = ReadDatabaseString(reader, "BornOn")
                };
                return true;
            }
            catch (Exception ex)
            {
                m_log.Warn($"[OpenSim AI] Unable to load SmartNPC profile: {ex.Message}");
                return false;
            }
        }

        private bool SaveVirtualProfile(UUID npcKey, ScenePresence presence, UUID owner, SmartNpcProfile profile)
        {
            if (string.IsNullOrEmpty(m_profileConnectionString))
                return true;

            string avatarName = presence.Name;
            int nameSplit = avatarName.LastIndexOf(' ');
            string firstName = nameSplit > 0 ? avatarName[..nameSplit] : avatarName;
            string lastName = nameSplit > 0 ? avatarName[(nameSplit + 1)..] : string.Empty;

            try
            {
                const string query = @"INSERT INTO ""TasiaVirtualAvatars""
                    (""VirtualID"", ""AvatarName"", ""FirstName"", ""LastName"", ""DisplayName"", ""OwnerID"", ""AboutText"", ""ProfileImage"", ""FirstLifeImage"", ""FirstLifeText"", ""WebUrl"", ""UserTitle"", ""BornOn"", ""Enabled"", ""Created"", ""Updated"")
                    VALUES (@VirtualID::uuid, @AvatarName, @FirstName, @LastName, @AvatarName, @OwnerID::uuid, @AboutText, @ProfileImage::uuid, @FirstLifeImage::uuid, @FirstLifeText, @WebUrl, @UserTitle, @BornOn, 1, @Now, @Now)
                    ON CONFLICT (""VirtualID"") DO UPDATE SET ""AvatarName"" = EXCLUDED.""AvatarName"", ""FirstName"" = EXCLUDED.""FirstName"", ""LastName"" = EXCLUDED.""LastName"", ""DisplayName"" = EXCLUDED.""DisplayName"", ""OwnerID"" = EXCLUDED.""OwnerID"",
                    ""AboutText"" = EXCLUDED.""AboutText"", ""ProfileImage"" = EXCLUDED.""ProfileImage"", ""FirstLifeImage"" = EXCLUDED.""FirstLifeImage"",
                    ""FirstLifeText"" = EXCLUDED.""FirstLifeText"", ""WebUrl"" = EXCLUDED.""WebUrl"", ""UserTitle"" = EXCLUDED.""UserTitle"",
                    ""BornOn"" = EXCLUDED.""BornOn"", ""Enabled"" = 1, ""Updated"" = EXCLUDED.""Updated""";
                int now = (int)Util.UnixTimeSinceEpoch();
                using NpgsqlConnection connection = new(m_profileConnectionString);
                connection.Open();
                using NpgsqlCommand command = new(query, connection);
                command.Parameters.AddWithValue("@VirtualID", npcKey.ToString());
                command.Parameters.AddWithValue("@AvatarName", avatarName);
                command.Parameters.AddWithValue("@FirstName", firstName);
                command.Parameters.AddWithValue("@LastName", lastName);
                command.Parameters.AddWithValue("@OwnerID", owner.ToString());
                command.Parameters.AddWithValue("@AboutText", profile.AboutText);
                command.Parameters.AddWithValue("@ProfileImage", profile.ProfileImage.ToString());
                command.Parameters.AddWithValue("@FirstLifeImage", profile.FirstLifeImage.ToString());
                command.Parameters.AddWithValue("@FirstLifeText", profile.FirstLifeText);
                command.Parameters.AddWithValue("@WebUrl", profile.WebUrl);
                command.Parameters.AddWithValue("@UserTitle", profile.UserTitle);
                command.Parameters.AddWithValue("@BornOn", profile.BornOn);
                command.Parameters.AddWithValue("@Now", now);
                command.ExecuteNonQuery();
                return true;
            }
            catch (Exception ex)
            {
                m_log.Warn($"[OpenSim AI] Unable to save SmartNPC profile: {ex.Message}");
                return false;
            }
        }

        private static string ReadDatabaseString(NpgsqlDataReader reader, string name)
        {
            int ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        private string GetSystemPromptForNpc(UUID npcKey, string expertise)
        {
            string prompt = GetSystemPromptByExpertise(expertise);
            if (!m_npcProfiles.TryGetValue(npcKey, out SmartNpcProfile profile))
                return prompt;

            StringBuilder profileText = new();
            if (!string.IsNullOrWhiteSpace(profile.UserTitle))
                profileText.Append("Title: ").Append(profile.UserTitle).Append('\n');
            if (!string.IsNullOrWhiteSpace(profile.WebUrl))
                profileText.Append("Website: ").Append(profile.WebUrl).Append('\n');
            if (!string.IsNullOrWhiteSpace(profile.FirstLifeText))
                profileText.Append("Background: ").Append(profile.FirstLifeText).Append('\n');
            if (!string.IsNullOrWhiteSpace(profile.AboutText))
                profileText.Append(profile.AboutText);

            if (profileText.Length == 0)
                return prompt;

            string context = profileText.ToString();
            if (context.Length > 4000)
                context = context[..4000];
            return prompt + "\n\nNPC profile:\n" + context;
        }

        private static string GetSystemPromptByExpertise(string expertise)
        {
            if (SystemPrompts.TryGetValue(expertise, out string prompt))
            {
                return prompt;
            }

            return "You are an expert AI companion specializing in the development of virtual worlds and roleplays based on OpenSimulator. Your first mission is to help newcomers understand the features available to them, explain, inform, and answer questions. Your secondary mission is to entertain, amuse, and lead discussions. Always return the result in the same language as the user prompt and in a friendly, cheerful and informative way.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks.";
        }

        private bool UpdateNpcExpertise(UUID npc, string expertise)
        {
            if (npc == UUID.Zero || string.IsNullOrEmpty(expertise) || !SystemPrompts.ContainsKey(expertise.ToLower())) return false;

            m_npcList[npc] = expertise.ToLower();
            return true;
        }

        private static readonly FrozenDictionary<string, string> SystemPrompts = new Dictionary<string, string>
        {
            {"default", "You are an expert AI companion specializing in the development of virtual worlds and roleplays based on OpenSimulator. Your first mission is to help newcomers understand the features available to them, explain, inform, and answer questions. Your secondary mission is to entertain, amuse, and lead discussions. Always return the result in the same language as the user prompt and in a friendly, cheerful and informative way.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks."},
            { "avatars", "You are an AI expert in Second Life and OpenSimulator avatars creation, anatomy, and customization for virtual worlds. You are highly skilled in morphing systems (shapes), skin textures, avatar baking, Baked on Mesh (BOM) body systems, and attachment management.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks." },

            { "scripting", "You are a systems development engineer specialized in OpenSimulator scripting (LSL, OSSL). You write optimized, lag-free code, expertly managing zone events, network listeners (HTTP/XML-RPC), sensors, and regional database interactions.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks." },

            { "building", "You are a 3D architect and virtual environment designer. Expert in Second Life and OpenSimulator Mesh modeling (Blender/Maya), Collada (.dae) imports, Level of Detail (LOD) management, collision physics (Physics Shape), and land impact (prim weight) optimization.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks." },

            { "texturing", "You are a technical artist expert in Second Life and OpenSimulator PBR (Physically Based Rendering) textures and materials applied to virtual worlds. You master albedo, normal, roughness, and metallic maps, as well as animated textures and advanced lighting effects on primitives.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks." },

            { "animations", "You are a 3D animator specialized in rigging and avatar skeletons for virtual worlds. Expert in creating Second Life and OpenSimulator BVH/ANIM animations, managing animation priorities, creating AO (Animation Overriders), and configuring autonomous animated objects via Animesh technology.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks." },

            { "environment", "You are an estate manager and region terraformer for OpenSimulator. You master region file configuration (Regions.ini), parcel management (land), access rights, terrain modification (RAW files), environment control (Windlight/EnvSet), and server performance optimization.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks." },

            { "economy", "You are an economic consultant specialized in in OpenSimulator virtual markets and virtual currencies (Gloebit, OMC, local currency). You advise on content monetization, vendor systems, intellectual property protection (open-world DRM), and event marketing within the metaverse.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks." },

            { "community", "You are a community manager and cultural/educational event organizer in OpenSimulator virtual worlds. Expert in group management, roleplay system design, in-world audio/video streaming server configuration, and new user onboarding.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks." }
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        private readonly string ExpertiseList = "\n[=>  AVAILABLE EXPERTISES LIST  <=]\n\ndefault : Set a generalist AI, expert in OpenSim.\n\navatars : Set an expert AI in avatars design and customization.\n\nscripting : Set an expert AI in LSL and OSSL scripting.\n\nbuilding : Set an expert AI in building & meshes creation.\n\ntexturing : Set an expert AI in texturing & PBR materials.\n\nanimations : Set an expert AI in creating BVH/ANIM animations.\n\nenvironment : Set an expert AI in scene/world customization, terraforming, environment...\n\neconomy : Set an expert AI in in-world economy and marketing.\n\ncommunity : Set an expert AI in events and groups management.\n";

        // Part of the copyright! Please, don't remove or alter.
        private readonly string Credits = "\nMade in Morocco with fun & love by Adil El Farissi (aka: Web Rain @ OsGrid/SL) https://github.com/AdilElFarissi \n\nThe OpenSim AI Module is under MIT License.";

        #endregion
    }
}
