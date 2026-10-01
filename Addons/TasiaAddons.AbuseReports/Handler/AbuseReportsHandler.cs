using System;
using System.IO;
using System.Net;
using System.Reflection;
using Nini.Config;
using log4net;
using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;

namespace TasiaAddons.AbuseReports.Handler
{
    public class AbuseReportsHandler
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly IAbuseReportsService m_service;

        public AbuseReportsHandler(IAbuseReportsService service)
        {
            m_service = service;
        }

        public bool HandleReport(AbuseReportData report)
        {
            if (m_service == null)
            {
                m_log.Warn("[ABUSE REPORTS HANDLER]: No service configured");
                return false;
            }

            return m_service.ReportAbuse(report);
        }
    }
}
