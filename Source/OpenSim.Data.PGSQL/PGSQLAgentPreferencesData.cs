/* 
 * Copyright (c) Contributors, http://opensimulator.org/ 
 * See CONTRIBUTORS.TXT for a full list of copyright holders. 
 * 
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer. 
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in
 *       the documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission. 
 * 
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL;

/// <summary>
/// A PGSQL Interface for Agent Preferences
/// </summary>
public class PGSQLAgentPreferencesData : PGSQLGenericTableHandler<AgentPreferencesData>, IAgentPreferencesData, IExperienceData
{
    public PGSQLAgentPreferencesData(string connectionString, string realm)
        : base(connectionString, realm, "AgentPrefs")
    {
    }

    public PGSQLAgentPreferencesData(string connectionString)
        : this(connectionString, "default")
    {
    }

    public AgentPreferencesData GetPrefs(UUID agentID)
    {
        AgentPreferencesData[] ret = Get("PrincipalID", agentID.ToString());

        if (ret.Length == 0)
            return null;
        return ret[0];
    }

    // IExperienceData implementation
    public Dictionary<UUID, bool> GetExperiencePermissions(UUID agent_id)
    {
        return new Dictionary<UUID, bool>();
    }

    public bool ForgetExperiencePermissions(UUID agent_id, UUID experience_id)
    {
        return false;
    }

    public bool SetExperiencePermissions(UUID agent_id, UUID experience_id, bool allow)
    {
        return false;
    }

    public ExperienceInfoData[] GetExperienceInfos(UUID[] experiences)
    {
        return new ExperienceInfoData[0];
    }

    public ExperienceInfoData[] FindExperiences(string search)
    {
        return new ExperienceInfoData[0];
    }

    public UUID[] GetAgentExperiences(UUID agent_id)
    {
        return new UUID[0];
    }

    public UUID[] GetGroupExperiences(UUID agent_id)
    {
        return new UUID[0];
    }

    public UUID[] GetExperiencesForGroups(UUID[] groups)
    {
        return new UUID[0];
    }

    public bool UpdateExperienceInfo(ExperienceInfoData data)
    {
        return false;
    }

    public string GetKeyValue(UUID experience, string key)
    {
        return null;
    }

    public bool SetKeyValue(UUID experience, string key, string val)
    {
        return false;
    }

    public bool DeleteKey(UUID experience, string key)
    {
        return false;
    }

    public int GetKeyCount(UUID experience)
    {
        return 0;
    }

    public string[] GetKeys(UUID experience, int start, int count)
    {
        return new string[0];
    }

    public int GetKeyValueSize(UUID experience)
    {
        return 0;
    }
}

