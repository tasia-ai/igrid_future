/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using Npgsql;
using NpgsqlTypes;

namespace OpenSim.Data.PGSQL
{
    /// <summary>
    /// PostgreSQL storage for the Experience service.
    /// </summary>
    public class PGSQLExperienceData : PGSqlFramework, IExperienceData
    {
        private readonly string connectionString;

        public PGSQLExperienceData(string connectionString)
            : base(connectionString)
        {
            this.connectionString = connectionString;

            using (NpgsqlConnection connection = new NpgsqlConnection(connectionString))
            {
                connection.Open();
                Migration migration = new Migration(connection, GetType().Assembly, "Experience");
                migration.Update();
            }
        }

        private static NpgsqlParameter Uuid(string name, UUID value)
        {
            return new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = value.Guid };
        }

        private static ExperienceInfoData ReadInfo(NpgsqlDataReader reader)
        {
            return new ExperienceInfoData
            {
                public_id = UUID.Parse(reader["public_id"].ToString()),
                owner_id = UUID.Parse(reader["owner_id"].ToString()),
                group_id = UUID.Parse(reader["group_id"].ToString()),
                name = reader["name"].ToString(),
                description = reader["description"].ToString(),
                logo = UUID.Parse(reader["logo"].ToString()),
                marketplace = reader["marketplace"].ToString(),
                slurl = reader["slurl"].ToString(),
                maturity = Convert.ToInt32(reader["maturity"]),
                properties = Convert.ToInt32(reader["properties"])
            };
        }

        public Dictionary<UUID, bool> GetExperiencePermissions(UUID agentId)
        {
            Dictionary<UUID, bool> result = new Dictionary<UUID, bool>();
            const string sql = "SELECT experience, allow FROM experience_permissions WHERE avatar = :avatar";

            using (NpgsqlConnection connection = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.Add(Uuid("avatar", agentId));
                connection.Open();
                using (NpgsqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        UUID experience;
                        if (UUID.TryParse(reader["experience"].ToString(), out experience))
                            result[experience] = reader.GetBoolean(reader.GetOrdinal("allow"));
                    }
                }
            }

            return result;
        }

        public bool ForgetExperiencePermissions(UUID agentId, UUID experienceId)
        {
            const string sql = "DELETE FROM experience_permissions WHERE avatar = :avatar AND experience = :experience";
            using (NpgsqlConnection connection = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.Add(Uuid("avatar", agentId));
                command.Parameters.Add(Uuid("experience", experienceId));
                connection.Open();
                return command.ExecuteNonQuery() > 0;
            }
        }

        public bool SetExperiencePermissions(UUID agentId, UUID experienceId, bool allow)
        {
            const string sql = @"INSERT INTO experience_permissions (experience, avatar, allow)
                VALUES (:experience, :avatar, :allow)
                ON CONFLICT (experience, avatar) DO UPDATE SET allow = EXCLUDED.allow";

            using (NpgsqlConnection connection = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.Add(Uuid("experience", experienceId));
                command.Parameters.Add(Uuid("avatar", agentId));
                command.Parameters.AddWithValue("allow", allow);
                connection.Open();
                return command.ExecuteNonQuery() > 0;
            }
        }

        public ExperienceInfoData[] GetExperienceInfos(UUID[] experiences)
        {
            if (experiences == null || experiences.Length == 0)
                return new ExperienceInfoData[0];

            const string sql = "SELECT * FROM experiences WHERE public_id = ANY(@ids)";
            List<ExperienceInfoData> result = new List<ExperienceInfoData>();

            using (NpgsqlConnection connection = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                {
                    Value = experiences.Select(id => id.Guid).ToArray()
                });
                connection.Open();
                using (NpgsqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        result.Add(ReadInfo(reader));
                }
            }

            return result.ToArray();
        }

        public ExperienceInfoData[] FindExperiences(string search)
        {
            const string sql = "SELECT * FROM experiences WHERE name ILIKE @search ORDER BY name";
            List<ExperienceInfoData> result = new List<ExperienceInfoData>();

            using (NpgsqlConnection connection = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.AddWithValue("search", "%" + (search ?? string.Empty) + "%");
                connection.Open();
                using (NpgsqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        result.Add(ReadInfo(reader));
                }
            }

            return result.ToArray();
        }

        private UUID[] SelectExperienceIds(string column, UUID id)
        {
            string sql = "SELECT public_id FROM experiences WHERE " + column + " = :id";
            List<UUID> result = new List<UUID>();

            using (NpgsqlConnection connection = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.Add(Uuid("id", id));
                connection.Open();
                using (NpgsqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        result.Add(UUID.Parse(reader["public_id"].ToString()));
                }
            }

            return result.ToArray();
        }

        public UUID[] GetAgentExperiences(UUID agentId)
        {
            return SelectExperienceIds("owner_id", agentId);
        }

        public UUID[] GetGroupExperiences(UUID groupId)
        {
            return SelectExperienceIds("group_id", groupId);
        }

        public UUID[] GetExperiencesForGroups(UUID[] groups)
        {
            if (groups == null || groups.Length == 0)
                return new UUID[0];

            const string sql = "SELECT public_id FROM experiences WHERE group_id = ANY(@ids)";
            List<UUID> result = new List<UUID>();
            using (NpgsqlConnection connection = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                {
                    Value = groups.Select(id => id.Guid).ToArray()
                });
                connection.Open();
                using (NpgsqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        result.Add(UUID.Parse(reader["public_id"].ToString()));
                }
            }
            return result.ToArray();
        }

        public bool UpdateExperienceInfo(ExperienceInfoData data)
        {
            if (data == null)
                return false;

            const string sql = @"INSERT INTO experiences
                (public_id, owner_id, name, description, group_id, logo, marketplace, slurl, maturity, properties)
                VALUES (:public_id, :owner_id, :name, :description, :group_id, :logo, :marketplace, :slurl, :maturity, :properties)
                ON CONFLICT (public_id) DO UPDATE SET
                    owner_id = EXCLUDED.owner_id,
                    name = EXCLUDED.name,
                    description = EXCLUDED.description,
                    group_id = EXCLUDED.group_id,
                    logo = EXCLUDED.logo,
                    marketplace = EXCLUDED.marketplace,
                    slurl = EXCLUDED.slurl,
                    maturity = EXCLUDED.maturity,
                    properties = EXCLUDED.properties";

            using (NpgsqlConnection connection = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.Add(Uuid("public_id", data.public_id));
                command.Parameters.Add(Uuid("owner_id", data.owner_id));
                command.Parameters.AddWithValue("name", data.name ?? string.Empty);
                command.Parameters.AddWithValue("description", data.description ?? string.Empty);
                command.Parameters.Add(Uuid("group_id", data.group_id));
                command.Parameters.Add(Uuid("logo", data.logo));
                command.Parameters.AddWithValue("marketplace", data.marketplace ?? string.Empty);
                command.Parameters.AddWithValue("slurl", data.slurl ?? string.Empty);
                command.Parameters.AddWithValue("maturity", data.maturity);
                command.Parameters.AddWithValue("properties", data.properties);
                connection.Open();
                return command.ExecuteNonQuery() > 0;
            }
        }

        public string GetKeyValue(UUID experience, string key)
        {
            const string sql = "SELECT value FROM experience_kv WHERE experience = :experience AND key = :key LIMIT 1";
            using (NpgsqlConnection connection = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.Add(Uuid("experience", experience));
                command.Parameters.AddWithValue("key", key);
                connection.Open();
                object value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? null : value.ToString();
            }
        }

        public bool SetKeyValue(UUID experience, string key, string value)
        {
            const string sql = @"INSERT INTO experience_kv (experience, key, value)
                VALUES (:experience, :key, :value)
                ON CONFLICT (experience, key) DO UPDATE SET value = EXCLUDED.value";
            using (NpgsqlConnection connection = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.Add(Uuid("experience", experience));
                command.Parameters.AddWithValue("key", key);
                command.Parameters.AddWithValue("value", value ?? string.Empty);
                connection.Open();
                return command.ExecuteNonQuery() > 0;
            }
        }

        public bool DeleteKey(UUID experience, string key)
        {
            const string sql = "DELETE FROM experience_kv WHERE experience = :experience AND key = :key";
            using (NpgsqlConnection connection = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.Add(Uuid("experience", experience));
                command.Parameters.AddWithValue("key", key);
                connection.Open();
                return command.ExecuteNonQuery() > 0;
            }
        }

        public int GetKeyCount(UUID experience)
        {
            const string sql = "SELECT count(*) FROM experience_kv WHERE experience = :experience";
            using (NpgsqlConnection connection = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.Add(Uuid("experience", experience));
                connection.Open();
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        public string[] GetKeys(UUID experience, int start, int count)
        {
            const string sql = "SELECT key FROM experience_kv WHERE experience = :experience ORDER BY key LIMIT :count OFFSET :start";
            List<string> result = new List<string>();
            using (NpgsqlConnection connection = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.Add(Uuid("experience", experience));
                command.Parameters.AddWithValue("count", count);
                command.Parameters.AddWithValue("start", start);
                connection.Open();
                using (NpgsqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        result.Add(reader["key"].ToString());
                }
            }
            return result.ToArray();
        }

        public int GetKeyValueSize(UUID experience)
        {
            const string sql = "SELECT COALESCE(sum(octet_length(key) + octet_length(value)), 0)::bigint FROM experience_kv WHERE experience = :experience";
            using (NpgsqlConnection connection = new NpgsqlConnection(connectionString))
            using (NpgsqlCommand command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.Add(Uuid("experience", experience));
                connection.Open();
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }
    }
}
