#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using MySqlConnector;
using System.Xml;
using System.Xml.Serialization;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Services.LLLoginService;
using TasiaAddons.Abstractions;
using TasiaAddons.LoginSecurity;
using TasiaAddons.LoginSecurity.Data;

namespace TasiaAddons.MACAudit;

public class MACAuditLoginService : ILoginService
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MACAuditLoginService));

    private readonly ILoginService m_inner;
    private readonly IAccessControlService? m_accessControl;
    private readonly IToSAcceptanceData? m_toAcceptance;
    private readonly MacAuditSettings m_settings;
    private readonly MacAuditWriter m_writer;

    public MACAuditLoginService(IConfigSource config)
        : this(config, null, null)
    {
    }

    public MACAuditLoginService(IConfigSource config, ISimulationService? simService, ILibraryService? libraryService)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        m_settings = MacAuditSettings.FromConfig(config);

        string innerService = m_settings.InnerLoginService;
        
        object[] args = simService is not null && libraryService is not null
            ? new object[] { config, simService, libraryService }
            : new object[] { config };

        m_inner = ServerUtils.LoadPlugin<ILoginService>(innerService, args);
        
        if (m_inner is null)
        {
            Log.Error($"[NGC.MACAUDIT]: Failed to load inner login service {innerService}");
            throw new InvalidOperationException($"Failed to load inner login service {innerService}");
        }

        Log.Info($"[NGC.MACAUDIT]: Inner login service loaded: {innerService}");

        // Load AccessControlService for IP/hardware ban checks
        m_accessControl = null;
        try
        {
            string accessServiceDll = m_settings.AccessControlServiceDll;
            if (!string.IsNullOrEmpty(accessServiceDll))
            {
                m_accessControl = ServerUtils.LoadPlugin<IAccessControlService>(accessServiceDll, new object[] { config });
                if (m_accessControl is not null)
                    Log.Info("[NGC.MACAUDIT]: AccessControlService loaded for ban checks");
                else
                    Log.Warn("[NGC.MACAUDIT]: AccessControlService not found, ban checks disabled");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[NGC.MACAUDIT]: Failed to load AccessControlService, ban checks disabled", ex);
        }

        // Load ToS acceptance database
        m_toAcceptance = null;
        try
        {
            string tosServiceDll = m_settings.ToSAcceptanceServiceDll;
            string tosConnString = m_settings.ToSConnectionString;
            if (!string.IsNullOrEmpty(tosServiceDll))
            {
                m_toAcceptance = ServerUtils.LoadPlugin<IToSAcceptanceData>(tosServiceDll, new object[] { tosConnString });
                if (m_toAcceptance is not null)
                    Log.Info("[NGC.MACAUDIT]: ToS acceptance database loaded");
                else
                    Log.Warn("[NGC.MACAUDIT]: ToS acceptance database not found, ToS checks disabled");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[NGC.MACAUDIT]: Failed to load ToS acceptance database, ToS checks disabled", ex);
        }

        m_writer = new MacAuditWriter(m_settings);

        if (m_settings.Enable)
            Log.Info("[NGC.MACAUDIT]: MAC audit logging enabled");
        else
            Log.Info("[NGC.MACAUDIT]: MAC audit logging disabled");
    }

    public LoginResponse Login(string firstName, string lastName, string passwd, string startLocation, UUID scopeID,
        string clientVersion, string channel, string mac, string id0, IPEndPoint clientIP)
    {
        if (m_inner is null)
        {
            Log.Error("[NGC.MACAUDIT]: Inner login service is null!");
            throw new InvalidOperationException("Inner login service is not initialized");
        }

        try
        {
            // Check IP/hardware bans via AccessControlService
            if (m_accessControl is not null)
            {
                string ipStr = clientIP?.Address?.ToString() ?? string.Empty;

                if (!string.IsNullOrEmpty(ipStr) && m_accessControl.IsIPBanned(ipStr))
                {
                    Log.InfoFormat("[NGC.MACAUDIT]: Login denied for {0} {1} — IP {2} is banned", firstName, lastName, ipStr);
                    return new LLFailedLoginResponse("presence", "Your IP address has been banned. Please contact the grid owner.", "false");
                }

                if (!string.IsNullOrEmpty(mac) && m_accessControl.IsHardwareBanned(mac, id0 ?? string.Empty))
                {
                    Log.InfoFormat("[NGC.MACAUDIT]: Login denied for {0} {1} — hardware (mac={2}, id0={3}) is banned", firstName, lastName, mac, id0);
                    return new LLFailedLoginResponse("presence", "Your hardware has been banned. Please contact the grid owner.", "false");
                }
            }

            LoginResponse response = m_inner.Login(firstName, lastName, passwd, startLocation, scopeID, clientVersion, channel, mac, id0, clientIP);

            if (response is null)
            {
                Log.Error("[NGC.MACAUDIT]: Inner login service returned null response");
                return new LLFailedLoginResponse("presence", "Internal error", "false");
            }

            // Check ToS acceptance — must be after inner login to get user ID
            if (m_toAcceptance is not null && m_settings.EnableToSCheck && response is LLLoginResponse llResp && !llResp.AgentID.IsZero())
            {
                if (!m_toAcceptance.HasAccepted(llResp.AgentID.ToString(), m_settings.ToSVersion))
                {
                    Log.InfoFormat("[NGC.MACAUDIT]: Login BLOCKED for {0} {1} (ID={2}) — ToS v{3} not accepted",
                        firstName, lastName, llResp.AgentID, m_settings.ToSVersion);

                    // reason="tos" triggers the viewer's native LLFloaterTOS dialog
                    // which loads the URL in an embedded browser. The user clicks
                    // "Agree" and the viewer retries login.
                    return new LLFailedLoginResponse("tos",
                        string.Format("{0}?user={1}&version={2}",
                            m_settings.ToSUrl, llResp.AgentID, m_settings.ToSVersion),
                        "false");
                }
            }

            if (ShouldDenyByMaintenanceGate(response, out string gateReason, firstName, lastName, channel, clientVersion))
                return new LLFailedLoginResponse("presence", gateReason, "false");

            if (!m_settings.Enable)
                return response;

            if (response is FailedLoginResponse)
                return response;

            try
            {
                MacAuditRecord record = BuildRecord(firstName, lastName, clientVersion, channel, mac, id0, clientIP, response);
                m_writer.Write(record);
            }
            catch (Exception ex)
            {
                Log.Error("[NGC.MACAUDIT]: Failed to record MAC audit entry", ex);
            }

            return response;
        }
        catch (Exception ex)
        {
            Log.Error("[NGC.MACAUDIT]: Exception in Login", ex);
            throw;
        }
    }

    public Hashtable SetLevel(string firstName, string lastName, string passwd, int level, IPEndPoint clientIP)
    {
        return m_inner.SetLevel(firstName, lastName, passwd, level, clientIP);
    }

    private bool ShouldDenyByMaintenanceGate(LoginResponse response, out string reason,
        string firstName = "", string lastName = "", string channel = "", string clientVersion = "")
    {
        reason = string.Empty;

        if (!m_settings.EnableMaintenanceGate)
            return false;

        if (string.IsNullOrWhiteSpace(m_settings.MaintenanceDbConnectionString))
            return false;

        if (response is not LLLoginResponse ll || ll.AgentID.IsZero())
            return false;

        string uuid = ll.AgentID.ToString().ToLowerInvariant();

        try
        {
            using var conn = new MySqlConnection(m_settings.MaintenanceDbConnectionString);
            conn.Open();

            // 1. Maintenance mode check
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT maintenance_mode, maintenance_message, allowed_uuids FROM opensim_maintenance WHERE id = 1 LIMIT 1";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    int maintenanceMode = reader.GetInt32("maintenance_mode");
                    if (maintenanceMode == 1)
                    {
                        string message = reader.IsDBNull(reader.GetOrdinal("maintenance_message"))
                            ? "System is under maintenance. Please try again later."
                            : reader.GetString("maintenance_message").Trim();
                        string rawAllowed = reader.IsDBNull(reader.GetOrdinal("allowed_uuids"))
                            ? ""
                            : reader.GetString("allowed_uuids").Trim();

                        bool isAllowed = false;
                        if (!string.IsNullOrEmpty(rawAllowed))
                        {
                            foreach (string token in rawAllowed.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                if (token.Trim().ToLowerInvariant() == uuid)
                                {
                                    isAllowed = true;
                                    break;
                                }
                            }
                        }

                        if (!isAllowed)
                        {
                            Log.InfoFormat("[NGC.MACAUDIT]: Login BLOCKED for {0} {1} (ID={2}) — maintenance mode active",
                                firstName, lastName, ll.AgentID);
                            reason = message;
                            return true;
                        }
                    }
                }
            }

            // 2. Viewer block check
            string viewerInfo = $"{channel} {clientVersion}".Trim();

            // Check if UUID is in viewer block exceptions
            bool viewerBlockedException = false;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT allowed_uuids FROM wp_igrid_viewer_block_config WHERE id = 1 LIMIT 1";
                var result = cmd.ExecuteScalar();
                if (result != null)
                {
                    string raw = result.ToString() ?? "";
                    foreach (string token in raw.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (token.Trim().ToLowerInvariant() == uuid)
                        {
                            viewerBlockedException = true;
                            break;
                        }
                    }
                }
            }

            if (!viewerBlockedException && !string.IsNullOrEmpty(viewerInfo))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT match_pattern, match_mode, reason FROM wp_igrid_viewer_block_rules WHERE enabled = 1 ORDER BY id DESC";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string pattern = reader.GetString("match_pattern").Trim();
                    string mode = reader.GetString("match_mode");
                    string blockReason = reader.IsDBNull(reader.GetOrdinal("reason"))
                        ? "Viewer version blocked by policy"
                        : reader.GetString("reason").Trim();

                    if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(viewerInfo))
                        continue;

                    bool matched = false;
                    if (mode.Equals("equals", StringComparison.OrdinalIgnoreCase))
                        matched = string.Equals(viewerInfo, pattern, StringComparison.OrdinalIgnoreCase);
                    else if (mode.Equals("regex", StringComparison.OrdinalIgnoreCase))
                    {
                        try { matched = System.Text.RegularExpressions.Regex.IsMatch(viewerInfo, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
                        catch { /* invalid regex, skip */ }
                    }
                    else
                        matched = viewerInfo.Contains(pattern, StringComparison.OrdinalIgnoreCase);

                    if (matched)
                    {
                        Log.InfoFormat("[NGC.MACAUDIT]: Login BLOCKED for {0} {1} (ID={2}) — viewer blocked: {3}",
                            firstName, lastName, ll.AgentID, blockReason);
                        reason = blockReason;
                        return true;
                    }
                }
            }

            // 3. Local user ban check (wp_oslogin_auth)
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT banned, ban_reason FROM wp_oslogin_auth WHERE uuid = ? LIMIT 1";
                cmd.Parameters.AddWithValue("?uuid", ll.AgentID.ToString());
                using var reader = cmd.ExecuteReader();
                if (reader.Read() && reader.GetInt32("banned") == 1)
                {
                    string banReason = reader.IsDBNull(reader.GetOrdinal("ban_reason"))
                        ? "This avatar is banned"
                        : reader.GetString("ban_reason").Trim();
                    Log.InfoFormat("[NGC.MACAUDIT]: Login BLOCKED for {0} {1} (ID={2}) — locally banned: {3}",
                        firstName, lastName, ll.AgentID, banReason);
                    reason = banReason;
                    return true;
                }
            }

            // 4. HG auth ban check (wp_opensim_auth)
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT banned, COALESCE(NULLIF(comment, ''), COALESCE(NULLIF(ban_reason, ''), 'This Avatar is banned')) AS reason FROM wp_opensim_auth WHERE uuid = ? LIMIT 1";
                cmd.Parameters.AddWithValue("?uuid", ll.AgentID.ToString());
                using var reader = cmd.ExecuteReader();
                if (reader.Read() && reader.GetInt32("banned") == 1)
                {
                    string banReason = reader.GetString("reason");
                    Log.InfoFormat("[NGC.MACAUDIT]: Login BLOCKED for {0} {1} (ID={2}) — HG banned: {3}",
                        firstName, lastName, ll.AgentID, banReason);
                    reason = banReason;
                    return true;
                }
            }

            // 5. Update last login timestamp for local users
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE wp_oslogin_auth SET last_login = NOW() WHERE uuid = ?";
                cmd.Parameters.AddWithValue("?uuid", ll.AgentID.ToString());
                cmd.ExecuteNonQuery();
            }
            catch { /* best effort, don't block login */ }
        }
        catch (Exception ex)
        {
            Log.Warn("[NGC.MACAUDIT]: Maintenance gate DB check failed; allowing login", ex);
        }

        return false;
    }

    private MacAuditRecord BuildRecord(string firstName, string lastName, string clientVersion, string channel, string mac, string id0, IPEndPoint clientIp, LoginResponse response)
    {
        string username = string.Format(CultureInfo.InvariantCulture, "{0} {1}", firstName, lastName);
        string? macNorm = NormalizeMac(mac);

        string? macTail = null;
        string? macStored = macNorm;
        if (m_settings.StoreHashedMac)
        {
            macStored = HashMac(macNorm, m_settings.HashSalt);
            macTail = ExtractMacTail(macNorm);
        }

        string viewer = string.IsNullOrWhiteSpace(channel) ? clientVersion : channel + " " + clientVersion;

        MacAuditRecord record = new()
        {
            Timestamp = DateTime.UtcNow,
            UserId = ExtractUserId(response),
            Username = username,
            SessionId = ExtractSessionId(response),
            SecureSessionId = ExtractSecureSessionId(response),
            RegionHandle = ExtractRegionHandle(response),
            RegionCoordinates = ExtractRegionCoordinates(response),
            SimAddress = ExtractSimAddress(response),
            SimPort = ExtractSimPort(response),
            SimQuicHost = ExtractSimQuicHost(response),
            SimQuicPort = ExtractSimQuicPort(response),
            Viewer = viewer,
            MacAddress = macStored,
            MacTail = macTail,
            Id0 = string.IsNullOrWhiteSpace(id0) ? null : id0,
            IpAddress = m_settings.IncludeIp ? clientIp.Address.ToString() : null,
            ViewerChannel = channel,
            ViewerVersion = clientVersion
        };

        return record;
    }

    private static string? NormalizeMac(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
            return null;
        string cleaned = mac.Trim();
        return cleaned.Length == 0 ? null : cleaned.ToLowerInvariant();
    }

    private static string? HashMac(string? mac, string salt)
    {
        if (string.IsNullOrEmpty(mac))
            return null;
        using SHA256 sha = SHA256.Create();
        byte[] bytes = Encoding.UTF8.GetBytes(salt + mac);
        byte[] hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? ExtractMacTail(string? mac)
    {
        if (string.IsNullOrEmpty(mac))
            return null;
        string hex = mac.Replace(":", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        if (hex.Length <= 4)
            return hex;
        return hex.Substring(hex.Length - 4);
    }

    private static UUID? ExtractUserId(LoginResponse response)
    {
        if (response is LLLoginResponse ll)
            return ll.AgentID;
        return null;
    }

    private static UUID? ExtractSessionId(LoginResponse response)
    {
        if (response is LLLoginResponse ll)
            return ll.SessionID;
        return null;
    }

    private static UUID? ExtractSecureSessionId(LoginResponse response)
    {
        if (response is LLLoginResponse ll)
            return ll.SecureSessionID;
        return null;
    }

    private static string? ExtractSimAddress(LoginResponse response)
    {
        if (response is LLLoginResponse ll)
            return ll.SimAddress;
        return null;
    }

    private static uint? ExtractSimPort(LoginResponse response)
    {
        if (response is LLLoginResponse ll)
            return ll.SimPort;
        return null;
    }

    private static string? ExtractSimQuicHost(LoginResponse response)
    {
        if (response is LLLoginResponse ll && !string.IsNullOrWhiteSpace(ll.SimQuicHost))
            return ll.SimQuicHost;
        return null;
    }

    private static uint? ExtractSimQuicPort(LoginResponse response)
    {
        if (response is LLLoginResponse ll && ll.SimQuicPort > 0)
            return ll.SimQuicPort;
        return null;
    }

    private static ulong? ExtractRegionHandle(LoginResponse response)
    {
        if (response is LLLoginResponse ll)
            return Utils.UIntsToLong(ll.RegionX, ll.RegionY);
        return null;
    }

    private static string? ExtractRegionCoordinates(LoginResponse response)
    {
        if (response is LLLoginResponse ll)
            return string.Format(CultureInfo.InvariantCulture, "{0},{1}", ll.RegionX, ll.RegionY);
        return null;
    }
}

internal sealed class MacAuditSettings
{
    public bool Enable { get; private set; }
    public string LogPath { get; private set; } = "./logs/ngc-mac-audit.jsonl";
    public bool RotateDaily { get; private set; }
    public int MaxSizeMb { get; private set; }
    public int RetentionDays { get; private set; }
    public bool StoreHashedMac { get; private set; }
    public string HashSalt { get; private set; } = string.Empty;
    public bool IncludeIp { get; private set; }
    public string? SyslogEndpoint { get; private set; }
    public string? HttpEndpoint { get; private set; }
    public string? HttpAuthHeader { get; private set; }
    public string InnerLoginService { get; private set; } = "OpenSim.Services.LLLoginService.dll:LLLoginService";
    public bool EnableMaintenanceGate { get; private set; } = true;
    public string MaintenanceDbConnectionString { get; private set; } = string.Empty;
    public string AccessControlServiceDll { get; private set; } = "TasiaAddons.LoginSecurity.dll:TasiaAddons.LoginSecurity.AccessControlService.AccessControlService";
    public bool EnableToSCheck { get; private set; } = false;
    public int ToSVersion { get; private set; } = 1;
    public string ToSUrl { get; private set; } = string.Empty;
    public string ToSAcceptanceServiceDll { get; private set; } = "TasiaAddons.LoginSecurity.dll:TasiaAddons.LoginSecurity.Data.SQLiteToSAcceptanceData";
    public string ToSConnectionString { get; private set; } = "Data Source=Data/accesscontrol.db;Version=3";

    public static MacAuditSettings FromConfig(IConfigSource source)
    {
        MacAuditSettings settings = new();
        IConfig? config = source.Configs["NGC.MACAudit"];
        if (config is null)
        {
            settings.Enable = false;
            return settings;
        }

        settings.Enable = config.GetBoolean("Enable", false);
        settings.LogPath = config.GetString("LogPath", settings.LogPath);
        settings.RotateDaily = config.GetBoolean("RotateDaily", true);
        settings.MaxSizeMb = config.GetInt("MaxSizeMB", 256);
        settings.RetentionDays = config.GetInt("RetentionDays", 30);
        settings.StoreHashedMac = config.GetBoolean("StoreHashedMAC", true);
        settings.HashSalt = config.GetString("HashSalt", settings.HashSalt);
        settings.IncludeIp = config.GetBoolean("IncludeIP", true);
        settings.SyslogEndpoint = Normalize(config.GetString("SyslogEndpoint", string.Empty));
        settings.HttpEndpoint = Normalize(config.GetString("HTTPSinkEndpoint", string.Empty));
        settings.HttpAuthHeader = Normalize(config.GetString("HTTPSinkAuthHeader", string.Empty));
        settings.EnableMaintenanceGate = config.GetBoolean("EnableMaintenanceGate", true);
        settings.MaintenanceDbConnectionString = config.GetString("MaintenanceDbConnectionString", settings.MaintenanceDbConnectionString);
        settings.AccessControlServiceDll = config.GetString("AccessControlService", settings.AccessControlServiceDll);
        settings.EnableToSCheck = config.GetBoolean("EnableToSCheck", false);
        settings.ToSVersion = config.GetInt("ToSVersion", 1);
        settings.ToSUrl = config.GetString("ToSUrl", string.Empty);
        settings.ToSAcceptanceServiceDll = config.GetString("ToSAcceptanceService", settings.ToSAcceptanceServiceDll);
        settings.ToSConnectionString = config.GetString("ToSConnectionString", settings.ToSConnectionString);
        string? innerSetting = Normalize(config.GetString("InnerLoginService", settings.InnerLoginService));
        if (!string.IsNullOrEmpty(innerSetting))
            settings.InnerLoginService = NormalizeServiceReference(innerSetting);
        return settings;

        static string? Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return value.Trim();
        }

        static string NormalizeServiceReference(string value)
        {
            string reference = value.Trim();

            if (reference.IndexOf(".dll", StringComparison.OrdinalIgnoreCase) >= 0)
                return reference;

            int colonIndex = reference.IndexOf(':');
            if (colonIndex >= 0)
            {
                string assembly = reference[..colonIndex];
                string type = reference[(colonIndex + 1)..];
                if (assembly.IndexOf(".dll", StringComparison.OrdinalIgnoreCase) < 0)
                    assembly += ".dll";
                return string.Concat(assembly, ':', type);
            }

            int lastDot = reference.LastIndexOf('.');
            if (lastDot > 0 && lastDot < reference.Length - 1)
            {
                string assembly = reference[..lastDot] + ".dll";
                string type = reference[(lastDot + 1)..];
                return string.Concat(assembly, ':', type);
            }

            return reference;
        }
    }
}

internal sealed class MacAuditWriter
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MacAuditWriter));

    private readonly MacAuditSettings m_settings;
    private readonly SemaphoreSlim m_lock = new(1, 1);
    private readonly HttpClient m_httpClient = new();
    private DateTime m_lastRotation = DateTime.UtcNow.Date;

    public MacAuditWriter(MacAuditSettings settings)
    {
        m_settings = settings;
        if (m_settings.Enable)
            EnsureDirectory();
    }

    public void Write(MacAuditRecord record)
    {
        if (!m_settings.Enable)
            return;

        m_lock.Wait();
        try
        {
            RotateIfNeeded();
            string line = JsonSerializer.Serialize(record, MacAuditJsonContext.Default.MacAuditRecord);
            File.AppendAllText(m_settings.LogPath, line + Environment.NewLine, Encoding.UTF8);
            SetFilePermissions();
        }
        finally
        {
            m_lock.Release();
        }

        if (!string.IsNullOrEmpty(m_settings.SyslogEndpoint))
            SendSyslog(record);
        if (!string.IsNullOrEmpty(m_settings.HttpEndpoint))
            _ = SendHttpAsync(record);
    }

    private void EnsureDirectory()
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(m_settings.LogPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    private void RotateIfNeeded()
    {
        string path = m_settings.LogPath;
        if (!File.Exists(path))
        {
            m_lastRotation = DateTime.UtcNow.Date;
            return;
        }

        bool shouldRotate = false;
        FileInfo info = new(path);
        if (m_settings.RotateDaily && DateTime.UtcNow.Date > m_lastRotation)
        {
            shouldRotate = true;
            m_lastRotation = DateTime.UtcNow.Date;
        }

        long maxBytes = Math.Max(1, m_settings.MaxSizeMb) * 1024L * 1024L;
        if (info.Length >= maxBytes)
            shouldRotate = true;

        if (!shouldRotate)
            return;

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string filename = Path.GetFileNameWithoutExtension(path);
        string rotatedPath = Path.Combine(directory, $"{filename}-{timestamp}.jsonl");
        File.Move(path, rotatedPath, overwrite: true);
        m_lastRotation = DateTime.UtcNow.Date;
        PruneOldLogs();
    }

    private void PruneOldLogs()
    {
        if (m_settings.RetentionDays <= 0)
            return;

        string directory = Path.GetDirectoryName(Path.GetFullPath(m_settings.LogPath)) ?? string.Empty;
        if (!Directory.Exists(directory))
            return;

        DateTime cutoff = DateTime.UtcNow.AddDays(-m_settings.RetentionDays);
        foreach (string file in Directory.GetFiles(directory, "*.jsonl"))
        {
            try
            {
                if (File.GetCreationTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch (Exception ex)
            {
                Log.WarnFormat("[NGC.MACAUDIT]: Failed pruning log {0}: {1}", file, ex.Message);
            }
        }
    }

    private void SendSyslog(MacAuditRecord record)
    {
        try
        {
            if (!TryParseEndpoint(m_settings.SyslogEndpoint!, out string host, out int port))
                return;

            using UdpClient client = new();
            client.Connect(host, port);
            string payload = JsonSerializer.Serialize(record, MacAuditJsonContext.Default.MacAuditRecord);
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            client.Send(bytes, bytes.Length);
        }
        catch (Exception ex)
        {
            Log.WarnFormat("[NGC.MACAUDIT]: Syslog delivery failed: {0}", ex.Message);
        }
    }

    private async System.Threading.Tasks.Task SendHttpAsync(MacAuditRecord record)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Post, m_settings.HttpEndpoint);
                if (!string.IsNullOrEmpty(m_settings.HttpAuthHeader))
                    request.Headers.TryAddWithoutValidation("Authorization", m_settings.HttpAuthHeader);
                string json = JsonSerializer.Serialize(record, MacAuditJsonContext.Default.MacAuditRecord);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await m_httpClient.SendAsync(request).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return;

                Log.WarnFormat("[NGC.MACAUDIT]: HTTP sink returned {0}", response.StatusCode);
            }
            catch (Exception ex)
            {
                Log.WarnFormat("[NGC.MACAUDIT]: HTTP sink attempt {0} failed: {1}", attempt + 1, ex.Message);
            }

            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt))).ConfigureAwait(false);
        }
    }

    private static bool TryParseEndpoint(string endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrEmpty(endpoint))
            return false;

        int separator = endpoint.LastIndexOf(':');
        if (separator < 0 || separator == endpoint.Length - 1)
            return false;

        host = endpoint[..separator];
        if (!int.TryParse(endpoint[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
            return false;

        return true;
    }

    private void SetFilePermissions()
    {
        try
        {
#if NET8_0_OR_GREATER
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                File.SetUnixFileMode(m_settings.LogPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#endif
        }
        catch (Exception ex)
        {
            Log.DebugFormat("[NGC.MACAUDIT]: Failed setting file permissions: {0}", ex.Message);
        }
    }
}

internal sealed record MacAuditRecord
{
    public DateTime Timestamp { get; set; }
    public UUID? UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public UUID? SessionId { get; set; }
    public UUID? SecureSessionId { get; set; }
    public ulong? RegionHandle { get; set; }
    public string? RegionCoordinates { get; set; }
    public string? SimAddress { get; set; }
    public uint? SimPort { get; set; }
    public string? SimQuicHost { get; set; }
    public uint? SimQuicPort { get; set; }
    public string? Viewer { get; set; }
    public string? ViewerChannel { get; set; }
    public string? ViewerVersion { get; set; }
    public string? MacAddress { get; set; }
    public string? MacTail { get; set; }
    public string? Id0 { get; set; }
    public string? IpAddress { get; set; }
}

[JsonSerializable(typeof(MacAuditRecord))]
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class MacAuditJsonContext : JsonSerializerContext
{
}
