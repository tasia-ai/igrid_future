using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace TasiaAddons.WebScripts;

/// <summary>Opt-in signed LSL installer. All trust decisions live here, including the portable MOD entry point.</summary>
#if LEGACY_DISCOVERY
[Mono.Addins.Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "WebScriptsModule")]
#endif
public sealed class WebScriptsModule : INonSharedRegionModule
{
    private static readonly ILogger Log = CreateLog();
    private static ILogger CreateLog()
    {
        // Tranquillity provides ILogger; older OpenSim hosts can still capture the addon audit on stderr.
        Type provider = typeof(Util).Assembly.GetType("OpenSim.Framework.LoggerProvider");
        var method = provider?.GetMethod("CreateLogger", new[] { typeof(string) });
        return method?.Invoke(null, new object[] { "TasiaAddons.WebScripts" }) as ILogger ?? new ConsoleAuditLogger();
    }
    private sealed class ConsoleAuditLogger : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception error, Func<TState, Exception, string> format)
            => Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:O}] {level} {format(state, error)}");
    }
    private const string Marker = "Verified web script";
    private readonly Func<WebScriptSecurity, WebScriptDownloader> createDownloader;
    public WebScriptsModule() : this(policy => new WebScriptDownloader(policy)) { }
    internal WebScriptsModule(Func<WebScriptSecurity, WebScriptDownloader> factory) => createDownloader = factory;

    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<UUID, DateTimeOffset> lastOwnerRequest = new();
    private readonly HashSet<UUID> busyObjects = new();
    private HashSet<UUID> owners = new(), creators = new();
    private Scene scene;
    private IScriptModuleComms comms;
    private WebScriptDownloader downloader;
    private bool enabled;
    private bool closed;
    private string engine;

    public string Name => "WebScriptsModule";
    public Type ReplaceableInterface => null;

    public void Initialise(IConfigSource source)
    {
        IConfig config = source.Configs["WebScripts"];
        if (config == null || !config.GetBoolean("Enabled", false)) return;
        owners = ParseIds(config.GetString("AllowedOwners", ""));
        creators = ParseIds(config.GetString("AllowedScriptCreators", ""));
        if (owners.Count == 0 || creators.Count == 0)
            throw new InvalidOperationException("WebScripts requires explicit AllowedOwners and AllowedScriptCreators.");
        engine = config.GetString("ScriptEngine", "YEngine");
        if (engine is not ("YEngine" or "Phlox" or "XEngine" or "InWorldz.Phlox"))
            throw new InvalidOperationException("Choose a supported LSL engine explicitly.");
        var security = new WebScriptSecurity(config.GetString("AllowedHosts", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            File.ReadAllText(config.GetString("PublisherPublicKeyFile", "")));
        downloader = createDownloader(security);
        enabled = true;
    }

    private static HashSet<UUID> ParseIds(string text) => text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(value => UUID.TryParse(value, out UUID id) && id != UUID.Zero ? id : throw new ArgumentException("Use nonzero UUIDs in WebScripts grants.")).ToHashSet();

    public void AddRegion(Scene value) { if (enabled) scene = value; }
    public void RegionLoaded(Scene value)
    {
        if (!enabled) return;
        if (!value.RequestModuleInterfaces<IScriptModule>().Any(module => module.ScriptEngineName == engine))
            throw new InvalidOperationException("WebScripts configured LSL engine is not loaded.");
        comms = value.RequestModuleInterface<IScriptModuleComms>() ?? throw new InvalidOperationException("WebScripts requires ScriptModuleComms.");
        comms.RegisterScriptInvocations(this);
        Log.LogInformation("[WEB SCRIPTS]: Enabled signed LSL installation with explicit owner, creator and publisher grants");
    }
    public void RemoveRegion(Scene value) => Close();
    public void Close()
    {
        lock (gate) { closed = true; enabled = false; lifetime.Cancel(); }
        // A registered callback remains fail-closed after removal; never restart this instance.
        downloader?.Dispose();
    }

    private SceneObjectPart Authorize(UUID hostID, UUID scriptID, UUID expectedOwner = default, UUID expectedAsset = default)
    {
        if (!enabled || closed || scene == null) throw new InvalidOperationException("WebScripts is disabled.");
        SceneObjectPart part = scene.GetSceneObjectPart(hostID);
        TaskInventoryItem caller = part?.Inventory.GetInventoryItem(scriptID);
        if (part == null || part.ParentGroup.IsDeleted || part.ParentGroup.inTransit || caller == null ||
            caller.Type != (int)AssetType.LSLText || caller.OwnerID != part.OwnerID || !owners.Contains(part.OwnerID) || !creators.Contains(caller.CreatorID) ||
            (expectedOwner != UUID.Zero && part.OwnerID != expectedOwner) || (expectedAsset != UUID.Zero && caller.AssetID != expectedAsset) ||
            !scene.Permissions.CanEditObjectInventory(hostID, part.OwnerID) ||
            !scene.Permissions.CanCreateObjectInventory((int)InventoryType.LSL, hostID, part.OwnerID))
            throw new InvalidOperationException("WebScripts caller is not authorized or changed while downloading.");
        if (part.Inventory.GetInventoryItems().Count(item => item.Description == Marker) >= 4)
            throw new InvalidOperationException("WebScripts limit of four installed scripts per prim reached.");
        return part;
    }

    [ScriptInvocation]
    public UUID maRunScriptFromWeb(UUID hostID, UUID scriptID, string url)
    {
        lock (gate)
        {
            SceneObjectPart part = Authorize(hostID, scriptID);
            if (busyObjects.Count >= 4 || busyObjects.Contains(hostID)) throw new InvalidOperationException("WebScripts download capacity reached.");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (UUID owner in lastOwnerRequest.Where(pair => now - pair.Value > TimeSpan.FromMinutes(1)).Select(pair => pair.Key).ToArray()) lastOwnerRequest.Remove(owner);
            if (lastOwnerRequest.ContainsKey(part.OwnerID)) throw new InvalidOperationException("WebScripts permits one request per owner per minute.");
            lastOwnerRequest[part.OwnerID] = now;
            busyObjects.Add(hostID);
            UUID request = UUID.Random(), ownerID = part.OwnerID, assetID = part.Inventory.GetInventoryItem(scriptID).AssetID;
            _ = InstallAsync(hostID, scriptID, ownerID, assetID, request, url);
            return request;
        }
    }

    private async Task InstallAsync(UUID hostID, UUID callerID, UUID ownerID, UUID callerAsset, UUID requestID, string url)
    {
        try
        {
            string source = await downloader.DownloadAsync(url, lifetime.Token).ConfigureAwait(false);
            lock (gate)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                SceneObjectPart part = Authorize(hostID, callerID, ownerID, callerAsset);
                var asset = new AssetBase(UUID.Random(), "WebScript " + requestID, (sbyte)AssetType.LSLText, ownerID.ToString()) {
                    Data = System.Text.Encoding.UTF8.GetBytes(source), Description = Marker };
                var item = new TaskInventoryItem {
                    Name = asset.Name, Description = Marker, Type = (int)AssetType.LSLText, InvType = (int)InventoryType.LSL,
                    OwnerID = ownerID, CreatorID = ownerID, GroupID = part.GroupID,
                    BasePermissions = (uint)PermissionMask.All, CurrentPermissions = (uint)PermissionMask.All,
                    NextPermissions = (uint)(PermissionMask.Copy | PermissionMask.Modify), EveryonePermissions = 0,
                    GroupPermissions = 0, AssetID = asset.FullID, CreationDate = (uint)Util.UnixTimeSinceEpoch(),
                    PermsGranter = UUID.Zero, PermsMask = 0 };
                item.ResetIDs(part.UUID);
                if (!scene.Permissions.CanRunScript(item, part)) throw new InvalidOperationException("Parcel or region script policy denies this script.");
                string stored = scene.AssetService.Store(asset);
                if (string.IsNullOrEmpty(stored)) throw new InvalidOperationException("Script asset storage failed.");
                // Asset storage may cross the network; recheck grants and ownership afterwards.
                part = Authorize(hostID, callerID, ownerID, callerAsset);
                part.Inventory.AddInventoryItem(item, false);
                try {
                    if (!part.Inventory.CreateScriptInstance(item, 0, false, engine, 0))
                        throw new InvalidOperationException("Script start was denied.");
                    // Resume this new script only; do not restart other scripts in the object.
                    var selected = scene.RequestModuleInterfaces<IScriptModule>().Single(module => module.ScriptEngineName == engine);
                    if (!selected.ResumeScript(item.ItemID))
                        throw new InvalidOperationException("LSL engine did not accept the script instance.");
                    part.ParentGroup.InvalidateDeepEffectivePerms();
                }
                catch { part.Inventory.RemoveInventoryItem(item.ItemID); throw; }
                // Compilation is asynchronous: do not claim successful execution before the engine reports it.
                comms.DispatchReply(callerID, 202, "verified; compilation queued; item=" + item.ItemID, requestID.ToString());
                Log.LogInformation("[WEB SCRIPTS]: Verified installation queued request={Request} owner={Owner} object={Object}", requestID, ownerID, hostID);
            }
        }
        catch (Exception error)
        {
            lock (gate)
            {
                if (!closed && scene?.GetSceneObjectPart(hostID)?.OwnerID == ownerID)
                    comms?.DispatchReply(callerID, 403, "Web script rejected; inspect the region log", requestID.ToString());
            }
            // Do not log URLs, downloaded content, response bodies or secrets.
            Log.LogWarning("[WEB SCRIPTS]: Request {Request} rejected ({ErrorType})", requestID, error.GetType().Name);
        }
        finally { lock (gate) busyObjects.Remove(hostID); }
    }
}
