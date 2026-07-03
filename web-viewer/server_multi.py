#!/usr/bin/env python3
"""
TasiaNGC Web Viewer - Multi-User WebSocket Server
Handles multiple users connecting to OpenSim via WebSocket
"""

import asyncio
import json
import socket
import struct
import threading
import uuid
import hashlib
import socketserver
from http.server import HTTPServer, SimpleHTTPRequestHandler
from urllib.parse import urlparse, parse_qs
import xmlrpc.client
import base64

# Configuration
LISTEN_HOST = "0.0.0.0"
LISTEN_PORT = 8080
OPENSIM_HOST = "127.0.0.1"
OPENSIM_PORT = 8002

# Store connected clients
clients = {}
client_lock = threading.Lock()

class OpenSimClient:
    """Manages a connection to OpenSim for a web user"""
    
    def __init__(self, client_id, ws):
        self.client_id = client_id
        self.ws = ws
        self.agent_id = None
        self.session_id = None
        self.circuit_code = None
        self.sim_host = None
        self.sim_port = None
        self.sock = None
        self.running = False
        self.sequence = 1
        
    def login(self, first, last, password, start="last", grid_url=None):
        """Login to OpenSim via XML-RPC"""
        global OPENSIM_HOST, OPENSIM_PORT
        
        # Use provided grid URL or default
        target_host = OPENSIM_HOST
        target_port = OPENSIM_PORT
        if grid_url:
            parsed = urlparse(grid_url)
            target_host = parsed.hostname or OPENSIM_HOST
            target_port = parsed.port or OPENSIM_PORT
        
        # Get MAC address (simulated)
        mac = ':'.join(['%02x' % ((uuid.getnode() >> (i * 8)) & 0xff) for i in range(6)])
        
        # Generate id0 (hardware ID)
        id0 = hashlib.md5(f"OpenSim:{mac}:Python".encode()).hexdigest()
        
        # Build login request
        login_params = {
            "first": first,
            "last": last,
            "passwd": password,  # OpenSim accepts plain text
            "start": start,
            "channel": "TasiaNGC-WebViewer",
            "mac": mac,
            "id0": id0,
            "version": "TasiaNGC-1.0.0",
            "platform": "Win",
            "options": ["inventory-root", "buddy-list", "login-flags", "global-textures"]
        }
        
        try:
            proxy = xmlrpc.client.ServerProxy(f"http://{target_host}:{target_port}/")
            result = proxy.login_to_simulator(login_params)
            
            if result.get("login") == "true":
                self.agent_id = result.get("agent_id")
                self.session_id = result.get("session_id")
                self.circuit_code = result.get("circuit_code")
                self.sim_host = result.get("sim_ip")
                self.sim_port = int(result.get("sim_port"))
                
                return {
                    "success": True,
                    "data": result
                }
            else:
                return {
                    "success": False,
                    "error": result.get("message", "Login failed")
                }
        except Exception as e:
            return {
                "success": False,
                "error": str(e)
            }
    
    def connect_to_sim(self):
        """Establish UDP connection to simulator"""
        if not self.sim_host or not self.sim_port:
            return False
            
        try:
            self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            self.sock.settimeout(1.0)
            self.sock.bind(("0.0.0.0", 0))
            self.running = True
            return True
        except Exception as e:
            print(f"Failed to connect to sim: {e}")
            return False
    
    def send_packet(self, data):
        """Send UDP packet to simulator"""
        if self.sock and self.sim_host:
            try:
                self.sock.sendto(data, (self.sim_host, self.sim_port))
                return True
            except:
                pass
        return False
    
    def recv_packet(self):
        """Receive UDP packet from simulator"""
        if self.sock:
            try:
                return self.sock.recvfrom(65507)
            except socket.timeout:
                return None
        return None
    
    def close(self):
        """Close connection"""
        self.running = False
        if self.sock:
            self.sock.close()


class ThreadedHTTPServer(HTTPServer):
    """HTTP server that handles each request in a new thread"""
    def process_request(self, request, client_address):
        thread = threading.Thread(target=self._process_request_thread, args=(request, client_address))
        thread.daemon = True
        thread.start()
    
    def _process_request_thread(self, request, client_address):
        try:
            self.finish_request(request, client_address)
        except:
            self.handle_error(request, client_address)
        finally:
            self.shutdown_request(request)


class WebViewerHandler(SimpleHTTPRequestHandler):
    """HTTP request handler for web viewer"""
    
    def log_message(self, format, *args):
        print(f"[HTTP] {args[0]}")
    
    def do_GET(self):
        if self.path == '/' or self.path == '/index.html':
            self.send_response(200)
            self.send_header('Content-Type', 'text/html')
            self.end_headers()
            with open('/build/tasia_release/web-viewer/index.html', 'rb') as f:
                self.wfile.write(f.read())
        elif self.path == '/viewer.js':
            self.serve_static('/build/tasia_release/web-viewer/viewer.js', 'application/javascript')
        elif self.path == '/style.css':
            self.serve_static('/build/tasia_release/web-viewer/style.css', 'text/css')
        else:
            self.send_error(404)
    
    def serve_static(self, filepath, content_type):
        try:
            with open(filepath, 'rb') as f:
                self.send_response(200)
                self.send_header('Content-Type', content_type)
                self.end_headers()
                self.wfile.write(f.read())
        except:
            self.send_error(404)
    
    def do_POST(self):
        if self.path == '/api/login':
            self.handle_login()
        elif self.path == '/api/logout':
            self.handle_logout()
        else:
            self.send_error(404)
    
    def handle_login(self):
        """Handle login request"""
        length = int(self.headers.get('Content-Length', 0))
        data = json.loads(self.rfile.read(length).decode('utf-8'))
        
        first = data.get('first', '')
        last = data.get('last', '')
        password = data.get('password', '')
        start = data.get('start', 'last')
        
        # Generate client ID
        client_id = str(uuid.uuid4())
        
        # Create OpenSim client
        sim_client = OpenSimClient(client_id, None)
        result = sim_client.login(first, last, password, start)
        
        if result['success']:
            # Store client
            with client_lock:
                clients[client_id] = {
                    'sim_client': sim_client,
                    'data': result['data'],
                    'first': first,
                    'last': last
                }
            
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Access-Control-Allow-Origin', '*')
            self.end_headers()
            
            response = {
                'success': True,
                'client_id': client_id,
                'session_id': sim_client.session_id,
                'agent_id': sim_client.agent_id,
                'sim_ip': sim_client.sim_host,
                'sim_port': sim_client.sim_port,
                'circuit_code': sim_client.circuit_code,
                'data': result['data']
            }
            self.wfile.write(json.dumps(response).encode())
        else:
            self.send_response(401)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            response = {'success': False, 'error': result['error']}
            self.wfile.write(json.dumps(response).encode())
    
    def handle_logout(self):
        """Handle logout request"""
        length = int(self.headers.get('Content-Length', 0))
        data = json.loads(self.rfile.read(length).decode('utf-8'))
        
        client_id = data.get('client_id')
        
        with client_lock:
            if client_id in clients:
                clients[client_id]['sim_client'].close()
                del clients[client_id]
        
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(json.dumps({'success': True}).encode())


def start_server():
    """Start the HTTP/WebSocket server"""
    server = ThreadedHTTPServer((LISTEN_HOST, LISTEN_PORT), WebViewerHandler)
    print(f"TasiaNGC Web Viewer Server starting on http://{LISTEN_HOST}:{LISTEN_PORT}")
    print(f"OpenSim target: {OPENSIM_HOST}:{OPENSIM_PORT}")
    server.serve_forever()


if __name__ == '__main__':
    start_server()
