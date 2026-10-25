using System.Net;
using Microsoft.Extensions.Logging;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.GridService;
using OpenSim.Services.Interfaces;
using OpenSim.Services.InventoryService;
using OpenSim.Services.UserAccountService;
using OpenSim.Services.AuthenticationService;
using OpenSim.Services.LLLoginService;
using Xunit;

namespace OpenSim.Data.PGSQL.Tests;

public class GridLoginSmokeTests
{
    [PostgreSqlFact]
    public void RegisterRegionCreateAccountInventoryAndLoginUsingPostgreSql()
    {
        using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddConsole());
        var previousFactory = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = loggerFactory;
        try
        {
            string connection = Environment.GetEnvironmentVariable("IGRID_PGSQL_TEST_CONNECTION");
            var settings = new Npgsql.NpgsqlConnectionStringBuilder(connection);
            Assert.Equal("igrid_provider_test", settings.Database);
            Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost" });
            MainConsole.Instance = new OpenSim.Framework.Console.MockConsole();
            var config = new IniConfigSource();
            config.AddConfig("DatabaseService").Set("StorageProvider", typeof(OpenSim.Data.PGSQL.PGSQLRegionData).Assembly.Location);
            config.Configs["DatabaseService"].Set("ConnectionString", connection);
            config.AddConfig("GridService").Set("DefaultRegionFlags", "DefaultRegion, FallbackRegion");
            foreach (string section in new[] { "UserAccountService", "GridUserService", "AuthenticationService", "InventoryService", "PresenceService", "AvatarService", "FriendsService", "LoginService" })
                config.AddConfig(section);
            var loginConfig = config.Configs["LoginService"];
            var modules = new Dictionary<string, Type> {
                ["UserAccountService"] = typeof(UserAccountService), ["GridUserService"] = typeof(GridUserService),
                ["AuthenticationService"] = typeof(PasswordAuthenticationService), ["InventoryService"] = typeof(XInventoryService),
                ["GridService"] = typeof(GridService), ["PresenceService"] = typeof(OpenSim.Services.PresenceService.PresenceService),
                ["AvatarService"] = typeof(OpenSim.Services.AvatarService.AvatarService), ["FriendsService"] = typeof(OpenSim.Services.Friends.FriendsService) };
            foreach (var (name, type) in modules)
                loginConfig.Set(name, type.Assembly.Location + ":" + type.Name);
            var grid = new GridService(config);
            var region = new OpenSim.Services.Interfaces.GridRegion {
                RegionID = UUID.Random(), RegionName = "PGSQL Smoke " + UUID.Random(),
                RegionLocX = 256000, RegionLocY = 256000, RegionSizeX = 256, RegionSizeY = 256,
                ExternalHostName = "127.0.0.1", HttpPort = 59000,
                InternalEndPoint = new IPEndPoint(IPAddress.Loopback, 59000),
                ServerURI = "http://127.0.0.1:59000/" };
            // Unique scope isolates region coordinates from other smoke test executions.
            UUID scope = UUID.Random();
            Assert.Equal(string.Empty, grid.RegisterRegion(scope, region));
            Assert.Equal(region.RegionID, grid.GetRegionByUUID(scope, region.RegionID).RegionID);
            Assert.NotNull(grid.GetNeighbours(scope, region.RegionID));

            var users = new UserAccountService(config);
            var account = new UserAccount(scope, UUID.Random(), "Smoke", "Test" + UUID.Random().ToString().Replace("-", ""), "");
            Assert.True(users.StoreUserAccount(account));
            var authentication = new PasswordAuthenticationService(config, users);
            const string password = "disposable-local-smoke-password";
            Assert.True(authentication.SetPassword(account.PrincipalID, password));
            var inventory = new XInventoryService(config);
            Assert.True(inventory.CreateUserInventory(account.PrincipalID));
            Assert.NotEmpty(inventory.GetInventorySkeleton(account.PrincipalID));
            var gridUsers = new GridUserService(config);
            Assert.True(gridUsers.SetHome(account.PrincipalID.ToString(), region.RegionID, new Vector3(128,128,25), Vector3.UnitY));

            ISimulationService simulation = DispatchProxy.Create<ISimulationService, AcceptingSimulation>();
            var login = new LLLoginService(config, simulation, null);
            var response = login.Login(account.FirstName, account.LastName, "$1$" + Util.Md5Hash(password), "home", scope,
                "test", "PGSQL smoke", "", "", new IPEndPoint(IPAddress.Loopback, 0)).ToHashtable();
            Assert.True(response["login"]?.ToString() == "true", $"Login failed: {response["message"]}");
            Assert.Equal(account.PrincipalID.ToString(), response["agent_id"]?.ToString());
            Assert.True(((AcceptingSimulation)(object)simulation).AgentCreated);
            Assert.True(grid.DeregisterRegion(region.RegionID));
        }
        finally
        {
            LoggerProvider.LoggerFactory = previousFactory;
        }
    }

    // The real login, authentication, presence, grid and inventory services use PostgreSQL.
    // Only the viewer-facing simulator handshake is a test double; no viewer/UDP claim is made.
    public class AcceptingSimulation : DispatchProxy
    {
        public bool AgentCreated { get; private set; }
        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            var parameters = targetMethod.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
                if (parameters[i].IsOut && parameters[i].ParameterType == typeof(string).MakeByRefType()) args[i] = "";
            if (targetMethod.Name == "CreateAgent") AgentCreated = true;
            if (targetMethod.ReturnType == typeof(bool)) return true;
            throw new NotSupportedException(targetMethod.Name);
        }
    }
}
