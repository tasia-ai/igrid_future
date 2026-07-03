#!/usr/bin/env python3
"""
Tasia Grid Info Provider
Provides grid information for viewers (like SLinfo)
Configured via WordPress
"""

import os
import json
import mysql.connector
from http.server import HTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlparse, parse_qs
import xml.etree.ElementTree as ET

# Configuration from environment or WordPress
WORDPRESS_PATH = os.environ.get('WORDPRESS_PATH', '/var/www/html/web')
CONFIG_FILE = os.path.join(WORDPRESS_PATH, 'wp-config.php')

def get_wp_config():
    """Extract configuration from WordPress wp-config.php"""
    config = {
        'db_host': 'localhost',
        'db_name': 'robust',
        'db_user': 'root',
        'db_pass': '',
    }
    
    try:
        with open(CONFIG_FILE, 'r') as f:
            content = f.read()
            
        import re
        match = re.search(r"define\s*\(\s*['\"]DB_HOST['\"]\s*,\s*['\"]([^'\"]+)['\"]\s*\)", content)
        if match: config['db_host'] = match.group(1)
        
        match = re.search(r"define\s*\(\s*['\"]DB_NAME['\"]\s*,\s*['\"]([^'\"]+)['\"]\s*\)", content)
        if match: config['db_name'] = match.group(1)
        
        match = re.search(r"define\s*\(\s*['\"]DB_USER['\"]\s*,\s*['\"]([^'\"]+)['\"]\s*\)", content)
        if match: config['db_user'] = match.group(1)
        
        match = re.search(r"define\s*\(\s*['\"]DB_PASSWORD['\"]\s*,\s*['\"]([^'\"]+)['\"]\s*\)", content)
        if match: config['db_pass'] = match.group(1)
    except Exception as e:
        print(f"Warning: Could not read WordPress config: {e}")
    
    return config

def get_grid_info_from_db(db_config):
    """Get grid info from database"""
    try:
        conn = mysql.connector.connect(
            host=db_config['db_host'],
            user=db_config['db_user'],
            password=db_config['db_pass'],
            database=db_config['db_name']
        )
        cursor = conn.cursor(dictionary=True)
        
        # Get regions
        cursor.execute("SELECT regionName, regionHandle, uri, serverIP, serverPort, locX, locY FROM regions WHERE offline = 0 LIMIT 100")
        regions = cursor.fetchall()
        
        # Get user count
        cursor.execute("SELECT COUNT(*) as count FROM UserAccounts")
        users = cursor.fetchone()
        
        conn.close()
        
        return {
            'regions': regions,
            'user_count': users['count'] if users else 0
        }
    except Exception as e:
        print(f"Database error: {e}")
        return {'regions': [], 'user_count': 0}

class GridInfoHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        parsed = urlparse(self.path)
        path = parsed.path.rstrip('/')
        
        if path == '' or path == '/grid-info' or path == '/get_grid_info':
            self.send_grid_info_xml()
        elif path == '/grid-info-json':
            self.send_grid_info_json()
        elif path == '/region-info':
            self.send_region_info()
        else:
            self.send_error(404)
    
    def send_grid_info_xml(self):
        """Send grid info in LLSD/XML format for viewers"""
        db_config = get_wp_config()
        grid_data = get_grid_info_from_db(db_config)
        
        # Get grid settings from WordPress options
        grid_name = os.environ.get('GRID_NAME', 'Tasia Grid')
        grid_url = os.environ.get('GRID_URL', 'https://i.let-us.cyou')
        login_url = f"{grid_url}/"
        helper_url = grid_url
        
        xml = f'''<?xml version="1.0" encoding="UTF-8"?>
<GridInfo>
  <GridName>{grid_name}</GridName>
  <GridLoginURL>{login_url}</GridLoginURL>
  <GridHelperURL>{helper_url}</GridHelperURL>
  <GridURI>{grid_url}:8002</GridURI>
  <WelcomeMessage>Welcome to {grid_name}!</WelcomeMessage>
  <MaxRegions>256</MaxRegions>
  <MaxUsers>{grid_data['user_count']}</MaxUsers>
  <CurrentUsers>{grid_data['user_count']}</CurrentUsers>
  <RegistrationURI></RegistrationURI>
  <PasswordURI></PasswordURI>
</GridInfo>'''
        
        self.send_response(200)
        self.send_header('Content-Type', 'application/xml')
        self.end_headers()
        self.wfile.write(xml.encode())
    
    def send_grid_info_json(self):
        """Send grid info in JSON format"""
        db_config = get_wp_config()
        grid_data = get_grid_info_from_db(db_config)
        
        grid_name = os.environ.get('GRID_NAME', 'Tasia Grid')
        grid_url = os.environ.get('GRID_URL', 'https://i.let-us.cyou')
        
        info = {
            'grid_name': grid_name,
            'grid_url': grid_url,
            'login_url': f"{grid_url}/",
            'user_count': grid_data['user_count'],
            'region_count': len(grid_data['regions']),
            'regions': [
                {
                    'name': r['regionName'],
                    'uri': r['uri'],
                    'x': r['locX'],
                    'y': r['locY']
                }
                for r in grid_data['regions']
            ]
        }
        
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(json.dumps(info).encode())
    
    def send_region_info(self):
        """Send region info for a specific region"""
        params = parse_qs(urlparse(self.path).query)
        region_name = params.get('name', [''])[0]
        
        if not region_name:
            self.send_error(400)
            return
        
        db_config = get_wp_config()
        
        try:
            conn = mysql.connector.connect(
                host=db_config['db_host'],
                user=db_config['db_user'],
                password=db_config['db_pass'],
                database=db_config['db_name']
            )
            cursor = conn.cursor(dictionary=True)
            cursor.execute("SELECT * FROM regions WHERE regionName = %s", (region_name,))
            region = cursor.fetchone()
            conn.close()
            
            if region:
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.end_headers()
                self.wfile.write(json.dumps(region).encode())
            else:
                self.send_error(404)
        except Exception as e:
            self.send_error(500)
    
    def log_message(self, format, *args):
        print(f"[GridInfo] {format % args}")

def run_server(port=8003):
    server_address = ('', port)
    httpd = HTTPServer(server_address, GridInfoHandler)
    print(f"Tasia Grid Info Server running on port {port}")
    httpd.serve_forever()

if __name__ == '__main__':
    run_server()
