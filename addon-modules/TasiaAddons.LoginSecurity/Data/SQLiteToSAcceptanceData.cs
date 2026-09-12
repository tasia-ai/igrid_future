using System;
using Microsoft.Data.Sqlite;
using System.Reflection;
using log4net;
using TasiaAddons.LoginSecurity.Data;

namespace TasiaAddons.LoginSecurity.Data
{
    public class SQLiteToSAcceptanceData : IToSAcceptanceData
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly SqliteConnection m_connection;
        private bool m_initialized;

        public SQLiteToSAcceptanceData(string connectionString)
        {
            m_connection = new SqliteConnection(connectionString);
            m_connection.Open();
            InitializeTables();
        }

        private void InitializeTables()
        {
            if (m_initialized)
                return;

            using (var cmd = m_connection.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS tos_acceptances (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        user_id TEXT NOT NULL,
                        tos_version INTEGER NOT NULL,
                        accepted_at TEXT DEFAULT (datetime('now')),
                        UNIQUE(user_id)
                    );
                    CREATE INDEX IF NOT EXISTS idx_tos_user ON tos_acceptances(user_id);
                ";
                cmd.ExecuteNonQuery();
            }

            m_initialized = true;
            m_log.Info("[TOS]: SQLite acceptance database initialized");
        }

        public bool HasAccepted(string userId, int tosVersion)
        {
            if (string.IsNullOrEmpty(userId) || tosVersion <= 0)
                return false;

            using (var cmd = m_connection.CreateCommand())
            {
                cmd.CommandText = "SELECT tos_version FROM tos_acceptances WHERE user_id = @uid";
                cmd.Parameters.AddWithValue("@uid", userId);
                object result = cmd.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                    return false;
                return Convert.ToInt32(result) >= tosVersion;
            }
        }

        public bool RecordAcceptance(string userId, int tosVersion)
        {
            if (string.IsNullOrEmpty(userId))
                return false;

            try
            {
                using (var cmd = m_connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        INSERT INTO tos_acceptances (user_id, tos_version, accepted_at)
                        VALUES (@uid, @ver, datetime('now'))
                        ON CONFLICT(user_id) DO UPDATE SET tos_version = @ver, accepted_at = datetime('now')
                        WHERE tos_version < @ver";
                    cmd.Parameters.AddWithValue("@uid", userId);
                    cmd.Parameters.AddWithValue("@ver", tosVersion);
                    cmd.ExecuteNonQuery();
                }
                return true;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[TOS]: Failed to record acceptance for {0}: {1}", userId, ex.Message);
                return false;
            }
        }

        public bool ResetAll(int newVersion)
        {
            try
            {
                using (var cmd = m_connection.CreateCommand())
                {
                    // Delete all acceptances below the new version - forces everyone to re-accept
                    cmd.CommandText = "DELETE FROM tos_acceptances WHERE tos_version < @ver";
                    cmd.Parameters.AddWithValue("@ver", newVersion);
                    int deleted = cmd.ExecuteNonQuery();
                    m_log.InfoFormat("[TOS]: Reset complete. {0} user(s) need to re-accept ToS v{1}", deleted, newVersion);
                }
                return true;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[TOS]: Failed to reset: {0}", ex.Message);
                return false;
            }
        }

        public int GetUserVersion(string userId)
        {
            if (string.IsNullOrEmpty(userId))
                return 0;

            using (var cmd = m_connection.CreateCommand())
            {
                cmd.CommandText = "SELECT tos_version FROM tos_acceptances WHERE user_id = @uid";
                cmd.Parameters.AddWithValue("@uid", userId);
                object result = cmd.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                    return 0;
                return Convert.ToInt32(result);
            }
        }
    }
}
