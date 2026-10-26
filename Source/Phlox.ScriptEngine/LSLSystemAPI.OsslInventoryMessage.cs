using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Services.Interfaces;
using InWorldz.Phlox.Types;

namespace Phlox.ScriptEngine
{
    public partial class LSLSystemAPI
    {
        /// <summary>OSSL_Api.cs:5810; upstream has no per-function threat check for this operation.</summary>
        public void osGiveLinkInventory(int linkNumber, string destination, string inventory)
        {
            if (!UUID.TryParse(destination, out UUID destId) || destId.IsZero())
                return;

            SceneObjectPart part = GetSingleLinkPart(linkNumber);
            if (part == null)
                return;

            TaskInventoryItem item = part.Inventory.GetInventoryItem(inventory);
            if (item == null)
                return;

            if (World.TryGetSceneObjectPart(destId, out _))
            {
                World.MoveTaskInventoryItem(destId, part, item.ItemID);
                return;
            }

            if (!World.TryGetScenePresence(destId, out ScenePresence _))
            {
                UserAccount account = World.UserAccountService.GetUserAccount(World.RegionInfo.ScopeID, destId);
                if (account == null)
                {
                    GridUserInfo info = World.GridUserService.GetGridUserInfo(destId.ToString());
                    if (info == null || !info.Online)
                        return;
                }
            }

            InventoryItemBase agentItem = World.MoveTaskInventoryItem(destId, UUID.Zero, part, item.ItemID, out string message);
            if (agentItem == null)
            {
                llSay(0, message);
                return;
            }

            byte[] bucket = { (byte)item.Type };
            GridInstantMessage msg = new GridInstantMessage(World, m_host.OwnerID, m_host.Name, destId,
                (byte)InstantMessageDialog.TaskInventoryOffered,
                m_host.OwnerID.Equals(m_host.GroupID), "'" + item.Name + "'. (" + m_host.Name + " is located at " +
                World.RegionInfo.RegionName + " " + m_host.AbsolutePosition + ")",
                agentItem.ID, true, m_host.AbsolutePosition, bucket, true);

            ScenePresence presence;
            if (World.TryGetScenePresence(destId, out presence))
                presence.ControllingClient.SendInstantMessage(msg);
            else
                World.RequestModuleInterface<IMessageTransferModule>()?.SendInstantMessage(msg, success => { });

            ScriptSleep(3000);
        }

        /// <summary>OSSL_Api.cs:5870; upstream has no per-function threat check for this operation.</summary>
        public void osGiveLinkInventoryList(int linkNumber, string destination, string folderName, LSLList inventory)
        {
            if (inventory.Length == 0)
                return;

            SceneObjectPart part = GetSingleLinkPart(linkNumber);
            if (part == null || !UUID.TryParse(destination, out UUID destId) || destId.IsZero())
                return;

            bool isNotOwner = true;
            SceneObjectPart destSop = null;
            if (!World.TryGetSceneObjectPart(destId, out destSop))
            {
                if (!World.TryGetScenePresence(destId, out ScenePresence sp))
                {
                    ShoutError("osGiveLinkInventoryList: Unable to give list, destination not found");
                    ScriptSleep(100);
                    return;
                }
                isNotOwner = sp.UUID.NotEqual(m_host.OwnerID);
            }

            List<UUID> itemList = new List<UUID>(inventory.Length);
            foreach (object value in inventory.Data)
            {
                string rawItemString = value.ToString();
                TaskInventoryItem taskItem = UUID.TryParse(rawItemString, out UUID itemId)
                    ? part.Inventory.GetInventoryItem(itemId)
                    : part.Inventory.GetInventoryItem(rawItemString);
                if (taskItem == null || (taskItem.CurrentPermissions & (uint)OpenSim.Framework.PermissionMask.Copy) == 0)
                    continue;

                if (destSop != null)
                {
                    if (!World.Permissions.CanDoObjectInvToObjectInv(taskItem, m_host, destSop))
                        continue;
                }
                else if (isNotOwner && (taskItem.CurrentPermissions & (uint)OpenSim.Framework.PermissionMask.Transfer) == 0)
                    continue;

                itemList.Add(taskItem.ItemID);
            }

            if (itemList.Count == 0)
            {
                ShoutError("osGiveLinkInventoryList: Unable to give list, no items found");
                ScriptSleep(100);
                return;
            }

            UUID folderId = World.MoveTaskInventoryItems(destId, folderName, part, itemList, false);
            if (folderId.IsZero())
            {
                ShoutError("osGiveLinkInventoryList: Unable to give list");
                ScriptSleep(100);
                return;
            }

            if (destSop != null)
            {
                ScriptSleep(100);
                return;
            }

            IMessageTransferModule transfer = World.RequestModuleInterface<IMessageTransferModule>();
            if (transfer != null)
            {
                byte[] bucket = { (byte)AssetType.Folder };
                GridInstantMessage msg = new GridInstantMessage(World, m_host.OwnerID, m_host.Name, destId,
                    (byte)InstantMessageDialog.TaskInventoryOffered,
                    m_host.OwnerID.Equals(m_host.GroupID), string.Format("'{0}'", folderName), folderId, false,
                    m_host.AbsolutePosition, bucket, false);
                transfer.SendInstantMessage(msg, success => { });
            }

            ScriptSleep(3000);
        }

        /// <summary>OSSL_Api.cs:4392; Moderate threat level, OSSL key osMessageAttachments.</summary>
        public void osMessageAttachments(string avatar, string message, LSLList attachmentPoints, int options)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.Moderate, "osMessageAttachments");

            if (!UUID.TryParse(avatar.ToString(), out UUID targetId) || targetId.IsZero() ||
                !World.TryGetScenePresence(targetId, out ScenePresence target) || target.IsDeleted || target.IsInTransit ||
                target.GetAttachmentsCount() == 0)
                return;

            bool invertPoints = (options & ScriptBaseClass.OS_ATTACH_MSG_INVERT_POINTS) != 0;
            bool msgAll = false;
            List<int> points;
            if (attachmentPoints.Length > 0)
            {
                points = new List<int>(attachmentPoints.Length);
                foreach (object point in attachmentPoints.Data)
                {
                    if (!OsslListObjectToInt(point, out int number))
                        return;
                    if (number == ScriptBaseClass.OS_ATTACH_MSG_ALL)
                    {
                        if (invertPoints)
                            return;
                        msgAll = true;
                        break;
                    }
                    if (number > 0)
                        points.Add(number);
                }
            }
            else
                points = new List<int>();

            if (points.Count == 0)
            {
                if (!invertPoints && !msgAll)
                    return;
                msgAll = true;
                invertPoints = false;
            }

            List<SceneObjectGroup> attachments;
            if (msgAll || invertPoints)
                attachments = target.GetAttachments();
            else
            {
                attachments = new List<SceneObjectGroup>();
                foreach (int point in points)
                    attachments.AddRange(target.GetAttachments((uint)point));
            }
            if (attachments.Count == 0)
                return;

            bool objectCreator = (options & ScriptBaseClass.OS_ATTACH_MSG_OBJECT_CREATOR) != 0;
            bool scriptCreator = (options & ScriptBaseClass.OS_ATTACH_MSG_SCRIPT_CREATOR) != 0;
            UUID hostCreatorId = m_host.CreatorID;
            TaskInventoryItem scriptItem = m_host.Inventory.GetInventoryItem(m_itemID);
            if (scriptItem == null)
                return;
            UUID itemCreatorId = scriptItem.CreatorID;
            foreach (SceneObjectGroup group in attachments)
            {
                if (group.IsDeleted || group.inTransit || (group.ScriptEvents & scriptEvents.dataserver) == 0)
                    continue;
                if (invertPoints && points.Contains((int)group.AttachmentPoint))
                    continue;
                UUID creatorId = group.RootPart.CreatorID;
                if (objectCreator && creatorId.NotEqual(hostCreatorId))
                    continue;
                if (scriptCreator && creatorId.NotEqual(itemCreatorId))
                    continue;

                foreach (SceneObjectPart child in group.Parts)
                    if ((child.ScriptEvents & scriptEvents.dataserver) != 0)
                        osMessageObject(child.UUID.ToString(), message);
            }
        }

        private static bool OsslListObjectToInt(object value, out int number)
        {
            try
            {
                if (value is int integer) number = integer;
                else if (value is uint u) number = (int)u;
                else if (value is string text) return int.TryParse(text, out number);
                else { number = 0; return false; }
                return true;
            }
            catch
            {
                number = 0;
                return false;
            }
        }
    }
}
