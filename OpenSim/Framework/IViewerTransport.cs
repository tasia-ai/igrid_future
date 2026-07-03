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
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS "AS IS" AND ANY
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

namespace OpenSim.Framework
{
    /// <summary>
    /// Abstraction for a viewer transport connection.
    /// Implementations exist for LLUDP and QUIC transports.
    /// </summary>
    public interface IViewerTransport
    {
        /// <summary>
        /// Send a packet payload over this transport.
        /// </summary>
        /// <param name="payload">Raw LL packet bytes.</param>
        /// <param name="reliable">If true, transport should guarantee delivery.</param>
        void SendPacket(byte[] payload, bool reliable);

        /// <summary>
        /// Raised when a complete packet payload is received.
        /// Parameter 1: raw LL packet bytes.
        /// Parameter 2: the transport that received them.
        /// </summary>
        event Action<byte[], IViewerTransport> PacketReceived;

        /// <summary>
        /// Close the transport connection.
        /// </summary>
        void Close(string reason);

        /// <summary>
        /// Human-readable transport name for logging.
        /// </summary>
        string TransportName { get; }

        /// <summary>
        /// Whether the transport is still connected.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// The remote endpoint of this transport.
        /// </summary>
        IPEndPoint RemoteEndPoint { get; }
    }
}
