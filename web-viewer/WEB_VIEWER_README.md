# TasiaNGC Web Viewer

A web-based 3D viewer for OpenSim grids using Babylon.js.

## Features

- **Multi-user support** - Multiple users can connect simultaneously
- **Web-based** - Works in any modern browser
- **Babylon.js 3D rendering** - Full 3D scene with avatar representation
- **Chat system** - Local chat with other users
- **Minimap** - Simple region map view
- **Nearby users list** - See who's in the region

## Quick Start

### Option 1: PHP Server (Recommended)

1. Configure OpenSim connection in `index.php`:
   ```php
   $opensimHost = '127.0.0.1';  // Your OpenSim IP
   $opensimPort = '8002';        // Your OpenSim port
   ```

2. Start PHP server:
   ```bash
   cd /build/tasia_release/web-viewer
   php -S 0.0.0.0:8080 index.php
   ```

3. Open browser: `http://your-server:8080`

### Option 2: Python Server

1. Install dependencies:
   ```bash
   pip install websockets
   ```

2. Run server:
   ```bash
   python3 server_multi.py
   ```

3. Open browser: `http://your-server:8080`

## Configuration

### Environment Variables (for Docker)

| Variable | Default | Description |
|----------|---------|-------------|
| OPENSIM_HOST | 127.0.0.1 | OpenSim simulator IP |
| OPENSIM_PORT | 8002 | OpenSim simulator port |
| GRID_URL | http://127.0.0.1:8002 | Grid URL for login |

## API Endpoints

- `POST /api/login` - Login to OpenSim
- `POST /api/logout` - Logout and cleanup session
- `GET /api/region` - Get region information

## Login Request Format

```json
{
  "first": "FirstName",
  "last": "LastName",
  "password": "password",
  "start": "Region Name"
}
```

## Login Response

```json
{
  "success": true,
  "session_id": "...",
  "agent_id": "...",
  "sim_ip": "...",
  "sim_port": 8002,
  "circuit_code": 123456
}
```

## Current Limitations

1. **UDP Protocol** - The full Linden/OpenSim UDP protocol is complex. This viewer currently demonstrates:
   - Login to OpenSim via XML-RPC
   - Basic 3D scene rendering with Babylon.js
   - Chat UI (sending not yet implemented)
   
2. **Avatar Movement** - Basic avatar representation, full movement requires UDP packet handling

3. **No UDP Proxy** - Full multi-user sync requires WebSocket-to-UDP bridge

## Future Enhancements

- [ ] WebSocket UDP proxy for real-time multi-user
- [ ] Avatar animation and movement
- [ ] Inventory browser
- [ ] Teleport support
- [ ] WebRTC voice chat

## Files

- `index.php` - Main PHP server with API
- `index.html` - Babylon.js web interface
- `server_multi.py` - Python alternative server

## Docker Integration

Add to your docker-compose:

```yaml
viewer:
  image: tasia-ngc/opensim:beta
  ports:
    - "8080:80"
  volumes:
    - ./web-viewer:/var/www/html
  environment:
    - OPENSIM_HOST=host.docker.internal
    - OPENSIM_PORT=8002
```
