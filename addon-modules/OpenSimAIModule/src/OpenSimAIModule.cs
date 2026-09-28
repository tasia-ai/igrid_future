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
* The above copyright notice and this permission notice and the "Credits" variable (see the * end of code) shall be included in all copies or substantial portions of the Software.
*
* THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
* IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
* FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
* AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
* LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
* OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
* SOFTWARE.
*/

/*
 * PORTOWANA WERSJA - przenośna dla starszych OpenSimów
 * ---------------------------------------------------
 * Oryginał: https://github.com/AdilElFarissi/opensim-ai-module
 *
 * Zmiany względem oryginału (wszystkie zachowują pełną funkcjonalność):
 *
 *  1. System.Collections.Frozen  -> zwykły Dictionary z StringComparer.OrdinalIgnoreCase
 *     (FrozenDictionary wymaga .NET 8; Dictionary działa wszędzie)
 *
 *  2. System.Text.Json           -> Newtonsoft.Json (Json.NET), który OpenSim
 *     już ma w swoim bin/ na każdej wersji
 *
 *  3. Składnia C# 9/11/12         -> C# 7.3
 *     (bez `new()`, bez `[...]`, bez `using var`, bez `out var`)
 *
 *  4. Drobne: `GetOwner()` na module NPC sprawdzany pod kątem null,
 *     oraz usunięty zbędny nagłówek Authorization z DefaultRequestHeaders
 *     (jest ustawiany per-request, co jest poprawniejsze).
 *
 * UWAGA DLA PORTUJĄCYCH: wywołanie CreateNPC używa sygnatury 10-argumentowej
 * (z agentID i groupTitle). OpenSim 0.9.x starszych wersji miał sygnaturę
 * 7-argumentową:
 *
 *   UUID CreateNPC(string firstname, string lastname, Vector3 position,
 *                  UUID owner, bool senseAsAgent, Scene scene,
 *                  AvatarAppearance appearance);
 *
 * Jeśli build nie przejdzie, podmień wywołanie w osCreateSmartNPC na wariant
 * 7-argumentowy (komentarz w kodzie przy wywołaniu).
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Mono.Addins;
using Nini.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

[assembly: Addin("OpenSimAIModule", "1.0")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace OpenSim.Region.OptionalModules.AI
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "OpenSimAIModule")]
    public class OpenSimAIModule : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly HttpClient m_httpClient = new HttpClient();
        private readonly List<Scene> m_scenes = new List<Scene>();

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
        private static readonly UUID chatBotID = new UUID(UUID.Random());
        private static readonly string chatBotName = "OpenSim AI";

        private class ChatTurn
        {
            public string role { get; set; }
            public string content { get; set; }
        }

        private readonly ConcurrentDictionary<UUID, string> m_npcList = new ConcurrentDictionary<UUID, string>();
        private static readonly ConcurrentDictionary<UUID, List<ChatTurn>> m_userHistories = new ConcurrentDictionary<UUID, List<ChatTurn>>();
        private const int MAX_HISTORY_TURNS = 10;
        private DateTime m_suspendUntil = DateTime.MinValue;
        private int remainingLimit = 50;
        private readonly object m_lockSuspend = new object();

        public string Name { get { return "OpenSimAIModule"; } }
        public Type ReplaceableInterface { get { return null; } }

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["OpenSimAI"];
            if (config == null)
                return;

            m_enabled = config.GetBoolean("Enabled", false);
            if (!m_enabled)
                return;

            m_apiKey = config.GetString("ApiKey", "");
            if (string.IsNullOrEmpty(m_apiKey))
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
            if (bankerUuidStr == UUID.ZeroString || !UUID.TryParse(bankerUuidStr, out m_bankerUuid))
                m_isMonetized = false;

            m_httpClient.Timeout = TimeSpan.FromSeconds(300);

            m_log.InfoFormat("[OpenSimAI]: Initialised. Model={0} Private={1} Monetized={2} Fallback='{3}'",
                m_modelName, m_isPrivate, m_isMonetized, m_fallbackModelName);
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            lock (m_scenes)
            {
                if (!m_scenes.Contains(scene))
                    m_scenes.Add(scene);
            }

            scene.EventManager.OnChatFromClient += OnNewPublicChatMessage;
            // UWAGA: celowo NIE subskrybujemy OnMakeRootAgent / OnMakeChildAgent.
            // AI nie wita sie z graczem przy wejściu do regionu - jest to natarczywe
            // i psuje immersje. AI odpowiada wylacznie wtedy, gdy ktos sie do niego
            // zwroci: przez @AI: na czacie, przez @Imie (SmartNPC) albo prywatnym IM.
            scene.EventManager.OnNewClient += OnNewClient;
            scene.EventManager.OnClientClosed += CleanMemoryOnClientClosed;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            lock (m_scenes)
            {
                if (m_scenes.Contains(scene))
                    m_scenes.Remove(scene);
            }

            scene.EventManager.OnChatFromClient -= OnNewPublicChatMessage;
            scene.EventManager.OnNewClient -= OnNewClient;
            scene.EventManager.OnClientClosed -= CleanMemoryOnClientClosed;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled)
                return;

            m_msgTransferModule = scene.RequestModuleInterface<IMessageTransferModule>();
            m_npcModule = scene.RequestModuleInterface<INPCModule>();
            m_scriptComms = scene.RequestModuleInterface<IScriptModuleComms>();

            if (m_scriptComms != null)
                m_scriptComms.RegisterScriptInvocations(this);

            m_money = scene.RequestModuleInterface<IMoneyModule>();

            if (m_npcModule == null)
                m_log.Warn("[OpenSimAI]: INPCModule not available - SmartNPC features will not work on this region.");

            if (m_msgTransferModule == null)
                m_log.Warn("[OpenSimAI]: IMessageTransferModule not available - instant messages will not work on this region.");
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
            m_httpClient.Dispose();
        }

        #region Events handlers

        private void OnNewClient(IClientAPI client)
        {
            client.OnInstantMessage += OnClientInstantMessage;
        }

        //
        // OnMakeAgent (powitanie przy wejściu do regionu) zostało celowo usunięte.
        // Powód: AI nie inicjuje rozmowy samo z siebie. Bez wzmianki (@AI:),
        // bez @Imienia SmartNPC i bez prywatnego IM AI milczy.
        // Historia rozmowy tworzy się sama w GetOrAdd przy pierwszym pytaniu,
        // więc nie potrzebowała wcześniejszego "zasiewania" przy wejściu.
        //

        private void CleanMemoryOnClientClosed(UUID agentId, Scene scene)
        {
            List<ChatTurn> ignored;
            m_userHistories.TryRemove(agentId, out ignored);
            string ignoredExpertise;
            m_npcList.TryRemove(agentId, out ignoredExpertise);
        }

        private void OnClientInstantMessage(IClientAPI client, GridInstantMessage im)
        {
            UUID agent = new UUID(im.toAgentID);
            UUID senderUuid = new UUID(im.fromAgentID);
            bool isEstateOwner = IsEstateOwner((Scene)client.Scene, senderUuid);

            if (m_isPrivate && !isEstateOwner)
                return;

            if ((im.toAgentID == chatBotID.Guid || m_npcList.ContainsKey(agent))
                && im.dialog == (byte)InstantMessageDialog.MessageFromAgent)
            {
                string userPrompt = im.message == null ? string.Empty : im.message.Trim();

                if (string.IsNullOrEmpty(userPrompt))
                    return;

                // kung-fu to bypass the presence detector...
                im.dialog = (byte)InstantMessageDialog.StartTyping;

                if (userPrompt.Equals("clear", StringComparison.CurrentCultureIgnoreCase)
                    || userPrompt.Equals("reset", StringComparison.CurrentCultureIgnoreCase))
                {
                    List<ChatTurn> ignored;
                    m_userHistories.TryRemove(senderUuid, out ignored);

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
                        SendInstantMessageFromNPC(agent, senderUuid, Credits);
                        return;
                    }

                    SendInstantMessage(senderUuid, Credits);
                    return;
                }

                if (m_npcModule != null)
                {
                    UUID npcOwner = m_npcModule.GetOwner(agent);

                    if (npcOwner == senderUuid
                        && userPrompt.StartsWith("#expertise:", StringComparison.CurrentCultureIgnoreCase))
                    {
                        string expertise = userPrompt.Substring(11).Trim().ToLower();

                        if (expertise == "list")
                        {
                            SendInstantMessageFromNPC(agent, senderUuid, ExpertiseList);
                            return;
                        }

                        if (SystemPrompts.ContainsKey(expertise))
                        {
                            if (UpdateNpcExpertise(agent, expertise))
                                SendInstantMessageFromNPC(agent, senderUuid, "Expertise successfully changed to: " + expertise + ".");
                        }
                        else
                        {
                            SendInstantMessageFromNPC(agent, senderUuid, "\nUnknown expertise: " + expertise + ".\n\n " + ExpertiseList);
                        }

                        return;
                    }
                }

                if (m_money != null && m_isMonetized && !isEstateOwner)
                {
                    if (!m_money.AmountCovered(client.AgentId, m_pricePerRequest))
                    {
                        client.SendAgentAlertMessage(
                            "You do not have enough money to use AI service! Please, load some funds and retry...", false);
                        return;
                    }
                }

                Task.Run(async () =>
                {
                    try
                    {
                        string expertise;
                        if (!m_npcList.TryGetValue(agent, out expertise))
                            expertise = "default";

                        string systemInstructions = GetSystemPromptByExpertise(expertise);

                        string aiResponse = await GenerateTextAsync(senderUuid, userPrompt, systemInstructions, 0, 0.1);

                        List<string> blocks = SplitTextIntoBlocks(aiResponse, 1000);

                        foreach (string block in blocks)
                        {
                            if (m_npcList.ContainsKey(agent))
                                SendInstantMessageFromNPC(agent, senderUuid, "\n" + block);
                            else
                                SendInstantMessage(senderUuid, "\n" + block);

                            await Task.Delay(300);
                        }

                        if (m_isMonetized && !isEstateOwner && blocks.Count > 0
                            && !aiResponse.Contains("The AI service is temporarily cooling down."))
                        {
                            m_money.MoveMoney(senderUuid, m_bankerUuid, m_pricePerRequest, "AI Service Request Fee");
                        }
                    }
                    catch (Exception ex)
                    {
                        m_log.Error("[OpenSim AI] Error during asynchronous processing : " + ex.Message + "\n " + ex.StackTrace);
                    }
                });
            }
        }

        private void OnNewPublicChatMessage(object sender, OSChatMessage e)
        {
            Scene scene = (Scene)e.Scene;

            if (m_isPrivate && !IsEstateOwner(scene, e.Sender.AgentId))
                return;

            if (e.Channel != 0 || e.Message == null)
                return;

            string rawChat = e.Message.TrimStart();

            //
            // @Imie - rozmowa ze SmartNPC w czacie publicznym.
            // Np. "@Bob jak tam pogoda?" -> odpowiada SmartNPC o imieniu "Bob".
            // Nazwa moze byc jedno- lub dwuwyrazowa ("@Jan Kowalski ...").
            // Nieznane @wzmianki sa cicho pomijane, zeby nie zasypywac czatu.
            //
            if (rawChat.StartsWith("@") && !rawChat.StartsWith("@AI:", StringComparison.InvariantCultureIgnoreCase))
            {
                string rest = rawChat.Substring(1).Trim();
                List<KeyValuePair<UUID, string>> matches;
                string npcPrompt;

                if (TryParseNpcChat(rest, out matches, out npcPrompt))
                {
                    if (matches.Count == 1)
                    {
                        HandleNpcPublicChat(scene, e, matches[0].Key, npcPrompt);
                    }
                    else
                    {
                        SendAmbiguousNpcHint(scene, e, matches);
                    }
                }

                return;
            }

            if (e.Channel == 0 && e.Message != null && e.Message.Trim().StartsWith("@AI:", StringComparison.InvariantCulture))
            {
                string userPrompt = e.Message.Substring(4).Trim();
                if (string.IsNullOrEmpty(userPrompt))
                    return;

                if (userPrompt.Equals("private", StringComparison.CurrentCultureIgnoreCase))
                {
                    SendInstantMessage(e.Sender.AgentId,
                        "\nHey " + e.Sender.Name
                        + ", I'm your OpenSim AI companion and copilot, designed to help you, answer your questions, and entertain you when you have nothing else to do...\n\n"
                        + "Please, ask your question in your language ! I will answer you in the same language...\n"
                        + "Note: You don't need to use (@AI:) keyword in this private chat window!\n "
                        + (m_isMonetized
                            ? "Warning!: Unfortunately, the AI service is not free for us and by extension, it's not for you either... The system will deduce from your balance "
                              + m_pricePerRequest + " currency unit per request. Use with moderation!\n"
                            : "\n"));
                    return;
                }

                if (userPrompt.Equals("credits", StringComparison.CurrentCultureIgnoreCase))
                {
                    scene.SimChat(Utils.StringToBytes(Credits), ChatTypeEnum.Owner, 0, e.Position, chatBotName, e.SenderUUID, false);
                    return;
                }

                if (userPrompt.Equals("npc", StringComparison.CurrentCultureIgnoreCase) ||
                    userPrompt.Equals("npcy", StringComparison.CurrentCultureIgnoreCase) ||
                    userPrompt.Equals("lista", StringComparison.CurrentCultureIgnoreCase))
                {
                    scene.SimChat(Utils.StringToBytes(BuildNpcList()), ChatTypeEnum.Owner, 0, e.Position, chatBotName, e.SenderUUID, false);
                    return;
                }

                Task.Run(async () =>
                {
                    string systemInstructions = GetSystemPromptByExpertise("default");

                    string aiResponse = await GenerateTextAsync(UUID.Zero, userPrompt, systemInstructions, 1024, 0.5);

                    List<string> blocks = SplitTextIntoBlocks(aiResponse, 1000);

                    foreach (string block in blocks)
                    {
                        scene.SimChat(Utils.StringToBytes("\n" + block), ChatTypeEnum.Owner, 0, e.Position, chatBotName, e.SenderUUID, false);
                        await Task.Delay(300);
                    }
                });
            }
        }

        #endregion

        #region SmartNPC w czacie publicznym

        /// <summary>
        /// Znajduje ScenePresence SmartNPC po kluczu UUID (tylko te utworzone przez osCreateSmartNPC).
        /// </summary>
        private ScenePresence FindSmartNpc(UUID npcKey)
        {
            ScenePresence found = null;

            lock (m_scenes)
            {
                foreach (Scene scene in m_scenes)
                {
                    ScenePresence sp = scene.GetScenePresence(npcKey);
                    if (sp != null)
                    {
                        found = sp;
                        break;
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// Parsuje tekst po "@" i sprawdza, czy zaczyna sie imieniem istniejacego
        /// SmartNPC. Obsluguje nazwy jednowyrazowe (pierwsze imie) i dwuwyrazowe
        /// ("Imie Nazwisko"). Zwraca wszystkie pasujace NPC, zeby caller mogl
        /// uprzedzic uzytkownika, gdy imie nie jest jednoznaczne.
        /// Zwraca false, gdy zaden NPC nie pasuje.
        /// </summary>
        private bool TryParseNpcChat(string text, out List<KeyValuePair<UUID, string>> matches, out string prompt)
        {
            matches = new List<KeyValuePair<UUID, string>>();
            prompt = string.Empty;

            if (string.IsNullOrEmpty(text))
                return false;

            string[] words = text.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                return false;

            // Najpierw probujemy pelne "Imie Nazwisko" (dwa slowa + pytanie)
            for (int take = Math.Min(2, words.Length); take >= 1; take--)
            {
                StringBuilder candidate = new StringBuilder();
                for (int i = 0; i < take; i++)
                {
                    if (i > 0)
                        candidate.Append(' ');
                    candidate.Append(words[i]);
                }

                List<KeyValuePair<UUID, string>> found = MatchNpcNames(candidate.ToString());
                if (found.Count == 0)
                    continue;

                // pytanie zaczyna sie dopiero za nazwa
                if (words.Length <= take)
                    return false; // samo "@Imie" bez pytania - cicho

                List<string> rest = new List<string>();
                for (int i = take; i < words.Length; i++)
                    rest.Add(words[i]);

                matches = found;
                prompt = string.Join(" ", rest.ToArray());
                return true;
            }

            return false;
        }

        /// <summary>
        /// Zwraca wszystkie SmartNPC pasujace do nazwy, posortowane alfabetycznie
        /// po pelnym imieniu (kolejnosc slownika ConcurrentDictionary jest losowa,
        /// a wynik musi byc powtarzalny).
        /// </summary>
        private List<KeyValuePair<UUID, string>> MatchNpcNames(string candidate)
        {
            List<KeyValuePair<UUID, string>> found = new List<KeyValuePair<UUID, string>>();

            if (string.IsNullOrEmpty(candidate))
                return found;

            foreach (KeyValuePair<UUID, string> entry in m_npcList)
            {
                ScenePresence sp = FindSmartNpc(entry.Key);
                if (sp == null)
                    continue;

                string fullName = sp.Name;
                string firstName = fullName;
                int space = firstName.IndexOf(' ');
                if (space > 0)
                    firstName = firstName.Substring(0, space);

                bool isFull = string.Equals(fullName, candidate, StringComparison.CurrentCultureIgnoreCase);
                bool isFirst = string.Equals(firstName, candidate, StringComparison.CurrentCultureIgnoreCase);

                if (isFull || isFirst)
                    found.Add(new KeyValuePair<UUID, string>(entry.Key, fullName));
            }

            // Pelne dopasowanie ma pierwszenstwo przed dopasowaniem po imieniu.
            found.Sort(delegate(KeyValuePair<UUID, string> a, KeyValuePair<UUID, string> b)
            {
                int c = string.Compare(a.Value, b.Value, StringComparison.CurrentCultureIgnoreCase);
                if (c != 0)
                    return c;

                bool aFull = a.Value.IndexOf(' ') < 0;
                bool bFull = b.Value.IndexOf(' ') < 0;
                if (aFull != bFull)
                    return aFull ? -1 : 1;

                return 0;
            });

            return found;
        }

        /// <summary>
        /// Więcej niż jeden NPC pasuje do @Imie - mówimy wprost, kto jest kto,
        /// zamiast losowo wybierać jednego.
        /// </summary>
        private void SendAmbiguousNpcHint(Scene scene, OSChatMessage e, List<KeyValuePair<UUID, string>> matches)
        {
            StringBuilder hint = new StringBuilder();
            hint.Append("\nUwaga: ta nazwa pasuje do kilku SmartNPC. Uzyj pelnego imienia:\n");

            foreach (KeyValuePair<UUID, string> m in matches)
                hint.Append("  @" + m.Value + "\n");

            scene.SimChat(Utils.StringToBytes(hint.ToString()), ChatTypeEnum.Owner, 0, e.Position, chatBotName, e.SenderUUID, false);
        }

        /// <summary>
        /// Lista SmartNPC dostepnych pod @Imie (wywolanie: "@AI: npc").
        /// </summary>
        private string BuildNpcList()
        {
            List<string> names = new List<string>();

            foreach (KeyValuePair<UUID, string> entry in m_npcList)
            {
                ScenePresence sp = FindSmartNpc(entry.Key);
                if (sp == null)
                    continue;

                string expertise;
                m_npcList.TryGetValue(entry.Key, out expertise);

                names.Add(sp.Name + " [" + (expertise ?? "default") + "]");
            }

            if (names.Count == 0)
                return "\nNie ma jeszcze zadnych SmartNPC w tym regionie.";

            names.Sort(StringComparer.CurrentCultureIgnoreCase);

            StringBuilder sb = new StringBuilder();
            sb.Append("\n[=>  SMART NPC (wpisz @Imie pytanie)  <=]\n\n");
            foreach (string n in names)
                sb.Append("@" + n + "\n");

            sb.Append("\nPelne nazwisko dziala takze wtedy, gdy imiona sie powtarzaja.");
            return sb.ToString();
        }

        /// <summary>
        /// Wysyla pytanie do SmartNPC i publikuje odpowiedz w czacie publicznym
        /// pod imieniem tego NPC (tak jak NPC odpowiada w oknie IM).
        /// </summary>
        private void HandleNpcPublicChat(Scene scene, OSChatMessage e, UUID npcKey, string prompt)
        {
            if (string.IsNullOrEmpty(prompt))
                return;

            ScenePresence sp = FindSmartNpc(npcKey);
            string npcName = sp == null ? "Smart NPC" : sp.Name;

            string expertise;
            if (!m_npcList.TryGetValue(npcKey, out expertise))
                expertise = "default";

            // Publiczny czat jest darmowy (tak jak @AI:), monetarization dziala w IM.
            Task.Run(async () =>
            {
                try
                {
                    string systemInstructions = GetSystemPromptByExpertise(expertise);

                    string aiResponse = await GenerateTextAsync(UUID.Zero, prompt, systemInstructions, 1024, 0.5);

                    List<string> blocks = SplitTextIntoBlocks(aiResponse, 1000);

                    foreach (string block in blocks)
                    {
                        scene.SimChat(Utils.StringToBytes("\n" + block), ChatTypeEnum.Owner, 0, e.Position, npcName, e.SenderUUID, false);
                        await Task.Delay(300);
                    }
                }
                catch (Exception ex)
                {
                    m_log.Error("[OpenSim AI] Error in @Npc public chat: " + ex.Message + "\n " + ex.StackTrace);
                }
            });
        }

        #endregion

        #region Script Functions

        [ScriptInvocation]
        public string osCreateSmartNPC(UUID hostID, UUID scriptID, string firstName, string lastName, Vector3 position, string notecardName, string expertise)
        {
            if (!m_enabled)
                return "OpenSim AI Module disabled.";

            if (m_npcModule == null)
                return UUID.ZeroString;

            UUID owner = UUID.Zero;
            Scene activeScene;
            SceneObjectPart sop;
            string appearanceLines = string.Empty;

            if (string.IsNullOrEmpty(expertise))
                expertise = "default";

            try
            {
                GetObjectData(hostID, out activeScene, out sop);

                if (activeScene == null || sop == null)
                    return UUID.ZeroString;

                owner = sop.OwnerID;

                if (m_isPrivate && !IsEstateOwner(activeScene, owner))
                    return UUID.ZeroString;

                if (!activeScene.Permissions.CanRezObject(1, owner, position))
                    return UUID.ZeroString;

                TaskInventoryItem item = sop.Inventory.GetInventoryItem(notecardName);

                if (item != null && item.Type == (int)AssetType.Notecard)
                {
                    AssetBase asset = activeScene.AssetService.Get(item.AssetID.ToString());
                    if (asset != null)
                    {
                        string[] lines = SLUtil.ParseNotecardToArray(asset.Data);
                        if (lines != null)
                            appearanceLines = string.Join("\n", lines);
                    }
                }

                OSDMap appOsd = OSDParser.DeserializeLLSDXml(appearanceLines) as OSDMap;

                AvatarAppearance appearance = new AvatarAppearance();
                appearance.Unpack(appOsd);

                // Sygnatura 10-argumentowa (OpenSim 0.9.x / master).
                // Jesli build nie przejdzie, uzyj wariantu 7-argumentowego:
                // UUID npcKey = m_npcModule.CreateNPC(firstName, lastName, position, owner, true, activeScene, appearance);
                UUID npcKey = m_npcModule.CreateNPC(firstName, lastName, position, UUID.Random(), owner,
                    string.Empty, UUID.Zero, true, activeScene, appearance);

                if (npcKey != UUID.Zero && !m_npcList.ContainsKey(npcKey) && activeScene.TryGetScenePresence(npcKey, out ScenePresence sp))
                {
                    m_npcList.TryAdd(npcKey, expertise.ToLower());
                    sp.SendAvatarDataToAllAgents();
                    return npcKey.ToString();
                }
            }
            catch (Exception ex)
            {
                m_log.Error("[OpenSim AI] Error while executing osCreateSmartNPC in " + hostID + " - " + owner + ": \n- " + ex.Message + "\n-" + ex.StackTrace);
            }

            return UUID.ZeroString;
        }

        [ScriptInvocation]
        public void osSetSmartNPC(UUID hostID, UUID scriptID, UUID npcKey, string expertise)
        {
            if (!m_enabled || !m_npcList.ContainsKey(npcKey))
                return;

            if (m_npcModule == null)
                return;

            Scene activeScene;
            SceneObjectPart sop;
            GetObjectData(hostID, out activeScene, out sop);

            if (string.IsNullOrEmpty(expertise))
                expertise = "default";

            try
            {
                if (activeScene == null || sop == null)
                    return;

                if (m_isPrivate && !IsEstateOwner(activeScene, sop.OwnerID))
                    return;

                UUID npcOwner = m_npcModule.GetOwner(npcKey);

                if (npcOwner != UUID.Zero && npcOwner == sop.OwnerID)
                {
                    if (UpdateNpcExpertise(npcKey, expertise))
                        SendInstantMessageFromNPC(npcKey, npcOwner, "Expertise successfully changed to: " + expertise + ".");

                    List<ChatTurn> ignored;
                    m_userHistories.TryRemove(npcOwner, out ignored);
                }
            }
            catch (Exception ex)
            {
                m_log.Error("[OpenSim AI] osSetSmartNPC in " + hostID + " failed to change expertise for " + npcKey + ": " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        [ScriptInvocation]
        public void osNpcInstantMessage(UUID hostID, UUID scriptID, UUID npcKey, UUID destination, string message)
        {
            if (!m_enabled)
                return;

            if (m_npcModule == null)
                return;

            Scene activeScene;
            SceneObjectPart sop;
            GetObjectData(hostID, out activeScene, out sop);

            if ((destination == UUID.Zero || string.IsNullOrEmpty(message)) && !m_npcList.ContainsKey(npcKey))
                return;

            try
            {
                if (activeScene == null || sop == null)
                    return;

                if (m_isPrivate && !IsEstateOwner(activeScene, sop.OwnerID))
                    return;

                UUID npcOwner = m_npcModule.GetOwner(npcKey);

                if (npcOwner != UUID.Zero && npcOwner == sop.OwnerID)
                    SendInstantMessageFromNPC(npcKey, destination, message);
            }
            catch (Exception ex)
            {
                m_log.Error("[OpenSim AI] osNpcInstantMessage in " + hostID + " failed to send IM from " + npcKey + " to " + destination + ": " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        [ScriptInvocation]
        public string osAI(UUID hostID, UUID scriptID, string systemPrompt, string userPrompt, int maxTokens, float temperature)
        {
            if (!m_enabled)
                return "OpenSim AI Module disabled.";

            Scene activeScene;
            SceneObjectPart sop;
            GetObjectData(hostID, out activeScene, out sop);

            if (activeScene == null || sop == null)
                return string.Empty;

            if (m_isPrivate && !IsEstateOwner(activeScene, sop.OwnerID))
                return "OpenSim AI Module is in Private Mode.";

            if (string.IsNullOrEmpty(userPrompt))
                return string.Empty;

            string systemInstructions = !string.IsNullOrEmpty(systemPrompt)
                ? systemPrompt
                : GetSystemPromptByExpertise("scripting");

            double temp;
            try
            {
                temp = Convert.ToDouble(temperature, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                temp = 0.5;
            }

            return Task.Run(async () => await GenerateTextAsync(UUID.Zero, userPrompt, systemInstructions, maxTokens, temp))
                .GetAwaiter().GetResult();
        }

        #endregion

        #region InstantMessage Communications

        private void SendInstantMessage(UUID targetUser, string message)
        {
            if (m_msgTransferModule == null)
                return;

            if (targetUser == UUID.Zero || string.IsNullOrEmpty(message))
                return;

            GridInstantMessage im = new GridInstantMessage();
            im.fromAgentID = chatBotID.Guid;
            im.fromAgentName = chatBotName;
            im.toAgentID = targetUser.Guid;
            im.dialog = (byte)InstantMessageDialog.MessageFromAgent;
            im.message = message;
            im.timestamp = (uint)Util.UnixTimeSinceEpoch();
            im.fromGroup = false;
            im.offline = (byte)0;
            im.ParentEstateID = 0;
            im.Position = Vector3.Zero;
            im.RegionID = UUID.Zero.Guid;
            im.binaryBucket = new byte[0];

            m_msgTransferModule.SendInstantMessage(im, delegate(bool success) { });
        }

        private void SendInstantMessageFromNPC(UUID npcId, UUID targetUser, string message)
        {
            if (m_msgTransferModule == null)
                return;

            Scene activeScene = null;
            ScenePresence sp = null;

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

            if (sp == null)
                return;

            GridInstantMessage im = new GridInstantMessage();
            im.fromAgentID = npcId.Guid;
            im.fromAgentName = chatBotName;
            im.toAgentID = targetUser.Guid;
            im.dialog = (byte)InstantMessageDialog.MessageFromAgent;
            im.message = message;
            im.RegionID = activeScene.RegionInfo.RegionID.Guid;

            m_msgTransferModule.SendInstantMessage(im, delegate(bool success) { });
        }

        #endregion

        #region AI Communications

        private async Task<string> GenerateTextAsync(UUID avatarId, string userPrompt, string systemPrompt, int maxTokens, double temperature)
        {
            string currentModel = m_modelName;

            if (DateTime.UtcNow < m_suspendUntil)
            {
                double remainingSeconds = Math.Ceiling((m_suspendUntil - DateTime.UtcNow).TotalSeconds);

                if (currentModel == m_modelName && remainingLimit == 0 && m_fallbackModelName != string.Empty)
                    currentModel = m_fallbackModelName;
                else
                {
                    m_log.Warn("[OpenSim AI] Request rejected locally. Service suspended for " + remainingSeconds + " secondes.");
                    return "The AI service is temporarily cooling down. Please try again in " + remainingSeconds + " seconds...";
                }
            }

            int maxAttempts = 3;
            int currentAttempt = 0;

            while (currentAttempt < maxAttempts)
            {
                currentAttempt++;

                List<object> messageList = new List<object>();
                object[] message = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                };

                List<ChatTurn> history = m_userHistories.GetOrAdd(avatarId, new List<ChatTurn>());

                if (avatarId != UUID.Zero)
                {
                    messageList.Add(new { role = "system", content = systemPrompt });

                    lock (history)
                    {
                        foreach (ChatTurn turn in history)
                            messageList.Add(new { role = turn.role, content = turn.content });
                    }

                    messageList.Add(new { role = "user", content = userPrompt });
                    message = messageList.ToArray();
                }

                object payload = new
                {
                    model = currentModel,
                    messages = message,
                    temperature = (temperature < 2.0 && temperature > 0) ? temperature : 0.1,
                    max_tokens = maxTokens > 0 ? maxTokens : 4096
                };

                string jsonPayload = JsonConvert.SerializeObject(payload);

                try
                {
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, m_apiUrl))
                    {
                        request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", m_apiKey);
                        request.Headers.Add("X-Title", "OpenSim AI Module");

                        using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                        using (HttpResponseMessage response = await m_httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                string responseJson = await response.Content.ReadAsStringAsync();

                                if (responseJson.Contains("User Safety:") || responseJson.Contains("Response Safety:"))
                                {
                                    m_log.Warn("[OpenSim AI] Security false positive detected (Attempts " + currentAttempt + "/" + maxAttempts + "). Request resubmit...");
                                    await Task.Delay(1000);
                                    continue;
                                }

                                string contentStr = string.Empty;

                                JObject root = JObject.Parse(responseJson);
                                JArray choices = root["choices"] as JArray;

                                if (choices != null && choices.Count > 0)
                                {
                                    JToken firstChoice = choices[0];
                                    JToken messageToken = firstChoice["message"];
                                    JToken contentToken = (messageToken == null) ? null : messageToken["content"];

                                    if (contentToken != null)
                                    {
                                        contentStr = contentToken.Value<string>();
                                        if (contentStr != null)
                                            contentStr = contentStr.Trim();
                                    }
                                }

                                if (string.IsNullOrEmpty(contentStr))
                                {
                                    m_log.Warn("[OpenSim AI] Empty response or invalid JSON structure (Attempts: " + currentAttempt + "/" + maxAttempts + "). New attempt...");
                                    await Task.Delay(1000);
                                    continue;
                                }

                                lock (history)
                                {
                                    ChatTurn userTurn = new ChatTurn();
                                    userTurn.role = "user";
                                    userTurn.content = userPrompt;
                                    history.Add(userTurn);

                                    ChatTurn assistantTurn = new ChatTurn();
                                    assistantTurn.role = "assistant";
                                    assistantTurn.content = contentStr;
                                    history.Add(assistantTurn);

                                    while (history.Count > MAX_HISTORY_TURNS * 2)
                                        history.RemoveAt(0);
                                }

                                return contentStr;
                            }

                            if ((int)response.StatusCode == 429)
                            {
                                int retryAfterSeconds = 10;
                                IEnumerable<string> values;

                                if (response.Headers.TryGetValues("Retry-After", out values))
                                {
                                    string first = values.FirstOrDefault();
                                    int parsedSeconds;
                                    if (int.TryParse(first, out parsedSeconds) && parsedSeconds > 0)
                                        retryAfterSeconds = parsedSeconds;
                                }

                                if (response.Headers.TryGetValues("X-RateLimit-Remaining", out values))
                                {
                                    string first = values.FirstOrDefault();
                                    int limit;
                                    if (int.TryParse(first, out limit))
                                        remainingLimit = limit;
                                }

                                lock (m_lockSuspend)
                                {
                                    m_suspendUntil = DateTime.UtcNow.AddSeconds(retryAfterSeconds);
                                }

                                m_log.Error("[OpenSim AI] Request quota or limit reached. Service suspended for " + retryAfterSeconds + " seconds.");

                                return "The AI service is temporarily unavailable due to rate limits. Service will resume in " + retryAfterSeconds + " seconds...";
                            }

                            string responseerror = await response.Content.ReadAsStringAsync();
                            m_log.Warn("[OpenSim AI] API Error: " + (int)response.StatusCode + " - Details: " + responseerror);
                            return "Sorry, a technical error has occurred. Please try again...";
                        }
                    }
                }
                catch (Exception e)
                {
                    m_log.Warn("[OpenSim AI] API Exception: " + e.Message + "\n " + e.StackTrace);
                    return "[OpenSim AI] API Exception: " + e.Message;
                }
            }

            return string.Empty;
        }

        #endregion

        #region Helpers

        private void GetObjectData(UUID hostID, out Scene activeScene, out SceneObjectPart sop)
        {
            activeScene = null;
            sop = null;

            lock (m_scenes)
            {
                foreach (Scene scene in m_scenes)
                {
                    SceneObjectPart part = scene.GetSceneObjectPart(hostID);
                    if (part != null)
                    {
                        sop = part;
                        activeScene = scene;
                    }

                    break;
                }
            }
        }

        private static bool IsEstateOwner(Scene scene, UUID avatar)
        {
            if (scene == null)
                return false;

            return avatar == scene.RegionInfo.EstateSettings.EstateOwner;
        }

        private static List<string> SplitTextIntoBlocks(string text, int maxBytes)
        {
            List<string> blocks = new List<string>();

            if (string.IsNullOrEmpty(text))
                return blocks;

            string[] words = text.Split(' ');
            StringBuilder currentBlock = new StringBuilder();

            foreach (string word in words)
            {
                string testStr = currentBlock.Length == 0
                    ? word
                    : currentBlock.ToString() + " " + word;

                if (Encoding.UTF8.GetByteCount(testStr) > maxBytes)
                {
                    if (currentBlock.Length > 0)
                    {
                        blocks.Add(currentBlock.ToString());
                        currentBlock.Remove(0, currentBlock.Length);
                    }

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

        private static string GetSystemPromptByExpertise(string expertise)
        {
            string prompt;
            if (expertise != null && SystemPrompts.TryGetValue(expertise, out prompt))
                return prompt;

            return "You are an expert AI companion specializing in the development of virtual worlds and roleplays based on OpenSimulator. Your first mission is to help newcomers understand the features available to them, explain, inform, and answer questions. Your secondary mission is to entertain, amuse, and lead discussions. Always return the result in the same language as the user prompt and in a friendly, cheerful and informative way.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks.";
        }

        private bool UpdateNpcExpertise(UUID npc, string expertise)
        {
            if (npc == UUID.Zero || string.IsNullOrEmpty(expertise) || !SystemPrompts.ContainsKey(expertise.ToLower()))
                return false;

            m_npcList[npc] = expertise.ToLower();
            return true;
        }

        // Zwykly Dictionary z OrdinalIgnoreCase zamiast FrozenDictionary (.NET 8 only).
        private static readonly Dictionary<string, string> SystemPrompts = CreateSystemPrompts();

        private static Dictionary<string, string> CreateSystemPrompts()
        {
            Dictionary<string, string> prompts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            prompts["default"] = "You are an expert AI companion specializing in the development of virtual worlds and roleplays based on OpenSimulator. Your first mission is to help newcomers understand the features available to them, explain, inform, and answer questions. Your secondary mission is to entertain, amuse, and lead discussions. Always return the result in the same language as the user prompt and in a friendly, cheerful and informative way.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks.";
            prompts["avatars"] = "You are an AI expert in Second Life and OpenSimulator avatars creation, anatomy, and customization for virtual worlds. You are highly skilled in morphing systems (shapes), skin textures, avatar baking, Baked on Mesh (BOM) body systems, and attachment management.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks.";
            prompts["scripting"] = "You are a systems development engineer specialized in OpenSimulator scripting (LSL, OSSL). You write optimized, lag-free code, expertly managing zone events, network listeners (HTTP/XML-RPC), sensors, and regional database interactions.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks.";
            prompts["building"] = "You are a 3D architect and virtual environment designer. Expert in Second Life and OpenSimulator Mesh modeling (Blender/Maya), Collada (.dae) imports, Level of Detail (LOD) management, collision physics (Physics Shape), and land impact (prim weight) optimization.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks.";
            prompts["texturing"] = "You are a technical artist expert in Second Life and OpenSimulator PBR (Physically Based Rendering) textures and materials applied to virtual worlds. You master albedo, normal, roughness, and metallic maps, as well as animated textures and advanced lighting effects on primitives.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks.";
            prompts["animations"] = "You are a 3D animator specialized in rigging and avatar skeletons for virtual worlds. Expert in creating and editing BVH/ANIM animations, managing animation priorities, creating AO (Animation Overriders), and configuring autonomous animated objects via Animesh technology.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks.";
            prompts["environment"] = "You are an estate manager and region terraformer for OpenSimulator. You master region file configuration (Regions.ini), parcel management (land), access rights, terrain modification (RAW files), environment control (Windlight/EnvSet), and server performance optimization.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks.";
            prompts["economy"] = "You are an economic consultant specialized in inSimulator virtual markets and virtual currencies (Gloebit, OMC, local currency). You advise on content monetization, vendor systems, intellectual property protection (open-world DRM), and event marketing within the metaverse.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks.";
            prompts["community"] = "You are a community manager and cultural/educational event organizer in OpenSimulator virtual worlds. Expert in group management, roleplay system design, in-world audio/video streaming server configuration, and new user onboarding.\n\nClean the response string for Second Life / OpenSim viewer chat protocol. Remove internal control codes. Return only the raw text with hyperlinks and no markdown or code blocks.";

            return prompts;
        }

        private readonly string ExpertiseList = "\n[=>  AVAILABLE EXPERTISES LIST  <=]\n\ndefault : Set a generalist AI, expert in OpenSim.\n\navatars : Set an expert AI in avatars design and customization.\n\nscripting : Set an expert AI in LSL and OSSL scripting.\n\nbuilding : Set an expert AI in building & meshes creation.\n\ntexturing : Set an expert AI in texturing & PBR materials.\n\nanimations : Set an expert AI in creating BVH/ANIM animations.\n\nenvironment : Set an expert AI in scene/world customization, terraforming, environment...\n\neconomy : Set an expert AI in in-world economy and marketing.\n\ncommunity : Set an expert AI in events and groups management.\n";

        // Part of the copyright! Please, don't remove or alter.
        private readonly string Credits = "\nMade in Morocco with fun & love by Adil El Farissi (aka: Web Rain @ OsGrid/SL) https://github.com/AdilElFarissi \n\nThe OpenSim AI Module is under MIT License.";

        #endregion
    }
}
