using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Fleck;

namespace TasiaAddons.WebViewer
{
    class Program
    {
        private static int VIEWER_PORT = 9002;
        private static int SIM_PORT = 9003;
        
        public static readonly ConcurrentDictionary<string, ViewerClient> viewers = new();
        public static readonly ConcurrentDictionary<string, SimServer> simulators = new();
        public static readonly ConcurrentDictionary<string, RegionInfo> regions = new();
        
        private static Timer? updateTimer;
        
        static void Main(string[] args)
        {
            LoadConfig();
            
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--viewer-port" && i + 1 < args.Length)
                    VIEWER_PORT = int.Parse(args[i + 1]);
                else if (args[i] == "--sim-port" && i + 1 < args.Length)
                    SIM_PORT = int.Parse(args[i + 1]);
                else if (args[i] == "--port" && i + 1 < args.Length)
                    VIEWER_PORT = int.Parse(args[i + 1]);
            }
            
            Console.WriteLine("===========================================");
            Console.WriteLine("   TasiaNGC Web Viewer Hub");
            Console.WriteLine("===========================================");
            Console.WriteLine($"Viewer port: {VIEWER_PORT}");
            Console.WriteLine($"Simulator port: {SIM_PORT}");
            Console.WriteLine();
            
            var simServer = new WebSocketServer($"ws://0.0.0.0:{SIM_PORT}");
            simServer.Start(socket =>
            {
                var connId = Guid.NewGuid().ToString();
                Console.WriteLine($"[HUB] Simulator connected: {connId}");
                
                var sim = new SimServer(socket, connId);
                simulators[connId] = sim;
                
                socket.OnMessage = msg => sim.HandleMessage(msg);
                socket.OnClose = () =>
                {
                    Console.WriteLine($"[HUB] Simulator disconnected: {connId}");
                    RemoveSimulator(connId);
                };
            });
            
            var viewerServer = new WebSocketServer($"ws://0.0.0.0:{VIEWER_PORT}");
            viewerServer.Start(socket =>
            {
                var connId = Guid.NewGuid().ToString();
                Console.WriteLine($"[HUB] Viewer connected: {connId}");
                
                var viewer = new ViewerClient(socket, connId);
                viewers[connId] = viewer;
                
                socket.OnMessage = msg => viewer.HandleMessage(msg);
                socket.OnClose = () =>
                {
                    Console.WriteLine($"[HUB] Viewer disconnected: {connId}");
                    viewers.TryRemove(connId, out _);
                };
            });
            
            updateTimer = new Timer(BroadcastUpdates, null, 1000, 1000);
            
            Console.WriteLine("[HUB] Running... Press Ctrl+C to stop");
            Thread.Sleep(Timeout.Infinite);
        }
        
        static void LoadConfig()
        {
            string[] iniFiles = { "WebViewerHub.ini", "../WebViewerHub.ini", "../../WebViewerHub.ini" };
            string? configFile = null;
            
            foreach (var f in iniFiles)
            {
                if (File.Exists(f))
                {
                    configFile = f;
                    break;
                }
            }
            
            if (configFile == null) return;
            
            try
            {
                var lines = File.ReadAllLines(configFile);
                string section = "";
                
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        section = trimmed.Substring(1, trimmed.Length - 2);
                        continue;
                    }
                    
                    if (trimmed.Contains("="))
                    {
                        var parts = trimmed.Split('=', 2);
                        var key = parts[0].Trim();
                        var value = parts[1].Trim();
                        
                        if (section == "Hub")
                        {
                            if (key == "ViewerPort" && int.TryParse(value, out int vp))
                                VIEWER_PORT = vp;
                            else if (key == "SimulatorPort" && int.TryParse(value, out int sp))
                                SIM_PORT = sp;
                        }
                    }
                }
                
                Console.WriteLine($"[HUB] Config loaded: ViewerPort={VIEWER_PORT}, SimulatorPort={SIM_PORT}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HUB] Config error: {ex.Message}");
            }
        }
        
        static void RemoveSimulator(string connId)
        {
            if (simulators.TryRemove(connId, out var sim))
            {
                foreach (var region in regions.Where(r => r.Value.SimId == connId).ToList())
                {
                    regions.TryRemove(region.Key, out _);
                }
            }
        }
        
        static void BroadcastUpdates(object? state)
        {
            try
            {
                foreach (var viewer in viewers.Values)
                {
                    var currentRegion = viewer.CurrentRegion;
                    if (currentRegion == null) continue;
                    
                    if (regions.TryGetValue(currentRegion, out var regionInfo))
                    {
                        var sim = simulators.GetValueOrDefault(regionInfo.SimId);
                        sim?.Send(new { cmd = "get_agents", viewer_id = viewer.Id });
                    }
                }
            }
            catch { }
        }
        
        public static void RegisterRegion(string simId, string name, ulong handle, int locX, int locY)
        {
            regions[name.ToLower()] = new RegionInfo { Name = name, Handle = handle, LocX = locX, LocY = locY, SimId = simId };
            Console.WriteLine($"[HUB] Registered: {name} ({locX},{locY})");
            foreach (var v in viewers.Values) v.SendRegions();
        }
        
        public static void SendToSim(string simId, object data)
        {
            if (simulators.TryGetValue(simId, out var sim)) sim.Send(data);
        }
        
        public static void BroadcastToViewers(object data)
        {
            foreach (var v in viewers.Values) v.Send(data);
        }
        
        public static void SendToViewer(string viewerId, object data)
        {
            if (viewers.TryGetValue(viewerId, out var v)) v.Send(data);
        }
        
        public static List<string> GetAllRegions() => regions.Values.Select(r => r.Name).ToList();
        
        public static RegionInfo? GetRegion(string name) => regions.GetValueOrDefault(name.ToLower());
    }
    
    class RegionInfo
    {
        public string Name { get; set; } = "";
        public ulong Handle { get; set; }
        public int LocX { get; set; }
        public int LocY { get; set; }
        public string SimId { get; set; } = "";
    }
    
    class SimServer
    {
        private readonly IWebSocketConnection _socket;
        private readonly string _id;
        
        public SimServer(IWebSocketConnection socket, string id) { _socket = socket; _id = id; }
        
        public void HandleMessage(string msg)
        {
            try
            {
                using var doc = JsonDocument.Parse(msg);
                var root = doc.RootElement;
                var cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : "";
                
                switch (cmd)
                {
                    case "register":
                        var name = root.GetProperty("region_name").GetString() ?? "";
                        var handle = root.GetProperty("region_handle").GetUInt64();
                        var locX = root.GetProperty("location_x").GetInt32();
                        var locY = root.GetProperty("location_y").GetInt32();
                        Program.RegisterRegion(_id, name, handle, locX, locY);
                        break;
                    case "viewer_login_response":
                        {
                            var loginViewerId = root.TryGetProperty("viewer_id", out var loginViewerIdProp) ? loginViewerIdProp.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(loginViewerId))
                            {
                                var success = root.TryGetProperty("success", out var successProp) && successProp.GetBoolean();
                                if (success)
                                {
                                    var regionName = root.TryGetProperty("region_name", out var regionNameProp) ? regionNameProp.GetString() ?? string.Empty : string.Empty;
                                    if (Program.viewers.TryGetValue(loginViewerId, out var loginViewer) && !string.IsNullOrWhiteSpace(regionName))
                                        loginViewer.SetCurrentRegion(regionName);

                                    Program.SendToViewer(loginViewerId, new
                                    {
                                        type = "login_success",
                                        agent_id = root.TryGetProperty("agent_id", out var agentIdProp) ? agentIdProp.GetString() : string.Empty,
                                        first_name = root.TryGetProperty("first_name", out var firstNameProp) ? firstNameProp.GetString() : string.Empty,
                                        last_name = root.TryGetProperty("last_name", out var lastNameProp) ? lastNameProp.GetString() : string.Empty,
                                        region_name = regionName,
                                        region_handle = root.TryGetProperty("region_handle", out var regionHandleProp) ? regionHandleProp.GetUInt64() : 0UL
                                    });
                                }
                                else
                                {
                                    Program.SendToViewer(loginViewerId, new
                                    {
                                        type = "error",
                                        message = root.TryGetProperty("message", out var messageProp) ? messageProp.GetString() : "Login failed"
                                    });
                                }
                            }
                            break;
                        }
                    case "agents":
                        var vid = root.TryGetProperty("viewer_id", out var v) ? v.GetString() : "";
                        if (!string.IsNullOrEmpty(vid) && root.TryGetProperty("agents", out var agentsProp))
                        {
                            Program.SendToViewer(vid, new { type = "agent_update", agents = JsonSerializer.Deserialize<object>(agentsProp.GetRawText()) });
                        }
                        break;
                    case "chat":
                        var chatRegionName = root.GetProperty("region_name").GetString()?.ToLower();
                        foreach (var chatViewer in Program.viewers.Values.Where(x => x.CurrentRegion?.ToLower() == chatRegionName))
                            chatViewer.Send(new { type = "chat", from = root.GetProperty("from").GetString(), message = root.GetProperty("message").GetString() });
                        break;
                    case "object_added":
                    case "object_removed":
                    case "object_update":
                        var r = root.GetProperty("region_name").GetString()?.ToLower();
                        foreach (var viewer in Program.viewers.Values.Where(x => x.CurrentRegion?.ToLower() == r))
                            viewer.Send(JsonSerializer.Deserialize<Dictionary<string, object>>(root.GetRawText()));
                        break;
                    case "objects":
                        {
                            var objectsRegion = root.GetProperty("region_name").GetString()?.ToLower();
                            foreach (var viewer in Program.viewers.Values.Where(x => x.CurrentRegion?.ToLower() == objectsRegion))
                            {
                                viewer.Send(new
                                {
                                    type = "object_update",
                                    objects = root.TryGetProperty("objects", out var objectsProp)
                                        ? JsonSerializer.Deserialize<object>(objectsProp.GetRawText())
                                        : Array.Empty<object>()
                                });
                            }
                            break;
                        }
                }
            }
            catch { }
        }
        
        public void Send(object data) { try { _socket.Send(JsonSerializer.Serialize(data)); } catch { } }
    }
    
    class ViewerClient
    {
        private readonly IWebSocketConnection _socket;
        public string Id { get; }
        public string? CurrentRegion { get; private set; }
        
        public ViewerClient(IWebSocketConnection socket, string id) { _socket = socket; Id = id; }
        
        public void HandleMessage(string msg)
        {
            try
            {
                using var doc = JsonDocument.Parse(msg);
                var root = doc.RootElement;
                var cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : "";
                
                switch (cmd)
                {
                    case "login":
                        {
                            string first = root.TryGetProperty("first", out var firstProp) ? firstProp.GetString() ?? string.Empty : string.Empty;
                            string last = root.TryGetProperty("last", out var lastProp) ? lastProp.GetString() ?? string.Empty : string.Empty;
                            string password = root.TryGetProperty("password", out var passwordProp) ? passwordProp.GetString() ?? string.Empty : string.Empty;
                            string regionInput = root.TryGetProperty("start", out var startProp) ? startProp.GetString() ?? string.Empty : string.Empty;

                            var availableRegions = Program.GetAllRegions();
                            if (availableRegions.Count == 0)
                            {
                                Send(new { type = "error", message = "No regions are currently online." });
                                break;
                            }

                            var regionName = string.IsNullOrWhiteSpace(regionInput)
                                ? availableRegions[0]
                                : availableRegions.FirstOrDefault(r => string.Equals(r, regionInput, StringComparison.OrdinalIgnoreCase)) ?? availableRegions[0];

                            var region = Program.GetRegion(regionName);
                            if (region == null)
                            {
                                Send(new { type = "error", message = "Region not found." });
                                break;
                            }

                            Program.SendToSim(region.SimId, new
                            {
                                cmd = "viewer_login",
                                viewer_id = Id,
                                first,
                                last,
                                password,
                                region = region.Name
                            });
                            break;
                        }
                    case "request_regions":
                        SendRegions();
                        break;
                    case "teleport":
                        var target = root.GetProperty("region").GetString();
                        var targetRegion = Program.GetRegion(target ?? "");
                        if (targetRegion != null)
                        {
                            CurrentRegion = targetRegion.Name;
                            Program.SendToSim(targetRegion.SimId, new { cmd = "viewer_login", viewer_id = Id, region = targetRegion.Name });
                        }
                        else Send(new { type = "error", message = "Region not found" });
                        break;
                    case "chat":
                    case "agent_update":
                    case "create_object":
                    case "object_move":
                    case "object_rotate":
                    case "object_delete":
                    case "request_objects":
                    case "request_agents":
                        if (CurrentRegion != null)
                        {
                            var ri = Program.GetRegion(CurrentRegion);
                            if (ri != null)
                            {
                                var data = JsonSerializer.Deserialize<Dictionary<string, object>>(root.GetRawText());
                                data["viewer_id"] = Id;
                                Program.SendToSim(ri.SimId, data);
                            }
                        }
                        break;
                    case "logout":
                        if (CurrentRegion != null)
                        {
                            var ri = Program.GetRegion(CurrentRegion);
                            if (ri != null) Program.SendToSim(ri.SimId, new { cmd = "viewer_logout", viewer_id = Id });
                        }
                        CurrentRegion = null;
                        break;
                }
            }
            catch { }
        }
        
        public void SetCurrentRegion(string regionName) => CurrentRegion = regionName;

        public void SendRegions() => Send(new { type = "regions", regions = Program.GetAllRegions().Select(r => new { name = r }).ToArray() });
        public void Send(object data) { try { _socket.Send(JsonSerializer.Serialize(data)); } catch { } }
    }
}
