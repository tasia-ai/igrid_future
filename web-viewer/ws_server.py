#!/usr/bin/env python3
"""
TasiaNGC Web Viewer - WebSocket UDP Proxy Server
Handles multi-user connections to OpenSim via WebSocket
"""

import asyncio
import json
import socket
import struct
import threading
import uuid
import hashlib
import os
from datetime import datetime
import websockets
from websockets.server import WebSocketServerProtocol
import xmlrpc.client

# Configuration
LISTEN_HOST = "0.0.0.0"
LISTEN_PORT = 8765
OPENSIM_HOST = "127.0.0.1"
OPENSIM_PORT = 8002

# Connected web clients
web_clients = {}
# OpenSim UDP connections
sim_connections = {}
# Lock for thread safety
lock = threading.Lock()

# Packet type definitions
class PacketTypes:
    UseCircuitCode = 3
    AgentUpdate = 4
    ChatFromViewer = 80
    AgentThrottle = 81
    AgentFOV = 82
    AgentHeightWidth = 83
    ChatFromSimulator = 139
    RegionHandshake = 148
    RegionHandshakeReply = 149
    CompleteAgentMovement = 249
    LogoutRequest = 252
    ImprovedInstantMessage = 254
    TeleportFinish = 69

class LLUUID:
    def __init__(self, val="00000000-0000-0000-0000-000000000000"):
        if isinstance(val, str):
            self.bytes = self._from_string(val)
        elif isinstance(val, bytes):
            self.bytes = val
        else:
            self.bytes = b'\x00' * 16
    
    def _from_string(self, s):
        s = s.replace('-', '')
        return bytes.fromhex(s) if len(s) == 32 else b'\x00' * 16
    
    def __str__(self):
        return '-'.join([self.bytes[i:i+4].hex() for i in range(0, 16, 4)])
    
    def __bytes__(self):
        return self.bytes

class OpenSimConnection:
    """Manages UDP connection to OpenSim simulator"""
    
    def __init__(self, client_id, sim_host, sim_port, agent_id, session_id, circuit_code):
        self.client_id = client_id
        self.sim_host = sim_host
        self.sim_port = sim_port
        self.agent_id = agent_id
        self.session_id = session_id
        self.circuit_code = circuit_code
        self.sock = None
        self.running = False
        self.sequence = 1
        self.last_seq = 0
        
    def connect(self):
        try:
            self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            self.sock.settimeout(0.5)
            self.sock.bind(("0.0.0.", 0))
            self.running = True
            return True
        except Exception as e:
            print(f"Failed to connect to sim: {e}")
            return False
    
    def send(self, data):
        if self.sock:
            try:
                self.sock.sendto(data, (self.sim_host, self.sim_port))
                return True
            except:
                pass
        return False
    
    def recv(self):
        if self.sock:
            try:
                return self.sock.recvfrom(65507)
            except socket.timeout:
                return None
        return None
    
    def close(self):
        self.running = False
        if self.sock:
            self.sock.close()

def encode_variable(data, type_code=1):
    """Encode variable-length data (type 1 = U8 length, type 2 = U16 length)"""
    if type_code == 1:
        return struct.pack("<B", len(data)) + data
    elif type_code == 2:
        return struct.pack("<H", len(data)) + data
    return data

def decode_variable(data, type_code=1):
    """Decode variable-length data"""
    if type_code == 1:
        length = data[0]
        return data[1:1+length], data[1+length:]
    elif type_code == 2:
        length = struct.unpack("<H", data[:2])[0]
        return data[2:2+length], data[2+length:]
    return data, b''

def zerocode_encode(data):
    """Zero-code compression"""
    output = bytearray()
    i = 0
    while i < len(data):
        if data[i] != 0:
            output.append(data[i])
            i += 1
        else:
            output.append(0)
            i += 1
            count = 0
            while i < len(data) and data[i] == 0 and count < 254:
                count += 1
                i += 1
            output.append(count)
    return bytes(output)

def zerocode_decode(data):
    """Zero-code decompression"""
    output = bytearray()
    i = 0
    while i < len(data):
        if data[i] != 0:
            output.append(data[i])
        else:
            i += 1
            if i < len(data):
                count = data[i]
                output.extend(b'\x00' * (count + 1))
        i += 1
    return bytes(output)

def build_packet(message_id, body, freq=0, reliable=False, zero_coded=True):
    """Build an OpenSim UDP packet"""
    flags = 0
    if zero_coded:
        flags |= 0x80
    if reliable:
        flags |= 0x40
    
    body_encoded = zerocode_encode(body) if zero_coded else body
    
    # Message ID encoding based on frequency
    if freq == 0:  # Low frequency (1 byte)
        mid_bytes = struct.pack(">B", message_id)
    elif freq == 1:  # Medium frequency (2 bytes)
        mid_bytes = struct.pack(">H", message_id + 0xFF00)
    elif freq == 2:  # High frequency (4 bytes)
        mid_bytes = struct.pack(">I", message_id + 0xFFFF0000)
    else:  # Fixed frequency
        mid_bytes = struct.pack(">I", message_id)
    
    packet = struct.pack(">BIB", flags, 0, 0) + mid_bytes + body_encoded
    return packet

def parse_packet(data):
    """Parse an OpenSim UDP packet"""
    if len(data) < 8:
        return None
    
    flags, sequence, extra = struct.unpack_from(">BIB", data, 0)
    zero_coded = (flags & 0x80) != 0
    reliable = (flags & 0x40) != 0
    
    payload = data[6 + extra:]
    
    # Decode message ID
    if payload[0] < 0xFF:
        mid = payload[0]
        offset = 1
    elif payload[1] != 0xFF:
        mid = struct.unpack(">H", payload[:2])[0] - 0xFF00
        offset = 2
    else:
        mid = struct.unpack(">I", payload[:4])[0] - 0xFFFF0000
        offset = 4
    
    body = zerocode_decode(payload[offset:]) if zero_coded else payload[offset:]
    
    return {
        'mid': mid,
        'flags': flags,
        'sequence': sequence,
        'reliable': reliable,
        'zero_coded': zero_coded,
        'body': body,
        'raw': data
    }

def build_use_circuit_code(agent_id, session_id, circuit_code):
    """Build UseCircuitCode packet"""
    body = struct.pack("<I", circuit_code)
    body += agent_id.bytes
    body += session_id.bytes
    return build_packet(PacketTypes.UseCircuitCode, body, freq=2, reliable=True, zero_coded=False)

def build_agent_update(agent_id, session_id, position, rotation, camera_center, controls=0):
    """Build AgentUpdate packet for movement"""
    body = agent_id.bytes + session_id.bytes
    body += rotation  # Body rotation (4 floats)
    body += rotation  # Head rotation
    body += struct.pack("<B", 0)  # State
    body += struct.pack("<fff", *position)  # Camera center
    body += struct.pack("<fff", 0, 1, 0)  # Camera at axis
    body += struct.pack("<fff", 1, 0, 0)  # Camera left
    body += struct.pack("<fff", 0, 0, 1)  # Camera up
    body += struct.pack("<f", 256.0)  # Far
    body += struct.pack("<I", controls)  # Control flags
    body += struct.pack("<B", 0)  # Flags
    
    return build_packet(PacketTypes.AgentUpdate, body, freq=0)

def build_complete_movement(agent_id, session_id, circuit_code):
    """Build CompleteAgentMovement packet"""
    body = agent_id.bytes + session_id.bytes
    body += struct.pack("<I", circuit_code)
    return build_packet(PacketTypes.CompleteAgentMovement, body, freq=2, reliable=True, zero_coded=False)

def build_chat_from_viewer(agent_id, session_id, message, channel=0):
    """Build ChatFromViewer packet"""
    body = agent_id.bytes + session_id.bytes
    msg_bytes = message.encode('utf-8') + b'\x00'
    body += encode_variable(msg_bytes, 2)  # Message (Variable 2)
    body += struct.pack("<Bi", 1, channel)  # Type, Channel
    return build_packet(PacketTypes.ChatFromViewer, body, freq=2, reliable=True, zero_coded=False)

class WebViewerServer:
    def __init__(self):
        self.clients = {}
        self.connections = {}
    
    async def handle_client(self, websocket: WebSocketServerProtocol, path: str):
        client_id = str(uuid.uuid4())
        print(f"Client connected: {client_id}")
        
        self.clients[client_id] = {
            'ws': websocket,
            'connected': True,
            'position': [128, 0, 30],
            'rotation': [0, 0, 0, 1],
            'name': ''
        }
        
        try:
            async for message in websocket:
                if isinstance(message, bytes):
                    # Binary data - forward to OpenSim
                    await self.handle_sim_data(client_id, message)
                else:
                    # Text data - JSON command
                    try:
                        data = json.loads(message)
                        await self.handle_json_command(client_id, data)
                    except json.JSONDecodeError:
                        print(f"Invalid JSON from client {client_id}")
        
        except websockets.exceptions.ConnectionClosed:
            print(f"Client disconnected: {client_id}")
        finally:
            await self.cleanup_client(client_id)
    
    async def handle_json_command(self, client_id, data):
        """Handle JSON commands from web client"""
        cmd = data.get('cmd')
        
        if cmd == 'login':
            await self.handle_login(client_id, data)
        elif cmd == 'logout':
            await self.handle_logout(client_id)
        elif cmd == 'chat':
            await self.handle_chat(client_id, data)
        elif cmd == 'move':
            await self.handle_move(client_id, data)
        elif cmd == 'teleport':
            await self.handle_teleport(client_id, data)
    
    async def handle_login(self, client_id, data):
        """Handle login request from web client"""
        first = data.get('first', '')
        last = data.get('last', '')
        password = data.get('password', '')
        start = data.get('start', 'last')
        
        # Get MAC and id0
        mac = ':'.join(['%02x' % ((uuid.getnode() >> (i * 8)) & 0xff) for i in range(6)])
        id0 = hashlib.md5(f"OpenSim:{mac}:WebViewer".encode()).hexdigest()
        
        # XML-RPC login
        login_params = {
            "first": first,
            "last": last,
            "passwd": password,
            "start": start,
            "channel": "TasiaNGC-WebViewer",
            "mac": mac,
            "id0": id0,
            "version": "TasiaNGC-1.0.0",
            "platform": "Win"
        }
        
        try:
            proxy = xmlrpc.client.ServerProxy(f"http://{OPENSIM_HOST}:{OPENSIM_PORT}/")
            result = proxy.login_to_simulator(login_params)
            
            if result.get("login") == "true":
                agent_id = LLUUID(result.get("agent_id"))
                session_id = LLUUID(result.get("session_id"))
                circuit_code = int(result.get("circuit_code"))
                sim_host = result.get("sim_ip")
                sim_port = int(result.get("sim_port"))
                
                # Create OpenSim connection
                conn = OpenSimConnection(client_id, sim_host, sim_port, agent_id, session_id, circuit_code)
                if conn.connect():
                    with lock:
                        self.connections[client_id] = conn
                    
                    # Store client info
                    self.clients[client_id]['name'] = f"{first} {last}"
                    self.clients[client_id]['agent_id'] = str(agent_id)
                    self.clients[client_id]['session_id'] = str(session_id)
                    
                    # Send UseCircuitCode
                    conn.send(build_use_circuit_code(agent_id, session_id, circuit_code))
                    
                    # Start receiving packets
                    asyncio.create_task(self.receive_from_sim(client_id))
                    
                    # Send success response
                    await self.send_to_client(client_id, {
                        'type': 'login_success',
                        'agent_id': str(agent_id),
                        'session_id': str(session_id),
                        'sim_host': sim_host,
                        'sim_port': sim_port,
                        'region': result.get('region', 'Unknown')
                    })
                    
                    print(f"User {first} {last} logged in to {sim_host}:{sim_port}")
                else:
                    await self.send_to_client(client_id, {
                        'type': 'error',
                        'message': 'Failed to connect to simulator'
                    })
            else:
                await self.send_to_client(client_id, {
                    'type': 'error',
                    'message': result.get('message', 'Login failed')
                })
        except Exception as e:
            await self.send_to_client(client_id, {
                'type': 'error',
                'message': str(e)
            })
    
    async def receive_from_sim(self, client_id):
        """Receive packets from OpenSim and forward to web client"""
        while client_id in self.connections:
            conn = self.connections.get(client_id)
            if not conn:
                break
            
            result = conn.recv()
            if result:
                data, addr = result
                packet = parse_packet(data)
                
                if packet:
                    # Handle different packet types
                    if packet['mid'] == PacketTypes.RegionHandshake:
                        # Send CompleteAgentMovement
                        conn.send(build_complete_movement(
                            conn.agent_id, conn.session_id, conn.circuit_code
                        ))
                        
                        await self.send_to_client(client_id, {
                            'type': 'region_handshake'
                        })
                    
                    elif packet['mid'] == PacketTypes.ChatFromSimulator:
                        # Parse chat message
                        body = packet['body']
                        try:
                            from_name, body = decode_variable(body, 1)
                            from_name = from_name.rstrip(b'\x00').decode('utf-8', errors='ignore')
                            
                            # Skip source ID, owner ID, source type, chat type, audible (18 bytes)
                            body = body[18:]
                            message, body = decode_variable(body, 2)
                            message = message.rstrip(b'\x00').decode('utf-8', errors='ignore')
                            
                            await self.send_to_client(client_id, {
                                'type': 'chat',
                                'from': from_name,
                                'message': message
                            })
                        except:
                            pass
                    
                    elif packet['mid'] == 16 or packet['mid'] == 17:  # ObjectUpdate
                        await self.send_to_client(client_id, {
                            'type': 'object_update',
                            'data': data.hex()
                        })
                    
                    elif packet['mid'] == 6:  # CoarseLocationUpdate
                        await self.send_to_client(client_id, {
                            'type': 'coarse_location',
                            'data': data.hex()
                        })
                    
                    elif packet['mid'] == PacketTypes.AgentMovementComplete:
                        await self.send_to_client(client_id, {
                            'type': 'movement_complete'
                        })
                    
                    # Forward raw packet to client
                    await self.send_to_client(client_id, {
                        'type': 'sim_packet',
                        'data': data.hex()
                    })
            
            await asyncio.sleep(0.01)
    
    async def handle_sim_data(self, client_id, data):
        """Handle data received from web client, forward to OpenSim"""
        with lock:
            conn = self.connections.get(client_id)
        
        if conn:
            conn.send(data)
    
    async def handle_chat(self, client_id, data):
        """Handle chat message from web client"""
        with lock:
            conn = self.connections.get(client_id)
        
        if conn:
            message = data.get('message', '')
            packet = build_chat_from_viewer(
                conn.agent_id, conn.session_id, message
            )
            conn.send(packet)
    
    async def handle_move(self, client_id, data):
        """Handle movement from web client"""
        with lock:
            conn = self.connections.get(client_id)
        
        if conn:
            pos = data.get('position', [128, 0, 30])
            rot = data.get('rotation', [0, 0, 0, 1])
            controls = data.get('controls', 0)
            
            self.clients[client_id]['position'] = pos
            
            packet = build_agent_update(
                conn.agent_id, conn.session_id,
                pos, bytes(16),  # rotation
                bytes(12),  # camera
                controls
            )
            conn.send(packet)
    
    async def handle_teleport(self, client_id, data):
        """Handle teleport request"""
        # For now, just notify client
        await self.send_to_client(client_id, {
            'type': 'info',
            'message': 'Teleport not yet implemented'
        })
    
    async def handle_logout(self, client_id):
        """Handle logout"""
        await self.cleanup_client(client_id)
        await self.send_to_client(client_id, {
            'type': 'logout_success'
        })
    
    async def send_to_client(self, client_id, data):
        """Send JSON data to web client"""
        if client_id in self.clients:
            try:
                await self.clients[client_id]['ws'].send(json.dumps(data))
            except:
                pass
    
    async def cleanup_client(self, client_id):
        """Clean up client on disconnect"""
        with lock:
            if client_id in self.connections:
                self.connections[client_id].close()
                del self.connections[client_id]
        
        if client_id in self.clients:
            del self.clients[client_id]

async def main():
    server = WebViewerServer()
    
    print(f"Starting WebSocket server on ws://{LISTEN_HOST}:{LISTEN_PORT}")
    print(f"OpenSim target: {OPENSIM_HOST}:{OPENSIM_PORT}")
    
    async with websockets.serve(server.handle_client, LISTEN_HOST, LISTEN_PORT):
        await asyncio.Future()  # Run forever

if __name__ == '__main__':
    # Check for environment variables
    OPENSIM_HOST = os.getenv('OPENSIM_HOST', OPENSIM_HOST)
    OPENSIM_PORT = int(os.getenv('OPENSIM_PORT', OPENSIM_PORT))
    LISTEN_PORT = int(os.getenv('WS_PORT', LISTEN_PORT))
    
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("Server stopped")
