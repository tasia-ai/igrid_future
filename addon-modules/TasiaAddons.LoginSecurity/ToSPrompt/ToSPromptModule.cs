using System;
using System.IO;
using System.Net;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace TasiaAddons.LoginSecurity.ToSPrompt
{
    public class ToSPromptModule : ISharedRegionModule
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private string m_TOS_URL = string.Empty;
        private int m_TOS_Date = 0;
        private bool m_Enabled = false;
        private IUserAccountService? m_UserAccountService;

        public string Name => "ToSPromptModule";
        public Type ReplaceableInterface => null;

        public void PostInitialise()
        {
        }

        public void Initialise(IConfigSource config)
        {
            IConfig? toscfg = config.Configs["ToS"];
            if (toscfg == null)
                return;

            m_Enabled = toscfg.GetBoolean("Enabled", false);
            if (!m_Enabled)
            {
                Log.Info("[ToS PROMPT]: Module disabled in configuration");
                return;
            }

            m_TOS_URL = toscfg.GetString("TOS_URL", string.Empty);
            m_TOS_Date = toscfg.GetInt("TOS_Date", 0);

            if (string.IsNullOrEmpty(m_TOS_URL))
            {
                Log.Warn("[ToS PROMPT]: TOS_URL not configured, module disabled");
                m_Enabled = false;
                return;
            }

            Log.Info($"[ToS PROMPT]: Module enabled. TOS URL: {m_TOS_URL}, TOS Date: {m_TOS_Date}");
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_UserAccountService = scene.UserAccountService;
        }

        public void Close()
        {
        }

        public bool CheckToSAcceptance(string userID, int currentTOSDate)
        {
            if (!m_Enabled || m_TOS_Date == 0)
                return true;

            return currentTOSDate >= m_TOS_Date;
        }

        public string? GetToSURL()
        {
            return m_Enabled ? m_TOS_URL : null;
        }

        public int GetToSDate()
        {
            return m_TOS_Date;
        }
    }
}
