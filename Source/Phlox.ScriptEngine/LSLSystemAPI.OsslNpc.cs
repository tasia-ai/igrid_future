/*
 * OSSL NPC helpers ported from OpenSim.Region.ScriptEngine.Shared.Api.OSSL_Api.
 */
using System;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Region.OptionalModules.World.NPC;
using InWorldz.Phlox.Types;

namespace Phlox.ScriptEngine
{
    public partial class LSLSystemAPI
    {
        /// <summary>OSSL_Api.cs:3303 - High (key osNpcSayTo).</summary>
        public void osNpcSayTo(string npc, string target, int channel, string msg)
        {
            OsslCheck(ThreatLevel.High, "osNpcSayTo");

            INPCModule module = World.RequestModuleInterface<INPCModule>();
            if (module == null)
                return;
            IWorldComm wComm = m_ScriptEngine.World.RequestModuleInterface<IWorldComm>();
            if (wComm == null)
                return;

            if (!UUID.TryParse(npc, out UUID npcId))
                return;
            if (!UUID.TryParse(target, out UUID targetId))
                return;
            if (!module.CheckPermissions(npcId, m_host.OwnerID))
                return;

            ScenePresence npcPresence = World.GetScenePresence(npcId);
            if (npcPresence == null || npcPresence.IsDeleted || !npcPresence.IsNPC)
                return;

            Vector3 npcPosition = npcPresence.AbsolutePosition;
            string npcName = npcPresence.Name;
            ChatSleep();
            wComm.DeliverMessageTo(targetId, channel, npcPosition, npcName, npcId, msg);
        }

        /// <summary>OSSL_Api.cs:6398 - upstream has no threat-level check.</summary>
        public int osNpcLookAt(string npckey, int ltype, string objkey, Vector3 offset)
        {
            if (World.GetNumberOfClients() == 0)
                return 0;

            if (ltype < 0 || ltype > ScriptBaseClass.NPCLOOKAT_CLEAR)
                return -1;
            if (!UUID.TryParse(npckey, out UUID npc))
                return -2;

            ScenePresence npcPresence = World.GetScenePresence(npc);
            if (npcPresence == null || !npcPresence.IsNPC)
                return -3;
            if (!UUID.TryParse(objkey, out UUID obj))
                return -4;

            if (!obj.IsZero())
            {
                ScenePresence objectPresence = World.GetScenePresence(obj);
                if (objectPresence == null && World.GetSceneObjectPart(obj) == null)
                    return -5;
            }

            byte[] data = new byte[57];
            npc.ToBytes(data, 0);
            obj.ToBytes(data, 16);
            Vector3d vectorOffset = new(offset.X, offset.Y, offset.Z);
            vectorOffset.ToBytes(data, 32);
            data[56] = (byte)ltype;

            OpenMetaverse.Packets.ViewerEffectPacket.EffectBlock[] effectBlocks =
            {
                new OpenMetaverse.Packets.ViewerEffectPacket.EffectBlock
                {
                    AgentID = npc,
                    Color = new byte[4],
                    Duration = 0,
                    ID = UUID.Random(),
                    Type = 14,
                    TypeData = data
                }
            };

            World.ForEachScenePresence(sp =>
            {
                if (!sp.IsNPC && !sp.IsDeleted)
                    sp.ControllingClient.SendViewerEffect(effectBlocks);
            });

            return 0;
        }
    }
}
