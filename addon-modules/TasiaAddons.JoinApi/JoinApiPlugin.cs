/*
 * TasiaAddons.JoinApi - JSON account registration endpoint for Robust.
 *
 * Exposes  POST /1-join  which accepts a JSON body and creates a grid account
 * using the same service calls as the built-in RemoteAdmin "create user":
 * store account, set password, set home region, create inventory.
 *
 * WHY A ServiceConnector AND NOT AN IApplicationPlugin
 * ---------------------------------------------------
 * Robust does NOT load application addins.  Server/ServerMain.cs builds the
 * server entirely from the [ServiceList] section:
 *
 *     IConfig servicesConfig = m_Server.Config.Configs["ServiceList"];
 *     ...
 *     connector = ServerUtils.LoadPlugin<IServiceConnector>(conn, modargs);   // args: (config, server, configName)
 *
 * The constructor is then tried again as (config, server).  Every handler
 * Robust exposes (AssetServiceConnector, QuicProxyConnector, ...) is wired that
 * way, and Robust.log contains zero [PLUGINS] lines while each region log
 * contains hundreds.  So this class is an IServiceConnector and is registered by
 * adding to Robust.ini:
 *
 *     [ServiceList]
 *     JoinApi = "${Const|PrivatePort}/TasiaAddons.JoinApi.dll:JoinApiConnector"
 *
 * Robust has no SceneManager, so services are resolved the same way the built-in
 * connectors do it: read LocalServiceModule out of the service's own ini section
 * and ServerUtils.LoadPlugin<T>() it.  Done lazily on first request so a bad
 * service name can never take the grid down at boot.
 *
 * Security posture (all configurable in [JoinApi]):
 *   - API key required, compared in constant time. Sent as X-Api-Key header
 *     or "apiKey" in the JSON body.
 *   - Localhost only by default. Set AllowRemote=true to accept off-box.
 *   - Per-IP rate limit, because an open create-account endpoint is a spam magnet.
 *   - The password is never echoed back in a response and never logged.
 */

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using log4net;
using Mono.Addins;
using Nini.Config;                                  // IConfigSource, IConfig
using OpenMetaverse;                                // UUID
using OpenSim.Framework;                            // UserAccount, Vector3
using OpenSim.Framework.Servers;                    // MainServer
using OpenSim.Framework.Servers.HttpServer;         // IHttpServer
using OpenSim.Server.Base;                          // ServerUtils
using OpenSim.Server.Handlers.Base;                 // ServiceConnector
using OpenSim.Services.Interfaces;                  // IUserAccountService etc.

namespace TasiaAddons.JoinApi
{
    public class JoinApiConnector : ServiceConnector
    {
        private static readonly ILog m_log = LogManager.GetLogger(typeof(JoinApiConnector));

        private const string HandlerPath = "/1-join";
        private const string JsonContentType = "application/json; charset=utf-8";

        // request field names - these mirror the RemoteAdmin create-user parameter
        // names (user_firstname, user_lastname, user_password, user_email) in a
        // friendlier camelCase JSON shape.
        private const string FFirstName = "firstName";
        private const string FLastName = "lastName";
        private const string FPassword = "password";
        private const string FEmail = "email";
        private const string FApiKey = "apiKey";

        private IConfigSource m_configSource;

        // ---- configuration (from [JoinApi]) ----
        private string m_apiKey = string.Empty;
        private bool m_enabled = true;
        private bool m_allowRemote = false;      // default: localhost only
        private int m_minPasswordLength = 8;
        private int m_maxRequestsPerMinute = 10;
        private bool m_returnProfileId = true;

        // ---- per-IP rate limiting ----
        private readonly ConcurrentDictionary<string, Queue<DateTime>> m_hits
            = new ConcurrentDictionary<string, Queue<DateTime>>(StringComparer.OrdinalIgnoreCase);

        // ---- services, resolved lazily on first request ----
        private IUserAccountService m_userAccountService;
        private IAuthenticationService m_authService;
        private IInventoryService m_inventoryService;
        private IGridUserService m_gridUserService;
        private IGridService m_gridService;
        private bool m_servicesTried;

        public JoinApiConnector(IConfigSource configSource, IHttpServer httpServer, string configName)
            : base(configSource, httpServer, configName)
        {
            m_configSource = configSource;
            LoadConfig();

            if (!m_enabled)
            {
                m_log.Warn("[JOINAPI]: Disabled by configuration (Enabled=false). Handler NOT registered.");
                return;
            }

            if (string.IsNullOrWhiteSpace(m_apiKey))
            {
                m_log.Error("[JOINAPI]: No ApiKey configured - handler NOT registered. Set ApiKey in the [JoinApi] section of Robust.ini.");
                return;
            }

            httpServer.AddHTTPHandler(HandlerPath, HandleJoin);

            m_log.InfoFormat("[JOINAPI]: {0} registered. allowRemote={1}, rateLimit={2}/min, minPasswordLength={3}",
                HandlerPath, m_allowRemote, m_maxRequestsPerMinute, m_minPasswordLength);
        }

        // ------------------------------------------------------------------
        private void LoadConfig()
        {
            try
            {
                if (m_configSource == null) { m_log.Warn("[JOINAPI]: no config source; using defaults."); return; }

                IConfig cfg = m_configSource.Configs["JoinApi"];
                if (cfg == null)
                {
                    m_log.Warn("[JOINAPI]: No [JoinApi] section in Robust.ini; using defaults (ApiKey will be missing).");
                    return;
                }

                m_apiKey = cfg.GetString("ApiKey", string.Empty);
                m_enabled = cfg.GetBoolean("Enabled", true);
                m_allowRemote = cfg.GetBoolean("AllowRemote", false);
                m_minPasswordLength = cfg.GetInt("MinPasswordLength", 8);
                m_maxRequestsPerMinute = cfg.GetInt("MaxRequestsPerMinute", 10);
                m_returnProfileId = cfg.GetBoolean("ReturnProfileId", true);
            }
            catch (Exception e)
            {
                m_log.Error("[JOINAPI]: Failed to read configuration: " + e.Message);
            }
        }

        /// <summary>
        /// Load the backing services the same way the built-in Robust connectors do:
        /// read LocalServiceModule from the service's own ini section, then
        /// ServerUtils.LoadPlugin.  Robust has no SceneManager, so there is nothing to
        /// pull these off of.  Called once, lazily, so a bad config can never abort boot.
        /// </summary>
        private bool EnsureServices(out string error)
        {
            error = null;
            if (m_servicesTried)
                return m_userAccountService != null;

            m_servicesTried = true;
            try
            {
                m_userAccountService = LoadService<IUserAccountService>("UserAccountService");
                m_authService = LoadService<IAuthenticationService>("AuthenticationService");
                m_inventoryService = LoadService<IInventoryService>("InventoryService");
                m_gridUserService = LoadService<IGridUserService>("GridUserService");
                m_gridService = LoadService<IGridService>("GridService");

                if (m_userAccountService == null)
                {
                    error = "IUserAccountService could not be loaded (check [UserAccountService] LocalServiceModule).";
                    m_log.Error("[JOINAPI]: " + error);
                    return false;
                }
            }
            catch (Exception e)
            {
                error = "Service load failed: " + e.Message;
                m_log.Error("[JOINAPI]: " + e);
                return false;
            }

            m_log.InfoFormat("[JOINAPI]: services ready (userAccount={0}, auth={1}, inventory={2}, gridUser={3}, grid={4})",
                m_userAccountService != null, m_authService != null, m_inventoryService != null,
                m_gridUserService != null, m_gridService != null);
            return true;
        }

        private T LoadService<T>(string sectionName) where T : class
        {
            IConfig section = m_configSource?.Configs[sectionName];
            if (section == null)
            {
                m_log.WarnFormat("[JOINAPI]: no [{0}] section in Robust.ini", sectionName);
                return null;
            }

            string module = section.GetString("LocalServiceModule", string.Empty);
            if (string.IsNullOrEmpty(module))
            {
                m_log.WarnFormat("[JOINAPI]: [{0}] has no LocalServiceModule", sectionName);
                return null;
            }

            return ServerUtils.LoadPlugin<T>(module, new object[] { m_configSource });
        }

        /// <summary>
        /// OpenSim builds the handler request table with the body under DIFFERENT keys
        /// depending on which path served the request, so accept either:
        ///   BaseHttpServer.cs:942  keysvals.Add("requestbody", ...)  (IGenericHTTPHandler / stream path)
        ///   BaseHttpServer.cs:1971 keysvals.Add("body",        ...)  (HandleContentVerbs, taken for
        ///                                                            Content-Type: application/json)
        /// Reading only one of them silently yields an empty body.
        /// </summary>
        private static string GetRequestBody(Hashtable request)
        {
            if (request == null) return string.Empty;
            foreach (string key in new[] { "body", "requestbody" })
            {
                if (request[key] is string s && !string.IsNullOrWhiteSpace(s))
                    return s;
            }
            return string.Empty;
        }

        // ------------------------------------------------------------------
        // HTTP entry point.  Signature is fixed by OpenSim:
        //   public delegate Hashtable GenericHTTPMethod(Hashtable request);
        // Response table keys are read by BaseHttpServer.DoHTTPGruntWork:
        //   "int_response_code", "content_type", "str_response_string"
        //   (or "bin_response_data" as byte[], which takes precedence), plus the
        //   optional "error_status_text", "keepalive" and "headers".
        // ------------------------------------------------------------------
        public Hashtable HandleJoin(Hashtable request)
        {
            try
            {
                string body = GetRequestBody(request);
                Hashtable headers = request?["headers"] as Hashtable ?? new Hashtable();

                string remoteIp = GetRemoteIp(headers);
                string presentedKey = ExtractApiKey(headers, body);

                // 1. API key first - do not leak validation detail to unauthenticated callers.
                if (!KeysMatch(presentedKey, m_apiKey))
                {
                    m_log.WarnFormat("[JOINAPI]: rejected request from {0} (bad or missing api key)", remoteIp);
                    return Respond(401, "unauthorized", "Invalid or missing API key.");
                }

                // 2. locality
                if (!m_allowRemote && !IsLocal(remoteIp))
                {
                    m_log.WarnFormat("[JOINAPI]: rejected off-box request from {0} (AllowRemote=false)", remoteIp);
                    return Respond(403, "forbidden", "Endpoint is restricted to local requests.");
                }

                // 3. rate limit
                if (!AllowRequest(remoteIp))
                {
                    m_log.WarnFormat("[JOINAPI]: rate limited {0}", remoteIp);
                    return Respond(429, "too_many_requests", "Too many requests. Try again shortly.");
                }

                // 4. parse JSON
                JoinRequest jr;
                try
                {
                    jr = JsonSerializer.Deserialize<JoinRequest>(body);
                }
                catch (JsonException je)
                {
                    return Respond(400, "bad_request", "Body is not valid JSON: " + je.Message);
                }
                if (jr == null)
                    return Respond(400, "bad_request", "Body is empty.");

                // 5. validate
                string validationError = Validate(jr);
                if (validationError != null)
                    return Respond(400, "bad_request", validationError);

                // 6. services
                if (!EnsureServices(out string serviceError))
                    return Respond(503, "unavailable", serviceError);

                // 7. create
                return CreateAccount(jr, remoteIp);
            }
            catch (Exception e)
            {
                m_log.Error("[JOINAPI]: unhandled error: " + e);
                return Respond(500, "server_error", "Internal error.");
            }
        }

        // ------------------------------------------------------------------
        private Hashtable CreateAccount(JoinRequest jr, string remoteIp)
        {
            // Robust is a pure server: there is no scene, so accounts are created
            // at the grid (null) scope.
            UUID scopeID = UUID.Zero;

            // already exists?
            UserAccount existing = null;
            try { existing = m_userAccountService.GetUserAccount(scopeID, jr.firstName, jr.lastName); }
            catch (Exception e) { m_log.Warn("[JOINAPI]: GetUserAccount threw: " + e.Message); }

            if (existing != null)
            {
                m_log.InfoFormat("[JOINAPI]: duplicate account rejected: {0} {1}", jr.firstName, jr.lastName);
                return Respond(409, "conflict", "An account with that name already exists.");
            }

            // ---- create ----
            var account = new UserAccount(scopeID, UUID.Random(), jr.firstName, jr.lastName,
                                         string.IsNullOrWhiteSpace(jr.email) ? string.Empty : jr.email);

            if (account.ServiceURLs == null || account.ServiceURLs.Count == 0)
            {
                account.ServiceURLs = new Dictionary<string, object>
                {
                    { "HomeURI", string.Empty },
                    { "InventoryServerURI", string.Empty },
                    { "AssetServerURI", string.Empty },
                };
            }

            if (!m_userAccountService.StoreUserAccount(account))
            {
                m_log.ErrorFormat("[JOINAPI]: StoreUserAccount failed for {0} {1}", jr.firstName, jr.lastName);
                return Respond(500, "server_error", "Could not store the account.");
            }

            bool passwordSet = false;
            if (m_authService != null)
                passwordSet = m_authService.SetPassword(account.PrincipalID, jr.password);
            if (!passwordSet)
                m_log.WarnFormat("[JOINAPI]: password NOT set for {0} {1}", jr.firstName, jr.lastName);

            // home region = first default region
            string homeWarning = null;
            try
            {
                if (m_gridService != null && m_gridUserService != null)
                {
                    var defaults = m_gridService.GetDefaultRegions(UUID.Zero);
                    if (defaults != null && defaults.Count >= 1)
                        m_gridUserService.SetHome(account.PrincipalID.ToString(), defaults[0].RegionID,
                                                  new Vector3(128, 128, 0), new Vector3(0, 1, 0));
                    else
                        homeWarning = "No default region is flagged, so no home region was set.";
                }
            }
            catch (Exception e) { homeWarning = "Home region could not be set: " + e.Message; }

            bool inventoryCreated = false;
            try { if (m_inventoryService != null) inventoryCreated = m_inventoryService.CreateUserInventory(account.PrincipalID); }
            catch (Exception e) { m_log.Warn("[JOINAPI]: CreateUserInventory threw: " + e.Message); }

            m_log.InfoFormat("[JOINAPI]: account created {0} {1} principal={2} passwordSet={3} inventory={4} from={5}",
                jr.firstName, jr.lastName, account.PrincipalID, passwordSet, inventoryCreated, remoteIp);

            // ---- respond.  NEVER include the password. ----
            var payload = new Dictionary<string, object>
            {
                { "ok", true },
                { "firstName", jr.firstName },
                { "lastName", jr.lastName },
            };
            if (m_returnProfileId)
                payload["principalId"] = account.PrincipalID.ToString();
            payload["passwordSet"] = passwordSet;
            payload["inventoryCreated"] = inventoryCreated;
            if (homeWarning != null) payload["warning"] = homeWarning;

            return Respond(200, "ok", null, payload);
        }

        // ------------------------------------------------------------------
        private string Validate(JoinRequest jr)
        {
            jr.firstName = (jr.firstName ?? string.Empty).Trim();
            jr.lastName = (jr.lastName ?? string.Empty).Trim();
            jr.email = (jr.email ?? string.Empty).Trim();

            if (jr.firstName.Length == 0 || jr.lastName.Length == 0)
                return "firstName and lastName are both required.";

            var nameOk = new Regex(@"^[A-Za-z][A-Za-z0-9'\-\. ]{0,62}$");
            if (!nameOk.IsMatch(jr.firstName)) return "firstName contains unsupported characters.";
            if (!nameOk.IsMatch(jr.lastName)) return "lastName contains unsupported characters.";

            if (string.IsNullOrEmpty(jr.password))
                return "password is required.";
            if (jr.password.Length < m_minPasswordLength)
                return string.Format("password must be at least {0} characters.", m_minPasswordLength);
            if (jr.password.Length > 256)
                return "password is too long.";

            if (jr.email.Length > 0)
            {
                if (jr.email.Length > 254) return "email is too long.";
                if (!Regex.IsMatch(jr.email, @"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$"))
                    return "email is not a valid address.";
            }

            return null;
        }

        // ------------------------------------------------------------------
        private string ExtractApiKey(Hashtable headers, string body)
        {
            // header first
            foreach (var k in new[] { "X-Api-Key", "x-api-key", "X-API-KEY" })
            {
                object v = headers[k];
                if (v != null && !string.IsNullOrWhiteSpace(v.ToString())) return v.ToString().Trim();
            }
            // then body
            try
            {
                using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty(FApiKey, out JsonElement k2) &&
                        k2.ValueKind == JsonValueKind.String)
                        return k2.GetString()?.Trim();
                }
            }
            catch { /* body is not JSON yet; validation happens later */ }
            return string.Empty;
        }

        /// <summary>Constant-time comparison so the key cannot be recovered by timing.</summary>
        private static bool KeysMatch(string presented, string expected)
        {
            if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected)) return false;
            byte[] a = Encoding.UTF8.GetBytes(presented);
            byte[] b = Encoding.UTF8.GetBytes(expected);
            if (a.Length != b.Length) return false;
            return CryptographicOperations.FixedTimeEquals(a, b);
        }

        private bool AllowRequest(string ip)
        {
            if (m_maxRequestsPerMinute <= 0) return true;
            var q = m_hits.GetOrAdd(ip, _ => new Queue<DateTime>());
            lock (q)
            {
                DateTime cutoff = DateTime.UtcNow.AddMinutes(-1);
                while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
                if (q.Count >= m_maxRequestsPerMinute) return false;
                q.Enqueue(DateTime.UtcNow);
                return true;
            }
        }

        private static string GetRemoteIp(Hashtable headers)
        {
            foreach (var k in new[] { "X-Forwarded-For", "x-forwarded-for", "X-Real-IP" })
            {
                object v = headers[k];
                if (v != null)
                {
                    string s = v.ToString().Trim();
                    if (s.Length > 0) return s.Split(',')[0].Trim();
                }
            }
            return "local";
        }

        private static bool IsLocal(string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip == "local") return true;
            if (IPAddress.TryParse(ip, out IPAddress parsed))
                return IPAddress.IsLoopback(parsed);
            return false;
        }

        private static Hashtable Respond(int code, string status, string message,
                                         IDictionary<string, object> extra = null)
        {
            var payload = new Dictionary<string, object> { { "ok", code == 200 } };
            if (message != null) payload["error"] = message;
            if (extra != null)
                foreach (var kv in extra) payload[kv.Key] = kv.Value;

            string json = JsonSerializer.Serialize(payload);

            return new Hashtable
            {
                { "int_response_code", code },
                { "content_type", JsonContentType },
                { "str_response_string", json },
                { "error_status_text", status },
                { "keepalive", false },
            };
        }

        // JSON contract.  Property names are matched case-insensitively.
        private sealed class JoinRequest
        {
            public string firstName { get; set; }
            public string lastName { get; set; }
            public string password { get; set; }
            public string email { get; set; }
            public string apiKey { get; set; }
        }
    }
}
