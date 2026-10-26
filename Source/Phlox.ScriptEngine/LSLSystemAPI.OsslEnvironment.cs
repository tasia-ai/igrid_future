/*
 * Phlox Script Engine Integration
 * OSSL environment functions ported from OSSL_Api.cs:6171-6374.
 */

using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Scripting;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;

namespace Phlox.ScriptEngine
{
    public partial class LSLSystemAPI
    {
        /// <summary>OSSL_Api.cs:6171-6214. Per-agent environment replacement is Moderate.</summary>
        public int osReplaceAgentEnvironment(string agentkey, int transition, string daycycle)
        {
            if (!string.IsNullOrEmpty(m_ScriptEngine.Ossl.Test(ThreatLevel.Moderate,
                    "osReplaceAgentEnvironment", World, m_host, OsslItem)))
                return -2;

            if (!UUID.TryParse(agentkey, out UUID agentid))
                return -4;

            ScenePresence sp = World.GetScenePresence(agentid);
            if (sp is null || sp.IsChildAgent || sp.IsNPC || sp.IsInTransit)
                return -4;

            IEnvironmentModule envModule = World.RequestModuleInterface<IEnvironmentModule>();
            if (string.IsNullOrEmpty(daycycle) || daycycle == ScriptBaseClass.NULL_KEY)
            {
                sp.Environment = null;
                envModule.WindlightRefreshForced(sp, transition);
                return 1;
            }

            UUID envID = ScriptUtils.GetAssetIdFromKeyOrItemName(m_host, daycycle);
            if (envID.IsZero())
                return -3;

            AssetBase asset = World.AssetService.Get(envID.ToString());
            if (asset is null || asset.Type != (byte)AssetType.Settings)
                return -3;

            try
            {
                OSD oenv = OSDParser.Deserialize(asset.Data);
                ViewerEnvironment venv = envModule.GetRegionEnvironment().Clone();
                if (!venv.CycleFromOSD(oenv))
                    return -3;
                sp.Environment = venv;
                envModule.WindlightRefreshForced(sp, transition);
            }
            catch
            {
                sp.Environment = null;
                envModule.WindlightRefreshForced(sp, transition);
                return -5;
            }
            return 1;
        }

        /// <summary>OSSL_Api.cs:6216-6265. Replaces the environment of the parcel containing the host.</summary>
        public int osReplaceParcelEnvironment(int transition, string daycycle)
        {
            IEnvironmentModule envModule = World.RequestModuleInterface<IEnvironmentModule>();
            if (!World.RegionInfo.EstateSettings.AllowEnvironmentOverride)
                return -1;

            ILandObject parcel = World.LandChannel.GetLandObject(m_host.GetWorldPosition().X,
                m_host.GetWorldPosition().Y);
            if (parcel is null)
                return -2;

            if (!World.Permissions.CanEditParcelProperties(m_host.OwnerID, parcel,
                    GroupPowers.AllowEnvironment, true))
                return -3;

            ViewerEnvironment venv = parcel.LandData.Environment is null
                ? envModule.GetRegionEnvironment().Clone()
                : parcel.LandData.Environment;

            bool changed = false;
            if (!string.IsNullOrEmpty(daycycle) && daycycle != ScriptBaseClass.NULL_KEY)
            {
                UUID envID = ScriptUtils.GetAssetIdFromKeyOrItemName(m_host, daycycle);
                if (envID.IsZero())
                    return -4;

                AssetBase asset = World.AssetService.Get(envID.ToString());
                if (asset is null || asset.Type != (byte)AssetType.Settings)
                    return -4;
                try
                {
                    OSD oenv = OSDParser.Deserialize(asset.Data);
                    if (!venv.CycleFromOSD(oenv))
                        return -5;
                    changed = true;
                }
                catch
                {
                    return -5;
                }
            }

            if (changed)
            {
                parcel.StoreEnvironment(venv);
                envModule.WindlightRefresh(transition, false);
            }
            return 1;
        }

        /// <summary>OSSL_Api.cs:6267-6345. Replaces the region environment and optional cycle parameters.</summary>
        public int osReplaceRegionEnvironment(int transition, string daycycle, float daylen, float dayoffset,
            float altitude1, float altitude2, float altitude3)
        {
            IEnvironmentModule envModule = World.RequestModuleInterface<IEnvironmentModule>();
            if (!World.Permissions.CanIssueEstateCommand(m_host.OwnerID, true))
                return -3;

            ViewerEnvironment venv = envModule.GetRegionEnvironment().Clone();
            bool changed = false;
            if (!string.IsNullOrEmpty(daycycle) && daycycle != ScriptBaseClass.NULL_KEY)
            {
                UUID envID = ScriptUtils.GetAssetIdFromKeyOrItemName(m_host, daycycle);
                if (envID.IsZero())
                    return -4;

                AssetBase asset = World.AssetService.Get(envID.ToString());
                if (asset is null || asset.Type != (byte)AssetType.Settings)
                    return -4;
                try
                {
                    OSD oenv = OSDParser.Deserialize(asset.Data);
                    if (!venv.CycleFromOSD(oenv))
                        return -5;
                    changed = true;
                }
                catch
                {
                    return -5;
                }
            }

            if (daylen >= 4 && daylen <= 24 * 7)
            {
                int oldLength = venv.DayLength;
                venv.DayLength = (int)(daylen * 3600f);
                changed |= oldLength != venv.DayLength;
            }

            if (dayoffset >= -11.5f && dayoffset <= 11.5f)
            {
                int oldOffset = venv.DayLength;
                if (dayoffset <= 0)
                    dayoffset += 24;
                venv.DayOffset = (int)(dayoffset * 3600f);
                changed |= oldOffset != venv.DayOffset;
            }

            bool needSort = false;
            if (altitude1 > 0 && altitude1 < 4000 && venv.Altitudes[0] != altitude1)
            {
                venv.Altitudes[0] = altitude1;
                needSort = true;
            }
            if (altitude2 > 0 && altitude2 < 4000 && venv.Altitudes[1] != altitude2)
            {
                venv.Altitudes[1] = altitude2;
                needSort = true;
            }
            if (altitude3 > 0 && altitude2 < 4000 && venv.Altitudes[2] != altitude3)
            {
                venv.Altitudes[2] = altitude3;
                needSort = true;
            }
            if (needSort)
            {
                venv.SortAltitudes();
                changed = true;
            }

            if (changed)
            {
                envModule.StoreOnRegion(venv);
                envModule.WindlightRefresh(transition);
            }
            return 1;
        }

        /// <summary>OSSL_Api.cs:6347-6374. Resets the host parcel or region environment.</summary>
        public int osResetEnvironment(int parcelOrRegion, int transition)
        {
            IEnvironmentModule envModule = World.RequestModuleInterface<IEnvironmentModule>();
            if (parcelOrRegion > 0)
            {
                if (!World.RegionInfo.EstateSettings.AllowEnvironmentOverride)
                    return -1;

                ILandObject parcel = World.LandChannel.GetLandObject(m_host.GetWorldPosition().X,
                    m_host.GetWorldPosition().Y);
                if (parcel is null)
                    return -2;

                if (!World.Permissions.CanEditParcelProperties(m_host.OwnerID, parcel,
                        GroupPowers.AllowEnvironment, true))
                    return -3;
                if (parcel.LandData.Environment is null)
                    return 1;

                parcel.StoreEnvironment(null);
                envModule.WindlightRefresh(transition, false);
                return 1;
            }

            if (!World.Permissions.CanIssueEstateCommand(m_host.OwnerID, true))
                return -3;

            envModule.StoreOnRegion(null);
            envModule.WindlightRefresh(transition);
            return 1;
        }
    }
}
