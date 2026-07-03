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
using System.Reflection;
using System.Text;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;

namespace OpenSim.Server.Handlers.Grid
{
    public class GridServiceConnector : ServiceConnector
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private IGridService m_GridService;
        private string m_ConfigName = "GridService";

        public GridServiceConnector(IConfigSource config, IHttpServer server, string configName) :
                base(config, server, configName)
        {
            IConfig serverConfig = config.Configs[m_ConfigName];
            if (serverConfig == null)
                throw new Exception(String.Format("No section {0} in config file", m_ConfigName));

            string gridService = serverConfig.GetString("LocalServiceModule",
                    String.Empty);

            if (gridService.Length == 0)
                throw new Exception("No LocalServiceModule in config file");

            Object[] args = new Object[] { config };
            m_GridService = ServerUtils.LoadPlugin<IGridService>(gridService, args);

            IServiceAuth auth = ServiceAuth.Create(config, m_ConfigName);

            server.AddStreamHandler(new GridServerPostHandler(m_GridService, auth));

            // QUIC region discovery endpoint — returns regions with QUIC support
            server.AddSimpleStreamHandler(new SimpleStreamHandler("/quic_regions", HandleQuicRegionsRequest));
        }

        /// <summary>
        /// Handles GET /quic_regions — returns JSON list of regions with QUIC support.
        /// Used by Tasia Viewer for pre-login QUIC discovery.
        /// </summary>
        private void HandleQuicRegionsRequest(IOSHttpRequest request, IOSHttpResponse response)
        {
            try
            {
                // Get all regions across a wide coordinate range
                var allRegions = m_GridService.GetRegionRange(UUID.Zero, int.MinValue, int.MaxValue, int.MinValue, int.MaxValue);

                var quicRegionsList = new List<OSD>();
                foreach (var region in allRegions)
                {
                    if (!string.IsNullOrWhiteSpace(region.QuicHost) && region.QuicPort > 0)
                    {
                        var regionMap = new OSDMap
                        {
                            ["uuid"] = OSD.FromUUID(region.RegionID),
                            ["name"] = OSD.FromString(region.RegionName),
                            ["quic_host"] = OSD.FromString(region.QuicHost),
                            ["quic_port"] = OSD.FromInteger((int)region.QuicPort),
                            ["x"] = OSD.FromInteger(region.RegionLocX),
                            ["y"] = OSD.FromInteger(region.RegionLocY)
                        };
                        quicRegionsList.Add(regionMap);
                    }
                }

                var result = new OSDMap
                {
                    ["success"] = OSD.FromBoolean(true),
                    ["regions"] = new OSDArray(quicRegionsList)
                };

                string json = OSDParser.SerializeJsonString(result);
                byte[] data = Encoding.UTF8.GetBytes(json);

                response.StatusCode = (int)System.Net.HttpStatusCode.OK;
                response.ContentType = "application/json";
                response.ContentLength64 = data.Length;
                response.RawBufferStart = 0;
                response.RawBufferLen = data.Length;
                response.RawBuffer = data;
            }
            catch (Exception ex)
            {
                m_log.Error($"[QUIC REGIONS]: Failed to retrieve region list: {ex.Message}");

                var errorResult = new OSDMap
                {
                    ["success"] = OSD.FromBoolean(false),
                    ["error"] = OSD.FromString(ex.Message)
                };

                string json = OSDParser.SerializeJsonString(errorResult);
                byte[] data = Encoding.UTF8.GetBytes(json);

                response.StatusCode = (int)System.Net.HttpStatusCode.InternalServerError;
                response.ContentType = "application/json";
                response.ContentLength64 = data.Length;
                response.RawBufferStart = 0;
                response.RawBufferLen = data.Length;
                response.RawBuffer = data;
            }
        }
    }
}
