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
using System.Linq;
using System.Reflection;
using System.Timers;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Timer=System.Timers.Timer;
namespace TasiaAddons.RestartModule
{
    public class RestartModule : INonSharedRegionModule, IRestartModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected Scene m_Scene;
        protected Timer m_CountdownTimer = null;
        protected DateTime m_RestartBegin;
        protected List<int> m_Alerts;
        protected string m_Message;
        protected UUID m_Initiator;
        protected bool m_Notice = false;
        protected IDialogModule m_DialogModule = null;
        protected string m_MarkerPath = String.Empty;
        private int[] m_CurrentAlerts = null;
        protected bool m_shortCircuitDelays = false;
        private bool m_isShutdown = false;
        protected bool m_rebootAll = false;

        private bool m_apiEnabled = false;
        private string m_apiToken = string.Empty;
        private string m_apiPathPrefix = "/tasia-ngc/restart";
        private int m_defaultDelaySeconds = 180;
        private string m_apiPath = string.Empty;
        private readonly object m_stateSync = new();
        private DateTime? m_pendingRestartUtc;
        private string m_pendingReason = string.Empty;
        private UUID m_pendingRequestedBy = UUID.Zero;

        private bool m_abuseForwardEnabled = false;
        private string m_abuseEndpointUrl = string.Empty;
        private string m_abuseApiToken = string.Empty;
        private string m_assetProbeBaseUrl = string.Empty;
        private static readonly HttpClient s_httpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
        private readonly object m_clientSubSync = new();
        private readonly Dictionary<UUID, IClientAPI> m_reportClients = new();
        private readonly SemaphoreSlim m_abuseForwardGate = new(8, 8);

        public void Initialise(IConfigSource config)
        {
            IConfig restartConfig = config.Configs["RestartModule"];
            if (restartConfig != null)
            {
                m_MarkerPath = restartConfig.GetString("MarkerPath", String.Empty);
                m_apiEnabled = restartConfig.GetBoolean("ApiEnabled", false);
                m_apiToken = restartConfig.GetString("ApiToken", string.Empty);
                m_apiPathPrefix = restartConfig.GetString("ApiPathPrefix", "/tasia-ngc/restart");
                m_defaultDelaySeconds = restartConfig.GetInt("DefaultDelaySeconds", 180);
                if (m_defaultDelaySeconds < 10)
                    m_defaultDelaySeconds = 10;

                if (m_apiToken == "CHANGE_ME_RESTART_TOKEN")
                    m_apiToken = string.Empty;
            }

            IConfig abuseConfig = config.Configs["AbuseReportForwarder"];
            if (abuseConfig != null)
            {
                m_abuseForwardEnabled = abuseConfig.GetBoolean("Enabled", false);
                m_abuseEndpointUrl = abuseConfig.GetString("EndpointUrl", string.Empty).Trim();
                m_abuseApiToken = abuseConfig.GetString("ApiToken", string.Empty).Trim();

                if (m_abuseApiToken == "CHANGE_ME_ABUSE_TOKEN")
                    m_abuseApiToken = string.Empty;

                if (m_abuseForwardEnabled && string.IsNullOrWhiteSpace(m_abuseEndpointUrl))
                {
                    m_log.Warn("[RESTART MODULE]: AbuseReportForwarder enabled but EndpointUrl is empty; forwarding disabled.");
                    m_abuseForwardEnabled = false;
                }
            }

            IConfig startupConfig = config.Configs["Startup"];
            m_shortCircuitDelays = startupConfig.GetBoolean("SkipDelayOnEmptyRegion", false);
            m_rebootAll = startupConfig.GetBoolean("InworldRestartShutsDown", false);

            IConfig assetConfig = config.Configs["AssetService"];
            if (assetConfig != null)
            {
                m_assetProbeBaseUrl = (assetConfig.GetString("AssetServerURI", string.Empty) ?? string.Empty).Trim();
                m_assetProbeBaseUrl = m_assetProbeBaseUrl.TrimEnd('/');
            }
        }

        public void AddRegion(Scene scene)
        {
            if (m_MarkerPath != String.Empty)
                File.Delete(Path.Combine(m_MarkerPath,
                        scene.RegionInfo.RegionID.ToString()));

            m_Scene = scene;

            scene.RegisterModuleInterface<IRestartModule>(this);

            MainConsole.Instance.Commands.AddCommand("Regions",
                    false, "region restart notice",
                    "region restart notice <delta seconds>+",
                    "Schedule a region restart",
                    "Schedule a region restart after a given number of seconds.  The region is restarted in delta seconds time.",
                    HandleRegionRestart);

            MainConsole.Instance.Commands.AddCommand("Regions",
                    false, "region restart abort",
                    "region restart abort [<message>]",
                    "Abort a region restart", HandleRegionRestart);

            scene.EventManager.OnNewClient += OnNewClient;
            scene.EventManager.OnClientClosed += OnClientClosed;

            RegisterApiHandler();
        }

        public void RegionLoaded(Scene scene)
        {
            m_DialogModule = m_Scene.RequestModuleInterface<IDialogModule>();
        }

        public void RemoveRegion(Scene scene)
        {
            scene.EventManager.OnNewClient -= OnNewClient;
            scene.EventManager.OnClientClosed -= OnClientClosed;

            lock (m_clientSubSync)
            {
                foreach (IClientAPI client in m_reportClients.Values)
                {
                    client.OnUserReport -= OnUserReport;
                }
                m_reportClients.Clear();
            }

            StopAndDisposeCountdownTimer();

            if (!string.IsNullOrWhiteSpace(m_apiPath))
            {
                try
                {
                    MainServer.Instance.DefaultServer.RemoveSimpleStreamHandler(m_apiPath);
                }
                catch (Exception ex)
                {
                    m_log.WarnFormat("[RESTART MODULE]: Failed to remove API handler {0}: {1}", m_apiPath, ex.Message);
                }
            }
        }

        public void Close()
        {
            StopAndDisposeCountdownTimer();
            lock (m_clientSubSync)
            {
                foreach (IClientAPI client in m_reportClients.Values)
                    client.OnUserReport -= OnUserReport;
                m_reportClients.Clear();
            }
            m_abuseForwardGate.Dispose();
        }

        public string Name
        {
            get { return "RestartModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return typeof(IRestartModule); }
        }

        public TimeSpan TimeUntilRestart
        {
            get { return DateTime.Now - m_RestartBegin; }
        }

        public void ScheduleRestart(UUID initiator, string message, int[] alerts, bool notice)
        {
            StopAndDisposeCountdownTimer();

            if (alerts == null || alerts.Length == 0)
            {
                ClearPendingRestartState();
                CreateMarkerFile();
                m_Scene.RestartNow();
                return;
            }

            m_Message = message;
            m_Initiator = initiator;
            m_Notice = notice;
            m_CurrentAlerts = alerts;
            m_Alerts = new List<int>(alerts);
            m_Alerts.Sort();
            m_Alerts.Reverse();

            if (m_Alerts[0] == 0)
            {
                ClearPendingRestartState();
                CreateMarkerFile();
                m_Scene.RestartNow();
                return;
            }

            SetPendingRestartState(m_Alerts[0], message, initiator);

            int nextInterval = DoOneNotice(true);

            SetTimer(nextInterval);
        }

        public void ScheduleRestart(UUID initiator, int seconds)
        {
            StopAndDisposeCountdownTimer();

            if (seconds == 0)
            {
                ClearPendingRestartState();
                CreateMarkerFile();
                m_Scene.RestartNow();
                return;
            }


            int[] alerts = BuildStandardAlerts(seconds);
			
            m_Initiator = initiator;
            m_CurrentAlerts = alerts;
            m_Alerts = new List<int>(alerts);
            m_Alerts.Sort();
            m_Alerts.Reverse();

            if (m_Alerts[0] == 0)
            {
                ClearPendingRestartState();
                CreateMarkerFile();
                m_Scene.RestartNow();
                return;
            }

            SetPendingRestartState(m_Alerts[0], string.Empty, initiator);

            int nextInterval = DoOneNotice(true);

            SetTimer(nextInterval);
        }

        public int DoOneNotice(bool sendOut)
        {
            if (m_Alerts.Count == 0 || m_Alerts[0] == 0)
            {
                ClearPendingRestartState();
                if (m_isShutdown)
                {
                    // Shutdown: backup + quit, no hot restart
                    CreateMarkerFile();
                    DoBackupAndQuit();
                }
                else
                {
                    CreateMarkerFile();
                    m_Scene.RestartNow();
                }
                return 0;
            }

            int nextAlert = 0;
            while (m_Alerts.Count > 1)
            {
                if (m_Alerts[1] == m_Alerts[0])
                {
                    m_Alerts.RemoveAt(0);
                    continue;
                }
                nextAlert = m_Alerts[1];
                break;
            }

            int currentAlert = m_Alerts[0];

            m_Alerts.RemoveAt(0);

            if (sendOut)
            {
                const string msgId = "RegionRestartSeconds";
                OSDMap osd = new();
                osd.Add("NAME", m_Scene.RegionInfo.RegionName);
                osd.Add("SECONDS", currentAlert);
                byte[] extra = Util.StringToBytes256(OSDParser.SerializeLLSDXmlString(osd));

                m_Scene.ForEachRootClient(delegate (IClientAPI client)
                {
                    try
                    {
                        MethodInfo mi = client.GetType().GetMethod(
                            "SendAlertMessage",
                            new[] { typeof(string), typeof(string), typeof(byte[]) });

                        if (mi != null)
                            mi.Invoke(client, new object[] { msgId, msgId, extra });
                        else
                            client.SendAlertMessage(msgId, currentAlert.ToString());
                    }
                    catch
                    {
                        client.SendAlertMessage(msgId, currentAlert.ToString());
                    }
                });

                string currentAlertString = FormatCountdown(currentAlert);
                m_log.InfoFormat("{0} will restart in {1}", m_Scene.Name, currentAlertString);
            }

            return currentAlert - nextAlert;
        }

        public void SetTimer(int intervalSeconds)
        {
            if (intervalSeconds > 0)
            {
                StopAndDisposeCountdownTimer();
                m_CountdownTimer = new Timer();
                m_CountdownTimer.AutoReset = false;
                m_CountdownTimer.Interval = intervalSeconds * 1000;
                m_CountdownTimer.Elapsed += OnTimer;
                m_CountdownTimer.Start();
            }
            else if (m_CountdownTimer != null)
                StopAndDisposeCountdownTimer();
            else
            {
                m_log.WarnFormat(
                    "[RESTART MODULE]: Tried to set restart timer to {0} in {1}, which is not a valid interval",
                    intervalSeconds, m_Scene.Name);
            }
        }

        private void OnTimer(object source, ElapsedEventArgs e)
        {
            int nextInterval = DoOneNotice(true);
            if (m_shortCircuitDelays)
            {
                if (CountAgents() == 0)
                {
                    ClearPendingRestartState();
                    if (m_isShutdown)
                        DoBackupAndQuit();
                    else
                        m_Scene.RestartNow();
                    return;
                }
            }

            if (nextInterval <= 0)
            {
                ClearPendingRestartState();
                if (m_isShutdown)
                    DoBackupAndQuit();
                else
                    m_Scene.RestartNow();
                return;
            }

            SetTimer(nextInterval);
        }

        private void DoBackupAndQuit()
        {
            m_log.Info("[RESTART MODULE]: Running backup before shutdown...");
            try
            {
                MainConsole.Instance.RunCommand("backup");
                m_log.Info("[RESTART MODULE]: Backup complete");
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("[RESTART MODULE]: Backup failed: {0}", ex.Message);
            }

            // Kill the python service app inside the container
            try
            {
                System.Diagnostics.Process.Start("pkill", "-f python3");
                m_log.Info("[RESTART MODULE]: Killed python3 process");
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("[RESTART MODULE]: Failed to kill python3: {0}", ex.Message);
            }

            // Quit OpenSim â€” container stays alive (sleep infinity)
            try
            {
                MainConsole.Instance.RunCommand("quit");
            }
            catch
            {
                Environment.Exit(0);
            }
        }

        public void DelayRestart(int seconds, string message)
        {
            if (m_CountdownTimer == null)
                return;

            MainConsole.Instance.Output("Region restart delayed for " + seconds.ToString() + " seconds");
            
            if (m_DialogModule != null)
                m_DialogModule.SendNotificationToUsersInRegion(UUID.Zero, "System", "Region restart has been delayed.");

            StopAndDisposeCountdownTimer();

            m_Alerts = new List<int>(m_CurrentAlerts);
            m_Alerts.Add(seconds);
            m_Alerts.Sort();
            m_Alerts.Reverse();

            if (m_Alerts.Count > 0)
                SetPendingRestartState(m_Alerts[0], message, m_Initiator);

            int nextInterval = DoOneNotice(false);

            SetTimer(nextInterval);
        }

        public void AbortRestart(string message)
        {
            if (m_CountdownTimer != null)
            {
                StopAndDisposeCountdownTimer();
                if (m_DialogModule != null && message != String.Empty)
                    m_DialogModule.SendNotificationToUsersInRegion(UUID.Zero, "System", message);
                    //m_DialogModule.SendGeneralAlert(message);
            }
            if (m_MarkerPath != String.Empty)
                File.Delete(Path.Combine(m_MarkerPath,
                        m_Scene.RegionInfo.RegionID.ToString()));

            ClearPendingRestartState();
        }

        private void HandleRegionRestart(string module, string[] args)
        {
            if (!(MainConsole.Instance.ConsoleScene is Scene))
                return;

            if (MainConsole.Instance.ConsoleScene != m_Scene)
                return;

            int seconds = 120;

            if (args.Length >= 3)
            {
                if (args[2] == "abort")
                {
                    string msg = String.Empty;
                    if (args.Length > 3)
                        msg = args[3];

                    AbortRestart(msg);

                    MainConsole.Instance.Output("Region restart aborted");
                    return;
                }
                else if (!int.TryParse(args[3], out seconds))
                {
                    MainConsole.Instance.Output("Error: restart region <abort/delta seconds>");
                    return;
                }
            }

            MainConsole.Instance.Output("Region {0} scheduled for restart in {1} seconds", null, m_Scene.Name, seconds);

            ScheduleRestart(UUID.Zero, seconds);
        }

        private void OnNewClient(IClientAPI client)
        {
            if (!m_abuseForwardEnabled || client == null)
                return;

            lock (m_clientSubSync)
            {
                if (m_reportClients.TryGetValue(client.AgentId, out IClientAPI existingClient) && !ReferenceEquals(existingClient, client))
                    existingClient.OnUserReport -= OnUserReport;

                client.OnUserReport -= OnUserReport;
                client.OnUserReport += OnUserReport;
                m_reportClients[client.AgentId] = client;
            }
        }

        private void OnClientClosed(UUID agentId, Scene scene)
        {
            if (!m_abuseForwardEnabled || scene == null)
                return;

            lock (m_clientSubSync)
            {
                if (m_reportClients.TryGetValue(agentId, out IClientAPI client))
                {
                    client.OnUserReport -= OnUserReport;
                    m_reportClients.Remove(agentId);
                    return;
                }
            }

            if (scene.TryGetClient(agentId, out IClientAPI fallbackClient))
                fallbackClient.OnUserReport -= OnUserReport;
        }

        private void OnUserReport(IClientAPI client,
            string regionName,
            UUID abuserID,
            byte category,
            byte checkflags,
            string details,
            UUID objectID,
            Vector3 position,
            byte reportType,
            UUID screenshotID,
            string summary,
            UUID reporter)
        {
            if (!m_abuseForwardEnabled || string.IsNullOrWhiteSpace(m_abuseEndpointUrl))
                return;

            if (!m_abuseForwardGate.Wait(0))
                return;

            string reporterName = client?.Name ?? string.Empty;

            _ = Task.Run(async () =>
            {
                try
                {
                    (UUID resolvedScreenshotId, string screenshotStatus) = await ResolveScreenshotUuidAsync(reporter, screenshotID).ConfigureAwait(false);
                    await ForwardAbuseReportAsync(
                        regionName,
                        abuserID,
                        category,
                        checkflags,
                        details,
                        objectID,
                        position,
                        reportType,
                        resolvedScreenshotId,
                        screenshotID,
                        screenshotStatus,
                        summary,
                        reporter,
                        reporterName).ConfigureAwait(false);
                }
                finally
                {
                    m_abuseForwardGate.Release();
                }
            });
        }

        private void StopAndDisposeCountdownTimer()
        {
            Timer timer = m_CountdownTimer;
            if (timer == null)
                return;

            m_CountdownTimer = null;
            try
            {
                timer.Stop();
                timer.Elapsed -= OnTimer;
                timer.Dispose();
            }
            catch
            {
            }
        }

        private async Task<(UUID resolvedId, string status)> ResolveScreenshotUuidAsync(UUID reporterId, UUID screenshotId)
        {
            if (screenshotId.IsZero())
                return (UUID.Zero, "none");

            UUID mappedAssetId = TryResolveScreenshotFromInventory(reporterId, screenshotId);
            UUID[] candidates = mappedAssetId.IsZero()
                ? new[] { screenshotId }
                : new[] { screenshotId, mappedAssetId };

            for (int attempt = 0; attempt < 20; attempt++)
            {
                foreach (UUID candidateId in candidates)
                {
                    if (TryPromoteScreenshotAsset(candidateId, reporterId, out UUID promotedId))
                    {
                        string status = candidateId == screenshotId
                            ? "promoted"
                            : "promoted_from_inventory";

                        return (promotedId, status);
                    }
                }

                await Task.Delay(1000).ConfigureAwait(false);
            }

            return (UUID.Zero, mappedAssetId.IsZero() ? "missing" : "mapped_missing");
        }

        private UUID TryResolveScreenshotFromInventory(UUID reporterId, UUID screenshotId)
        {
            try
            {
                if (reporterId.IsZero() || m_Scene?.InventoryService == null)
                    return UUID.Zero;

                InventoryItemBase item = m_Scene.InventoryService.GetItem(reporterId, screenshotId);
                if (item == null || item.AssetID.IsZero())
                    return UUID.Zero;

                return item.AssetID;
            }
            catch
            {
                return UUID.Zero;
            }
        }

        private bool AssetExists(UUID assetId)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(m_assetProbeBaseUrl))
                {
                    string directUrl = m_assetProbeBaseUrl + "/" + assetId;
                    if (ProbeAssetUrl(directUrl))
                        return true;

                    string assetsUrl = m_assetProbeBaseUrl.EndsWith("/assets", StringComparison.OrdinalIgnoreCase)
                        ? m_assetProbeBaseUrl + "/" + assetId
                        : m_assetProbeBaseUrl + "/assets/" + assetId;

                    if (ProbeAssetUrl(assetsUrl))
                        return true;
                }

                if (m_Scene?.AssetService == null)
                    return false;

                AssetBase asset = m_Scene.AssetService.Get(assetId.ToString());
                return asset != null && asset.Data != null && asset.Data.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool ProbeAssetUrl(string url)
        {
            try
            {
                using HttpRequestMessage req = new(HttpMethod.Get, url);
                using HttpResponseMessage resp = s_httpClient.Send(req);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private bool TryPromoteScreenshotAsset(UUID sourceAssetId, UUID reporterId, out UUID promotedId)
        {
            promotedId = UUID.Zero;

            try
            {
                if (m_Scene?.AssetService == null || sourceAssetId.IsZero())
                    return false;

                AssetBase sourceAsset = m_Scene.AssetService.Get(sourceAssetId.ToString());
                if (sourceAsset == null || sourceAsset.Data == null || sourceAsset.Data.Length == 0)
                    return false;

                sbyte promotedType = IsImageAssetType(sourceAsset.Type)
                    ? sourceAsset.Type
                    : (sbyte)AssetType.Texture;

                string creatorId = reporterId.IsZero() ? sourceAsset.CreatorID : reporterId.ToString();
                if (string.IsNullOrWhiteSpace(creatorId))
                    creatorId = UUID.Zero.ToString();

                AssetBase inPlace = new(sourceAssetId, sourceAsset.Name ?? "abuse-report-screenshot", promotedType, creatorId)
                {
                    Description = sourceAsset.Description ?? "Promoted abuse report screenshot",
                    Data = sourceAsset.Data,
                    Local = false,
                    Temporary = false,
                    Flags = sourceAsset.Flags
                };

                m_Scene.AssetService.Store(inPlace);
                if (AssetExists(sourceAssetId))
                {
                    promotedId = sourceAssetId;
                    return true;
                }

                UUID newAssetId = UUID.Random();

                AssetBase promoted = new(newAssetId, "abuse-report-screenshot", promotedType, creatorId)
                {
                    Description = "Promoted abuse report screenshot",
                    Data = sourceAsset.Data,
                    Local = false,
                    Temporary = false,
                    Flags = sourceAsset.Flags
                };

                m_Scene.AssetService.Store(promoted);

                AssetBase verify = m_Scene.AssetService.Get(newAssetId.ToString());
                if (verify == null || verify.Data == null || verify.Data.Length == 0)
                    return false;

                for (int i = 0; i < 10; i++)
                {
                    if (AssetExists(newAssetId))
                    {
                        promotedId = newAssetId;
                        return true;
                    }

                    Thread.Sleep(500);
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsImageAssetType(sbyte assetType)
        {
            return assetType == (sbyte)AssetType.Texture
                || assetType == (sbyte)AssetType.TextureTGA
                || assetType == (sbyte)AssetType.ImageJPEG
                || assetType == (sbyte)AssetType.ImageTGA;
        }

        private async Task ForwardAbuseReportAsync(
            string regionName,
            UUID abuserID,
            byte category,
            byte checkflags,
            string details,
            UUID objectID,
            Vector3 position,
            byte reportType,
            UUID screenshotID,
            UUID screenshotOriginalID,
            string screenshotStatus,
            string summary,
            UUID reporter,
            string reporterName)
        {
            try
            {
                OSDMap payload = new();
                payload["event"] = OSD.FromString("abuse_report");
                payload["received_utc"] = OSD.FromString(DateTime.UtcNow.ToString("o"));
                payload["source_region_uuid"] = OSD.FromUUID(m_Scene.RegionInfo.RegionID);
                payload["source_region_name"] = OSD.FromString(m_Scene.RegionInfo.RegionName);
                payload["reported_region_name"] = OSD.FromString(regionName ?? string.Empty);
                payload["reporter_uuid"] = OSD.FromUUID(reporter);
                payload["reporter_name"] = OSD.FromString(reporterName ?? string.Empty);
                payload["abuser_uuid"] = OSD.FromUUID(abuserID);
                payload["category"] = OSD.FromInteger(category);
                payload["checkflags"] = OSD.FromInteger(checkflags);
                payload["details"] = OSD.FromString(details ?? string.Empty);
                payload["object_uuid"] = OSD.FromUUID(objectID);
                payload["position_x"] = OSD.FromReal(position.X);
                payload["position_y"] = OSD.FromReal(position.Y);
                payload["position_z"] = OSD.FromReal(position.Z);
                payload["report_type"] = OSD.FromInteger(reportType);
                payload["screenshot_uuid"] = OSD.FromUUID(screenshotID);
                payload["screenshot_uuid_original"] = OSD.FromUUID(screenshotOriginalID);
                payload["screenshot_status"] = OSD.FromString(screenshotStatus ?? string.Empty);
                payload["summary"] = OSD.FromString(summary ?? string.Empty);

                string json = OSDParser.SerializeJsonString(payload);
                using HttpRequestMessage request = new(HttpMethod.Post, m_abuseEndpointUrl);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                if (!string.IsNullOrWhiteSpace(m_abuseApiToken))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + m_abuseApiToken);
                    request.Headers.TryAddWithoutValidation("X-Abuse-Token", m_abuseApiToken);
                }

                using HttpResponseMessage response = await s_httpClient.SendAsync(request).ConfigureAwait(false);
                string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    m_log.WarnFormat(
                        "[RESTART MODULE]: Abuse report forward failed ({0}) for reporter {1} in {2}: {3}",
                        (int)response.StatusCode,
                        reporter,
                        m_Scene.RegionInfo.RegionName,
                        responseBody);
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat(
                    "[RESTART MODULE]: Exception forwarding abuse report in {0}: {1}",
                    m_Scene.RegionInfo.RegionName,
                    ex.Message);
            }
        }

        private void RegisterApiHandler()
        {
            if (!m_apiEnabled)
                return;

            if (string.IsNullOrWhiteSpace(m_apiToken))
            {
                m_log.Warn("[RESTART MODULE]: Restart API enabled but ApiToken is empty; endpoint will not be registered.");
                return;
            }

            string prefix = m_apiPathPrefix;
            if (string.IsNullOrWhiteSpace(prefix))
                prefix = "/tasia-ngc/restart";
            if (!prefix.StartsWith("/"))
                prefix = "/" + prefix;
            prefix = prefix.TrimEnd('/');

            m_apiPath = string.Format("{0}/{1}", prefix, m_Scene.RegionInfo.RegionID.ToString().ToLowerInvariant());

            try
            {
                MainServer.Instance.DefaultServer.AddSimpleStreamHandler(new SimpleStreamHandler(m_apiPath, HandleApiRequest, Name + ".API"));
                m_log.InfoFormat("[RESTART MODULE]: API endpoint enabled for {0} at {1}", m_Scene.RegionInfo.RegionName, m_apiPath);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[RESTART MODULE]: Failed registering API handler for {0}: {1}", m_apiPath, ex.Message);
            }
        }

        private void HandleApiRequest(IOSHttpRequest request, IOSHttpResponse response)
        {
            response.ContentType = "application/json";

            if (!IsAuthorizedRequest(request))
            {
                WriteJsonResponse(response, 401, BuildStatusMap(false, "unauthorized"));
                return;
            }

            string action = string.Empty;
            int delaySeconds = m_defaultDelaySeconds;
            string reason = string.Empty;
            UUID requestedBy = UUID.Zero;

            if (request.HttpMethod == "GET")
            {
                action = request.QueryString["action"] ?? "status";
            }
            else if (request.HttpMethod == "POST")
            {
                if (!TryParseRequestBody(request, out OSDMap payload))
                {
                    WriteJsonResponse(response, 400, BuildStatusMap(false, "invalid_json"));
                    return;
                }

                action = payload["action"].AsString();
                if (string.IsNullOrWhiteSpace(action))
                    action = "status";

                if (payload.ContainsKey("delay_seconds"))
                {
                    delaySeconds = payload["delay_seconds"].AsInteger();
                    if (delaySeconds <= 0)
                        delaySeconds = m_defaultDelaySeconds;
                }

                reason = payload["reason"].AsString();

                if (payload.ContainsKey("requested_by"))
                {
                    string requestedByRaw = payload["requested_by"].AsString();
                    UUID.TryParse(requestedByRaw, out requestedBy);
                }
            }
            else
            {
                WriteJsonResponse(response, 405, BuildStatusMap(false, "method_not_allowed"));
                return;
            }

            action = action.Trim().ToLowerInvariant();

            switch (action)
            {
                case "backup":
                    try
                    {
                        MainConsole.Instance.RunCommand("backup");
                        m_log.Info("[RESTART MODULE]: Persistence backup API complete");
                        WriteJsonResponse(response, 200, BuildStatusMap(true, "backup_complete"));
                    }
                    catch (Exception ex)
                    {
                        m_log.WarnFormat("[RESTART MODULE]: Persistence backup API failed: {0}", ex.Message);
                        WriteJsonResponse(response, 500, BuildStatusMap(false, "backup_failed"));
                    }
                    return;

                case "schedule":
                    if (delaySeconds < 10)
                        delaySeconds = 10;

                    {
                        int[] restartAlerts = BuildStandardAlerts(delaySeconds);
                        string restartMsg = BuildMessageTemplate(reason);
                        ScheduleRestart(requestedBy, restartMsg, restartAlerts, false);
                        SetPendingRestartState(delaySeconds, reason, requestedBy);
                    }
                    WriteJsonResponse(response, 200, BuildStatusMap(true, "scheduled"));
                    return;

                case "cancel":
                    m_isShutdown = false;
                    AbortRestart(string.IsNullOrWhiteSpace(reason) ? "Region restart cancelled." : reason);
                    WriteJsonResponse(response, 200, BuildStatusMap(true, "cancelled"));
                    return;

                case "shutdown":
                    m_log.Info("[RESTART MODULE]: Shutdown API called â€” backup + quit after countdown");
                    m_isShutdown = true;
                    {
                        if (delaySeconds < 10)
                            delaySeconds = 10;
                        int[] shutdownAlerts = BuildStandardAlerts(delaySeconds);
                        string shutdownMsg = "Grid shutdown â€” saving and quitting";
                        ScheduleRestart(requestedBy, shutdownMsg, shutdownAlerts, false);
                        SetPendingRestartState(delaySeconds, reason, requestedBy);
                    }
                    WriteJsonResponse(response, 200, BuildStatusMap(true, "shutdown_scheduled"));
                    return;

                case "status":
                    WriteJsonResponse(response, 200, BuildStatusMap(true, "status"));
                    return;

                default:
                    WriteJsonResponse(response, 400, BuildStatusMap(false, "invalid_action"));
                    return;
            }
        }

        private bool IsAuthorizedRequest(IOSHttpRequest request)
        {
            if (string.IsNullOrWhiteSpace(m_apiToken))
                return false;

            string authHeader = request.Headers["Authorization"] ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(authHeader))
            {
                const string prefix = "Bearer ";
                if (authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string token = authHeader.Substring(prefix.Length).Trim();
                    return token == m_apiToken;
                }
            }

            string altHeader = request.Headers["X-Restart-Token"] ?? string.Empty;
            return altHeader == m_apiToken;
        }

        private static bool TryParseRequestBody(IOSHttpRequest request, out OSDMap payload)
        {
            payload = new OSDMap();

            if (request?.InputStream == null)
                return true;

            using StreamReader reader = new(request.InputStream);
            string body = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(body))
                return true;

            try
            {
                OSD parsed = OSDParser.DeserializeJson(body);
                payload = parsed as OSDMap ?? new OSDMap();
                return true;
            }
            catch
            {
                payload = new OSDMap();
                return false;
            }
        }

        private void WriteJsonResponse(IOSHttpResponse response, int statusCode, OSDMap payload)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            response.RawBuffer = Util.UTF8.GetBytes(OSDParser.SerializeJsonString(payload));
        }

        private OSDMap BuildStatusMap(bool ok, string status)
        {
            OSDMap map = new();
            map["ok"] = OSD.FromBoolean(ok);
            map["status"] = OSD.FromString(status);
            map["region_uuid"] = OSD.FromUUID(m_Scene.RegionInfo.RegionID);
            map["region_name"] = OSD.FromString(m_Scene.RegionInfo.RegionName);

            bool pending;
            int secondsRemaining;
            string reason;
            UUID requestedBy;

            lock (m_stateSync)
            {
                pending = m_pendingRestartUtc.HasValue;
                secondsRemaining = pending
                    ? Math.Max(0, (int)(m_pendingRestartUtc.Value - DateTime.UtcNow).TotalSeconds)
                    : 0;
                reason = m_pendingReason;
                requestedBy = m_pendingRequestedBy;
            }

            map["pending"] = OSD.FromBoolean(pending);
            map["seconds_remaining"] = OSD.FromInteger(secondsRemaining);
            map["default_delay_seconds"] = OSD.FromInteger(m_defaultDelaySeconds);
            map["reason"] = OSD.FromString(reason ?? string.Empty);
            map["requested_by"] = OSD.FromUUID(requestedBy);
            map["server_time_utc"] = OSD.FromString(DateTime.UtcNow.ToString("o"));
            return map;
        }

        private void SetPendingRestartState(int seconds, string reason, UUID requestedBy)
        {
            lock (m_stateSync)
            {
                m_pendingRestartUtc = DateTime.UtcNow.AddSeconds(Math.Max(0, seconds));
                m_pendingReason = reason ?? string.Empty;
                m_pendingRequestedBy = requestedBy;
                m_RestartBegin = DateTime.UtcNow;
            }
        }

        private static int[] BuildStandardAlerts(int seconds)
        {
            List<int> times = new();
            while (seconds > 0)
            {
                times.Add(seconds);
                if (seconds > 300)
                    seconds -= 120;
                else if (seconds > 30)
                    seconds -= 30;
                else
                    seconds -= 15;
            }

            if (times.Count == 0)
                return new[] { 0 };

            times.Sort();
            times.Reverse();
            return times.ToArray();
        }

        private static string BuildMessageTemplate(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return "Region will restart in {0}";

            string safeReason = reason.Trim().Replace("{", "{{").Replace("}", "}}");
            return safeReason + " (restart in {0})";
        }

        private static string FormatCountdown(int currentAlert)
        {
            int minutes = currentAlert / 60;
            string currentAlertString = String.Empty;

            if (minutes > 0)
            {
                if (minutes == 1)
                    currentAlertString += "1 minute";
                else
                    currentAlertString += String.Format("{0} minutes", minutes);

                if ((currentAlert % 60) != 0)
                    currentAlertString += " and ";
            }

            if ((currentAlert % 60) != 0)
            {
                int seconds = currentAlert % 60;
                if (seconds == 1)
                    currentAlertString += "1 second";
                else
                    currentAlertString += String.Format("{0} seconds", seconds);
            }

            if (String.IsNullOrWhiteSpace(currentAlertString))
                currentAlertString = "0 seconds";

            return currentAlertString;
        }

        private void ClearPendingRestartState()
        {
            lock (m_stateSync)
            {
                m_pendingRestartUtc = null;
                m_pendingReason = string.Empty;
                m_pendingRequestedBy = UUID.Zero;
            }
        }

        protected void CreateMarkerFile()
        {
            if (m_MarkerPath == String.Empty)
                return;

            string path = Path.Combine(m_MarkerPath, m_Scene.RegionInfo.RegionID.ToString());
            try
            {
                string pidstring = System.Diagnostics.Process.GetCurrentProcess().Id.ToString();
                FileStream fs = File.Create(path);
                System.Text.ASCIIEncoding enc = new System.Text.ASCIIEncoding();
                Byte[] buf = enc.GetBytes(pidstring);
                fs.Write(buf, 0, buf.Length);
                fs.Close();
            }
            catch (Exception)
            {
            }
        }

        int CountAgents()
        {
            m_log.Info("[RESTART MODULE]: Counting affected avatars");
            int agents = 0;

            if (m_rebootAll)
            {
                foreach (Scene s in SceneManager.Instance.Scenes)
                {
                    foreach (ScenePresence sp in s.GetScenePresences())
                    {
                        if (!sp.IsChildAgent && !sp.IsNPC)
                            agents++;
                    }
                }
            }
            else
            {
                foreach (ScenePresence sp in m_Scene.GetScenePresences())
                {
                    if (!sp.IsChildAgent && !sp.IsNPC)
                        agents++;
                }
            }

            m_log.InfoFormat("[RESTART MODULE]: Avatars in region: {0}", agents);

            return agents;
        }
    }
}
