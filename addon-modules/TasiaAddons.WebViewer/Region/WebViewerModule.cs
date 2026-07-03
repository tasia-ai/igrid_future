using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace TasiaAddons.WebViewer
{
    public class WebViewerHubClient : ISharedRegionModule
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(WebViewerHubClient));

        private bool m_enabled;
        private string m_hubHost = "127.0.0.1";
        private int m_hubPort = 9003;
        
        private ClientWebSocket? m_ws;
        private CancellationTokenSource? m_cts;
        private readonly List<Scene> m_scenes = new();
        private readonly ConcurrentDictionary<string, ViewerConnection> m_viewers = new();
        private bool m_initialized;

        public void Initialise(IConfigSource config)
        {
            IConfig webConfig = config.Configs["WebViewer"];
            if (webConfig == null)
            {
                Log.Info("[WEBVIEWER]: No WebViewer section in config; disabled.");
                m_enabled = false;
                return;
            }

            m_enabled = webConfig.GetBoolean("Enabled", false);
            if (!m_enabled)
            {
                Log.Info("[WEBVIEWER]: Disabled via config.");
                return;
            }

            m_hubHost = webConfig.GetString("HubHost", m_hubHost);
            m_hubPort = webConfig.GetInt("HubPort", m_hubPort);
            
            Log.InfoFormat("[WEBVIEWER]: Will connect to hub at {0}:{1}", m_hubHost, m_hubPort);
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled) return;

            lock (m_scenes)
            {
                if (!m_scenes.Contains(scene))
                    m_scenes.Add(scene);
            }

            scene.EventManager.OnChatFromClient += OnChatFromClient;
            scene.EventManager.OnChatFromWorld += OnChatFromWorld;
            scene.EventManager.OnNewClient += OnNewClient;
            scene.EventManager.OnClientClosed += OnClientClosed;
            scene.EventManager.OnObjectAddedToScene += OnObjectAdded;
            scene.EventManager.OnObjectRemovedFromScene += OnObjectRemoved;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled || m_initialized) return;

            ConnectToHub();
            m_initialized = true;
        }

        private async void ConnectToHub()
        {
            try
            {
                m_cts = new CancellationTokenSource();
                m_ws = new ClientWebSocket();
                
                var uri = $"ws://{m_hubHost}:{m_hubPort}";
                Log.Info($"[WEBVIEWER]: Connecting to hub at {uri}");
                
                await m_ws.ConnectAsync(new Uri(uri), m_cts.Token);
                
                if (m_ws.State == WebSocketState.Open)
                {
                    Log.Info("[WEBVIEWER]: Connected to hub!");
                    
                    // Register all regions
                    foreach (var scene in m_scenes)
                    {
                        RegisterRegion(scene);
                    }
                    
                    // Start receive loop
                    _ = ReceiveLoop();
                }
            }
            catch (Exception ex)
            {
                Log.Error("[WEBVIEWER]: Failed to connect to hub", ex);
            }
        }
        
        private void RegisterRegion(Scene scene)
        {
            SendToHub(new
            {
                cmd = "register",
                region_name = scene.RegionInfo.RegionName,
                region_handle = scene.RegionInfo.RegionHandle,
                location_x = scene.RegionInfo.RegionLocX,
                location_y = scene.RegionInfo.RegionLocY
            });
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[8192];
            
            try
            {
                while (m_ws?.State == WebSocketState.Open && !m_cts!.Token.IsCancellationRequested)
                {
                    var result = await m_ws.ReceiveAsync(new ArraySegment<byte>(buffer), m_cts.Token);
                    
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        HandleHubMessage(msg);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("[WEBVIEWER]: Receive error", ex);
            }
            
            // Reconnect
            await Task.Delay(3000);
            ConnectToHub();
        }

        private void HandleHubMessage(string msg)
        {
            try
            {
                using var doc = JsonDocument.Parse(msg);
                var root = doc.RootElement;
                var cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : "";
                var viewerId = root.TryGetProperty("viewer_id", out var vid) ? vid.GetString() : "";
                
                switch (cmd)
                {
                    case "viewer_login":
                        HandleViewerLogin(viewerId, root);
                        break;
                    case "viewer_logout":
                        HandleViewerLogout(viewerId);
                        break;
                    case "chat":
                        HandleChatFromViewer(viewerId, root);
                        break;
                    case "agent_update":
                        HandleAgentUpdate(viewerId, root);
                        break;
                    case "create_object":
                        HandleCreateObject(viewerId, root);
                        break;
                    case "object_move":
                    case "object_rotate":
                    case "object_delete":
                        HandleObjectOperation(viewerId, cmd, root);
                        break;
                    case "request_objects":
                    case "request_agents":
                        HandleDataRequest(viewerId, cmd);
                        break;
                    case "get_agents":
                        SendAgentsToHub(root);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("[WEBVIEWER]: Handle message error", ex);
            }
        }
        
        private void HandleViewerLogin(string viewerId, JsonElement data)
        {
            string first = data.TryGetProperty("first", out var f) ? f.GetString() ?? "" : "";
            string last = data.TryGetProperty("last", out var l) ? l.GetString() ?? "" : "";
            string password = data.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
            string regionName = data.TryGetProperty("region", out var r) ? r.GetString() ?? "" : "";

            var scene = string.IsNullOrEmpty(regionName) ? m_scenes.FirstOrDefault() : GetSceneByName(regionName);
            scene ??= m_scenes.FirstOrDefault();

            if (scene == null) return;

            var userService = scene.UserAccountService;
            if (userService == null) return;

            var account = userService.GetUserAccount(scene.RegionInfo.ScopeID, $"{first} {last}");
            if (account == null) return;

            if (!VerifyPassword(account, password)) return;

            var viewer = new ViewerConnection
            {
                Id = viewerId,
                Scene = scene,
                AgentId = account.PrincipalID,
                Name = $"{first} {last}"
            };
            
            m_viewers[viewerId] = viewer;

            SendToHub(new
            {
                cmd = "viewer_login_response",
                viewer_id = viewerId,
                success = true,
                agent_id = viewer.AgentId.ToString(),
                first_name = first,
                last_name = last,
                region_handle = scene.RegionInfo.RegionHandle,
                region_name = scene.RegionInfo.RegionName
            });
        }
        
        private void HandleViewerLogout(string viewerId)
        {
            m_viewers.TryRemove(viewerId, out _);
        }
        
        private void HandleChatFromViewer(string viewerId, JsonElement data)
        {
            if (!m_viewers.TryGetValue(viewerId, out var viewer)) return;
            
            string message = data.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            int channel = data.TryGetProperty("channel", out var c) ? c.GetInt32() : 0;
            
            var chatMessage = new OSChatMessage
            {
                Type = ChatType.Chat,
                Position = new Vector3(128, 128, 30),
                Scene = viewer.Scene,
                SenderUUID = viewer.AgentId,
                From = viewer.Name,
                Message = message,
                Channel = channel,
                Destination = "region"
            };
            
            viewer.Scene.EventManager.ChatFromClient(chatMessage);
        }
        
        private void HandleAgentUpdate(string viewerId, JsonElement data)
        {
            if (!m_viewers.TryGetValue(viewerId, out var viewer)) return;
            
            if (data.TryGetProperty("position", out var pos))
            {
                var arr = pos.EnumerateArray().ToArray();
                if (arr.Length >= 3)
                {
                    var newPos = new Vector3(arr[0].GetSingle(), arr[1].GetSingle(), arr[2].GetSingle());
                    var agent = viewer.Scene.GetSceneAgent(viewer.AgentId);
                    if (agent != null) agent.Position = newPos;
                }
            }
        }
        
        private void HandleCreateObject(string viewerId, JsonElement data)
        {
            if (!m_viewers.TryGetValue(viewerId, out var viewer)) return;
            
            string type = data.TryGetProperty("type", out var t) ? t.GetString() ?? "box" : "box";
            
            var shape = new PrimitiveBaseShape();
            shape.PrimType = type switch
            {
                "sphere" => PrimType.Sphere,
                "cylinder" => PrimType.Cylinder,
                "torus" => PrimType.Torus,
                _ => PrimType.Box
            };
            
            var pos = new Vector3(128, 128, 30);
            var part = new SceneObjectPart(viewer.AgentId, shape, pos, Quaternion.Identity, "")
            {
                Scale = new Vector3(0.5f, 0.5f, 0.5f)
            };
            
            var group = new SceneObjectGroup(part) { Name = $"Object by {viewer.Name}" };
            viewer.Scene.AddNewSceneObject(group, true);
        }
        
        private void HandleObjectOperation(string viewerId, string op, JsonElement data)
        {
            if (!m_viewers.TryGetValue(viewerId, out var viewer)) return;
            
            uint localId = data.TryGetProperty("local_id", out var id) ? id.GetUInt32() : 0;
            var obj = viewer.Scene.GetSceneObjectGroup(localId);
            if (obj == null || obj.OwnerID != viewer.AgentId) return;
            
            switch (op)
            {
                case "object_move":
                    if (data.TryGetProperty("position", out var pos))
                    {
                        var arr = pos.EnumerateArray().ToArray();
                        if (arr.Length >= 3)
                        {
                            obj.AbsolutePosition = new Vector3(arr[0].GetSingle(), arr[1].GetSingle(), arr[2].GetSingle());
                            obj.ScheduleGroupUpdate(PrimUpdateFlags.Position);
                        }
                    }
                    break;
                case "object_rotate":
                    if (data.TryGetProperty("rotation", out var rot))
                    {
                        var arr = rot.EnumerateArray().ToArray();
                        if (arr.Length >= 4)
                        {
                            obj.RootPart.Rotation = new Quaternion(arr[0].GetSingle(), arr[1].GetSingle(), arr[2].GetSingle(), arr[3].GetSingle());
                            obj.ScheduleGroupUpdate(PrimUpdateFlags.Rotation);
                        }
                    }
                    break;
                case "object_delete":
                    viewer.Scene.DeleteSceneObject(obj, false);
                    break;
            }
        }
        
        private void HandleDataRequest(string viewerId, string cmd)
        {
            if (!m_viewers.TryGetValue(viewerId, out var viewer)) return;
            
            if (cmd == "request_objects")
            {
                var objects = new List<object>();
                foreach (var entity in viewer.Scene.GetEntities())
                {
                    if (entity is SceneObjectGroup sog)
                    {
                        var pos = sog.AbsolutePosition;
                        objects.Add(new { local_id = sog.LocalId, name = sog.Name, position = new[] { pos.X, pos.Y, pos.Z } });
                    }
                }
                
                SendToHub(new { cmd = "objects", region_name = viewer.Scene.RegionInfo.RegionName, objects = objects });
            }
            else if (cmd == "request_agents")
            {
                SendAgentsToHub(viewerId, viewerId);
            }
        }
        
        private void SendAgentsToHub(string? filterViewerId = null, string? requestingViewerId = null)
        {
            foreach (var kvp in m_viewers)
            {
                var viewer = kvp.Value;
                if (filterViewerId != null && viewer.Id != filterViewerId) continue;
                
                var agents = new List<object>();
                foreach (var client in viewer.Scene.GetClients())
                {
                    var pos = client.SceneAgent?.Position ?? Vector3.Zero;
                    agents.Add(new { agent_id = client.AgentId.ToString(), name = client.Name, position = new[] { pos.X, pos.Y, pos.Z } });
                }
                
                if (requestingViewerId != null)
                {
                    SendToHub(new { cmd = "agents", viewer_id = requestingViewerId, agents = agents });
                }
                else
                {
                    SendToHub(new { cmd = "agents", agents = agents });
                }
            }
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled) return;
            lock (m_scenes) m_scenes.Remove(scene);
        }

        public void Close()
        {
            if (!m_enabled) return;
            
            m_cts?.Cancel();
            m_ws?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).Wait(1000);
            
            Log.Info("[WEBVIEWER]: Disconnected from hub");
        }

        public void PostInitialise() { }
        public string Name => "WebViewerHubClient";
        public Type ReplaceableInterface => null;

        private void OnObjectAdded(SceneObjectGroup obj)
        {
            var scene = obj.Scene;
            if (scene == null) return;
            
            var pos = obj.AbsolutePosition;
            SendToHub(new
            {
                cmd = "object_added",
                region_name = scene.RegionInfo.RegionName,
                obj = new { local_id = obj.LocalId, name = obj.Name, position = new[] { pos.X, pos.Y, pos.Z } }
            });
        }

        private void OnObjectRemoved(SceneObjectGroup obj)
        {
            var scene = obj.Scene;
            if (scene == null) return;
            
            SendToHub(new { cmd = "object_removed", region_name = scene.RegionInfo.RegionName, local_id = obj.LocalId });
        }

        private void OnNewClient(IClientAPI client) { }
        private void OnClientClosed(UUID agentId, Scene scene)
        {
            foreach (var kvp in m_viewers.ToArray())
            {
                if (kvp.Value.AgentId != agentId)
                    continue;

                SendToHub(new { cmd = "viewer_logout", viewer_id = kvp.Value.Id });
                m_viewers.TryRemove(kvp.Key, out _);
            }
        }

        private void OnChatFromClient(object sender, OSChatMessage chat)
        {
            if (chat.Scene == null) return;
            SendToHub(new
            {
                cmd = "chat",
                region_name = chat.Scene.RegionInfo.RegionName,
                from = chat.From,
                message = chat.Message,
                channel = chat.Channel
            });
        }

        private void OnChatFromWorld(object sender, OSChatMessage chat)
        {
            if (chat.Scene == null) return;
            SendToHub(new
            {
                cmd = "chat",
                region_name = chat.Scene.RegionInfo.RegionName,
                from = chat.From,
                message = chat.Message,
                channel = chat.Channel
            });
        }

        private Scene? GetSceneByName(string name)
        {
            lock (m_scenes)
            {
                return m_scenes.FirstOrDefault(s => s.RegionInfo.RegionName.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
        }

        private void SendToHub(object data)
        {
            try
            {
                if (m_ws?.State == WebSocketState.Open)
                {
                    var json = JsonSerializer.Serialize(data);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    m_ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, m_cts!.Token).Wait(100);
                }
            }
            catch { }
        }

        private bool VerifyPassword(UserAccount account, string password)
        {
            string stored = account.Password;
            if (string.IsNullOrEmpty(stored)) return false;

            if (stored.StartsWith("$"))
            {
                try
                {
                    var parts = stored.Substring(1).Split('$');
                    if (parts.Length >= 2)
                    {
                        return Crypto.Hash(parts[0], password) == parts[1];
                    }
                }
                catch { }
            }
            return stored == password;
        }
    }
    
    class ViewerConnection
    {
        public string Id { get; set; } = "";
        public Scene? Scene { get; set; }
        public UUID AgentId { get; set; }
        public string Name { get; set; } = "";
    }
}
