using Npgsql;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.PGSQL;
using OpenSim.Framework;
using OpenSim.Services.Base;
using Xunit;

namespace OpenSim.Data.PGSQL.Tests;

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IGRID_PGSQL_TEST_CONNECTION")))
            Skip = "Set IGRID_PGSQL_TEST_CONNECTION to a disposable local igrid_provider_test database.";
    }
}

public class PostgreSqlSmokeTests
{
    private static string Connection()
    {
        string value = Environment.GetEnvironmentVariable("IGRID_PGSQL_TEST_CONNECTION");
        var settings = new NpgsqlConnectionStringBuilder(value);
        if (settings.Database != "igrid_provider_test" || settings.Host is not ("127.0.0.1" or "localhost"))
            throw new InvalidOperationException("Integration tests require the disposable LOCAL igrid_provider_test database.");
        return value;
    }

    private static T Load<T>(params object[] args) where T : class =>
        Assert.IsAssignableFrom<T>(new ServiceBase(null).LoadPlugin<T>(typeof(PGSQLRegionData).Assembly.Location, args));

    [PostgreSqlFact]
    public void ProvidersMigrateDiscoverAndPersistCoreData()
    {
        string connection = Connection();
        var region = Load<IRegionData>(connection, "regions");
        var users = Load<IUserAccountData>(connection, "useraccounts");
        var presence = Load<IPresenceData>(connection, "presence");
        var gridUsers = Load<IGridUserData>(connection, "griduser");
        var friends = Load<IFriendsData>(connection, "friends");
        var authentication = Load<IAuthenticationData>(connection, "auth");
        var inventory = Load<IXInventoryData>(connection, "inventory");
        var experience = Load<IExperienceData>(connection);
        Load<IAgentPreferencesData>(connection, "agentprefs");
        Load<IHGTravelingData>(connection, "hg_traveling_data");
        Load<IGroupsData>(connection, "os_groups");
        Load<IProfilesData>(connection);
        Load<IMuteListData>(connection);
        Load<IEstateDataStore>(connection);
        Load<IAvatarData>(connection, "avatars");
        Load<IOfflineIMData>(connection, "im_offline");

        UUID user = UUID.Random(), regionID = UUID.Random(), session = UUID.Random(), secure = UUID.Random();
        var account = new UserAccountData { PrincipalID = user, ScopeID = UUID.Zero,
            FirstName = "Provider", LastName = "Smoke", Data = new() { ["Email"] = "", ["ServiceURLs"] = "", ["Created"] = "0", ["UserLevel"] = "0", ["UserFlags"] = "0", ["UserTitle"] = "" } };
        Assert.True(users.Store(account));
        Assert.Equal(user, Assert.Single(users.Get(new[] { "PrincipalID" }, new[] { user.ToString() })).PrincipalID);
        account.FirstName = "Updated";
        Assert.True(users.Store(account));
        Assert.Equal("Updated", Assert.Single(users.Get(new[] { "PrincipalID" }, new[] { user.ToString() })).FirstName);
        Assert.True(gridUsers.Store(new GridUserData { UserID = user.ToString() }));
        Assert.Equal(user.ToString(), gridUsers.Get(user.ToString()).UserID);
        Assert.True(presence.Store(new PresenceData { UserID = user.ToString(), RegionID = UUID.Zero, SessionID = session,
            Data = new() { ["SecureSessionID"] = secure.ToString() } }));
        Assert.True(presence.VerifyAgent(user, secure));
        Assert.True(presence.ReportAgent(session, regionID));
        Assert.Equal(regionID, presence.Get(session).RegionID);
        var folder = new XInventoryFolder { folderID = UUID.Random(), agentID = user, parentFolderID = UUID.Zero,
            folderName = "My Inventory", type = 8, version = 1 };
        Assert.True(inventory.StoreFolder(folder));
        Assert.Equal(folder.folderID, Assert.Single(inventory.GetFolder("folderID", folder.folderID.ToString())).folderID);
        var item = new XInventoryItem { inventoryID = UUID.Random(), assetID = UUID.Random(),
            avatarID = user, parentFolderID = folder.folderID, inventoryName = "Smoke gesture",
            inventoryDescription = "Test only", creatorID = user.ToString(), assetType = (int)AssetType.Gesture,
            flags = 1, inventoryCurrentPermissions = 7 };
        Assert.True(inventory.StoreItem(item));
        Assert.Equal(item.inventoryID, Assert.Single(inventory.GetActiveGestures(user)).inventoryID);
        item.groupOwned = 1;
        Assert.True(inventory.StoreItem(item));
        Assert.Equal(1, Assert.Single(inventory.GetItems(new[] { "inventoryID" }, new[] { item.inventoryID.ToString() })).groupOwned);
        Assert.Equal(7, inventory.GetAssetPermissions(user, item.assetID));
        Assert.True(inventory.MoveItem(item.inventoryID.ToString(), folder.folderID.ToString()));
        Assert.True(inventory.DeleteItems("inventoryID", item.inventoryID.ToString()));
        UUID experienceID = UUID.Random();
        Assert.True(experience.UpdateExperienceInfo(new ExperienceInfoData { public_id = experienceID,
            owner_id = user, group_id = user, name = "Smoke experience", description = "Test only",
            marketplace = "", slurl = "" }));
        Assert.Equal(experienceID, Assert.Single(experience.GetAgentExperiences(user)));
        Assert.Equal(experienceID, Assert.Single(experience.GetGroupExperiences(user)));
        Assert.Equal(experienceID, Assert.Single(experience.GetExperiencesForGroups(new[] { user })));
        Assert.Equal("Smoke experience", Assert.Single(experience.GetExperienceInfos(new[] { experienceID })).name);
        Assert.Contains(experience.FindExperiences("Smoke"), info => info.public_id == experienceID);
        Assert.True(experience.SetExperiencePermissions(user, experienceID, true));
        Assert.True(experience.GetExperiencePermissions(user)[experienceID]);
        Assert.True(experience.SetKeyValue(experienceID, "test", "Zażółć"));
        Assert.Equal("Zażółć", experience.GetKeyValue(experienceID, "test"));
        Assert.Equal(1, experience.GetKeyCount(experienceID));
        Assert.Equal(new[] { "test" }, experience.GetKeys(experienceID, 0, 10));
        Assert.True(experience.GetKeyValueSize(experienceID) > 4);
        Assert.True(experience.SetExperiencePermissions(user, experienceID, false));
        Assert.False(experience.GetExperiencePermissions(user)[experienceID]);
        Assert.True(experience.DeleteKey(experienceID, "test"));
        Assert.True(experience.ForgetExperiencePermissions(user, experienceID));
        Assert.True(presence.Delete("SessionID", session.ToString()));
        Assert.Null(presence.Get(session));
        Assert.True(users.Delete("PrincipalID", user.ToString()));
        Assert.Empty(users.Get(new[] { "PrincipalID" }, new[] { user.ToString() }));
    }
}
