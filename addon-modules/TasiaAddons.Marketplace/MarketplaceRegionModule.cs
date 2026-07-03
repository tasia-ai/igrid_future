using System;
using System.Net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Services.Interfaces;

[assembly: Addin("TasiaAddons.Marketplace", "1.0.0")]
[assembly: AddinDescription("Marketplace send endpoint module")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace TasiaAddons.Marketplace;

[Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "MarketplaceRegionModule")]
public class MarketplaceRegionModule : INonSharedRegionModule
{
    private Scene m_scene;
    private IMessageTransferModule m_messageTransfer;
    private bool m_enabled;
    private string m_password = string.Empty;
    private const string SendPath = "/send";

    public string Name => "MarketplaceRegionModule";
    public Type ReplaceableInterface => null;

    public void Initialise(IConfigSource source)
    {
        IConfig cfg = source.Configs["Marketplace"];
        m_enabled = cfg?.GetBoolean("Enabled", false) ?? false;
        m_password = cfg?.GetString("Password", string.Empty) ?? string.Empty;

        if (m_enabled && string.IsNullOrWhiteSpace(m_password))
            m_enabled = false;
    }

    public void AddRegion(Scene scene)
    {
        m_scene = scene;
        m_messageTransfer = m_scene.RequestModuleInterface<IMessageTransferModule>();
        if (!m_enabled)
            return;

        MainServer.Instance.AddSimpleStreamHandler(
            new SimpleStreamHandler(SendPath, HandleSendRequest, Name + ".send"));
    }

    public void RegionLoaded(Scene scene)
    {
    }

    public void RemoveRegion(Scene scene)
    {
        if (m_enabled)
            MainServer.Instance.RemoveSimpleStreamHandler(SendPath);
    }

    public void Close()
    {
    }

    private void HandleSendRequest(IOSHttpRequest req, IOSHttpResponse resp)
    {
        resp.ContentType = "text/plain";

        if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            resp.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            resp.RawBuffer = Util.UTF8NBGetbytes("Only GET is supported.");
            return;
        }

        var q = req.QueryAsDictionary;
        if (!q.TryGetValue("pass", out string pass) || pass != m_password)
        {
            resp.StatusCode = (int)HttpStatusCode.Forbidden;
            resp.RawBuffer = Util.UTF8NBGetbytes("Invalid password.");
            return;
        }

        if (!q.TryGetValue("oid", out string oidValue) || !UUID.TryParse(oidValue, out UUID objectId))
        {
            resp.StatusCode = (int)HttpStatusCode.BadRequest;
            resp.RawBuffer = Util.UTF8NBGetbytes("Missing or invalid oid parameter.");
            return;
        }

        if (!q.TryGetValue("uid", out string uidValue) || !UUID.TryParse(uidValue, out UUID userId))
        {
            resp.StatusCode = (int)HttpStatusCode.BadRequest;
            resp.RawBuffer = Util.UTF8NBGetbytes("Missing or invalid uid parameter.");
            return;
        }

        string message;
        HttpStatusCode code = TrySendPrim(objectId, userId, out message);
        resp.StatusCode = (int)code;
        resp.RawBuffer = Util.UTF8NBGetbytes(message);
    }

    private HttpStatusCode TrySendPrim(UUID objectId, UUID userId, out string message)
    {
        if (m_scene == null)
        {
            message = "Scene unavailable.";
            return HttpStatusCode.InternalServerError;
        }

        if (!m_scene.TryGetSceneObjectGroup(objectId, out SceneObjectGroup sog) || sog == null)
        {
            message = $"Object {objectId} not found.";
            return HttpStatusCode.NotFound;
        }

        UserAccount account = m_scene.UserAccountService?.GetUserAccount(m_scene.RegionInfo.ScopeID, userId);
        if (account == null)
        {
            message = $"User {userId} not found.";
            return HttpStatusCode.NotFound;
        }

        if (m_scene.InventoryService == null)
        {
            message = "Inventory service unavailable.";
            return HttpStatusCode.ServiceUnavailable;
        }

        if (m_scene.AssetService == null)
        {
            message = "Asset service unavailable.";
            return HttpStatusCode.ServiceUnavailable;
        }

        try
        {
            string xml = SceneObjectSerializer.ToOriginalXmlFormat(sog);
            if (string.IsNullOrEmpty(xml))
            {
                message = "Failed to serialize object.";
                return HttpStatusCode.InternalServerError;
            }

            UUID assetId = UUID.Random();
            AssetBase asset = new(assetId, sog.RootPart.Name, (sbyte)AssetType.Object, sog.RootPart.OwnerID.ToString())
            {
                Description = sog.RootPart.Description,
                Data = Util.UTF8NBGetbytes(xml)
            };

            string storedId = m_scene.AssetService.Store(asset);
            if (string.IsNullOrEmpty(storedId))
            {
                message = "Failed to store object asset.";
                return HttpStatusCode.ServiceUnavailable;
            }

            if (UUID.TryParse(storedId, out UUID parsedAssetId))
                assetId = parsedAssetId;

            InventoryFolderBase folder = m_scene.InventoryService.GetFolderForType(userId, FolderType.Object)
                ?? m_scene.InventoryService.GetRootFolder(userId);
            if (folder == null || folder.ID == UUID.Zero)
            {
                message = "Could not resolve destination folder.";
                return HttpStatusCode.ServiceUnavailable;
            }

            InventoryItemBase item = new(UUID.Random())
            {
                Owner = userId,
                AssetID = assetId,
                Name = sog.RootPart.Name,
                Description = sog.RootPart.Description,
                InvType = (int)InventoryType.Object,
                AssetType = (int)AssetType.Object,
                Folder = folder.ID,
                CurrentPermissions = (uint)OpenMetaverse.PermissionMask.All,
                NextPermissions = (uint)OpenMetaverse.PermissionMask.All,
                BasePermissions = (uint)OpenMetaverse.PermissionMask.All,
                EveryOnePermissions = 0,
                GroupPermissions = 0,
                CreationDate = (int)Util.UnixTimeSinceEpoch()
            };

            if (!m_scene.InventoryService.AddItem(item))
            {
                message = "Failed to add item to inventory.";
                return HttpStatusCode.ServiceUnavailable;
            }

            if (m_scene.TryGetScenePresence(userId, out ScenePresence presence)
                && presence?.ControllingClient != null
                && presence.ControllingClient.IsActive)
            {
                try
                {
                    presence.ControllingClient.SendInventoryItemCreateUpdate(item, 0);
                    presence.ControllingClient.SendBulkUpdateInventory(Array.Empty<InventoryFolderBase>(), new[] { item });
                    presence.ControllingClient.SendAgentAlertMessage("Marketplace: item delivered to your inventory.", false);
                }
                catch
                {
                    // Inventory already stored. Viewer can still refresh on next inventory fetch.
                }
            }
            else if (m_messageTransfer != null)
            {
                // Best-effort remote viewer nudge for users online in a different region process.
                // This mimics inventory offer payload with already-created item id.
                try
                {
                    GridInstantMessage im = new()
                    {
                        fromAgentID = UUID.Zero.Guid,
                        toAgentID = userId.Guid,
                        fromAgentName = "Marketplace",
                        message = "Marketplace item delivered: " + item.Name,
                        imSessionID = item.ID.Guid,
                        offline = 0,
                        dialog = (byte)InstantMessageDialog.InventoryOffered,
                        ParentEstateID = 0,
                        Position = Vector3.Zero,
                        RegionID = m_scene.RegionInfo.RegionID.Guid,
                        binaryBucket = new byte[17],
                        timestamp = (uint)Util.UnixTimeSinceEpoch(),
                        fromGroup = false
                    };

                    im.binaryBucket[0] = (byte)AssetType.Object;
                    item.ID.ToBytes(im.binaryBucket, 1);

                    m_messageTransfer.SendInstantMessage(im, delegate { });
                }
                catch
                {
                    // Do nothing. Item is persisted even if remote nudge fails.
                }
            }

            message = $"Prim {sog.UUID} sent to {account.Name}.";
            return HttpStatusCode.OK;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return HttpStatusCode.InternalServerError;
        }
    }
}
