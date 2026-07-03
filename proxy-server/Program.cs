using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Fleck;

class Program
{
    static Dictionary<string, BrowserSession> sessions = new();
    static int wsPort = 9002;

    static void Main(string[] args)
    {
        Console.WriteLine("TasiaNGC Web Viewer Proxy Server v3");
        Console.WriteLine("==================================");

        var server = new WebSocketServer($"ws://0.0.0.0:{wsPort}");

        server.Start(socket =>
        {
            var connId = Guid.NewGuid().ToString();
            Console.WriteLine($"Browser connected: {connId}");

            var session = new BrowserSession(socket, connId);
            sessions[connId] = session;

            socket.OnMessage = message => session.HandleMessage(message);

            socket.OnClose = () =>
            {
                Console.WriteLine($"Browser disconnected: {connId}");
                session.Disconnect();
                sessions.Remove(connId);
            };
        });

        Console.WriteLine($"Proxy running on ws://0.0.0.0:{wsPort}");
        Console.WriteLine("Press Ctrl+C to stop");

        Thread.Sleep(Timeout.Infinite);
    }
}

class BrowserSession
{
    private readonly IWebSocketConnection _conn;
    private readonly string _connId;
    private UdpClient? _udp;
    private IPEndPoint? _simEP;
    private Thread? _listener;
    private bool _active = true;
    private uint _circuitCode = 0;
    private uint _sequence = 1;

    public string SessionId { get; private set; } = "";
    public string AgentId { get; private set; } = "";
    public ulong RegionHandle { get; private set; } = 0;

    public BrowserSession(IWebSocketConnection conn, string connId)
    {
        _conn = conn;
        _connId = connId;
    }

    public void HandleMessage(string msg)
    {
        try
        {
            var pkt = JsonSerializer.Deserialize<BrowserPacket>(msg);
            if (pkt == null) return;

            switch (pkt.type)
            {
                case "login":
                    HandleLogin(pkt);
                    break;
                case "agent_update":
                    SendAgentUpdate(pkt);
                    break;
                case "chat":
                    SendChat(pkt);
                    break;
                case "complete_agent_movement":
                    SendCompleteAgentMovement();
                    break;
                case "request_objects":
                    // Request new object data
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    private void HandleLogin(BrowserPacket pkt)
    {
        Console.WriteLine($"Login: {pkt.first} {pkt.last}");

        try
        {
            _simEP = new IPEndPoint(IPAddress.Parse(pkt.sim_ip!), pkt.sim_port);
            _udp = new UdpClient();
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            _listener = new Thread(ListenToSim);
            _listener.Start();

            _active = true;
            SessionId = pkt.session_id ?? "";
            AgentId = pkt.agent_id ?? "";
            _circuitCode = (uint)new Random().Next(100000, 999999);
            RegionHandle = pkt.region_handle;

            // Send UseCircuitCode
            SendUseCircuitCode();

            // Send CompleteAgentMovement after short delay
            new Thread(() => {
                Thread.Sleep(200);
                SendCompleteAgentMovement();
            }).Start();

            Send(new { type = "login_response", success = true, circuit_code = _circuitCode, message = "Connected" });
            Console.WriteLine($"Logged in! Circuit: {_circuitCode}");
        }
        catch (Exception ex)
        {
            Send(new { type = "login_response", success = false, message = ex.Message });
            Console.WriteLine($"Login failed: {ex.Message}");
        }
    }

    private void SendUseCircuitCode()
    {
        if (_udp == null || _simEP == null) return;

        // Build UseCircuitCode message
        var data = BuildPacket(0xFFFF, new byte[] { });
        _udp.Send(data, data.Length, _simEP);
        Console.WriteLine("Sent UseCircuitCode");
    }

    private void SendCompleteAgentMovement()
    {
        if (_udp == null || _simEP == null) return;

        // CompleteAgentMovement message
        // This tells simulator we're ready to receive world data
        var data = BuildPacket(0xFFFF, new byte[] { });
        _udp.Send(data, data.Length, _simEP);
        Console.WriteLine("Sent CompleteAgentMovement");
    }

    private void SendAgentUpdate(BrowserPacket pkt)
    {
        if (_udp == null || _simEP == null) return;

        try
        {
            // AgentUpdate - sends camera position
            // Message ID 0xFFFF (simplified)
            var data = BuildPacket(0xFFFF, new byte[] { });
            _udp.Send(data, data.Length, _simEP);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Agent update error: {ex.Message}");
        }
    }

    private void SendChat(BrowserPacket pkt)
    {
        if (_udp == null || _simEP == null || pkt.data == null) return;

        try
        {
            var chatData = JsonSerializer.Serialize(pkt.data);
            var msgBytes = Encoding.UTF8.GetBytes(chatData);
            var data = BuildPacket(0xFFFF, msgBytes);
            _udp.Send(data, data.Length, _simEP);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Chat error: {ex.Message}");
        }
    }

    private byte[] BuildPacket(ushort messageId, byte[] data)
    {
        using var ms = new System.IO.MemoryStream();
        var bw = new System.IO.BinaryWriter(ms);

        // Packet header
        bw.Write((byte)_sequence++); // sequence
        bw.Write((byte)0); // flags
        bw.Write(_circuitCode); // circuit code
        
        // Session ID
        var sessionBytes = UUID.FromString(SessionId).GetBytes();
        bw.Write(sessionBytes);
        
        // Agent ID
        var agentBytes = UUID.FromString(AgentId).GetBytes();
        bw.Write(agentBytes);
        
        // Message data
        if (data.Length > 0)
        {
            bw.Write(data);
        }

        return ms.ToArray();
    }

    private void ListenToSim()
    {
        while (_active && _udp != null)
        {
            try
            {
                if (_udp.Available > 0)
                {
                    var data = _udp.Receive(ref _simEP!);
                    ProcessSimulatorData(data);
                }
            }
            catch { }
            Thread.Sleep(10);
        }
    }

    private void ProcessSimulatorData(byte[] data)
    {
        if (data.Length < 4) return;

        try
        {
            // Parse OpenSim packet - simplified
            // Real implementation needs proper message template parsing
            
            // Check for common message types
            // ObjectUpdate: 0x40
            // AvatarUpdate: 0x42
            // ImprovedInstantMessage: 0xF
            
            // For now, try to extract position data (floats)
            var objects = new List<ObjectData>();
            
            // Simple heuristic: look for object data in packet
            // This is very simplified - real parsing needs message template
            for (int i = 20; i < data.Length - 50; i += 4)
            {
                try
                {
                    float x = BitConverter.ToSingle(data, i);
                    float y = BitConverter.ToSingle(data, i + 4);
                    float z = BitConverter.ToSingle(data, i + 8);
                    
                    // Check if valid world position
                    if (x > -256 && x < 512 && y > -256 && y < 512 && z > -10 && z < 500)
                    {
                        // Found a valid position - create a simple object
                        var obj = new ObjectData
                        {
                            LocalID = (uint)(i * 17 + 1000), // fake ID for now
                            Position = new float[] { x, y, z },
                            Scale = new float[] { 1, 1, 1 },
                            PrimType = 0 // box
                        };
                        
                        // Check if this position already exists
                        bool exists = false;
                        foreach (var o in objects)
                        {
                            if (Math.Abs(o.Position[0] - x) < 0.1 && Math.Abs(o.Position[1] - y) < 0.1)
                            {
                                exists = true;
                                break;
                            }
                        }
                        
                        if (!exists)
                        {
                            objects.Add(obj);
                        }
                    }
                }
                catch { }
            }
            
            if (objects.Count > 0)
            {
                Send(new { 
                    type = "objects", 
                    objects = objects.ToArray() 
                });
                
                Console.WriteLine($"Found {objects.Count} objects");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Parse error: {ex.Message}");
        }
    }

    private void Send(object data)
    {
        try
        {
            _conn.Send(JsonSerializer.Serialize(data));
        }
        catch { }
    }

    public void Disconnect()
    {
        _active = false;
        try { _listener?.Interrupt(); } catch { }
        _udp?.Close();
    }
}

class BrowserPacket
{
    public string type { get; set; } = "";
    public string? first { get; set; }
    public string? last { get; set; }
    public string? sim_ip { get; set; }
    public int sim_port { get; set; }
    public string? session_id { get; set; }
    public string? agent_id { get; set; }
    public ulong region_handle { get; set; }
    public object? data { get; set; }
}

class ObjectData
{
    public uint LocalID { get; set; }
    public float[] Position { get; set; } = new float[3];
    public float[] Scale { get; set; } = new float[3];
    public int PrimType { get; set; }
    public float[] Color { get; set; } = new float[3];
}

class UUID
{
    private byte[] _bytes = new byte[16];
    
    public static UUID FromString(string s)
    {
        var uuid = new UUID();
        s = s.Replace("-", "").Replace("{", "").Replace("}", "");
        if (s.Length == 32)
        {
            for (int i = 0; i < 16; i++)
            {
                uuid._bytes[i] = Convert.ToByte(s.Substring(i*2, 2), 16);
            }
        }
        return uuid;
    }
    
    public byte[] GetBytes() => _bytes;
}
