using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InWorldz.Phlox.Tests;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.Scripting.ScriptModuleComms;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using TasiaAddons.WebScripts;
using Xunit;

namespace TasiaAddons.WebScripts.Tests;

[Collection("phlox-yengine")]
public class ModuleIntegrationTests
{
    [Theory]
    [InlineData("InWorldz.Phlox")]
    [InlineData("YEngine")]
    public void SignedSourceIsInstalledAndRunsInTheNormalEngine(string engine)
    {
        using var publisher = RSA.Create(3072);
        using var h = new SchedulerHarness(config => config.AddConfig("OSSL").Set("AllowMODFunctions", true), withYEngine: engine == "YEngine");
        var comms = new ScriptModuleCommsModule(); comms.Initialise(h.Config); comms.AddRegion(h.Scene); comms.RegionLoaded(h.Scene);
        const string url = "https://scripts.example.org/hello.json";
        const string source = "default { state_entry() { llSay(0, \"signed-script-ran\"); } }";
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(source)); long expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, url, expires, script = encoded,
            signature = Convert.ToBase64String(publisher.SignData(WebScriptSecurity.SigningBytes(url, expires, encoded), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) });
        string directory = Path.Combine(Path.GetTempPath(), "webscripts-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var caller = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, UUID.Random(), UUID.Random(), "trusted caller", "default { state_entry() { maRunScriptFromWeb(\"" + url + "\"); } }");
        // A caller can only install into its own prim; grants are checked inside the module, not only the API bridge.
        caller.OwnerID = h.Prim.OwnerID; caller.CreatorID = h.Prim.OwnerID;
        File.WriteAllText(Path.Combine(directory, "publisher.pem"), publisher.ExportSubjectPublicKeyInfoPem());
        IConfig settings = h.Config.AddConfig("WebScripts"); settings.Set("Enabled", true);
        settings.Set("AllowedOwners", h.Prim.OwnerID.ToString()); settings.Set("AllowedScriptCreators", caller.CreatorID.ToString());
        settings.Set("AllowedHosts", "scripts.example.org"); settings.Set("PublisherPublicKeyFile", Path.Combine(directory, "publisher.pem")); settings.Set("ScriptEngine", engine);
        var module = new WebScriptsModule(policy => new WebScriptDownloader(policy, new EnvelopeHandler(body)));
        try {
            module.Initialise(h.Config); module.AddRegion(h.Scene); module.RegionLoaded(h.Scene);
            Assert.True(h.Prim.Inventory.CreateScriptInstance(caller, 0, false, engine, 0));
            h.Prim.ParentGroup.ResumeScripts();
            Assert.True(h.PumpUntil(() => h.Said.Contains("signed-script-ran"), TimeSpan.FromSeconds(30)), "Downloaded signed LSL did not execute.");
            var installed = Assert.Single(h.Prim.Inventory.GetInventoryItems(), item => item.Name.StartsWith("WebScript "));
            Assert.Equal(h.Prim.OwnerID, installed.OwnerID); Assert.Equal(0, installed.PermsMask); Assert.Equal(UUID.Zero, installed.PermsGranter);
            Assert.Throws<InvalidOperationException>(() => module.maRunScriptFromWeb(h.Prim.UUID, caller.ItemID, url));
        }
        finally { module.Close(); comms.RemoveRegion(h.Scene); Directory.Delete(directory, true); }
    }

    private sealed class EnvelopeHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        }
    }
}
