using Microsoft.Extensions.Logging.Abstractions;
using Nini.Config;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base.Hosting;
using OpenSim.Server.GridServer;
using Xunit;

namespace OpenSim.Data.PGSQL.Tests;

public class GridStartupFailureTests
{
    [Fact]
    public void EmptyServiceListCannotReportSuccessfulInitialization()
    {
        var config = new IniConfigSource();
        config.AddConfig("Startup");
        var loader = new GridServiceConnectorLoader(NullLogger<GridServiceConnectorLoader>.Instance, new TestServers());
        Assert.Throws<InvalidOperationException>(() => loader.LoadConnectors(config));
    }

    [Fact]
    public void MissingConfiguredServiceAbortsInitialization()
    {
        var config = new IniConfigSource();
        config.AddConfig("Startup");
        config.AddConfig("ServiceList").Set("Grid", "missing-provider.dll:MissingGridConnector");
        var loader = new GridServiceConnectorLoader(NullLogger<GridServiceConnectorLoader>.Instance, new TestServers());
        Assert.Throws<InvalidOperationException>(() => loader.LoadConnectors(config));
    }

    private sealed class TestServers : IMainServerAccessor
    {
        public IHttpServer DefaultServer { get; } = new BaseHttpServer(0, false, null, null);
        public IHttpServer GetHttpServer(uint port) => DefaultServer;
        public void Stop() { }
    }
}
