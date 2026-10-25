using System.Reflection;
using Npgsql;
using OpenMetaverse;
using OpenSim.Data.PGSQL;
using Xunit;

namespace OpenSim.Data.PGSQL.Tests;

public class ReviewRegressionTests
{
    private static string LocalConnection()
    {
        string value = Environment.GetEnvironmentVariable("IGRID_PGSQL_TEST_CONNECTION");
        var c = new NpgsqlConnectionStringBuilder(value);
        if (c.Database != "igrid_provider_test" || c.Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException("Disposable LOCAL database required");
        return value;
    }

    [PostgreSqlFact]
    public void GroupMembershipQueriesAndLiteralSearchAreImplemented()
    {
        string connection = LocalConnection();
        var groups = new PGSQLGroupsData(connection, "os_groups");
        UUID group = UUID.Random(); string user = UUID.Random().ToString();
        Assert.True(groups.StoreMember(new OpenSim.Data.MembershipData { GroupID = group, PrincipalID = user,
            Data = new() { ["SelectedRoleID"] = UUID.Zero.ToString(), ["AccessToken"] = UUID.Zero.ToString() } }));
        Assert.Equal(group, groups.RetrievePrincipalGroupMembership(user, group).GroupID);
        Assert.Equal(group, Assert.Single(groups.RetrievePrincipalGroupMemberships(user)).GroupID);
        Assert.Empty(groups.RetrieveGroups("O'Reilly"));
        Assert.True(groups.GroupsCount() >= 0);
        Assert.True(groups.DeleteMember(group, user));
        var gridUsers = new PGSQLGridUserData(connection, "griduser");
        Assert.Empty(gridUsers.GetAll("O'Reilly"));
        var users = new PGSQLUserAccountData(connection, "useraccounts");
        Assert.NotNull(users.GetUsers(UUID.Zero, "Smoke"));
    }

    [PostgreSqlFact]
    public void SimulationStorageMigratesAndListsOnlyRootObjects()
    {
        string connection = LocalConnection();
        var store = new PGSQLSimulationData(connection);
        UUID region = UUID.Random(), root = UUID.Random(), child = UUID.Random();
        using var db = new NpgsqlConnection(connection); db.Open();
        using var cmd = new NpgsqlCommand("INSERT INTO prims (\"UUID\",\"RegionUUID\",\"SceneGroupID\",\"PassCollisions\") VALUES (@root,@region,@root,false),(@child,@region,@root,false)", db);
        cmd.Parameters.AddWithValue("root", root.Guid); cmd.Parameters.AddWithValue("child", child.Guid); cmd.Parameters.AddWithValue("region", region.Guid);
        cmd.ExecuteNonQuery();
        Assert.Equal(new[] { root }, store.GetObjectIDs(region));
    }

    [PostgreSqlFact]
    public void FailedMigrationDoesNotAdvanceItsVersion()
    {
        using var db = new NpgsqlConnection(LocalConnection()); db.Open();
        var migration = new OpenSim.Data.Migration(db, GetType().Assembly, "QualityFailureProbe");
        Assert.Throws<InvalidOperationException>(() => migration.Update());
        using var query = new NpgsqlCommand("SELECT version FROM migrations WHERE name='QualityFailureProbe'", db);
        Assert.Equal(1, (int)query.ExecuteScalar());
        Assert.Throws<InvalidOperationException>(() => migration.Update());
        Assert.Equal(1, (int)query.ExecuteScalar());
    }

    [PostgreSqlFact]
    public void EstateDeletionRejectsLinkedRegionsAndDeletesUnlinkedEstate()
    {
        string connection = LocalConnection();
        var store = new PGSQLEstateStore(connection); UUID region = UUID.Random();
        var estate = store.LoadEstateSettings(region, true);
        Assert.NotNull(estate);
        Assert.False(store.DeleteEstate((int)estate.EstateID));
        using var db = new NpgsqlConnection(connection); db.Open();
        using var unlink = new NpgsqlCommand("DELETE FROM estate_map WHERE \"RegionID\"=@region", db);
        unlink.Parameters.AddWithValue("region", region.Guid); unlink.ExecuteNonQuery();
        Assert.True(store.DeleteEstate((int)estate.EstateID));
        Assert.False(store.DeleteEstate((int)estate.EstateID));
    }

    [PostgreSqlFact]
    public void HistoricalGroupAndOfflineMessageUpgradesPreserveRecords()
    {
        using var admin = new NpgsqlConnection(LocalConnection()); admin.Open();
        foreach (bool groups in new[] { true, false })
        {
            string schema = "quality_legacy_" + Guid.NewGuid().ToString("N");
            using var ddl = new NpgsqlCommand("CREATE SCHEMA " + schema, admin); ddl.ExecuteNonQuery();
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(LocalConnection()) { SearchPath = schema };
                using var db = new NpgsqlConnection(builder.ConnectionString); db.Open();
                string store = groups ? "os_groups_Store" : "IM_Store";
                var assembly = typeof(PGSQLRegionData).Assembly;
                string resource = Assert.Single(assembly.GetManifestResourceNames(), name => name.EndsWith("Resources." + store + ".migrations"));
                using var stream = assembly.GetManifestResourceStream(resource); using var reader = new StreamReader(stream);
                string historical = reader.ReadToEnd().Split(":VERSION 3")[0];
                historical = System.Text.RegularExpressions.Regex.Replace(historical, @"^:VERSION[^\r\n]*", "", System.Text.RegularExpressions.RegexOptions.Multiline);
                using var setup = new NpgsqlCommand(historical + "CREATE TABLE migrations(name varchar(100),version int); INSERT INTO migrations VALUES ('migrations',1),('" + store + "',2);", db); setup.ExecuteNonQuery();
                UUID id = UUID.Random();
                string sql = groups ? "INSERT INTO os_groups_membership (\"GroupID\",\"PrincipalID\",\"SelectedRoleID\",\"AccessToken\") VALUES (@id,'legacy-user',@id,@id)" :
                    "INSERT INTO im_offline (\"PrincipalID\",\"Message\") VALUES (@id,'preserve-me')";
                using var seed = new NpgsqlCommand(sql, db); seed.Parameters.AddWithValue("id", id.ToString()); seed.ExecuteNonQuery();
                new OpenSim.Data.Migration(db, assembly, store).Update();
                using var verify = new NpgsqlCommand(groups ? "SELECT count(*) FROM os_groups_membership WHERE \"PrincipalID\"='legacy-user'" : "SELECT count(*) FROM im_offline WHERE \"Message\"='preserve-me'", db);
                Assert.Equal(1L, (long)verify.ExecuteScalar());
            }
            finally { ddl.CommandText = "DROP SCHEMA " + schema + " CASCADE"; ddl.ExecuteNonQuery(); }
        }
    }

    [Theory]
    [InlineData("invalid-id", "uuid")]
    [InlineData("2147483648", "integer")]
    [InlineData("not-a-number", "bigint")]
    public void InvalidStorageValuesAreRejectedInsteadOfBecomingZero(string value, string type)
    {
        var method = typeof(PGSQLManager).GetMethod("CreateParameterValue", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(object), typeof(string) }, null);
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { value, type }));
        Assert.True(error.InnerException is FormatException or OverflowException);
    }
}
