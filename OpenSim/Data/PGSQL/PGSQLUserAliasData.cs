/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 */

using System;
using System.Collections.Generic;
using System.Data;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL
{
    /// <summary>
    /// PostgreSQL storage for local user aliases.
    /// </summary>
    public class PGSQLUserAliasData : PGSQLGenericTableHandler<UserAliasData>, IUserAliasData
    {
        public PGSQLUserAliasData(string connectionString, string realm)
            : base(connectionString, realm, "UserAlias")
        {
        }

        public UserAliasData Get(int id)
        {
            UserAliasData[] result = Get("Id", id.ToString());
            return result.Length == 0 ? null : result[0];
        }

        public UserAliasData GetUserForAlias(UUID aliasID)
        {
            UserAliasData[] result = Get("AliasID", aliasID.ToString());
            return result.Length == 0 ? null : result[0];
        }

        public List<UserAliasData> GetUserAliases(UUID userID)
        {
            UserAliasData[] result = Get("UserID", userID.ToString());
            return result.Length == 0 ? null : new List<UserAliasData>(result);
        }

        public new bool Store(UserAliasData data)
        {
            if (data == null)
                return false;

            if (data.Id == 0)
            {
                const string insert = @"INSERT INTO ""UserAlias""
                    (""AliasID"", ""UserID"", ""Description"")
                    VALUES (:AliasID, :UserID, :Description)
                    RETURNING ""Id""";

                using (Npgsql.NpgsqlConnection connection = new Npgsql.NpgsqlConnection(m_ConnectionString))
                using (Npgsql.NpgsqlCommand command = new Npgsql.NpgsqlCommand(insert, connection))
                {
                    command.Parameters.Add(m_database.CreateParameter("AliasID", data.AliasID));
                    command.Parameters.Add(m_database.CreateParameter("UserID", data.UserID));
                    command.Parameters.Add(m_database.CreateParameter("Description", data.Description ?? string.Empty));
                    connection.Open();
                    data.Id = Convert.ToInt32(command.ExecuteScalar());
                    return data.Id > 0;
                }
            }

            const string update = @"UPDATE ""UserAlias""
                SET ""AliasID"" = :AliasID,
                    ""UserID"" = :UserID,
                    ""Description"" = :Description
                WHERE ""Id"" = :Id";

            using (Npgsql.NpgsqlConnection connection = new Npgsql.NpgsqlConnection(m_ConnectionString))
            using (Npgsql.NpgsqlCommand command = new Npgsql.NpgsqlCommand(update, connection))
            {
                command.Parameters.Add(m_database.CreateParameter("AliasID", data.AliasID));
                command.Parameters.Add(m_database.CreateParameter("UserID", data.UserID));
                command.Parameters.Add(m_database.CreateParameter("Description", data.Description ?? string.Empty));
                command.Parameters.Add(m_database.CreateParameter("Id", data.Id));
                connection.Open();
                return command.ExecuteNonQuery() > 0;
            }
        }

        public new bool Delete(string field, string value)
        {
            return base.Delete(field, value);
        }
    }
}
