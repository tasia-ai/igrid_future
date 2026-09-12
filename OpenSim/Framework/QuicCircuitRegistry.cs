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

using System.Collections.Concurrent;
using System.Net;
using System.Collections.Generic;
using System.Linq;
using System;

namespace OpenSim.Framework
{
    /// <summary>
    /// Thread-safe in-memory registry mapping QUIC circuit codes to sim UDP endpoints.
    ///
    /// Shared between LLLoginService and QuicProxyConnector within the ROBUST process.
    /// Also receives remote registrations from sims via HTTP for teleport-aware routing.
    /// </summary>
    public static class QuicCircuitRegistry
    {
        public static event Action<uint, IPEndPoint> QuicEndpointRegistered;
        public static event Action<uint> CircuitUnregistered;
        /// <summary>circuitCode → sim UDP endpoint</summary>
        private static readonly ConcurrentDictionary<uint, IPEndPoint> s_circuits = new();

        /// <summary>circuitCode → sim QUIC endpoint</summary>
        private static readonly ConcurrentDictionary<uint, IPEndPoint> s_quicEndpoints = new();

        /// <summary>
        /// Known sim UDP endpoints, learned from circuit registrations and grid service queries.
        /// Used for broadcast fallback when a circuit is not locally registered.
        /// </summary>
        private static readonly ConcurrentDictionary<string, IPEndPoint> s_knownSims = new();

        /// <summary>
        /// Register a circuit code with its sim endpoint.
        /// Called by:
        ///   - LLLoginService on login (same process, pre-registers before viewer connects)
        ///   - Sim QuicServerModule on circuit creation (for teleport cross-registration)
        ///   - Proxy HTTP handler (remote registration from sims)
        /// </summary>
        public static void Register(uint circuitCode, IPEndPoint simEndpoint)
        {
            s_circuits[circuitCode] = simEndpoint;

            // Also learn this sim for future broadcast fallback
            string key = $"{simEndpoint.Address}:{simEndpoint.Port}";
            s_knownSims[key] = simEndpoint;
        }

        /// <summary>
        /// Try to get the sim endpoint for a circuit code.
        /// Returns true if found, false otherwise.
        /// </summary>
        public static bool TryGetCircuit(uint circuitCode, out IPEndPoint simEndpoint)
        {
            return s_circuits.TryGetValue(circuitCode, out simEndpoint);
        }

        /// <summary>
        /// Register a circuit code with its QUIC endpoint (sim's QUIC listener).
        /// Needed by the proxy to establish a QUIC→QUIC bridge instead of QUIC→UDP.
        /// </summary>
        public static void RegisterQUIC(uint circuitCode, IPEndPoint quicEndpoint)
        {
            s_quicEndpoints[circuitCode] = quicEndpoint;
            QuicEndpointRegistered?.Invoke(circuitCode, quicEndpoint);
        }

        /// <summary>
        /// Try to get the sim QUIC endpoint for a circuit code.
        /// </summary>
        public static bool TryGetQUIC(uint circuitCode, out IPEndPoint quicEndpoint)
        {
            return s_quicEndpoints.TryGetValue(circuitCode, out quicEndpoint);
        }

        /// <summary>
        /// Remove a circuit registration.
        /// Called when:
        ///   - Viewer disconnects (proxy detects QUIC stream close)
        ///   - Sim unregisters on client disconnect
        /// </summary>
        public static void Unregister(uint circuitCode)
        {
            s_circuits.TryRemove(circuitCode, out _);
            s_quicEndpoints.TryRemove(circuitCode, out _);
            CircuitUnregistered?.Invoke(circuitCode);
        }

        /// <summary>
        /// Remove a circuit registration and return the associated endpoint (if any).
        /// Returns true if the circuit was registered.
        /// </summary>
        public static bool TryUnregister(uint circuitCode, out IPEndPoint simEndpoint)
        {
            bool removed = s_circuits.TryRemove(circuitCode, out simEndpoint);
            s_quicEndpoints.TryRemove(circuitCode, out _);
            return removed;
        }

        /// <summary>
        /// Remove a circuit only if the currently registered endpoint still
        /// belongs to the caller. This protects teleports where OpenSim reuses
        /// the same circuit code while moving from one simulator to another.
        /// </summary>
        public static bool TryUnregisterIfMatches(uint circuitCode, IPEndPoint simEndpoint, IPEndPoint quicEndpoint)
        {
            bool hasExpectedEndpoint = false;

            if (quicEndpoint != null)
            {
                hasExpectedEndpoint = true;
                if (!s_quicEndpoints.TryGetValue(circuitCode, out IPEndPoint currentQuic) ||
                    !EndpointEquals(currentQuic, quicEndpoint))
                    return false;
            }

            if (simEndpoint != null)
            {
                hasExpectedEndpoint = true;
                if (!s_circuits.TryGetValue(circuitCode, out IPEndPoint currentSim) ||
                    !EndpointEquals(currentSim, simEndpoint))
                    return false;
            }

            if (!hasExpectedEndpoint)
                return false;

            bool removed = false;
            if (simEndpoint != null)
            {
                removed |= ((ICollection<KeyValuePair<uint, IPEndPoint>>)s_circuits)
                    .Remove(new KeyValuePair<uint, IPEndPoint>(circuitCode, simEndpoint));
            }

            if (quicEndpoint != null)
            {
                removed |= ((ICollection<KeyValuePair<uint, IPEndPoint>>)s_quicEndpoints)
                    .Remove(new KeyValuePair<uint, IPEndPoint>(circuitCode, quicEndpoint));
            }

            return removed;
        }

        private static bool EndpointEquals(IPEndPoint a, IPEndPoint b)
        {
            return a != null && b != null && a.Port == b.Port && a.Address.Equals(b.Address);
        }

        /// <summary>
        /// Get all known sim UDP endpoints for broadcast fallback.
        /// </summary>
        public static List<IPEndPoint> GetAllKnownSims()
        {
            return s_knownSims.Values.ToList();
        }

        /// <summary>
        /// Register a sim endpoint so it's known for future broadcasts.
        /// </summary>
        public static void RegisterSim(IPEndPoint simEndpoint)
        {
            string key = $"{simEndpoint.Address}:{simEndpoint.Port}";
            s_knownSims[key] = simEndpoint;
        }

        /// <summary>
        /// Clear all data. Used for testing or full reset.
        /// </summary>
        public static void Clear()
        {
            s_circuits.Clear();
            s_quicEndpoints.Clear();
            s_knownSims.Clear();
        }

        /// <summary>
        /// Get the number of currently registered circuits.
        /// </summary>
        public static int CircuitCount => s_circuits.Count;

        /// <summary>
        /// Get the number of known sims.
        /// </summary>
        public static int KnownSimCount => s_knownSims.Count;
    }
}
