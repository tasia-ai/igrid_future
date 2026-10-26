using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.Scripting.ScriptModuleComms;
using Xunit;

namespace InWorldz.Phlox.Tests;

[Collection("phlox-yengine")]
public class ModApiTests
{
    [Fact]
    public void VariadicInvokePassesArgumentsAndConvertsResultsThroughModuleComms()
    {
        using var h = new SchedulerHarness(config => config.AddConfig("OSSL").Set("AllowMODFunctions", true));
        var comms = new ScriptModuleCommsModule();
        comms.Initialise(h.Config);
        comms.AddRegion(h.Scene);
        comms.RegionLoaded(h.Scene);

        var fixture = new InvocationFixture();
        comms.RegisterScriptInvocation(fixture, new[] { "Echo", "Notify" });

        const string source = @"default
{
    state_entry()
    {
        llOwnerSay(modInvokeS(""Echo"", ""hello"", 42));
        modInvokeN(""Notify"", ""done"", 7);
    }
}";
        var compile = PhloxCompiler.Compile(source);
        Assert.False(compile.HasErrors(), compile.Report);
        var item = h.RezScript(source);
        h.Prim.ParentGroup.ResumeScripts();

        Assert.True(h.PumpUntil(() => fixture.Notified && fixture.EchoInput != null,
            TimeSpan.FromSeconds(20)), "MOD calls did not reach ScriptModuleComms: " + h.Diagnose(item) +
            $" notified={fixture.Notified} result={fixture.Notification} frame={h.DumpFrame(item)}");
        Assert.Equal("hello:42", fixture.EchoInput);
        Assert.Equal("done:7", fixture.Notification);
        comms.RemoveRegion(h.Scene);
    }

    private sealed class InvocationFixture
    {
        public bool Notified { get; private set; }
        public string Notification { get; private set; }
        public string EchoInput { get; private set; }

        public string Echo(UUID host, UUID script, string text, int number)
        {
            EchoInput = $"{text}:{number}";
            return EchoInput;
        }

        public void Notify(UUID host, UUID script, string text, int number)
        {
            Notification = $"{text}:{number}";
            Notified = true;
        }
    }
}
