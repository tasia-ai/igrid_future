using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;

namespace TasiaAddon.AlertNotifications;

public class AlertNotificationConnector : ServiceConnector
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AlertNotificationConnector));

    private readonly AlertNotificationService m_service;

    public AlertNotificationConnector(IConfigSource config, IHttpServer server, string configName) : base(config, server, configName)
    {
        string section = string.IsNullOrEmpty(configName) ? "AlertService" : configName;
        IConfig alertConfig = config.Configs[section] ?? throw new Exception($"No section {section} in config file");

        string estateService = alertConfig.GetString("EstateService", string.Empty);
        if (string.IsNullOrWhiteSpace(estateService))
            throw new Exception("No EstateService specified in config");

        string gridService = alertConfig.GetString("GridService", string.Empty);
        if (string.IsNullOrWhiteSpace(gridService))
            throw new Exception("No GridService specified in config");

        string estateToken = alertConfig.GetString("EstateToken", string.Empty);
        if (string.IsNullOrEmpty(estateToken))
            Log.Warn("[ALERT SERVICE]: EstateToken missing from configuration. Remote estate requests will likely fail.");

        string defaultFromName = alertConfig.GetString("DefaultFromName", "System");
        string fromIdSetting = alertConfig.GetString("DefaultFromID", UUID.Zero.ToString());
        UUID defaultFromId = UUID.Zero;
        if (!UUID.TryParse(fromIdSetting, out defaultFromId))
        {
            Log.WarnFormat("[ALERT SERVICE]: Invalid DefaultFromID '{0}'. Using UUID.Zero instead.", fromIdSetting);
            defaultFromId = UUID.Zero;
        }

        string scopeSetting = alertConfig.GetString("DefaultScopeID", UUID.Zero.ToString());
        UUID defaultScopeId = UUID.Zero;
        if (!UUID.TryParse(scopeSetting, out defaultScopeId))
        {
            Log.WarnFormat("[ALERT SERVICE]: Invalid DefaultScopeID '{0}'. Using UUID.Zero instead.", scopeSetting);
            defaultScopeId = UUID.Zero;
        }

        object[] args = new object[] { config };
        IEstateDataService estateDataService = ServerUtils.LoadPlugin<IEstateDataService>(estateService, args)
            ?? throw new Exception($"Failed to load estate data service {estateService}");
        IGridService grid = ServerUtils.LoadPlugin<IGridService>(gridService, args)
            ?? throw new Exception($"Failed to load grid service {gridService}");

        m_service = new AlertNotificationService(estateDataService, grid, estateToken, defaultScopeId, defaultFromId, defaultFromName);

        IServiceAuth auth = ServiceAuth.Create(config, section);
        server.AddStreamHandler(new AlertNotificationHandler(m_service, auth));
        Log.Info("[ALERT SERVICE]: /alert endpoint enabled");
    }
}

public class AlertNotificationHandler : BaseStreamHandler
{
    private readonly AlertNotificationService m_service;

    public AlertNotificationHandler(AlertNotificationService service, IServiceAuth? auth) : base("POST", "/alert", auth)
    {
        m_service = service ?? throw new ArgumentNullException(nameof(service));
    }

    protected override byte[] ProcessRequest(string path, Stream requestData, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        string body;
        using (StreamReader sr = new StreamReader(requestData))
            body = sr.ReadToEnd();
        body = body.Trim();

        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(body))
        {
            string contentType = httpRequest.Headers["content-type"] ?? httpRequest.Headers["Content-Type"] ?? string.Empty;
            if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
                PopulateFromJson(values, body);
            else
                PopulateFromQuery(values, body);
        }

        foreach (KeyValuePair<string, string> pair in httpRequest.QueryAsDictionary)
        {
            string key = pair.Key;
            if (!string.IsNullOrEmpty(key) && !values.ContainsKey(key))
                values[key] = pair.Value ?? string.Empty;
        }

        AlertNotificationRequest request = AlertNotificationRequest.FromDictionary(values);
        if (!request.IsValid(out string error))
        {
            httpResponse.StatusCode = (int)System.Net.HttpStatusCode.BadRequest;
            return Util.ResultFailureMessage(error);
        }

        bool result = m_service.TrySendAlert(request, out string? failureReason);
        if (!result)
        {
            httpResponse.StatusCode = (int)System.Net.HttpStatusCode.BadGateway;
            return Util.ResultFailureMessage(failureReason ?? "Failed to deliver alert");
        }

        httpResponse.StatusCode = (int)System.Net.HttpStatusCode.OK;
        return Util.sucessResultSuccess;
    }

    private static void PopulateFromQuery(IDictionary<string, string> target, string body)
    {
        Dictionary<string, object> parsed = ServerUtils.ParseQueryString(body);
        foreach (KeyValuePair<string, object> pair in parsed)
            target[pair.Key] = pair.Value?.ToString() ?? string.Empty;
    }

    private static void PopulateFromJson(IDictionary<string, string> target, string body)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            target[prop.Name] = prop.Value.ToString();
    }
}

public record struct AlertNotificationRequest
(
    int? EstateId,
    UUID? RegionId,
    UUID? FromId,
    string? FromName,
    string? Message,
    UUID? ScopeId
)
{
    public static AlertNotificationRequest FromDictionary(IDictionary<string, string> values)
    {
        int? estateId = null;
        if (values.TryGetValue("EstateID", out string? estateValue) && !string.IsNullOrWhiteSpace(estateValue) && int.TryParse(estateValue, out int estate))
            estateId = estate;
        else if (values.TryGetValue("estate_id", out estateValue) && int.TryParse(estateValue, out estate))
            estateId = estate;

        UUID? regionId = null;
        if (values.TryGetValue("RegionID", out string? regionValue) && UUID.TryParse(regionValue, out UUID region))
            regionId = region;
        else if (values.TryGetValue("region_id", out regionValue) && UUID.TryParse(regionValue, out region))
            regionId = region;

        UUID? fromId = null;
        if (values.TryGetValue("FromID", out string? fromIdValue) && UUID.TryParse(fromIdValue, out UUID parsedFromId))
            fromId = parsedFromId;
        else if (values.TryGetValue("from_id", out fromIdValue) && UUID.TryParse(fromIdValue, out parsedFromId))
            fromId = parsedFromId;

        string? fromName = null;
        if (values.TryGetValue("FromName", out string? fromNameValue) && !string.IsNullOrEmpty(fromNameValue))
            fromName = fromNameValue;
        else if (values.TryGetValue("from_name", out fromNameValue) && !string.IsNullOrEmpty(fromNameValue))
            fromName = fromNameValue;

        string? message = null;
        if (values.TryGetValue("Message", out string? messageValue) && !string.IsNullOrWhiteSpace(messageValue))
            message = messageValue;
        else if (values.TryGetValue("message", out messageValue) && !string.IsNullOrWhiteSpace(messageValue))
            message = messageValue;

        UUID? scopeId = null;
        if (values.TryGetValue("ScopeID", out string? scopeValue) && UUID.TryParse(scopeValue, out UUID scope))
            scopeId = scope;
        else if (values.TryGetValue("scope_id", out scopeValue) && UUID.TryParse(scopeValue, out scope))
            scopeId = scope;

        return new AlertNotificationRequest(estateId, regionId, fromId, fromName, message, scopeId);
    }

    public bool IsValid(out string error)
    {
        if (EstateId is null && RegionId is null)
        {
            error = "EstateID or RegionID must be provided.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Message))
        {
            error = "Message is required.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

public class AlertNotificationService
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AlertNotificationService));

    private readonly IEstateDataService m_estateService;
    private readonly IGridService m_gridService;
    private readonly string m_token;
    private readonly UUID m_defaultScopeId;
    private readonly UUID m_defaultFromId;
    private readonly string m_defaultFromName;

    public AlertNotificationService()
        : this(null!, null!, string.Empty, UUID.Zero, UUID.Zero, "System")
    {
    }

    public AlertNotificationService(IEstateDataService estateService, IGridService gridService, string token, UUID defaultScopeId, UUID defaultFromId, string defaultFromName)
    {
        m_estateService = estateService ?? throw new ArgumentNullException(nameof(estateService));
        m_gridService = gridService ?? throw new ArgumentNullException(nameof(gridService));
        m_token = token ?? string.Empty;
        m_defaultScopeId = defaultScopeId;
        m_defaultFromId = defaultFromId;
        m_defaultFromName = string.IsNullOrWhiteSpace(defaultFromName) ? "System" : defaultFromName;
    }

    public bool TrySendAlert(AlertNotificationRequest request, out string? failureReason)
    {
        failureReason = null;

        int estateId = request.EstateId ?? ResolveEstateId(request.RegionId);
        if (estateId <= 0)
        {
            failureReason = "Unable to resolve estate.";
            return false;
        }

        string message = request.Message!.Trim();
        UUID fromId = request.FromId ?? m_defaultFromId;
        string fromName = string.IsNullOrWhiteSpace(request.FromName) ? m_defaultFromName : request.FromName!.Trim();
        UUID scopeId = request.ScopeId ?? m_defaultScopeId;

        List<UUID> regions = m_estateService.GetRegions(estateId);
        if (regions == null || regions.Count == 0)
        {
            failureReason = "No regions found for estate.";
            return false;
        }

        Dictionary<string, object> payload = new()
        {
            ["METHOD"] = "estate_message",
            ["TOKEN"] = m_token,
            ["EstateID"] = estateId.ToString(),
            ["FromID"] = fromId.ToString(),
            ["FromName"] = fromName,
            ["Message"] = message
        };

        string body = ServerUtils.BuildQueryString(payload);

        bool delivered = false;
        HashSet<string> attempted = new(StringComparer.OrdinalIgnoreCase);

        foreach (UUID regionId in regions)
        {
            OpenSim.Services.Interfaces.GridRegion region = m_gridService.GetRegionByUUID(scopeId, regionId);
            if (region == null)
            {
                Log.WarnFormat("[ALERT SERVICE]: Unable to find region {0} for estate {1}.", regionId, estateId);
                continue;
            }

            string serverUri = region.ServerURI;
            if (string.IsNullOrEmpty(serverUri))
            {
                Log.WarnFormat("[ALERT SERVICE]: Region {0} does not expose a server URI.", region.RegionName);
                continue;
            }

            if (!attempted.Add(serverUri))
                continue;

            try
            {
                string reply = SynchronousRestFormsRequester.MakeRequest("POST", serverUri + "estate", body, 10000);
                if (!string.IsNullOrEmpty(reply) && reply.IndexOf("true", StringComparison.OrdinalIgnoreCase) >= 0)
                    delivered = true;
                else
                    Log.WarnFormat("[ALERT SERVICE]: Estate alert to {0} returned unexpected response: {1}", serverUri, reply);
            }
            catch (Exception ex)
            {
                Log.ErrorFormat("[ALERT SERVICE]: Failed sending alert to region {0} ({1}): {2}", region.RegionName, serverUri, ex.Message);
            }
        }

        if (!delivered)
        {
            failureReason = "No estate regions accepted the alert.";
            return false;
        }

        return true;
    }

    private int ResolveEstateId(UUID? regionId)
    {
        if (regionId is null || regionId == UUID.Zero)
            return 0;

        EstateSettings settings = m_estateService.LoadEstateSettings(regionId.Value, false);
        return settings != null ? (int)settings.EstateID : 0;
    }
}
