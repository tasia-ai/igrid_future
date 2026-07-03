# TasiaNGC Web Viewer - Implementation Plan

## Overview
Complete browser-based 3D viewer for OpenSim running inside OpenSim as a module (no external server).

## Excluded
- Voice chat

---

## Phase 1: Core Infrastructure (Priority: Critical)

### 1.1 Enhanced Client Connection
- [ ] Proper IClientAPI implementation for full packet handling
- [ ] Circuit code management
- [ ] Session handling
- [ ] Handle OpenSim's internal message bus properly

### 1.2 Object Rendering
- [ ] Parse SceneObjectGroup to extract prim data
- [ ] Support all prim types: box, sphere, cylinder, torus, etc.
- [ ] Apply textures and colors
- [ ] Handle scale and rotation
- [ ] Support linksets (multiple prims)

### 1.3 Avatar Rendering
- [ ] Load avatar appearance from OpenSim
- [ ] Support avatar textures (body, head, eyes)
- [ ] Handle avatar shape (height, proportions)
- [ ] Attachments support (items worn)

---

## Phase 2: Multi-User Features (Priority: High)

### 2.1 Real-time Avatar Sync
- [ ] Receive AgentUpdate packets from OpenSim
- [ ] Broadcast position/rotation to all clients
- [ ] Smooth interpolation between positions
- [ ] Handle avatar enter/leave region

### 2.2 Multi-Viewer Sync
- [ ] Sync chat across all viewers
- [ ] Sync inventory operations
- [ ] Handle IM (instant messages)

### 2.3 Presence Updates
- [ ] CoarseLocationUpdate for minimap
- [ ] Track which avatars are in region
- [ ] Show in users panel

---

## Phase 3: Interaction (Priority: High)

### 3.1 Object Interaction
- [ ] Click to select objects
- [ ] Sit on objects
- [ ] Touch objects (rezzing, scripts)

### 3.2 Building Tools (Basic)
- [ ] Create new prims (box, sphere)
- [ ] Move objects
- [ ] Scale objects
- [ ] Rotate objects

### 3.3 Inventory
- [ ] View inventory folder tree
- [ ] Wear items (attachments)
- [ ] Rezzing objects from inventory

---

## Phase 4: User Interface (Priority: Medium)

### 4.1 Enhanced Login
- [ ] Region selector dropdown
- [ ] Last login memory
- [ ] Remember password option

### 4.2 Better Minimap
- [ ] Show all avatars as dots
- [ ] Show objects/landmarks
- [ ] Click to teleport

### 4.3 Chat Improvements
- [ ] Channel selector
- [ ] IM window
- [ ] Chat history
- [ ] Emoji support

### 4.4 Toolbar
- [ ] Build tools
- [ ] World menu
- [ ] Search

---

## Phase 5: Performance & Polish (Priority: Medium)

### 5.1 Performance
- [ ] Object culling (only render visible)
- [ ] Level of detail (LOD)
- [ ] Texture streaming
- [ ] Packet batching

### 5.2 Stability
- [ ] Reconnection handling
- [ ] Timeout handling
- [ ] Error recovery

### 5.3 Polish
- [ ] Loading screens
- [ ] Progress indicators
- [ ] Better error messages

---

## Technical Implementation

### Message Protocol (JSON over WebSocket)
```
Client -> Server:
- login: {cmd, first, last, password, start}
- logout: {cmd}
- chat: {cmd, message, channel}
- agent_update: {cmd, position, rotation, velocity}
- request_objects: {cmd}
- request_agents: {cmd}
- teleport: {cmd, region, position}

Server -> Client:
- login_success: {type, agent_id, session_id, region_*}
- error: {type, message}
- chat: {type, from, message, channel}
- object_update: {type, objects[]}
- agent_update: {type, agent_id, position, rotation}
- logout_success: {type}
- coarse_location: {type, locations[], avatar_ids[]}
```

### OpenSim Module Architecture
```
WebViewerModule (ISharedRegionModule)
├── WebSocket Server (Fleck)
│   └── BrowserClient[]
│       ├── Scene reference
│       ├── Agent info
│       └── Message handlers
├── Event subscriptions
│   ├── OnChatFromClient
│   ├── OnChatFromWorld
│   ├── OnNewClient
│   ├── OnClientClosed
│   └── Entity updates
└── Scene management
    └── GetAllScenes()
```

---

## Files to Modify/Create

1. **WebViewerModule.cs** - Main module (existing, needs enhancement)
2. **ViewerProtocol.cs** - Message definitions
3. **ObjectSerializer.cs** - Prim data extraction
4. **AvatarManager.cs** - Avatar appearance handling

---

## Testing Plan

1. Single user login
2. Multi-user sync
3. Object creation
4. Inventory operations
5. Teleport between regions
6. Performance with 50+ objects
7. Performance with 10+ users

---

## Time Estimate
- Phase 1: 2-3 days
- Phase 2: 2-3 days
- Phase 3: 3-4 days
- Phase 4: 2-3 days
- Phase 5: 2-3 days

**Total: ~12-16 days**
