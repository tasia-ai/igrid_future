using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace TasiaAddons.AbuseReports
{
    public interface IAbuseReportsData
    {
        bool Store(AbuseReportData data);
    }
}
