using MySqlConnector;
using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using Nini.Config;

namespace TasiaAddons.GridFunctions;

/// <summary>MOD-invokable grid chat and virtual-avatar profile operations.</summary>
public sealed class TasiaGridFunctionsModule : INonSharedRegionModule
{
    private const string ProfileTable = "TasiaVirtualAvatars";
    private readonly object gate = new();
    private IScriptModuleComms scriptComms;
    private Scene scene;
    private bool enabled;
    private int minimumUserLevel = 150;
    private string provider;
    private string connectionString;

    public string Name => "TasiaGridFunctionsModule";
    public Type ReplaceableInterface => null;

    public void Initialise(IConfigSource source)
    {
        IConfig config = source.Configs["TasiaGridFunctions"];
        if (config == null || !config.GetBoolean("Enabled", false))
            return;

        provider = config.GetString("DatabaseProvider", "PostgreSQL").Trim();
        if (!provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase) &&
            !provider.Equals("MySQL", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TasiaGridFunctions DatabaseProvider must be PostgreSQL or MySQL.");

        minimumUserLevel = config.GetInt("MinimumUserLevel", 150);
        if (minimumUserLevel < 0)
            throw new InvalidOperationException("TasiaGridFunctions MinimumUserLevel cannot be negative.");

        IConfig database = source.Configs["DatabaseService"];
        connectionString = database?.GetString("ConnectionString", string.Empty) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("TasiaGridFunctions requires DatabaseService ConnectionString.");

        string databaseName = config.GetString("DatabaseName", "robust");
        connectionString = BuildConnectionString(provider, connectionString, databaseName);
        enabled = true;
    }

    public void AddRegion(Scene value)
    {
        if (enabled)
            scene = value;
    }

    public void RegionLoaded(Scene value)
    {
        if (!enabled)
            return;

        scriptComms = value.RequestModuleInterface<IScriptModuleComms>()
            ?? throw new InvalidOperationException("TasiaGridFunctions requires ScriptModuleComms.");
        scriptComms.RegisterScriptInvocations(this);
    }

    public void RemoveRegion(Scene value)
    {
        lock (gate)
        {
            if (ReferenceEquals(scene, value))
                scene = null;
        }
    }

    public void Close()
    {
        lock (gate)
        {
            enabled = false;
            scene = null;
            scriptComms = null;
        }
    }

    [ScriptInvocation]
    public void maGridSay(UUID hostID, UUID scriptID, string name, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        Scene current = GetAuthorizedScene(hostID, scriptID);
        if (current == null || !TryGetVirtualSender(name, out string senderName, out UUID senderID))
            return;

        foreach (Scene targetScene in SceneManager.Instance.Scenes)
        {
            foreach (ScenePresence presence in targetScene.GetScenePresences())
            {
                if (presence.IsDeleted || presence.IsInTransit || presence.ControllingClient == null ||
                    !presence.ControllingClient.IsActive)
                    continue;

                presence.ControllingClient.SendChatMessage(
                    message, (byte)ChatTypeEnum.Say,
                    current.GetSceneObjectPart(hostID)?.AbsolutePosition ?? Vector3.Zero,
                    senderName, senderID, senderID,
                    (byte)ChatSourceType.Agent, (byte)ChatAudibleLevel.Fully);
            }
        }
    }

    [ScriptInvocation]
    public void maGridSayIM(UUID hostID, UUID scriptID, string name, UUID agentID, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        Scene current = GetAuthorizedScene(hostID, scriptID);
        if (current == null || agentID == UUID.Zero || !TryGetVirtualSender(name, out string senderName, out UUID senderID))
            return;

        SceneObjectPart host = current.GetSceneObjectPart(hostID);
        if (host == null)
            return;

        Vector3 position = host.AbsolutePosition;
        GridInstantMessage im = new()
        {
            fromAgentID = senderID.Guid,
            toAgentID = agentID.Guid,
            imSessionID = senderID.Guid,
            timestamp = (uint)Util.UnixTimeSinceEpoch(),
            fromAgentName = senderName,
            dialog = (byte)InstantMessageDialog.MessageFromAgent,
            fromGroup = false,
            offline = 0,
            ParentEstateID = current.RegionInfo.EstateSettings.EstateID,
            Position = position,
            RegionID = current.RegionInfo.RegionID.Guid,
            message = message.Length > 1024 ? message[..1024] : message,
            binaryBucket = Util.StringToBytes256($"{current.RegionInfo.RegionName}/{(int)position.X}/{(int)position.Y}/{(int)position.Z}")
        };

        if (current.TryGetScenePresence(agentID, out ScenePresence presence))
            presence.ControllingClient.SendInstantMessage(im);
        else
            current.RequestModuleInterface<IMessageTransferModule>()?.SendInstantMessage(im, _ => { });
    }

    [ScriptInvocation]
    public void maChangeGridProfile(
        UUID hostID, UUID scriptID, string who, string aboutText, string profileImage,
        string firstLifeText, string firstLifeImage, string webUrl, string accountTitle, string bornOn)
    {
        Scene current = GetAuthorizedScene(hostID, scriptID);
        if (current == null || !TryGetVirtualSender(who, out string avatarName, out UUID virtualID))
            return;

        SceneObjectPart host = current.GetSceneObjectPart(hostID);
        if (host == null)
            return;

        int split = avatarName.LastIndexOf(' ');
        string firstName = split > 0 ? avatarName[..split] : avatarName;
        string lastName = split > 0 ? avatarName[(split + 1)..] : string.Empty;
        UUID profileImageID = UUID.TryParse(profileImage, out UUID p) ? p : UUID.Zero;
        UUID firstLifeImageID = UUID.TryParse(firstLifeImage, out UUID f) ? f : UUID.Zero;
        int now = (int)Util.UnixTimeSinceEpoch();

        try
        {
            if (provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
                UpsertPostgreSql(virtualID, avatarName, firstName, lastName, host.OwnerID,
                    aboutText, profileImageID, firstLifeImageID, firstLifeText, webUrl, accountTitle, bornOn, now);
            else
                UpsertMySql(virtualID, avatarName, firstName, lastName, host.OwnerID,
                    aboutText, profileImageID, firstLifeImageID, firstLifeText, webUrl, accountTitle, bornOn, now);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[TasiaGridFunctions] Profile upsert failed: {e}");
            throw new InvalidOperationException("maChangeGridProfile could not save the virtual-avatar profile.", e);
        }
    }

    private Scene GetAuthorizedScene(UUID hostID, UUID scriptID)
    {
        Scene current;
        lock (gate)
        {
            if (!enabled || scene == null)
                throw new InvalidOperationException("TasiaGridFunctions is not enabled for this region.");
            current = scene;
        }

        SceneObjectPart host = current.GetSceneObjectPart(hostID);
        TaskInventoryItem script = host?.Inventory.GetInventoryItem(scriptID);
        if (host == null || host.ParentGroup.IsDeleted || host.ParentGroup.inTransit || script == null ||
            script.Type != (int)AssetType.LSLText || script.OwnerID != host.OwnerID)
            throw new InvalidOperationException("The calling script or prim is no longer valid.");

        UserAccount owner = current.UserAccountService?.GetUserAccount(current.RegionInfo.ScopeID, host.OwnerID);
        if (owner == null || owner.UserLevel < minimumUserLevel)
            throw new InvalidOperationException($"This operation requires an account level of at least {minimumUserLevel}.");

        return current;
    }

    private static bool TryGetVirtualSender(string name, out string senderName, out UUID senderID)
    {
        senderName = name?.Trim();
        senderID = UUID.Zero;
        if (string.IsNullOrWhiteSpace(senderName) || senderName.Length > 255)
            return false;

        senderID = Util.ComputeASCIISHA1UUID("igrid-virtual-avatar:" + senderName.ToLowerInvariant());
        return true;
    }

    private void UpsertPostgreSql(UUID virtualID, string avatarName, string firstName, string lastName,
        UUID ownerID, string aboutText, UUID profileImage, UUID firstLifeImage, string firstLifeText,
        string webUrl, string userTitle, string bornOn, int now)
    {
        const string sql = @"
INSERT INTO ""TasiaVirtualAvatars""
    (""VirtualID"", ""AvatarName"", ""FirstName"", ""LastName"", ""DisplayName"", ""OwnerID"",
     ""AboutText"", ""ProfileImage"", ""FirstLifeImage"", ""FirstLifeText"", ""WebUrl"", ""UserTitle"",
     ""BornOn"", ""Enabled"", ""Created"", ""Updated"")
VALUES (@VirtualID::uuid, @AvatarName, @FirstName, @LastName, @AvatarName, @OwnerID::uuid,
        @AboutText, @ProfileImage::uuid, @FirstLifeImage::uuid, @FirstLifeText, @WebUrl, @UserTitle,
        @BornOn, 1, @Now, @Now)
ON CONFLICT (""VirtualID"") DO UPDATE SET
    ""AvatarName"" = EXCLUDED.""AvatarName"", ""FirstName"" = EXCLUDED.""FirstName"",
    ""LastName"" = EXCLUDED.""LastName"", ""DisplayName"" = EXCLUDED.""DisplayName"",
    ""OwnerID"" = EXCLUDED.""OwnerID"", ""AboutText"" = EXCLUDED.""AboutText"",
    ""ProfileImage"" = EXCLUDED.""ProfileImage"", ""FirstLifeImage"" = EXCLUDED.""FirstLifeImage"",
    ""FirstLifeText"" = EXCLUDED.""FirstLifeText"", ""WebUrl"" = EXCLUDED.""WebUrl"",
    ""UserTitle"" = EXCLUDED.""UserTitle"", ""BornOn"" = EXCLUDED.""BornOn"",
    ""Enabled"" = 1, ""Updated"" = EXCLUDED.""Updated"";";

        using NpgsqlConnection connection = new(connectionString);
        connection.Open();
        using NpgsqlCommand command = new(sql, connection);
        AddParameters(command, virtualID, avatarName, firstName, lastName, ownerID, aboutText,
            profileImage, firstLifeImage, firstLifeText, webUrl, userTitle, bornOn, now);
        command.ExecuteNonQuery();
    }

    private void UpsertMySql(UUID virtualID, string avatarName, string firstName, string lastName,
        UUID ownerID, string aboutText, UUID profileImage, UUID firstLifeImage, string firstLifeText,
        string webUrl, string userTitle, string bornOn, int now)
    {
        const string sql = @"
INSERT INTO TasiaVirtualAvatars
    (VirtualID, AvatarName, FirstName, LastName, DisplayName, OwnerID, AboutText, ProfileImage,
     FirstLifeImage, FirstLifeText, WebUrl, UserTitle, BornOn, Enabled, Created, Updated)
VALUES (@VirtualID, @AvatarName, @FirstName, @LastName, @AvatarName, @OwnerID, @AboutText,
        @ProfileImage, @FirstLifeImage, @FirstLifeText, @WebUrl, @UserTitle, @BornOn, 1, @Now, @Now)
ON DUPLICATE KEY UPDATE AvatarName = VALUES(AvatarName), FirstName = VALUES(FirstName),
    LastName = VALUES(LastName), DisplayName = VALUES(DisplayName), OwnerID = VALUES(OwnerID),
    AboutText = VALUES(AboutText), ProfileImage = VALUES(ProfileImage), FirstLifeImage = VALUES(FirstLifeImage),
    FirstLifeText = VALUES(FirstLifeText), WebUrl = VALUES(WebUrl), UserTitle = VALUES(UserTitle),
    BornOn = VALUES(BornOn), Enabled = 1, Updated = VALUES(Updated);";

        using MySqlConnection connection = new(connectionString);
        connection.Open();
        using MySqlCommand command = new(sql, connection);
        AddParameters(command, virtualID, avatarName, firstName, lastName, ownerID, aboutText,
            profileImage, firstLifeImage, firstLifeText, webUrl, userTitle, bornOn, now);
        command.ExecuteNonQuery();
    }

    private static void AddParameters(System.Data.Common.DbCommand command, UUID virtualID, string avatarName,
        string firstName, string lastName, UUID ownerID, string aboutText, UUID profileImage,
        UUID firstLifeImage, string firstLifeText, string webUrl, string userTitle, string bornOn, int now)
    {
        AddParameter(command, "VirtualID", virtualID.ToString());
        AddParameter(command, "AvatarName", avatarName);
        AddParameter(command, "FirstName", firstName);
        AddParameter(command, "LastName", lastName);
        AddParameter(command, "OwnerID", ownerID.ToString());
        AddParameter(command, "AboutText", aboutText ?? string.Empty);
        AddParameter(command, "ProfileImage", profileImage.ToString());
        AddParameter(command, "FirstLifeImage", firstLifeImage.ToString());
        AddParameter(command, "FirstLifeText", firstLifeText ?? string.Empty);
        AddParameter(command, "WebUrl", webUrl ?? string.Empty);
        AddParameter(command, "UserTitle", userTitle ?? string.Empty);
        AddParameter(command, "BornOn", bornOn ?? string.Empty);
        AddParameter(command, "Now", now);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        System.Data.Common.DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@" + name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string BuildConnectionString(string databaseProvider, string source, string databaseName)
    {
        if (databaseProvider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            NpgsqlConnectionStringBuilder builder = new(source) { Database = databaseName };
            return builder.ConnectionString;
        }

        MySqlConnectionStringBuilder mysql = new(source) { Database = databaseName };
        return mysql.ConnectionString;
    }
}
