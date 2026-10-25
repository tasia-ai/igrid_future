using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TasiaAddons.WebScripts;
using Xunit;

namespace TasiaAddons.WebScripts.Tests;

public class SecurityTests : IDisposable
{
    private readonly RSA key = RSA.Create(3072);
    private const string Url = "https://scripts.example.org/hello.json";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(2000000000);
    private WebScriptSecurity Policy => new(new[] { "scripts.example.org" }, key.ExportSubjectPublicKeyInfoPem());

    private byte[] Envelope(string script = "default { state_entry() { llOwnerSay(\"hello\"); } }", string url = Url, long expires = 2000003600)
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        string signature = Convert.ToBase64String(key.SignData(WebScriptSecurity.SigningBytes(url, expires, encoded), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return JsonSerializer.SerializeToUtf8Bytes(new { version = 1, url, expires, script = encoded, signature });
    }
    [Fact] public void ValidSignedScriptIsForcedToNormalLsl() => Assert.StartsWith("//lsl\ndefault", Policy.VerifyEnvelope(Envelope(), new Uri(Url), Now));
    [Fact] public void TamperedSourceIsRejected() {
        string json = Encoding.UTF8.GetString(Envelope());
        using var document = JsonDocument.Parse(json);
        string changed = json.Replace(document.RootElement.GetProperty("script").GetString(), Convert.ToBase64String(Encoding.UTF8.GetBytes("default{}")));
        Assert.ThrowsAny<Exception>(() => Policy.VerifyEnvelope(Encoding.UTF8.GetBytes(changed), new Uri(Url), Now));
    }
    [Fact] public void AnotherPublisherCannotSign() {
        using var other = RSA.Create(3072);
        var wrong = new WebScriptSecurity(new[] { "scripts.example.org" }, other.ExportSubjectPublicKeyInfoPem());
        Assert.Throws<InvalidOperationException>(() => wrong.VerifyEnvelope(Envelope(), new Uri(Url), Now));
    }
    [Theory]
    [InlineData(1999999999L)] [InlineData(2000000000L)] [InlineData(2000086401L)]
    public void ExpiredOrOverlongValidityIsRejected(long expires) => Assert.Throws<InvalidOperationException>(() => Policy.VerifyEnvelope(Envelope(expires: expires), new Uri(Url), Now));
    [Fact] public void SignatureIsBoundToExactUrl() => Assert.Throws<InvalidOperationException>(() => Policy.VerifyEnvelope(Envelope(), new Uri(Url + "?other=1"), Now));
    [Fact] public void DuplicateFieldsAreRejected() {
        byte[] json = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Envelope()).Replace("{", "{\"version\":1,", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => Policy.VerifyEnvelope(json, new Uri(Url), Now));
    }
    [Theory]
    [InlineData("//c#\nclass Attack {}")]
    [InlineData("//YEngine:c#\nclass Attack {}")]
    [InlineData("\uFEFFdefault{}")]
    [InlineData("default{}\0")]
    public void NonLslPayloadsAreRejected(string source) => Assert.Throws<InvalidOperationException>(() => Policy.VerifyEnvelope(Envelope(source), new Uri(Url), Now));
    [Fact] public void OversizedBodyIsRejected() => Assert.Throws<InvalidOperationException>(() => Policy.VerifyEnvelope(new byte[100001], new Uri(Url), Now));
    [Fact] public void OversizedScriptIsRejected() => Assert.ThrowsAny<Exception>(() => Policy.VerifyEnvelope(Envelope(new string(' ', 65537)), new Uri(Url), Now));
    [Theory]
    [InlineData("http://scripts.example.org/a")]
    [InlineData("https://scripts.example.org:8443/a")]
    [InlineData("https://scripts.example.org.evil.test/a")]
    [InlineData("https://user:secret@scripts.example.org/a")]
    [InlineData("https://scripts.example.org/a#fragment")]
    [InlineData("https://127.0.0.1/a")]
    [InlineData("file:///etc/passwd")]
    public void UntrustedDestinationsAreRejected(string url) => Assert.Throws<InvalidOperationException>(() => Policy.ValidateUrl(url));
    [Theory]
    [InlineData("127.0.0.1")] [InlineData("10.0.0.1")] [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")] [InlineData("169.254.169.254")]
    [InlineData("100.100.100.200")] [InlineData("0.0.0.0")] [InlineData("224.0.0.1")]
    [InlineData("192.0.2.1")] [InlineData("198.18.0.1")] [InlineData("203.0.113.1")]
    [InlineData("::1")] [InlineData("::ffff:127.0.0.1")] [InlineData("fc00::1")]
    [InlineData("fe80::1")] [InlineData("2001:db8::1")] [InlineData("2002:7f00:1::")]
    public void PrivateAndSpecialAddressesAreRejected(string ip) => Assert.False(WebScriptSecurity.IsPublicAddress(IPAddress.Parse(ip)));
    [Theory] [InlineData("8.8.8.8")] [InlineData("2606:4700:4700::1111")]
    public void PublicAddressesAreAccepted(string ip) => Assert.True(WebScriptSecurity.IsPublicAddress(IPAddress.Parse(ip)));
    [Fact] public void ModuleIsDisabledUnlessExplicitlyConfigured() {
        using var module = new ModuleLifetime(new WebScriptsModule());
        module.Module.Initialise(new Nini.Config.IniConfigSource());
        Assert.Throws<InvalidOperationException>(() => module.Module.maRunScriptFromWeb(OpenMetaverse.UUID.Random(), OpenMetaverse.UUID.Random(), Url));
    }
    [Fact] public void EnabledModuleRejectsMissingOwnerAndCreatorGrants() {
        using var module = new ModuleLifetime(new WebScriptsModule());
        var config = new Nini.Config.IniConfigSource(); config.AddConfig("WebScripts").Set("Enabled", true);
        Assert.Throws<InvalidOperationException>(() => module.Module.Initialise(config));
    }
    private sealed class ModuleLifetime(WebScriptsModule module) : IDisposable {
        public WebScriptsModule Module { get; } = module; public void Dispose() => Module.Close();
    }
    public void Dispose() => key.Dispose();
}
