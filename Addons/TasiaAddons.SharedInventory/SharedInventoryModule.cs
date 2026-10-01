using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Mono.Addins;

[assembly: Addin("TasiaAddons.SharedInventory", "1.2.0")]
[assembly: AddinDescription("Shared RW inventory synchronizer with moderation")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace TasiaAddons.SharedInventory;

[Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "SharedInventoryModule")]
public class SharedInventoryModule : ISharedRegionModule
{
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private sealed class InventoryLookupEntry
    {
        public UUID Id;
        public string Name;
        public string Path;
    }

    private enum SyncScope
    {
        Full,
        Uploads,
        Mirror
    }

    private enum DuplicatePolicy
    {
        Skip,
        Rename,
        Replace
    }

    private sealed class SyncStats
    {
        public int UsersProcessed;
        public int UploadItemsMoved;
        public int UploadFoldersMoved;
        public int MirrorItemsCopied;
        public int MirrorFoldersCreated;
        public int ItemsSkipped;
        public int ItemsReplaced;
        public int ItemsRenamed;
        public int Errors;

        public void Add(SyncStats other)
        {
            if (other == null)
                return;

            UsersProcessed += other.UsersProcessed;
            UploadItemsMoved += other.UploadItemsMoved;
            UploadFoldersMoved += other.UploadFoldersMoved;
            MirrorItemsCopied += other.MirrorItemsCopied;
            MirrorFoldersCreated += other.MirrorFoldersCreated;
            ItemsSkipped += other.ItemsSkipped;
            ItemsReplaced += other.ItemsReplaced;
            ItemsRenamed += other.ItemsRenamed;
            Errors += other.Errors;
        }
    }

    private readonly object m_sync = new();
    private readonly List<Scene> m_scenes = new();
    private readonly HashSet<UUID> m_managerUuids = new();
    private readonly SemaphoreSlim m_syncGate = new(1, 1);

    private bool m_enabled;
    private bool m_syncOnLogin = true;
    private bool m_regionCommandEnabled = true;
    private bool m_verboseLogging;
    private bool m_dryRun;
    private bool m_requireManagerApproval;
    private bool m_managerChatEnabled = true;
    private bool m_publishLibrarySnapshot;
    private int m_syncIntervalSeconds = 300;
    private int m_managerChatChannel = 77;
    private string m_managerChatPrefix = "sharedinv";

    private DuplicatePolicy m_uploadDuplicatePolicy = DuplicatePolicy.Rename;
    private DuplicatePolicy m_mirrorDuplicatePolicy = DuplicatePolicy.Skip;

    private UUID m_sharedAvatarId = UUID.Zero;
    private string m_sharedRootFolderName = "Shared Inventory";
    private string m_pendingFolderName = "Pending Approval";
    private string m_publishFolderName = "Library Publish";
    private string m_userRootFolderName = "Shared RW";
    private string m_userUploadFolderName = "Upload";
    private string m_userBrowseFolderName = "Shared";

    private Timer m_timer;
    private bool m_commandRegistered;
    private long m_syncRuns;
    private long m_syncUsers;
    private long m_syncErrors;

    public string Name => "SharedInventoryModule";
    public Type ReplaceableInterface => null;

    public void Initialise(IConfigSource source)
    {
        IConfig cfg = source.Configs["SharedInventory"];
        if (cfg == null)
        {
            m_enabled = false;
            return;
        }

        m_enabled = cfg.GetBoolean("Enabled", false);
        if (!m_enabled)
            return;

        string avatarRaw = cfg.GetString("SharedAvatarUUID", string.Empty).Trim();
        if (!UUID.TryParse(avatarRaw, out m_sharedAvatarId) || m_sharedAvatarId.IsZero())
        {
            Log.Warn("[SHARED INVENTORY]: Invalid SharedAvatarUUID; module disabled.");
            m_enabled = false;
            return;
        }

        m_sharedRootFolderName = NonEmpty(cfg.GetString("RootFolderName", m_sharedRootFolderName), "Shared Inventory");
        m_pendingFolderName = NonEmpty(cfg.GetString("PendingFolderName", m_pendingFolderName), "Pending Approval");
        m_publishFolderName = NonEmpty(cfg.GetString("PublishFolderName", m_publishFolderName), "Library Publish");
        m_userRootFolderName = NonEmpty(cfg.GetString("UserRootFolderName", m_userRootFolderName), "Shared RW");
        m_userUploadFolderName = NonEmpty(cfg.GetString("UserUploadFolderName", m_userUploadFolderName), "Upload");
        m_userBrowseFolderName = NonEmpty(cfg.GetString("UserBrowseFolderName", m_userBrowseFolderName), "Shared");

        m_syncIntervalSeconds = Math.Max(60, cfg.GetInt("SyncIntervalSeconds", 300));
        m_syncOnLogin = cfg.GetBoolean("SyncOnLogin", true);
        m_regionCommandEnabled = cfg.GetBoolean("RegionCommandEnabled", true);
        m_verboseLogging = cfg.GetBoolean("VerboseLogging", false);
        m_dryRun = cfg.GetBoolean("DryRun", false);
        m_requireManagerApproval = cfg.GetBoolean("RequireManagerApproval", false);
        m_publishLibrarySnapshot = cfg.GetBoolean("PublishLibrarySnapshot", false);
        m_managerChatEnabled = cfg.GetBoolean("ManagerChatEnabled", true);
        m_managerChatChannel = cfg.GetInt("ManagerChatChannel", 77);
        m_managerChatPrefix = NonEmpty(cfg.GetString("ManagerChatPrefix", "sharedinv"), "sharedinv").ToLowerInvariant();

        m_uploadDuplicatePolicy = ParseDuplicatePolicy(cfg.GetString("UploadDuplicatePolicy", "rename"), DuplicatePolicy.Rename);
        m_mirrorDuplicatePolicy = ParseDuplicatePolicy(cfg.GetString("MirrorDuplicatePolicy", "skip"), DuplicatePolicy.Skip);

        m_managerUuids.Clear();
        ParseUuidList(cfg.GetString("ManagerUUIDs", string.Empty), m_managerUuids);

        Log.InfoFormat(
            "[SHARED INVENTORY]: Enabled. SharedAvatar={0}, interval={1}s, syncOnLogin={2}, managers={3}, dryRun={4}, moderation={5}",
            m_sharedAvatarId,
            m_syncIntervalSeconds,
            m_syncOnLogin,
            m_managerUuids.Count,
            m_dryRun,
            m_requireManagerApproval);
    }

    public void AddRegion(Scene scene)
    {
        if (!m_enabled)
            return;

        lock (m_sync)
        {
            if (!m_scenes.Contains(scene))
                m_scenes.Add(scene);
        }

        scene.EventManager.OnNewClient += OnNewClient;
        if (m_managerChatEnabled)
            scene.EventManager.OnChatFromClient += OnChatFromClient;

        if (m_regionCommandEnabled && !m_commandRegistered)
        {
            MainConsole.Instance.Commands.AddCommand(
                "Regions",
                false,
                "sharedinventory",
                "sharedinventory <sync|set|moderate|stats> ...",
                "Run shared inventory controls",
                "Commands: sync, set, moderate, stats",
                HandleConsoleCommand);

            m_commandRegistered = true;
        }

        EnsureTimerStarted();
    }

    public void RegionLoaded(Scene scene)
    {
    }

    public void RemoveRegion(Scene scene)
    {
        if (!m_enabled)
            return;

        scene.EventManager.OnNewClient -= OnNewClient;
        if (m_managerChatEnabled)
            scene.EventManager.OnChatFromClient -= OnChatFromClient;

        lock (m_sync)
        {
            m_scenes.Remove(scene);
            if (m_scenes.Count == 0)
                StopTimer();
        }
    }

    public void Close()
    {
        StopTimer();

        lock (m_sync)
        {
            foreach (Scene scene in m_scenes)
            {
                scene.EventManager.OnNewClient -= OnNewClient;
                if (m_managerChatEnabled)
                    scene.EventManager.OnChatFromClient -= OnChatFromClient;
            }

            m_scenes.Clear();
        }

        m_syncGate.Dispose();
    }

    public void PostInitialise()
    {
    }

    private void OnNewClient(IClientAPI client)
    {
        if (!m_enabled || !m_syncOnLogin || client == null)
            return;

        _ = Task.Run(async () =>
        {
            await SyncUsersAsync(new HashSet<UUID> { client.AgentId }, "login", SyncScope.Full, m_dryRun).ConfigureAwait(false);
        });
    }

    private void OnChatFromClient(object sender, OSChatMessage chat)
    {
        if (!m_enabled || !m_managerChatEnabled || chat == null)
            return;

        if (chat.Channel != m_managerChatChannel)
            return;

        IClientAPI client = sender as IClientAPI;
        if (client == null || !m_managerUuids.Contains(client.AgentId))
            return;

        string msg = (chat.Message ?? string.Empty).Trim();
        if (!msg.StartsWith(m_managerChatPrefix, StringComparison.OrdinalIgnoreCase))
            return;

        string cmd = msg.Length == m_managerChatPrefix.Length
            ? string.Empty
            : msg.Substring(m_managerChatPrefix.Length).TrimStart();

        if (string.IsNullOrEmpty(cmd))
        {
            client.SendAgentAlertMessage("SharedInv: commands sync|approve|reject|stats", false);
            return;
        }

        string[] parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;

        switch (parts[0].ToLowerInvariant())
        {
            case "sync":
                SyncScope scope = parts.Length > 1 ? ParseSyncScope(parts[1], SyncScope.Full) : SyncScope.Full;
                HashSet<UUID> target = parts.Length > 2 && parts[2].Equals("all", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : new HashSet<UUID> { client.AgentId };

                _ = Task.Run(async () =>
                {
                    await SyncUsersAsync(target, "manager-chat", scope, m_dryRun).ConfigureAwait(false);
                });

                client.SendAgentAlertMessage($"SharedInv: sync requested ({scope})", false);
                return;

            case "approve":
            case "reject":
                if (parts.Length < 3)
                {
                    client.SendAgentAlertMessage("SharedInv: use approve|reject item|folder <uuid|name|path>", false);
                    return;
                }

                bool approve = parts[0].Equals("approve", StringComparison.OrdinalIgnoreCase);
                string type = parts[1].ToLowerInvariant();
                string targetSpec = NormalizeLookupValue(string.Join(" ", parts.Skip(2)));
                if (string.IsNullOrWhiteSpace(targetSpec))
                {
                    client.SendAgentAlertMessage("SharedInv: use approve|reject item|folder <uuid|name|path>", false);
                    return;
                }

                bool ok = ModerateBySpec(type, targetSpec, approve, out string reason);
                client.SendAgentAlertMessage(ok ? "SharedInv: moderation done" : "SharedInv: " + reason, false);
                return;

            case "stats":
                client.SendAgentAlertMessage(GetStatsMessage(), false);
                return;
        }
    }

    private void HandleConsoleCommand(string module, string[] args)
    {
        if (!m_enabled)
            return;

        if (args == null || args.Length < 2)
        {
            MainConsole.Instance.Output("Usage: sharedinventory <sync|set|moderate|stats>");
            return;
        }

        string op = args[1].Trim().ToLowerInvariant();
        switch (op)
        {
            case "sync":
                HandleConsoleSync(args);
                return;

            case "set":
                HandleConsoleSet(args);
                return;

            case "moderate":
                HandleConsoleModerate(args);
                return;

            case "stats":
                MainConsole.Instance.Output(GetStatsMessage());
                return;

            default:
                MainConsole.Instance.Output("Usage: sharedinventory <sync|set|moderate|stats>");
                return;
        }
    }

    private void HandleConsoleSync(string[] args)
    {
        // sharedinventory sync [full|uploads|mirror] [all|online|user <uuid>] [dryrun]
        SyncScope scope = SyncScope.Full;
        if (args.Length >= 3)
            scope = ParseSyncScope(args[2], SyncScope.Full);

        bool dryRun = m_dryRun;
        HashSet<UUID> targetUsers = null;

        if (args.Length >= 4)
        {
            string target = args[3].ToLowerInvariant();
            if (target == "user")
            {
                if (args.Length < 5 || !UUID.TryParse(args[4], out UUID userId) || userId.IsZero())
                {
                    MainConsole.Instance.Output("Usage: sharedinventory sync [full|uploads|mirror] user <avatar-uuid> [dryrun]");
                    return;
                }

                targetUsers = new HashSet<UUID> { userId };
            }
        }

        if (args.Any(a => a.Equals("dryrun", StringComparison.OrdinalIgnoreCase)))
            dryRun = true;

        _ = Task.Run(async () =>
        {
            await SyncUsersAsync(targetUsers, "console", scope, dryRun).ConfigureAwait(false);
        });

        MainConsole.Instance.Output($"[SHARED INVENTORY] sync requested scope={scope}, target={(targetUsers == null ? "online" : "user")}, dryRun={dryRun}");
    }

    private void HandleConsoleSet(string[] args)
    {
        // sharedinventory set dryrun on|off
        // sharedinventory set verbose on|off
        if (args.Length < 4)
        {
            MainConsole.Instance.Output("Usage: sharedinventory set dryrun|verbose on|off");
            return;
        }

        string key = args[2].ToLowerInvariant();
        bool enabled = args[3].Equals("on", StringComparison.OrdinalIgnoreCase) || args[3].Equals("true", StringComparison.OrdinalIgnoreCase);

        switch (key)
        {
            case "dryrun":
                m_dryRun = enabled;
                MainConsole.Instance.Output($"[SHARED INVENTORY] DryRun={m_dryRun}");
                return;

            case "verbose":
                m_verboseLogging = enabled;
                MainConsole.Instance.Output($"[SHARED INVENTORY] VerboseLogging={m_verboseLogging}");
                return;

            default:
                MainConsole.Instance.Output("Usage: sharedinventory set dryrun|verbose on|off");
                return;
        }
    }

    private void HandleConsoleModerate(string[] args)
    {
        // sharedinventory moderate <manager-uuid> approve|reject item|folder <uuid|name|path>
        if (args.Length < 6 || !UUID.TryParse(args[2], out UUID manager) || manager.IsZero())
        {
            MainConsole.Instance.Output("Usage: sharedinventory moderate <manager-uuid> approve|reject item|folder <uuid|name|path>");
            return;
        }

        if (!m_managerUuids.Contains(manager))
        {
            MainConsole.Instance.Output("[SHARED INVENTORY] manager UUID not authorized");
            return;
        }

        if (!args[3].Equals("approve", StringComparison.OrdinalIgnoreCase) &&
            !args[3].Equals("reject", StringComparison.OrdinalIgnoreCase))
        {
            MainConsole.Instance.Output("Usage: sharedinventory moderate <manager-uuid> approve|reject item|folder <uuid|name|path>");
            return;
        }

        bool approve = args[3].Equals("approve", StringComparison.OrdinalIgnoreCase);
        string type = args[4].ToLowerInvariant();
        string targetSpec = NormalizeLookupValue(string.Join(" ", args.Skip(5)));
        if (string.IsNullOrWhiteSpace(targetSpec))
        {
            MainConsole.Instance.Output("Usage: sharedinventory moderate <manager-uuid> approve|reject item|folder <uuid|name|path>");
            return;
        }

        bool ok = ModerateBySpec(type, targetSpec, approve, out string reason);
        MainConsole.Instance.Output(ok ? "[SHARED INVENTORY] moderation done" : "[SHARED INVENTORY] " + reason);
    }

    private bool ModerateBySpec(string type, string targetSpec, bool approve, out string reason)
    {
        reason = string.Empty;
        string normalized = NormalizeLookupValue(targetSpec);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            reason = "missing moderation target";
            return false;
        }

        if (UUID.TryParse(normalized, out UUID asUuid) && asUuid.IsNotZero())
            return ModerateByUuid(type, asUuid, approve, out reason);

        Scene scene = GetPrimaryScene();
        if (scene?.InventoryService == null)
        {
            reason = "inventory service unavailable";
            return false;
        }

        InventoryFolderBase sharedRoot = EnsureSharedRootFolder(scene);
        InventoryFolderBase pending = EnsureNamedChild(scene, m_sharedAvatarId, sharedRoot?.ID ?? UUID.Zero, m_pendingFolderName);
        if (sharedRoot == null || pending == null)
        {
            reason = "shared root/pending folder missing";
            return false;
        }

        if (!TryResolvePendingTarget(scene, type, pending, normalized, out UUID targetId, out reason))
            return false;

        return ModerateByUuid(type, targetId, approve, out reason);
    }

    private bool ModerateByUuid(string type, UUID targetId, bool approve, out string reason)
    {
        reason = string.Empty;
        Scene scene = GetPrimaryScene();
        if (scene?.InventoryService == null)
        {
            reason = "inventory service unavailable";
            return false;
        }

        InventoryFolderBase sharedRoot = EnsureSharedRootFolder(scene);
        InventoryFolderBase pending = EnsureNamedChild(scene, m_sharedAvatarId, sharedRoot?.ID ?? UUID.Zero, m_pendingFolderName);
        if (sharedRoot == null || pending == null)
        {
            reason = "shared root/pending folder missing";
            return false;
        }

        if (type == "item")
        {
            InventoryItemBase item = scene.InventoryService.GetItem(m_sharedAvatarId, targetId);
            if (item == null)
            {
                reason = "item not found";
                return false;
            }

            if (!IsWithinSubtree(scene, item.Folder, pending.ID))
            {
                reason = "item is not in pending subtree";
                return false;
            }

            if (!approve)
                return scene.InventoryService.DeleteItems(m_sharedAvatarId, new List<UUID> { targetId });

            item.Folder = sharedRoot.ID;
            return scene.InventoryService.MoveItems(m_sharedAvatarId, new List<InventoryItemBase> { item });
        }

        if (type == "folder")
        {
            InventoryFolderBase folder = scene.InventoryService.GetFolder(m_sharedAvatarId, targetId);
            if (folder == null)
            {
                reason = "folder not found";
                return false;
            }

            if (!IsWithinSubtree(scene, folder.ID, pending.ID))
            {
                reason = "folder is not in pending subtree";
                return false;
            }

            if (!approve)
                return scene.InventoryService.DeleteFolders(m_sharedAvatarId, new List<UUID> { targetId });

            folder.ParentID = sharedRoot.ID;
            return scene.InventoryService.MoveFolder(folder);
        }

        reason = "type must be item|folder";
        return false;
    }

    private bool TryResolvePendingTarget(Scene scene, string type, InventoryFolderBase pending, string targetSpec, out UUID targetId, out string reason)
    {
        targetId = UUID.Zero;
        reason = string.Empty;

        string lookup = NormalizeLookupValue(targetSpec);
        if (string.IsNullOrWhiteSpace(lookup))
        {
            reason = "target is empty";
            return false;
        }

        if (type == "item")
        {
            List<InventoryLookupEntry> entries = new();
            CollectPendingItems(scene, pending.ID, string.Empty, entries);
            return ResolveEntryByNameOrPath(entries, lookup, "item", out targetId, out reason);
        }

        if (type == "folder")
        {
            List<InventoryLookupEntry> entries = new();
            CollectPendingFolders(scene, pending.ID, string.Empty, entries);
            return ResolveEntryByNameOrPath(entries, lookup, "folder", out targetId, out reason);
        }

        reason = "type must be item|folder";
        return false;
    }

    private bool ResolveEntryByNameOrPath(List<InventoryLookupEntry> entries, string lookup, string kind, out UUID targetId, out string reason)
    {
        targetId = UUID.Zero;
        reason = string.Empty;

        if (entries == null || entries.Count == 0)
        {
            reason = $"no pending {kind}s found";
            return false;
        }

        List<InventoryLookupEntry> pathMatches = entries
            .Where(e => PathMatchesLookup(e.Path, lookup))
            .ToList();

        if (pathMatches.Count == 1)
        {
            targetId = pathMatches[0].Id;
            return true;
        }

        if (pathMatches.Count > 1)
        {
            reason = $"multiple pending {kind}s match path '{lookup}': {FormatCandidates(pathMatches)}";
            return false;
        }

        List<InventoryLookupEntry> nameMatches = entries
            .Where(e => string.Equals(e.Name, lookup, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (nameMatches.Count == 1)
        {
            targetId = nameMatches[0].Id;
            return true;
        }

        if (nameMatches.Count > 1)
        {
            reason = $"multiple pending {kind}s named '{lookup}'. use full path: {FormatCandidates(nameMatches)}";
            return false;
        }

        reason = $"pending {kind} not found by UUID, name, or path";
        return false;
    }

    private void CollectPendingItems(Scene scene, UUID folderId, string pathPrefix, List<InventoryLookupEntry> entries)
    {
        InventoryCollection content = scene.InventoryService.GetFolderContent(m_sharedAvatarId, folderId);
        if (content == null)
            return;

        foreach (InventoryItemBase item in content.Items)
        {
            if (item == null)
                continue;

            string itemName = item.Name ?? string.Empty;
            string itemPath = string.IsNullOrEmpty(pathPrefix) ? itemName : pathPrefix + "/" + itemName;
            entries.Add(new InventoryLookupEntry
            {
                Id = item.ID,
                Name = itemName,
                Path = itemPath
            });
        }

        foreach (InventoryFolderBase folder in content.Folders)
        {
            if (folder == null)
                continue;

            string folderName = folder.Name ?? string.Empty;
            string childPrefix = string.IsNullOrEmpty(pathPrefix) ? folderName : pathPrefix + "/" + folderName;
            CollectPendingItems(scene, folder.ID, childPrefix, entries);
        }
    }

    private void CollectPendingFolders(Scene scene, UUID folderId, string pathPrefix, List<InventoryLookupEntry> entries)
    {
        InventoryCollection content = scene.InventoryService.GetFolderContent(m_sharedAvatarId, folderId);
        if (content == null)
            return;

        foreach (InventoryFolderBase folder in content.Folders)
        {
            if (folder == null)
                continue;

            string folderName = folder.Name ?? string.Empty;
            string folderPath = string.IsNullOrEmpty(pathPrefix) ? folderName : pathPrefix + "/" + folderName;
            entries.Add(new InventoryLookupEntry
            {
                Id = folder.ID,
                Name = folderName,
                Path = folderPath
            });

            CollectPendingFolders(scene, folder.ID, folderPath, entries);
        }
    }

    private bool PathMatchesLookup(string entryPath, string lookup)
    {
        string candidate = NormalizeLookupValue(entryPath);
        string target = NormalizeLookupValue(lookup);
        if (string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase))
            return true;

        string pendingQualified = NormalizeLookupValue(m_pendingFolderName + "/" + entryPath);
        return string.Equals(pendingQualified, target, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatCandidates(List<InventoryLookupEntry> matches)
    {
        return string.Join(", ", matches.Take(5).Select(m => m.Path));
    }

    private void EnsureTimerStarted()
    {
        lock (m_sync)
        {
            if (m_timer != null)
                return;

            m_timer = new Timer(
                _ =>
                {
                    _ = Task.Run(async () =>
                    {
                        await SyncUsersAsync(null, "timer", SyncScope.Full, m_dryRun).ConfigureAwait(false);
                    });
                },
                null,
                TimeSpan.FromSeconds(m_syncIntervalSeconds),
                TimeSpan.FromSeconds(m_syncIntervalSeconds));
        }
    }

    private void StopTimer()
    {
        lock (m_sync)
        {
            Timer timer = m_timer;
            m_timer = null;
            timer?.Dispose();
        }
    }

    private async Task SyncUsersAsync(HashSet<UUID> targetUsers, string reason, SyncScope scope, bool dryRun)
    {
        if (!await m_syncGate.WaitAsync(0).ConfigureAwait(false))
            return;

        Stopwatch sw = Stopwatch.StartNew();
        SyncStats aggregate = new();

        try
        {
            List<(Scene scene, UUID userId)> jobs = GatherSyncTargets(targetUsers);
            if (jobs.Count == 0)
                return;

            foreach ((Scene scene, UUID userId) in jobs)
            {
                try
                {
                    SyncStats stats = SyncOneUser(scene, userId, scope, dryRun);
                    aggregate.Add(stats);
                }
                catch (Exception ex)
                {
                    aggregate.Errors++;
                    Log.WarnFormat("[SHARED INVENTORY]: Sync failed for user {0}: {1}", userId, ex.Message);
                }
            }

            if (m_publishLibrarySnapshot)
                PublishSnapshotFolder(aggregate, dryRun);

            Interlocked.Increment(ref m_syncRuns);
            Interlocked.Add(ref m_syncUsers, aggregate.UsersProcessed);
            Interlocked.Add(ref m_syncErrors, aggregate.Errors);

            sw.Stop();
            Log.InfoFormat(
                "[SHARED INVENTORY]: Sync({0}/{1}) done in {2}ms users={3} uploadItems={4} uploadFolders={5} mirrorItems={6} mirrorFolders={7} skip={8} replace={9} rename={10} errors={11} dryRun={12}",
                reason,
                scope,
                sw.ElapsedMilliseconds,
                aggregate.UsersProcessed,
                aggregate.UploadItemsMoved,
                aggregate.UploadFoldersMoved,
                aggregate.MirrorItemsCopied,
                aggregate.MirrorFoldersCreated,
                aggregate.ItemsSkipped,
                aggregate.ItemsReplaced,
                aggregate.ItemsRenamed,
                aggregate.Errors,
                dryRun);
        }
        finally
        {
            m_syncGate.Release();
        }
    }

    private List<(Scene scene, UUID userId)> GatherSyncTargets(HashSet<UUID> targetUsers)
    {
        List<Scene> scenes;
        lock (m_sync)
            scenes = m_scenes.ToList();

        Dictionary<UUID, (Scene scene, UUID userId)> unique = new();

        foreach (Scene scene in scenes)
        {
            foreach (ScenePresence sp in scene.GetScenePresences())
            {
                if (sp == null || sp.IsDeleted || sp.IsChildAgent || sp.IsNPC)
                    continue;

                UUID userId = sp.UUID;
                if (targetUsers != null && !targetUsers.Contains(userId))
                    continue;

                if (!unique.ContainsKey(userId))
                    unique[userId] = (scene, userId);
            }
        }

        return unique.Values.ToList();
    }

    private SyncStats SyncOneUser(Scene scene, UUID userId, SyncScope scope, bool dryRun)
    {
        SyncStats stats = new();
        if (scene?.InventoryService == null || userId.IsZero())
        {
            stats.Errors++;
            return stats;
        }

        InventoryFolderBase sharedRoot = EnsureSharedRootFolder(scene);
        if (sharedRoot == null)
        {
            stats.Errors++;
            return stats;
        }

        InventoryFolderBase userRoot = EnsureNamedChild(scene, userId, scene.InventoryService.GetRootFolder(userId)?.ID ?? UUID.Zero, m_userRootFolderName);
        if (userRoot == null)
        {
            stats.Errors++;
            return stats;
        }

        InventoryFolderBase userUpload = EnsureNamedChild(scene, userId, userRoot.ID, m_userUploadFolderName);
        InventoryFolderBase userBrowse = EnsureNamedChild(scene, userId, userRoot.ID, m_userBrowseFolderName);
        if (userUpload == null || userBrowse == null)
        {
            stats.Errors++;
            return stats;
        }

        InventoryFolderBase targetRoot = sharedRoot;
        if (m_requireManagerApproval)
        {
            targetRoot = EnsureNamedChild(scene, m_sharedAvatarId, sharedRoot.ID, m_pendingFolderName);
            if (targetRoot == null)
            {
                stats.Errors++;
                return stats;
            }
        }

        if (scope is SyncScope.Full or SyncScope.Uploads)
            ProcessUserUploads(scene, userId, userUpload.ID, targetRoot.ID, dryRun, stats);

        if (scope is SyncScope.Full or SyncScope.Mirror)
            MirrorSharedToUser(scene, userId, sharedRoot.ID, userBrowse.ID, dryRun, stats);

        if (!dryRun && HasUserInventoryChanges(stats))
            PushViewerInventoryRefresh(scene, userId, userRoot, userUpload, userBrowse);

        stats.UsersProcessed = 1;
        return stats;
    }

    private static bool HasUserInventoryChanges(SyncStats stats)
    {
        if (stats == null)
            return false;

        return stats.UploadItemsMoved > 0
            || stats.UploadFoldersMoved > 0
            || stats.MirrorItemsCopied > 0
            || stats.MirrorFoldersCreated > 0
            || stats.ItemsReplaced > 0
            || stats.ItemsRenamed > 0;
    }

    private void PushViewerInventoryRefresh(Scene scene, UUID userId, InventoryFolderBase userRoot, InventoryFolderBase userUpload, InventoryFolderBase userBrowse)
    {
        if (scene == null || userId.IsZero())
            return;

        if (!scene.TryGetScenePresence(userId, out ScenePresence presence) || presence == null || presence.IsDeleted || presence.IsChildAgent)
            return;

        IClientAPI client = presence.ControllingClient;
        if (client == null || !client.IsActive)
            return;

        try
        {
            scene.SendInventoryUpdate(client, userRoot, fetchFolders: true, fetchItems: true);
            scene.SendInventoryUpdate(client, userUpload, fetchFolders: true, fetchItems: true);
            scene.SendInventoryUpdate(client, userBrowse, fetchFolders: true, fetchItems: true);
        }
        catch (Exception ex)
        {
            if (m_verboseLogging)
                Log.WarnFormat("[SHARED INVENTORY]: Failed to push viewer inventory refresh for {0}: {1}", userId, ex.Message);
        }
    }

    private InventoryFolderBase EnsureSharedRootFolder(Scene scene)
    {
        InventoryFolderBase root = scene.InventoryService.GetRootFolder(m_sharedAvatarId);
        if (root == null)
            return null;

        return EnsureNamedChild(scene, m_sharedAvatarId, root.ID, m_sharedRootFolderName);
    }

    private InventoryFolderBase EnsureNamedChild(Scene scene, UUID ownerId, UUID parentId, string name)
    {
        if (parentId.IsZero() || string.IsNullOrWhiteSpace(name))
            return null;

        InventoryCollection content = scene.InventoryService.GetFolderContent(ownerId, parentId);
        if (content != null)
        {
            InventoryFolderBase existing = content.Folders.FirstOrDefault(
                f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return existing;
        }

        InventoryFolderBase created = new(UUID.Random(), name, ownerId, (short)FolderType.None, parentId, 1);
        if (!scene.InventoryService.AddFolder(created))
            return null;

        return created;
    }

    private void ProcessUserUploads(Scene scene, UUID userId, UUID userUploadFolderId, UUID targetFolderId, bool dryRun, SyncStats stats)
    {
        InventoryCollection uploadContent = scene.InventoryService.GetFolderContent(userId, userUploadFolderId);
        if (uploadContent == null)
            return;

        foreach (InventoryItemBase item in uploadContent.Items)
        {
            if (item == null)
                continue;

            InventoryCollection targetContent = scene.InventoryService.GetFolderContent(m_sharedAvatarId, targetFolderId);
            InventoryItemBase existing = targetContent?.Items?.FirstOrDefault(i => string.Equals(i.Name, item.Name, StringComparison.OrdinalIgnoreCase));

            if (!HandleUploadItemConflict(scene, existing, targetFolderId, item.Name, dryRun, stats, out string finalName))
                continue;

            if (dryRun)
            {
                stats.UploadItemsMoved++;
                continue;
            }

            InventoryItemBase sharedCopy = scene.GiveInventoryItem(m_sharedAvatarId, userId, item.ID, targetFolderId, out string _);
            if (sharedCopy == null)
            {
                stats.Errors++;
                continue;
            }

            if (!string.Equals(sharedCopy.Name, finalName, StringComparison.Ordinal))
            {
                sharedCopy.Name = finalName;
                scene.InventoryService.UpdateItem(sharedCopy);
            }

            scene.InventoryService.DeleteItems(userId, new List<UUID> { item.ID });
            stats.UploadItemsMoved++;
        }

        foreach (InventoryFolderBase folder in uploadContent.Folders)
        {
            if (folder == null)
                continue;

            InventoryCollection targetContent = scene.InventoryService.GetFolderContent(m_sharedAvatarId, targetFolderId);
            InventoryFolderBase existing = targetContent?.Folders?.FirstOrDefault(f => string.Equals(f.Name, folder.Name, StringComparison.OrdinalIgnoreCase));

            if (existing != null && m_uploadDuplicatePolicy == DuplicatePolicy.Skip)
            {
                stats.ItemsSkipped++;
                continue;
            }

            if (existing != null && m_uploadDuplicatePolicy == DuplicatePolicy.Replace)
            {
                if (!dryRun)
                    scene.InventoryService.DeleteFolders(m_sharedAvatarId, new List<UUID> { existing.ID });
                stats.ItemsReplaced++;
            }

            if (dryRun)
            {
                stats.UploadFoldersMoved++;
                continue;
            }

            InventoryFolderBase sharedFolder = scene.GiveInventoryFolder(null, m_sharedAvatarId, userId, folder.ID, targetFolderId);
            if (sharedFolder == null)
            {
                stats.Errors++;
                continue;
            }

            if (existing != null && m_uploadDuplicatePolicy == DuplicatePolicy.Rename)
            {
                sharedFolder.Name = BuildUniqueFolderName(scene, m_sharedAvatarId, targetFolderId, sharedFolder.Name);
                scene.InventoryService.UpdateFolder(sharedFolder);
                stats.ItemsRenamed++;
            }

            scene.InventoryService.DeleteFolders(userId, new List<UUID> { folder.ID });
            stats.UploadFoldersMoved++;
        }
    }

    private bool HandleUploadItemConflict(Scene scene, InventoryItemBase existing, UUID folderId, string sourceName, bool dryRun, SyncStats stats, out string finalName)
    {
        finalName = sourceName;
        if (existing == null)
            return true;

        switch (m_uploadDuplicatePolicy)
        {
            case DuplicatePolicy.Skip:
                stats.ItemsSkipped++;
                return false;

            case DuplicatePolicy.Replace:
                if (!dryRun)
                    scene.InventoryService.DeleteItems(m_sharedAvatarId, new List<UUID> { existing.ID });
                stats.ItemsReplaced++;
                return true;

            case DuplicatePolicy.Rename:
            default:
                finalName = BuildUniqueItemName(scene, m_sharedAvatarId, folderId, sourceName);
                stats.ItemsRenamed++;
                return true;
        }
    }

    private void MirrorSharedToUser(Scene scene, UUID userId, UUID sharedFolderId, UUID userFolderId, bool dryRun, SyncStats stats)
    {
        InventoryCollection sharedContent = scene.InventoryService.GetFolderContent(m_sharedAvatarId, sharedFolderId);
        InventoryCollection userContent = scene.InventoryService.GetFolderContent(userId, userFolderId);
        if (sharedContent == null || userContent == null)
            return;

        Dictionary<string, InventoryFolderBase> userFoldersByName = userContent.Folders
            .Where(f => f != null)
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        Dictionary<string, List<InventoryItemBase>> userItemsByName = userContent.Items
            .Where(i => i != null)
            .GroupBy(i => i.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (InventoryItemBase sharedItem in sharedContent.Items)
        {
            if (sharedItem == null)
                continue;

            string sig = BuildItemSignature(sharedItem);
            List<InventoryItemBase> withSameName = userItemsByName.TryGetValue(sharedItem.Name ?? string.Empty, out List<InventoryItemBase> list)
                ? list
                : new List<InventoryItemBase>();

            InventoryItemBase matching = withSameName.FirstOrDefault(i => string.Equals(BuildItemSignature(i), sig, StringComparison.OrdinalIgnoreCase));
            if (matching != null)
                continue;

            if (withSameName.Count > 0)
            {
                if (m_mirrorDuplicatePolicy == DuplicatePolicy.Skip)
                {
                    stats.ItemsSkipped++;
                    continue;
                }

                if (m_mirrorDuplicatePolicy == DuplicatePolicy.Replace)
                {
                    if (!dryRun)
                        scene.InventoryService.DeleteItems(userId, withSameName.Select(i => i.ID).ToList());
                    stats.ItemsReplaced++;
                }
            }

            if (dryRun)
            {
                stats.MirrorItemsCopied++;
                continue;
            }

            InventoryItemBase userCopy = scene.GiveInventoryItem(userId, m_sharedAvatarId, sharedItem.ID, userFolderId, out string _);
            if (userCopy == null)
            {
                stats.Errors++;
                continue;
            }

            if (m_mirrorDuplicatePolicy == DuplicatePolicy.Rename && withSameName.Count > 0)
            {
                userCopy.Name = BuildUniqueItemName(scene, userId, userFolderId, userCopy.Name);
                scene.InventoryService.UpdateItem(userCopy);
                stats.ItemsRenamed++;
            }

            stats.MirrorItemsCopied++;
        }

        foreach (InventoryFolderBase sharedChild in sharedContent.Folders)
        {
            if (sharedChild == null)
                continue;

            if (!userFoldersByName.TryGetValue(sharedChild.Name, out InventoryFolderBase userChild))
            {
                if (!dryRun)
                {
                    userChild = new InventoryFolderBase(UUID.Random(), sharedChild.Name, userId, (short)FolderType.None, userFolderId, 1);
                    if (!scene.InventoryService.AddFolder(userChild))
                    {
                        stats.Errors++;
                        continue;
                    }
                    userFoldersByName[userChild.Name] = userChild;
                }

                stats.MirrorFoldersCreated++;
            }

            if (userChild != null)
                MirrorSharedToUser(scene, userId, sharedChild.ID, userChild.ID, dryRun, stats);
        }
    }

    private string BuildUniqueItemName(Scene scene, UUID owner, UUID folderId, string desiredName)
    {
        InventoryCollection content = scene.InventoryService.GetFolderContent(owner, folderId);
        HashSet<string> names = content?.Items?.Where(i => i != null).Select(i => i.Name ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!names.Contains(desiredName ?? string.Empty))
            return desiredName;

        string baseName = desiredName ?? "Item";
        for (int i = 2; i < 5000; i++)
        {
            string candidate = string.Format(CultureInfo.InvariantCulture, "{0} ({1})", baseName, i);
            if (!names.Contains(candidate))
                return candidate;
        }

        return baseName + " (copy)";
    }

    private string BuildUniqueFolderName(Scene scene, UUID owner, UUID folderId, string desiredName)
    {
        InventoryCollection content = scene.InventoryService.GetFolderContent(owner, folderId);
        HashSet<string> names = content?.Folders?.Where(i => i != null).Select(i => i.Name ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!names.Contains(desiredName ?? string.Empty))
            return desiredName;

        string baseName = desiredName ?? "Folder";
        for (int i = 2; i < 5000; i++)
        {
            string candidate = string.Format(CultureInfo.InvariantCulture, "{0} ({1})", baseName, i);
            if (!names.Contains(candidate))
                return candidate;
        }

        return baseName + " (copy)";
    }

    private void PublishSnapshotFolder(SyncStats stats, bool dryRun)
    {
        Scene scene = GetPrimaryScene();
        if (scene?.InventoryService == null)
            return;

        InventoryFolderBase sharedRoot = EnsureSharedRootFolder(scene);
        if (sharedRoot == null)
            return;

        InventoryFolderBase publishRoot = EnsureNamedChild(scene, m_sharedAvatarId, sharedRoot.ID, m_publishFolderName);
        if (publishRoot == null)
            return;

        if (m_verboseLogging)
            Log.InfoFormat("[SHARED INVENTORY]: Publish snapshot prepared in '{0}' (dryRun={1})", m_publishFolderName, dryRun);
    }

    private bool IsWithinSubtree(Scene scene, UUID folderId, UUID rootId)
    {
        if (folderId.IsZero() || rootId.IsZero())
            return false;

        UUID cursor = folderId;
        for (int i = 0; i < 256; i++)
        {
            if (cursor == rootId)
                return true;

            InventoryFolderBase f = scene.InventoryService.GetFolder(m_sharedAvatarId, cursor);
            if (f == null || f.ParentID.IsZero())
                return false;

            cursor = f.ParentID;
        }

        return false;
    }

    private Scene GetPrimaryScene()
    {
        lock (m_sync)
            return m_scenes.FirstOrDefault();
    }

    private string GetStatsMessage()
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "[SHARED INVENTORY] runs={0} users={1} errors={2} dryRun={3} verbose={4} moderation={5} uploadPolicy={6} mirrorPolicy={7}",
            Interlocked.Read(ref m_syncRuns),
            Interlocked.Read(ref m_syncUsers),
            Interlocked.Read(ref m_syncErrors),
            m_dryRun,
            m_verboseLogging,
            m_requireManagerApproval,
            m_uploadDuplicatePolicy,
            m_mirrorDuplicatePolicy);
    }

    private static string BuildItemSignature(InventoryItemBase item)
    {
        if (item == null)
            return string.Empty;

        return string.Concat(item.AssetID, "|", item.AssetType, "|", item.InvType, "|", item.Name ?? string.Empty);
    }

    private static string NonEmpty(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        return value.Trim();
    }

    private static string NormalizeLookupValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string normalized = value.Trim();
        if (normalized.Length >= 2)
        {
            bool quoted =
                (normalized.StartsWith("\"", StringComparison.Ordinal) && normalized.EndsWith("\"", StringComparison.Ordinal)) ||
                (normalized.StartsWith("'", StringComparison.Ordinal) && normalized.EndsWith("'", StringComparison.Ordinal));
            if (quoted)
                normalized = normalized.Substring(1, normalized.Length - 2).Trim();
        }

        normalized = normalized.Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);

        normalized = normalized.Trim('/');
        return normalized;
    }

    private static void ParseUuidList(string raw, ISet<UUID> target)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;

        string[] entries = raw.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string entry in entries)
        {
            if (UUID.TryParse(entry.Trim(), out UUID id) && !id.IsZero())
                target.Add(id);
        }
    }

    private static DuplicatePolicy ParseDuplicatePolicy(string raw, DuplicatePolicy fallback)
    {
        string value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "skip" => DuplicatePolicy.Skip,
            "rename" => DuplicatePolicy.Rename,
            "replace" => DuplicatePolicy.Replace,
            _ => fallback
        };
    }

    private static SyncScope ParseSyncScope(string raw, SyncScope fallback)
    {
        string value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "uploads" => SyncScope.Uploads,
            "mirror" => SyncScope.Mirror,
            "full" => SyncScope.Full,
            _ => fallback
        };
    }
}
