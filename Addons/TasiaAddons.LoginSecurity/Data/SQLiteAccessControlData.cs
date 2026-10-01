using System;
using System.Data;
using Microsoft.Data.Sqlite;
using System.Reflection;
using log4net;
using TasiaAddons.LoginSecurity.Data;

namespace TasiaAddons.LoginSecurity.Data
{
    public class SQLiteAccessControlData : IAccessControlData
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly SqliteConnection m_connection;
        private bool m_initialized;

        public SQLiteAccessControlData(string connectionString)
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
                    CREATE TABLE IF NOT EXISTS ipbans (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ip TEXT NOT NULL UNIQUE,
                        created_at TEXT DEFAULT (datetime('now'))
                    );
                    CREATE TABLE IF NOT EXISTS hw_bans (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        mac TEXT,
                        id0 TEXT,
                        created_at TEXT DEFAULT (datetime('now'))
                    );
                    CREATE INDEX IF NOT EXISTS idx_ipbans_ip ON ipbans(ip);
                    CREATE INDEX IF NOT EXISTS idx_hw_bans_mac ON hw_bans(mac);
                    CREATE INDEX IF NOT EXISTS idx_hw_bans_id0 ON hw_bans(id0);
                ";
                cmd.ExecuteNonQuery();
            }

            m_initialized = true;
            m_log.Info("[ACCESS CONTROL]: SQLite database initialized");
        }

        public bool IsIPBanned(string ip)
        {
            if (string.IsNullOrEmpty(ip))
                return false;

            using (var cmd = m_connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM ipbans WHERE ip = @ip";
                cmd.Parameters.AddWithValue("@ip", ip);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
        }

        public bool IsHardwareBanned(string mac, string id0)
        {
            if (string.IsNullOrEmpty(mac) && string.IsNullOrEmpty(id0))
                return false;

            using (var cmd = m_connection.CreateCommand())
            {
                bool hasMac = !string.IsNullOrEmpty(mac);
                bool hasId0 = !string.IsNullOrEmpty(id0);

                if (hasMac && hasId0)
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM hw_bans WHERE mac = @mac OR id0 = @id0";
                    cmd.Parameters.AddWithValue("@mac", mac);
                    cmd.Parameters.AddWithValue("@id0", id0);
                }
                else if (hasMac)
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM hw_bans WHERE mac = @mac";
                    cmd.Parameters.AddWithValue("@mac", mac);
                }
                else
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM hw_bans WHERE id0 = @id0";
                    cmd.Parameters.AddWithValue("@id0", id0);
                }

                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
        }

        public bool BanIPAddress(string ip)
        {
            if (string.IsNullOrEmpty(ip))
                return false;

            try
            {
                using (var cmd = m_connection.CreateCommand())
                {
                    cmd.CommandText = "INSERT OR IGNORE INTO ipbans (ip) VALUES (@ip)";
                    cmd.Parameters.AddWithValue("@ip", ip);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[ACCESS CONTROL]: Failed to ban IP {0}: {1}", ip, ex.Message);
                return false;
            }
        }

        public bool UnbanIPAddress(string ip)
        {
            if (string.IsNullOrEmpty(ip))
                return false;

            try
            {
                using (var cmd = m_connection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM ipbans WHERE ip = @ip";
                    cmd.Parameters.AddWithValue("@ip", ip);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[ACCESS CONTROL]: Failed to unban IP {0}: {1}", ip, ex.Message);
                return false;
            }
        }

        public bool BanMacAddress(string mac)
        {
            if (string.IsNullOrEmpty(mac))
                return false;

            try
            {
                using (var cmd = m_connection.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO hw_bans (mac) VALUES (@mac)";
                    cmd.Parameters.AddWithValue("@mac", mac);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[ACCESS CONTROL]: Failed to ban MAC {0}: {1}", mac, ex.Message);
                return false;
            }
        }

        public bool UnbanMacAddress(string mac)
        {
            if (string.IsNullOrEmpty(mac))
                return false;

            try
            {
                using (var cmd = m_connection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM hw_bans WHERE mac = @mac";
                    cmd.Parameters.AddWithValue("@mac", mac);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[ACCESS CONTROL]: Failed to unban MAC {0}: {1}", mac, ex.Message);
                return false;
            }
        }

        public bool BanID0(string id0)
        {
            if (string.IsNullOrEmpty(id0))
                return false;

            try
            {
                using (var cmd = m_connection.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO hw_bans (id0) VALUES (@id0)";
                    cmd.Parameters.AddWithValue("@id0", id0);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[ACCESS CONTROL]: Failed to ban ID0 {0}: {1}", id0, ex.Message);
                return false;
            }
        }

        public bool UnbanID0(string id0)
        {
            if (string.IsNullOrEmpty(id0))
                return false;

            try
            {
                using (var cmd = m_connection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM hw_bans WHERE id0 = @id0";
                    cmd.Parameters.AddWithValue("@id0", id0);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[ACCESS CONTROL]: Failed to unban ID0 {0}: {1}", id0, ex.Message);
                return false;
            }
        }
    }
}
