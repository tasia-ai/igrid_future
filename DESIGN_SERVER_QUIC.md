# I-Grid Server QUIC Transport — Design Document

## 1. Overview

This document describes the server-side implementation of QUIC transport support for Tasia Viewer connections. The implementation adds a new QUIC-based transport alongside the existing LLUDP stack, enabling simulator packet traffic to flow over QUIC connections.

## 2. Architecture

### 2.1 High-Level Design

```
Tasia Viewer                    OpenSim Region Server
─────────────                   ─────────────────────
                                ┌─────────────────────────┐
                                │  LLUDPServer (UDP:9000) │ ← Legacy LLUDP path
                                │  LegacyUdpViewerTransport│
                                └─────────────────────────┘
                                         │
                                ┌─────────────────────────┐
QUIC ─────────────────────────→│  QuicServerModule        │ ← New QUIC path
  ALPN: "opensim-ll/1"         │  (QUIC listener :9001)   │
  Stream: 4-byte len + payload │  QuicClientConnection    │
  Datagram: optional           │  QuicViewerTransport     │
                                └─────────────────────────┘
                                         │
                                         ▼
                                ┌─────────────────────────┐
                                │  LLClientView            │ ← Same packet dispatch
                                │  (IClientAPI impl)       │    for both paths
                                └─────────────────────────┘
                                         │
                                         ▼
                                ┌─────────────────────────┐
                                │  Scene / Region Modules  │ ← Existing game logic
                                └─────────────────────────┘
```

### 2.2 Transport Abstraction

A new interface `IViewerTransport` sits between the packet dispatch layer and the actual network transport:

```
interface IViewerTransport
{
    void SendPacket(byte[] payload, bool reliable);
    event Action<byte[], IViewerTransport> PacketReceived;
    void Close(string reason);
    string TransportName { get; }
    bool IsConnected { get; }
    IPEndPoint RemoteEndPoint { get; }
}
```

Two implementations:
- **`LegacyUdpViewerTransport`** — wraps the existing LLUDP client/send path
- **`QuicViewerTransport`** — sends/receives over QUIC stream + datagrams

## 3. Protocol Specification

### 3.1 Connection Establishment

1. Viewer opens QUIC connection to server port
2. TLS 1.3 handshake with ALPN `"opensim-ll/1"`
3. Server accepts, creates `QuicClientConnection`
4. Viewer opens a bidirectional stream
5. Server reads 4-byte big-endian length prefix, then payload
6. First payload is a `UseCircuitCode` packet (same LLUDP format)
7. Server processes UseCircuitCode normally, binds agent session
8. Server sends `GenericMessage("quicready")` to signal ready state

### 3.2 Stream Frame Format

All packets over QUIC streams use a simple length-prefixed framing:

```
[4 bytes: payload length (big-endian uint32)]
[N bytes: LL packet data (same format as LLUDP payload)]
```

The LL packet data is identical to what would be sent over LLUDP — no additional headers, no FF/FE bytes. Zero-encoding is NOT applied (the raw post-encode LL packet bytes).

### 3.3 Datagram Support (Optional)

When the connection negotiates QUIC datagram support (server sends `max_datagram_frame_size > 0`), certain unreliable packets can be sent as QUIC datagrams instead of stream data. No additional framing beyond the raw LL packet bytes.

### 3.4 "quicready" Signal

After `UseCircuitCode` is processed and the agent session is established, the server sends a `GenericMessage` with method `"quicready"` to the client. This signals that the QUIC transport is fully connected and normal traffic can flow.

### 3.5 Packet Flow

**Viewer → Server (over QUIC):**
1. Frame received on stream (or datagram)
2. 4-byte length prefix read
3. LL packet bytes extracted
4. Same packet dispatch as LLUDP path

**Server → Viewer (over QUIC):**
1. `LLClientView.Send*()` called (same as normal)
2. Packet serialized to bytes
3. 4-byte length prefix prepended
4. Sent over QUIC stream (reliable) or datagram (unreliable)

## 4. Implementation Details

### 4.1 File Map

```
OpenSim/Framework/
  IViewerTransport.cs                  ← Transport interface

OpenSim/Region/ClientStack/Linden/Quic/
  QuicServerModule.cs                  ← INonSharedRegionModule entry point
  QuicListener.cs                      ← QuicListener wrapper
  QuicClientConnection.cs              ← Per-connection QUIC state
  QuicViewerTransport.cs               ← IViewerTransport impl over QUIC
  QuicServerConfig.cs                  ← Config model
  PacketFraming.cs                     ← 4-byte length prefix helper
  OpenSim.Region.ClientStack.Linden.Quic.csproj  ← Project file

OpenSim/Region/ClientStack/Linden/UDP/
  LLUDPServer.cs                       ← Modified: holds IViewerTransport refs
  LegacyUdpViewerTransport.cs          ← IViewerTransport over LLUDP
```

### 4.2 QuicServerModule

```csharp
[Extension(Path = "/OpenSim/RegionModules",
           NodeName = "RegionModule",
           Id = "QuicServerModule")]
public class QuicServerModule : INonSharedRegionModule
{
    private QuicListener m_listener;
    private Scene m_scene;
    private ConcurrentDictionary<string, QuicClientConnection> m_clients;
    
    // INonSharedRegionModule implementation
    public void Initialise(IConfigSource configSource);
    public void AddRegion(Scene scene);
    public void RegionLoaded(Scene scene);
    public void RemoveRegion(Scene scene);
    public void Close();
}
```

### 4.3 QuicListener

Uses `System.Net.Quic.QuicListener` (.NET 8 built-in):

```csharp
public class QuicListener
{
    private System.Net.Quic.QuicListener m_listener;
    
    public async Task StartAsync(IPEndPoint endpoint, X509Certificate2 certificate);
    public async Task AcceptConnectionsAsync();
    // For each accepted connection, creates QuicClientConnection
}
```

### 4.4 QuicClientConnection

```csharp
public class QuicClientConnection
{
    private System.Net.Quic.QuicConnection m_connection;
    private QuicStream m_controlStream;
    private BlockingCollection<byte[]> m_rxQueue;
    private CancellationTokenSource m_cts;
    
    public event Action<byte[]> OnPacketReceived;
    
    public void Start();       // Begin read loop
    public void Send(byte[] data, bool reliable);
    public void Close(string reason);
}
```

### 4.5 Packet Routing Integration

The critical integration point is in `LLUDPServer.PacketReceived()` and the client view. For QUIC, instead of receiving from the UDP socket, packets arrive through `IViewerTransport.PacketReceived` event.

**Modified flow:**

```
LLUDPServer.PacketReceived(UDP buffer)
    └── Existing path (LLUDP only)

QuicViewerTransport.PacketReceived += (bytes, transport) =>
    └── New path: create IncomingPacket from bytes
        └── Enqueue to same packetInbox
            └── Same dispatch
```

The `QuicViewerTransport` is bound to an `LLClientView` instance. When `LLClientView` sends packets, it checks its associated transport and routes accordingly.

### 4.6 LLClientView Transport Binding

```csharp
// In LLClientView, add property:
public IViewerTransport Transport { get; set; }

// In all Send* methods, check transport:
if (Transport != null && Transport is QuicViewerTransport)
{
    // Serialize to bytes, send via Transport.SendPacket()
}
else
{
    // Existing LLUDP send path
}
```

## 5. Configuration

### 5.1 New Config Section

In `OpenSimDefaults.ini` (or region config):

```ini
[ClientStack.Quic]
Enabled = false
Port = 9001
CertificatePath = ""
PrivateKeyPath = ""
ALPN = "opensim-ll/1"
IdleTimeoutMs = 60000
KeepaliveMs = 30000
MaxBidirectionalStreams = 1024
MaxDatagramSize = 1200
RequireTasiaViewer = false
AllowLegacyLLUDP = true
LogPackets = false
LogHandshake = true
```

### 5.2 GridInfo / Login Response Advertisement

When QUIC is enabled and a QUIC-ready viewer connects, the following LLSD fields are included in appropriate responses:

```lsl
sim_quic_host = "region.example.com"
sim_quic_port = 9001
```

These match what the viewer expects in:
- Login response (`SimulatorInfo[0].sim_quic_host` / `.sim_quic_port`)
- EnableSimulator (`SimulatorInfo[0].QuicHost` / `.QuicPort`)
- TeleportFinish (`Info[0].SimQuicHost` / `.SimQuicPort`)
- CrossedRegion (`RegionData[0].SimQuicHost` / `.SimQuicPort`)

## 6. Fallback Behavior

### 6.1 AllowLegacyLLUDP = true (default)
- Tasia Viewer may use QUIC or LLUDP
- Old viewers connect via LLUDP as before
- Log shows `user=<name> transport=QUIC` or `transport=LLUDP`

### 6.2 RequireTasiaViewer = true
- Only QUIC-capable viewers can connect
- Legacy LLUDP connection attempts are rejected with a clear disconnect message:
  "This region requires Tasia Viewer with QUIC support."
- The rejection happens at the UseCircuitCode stage

### 6.3 QUIC Connection Failure
- If QUIC connection drops, viewer session is terminated
- No automatic fallback to LLUDP (matches viewer behavior: "per spec NOT falling back to LLUDP")

## 7. Security Considerations

- **TLS 1.3**: All QUIC connections use TLS 1.3 with proper certificate validation
- **No client certificate required**: Viewer uses `QUIC_CREDENTIAL_TYPE_NONE`
- **Session binding**: QUIC connection is bound to agent session via UseCircuitCode
- **No token replay concern**: Authentication happens through existing UseCircuitCode mechanism
- **Rate limiting**: QUIC has built-in congestion control; additional rate limiting on handshake failures
- **Logging**: Rejection reasons logged, no secrets logged

## 8. Implementation Phases

### Phase 1 (Current)
- QUIC listener accepting connections
- Stream-based packet transport (4-byte length prefix + LL packets)
- UseCircuitCode processing over QUIC
- "quicready" signal
- Dual transport support (LLUDP + QUIC concurrently)
- Console logging and stats

### Phase 2 (Future)
- Datagram support for unreliable packets
- Token-based pre-authentication
- Additional admin commands
- Performance optimization

## 9. Testing

1. QUIC disabled → legacy behavior unchanged
2. QUIC enabled, AllowLegacyLLUDP=true → old viewers still work
3. Tasia Viewer connects via QUIC → logs show transport=QUIC
4. Movement, chat, object updates, teleport work over QUIC
5. RequireTasiaViewer=true → old viewer rejected with message
6. ROBUST services (login, inventory, assets) still work
7. QUIC disconnect → viewer session cleans up properly

## 10. References

- Viewer QUIC implementation: `indra/llquic/`
- Viewer ALPN: `"opensim-ll/1"`
- Viewer stream framing: 4-byte big-endian length prefix
- Viewer `"quicready"` signal: `GenericMessage("quicready")`
- .NET System.Net.Quic: https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic
