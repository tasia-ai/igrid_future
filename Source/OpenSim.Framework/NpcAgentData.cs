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
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace OpenSim.Framework;

/// <summary>
/// The state needed to materialise an NPC in a region other than the one it was
/// created in.
///
/// This is deliberately NOT <see cref="AgentData"/>. AgentData is the viewer
/// transfer payload: routing one of those into IncomingUpdateChildAgent makes
/// the destination call WaitGetScenePresence, which polls for 30 seconds
/// waiting for a viewer to send UseCircuitCode and then fails silently. An NPC
/// has no viewer and never will, so it needs its own payload and its own
/// landing method.
///
/// Neither AgentData nor AgentCircuitData carries anything about owner,
/// senseAsAgent or profile fields, so none of that rides along by accident.
/// </summary>
public class NpcAgentData
{
    /// <summary>
    /// The NPC's agent UUID. Preserved across regions so the same NPC keeps its
    /// identity - this is what makes create-then-delete at the destination safe
    /// to reason about.
    /// </summary>
    public UUID AgentID;

    public string FirstName = string.Empty;
    public string LastName = string.Empty;

    public UUID OwnerID;
    public bool SenseAsAgent;
    public string GroupTitle = string.Empty;
    public UUID ActiveGroupID;

    // Profile data. INPCModule.CreateNPC sets none of this; without it a
    // transferred NPC shows a blank profile card.
    public string Born = string.Empty;
    public string ProfileAbout = string.Empty;
    public UUID ProfileImage;

    public Vector3 Position;
    public Vector3 Velocity;
    public Vector3 LookAt;

    public AvatarAppearance Appearance;

    public OSDMap Pack(EntityTransferContext ctx)
    {
        OSDMap args = new OSDMap();

        // Discriminator, so a receiving handler can tell this apart from an
        // AgentData payload on the same URL space.
        args["message_type"] = OSD.FromString("NpcAgentData");

        args["agent_id"] = OSD.FromUUID(AgentID);
        args["first_name"] = OSD.FromString(FirstName ?? string.Empty);
        args["last_name"] = OSD.FromString(LastName ?? string.Empty);
        args["owner_id"] = OSD.FromUUID(OwnerID);
        args["sense_as_agent"] = OSD.FromBoolean(SenseAsAgent);
        args["group_title"] = OSD.FromString(GroupTitle ?? string.Empty);
        args["active_group_id"] = OSD.FromUUID(ActiveGroupID);

        args["born"] = OSD.FromString(Born ?? string.Empty);
        args["profile_about"] = OSD.FromString(ProfileAbout ?? string.Empty);
        args["profile_image"] = OSD.FromUUID(ProfileImage);

        args["position"] = OSD.FromString(Position.ToString());
        args["velocity"] = OSD.FromString(Velocity.ToString());
        args["look_at"] = OSD.FromString(LookAt.ToString());

        if (Appearance != null)
            args["packed_appearance"] = Appearance.Pack(ctx);

        return args;
    }

    /// <summary>
    /// Unpack from a map produced by <see cref="Pack"/>. Never throws: a bad
    /// payload comes back with AgentID left at Zero, which every caller must
    /// treat as a failure.
    /// </summary>
    public bool Unpack(OSDMap args)
    {
        if (args == null)
            return false;

        try
        {
            AgentID = args["agent_id"].AsUUID();
            FirstName = args["first_name"].AsString();
            LastName = args["last_name"].AsString();
            OwnerID = args["owner_id"].AsUUID();
            SenseAsAgent = args["sense_as_agent"].AsBoolean();
            GroupTitle = args["group_title"].AsString();
            ActiveGroupID = args["active_group_id"].AsUUID();

            Born = args["born"].AsString();
            ProfileAbout = args["profile_about"].AsString();
            ProfileImage = args["profile_image"].AsUUID();

            Vector3.TryParse(args["position"], out Position);
            Vector3.TryParse(args["velocity"], out Velocity);
            Vector3.TryParse(args["look_at"], out LookAt);

            if (args["packed_appearance"] != null)
                Appearance = new AvatarAppearance((OSDMap)args["packed_appearance"]);
        }
        catch (Exception)
        {
            // A malformed payload must not take the receiving simulator with it.
            AgentID = UUID.Zero;
            return false;
        }

        return !AgentID.IsZero();
    }
}
