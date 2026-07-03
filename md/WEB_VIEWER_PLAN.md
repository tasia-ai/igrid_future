# Web Viewer Implementation Plan - REVISED
## TasiaNGC Web Viewer

**Status: Starting from existing components**

---

## What's Already Implemented

### ✅ Server-Side (Already Done)
| Component | Implementation | Location |
|-----------|---------------|----------|
| User Login | w4os plugin | `/var/www/html/web/wp-content/plugins/w4os/` |
| Avatar Profiles | w4os | Profiles, grid info, registration |
| HG Auth | tasia-hg-auth-pro | Ban, maintenance, traffic log |
| Password Reset | tasia-password-reset | SMTP from WordPress |
| Docker Management | tasia-os-docker-mgr | Region containers |
| GitHub Integration | tasia-github-manager | Gist + password sharing |

### ✅ Client-Side UI (Andromeda)
| Feature | Status |
|---------|--------|
| Login screen | ✅ Ready |
| Chat | ✅ UI ready |
| Friends list | ✅ UI ready |
| Groups | ✅ UI ready |
| Inventory browser | ✅ UI ready |
| Profile viewing | ✅ UI ready |
| Map | ✅ UI ready |
| **3D Rendering** | ❌ Missing |

### ✅ 3D Model Display
| Component | Status |
|-----------|--------|
| Babylon.js viewer | ✅ Working |
| GLB/PBR support | ✅ Ready |
| Shortcode | `[3d_viewer model="url.glb"]` |

---

## What's Missing

### ❌ 3D World Viewer
1. **Proxy Server** - Connect browser to OpenSim (UDP→WebSocket)
2. **Babylon.js Integration** - Render 3D world
3. **Avatar Rendering** - See other users
4. **Own Avatar** - Your avatar in world

---

## Revised Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        WORDPRESS                                 │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐   │
│  │    w4os    │  │  Andromeda  │  │  tasia-3d-viewer   │   │
│  │ (profiles) │  │   (UI)      │  │  (Babylon.js)       │   │
│  └─────────────┘  └─────────────┘  └─────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                    PROXY SERVER (NEW)                           │
│  ┌──────────────────┐  ┌──────────────────────────────────┐    │
│  │  WebSocket       │  │  UDP Gateway                    │    │
│  │  (Browser)       │◄─►│  (OpenSim Simulator)           │    │
│  └──────────────────┘  └──────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                      OPENSIMULATOR                              │
│  ┌─────────────┐  ┌──────────────┐  ┌────────────────┐      │
│  │  Robust     │  │  Simulator   │  │  Database      │      │
│  │  (Login)   │  │  (Regions)   │  │  (MySQL)       │      │
│  └─────────────┘  └──────────────┘  └────────────────┘      │
└─────────────────────────────────────────────────────────────────┘
```

---

## What We Need to Build

### Phase 1: Proxy Server (Week 1-2)
**New code needed**

```
proxy-server/
├── Program.cs           # Entry point
├── WebSocketServer.cs  # Browser connection
├── UdpGateway.cs       # OpenSim connection
├── MessageRouter.cs    # Route WS↔UDP
├── LoginHandler.cs     # Handle login
└── SessionManager.cs   # Track sessions
```

**Responsibilities:**
1. Accept WebSocket connections from browser
2. Connect to OpenSim simulators via UDP
3. Forward messages between them
4. Handle login authentication

### Phase 2: Babylon.js Integration (Week 3-4)
**Extend existing `tasia-3d-viewer`**

```typescript
// What to add to existing viewer
src/
├── network/
│   ├── WebSocketClient.ts   # Connect to proxy
│   ├── PacketDecoder.ts     # Decode OpenSim packets
│   └── ObjectParser.ts      # Parse ObjectUpdate
├── engine/
│   ├── WorldRenderer.ts     # Render world from packets
│   ├── PrimFactory.ts       # Create prims
│   └── AvatarRenderer.ts    # Render avatars
└── integration/
    └── ConnectAndromeda.ts  # Link with Andromeda UI
```

### Phase 3: Basic 3D (Week 5-6)
**MVP Features:**
- [ ] Connect to proxy server
- [ ] Receive ObjectUpdate packets
- [ ] Render simple primitives (box, sphere, cylinder)
- [ ] Camera movement
- [ ] See other avatars (simple shapes)

### Phase 4: Avatars (Week 7-8)
- [ ] Load avatar appearance
- [ ] Render avatar meshes
- [ ] Send position updates
- [ ] See other users move

### Phase 5: Polish (Week 9-12)
- [ ] Chat integration (link to Andromeda)
- [ ] Inventory (link to Andromeda)
- [ ] Mesh support
- [ ] PBR materials

---

## Existing Code to Leverage

### 1. w4os Integration
```php
// Use w4os for login
$grid_url = get_option('w4os_grid_uri');
$login_url = $grid_url . '/?q=login';
```

### 2. Andromeda UI (already in repo)
- Chat panel: `/build/tasia_release/web/andromeda-viewer/src/components/Chat.tsx`
- Inventory: `/build/tasia_release/web/andromeda-viewer/src/components/Inventory.tsx`
- Friends: `/build/tasia_release/web/andromeda-viewer/src/components/Friends.tsx`

### 3. Babylon.js Viewer (already done)
- Location: `/build/tasia_release/web/wordpress-plugins/tasia-3d-viewer/`
- Already handles GLB/PBR models

---

## Implementation Priority

### Now (Immediate)
1. **Build proxy server** - This is NEW code needed
2. **Connect Babylon.js to proxy** - NEW

### Later
3. Link Andromeda chat to proxy
4. Avatar rendering
5. Full feature parity

---

## Next Steps

1. **Start proxy server development**
2. **Test login flow**
3. **Add WebSocket client to Babylon.js**
4. **Render first primitive**

---

## Quick Start

```bash
# 1. Create proxy server
mkdir -p /build/tasia_release/proxy-server
cd /build/tasia_release/proxy-server
dotnet new console

# 2. Add dependencies
dotnet add package Fleck
dotnet add package OpenMetaverse

# 3. Implement WebSocket + UDP bridge
# (See Phase 1 in original plan)
```

---

## Questions Before Proceeding

1. Where should proxy server run? (same machine? separate?)
2. Which port for WebSocket? (8080?)
3. Start with basic primitives or avatars first?

---

*Document Version: 2.0*
*Last Updated: 2026-02-25*
