using System.Reflection;
using System.Text.RegularExpressions;

namespace OpenSim.Framework.Tests;

public class MigrationResourceAuditTests
{
    private static string ReadResource(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded migration resource: {name}");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static string VersionSection(string text, int version)
    {
        MatchCollection markers = Regex.Matches(text, @"(?m)^:VERSION\s+(\d+)");
        Match marker = markers.Cast<Match>().Single(match => int.Parse(match.Groups[1].Value) == version);
        int start = marker.Index + marker.Length;
        Match? next = markers.Cast<Match>().FirstOrDefault(match => match.Index > marker.Index);
        int end = next?.Index ?? text.Length;
        return text[start..end];
    }

    [Theory]
    [InlineData("MigrationAudit.PGSQL.EstateStore.migrations", 18)]
    [InlineData("MigrationAudit.MySQL.EstateStore.migrations", 40)]
    [InlineData("MigrationAudit.SQLite.EstateStore.migrations", 15)]
    public void EstateMigrationForwardRepairRetainsBothFeatureFamilies(string resource, int repairVersion)
    {
        string sql = ReadResource(resource);
        int[] versions = Regex.Matches(sql, @"(?m)^:VERSION\s+(\d+)")
            .Cast<Match>()
            .Select(match => int.Parse(match.Groups[1].Value))
            .ToArray();
        Assert.Equal(versions.Length, versions.Distinct().Count());

        string repair = VersionSection(sql, repairVersion);
        Assert.Contains("estate_allowed_experiences", repair, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("estate_key_experiences", repair, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("estate_blocked_experiences", repair, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("estate_manager_roles", repair, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", repair, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE", repair, StringComparison.OrdinalIgnoreCase);
        if (resource == "MigrationAudit.PGSQL.EstateStore.migrations")
        {
            Assert.Contains("ADD COLUMN IF NOT EXISTS \"EstateOwnerGroup\" uuid NOT NULL", repair,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DEFAULT '00000000-0000-0000-0000-000000000000'::uuid", repair,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("MigrationAudit.PGSQL.EstateStore.migrations", 17)]
    [InlineData("MigrationAudit.MySQL.EstateStore.migrations", 39)]
    [InlineData("MigrationAudit.SQLite.EstateStore.migrations", 14)]
    public void CollidingEstateAuthorityMigrationsDoNotBlindlyReAddExistingOwnerColumn(
        string resource, int collidingVersion)
    {
        string section = VersionSection(ReadResource(resource), collidingVersion);
        Assert.DoesNotContain("ADD COLUMN \"EstateOwnerGroup\"", section, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ADD COLUMN `EstateOwnerGroup`", section, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ADD COLUMN EstateOwnerGroup", section, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserAccountV7SafelyNormalizesBlankAndNullTimestampsAndRejectsOverflow()
    {
        string sql = ReadResource("MigrationAudit.PGSQL.UserAccount.migrations");
        string v7 = VersionSection(sql, 7);

        Assert.Contains("btrim(\"NameChanged\"::text) <> ''", v7, StringComparison.Ordinal);
        Assert.Contains("THEN 0", v7, StringComparison.Ordinal);
        Assert.Contains("::numeric > 2147483647", v7, StringComparison.Ordinal);
        Assert.Contains("SET \"NameChanged\" = 0 WHERE \"NameChanged\" IS NULL", v7, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN \"NameChanged\" SET NOT NULL", v7, StringComparison.Ordinal);
        Assert.Contains("SET \"DisplayName\" = '' WHERE \"DisplayName\" IS NULL", v7, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN \"DisplayName\" SET NOT NULL", v7, StringComparison.Ordinal);
    }

    [Fact]
    public void UserAccountV8RepairsEarlierV7NullabilityWithoutDroppingRows()
    {
        string sql = ReadResource("MigrationAudit.PGSQL.UserAccount.migrations");
        string v8 = VersionSection(sql, 8);

        Assert.Contains("SET \"DisplayName\" = '' WHERE \"DisplayName\" IS NULL", v8, StringComparison.Ordinal);
        Assert.Contains("SET \"NameChanged\" = 0 WHERE \"NameChanged\" IS NULL", v8, StringComparison.Ordinal);
        Assert.Contains("SET \"UserCountry\" = '' WHERE \"UserCountry\" IS NULL", v8, StringComparison.Ordinal);
        Assert.Contains("WHERE \"NameChanged\" < 0", v8, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", v8, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", v8, StringComparison.OrdinalIgnoreCase);
    }
}
