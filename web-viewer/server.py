#!/usr/bin/env python3
import http.server
import socketserver
import urllib.request

PORT = 8080

class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, format, *args):
        print(f"{self.address_string()} - {format % args}")
    
    def do_POST(self):
        if self.path == '/login':
            length = int(self.headers.get('Content-Length', 0))
            data = self.rfile.read(length)
            
            req = urllib.request.Request(
                'http://127.0.0.1:8002/',
                data=data,
                headers={'Content-Type': 'application/xml'}
            )
            
            try:
                resp = urllib.request.urlopen(req)
                result = resp.read()
                self.send_response(200)
                self.send_header('Content-Type', 'application/xml')
                self.end_headers()
                self.wfile.write(result)
            except Exception as e:
                self.send_response(500)
                self.end_headers()
                self.wfile.write(str(e).encode())
        else:
            self.send_response(404)
            self.end_headers()
    
    def do_GET(self):
        super().do_GET()

print(f"Starting on port {PORT}")
socketserver.TCPServer(("", PORT), Handler).serve_forever()
