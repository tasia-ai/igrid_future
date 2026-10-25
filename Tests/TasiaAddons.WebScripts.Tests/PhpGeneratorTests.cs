using System.Diagnostics;
using System.Security.Cryptography;
using TasiaAddons.WebScripts;
using Xunit;

namespace TasiaAddons.WebScripts.Tests;

public class PhpGeneratorTests
{
    [Fact]
    public async Task PhpOutputIsAcceptedByDotNetAndCannotBeReusedAtAnotherUrl()
    {
        string directory = Path.Combine(Path.GetTempPath(), "webscript-php-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            using var key = RSA.Create(3072);
            string keyFile = Path.Combine(directory, "private.pem"), scriptFile = Path.Combine(directory, "hello.lsl"), json = Path.Combine(directory, "hello.json");
            File.WriteAllText(keyFile, key.ExportPkcs8PrivateKeyPem());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            const string source = "default { state_entry() { llSay(0, \"Zażółć\"); } }";
            File.WriteAllText(scriptFile, source);
            string tool = Path.Combine(AppContext.BaseDirectory, "tools", "sign-script.php");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("PHP_BINARY") ?? "php") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string argument in new[] { tool, "--script", scriptFile, "--url", "https://scripts.example.org/hello.json", "--private-key", keyFile, "--out", json }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }
            Assert.True(process.ExitCode == 0, await output + await error);
            var policy = new WebScriptSecurity(new[] { "scripts.example.org" }, key.ExportSubjectPublicKeyInfoPem());
            Assert.Equal("//lsl\n" + source, policy.VerifyEnvelope(await File.ReadAllBytesAsync(json), new Uri("https://scripts.example.org/hello.json"), DateTimeOffset.UtcNow));
            Assert.Throws<InvalidOperationException>(() => policy.VerifyEnvelope(File.ReadAllBytes(json), new Uri("https://scripts.example.org/other.json"), DateTimeOffset.UtcNow));
        }
        finally { Directory.Delete(directory, true); }
    }
}
