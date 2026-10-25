using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL;

/// <summary>PostgreSQL experience storage; unrelated table handlers must not implement this contract.</summary>
public sealed class PGSQLExperienceData : PGSqlFramework, IExperienceData
{
    public PGSQLExperienceData(string connectionString) : base(connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        new PGSQLMigration(connection, GetType().Assembly, "Experience").Update();
    }

    private T Query<T>(string sql, Func<NpgsqlDataReader, T> read, params (string, object)[] parameters)
    {
        using var connection = new NpgsqlConnection(m_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value is UUID uuid ? uuid.Guid : value);
        using var reader = command.ExecuteReader();
        return read(reader);
    }

    private bool Execute(string sql, params (string, object)[] parameters) =>
        Query(sql + " RETURNING 1", reader => reader.Read(), parameters);

    public Dictionary<UUID, bool> GetExperiencePermissions(UUID agent_id) =>
        Query("SELECT experience, allow FROM experience_permissions WHERE avatar=@avatar", reader =>
        {
            var result = new Dictionary<UUID, bool>();
            while (reader.Read()) result[new UUID(reader.GetGuid(0))] = reader.GetBoolean(1);
            return result;
        }, ("avatar", agent_id));

    public bool ForgetExperiencePermissions(UUID agent_id, UUID experience_id) =>
        Execute("DELETE FROM experience_permissions WHERE avatar=@avatar AND experience=@experience", ("avatar", agent_id), ("experience", experience_id));

    public bool SetExperiencePermissions(UUID agent_id, UUID experience_id, bool allow) =>
        Execute("INSERT INTO experience_permissions (avatar,experience,allow) VALUES (@avatar,@experience,@allow) ON CONFLICT (experience,avatar) DO UPDATE SET allow=EXCLUDED.allow",
            ("avatar", agent_id), ("experience", experience_id), ("allow", allow));

    private ExperienceInfoData[] Infos(string condition, params (string, object)[] parameters) =>
        Query("SELECT * FROM experiences WHERE " + condition, reader =>
        {
            var result = new List<ExperienceInfoData>();
            while (reader.Read()) result.Add(new ExperienceInfoData {
                public_id = new UUID((Guid)reader["public_id"]), owner_id = new UUID((Guid)reader["owner_id"]),
                group_id = new UUID((Guid)reader["group_id"]), logo = new UUID((Guid)reader["logo"]),
                name = (string)reader["name"], description = (string)reader["description"],
                marketplace = (string)reader["marketplace"], slurl = (string)reader["slurl"],
                maturity = (int)reader["maturity"], properties = (int)reader["properties"] });
            return result.ToArray();
        }, parameters);

    public ExperienceInfoData[] GetExperienceInfos(UUID[] experiences) => Infos("public_id=ANY(@ids)", ("ids", experiences.Select(id => id.Guid).ToArray()));
    public ExperienceInfoData[] FindExperiences(string search) => Infos("name ILIKE @search", ("search", "%" + search + "%"));
    public UUID[] GetAgentExperiences(UUID agent_id) => Infos("owner_id=@owner", ("owner", agent_id)).Select(info => info.public_id).ToArray();
    public UUID[] GetGroupExperiences(UUID group_id) => GetExperiencesForGroups(new[] { group_id });
    public UUID[] GetExperiencesForGroups(UUID[] groups) => Infos("group_id=ANY(@groups)", ("groups", groups.Select(id => id.Guid).ToArray())).Select(info => info.public_id).ToArray();
    public bool UpdateExperienceInfo(ExperienceInfoData data) => Execute(@"INSERT INTO experiences
        (public_id,owner_id,group_id,logo,name,description,marketplace,slurl,maturity,properties)
        VALUES (@id,@owner,@group,@logo,@name,@description,@marketplace,@slurl,@maturity,@properties)
        ON CONFLICT (public_id) DO UPDATE SET owner_id=EXCLUDED.owner_id,group_id=EXCLUDED.group_id,
        logo=EXCLUDED.logo,name=EXCLUDED.name,description=EXCLUDED.description,marketplace=EXCLUDED.marketplace,
        slurl=EXCLUDED.slurl,maturity=EXCLUDED.maturity,properties=EXCLUDED.properties",
        ("id", data.public_id), ("owner", data.owner_id), ("group", data.group_id), ("logo", data.logo),
        ("name", data.name), ("description", data.description), ("marketplace", data.marketplace),
        ("slurl", data.slurl), ("maturity", data.maturity), ("properties", data.properties));

    public string GetKeyValue(UUID experience, string key) => Query("SELECT value FROM experience_kv WHERE experience=@id AND key=@key", r => r.Read() ? r.GetString(0) : null, ("id", experience), ("key", key));
    public bool SetKeyValue(UUID experience, string key, string val) => Execute("INSERT INTO experience_kv (experience,key,value) VALUES (@id,@key,@value) ON CONFLICT (experience,key) DO UPDATE SET value=EXCLUDED.value", ("id", experience), ("key", key), ("value", val));
    public bool DeleteKey(UUID experience, string key) => Execute("DELETE FROM experience_kv WHERE experience=@id AND key=@key", ("id", experience), ("key", key));
    public int GetKeyCount(UUID experience) => Query("SELECT count(*) FROM experience_kv WHERE experience=@id", r => { r.Read(); return checked((int)r.GetInt64(0)); }, ("id", experience));
    public string[] GetKeys(UUID experience, int start, int count) => Query("SELECT key FROM experience_kv WHERE experience=@id ORDER BY key OFFSET @start LIMIT @count", r => {
        var result = new List<string>(); while (r.Read()) result.Add(r.GetString(0)); return result.ToArray();
    }, ("id", experience), ("start", start), ("count", count));
    public int GetKeyValueSize(UUID experience) => Query("SELECT coalesce(sum(octet_length(key)+octet_length(value)),0)::bigint FROM experience_kv WHERE experience=@id", r => { r.Read(); return checked((int)r.GetInt64(0)); }, ("id", experience));
}
