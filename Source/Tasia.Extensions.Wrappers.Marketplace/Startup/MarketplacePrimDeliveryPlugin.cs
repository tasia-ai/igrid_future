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
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Services.Interfaces;
using TasiaAddons.Abstractions;
using GridServiceRegion = OpenSim.Services.Interfaces.GridRegion;

#nullable enable

[assembly: Addin("TasiaAddons.Marketplace", "1.0.0")]
[assembly: AddinDescription("Marketplace prim delivery plugin")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace TasiaAddon.Marketplace
{
    [Extension(Path = "/OpenSim/Startup", NodeName = "Plugin", Id = "MarketplacePrimDeliveryPlugin")]
    public sealed class MarketplacePrimDeliveryPlugin : IApplicationPlugin, ITasiaAddonsFeature
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(MarketplacePrimDeliveryPlugin));

        private OpenSimBase m_application;
        private ICommandConsole m_console;
        private bool m_enabled;
        private string m_password = string.Empty;
        private ITasiaAddonsContext? m_context;

        private enum SendPrimResult
        {
            Success,
            ObjectNotFound,
            UserNotFound,
            InventoryUnavailable,
            AssetUnavailable,
            AssetStoreFailed,
            InventoryAddFailed,
            Exception
        }

        public string Name => "MarketplacePrimDeliveryPlugin";

        public string Version => "1.0";

        public void Initialise()
        {
            throw new PluginNotInitialisedException(Name);
        }

        public void Initialise(OpenSimBase openSim)
        {
            m_application = openSim ?? throw new ArgumentNullException(nameof(openSim));

            IConfig config = openSim.Config?.Configs?["Marketplace"];
            if (config == null || !config.GetBoolean("Enabled", false))
            {
                Log.Info("[MARKETPLACE]: Prim delivery add-on disabled.");
                return;
            }

            m_password = config.GetString("Password", string.Empty);
            if (string.IsNullOrWhiteSpace(m_password))
            {
                Log.Warn("[MARKETPLACE]: Prim delivery add-on disabled because no Password is configured.");
                return;
            }

            m_enabled = true;

            m_console = MainConsole.Instance;
            if (m_console != null)
            {
                m_console.Commands.AddCommand(
                    "Marketplace",
                    false,
                    "send",
                    "send <object-uuid> <user-uuid>",
                    "Delivers the specified object to the user's inventory.",
                    HandleSendPrimCommand);
            }

            MainServer.Instance.AddSimpleStreamHandler(new SendPrimHandler(this));

            Log.Info("[MARKETPLACE]: Prim delivery add-on initialised.");
        }

        public void PostInitialise()
        {
        }

        public void Dispose()
        {
        }

        public void Configure(ITasiaAddonsContext context)
        {
            m_context = context ?? throw new ArgumentNullException(nameof(context));
        }

        private void HandleSendPrimCommand(string module, string[] args)
        {
            if (args.Length < 3)
            {
                m_console?.Output("Usage: send <object-uuid> <user-uuid>");
                return;
            }

            if (!UUID.TryParse(args[1], out UUID objectId))
            {
                m_console?.Output($"Invalid object id: {args[1]}");
                return;
            }

            if (!UUID.TryParse(args[2], out UUID userId))
            {
                m_console?.Output($"Invalid user id: {args[2]}");
                return;
            }

            SendPrimResult result = TrySendPrim(objectId, userId, out string message);

            if (result == SendPrimResult.Success)
            {
                m_context?.AuditService.RecordEvent(
                    "Marketplace",
                    "SendPrim",
                    userId,
                    null,
                    $"{message} (Object {objectId})",
                    null,
                    null);
                m_console?.Output(message);
            }
            else
                m_console?.Output($"Failed to send prim: {message}");
        }

        private SendPrimResult TrySendPrim(UUID objectId, UUID userId, out string message)
        {
            return TrySendPrim(objectId, userId, false, out message);
        }

        private SendPrimResult TrySendPrim(UUID objectId, UUID userId, bool searchLocallyOnly, out string message)
        {
            foreach (Scene scene in m_application.SceneManager.Scenes)
            {
                if (scene.TryGetSceneObjectGroup(objectId, out SceneObjectGroup sog) && sog is not null)
                    return TrySendPrim(scene, sog, userId, out message);
            }

            if (searchLocallyOnly)
            {
                message = $"Object {objectId} not found.";
                return SendPrimResult.ObjectNotFound;
            }

            return TrySendPrimRemotely(objectId, userId, out message);
        }

        private SendPrimResult TrySendPrim(Scene scene, SceneObjectGroup sog, UUID userId, out string message)
        {
            IUserAccountService userAccountService = scene.UserAccountService;
            UserAccount account = userAccountService?.GetUserAccount(scene.RegionInfo.ScopeID, userId);
            if (account == null)
            {
                message = $"User {userId} not found.";
                return SendPrimResult.UserNotFound;
            }

            if (scene.InventoryService == null)
            {
                message = "Inventory service is unavailable.";
                return SendPrimResult.InventoryUnavailable;
            }

            if (scene.AssetService == null)
            {
                message = "Asset service is unavailable.";
                return SendPrimResult.AssetUnavailable;
            }

            try
            {
                string xml = SceneObjectSerializer.ToOriginalXmlFormat(sog);
                if (string.IsNullOrEmpty(xml))
                {
                    message = $"Failed to serialize object {sog.UUID}.";
                    return SendPrimResult.AssetStoreFailed;
                }

                UUID assetId = UUID.Random();
                AssetBase asset = new AssetBase(assetId, sog.RootPart.Name, (sbyte)AssetType.Object, sog.RootPart.OwnerID.ToString())
                {
                    Description = sog.RootPart.Description,
                    Data = Util.UTF8NBGetbytes(xml)
                };

                string storedId = scene.AssetService.Store(asset);
                if (string.IsNullOrEmpty(storedId))
                {
                    message = $"Failed to store asset for object {sog.UUID}.";
                    return SendPrimResult.AssetStoreFailed;
                }

                if (UUID.TryParse(storedId, out UUID storedAssetId))
                {
                    assetId = storedAssetId;
                    asset.FullID = storedAssetId;
                }

                UUID destinationFolder = ResolveDestinationFolder(scene.InventoryService, userId);
                if (destinationFolder == UUID.Zero)
                {
                    message = "Could not resolve destination folder.";
                    return SendPrimResult.InventoryUnavailable;
                }

                InventoryItemBase item = CreateInventoryItem(assetId, sog, userId, destinationFolder);

                if (!scene.InventoryService.AddItem(item))
                {
                    message = "Failed to add item to inventory.";
                    return SendPrimResult.InventoryAddFailed;
                }

                message = $"Prim {sog.UUID} sent to {account.Name}.";
                return SendPrimResult.Success;
            }
            catch (Exception ex)
            {
                Log.ErrorFormat("[MARKETPLACE]: Error while sending prim {0} to {1}: {2}", sog.UUID, userId, ex.Message);
                message = ex.Message;
                return SendPrimResult.Exception;
            }
        }

        private SendPrimResult TrySendPrimRemotely(UUID objectId, UUID userId, out string message)
        {
            foreach (Scene scene in m_application.SceneManager.Scenes)
            {
                if (scene.GridService == null)
                    continue;

                List<GridServiceRegion> regions;
                try
                {
                    int regionX = (int)scene.RegionInfo.RegionLocX;
                    int regionY = (int)scene.RegionInfo.RegionLocY;
                    regions = scene.GridService.GetRegionRange(scene.RegionInfo.ScopeID, regionX - 1, regionX + 1, regionY - 1, regionY + 1);
                }
                catch (Exception ex)
                {
                    Log.WarnFormat("[MARKETPLACE]: Failed querying grid service for prim {0}: {1}", objectId, ex.Message);
                    continue;
                }

                foreach (GridServiceRegion region in regions)
                {
                    if (region == null || region.HttpPort == 0)
                        continue;

                if (!TryBuildRemoteSendUri(region, objectId, userId, out Uri? sendUri))
                    continue;

                SendPrimResult remoteResult = TryForwardSendRequest(region, sendUri!, out string remoteMessage);
                    if (remoteResult == SendPrimResult.Success)
                    {
                        message = remoteMessage;
                        return SendPrimResult.Success;
                    }

                    if (remoteResult == SendPrimResult.ObjectNotFound || remoteResult == SendPrimResult.UserNotFound)
                    {
                        message = remoteMessage;
                        return remoteResult;
                    }
                }
            }

            message = $"Object {objectId} not found.";
            return SendPrimResult.ObjectNotFound;
        }

        private InventoryItemBase CreateInventoryItem(UUID assetId, SceneObjectGroup sog, UUID userId, UUID folderId)
        {
            return new InventoryItemBase(UUID.Random())
            {
                Owner = userId,
                AssetID = assetId,
                Name = sog.RootPart.Name,
                Description = sog.RootPart.Description,
                InvType = (int)InventoryType.Object,
                AssetType = (int)AssetType.Object,
                Folder = folderId,
                CurrentPermissions = (uint)OpenMetaverse.PermissionMask.All,
                NextPermissions = (uint)OpenMetaverse.PermissionMask.All,
                BasePermissions = (uint)OpenMetaverse.PermissionMask.All,
                EveryOnePermissions = 0,
                GroupPermissions = 0,
                CreationDate = (int)Util.UnixTimeSinceEpoch()
            };
        }

        private static UUID ResolveDestinationFolder(IInventoryService inventoryService, UUID userId)
        {
            if (inventoryService == null)
                return UUID.Zero;

            InventoryFolderBase folder = inventoryService.GetFolderForType(userId, FolderType.Object);
            if (folder == null || folder.ID == UUID.Zero)
                folder = inventoryService.GetRootFolder(userId);

            return folder?.ID ?? UUID.Zero;
        }

        private bool TryBuildRemoteSendUri(GridServiceRegion region, UUID objectId, UUID userId, out Uri? sendUri)
        {
            sendUri = null;

            if (string.IsNullOrEmpty(region.ServerURI))
                return false;

            if (!Uri.TryCreate(region.ServerURI, UriKind.Absolute, out Uri baseUri))
                return false;

            UriBuilder builder = new UriBuilder(baseUri)
            {
                Path = baseUri.AbsolutePath.EndsWith("/", StringComparison.Ordinal) ? baseUri.AbsolutePath + "send" : baseUri.AbsolutePath.TrimEnd('/') + "/send"
            };

            string query = $"oid={Uri.EscapeDataString(objectId.ToString())}&uid={Uri.EscapeDataString(userId.ToString())}&pass={Uri.EscapeDataString(m_password)}&forwarded=1";
            builder.Query = query;

            sendUri = builder.Uri;
            return true;
        }

        private SendPrimResult TryForwardSendRequest(GridServiceRegion region, Uri sendUri, out string message)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(sendUri);
                request.Method = "GET";
                request.Timeout = 10000;
                request.ReadWriteTimeout = 10000;
                request.UserAgent = "OpenSimSendPrim/1.0";

                using HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                using Stream responseStream = response.GetResponseStream() ?? Stream.Null;
                using StreamReader reader = new StreamReader(responseStream);
                message = reader.ReadToEnd();

                if (string.IsNullOrEmpty(message))
                    message = $"Remote region {region.RegionName} responded with status {response.StatusCode}.";

                if (response.StatusCode == HttpStatusCode.OK)
                    return SendPrimResult.Success;

                if (response.StatusCode == HttpStatusCode.NotFound)
                    return MapNotFoundResult(message);

                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                    return MapUnavailableResult(message);

                return SendPrimResult.Exception;
            }
            catch (WebException ex)
            {
                if (ex.Response is HttpWebResponse errorResponse)
                {
                    using Stream errorStream = errorResponse.GetResponseStream() ?? Stream.Null;
                    using StreamReader reader = new StreamReader(errorStream);
                    message = reader.ReadToEnd();

                    if (string.IsNullOrEmpty(message))
                        message = ex.Message;

                    if (errorResponse.StatusCode == HttpStatusCode.NotFound)
                        return MapNotFoundResult(message);

                    if (errorResponse.StatusCode == HttpStatusCode.ServiceUnavailable)
                        return MapUnavailableResult(message);

                    return SendPrimResult.Exception;
                }

                Log.WarnFormat("[MARKETPLACE]: Unable to contact remote region {0} for prim {1}: {2}", region.RegionName, sendUri, ex.Message);
                message = "Unable to contact remote region.";
                return SendPrimResult.ObjectNotFound;
            }
            catch (Exception ex)
            {
                Log.WarnFormat("[MARKETPLACE]: Error while contacting remote region {0} for prim {1}: {2}", region.RegionName, sendUri, ex.Message);
                message = ex.Message;
                return SendPrimResult.Exception;
            }
        }

        private static SendPrimResult MapNotFoundResult(string message)
        {
            if (!string.IsNullOrEmpty(message) && message.IndexOf("user", StringComparison.OrdinalIgnoreCase) >= 0)
                return SendPrimResult.UserNotFound;

            return SendPrimResult.ObjectNotFound;
        }

        private static SendPrimResult MapUnavailableResult(string message)
        {
            if (!string.IsNullOrEmpty(message) && message.IndexOf("inventory", StringComparison.OrdinalIgnoreCase) >= 0)
                return SendPrimResult.InventoryUnavailable;

            return SendPrimResult.AssetUnavailable;
        }

        private sealed class SendPrimHandler : SimpleStreamHandler
        {
            private readonly MarketplacePrimDeliveryPlugin m_plugin;

            public SendPrimHandler(MarketplacePrimDeliveryPlugin plugin)
                : base("/send")
            {
                m_plugin = plugin;
            }

            protected override void ProcessRequest(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
            {
                httpResponse.ContentType = "text/plain";

                if (!string.Equals(httpRequest.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    httpResponse.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    httpResponse.AddHeader("Allow", "GET");
                    httpResponse.RawBuffer = Util.UTF8NBGetbytes("Only GET is supported.");
                    return;
                }

                Dictionary<string, string> query = httpRequest.QueryAsDictionary;

                if (!query.TryGetValue("pass", out string pass) || pass != m_plugin.m_password)
                {
                    httpResponse.StatusCode = (int)HttpStatusCode.Forbidden;
                    httpResponse.RawBuffer = Util.UTF8NBGetbytes("Invalid password.");
                    return;
                }

                if (!query.TryGetValue("oid", out string oidValue) || !UUID.TryParse(oidValue, out UUID objectId))
                {
                    httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                    httpResponse.RawBuffer = Util.UTF8NBGetbytes("Missing or invalid oid parameter.");
                    return;
                }

                if (!query.TryGetValue("uid", out string uidValue) || !UUID.TryParse(uidValue, out UUID userId))
                {
                    httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                    httpResponse.RawBuffer = Util.UTF8NBGetbytes("Missing or invalid uid parameter.");
                    return;
                }

                bool forwarded = false;
                if (query.TryGetValue("forwarded", out string forwardedValue))
                {
                    if (bool.TryParse(forwardedValue, out bool parsed))
                        forwarded = parsed;
                    else if (string.Equals(forwardedValue, "1", StringComparison.Ordinal))
                        forwarded = true;
                }

                SendPrimResult result = m_plugin.TrySendPrim(objectId, userId, forwarded, out string message);

                switch (result)
                {
                    case SendPrimResult.Success:
                        httpResponse.StatusCode = (int)HttpStatusCode.OK;
                        break;
                    case SendPrimResult.ObjectNotFound:
                    case SendPrimResult.UserNotFound:
                        httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
                        break;
                    case SendPrimResult.InventoryUnavailable:
                    case SendPrimResult.AssetUnavailable:
                        httpResponse.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                        break;
                    default:
                        httpResponse.StatusCode = (int)HttpStatusCode.InternalServerError;
                        break;
                }

                httpResponse.RawBuffer = Util.UTF8NBGetbytes(message);
            }
        }
    }
}
