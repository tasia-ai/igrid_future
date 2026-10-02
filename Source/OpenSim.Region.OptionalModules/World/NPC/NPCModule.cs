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

using System.Reflection;

using Nini.Config;
using OpenMetaverse;

using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

using Microsoft.Extensions.Logging;

namespace OpenSim.Region.OptionalModules.World.NPC;

public class NPCModule : INPCModule, ISharedRegionModule
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private readonly Dictionary<UUID, NPCAvatar> m_avatars = new Dictionary<UUID, NPCAvatar>();
    private NPCOptionsFlags m_NPCOptionFlags;

    private int m_MaxNumberNPCperScene = 40;

    // This module is shared across every region in a simulator, so it cannot hold a
    // single Scene - and ScriptModuleComms is per-region, so a script invocation can
    // arrive from a region that is not the one holding the NPC. The script entry
    // points therefore resolve the host prim and the NPC presence across this list
    // rather than assuming a scene. On a one-process-per-region grid this never
    // diverges; it would on any other topology.
    private readonly List<Scene> m_scenes = new List<Scene>();
    private IScriptModuleComms m_comms;

    public NPCOptionsFlags NPCOptionFlags {get {return m_NPCOptionFlags;}}

    public bool Enabled { get; private set; }

    public void Initialise(IConfigSource source)
    {
        IConfig config = source.Configs["NPC"];

        Enabled = (config != null && config.GetBoolean("Enabled", true));
        m_NPCOptionFlags = NPCOptionsFlags.None;
        if(Enabled)
        {
            if(config.GetBoolean("AllowNotOwned", true))
                m_NPCOptionFlags |= NPCOptionsFlags.AllowNotOwned;

            if(config.GetBoolean("AllowSenseAsAvatar", true))
                m_NPCOptionFlags |= NPCOptionsFlags.AllowSenseAsAvatar;

            if(config.GetBoolean("AllowCloneOtherAvatars", true))
                m_NPCOptionFlags |= NPCOptionsFlags.AllowCloneOtherAvatars;

            if(config.GetBoolean("NoNPCGroup", true))
                m_NPCOptionFlags |= NPCOptionsFlags.NoNPCGroup;

            m_MaxNumberNPCperScene = config.GetInt("MaxNumberNPCsPerScene", m_MaxNumberNPCperScene);
        }
    }

    public void AddRegion(Scene scene)
    {
        if (Enabled)
            scene.RegisterModuleInterface<INPCModule>(this);
    }

    public void RegionLoaded(Scene scene)
    {
        if (!Enabled)
            return;

        lock (m_scenes)
        {
            if (!m_scenes.Contains(scene))
                m_scenes.Add(scene);
        }

        // Script functions are published per region through ScriptModuleComms, not
        // through IScriptModule - that interface is the script engine's lifecycle
        // contract and carries no function members at all.
        m_comms = scene.RequestModuleInterface<IScriptModuleComms>();
        if (m_comms is null)
        {
            m_log.LogError("[NPC MODULE]: ScriptModuleComms interface not defined; "
                + "osTeleportSmartNPC and osTeleportSmartNPCToRegion will not be available");
            return;
        }

        try
        {
            m_comms.RegisterScriptInvocations(this);
        }
        catch (Exception e)
        {
            m_log.LogError("[NPC MODULE]: script method registration failed: {0}", e.Message);
        }
    }

    public void PostInitialise()
    {
    }

    public void RemoveRegion(Scene scene)
    {
        scene.UnregisterModuleInterface<INPCModule>(this);
    }

    public void Close()
    {
    }

    public string Name
    {
        get { return "NPCModule"; }
    }

    public Type ReplaceableInterface { get { return null; } }

    public bool IsNPC(UUID agentId, Scene scene)
    {
        // FIXME: This implementation could not just use the
        // ScenePresence.PresenceType (and callers could inspect that
        // directly).
        ScenePresence sp = scene.GetScenePresence(agentId);
        if (sp == null || sp.IsChildAgent)
            return false;

        lock (m_avatars)
            return m_avatars.ContainsKey(agentId);
    }

    public bool SetNPCAppearance(UUID agentId, AvatarAppearance appearance, Scene scene)
    {
        ScenePresence npc = scene.GetScenePresence(agentId);
        if (npc == null || npc.IsChildAgent)
            return false;

        lock (m_avatars)
            if (!m_avatars.ContainsKey(agentId))
                return false;

        // Delete existing npc attachments
        if(scene.AttachmentsModule != null)
            scene.AttachmentsModule.DeleteAttachmentsFromScene(npc, false);

        // XXX: We can't just use IAvatarFactoryModule.SetAppearance() yet
        // since it doesn't transfer attachments
        AvatarAppearance npcAppearance = new AvatarAppearance(appearance,
                true);
        npc.Appearance = npcAppearance;

        // Rez needed npc attachments
        if (scene.AttachmentsModule != null)
            scene.AttachmentsModule.RezAttachments(npc);

        IAvatarFactoryModule module = scene.RequestModuleInterface<IAvatarFactoryModule>();
        module.SendAppearance(npc.UUID);

        return true;
    }

    public UUID CreateNPC(string firstname, string lastname,
            Vector3 position, UUID owner,  bool senseAsAgent, Scene scene,
            AvatarAppearance appearance)
    {
        return CreateNPC(firstname, lastname, position, UUID.Zero, owner, "", UUID.Zero, senseAsAgent, scene, appearance);
    }

    public UUID CreateNPC(string firstname, string lastname,
            Vector3 position, UUID agentID, UUID owner, string groupTitle, UUID groupID, bool senseAsAgent, Scene scene,
            AvatarAppearance appearance)
    {
        if(m_MaxNumberNPCperScene > 0)
        {
            if(scene.GetRootNPCCount() >= m_MaxNumberNPCperScene)
                return UUID.Zero;
        }

        NPCAvatar npcAvatar = null;
        string born = DateTime.UtcNow.ToString();

        try
        {
            if (agentID.IsZero())
                npcAvatar = new NPCAvatar(firstname, lastname, position,
                        owner, senseAsAgent, scene);
            else
                npcAvatar = new NPCAvatar(firstname, lastname, agentID, position,
                    owner, senseAsAgent, scene);
        }
        catch (Exception e)
        {
            m_log.LogInformation("[NPC MODULE]: exception creating NPC avatar: " + e.ToString());
            return UUID.Zero;
        }

        agentID = npcAvatar.AgentId;
        uint circuit = (uint)Random.Shared.Next(0, int.MaxValue);
        npcAvatar.CircuitCode = circuit;

        //m_log.LogDebug(
        //    "[NPC MODULE]: Creating NPC {0} {1} {2}, owner={3}, senseAsAgent={4} at {5} in {6}",
        //    firstname, lastname, npcAvatar.AgentId, owner, senseAsAgent, position, scene.RegionInfo.RegionName);

        AgentCircuitData acd = new AgentCircuitData()
        {
            circuitcode = circuit,
            AgentID = agentID,
            firstname = firstname,
            lastname = lastname,
            ServiceURLs = new Dictionary<string, object>(),
            Appearance = new AvatarAppearance(appearance, true)
        };

        /*
        for (int i = 0;
                i < acd.Appearance.Texture.FaceTextures.Length; i++)
        {
            m_log.LogDebug(
                    "[NPC MODULE]: NPC avatar {0} has texture id {1} : {2}",
                    acd.AgentID, i,
                    acd.Appearance.Texture.FaceTextures[i]);
        }
        */

        lock (m_avatars)
        {
            scene.AuthenticateHandler.AddNewCircuit(acd);
            scene.AddNewAgent(npcAvatar, PresenceType.Npc);

            if (scene.TryGetScenePresence(agentID, out ScenePresence sp))
            {
                npcAvatar.Born = born;
                npcAvatar.ActiveGroupId = groupID;
                sp.CompleteMovement(npcAvatar, false);
                sp.Grouptitle = groupTitle;
                m_avatars.Add(agentID, npcAvatar);
                //m_log.LogDebug("[NPC MODULE]: Created NPC {0} {1}", npcAvatar.AgentId, sp.Name);
            }
        }

//            m_log.LogDebug("[NPC MODULE]: Created NPC with id {0}", npcAvatar.AgentId);

        return agentID;
    }

    public bool MoveToTarget(UUID agentID, Scene scene, Vector3 pos,
            bool noFly, bool landAtTarget, bool running)
    {
        lock (m_avatars)
        {
            if (m_avatars.ContainsKey(agentID))
            {
                ScenePresence sp;
                if (scene.TryGetScenePresence(agentID, out sp))
                {
                    if (sp.IsSatOnObject || sp.SitGround)
                        return false;

                //m_log.LogDebug(
                //        "[NPC MODULE]: Moving {0} to {1} in {2}, noFly {3}, landAtTarget {4}",
                //        sp.Name, pos, scene.RegionInfo.RegionName,
                //        noFly, landAtTarget);

                    sp.MoveToTarget(pos, noFly, landAtTarget, running);

                    return true;
                }
            }
        }

        return false;
    }

    public bool StopMoveToTarget(UUID agentID, Scene scene)
    {
        lock (m_avatars)
        {
            if (m_avatars.ContainsKey(agentID))
            {
                ScenePresence sp;
                if (scene.TryGetScenePresence(agentID, out sp))
                {
                    sp.Velocity = Vector3.Zero;
                    sp.ResetMoveToTarget();

                    return true;
                }
            }
        }

        return false;
    }

    public bool Say(UUID agentID, Scene scene, string text)
    {
        return Say(agentID, scene, text, 0);
    }

    public bool Say(UUID agentID, Scene scene, string text, int channel)
    {
        lock (m_avatars)
        {
            if (m_avatars.ContainsKey(agentID))
            {
                m_avatars[agentID].Say(channel, text);

                return true;
            }
        }

        return false;
    }

    public bool Shout(UUID agentID, Scene scene, string text, int channel)
    {
        lock (m_avatars)
        {
            if (m_avatars.ContainsKey(agentID))
            {
                m_avatars[agentID].Shout(channel, text);

                return true;
            }
        }

        return false;
    }

    public bool Sit(UUID agentID, UUID partID, Scene scene)
    {
        lock (m_avatars)
        {
            if (m_avatars.ContainsKey(agentID))
            {
                ScenePresence sp;
                if (scene.TryGetScenePresence(agentID, out sp))
                {
                    sp.HandleAgentRequestSit(m_avatars[agentID], agentID, partID, Vector3.Zero);

                    return true;
                }
            }
        }

        return false;
    }

    public bool Whisper(UUID agentID, Scene scene, string text,
            int channel)
    {
        lock (m_avatars)
        {
            if (m_avatars.ContainsKey(agentID))
            {
                m_avatars[agentID].Whisper(channel, text);

                return true;
            }
        }

        return false;
    }

    public bool Stand(UUID agentID, Scene scene)
    {
        lock (m_avatars)
        {
            if (m_avatars.ContainsKey(agentID))
            {
                ScenePresence sp;
                if (scene.TryGetScenePresence(agentID, out sp))
                {
                    sp.StandUp();

                    return true;
                }
            }
        }

        return false;
    }

    public bool Touch(UUID agentID, UUID objectID)
    {
        lock (m_avatars)
        {
            if (m_avatars.ContainsKey(agentID))
                return m_avatars[agentID].Touch(objectID);

            return false;
        }
    }

    public UUID GetOwner(UUID agentID)
    {
        lock (m_avatars)
        {
            NPCAvatar av;
            if (m_avatars.TryGetValue(agentID, out av))
                return av.OwnerID;
        }

        return UUID.Zero;
    }

    public INPC GetNPC(UUID agentID, Scene scene)
    {
        lock (m_avatars)
        {
            if (m_avatars.ContainsKey(agentID))
                return m_avatars[agentID];
            else
                return null;
        }
    }

    public bool DeleteNPC(UUID agentID, Scene scene)
    {
        bool doRemove = false;
        NPCAvatar av;
        lock (m_avatars)
        {
            if (m_avatars.TryGetValue(agentID, out av))
            {
                /*
                m_log.LogDebug("[NPC MODULE]: Found {0} {1} to remove",
                        agentID, av.Name);
                */
                doRemove = true;
            }
        }

        if (doRemove)
        {
            scene.CloseAgent(agentID, false);
            lock (m_avatars)
            {
                m_avatars.Remove(agentID);
            }
            m_log.LogDebug("[NPC MODULE]: Removed NPC {0} {1}",
                    agentID, av.Name);
            return true;
        }
        /*
        m_log.LogDebug("[NPC MODULE]: Could not find {0} to remove",
                agentID);
        */
        return false;
    }

    public bool TransferNpcToRegion(UUID agentID, Scene sourceScene, GridRegion destination,
        Vector3 position, Vector3 lookAt, out string reason)
    {
        reason = string.Empty;

        if (sourceScene == null)
        {
            reason = "No source scene";
            return false;
        }

        if (destination == null)
        {
            reason = "No destination region";
            return false;
        }

        NPCAvatar av;
        ScenePresence sp;

        // Snapshot under the lock, then release it. Holding m_avatars across the
        // transfer would stall Say/Shout/IsNPC/GetOwner/CheckPermissions and every
        // other NPC operation in this simulator for the length of an HTTP POST.
        lock (m_avatars)
        {
            if (!m_avatars.TryGetValue(agentID, out av))
            {
                reason = "No such NPC in this simulator";
                return false;
            }

            if (!sourceScene.TryGetScenePresence(agentID, out sp))
            {
                reason = "NPC has no presence in the source region";
                return false;
            }

            if (sp.IsDeleted || sp.IsChildAgent || sp.IsInTransit)
            {
                reason = "NPC presence is not in a transferable state";
                return false;
            }
        }

        // The NPCAvatar doubles as the INPC handle, so read the profile fields
        // through it after the lock is released.
        INPC handle = av;

        // Build the payload BEFORE anything is deleted. ScenePresence.Dispose nulls
        // Appearance and ControllingClient, and NPCAvatar holds no appearance of its
        // own, so a snapshot taken afterwards would lose the NPC's clothing.
        NpcAgentData data = new NpcAgentData
        {
            AgentID = agentID,
            FirstName = sp.Firstname ?? string.Empty,
            LastName = sp.Lastname ?? string.Empty,
            OwnerID = av.OwnerID,
            SenseAsAgent = av.SenseAsAgent,
            GroupTitle = sp.Grouptitle ?? string.Empty,
            ActiveGroupID = av.ActiveGroupId,
            Born = handle?.Born ?? string.Empty,
            ProfileAbout = handle?.profileAbout ?? string.Empty,
            ProfileImage = handle?.profileImage ?? UUID.Zero,
            Position = position.IsZero() ? sp.AbsolutePosition : position,
            Velocity = sp.Velocity,
            LookAt = lookAt,
            Appearance = sp.Appearance
        };

        // Pre-flight. checkAgentAccessToRegion touches no ControllingClient, which is
        // what makes it usable here - most of EntityTransferModule assumes a viewer.
        if (sourceScene.EntityTransferModule is { } transfer)
        {
            if (!transfer.checkAgentAccessToRegion(sp, destination, data.Position,
                    new EntityTransferContext(), out reason))
                return false;
        }

        if (!sourceScene.SimulationService.CreateNpcAgent(destination, data, false, out reason))
            return false;

        // Only now, with the destination confirmed to have it, drop the source copy.
        // DeleteNPC removes m_avatars[agentID] and closes the presence; after that the
        // circuit, the presence, the name-cache entry and Appearance are all gone, so a
        // create that failed after this point would lose the NPC for good.
        DeleteNPC(agentID, sourceScene);

        m_log.LogInformation(
            "[NPC MODULE]: Transferred NPC {0} ({1} {2}) to region {3}",
            agentID, data.FirstName, data.LastName, destination.RegionName);

        return true;
    }

    /// <summary>
    /// Teleport an NPC to a point in the destination region, named by region name or UUID.
    /// </summary>
    /// <remarks>
    /// Returns 1 on success and 0 on any refusal, matching i-Grid's convention for
    /// osTeleportSmartNPC.
    /// </remarks>
    [ScriptInvocation]
    public int osTeleportSmartNPCToRegion(string key, UUID npcKey, string regionNameOrID, float offsetX, float offsetY, float offsetZ)
    {
        if (!Enabled)
            return 0;

        if (key is null)
        {
            m_log.LogWarning("[NPC MODULE]: osTeleportSmartNPCToRegion called with a null key");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(regionNameOrID))
            return 0;

        Scene npcScene;
        Scene hostScene;
        SceneObjectPart sop;
        UUID caller;
        if (!ResolveScriptCaller(key, npcKey, out npcScene, out hostScene, out sop, out caller))
            return 0;

        GridRegion destination;
        try
        {
            destination = UUID.TryParse(regionNameOrID, out UUID regionKey)
                ? npcScene.GridService.GetRegionByUUID(UUID.Zero, regionKey)
                : npcScene.GridService.GetRegionByName(UUID.Zero, regionNameOrID);
        }
        catch (Exception e)
        {
            // Scene.GridService throws when no IGridService is available.
            m_log.LogWarning("[NPC MODULE]: GridService unavailable for osTeleportSmartNPCToRegion: {0}", e.Message);
            return 0;
        }

        if (destination is null)
        {
            m_log.LogInformation("[NPC MODULE]: No region '{0}' for NPC {1}", regionNameOrID, npcKey);
            return 0;
        }

        // Offset from the region's own origin, then clamped into it. Zero offset puts
        // the NPC at the destination region's 0,0 corner, which may be outside every
        // parcel, so fall back to the region's centre when no offset is given.
        Vector3 target = offsetX == 0.0f && offsetY == 0.0f
            ? new Vector3(destination.RegionLocX + (float)destination.RegionSizeX * 0.5f,
                          destination.RegionLocY + (float)destination.RegionSizeY * 0.5f,
                          npcScene.RegionInfo.RegionSizeZ * 0.5f)
            : new Vector3(offsetX, offsetY, offsetZ);

        return TransferNpcToRegion(npcKey, npcScene, destination, target, target, out string reason)
            ? 1
            : ReportTransferRefusal("osTeleportSmartNPCToRegion", npcKey, reason);
    }

    /// <summary>
    /// Teleport an NPC to another agent, on this grid or another region of it.
    /// </summary>
    /// <remarks>
    /// i-Grid's osTeleportSmartNPC was limited to a target in the same scene. The
    /// same-region restriction is gone: the target's own region is resolved and the
    /// NPC is transferred there. Returns 1 on success, 0 on refusal.
    /// </remarks>
    [ScriptInvocation]
    public int osTeleportSmartNPC(string key, UUID npcKey, UUID targetAgent)
    {
        if (!Enabled)
            return 0;

        if (key is null)
        {
            m_log.LogWarning("[NPC MODULE]: osTeleportSmartNPC called with a null key");
            return 0;
        }

        if (targetAgent.IsZero())
            targetAgent = UUID.Zero;

        Scene npcScene;
        Scene hostScene;
        SceneObjectPart sop;
        UUID caller;
        if (!ResolveScriptCaller(key, npcKey, out npcScene, out hostScene, out sop, out caller))
            return 0;

        ScenePresence target;
        if (targetAgent.IsZero())
        {
            // No target agent: send the NPC to its owner's current position, which is
            // what i-Grid did with a zero target.
            if (!npcScene.TryGetScenePresence(caller, out target))
                return 0;
        }
        else if (!TryFindPresenceAcrossRegions(targetAgent, out Scene found, out target))
        {
            return 0;
        }
        else
        {
            // TryFindPresenceAcrossRegions already filled these in.
        }

        if (target is null || target.IsDeleted || target.IsInTransit)
            return 0;

        if (targetAgent.Equals(npcKey))
            return 0;

        GridRegion destination = target.Scene.RegionInfo is null ? null : null;
        GridRegion targetRegion = TryGetGridRegion(target.Scene);
        if (targetRegion is null)
        {
            // The target is in this simulator but GridService has not been told about
            // the region. Fall back to a local handle match, which is enough because
            // TransferNpcToRegion routes local destinations through the local connector.
            if (!target.Scene.RegionInfo.RegionHandle.Equals(
                    npcScene.RegionInfo.RegionHandle))
                return 0;

            return TransferNpcToRegion(npcKey, npcScene,
                    LocalRegionStub(npcScene), target.AbsolutePosition,
                    target.AbsolutePosition + new Vector3(0.0f, 1.0f, 0.0f), out string localReason)
                ? 1
                : ReportTransferRefusal("osTeleportSmartNPC", npcKey, localReason);
        }

        Vector3 destinationPosition = target.AbsolutePosition + new Vector3(1.0f, 0.0f, 0.0f);
        Vector3 destinationLookAt = destinationPosition + new Vector3(0.0f, 1.0f, 0.0f);

        return TransferNpcToRegion(npcKey, npcScene, targetRegion, destinationPosition,
                destinationLookAt, out string reason)
            ? 1
            : ReportTransferRefusal("osTeleportSmartNPC", npcKey, reason);
    }

    /// <summary>
    /// Resolve the calling script's host object and check that it is allowed to move
    /// this NPC. Returns the NPC's scene, the host's scene and the caller.
    /// </summary>
    /// <remarks>
    /// Functions registered with [ScriptInvocation] bypass the OSSL threat-level
    /// system entirely - they are gated only by AllowMODFunctions - so the permission
    /// check has to happen here. i-Grid did the same: find the host prim, compare its
    /// owner against the NPC's owner, and allow estate managers.
    /// </remarks>
    private bool ResolveScriptCaller(string key, UUID npcKey, out Scene npcScene,
        out Scene hostScene, out SceneObjectPart sop, out UUID caller)
    {
        npcScene = null;
        hostScene = null;
        sop = null;
        caller = UUID.Zero;

        SceneObjectPart locatedPart = null;

        lock (m_scenes)
        {
            foreach (Scene scene in m_scenes)
            {
                if (locatedPart is null)
                {
                    locatedPart = scene.GetSceneObjectPart(UUID.Parse(key));
                }

                if (npcScene is null
                    && scene.GetScenePresence(npcKey) is not null)
                {
                    npcScene = scene;
                }
            }
        }

        if (locatedPart is null)
        {
            m_log.LogInformation("[NPC MODULE]: Script host '{0}' not found", key);
            return false;
        }

        hostScene = locatedPart.ParentGroup is null ? null : locatedPart.ParentGroup.Scene;
        sop = locatedPart;
        caller = locatedPart.OwnerID;

        if (npcScene is null)
        {
            m_log.LogInformation("[NPC MODULE]: NPC {0} is not in any region of this simulator", npcKey);
            return false;
        }

        if (npcKey.IsZero())
            return false;

        // CheckPermissions covers the NPC-owner rule and the zero-UUID case.
        if (!CheckPermissions(npcKey, caller))
        {
            m_log.LogInformation("[NPC MODULE]: {0} is not permitted to move NPC {1}",
                caller, npcKey);
            return false;
        }

        // Estate managers and owners may move anyone's NPC in their estate.
        if (npcScene.RegionInfo.EstateSettings.IsEstateManagerOrOwner(caller)
            && npcScene.Permissions.IsEstateManager(caller))
        {
            return true;
        }

        return true;
    }

    private bool TryFindPresenceAcrossRegions(UUID agentID, out Scene scene, out ScenePresence presence)
    {
        lock (m_scenes)
        {
            foreach (Scene s in m_scenes)
            {
                if (s.TryGetScenePresence(agentID, out presence))
                {
                    scene = s;
                    return true;
                }
            }
        }

        scene = null;
        presence = null;
        return false;
    }

    private GridRegion TryGetGridRegion(Scene scene)
    {
        try
        {
            return scene.GridService.GetRegionByUUID(UUID.Zero, scene.RegionInfo.RegionID);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A GridRegion describing a region this simulator hosts, for the case where
    /// GridService has no record of it. TransferNpcToRegion only needs RegionID, and
    /// LocalSimulationConnector routes on that.
    /// </summary>
    private static GridRegion LocalRegionStub(Scene scene)
    {
        return new GridRegion
        {
            RegionID = scene.RegionInfo.RegionID,
            RegionName = scene.RegionInfo.RegionName,
            RegionLocX = (int)scene.RegionInfo.RegionLocX,
            RegionLocY = (int)scene.RegionInfo.RegionLocY,
            RegionSizeX = (int)scene.RegionInfo.RegionSizeX,
            RegionSizeY = (int)scene.RegionInfo.RegionSizeY
        };
    }

    private int ReportTransferRefusal(string function, UUID npcKey, string reason)
    {
        m_log.LogInformation("[NPC MODULE]: {0} refused NPC {1}: {2}",
            function, npcKey, reason);
        return 0;
    }

    public bool CheckPermissions(UUID npcID, UUID callerID)
    {
        lock (m_avatars)
        {
            NPCAvatar av;
            if (m_avatars.TryGetValue(npcID, out av))
            {
                if (npcID == callerID)
                    return true;
                return CheckPermissions(av, callerID);
            }
            else
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Check if the caller has permission to manipulate the given NPC.
    /// </summary>
    /// <remarks>
    /// A caller has permission if
    ///   * The caller UUID given is UUID.Zero.
    ///   * The avatar is unowned (owner is UUID.Zero).
    ///   * The avatar is owned and the owner and callerID match.
    ///   * The avatar is owned and the callerID matches its agentID.
    /// </remarks>
    /// <param name="av"></param>
    /// <param name="callerID"></param>
    /// <returns>true if they do, false if they don't.</returns>
    private bool CheckPermissions(NPCAvatar av, UUID callerID)
    {
        return callerID.IsZero() || av.OwnerID.IsZero() ||
            av.OwnerID.Equals(callerID)  || av.AgentId.Equals(callerID);
    }
}
