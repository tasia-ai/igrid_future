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
using System.Net;
using System.Reflection;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace OpenSim.Server.Handlers.Simulation;

/// <summary>
/// Receives a SmartNPC arriving from another simulator's region and materialises
/// it here. The counterpart to SimulationServiceConnector.CreateNpcAgent.
/// </summary>
/// <remarks>
/// Modelled on ObjectSimpleHandler: a SimpleStreamHandler, POST only, authorised
/// through ControlPlaneAccess. Because this grid runs one simulator process per
/// region, every transfer crosses a process boundary, so the source simulator's
/// address must appear in the destination's Security/ControlPlaneTrustedHosts.
/// </remarks>
public class NpcAgentSimpleHandler : SimpleStreamHandler
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private readonly ISimulationService m_SimulationService;
    private readonly ControlPlaneAccess m_ControlPlaneAccess;

    public NpcAgentSimpleHandler(ISimulationService service, ControlPlaneAccess controlPlaneAccess)
        : base("/npcagent")
    {
        m_SimulationService = service;
        m_ControlPlaneAccess = controlPlaneAccess;
    }

    protected override void ProcessRequest(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        httpResponse.KeepAlive = false;

        if (m_SimulationService == null)
        {
            httpResponse.StatusCode = (int)HttpStatusCode.InternalServerError;
            httpResponse.RawBuffer = Utils.falseStrBytes;
            return;
        }

        if (httpRequest.HttpMethod != "POST")
        {
            httpResponse.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            return;
        }

        if (!m_ControlPlaneAccess.Authorize(httpRequest, httpResponse))
            return;

        OSDMap args = Utils.DeserializeJSONOSMap(httpRequest);
        if (args == null)
        {
            m_log.LogWarning("[NPC AGENT HANDLER]: Could not read the request body");
            httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
            httpResponse.RawBuffer = Utils.falseStrBytes;
            return;
        }

        NpcAgentData npc = new NpcAgentData();
        if (!npc.Unpack(args))
        {
            m_log.LogWarning("[NPC AGENT HANDLER]: Malformed NPC payload");
            httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
            httpResponse.RawBuffer = Utils.falseStrBytes;
            return;
        }

        GridRegion destination = new GridRegion();
        destination.RegionID = args["destination_uuid"].AsUUID();
        destination.RegionLocX = args["destination_x"].AsInteger();
        destination.RegionLocY = args["destination_y"].AsInteger();
        destination.RegionName = args["destination_name"].AsString();

        string reason;
        bool ok;
        try
        {
            ok = m_SimulationService.CreateNpcAgent(destination, npc, false, out reason);
        }
        catch (Exception e)
        {
            m_log.LogWarning("[NPC AGENT HANDLER]: CreateNpcAgent for {0} threw: {1}", npc.AgentID, e.ToString());
            ok = false;
            reason = "Exception while creating the NPC";
        }

        if (!ok)
        {
            // Deliberately still 200: this is a refusal by the destination, not a
            // malformed request. The caller reads the reason out of the body. A 4xx
            // here would read as a transport failure and lose the explanation.
            m_log.LogInformation(
                "[NPC AGENT HANDLER]: Refused NPC {0} ({1} {2}): {3}",
                npc.AgentID, npc.FirstName, npc.LastName, reason);

            OSDMap result = new OSDMap();
            result["success"] = OSD.FromBoolean(false);
            OSDMap inner = new OSDMap();
            inner["reason"] = OSD.FromString(reason ?? string.Empty);
            result["_Result"] = inner;
            httpResponse.RawBuffer = Util.UTF8.GetBytes(OSDParser.SerializeJsonString(result));
            return;
        }

        OSDMap okResult = new OSDMap();
        okResult["success"] = OSD.FromBoolean(true);
        OSDMap okInner = new OSDMap();
        okInner["reason"] = OSD.FromString(string.Empty);
        okResult["_Result"] = okInner;
        httpResponse.RawBuffer = Util.UTF8.GetBytes(OSDParser.SerializeJsonString(okResult));
    }
}
