/*
 * Phlox Script Engine Integration
 * OSSL wind-volume functions ported from OSSL_Api.cs:1712-1770.
 */

using System.Collections.Generic;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;

namespace Phlox.ScriptEngine
{
    public partial class LSLSystemAPI
    {
        /// <summary>Creates a wind volume, returning its handle or a WV_ERR_ code.</summary>
        public int osWindVolumeCreate(LSLList rules)
        {
            m_ScriptEngine.Ossl.Check(ThreatLevel.High, "osWindVolumeCreate", World, m_host, OsslItem);

            IWindVolumeStore store = World.RequestModuleInterface<IWindModule>()?.WindVolumes;
            if (store is null || !store.Enabled) return WindVolumeError.Disabled;
            if (!WindVolumeAccess.MayChangeWind(World, m_host.OwnerID)) return WindVolumeError.NotAllowed;

            var plainRules = new List<object>(rules.Length);
            foreach (object value in rules.Data)
            {
                plainRules.Add(value switch
                {
                    int i => i,
                    float f => f,
                    Vector3 v => v,
                    _ => value,
                });
            }

            int bad = WindVolumeRules.TryParse(plainRules, out WindVolumeSpec spec);
            if (bad != WindVolumeError.Ok) return bad;
            return store.Create(m_host.ParentGroup.UUID, m_host.OwnerID, spec);
        }

        /// <summary>Removes a wind volume created by this object, returning 0 or a WV_ERR_ code.</summary>
        public int osWindVolumeRemove(int handle)
        {
            m_ScriptEngine.Ossl.Check(ThreatLevel.High, "osWindVolumeRemove", World, m_host, OsslItem);

            IWindVolumeStore store = World.RequestModuleInterface<IWindModule>()?.WindVolumes;
            if (store is null || !store.Enabled) return WindVolumeError.Disabled;
            if (!WindVolumeAccess.MayChangeWind(World, m_host.OwnerID)) return WindVolumeError.NotAllowed;
            return store.Remove(handle, m_host.ParentGroup.UUID);
        }

        /// <summary>Returns this object's wind-volume handles, oldest first.</summary>
        public LSLList osWindVolumes()
        {
            m_ScriptEngine.Ossl.Check(ThreatLevel.None, "osWindVolumes", World, m_host, OsslItem);

            var result = new List<object>();
            IWindVolumeStore store = World.RequestModuleInterface<IWindModule>()?.WindVolumes;
            if (store is not null && store.Enabled && WindVolumeAccess.MayChangeWind(World, m_host.OwnerID))
            {
                foreach (WindVolume volume in store.List(m_host.ParentGroup.UUID))
                    result.Add(volume.Id);
            }
            return new LSLList(result);
        }
    }
}
