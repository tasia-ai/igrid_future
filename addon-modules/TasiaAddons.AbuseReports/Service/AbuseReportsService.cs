using System;
using System.IO;
using System.Net;
using System.Reflection;
using Nini.Config;
using log4net;
using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenMetaverse;

namespace TasiaAddons.AbuseReports.Service
{
    public class AbuseReportsService : AbuseReportsServiceBase, IAbuseReportsService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public AbuseReportsService(IConfigSource config) : base(config)
        {
            m_log.Debug("[ABUSE REPORTS SERVICE]: Starting abuse reports service");
        }

        public bool ReportAbuse(AbuseReportData report)
        {
            if (m_Database == null)
            {
                m_log.Warn("[ABUSE REPORTS SERVICE]: No database configured");
                return false;
            }

            try
            {
                return m_Database.Store(report);
            }
            catch (Exception ex)
            {
                m_log.Error("[ABUSE REPORTS SERVICE]: Failed to store abuse report", ex);
                return false;
            }
        }
    }
}
