using System;
using System.Collections;
using System.Net;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using TasiaAddons.LoginSecurity.Data;

namespace TasiaAddons.LoginSecurity.ToSPrompt
{
    public class ToSPromptModule : ISharedRegionModule
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private string m_TOS_URL = string.Empty;
        private int m_TOS_Date = 0;
        private bool m_Enabled = false;
        private IToSAcceptanceData? m_Database;
        private string m_dbConnectionString = string.Empty;

        public string Name => "ToSPromptModule";
        public Type ReplaceableInterface => null;

        // Static so all region instances share the same enabled state
        private static bool s_initialized = false;

        public void PostInitialise() { }

        public void Initialise(IConfigSource config)
        {
            IConfig? toscfg = config.Configs["ToS"];
            if (toscfg == null)
            {
                Log.Info("[TOS]: No [ToS] config section found, module disabled");
                return;
            }

            m_Enabled = toscfg.GetBoolean("Enabled", false);
            if (!m_Enabled)
            {
                Log.Info("[TOS]: Module disabled in configuration");
                return;
            }

            m_TOS_URL = toscfg.GetString("TOS_URL", string.Empty);
            m_TOS_Date = toscfg.GetInt("TOS_Date", 1);

            if (string.IsNullOrEmpty(m_TOS_URL))
            {
                Log.Warn("[TOS]: TOS_URL not configured, module disabled");
                m_Enabled = false;
                return;
            }

            // Database config
            string storageProvider = toscfg.GetString("StorageProvider",
                "TasiaAddons.LoginSecurity.dll:TasiaAddons.LoginSecurity.Data.SQLiteToSAcceptanceData");
            m_dbConnectionString = toscfg.GetString("ConnectionString",
                "Data Source=Data/accesscontrol.db;Version=3");

            try
            {
                m_Database = new SQLiteToSAcceptanceData(m_dbConnectionString);

                if (m_Database is null)
                {
                    Log.Error("[TOS]: Failed to load ToS acceptance database");
                    m_Enabled = false;
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error("[TOS]: Failed to initialize database: " + ex.Message);
                m_Enabled = false;
                return;
            }

            if (!s_initialized)
            {
                s_initialized = true;
                RegisterCommands();
            }

            Log.InfoFormat("[TOS]: Module enabled. URL: {0}, Version: {1}", m_TOS_URL, m_TOS_Date);
        }

        public void AddRegion(Scene scene) { }

        public void RemoveRegion(Scene scene) { }

        public void RegionLoaded(Scene scene) { }

        public void Close() { }

        #region Console Commands

        private void RegisterCommands()
        {
            MainConsole.Instance.Commands.AddCommand("tos", false,
                "tos status",
                "tos status",
                "Show current ToS version and URL",
                HandleStatusCommand);

            MainConsole.Instance.Commands.AddCommand("tos", false,
                "tos version",
                "tos version",
                "Show current ToS version",
                HandleStatusCommand);

            MainConsole.Instance.Commands.AddCommand("tos", false,
                "tos set version <number>",
                "tos set version <number>",
                "Set ToS version (users must re-accept)",
                HandleSetVersionCommand);

            MainConsole.Instance.Commands.AddCommand("tos", false,
                "tos reset",
                "tos reset",
                "Reset all acceptances - everyone must re-accept current ToS",
                HandleResetCommand);

            MainConsole.Instance.Commands.AddCommand("tos", false,
                "tos reset user <name>",
                "tos reset user <name>",
                "Reset a specific user's ToS acceptance",
                HandleResetUserCommand);

            MainConsole.Instance.Commands.AddCommand("tos", false,
                "tos check <name>",
                "tos check <name>",
                "Check if a user has accepted the current ToS",
                HandleCheckCommand);

            MainConsole.Instance.Commands.AddCommand("tos", false,
                "tos url [new_url]",
                "tos url [new_url]",
                "Show or set the ToS URL",
                HandleUrlCommand);
        }

        private void HandleStatusCommand(string module, string[] cmd)
        {
            if (!m_Enabled)
            {
                Log.Info("[TOS]: Module is DISABLED");
                return;
            }

            Log.InfoFormat("[TOS]: Status");
            Log.InfoFormat("[TOS]:   Enabled:  Yes");
            Log.InfoFormat("[TOS]:   Version:  {0}", m_TOS_Date);
            Log.InfoFormat("[TOS]:   URL:      {0}", m_TOS_URL);
        }

        private void HandleSetVersionCommand(string module, string[] cmd)
        {
            if (cmd.Length < 4)
            {
                Log.Info("[TOS]: Usage: tos set version <number>");
                return;
            }

            if (int.TryParse(cmd[3], out int newVersion))
            {
                m_TOS_Date = newVersion;
                Log.InfoFormat("[TOS]: ToS version set to {0}. Use 'tos reset' to force all users to re-accept.", newVersion);
            }
            else
            {
                Log.Info("[TOS]: Invalid version number");
            }
        }

        private void HandleResetCommand(string module, string[] cmd)
        {
            if (m_Database is null)
            {
                Log.Error("[TOS]: Database not available");
                return;
            }

            if (m_Database.ResetAll(m_TOS_Date))
            {
                Log.InfoFormat("[TOS]: All users must now re-accept ToS v{0}", m_TOS_Date);
            }
            else
            {
                Log.Error("[TOS]: Failed to reset acceptances");
            }
        }

        private void HandleResetUserCommand(string module, string[] cmd)
        {
            if (m_Database is null)
            {
                Log.Error("[TOS]: Database not available");
                return;
            }

            if (cmd.Length < 4)
            {
                Log.Info("[TOS]: Usage: tos reset user <name>");
                return;
            }

            string userName = cmd[3];
            // Try to find user by name - we need to iterate regions to find UserAccountService
            // For now use the name as-is as the user ID
            if (m_Database.RecordAcceptance(userName, 0))
            {
                Log.InfoFormat("[TOS]: Reset ToS acceptance for user '{0}'. They must re-accept.", userName);
            }
            else
            {
                Log.ErrorFormat("[TOS]: Failed to reset ToS for user '{0}'", userName);
            }
        }

        private void HandleCheckCommand(string module, string[] cmd)
        {
            if (m_Database is null)
            {
                Log.Error("[TOS]: Database not available");
                return;
            }

            if (cmd.Length < 3)
            {
                Log.Info("[TOS]: Usage: tos check <name>");
                return;
            }

            string userName = cmd[2];
            int userVersion = m_Database.GetUserVersion(userName);
            bool accepted = userVersion >= m_TOS_Date;

            Log.InfoFormat("[TOS]: User '{0}' - Accepted version: {1}, Current: {2} - {3}",
                userName, userVersion, m_TOS_Date,
                accepted ? "ACCEPTED" : "NOT ACCEPTED");
        }

        private void HandleUrlCommand(string module, string[] cmd)
        {
            if (cmd.Length >= 3)
            {
                m_TOS_URL = string.Join(" ", cmd, 2, cmd.Length - 2);
                Log.InfoFormat("[TOS]: ToS URL set to: {0}", m_TOS_URL);
            }
            else
            {
                Log.InfoFormat("[TOS]: Current ToS URL: {0}", m_TOS_URL);
            }
        }

        #endregion

        #region Public API (called from login chain)

        public bool IsEnabled() => m_Enabled;

        public bool CheckToSAcceptance(string userId)
        {
            if (!m_Enabled || m_Database is null)
                return true;

            return m_Database.HasAccepted(userId, m_TOS_Date);
        }

        public bool RecordAcceptance(string userId)
        {
            if (m_Database is null)
                return false;

            return m_Database.RecordAcceptance(userId, m_TOS_Date);
        }

        public string GetToSURL() => m_TOS_URL;

        public int GetToSVersion() => m_TOS_Date;

        public string GetLoginBlockedMessage()
        {
            return string.Format(
                "You must accept our Terms of Service before logging in.\n" +
                "Please visit: {0}\n" +
                "After accepting, try logging in again.",
                m_TOS_URL);
        }

        #endregion
    }
}
