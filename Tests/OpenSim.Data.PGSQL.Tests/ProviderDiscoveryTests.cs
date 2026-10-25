using System.Reflection;
using OpenSim.Data;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Data.PGSQL;
using OpenSim.Services.Base;
using OpenSim.Server.Base;
using Xunit;

namespace OpenSim.Data.PGSQL.Tests;

public class ProviderDiscoveryTests
{
    public static IEnumerable<object[]> Contracts()
    {
        foreach (Type contract in new[] { typeof(IRegionData), typeof(IUserAccountData),
            typeof(IPresenceData), typeof(IGridUserData), typeof(IFriendsData),
            typeof(IExperienceData), typeof(IXInventoryData), typeof(IAuthenticationData),
            typeof(IAgentPreferencesData), typeof(IAvatarData), typeof(IOfflineIMData),
            typeof(IGroupsData), typeof(IHGTravelingData), typeof(IMuteListData),
            typeof(IProfilesData), typeof(IEstateDataStore), typeof(ISimulationDataStore),
            typeof(IInventoryDataPlugin), typeof(IAssetDataPlugin), typeof(IXAssetDataPlugin), typeof(IFSAssetDataPlugin) })
            yield return new object[] { contract };
    }

    [Theory]
    [MemberData(nameof(Contracts))]
    public void ContractHasExactlyOneConcreteProviderWithPublicConstructor(Type contract)
    {
        Type provider = Assert.Single(typeof(PGSQLRegionData).Assembly.GetTypes(),
            t => t.IsPublic && !t.IsAbstract && !t.ContainsGenericParameters && contract.IsAssignableFrom(t));
        Assert.NotEmpty(provider.GetConstructors());
        var map = provider.GetInterfaceMap(contract);
        Assert.All(map.TargetMethods, method => Assert.False(method.IsAbstract));
    }

    [Fact]
    public void EveryCurrentDataContractIsAudited()
    {
        // These optional contracts have no PostgreSQL implementation in this branch.
        var unsupported = new[] { typeof(ITrustedGridData), typeof(IUserAliasData), typeof(IXGroupData) };
        var audited = Contracts().Select(row => (Type)row[0]).Concat(unsupported).ToHashSet();
        Assert.All(typeof(IRegionData).Assembly.GetTypes().Where(t => t.IsInterface && t.Namespace == "OpenSim.Data"),
            contract => Assert.Contains(contract, audited));
        Assert.All(unsupported, contract => Assert.DoesNotContain(typeof(PGSQLRegionData).Assembly.GetTypes(),
            type => contract.IsAssignableFrom(type)));
    }

    [Fact]
    public void ProviderAndContractsShareLoadContextAndBuild()
    {
        Assembly provider = typeof(PGSQLRegionData).Assembly;
        Assembly contracts = typeof(IRegionData).Assembly;
        Assert.Same(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(contracts),
            System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(provider));
        Assert.Equal(contracts.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            provider.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        Assert.All(provider.GetReferencedAssemblies().Where(a => a.Name.StartsWith("OpenSim.")),
            a => Assert.NotNull(Assembly.Load(a)));
    }
}
