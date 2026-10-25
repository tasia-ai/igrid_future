using System.Data.Common;
using MySqlConnector;
using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;
using Nini.Config;

namespace TasiaAddons.GridFunctions;

internal sealed record VirtualAvatarProfile(
    UUID VirtualID,
    string AvatarName,
    string FirstName,
    string LastName,
    string DisplayName,
    UUID OwnerID,
    string AboutText,
    UUID ProfileImage,
    UUID FirstLifeImage,
    string FirstLifeText,
    string WebUrl,
    string UserTitle,
    string BornOn,
    int Created,
    int Updated);

/// <summary>Small provider-aware store shared by the account and profile service adapters.</summary>
internal sealed class VirtualAvatarStore
{
    private readonly bool postgres;
    private readonly string connectionString;

    public VirtualAvatarStore(IConfigSource config)
    {
        IConfig options = config.Configs["TasiaGridFunctions"];
        string provider = options?.GetString("DatabaseProvider", "PostgreSQL") ?? "PostgreSQL";
        string databaseName = options?.GetString("DatabaseName", "robust") ?? "robust";
        IConfig database = config.Configs["DatabaseService"];
        string source = database?.GetString("ConnectionString", string.Empty) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("TasiaGridFunctions requires DatabaseService ConnectionString.");

        postgres = provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase);
        if (!postgres && !provider.Equals("MySQL", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TasiaGridFunctions DatabaseProvider must be PostgreSQL or MySQL.");
        connectionString = postgres
            ? new NpgsqlConnectionStringBuilder(source) { Database = databaseName }.ConnectionString
            : new MySqlConnectionStringBuilder(source) { Database = databaseName }.ConnectionString;
    }

    public VirtualAvatarProfile Find(UUID id) => QueryOne(
        postgres
            ? "SELECT \"VirtualID\", \"AvatarName\", \"FirstName\", \"LastName\", \"DisplayName\", \"OwnerID\", \"AboutText\", \"ProfileImage\", \"FirstLifeImage\", \"FirstLifeText\", \"WebUrl\", \"UserTitle\", \"BornOn\", \"Created\", \"Updated\" FROM \"TasiaVirtualAvatars\" WHERE \"VirtualID\" = @id::uuid AND \"Enabled\" = 1"
            : "SELECT VirtualID, AvatarName, FirstName, LastName, DisplayName, OwnerID, AboutText, ProfileImage, FirstLifeImage, FirstLifeText, WebUrl, UserTitle, BornOn, Created, Updated FROM TasiaVirtualAvatars WHERE VirtualID = @id AND Enabled = 1",
        command => Add(command, "id", id.ToString()));

    public VirtualAvatarProfile Find(UUID scopeID, string firstName, string lastName) => QueryOne(
        postgres
            ? "SELECT \"VirtualID\", \"AvatarName\", \"FirstName\", \"LastName\", \"DisplayName\", \"OwnerID\", \"AboutText\", \"ProfileImage\", \"FirstLifeImage\", \"FirstLifeText\", \"WebUrl\", \"UserTitle\", \"BornOn\", \"Created\", \"Updated\" FROM \"TasiaVirtualAvatars\" WHERE lower(\"FirstName\") = lower(@first) AND lower(\"LastName\") = lower(@last) AND \"Enabled\" = 1 LIMIT 1"
            : "SELECT VirtualID, AvatarName, FirstName, LastName, DisplayName, OwnerID, AboutText, ProfileImage, FirstLifeImage, FirstLifeText, WebUrl, UserTitle, BornOn, Created, Updated FROM TasiaVirtualAvatars WHERE lower(FirstName) = lower(@first) AND lower(LastName) = lower(@last) AND Enabled = 1 LIMIT 1",
        command => { Add(command, "first", firstName); Add(command, "last", lastName); });

    public List<VirtualAvatarProfile> Search(string query, int limit = 100)
    {
        string pattern = "%" + (query ?? string.Empty).Trim().Replace("%", "\\%").Replace("_", "\\_") + "%";
        string sql = postgres
            ? "SELECT \"VirtualID\", \"AvatarName\", \"FirstName\", \"LastName\", \"DisplayName\", \"OwnerID\", \"AboutText\", \"ProfileImage\", \"FirstLifeImage\", \"FirstLifeText\", \"WebUrl\", \"UserTitle\", \"BornOn\", \"Created\", \"Updated\" FROM \"TasiaVirtualAvatars\" WHERE \"Enabled\" = 1 AND (\"AvatarName\" ILIKE @query OR \"DisplayName\" ILIKE @query) ORDER BY \"AvatarName\" LIMIT @limit"
            : "SELECT VirtualID, AvatarName, FirstName, LastName, DisplayName, OwnerID, AboutText, ProfileImage, FirstLifeImage, FirstLifeText, WebUrl, UserTitle, BornOn, Created, Updated FROM TasiaVirtualAvatars WHERE Enabled = 1 AND (AvatarName LIKE @query OR DisplayName LIKE @query) ORDER BY AvatarName LIMIT @limit";

        using DbConnection connection = Open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        Add(command, "query", pattern);
        Add(command, "limit", Math.Clamp(limit, 1, 500));
        using DbDataReader reader = command.ExecuteReader();
        List<VirtualAvatarProfile> result = [];
        while (reader.Read())
            result.Add(Read(reader));
        return result;
    }

    public List<VirtualAvatarProfile> FindMany(IEnumerable<string> ids)
    {
        List<VirtualAvatarProfile> result = [];
        foreach (string text in ids ?? [])
            if (UUID.TryParse(text, out UUID id) && id != UUID.Zero)
            {
                VirtualAvatarProfile profile = Find(id);
                if (profile != null)
                    result.Add(profile);
            }
        return result;
    }

    public bool UpdateDisplayName(UUID id, string displayName)
    {
        string sql = postgres
            ? "UPDATE \"TasiaVirtualAvatars\" SET \"DisplayName\" = @name, \"Updated\" = @now WHERE \"VirtualID\" = @id::uuid AND \"Enabled\" = 1"
            : "UPDATE TasiaVirtualAvatars SET DisplayName = @name, Updated = @now WHERE VirtualID = @id AND Enabled = 1";
        using DbConnection connection = Open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        Add(command, "name", displayName ?? string.Empty);
        Add(command, "now", (int)Util.UnixTimeSinceEpoch());
        Add(command, "id", id.ToString());
        return command.ExecuteNonQuery() == 1;
    }

    private VirtualAvatarProfile QueryOne(string sql, Action<DbCommand> bind)
    {
        using DbConnection connection = Open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        using DbDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private DbConnection Open()
    {
        DbConnection connection = postgres
            ? new NpgsqlConnection(connectionString)
            : new MySqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@" + name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static VirtualAvatarProfile Read(DbDataReader reader)
    {
        static string Text(DbDataReader r, int index) => r.IsDBNull(index) ? string.Empty : Convert.ToString(r.GetValue(index)) ?? string.Empty;
        static UUID Id(DbDataReader r, int index) => UUID.TryParse(Text(r, index), out UUID id) ? id : UUID.Zero;
        static int Number(DbDataReader r, int index) => r.IsDBNull(index) ? 0 : Convert.ToInt32(r.GetValue(index));
        return new VirtualAvatarProfile(Id(reader, 0), Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4),
            Id(reader, 5), Text(reader, 6), Id(reader, 7), Id(reader, 8), Text(reader, 9), Text(reader, 10),
            Text(reader, 11), Text(reader, 12), Number(reader, 13), Number(reader, 14));
    }
}
