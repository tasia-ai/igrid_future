using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TasiaAddons.WebScripts;

/// <summary>Pure verification policy shared by the region addon and its security tests.</summary>
public sealed class WebScriptSecurity
{
    public const int MaxScriptBytes = 65536;
    public const int MaxEnvelopeBytes = 100000;
    private readonly HashSet<string> hosts;
    private readonly string publicKey;

    public WebScriptSecurity(IEnumerable<string> allowedHosts, string publisherPublicKey)
    {
        hosts = new HashSet<string>(allowedHosts.Select(h => h.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        if (hosts.Count == 0 || hosts.Any(h => h.Length == 0 || h.Contains('*') || h.Contains('/') || h.Contains(':') || IPAddress.TryParse(h, out _)))
            throw new ArgumentException("Configure exact DNS hostnames, not URLs, wildcards or IP addresses.");
        using var rsa = RSA.Create();
        if (!publisherPublicKey.StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
            throw new ArgumentException("An RSA public key in SubjectPublicKeyInfo PEM format is required.");
        rsa.ImportFromPem(publisherPublicKey);
        if (rsa.KeySize < 3072) throw new ArgumentException("RSA keys must have at least 3072 bits.");
        publicKey = publisherPublicKey;
    }

    public Uri ValidateUrl(string value)
    {
        if (value == null || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.HostNameType != UriHostNameType.Dns || !hosts.Contains(uri.IdnHost))
            throw new InvalidOperationException("Only configured HTTPS publisher hosts on port 443 are permitted.");
        return uri;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 ||
                (b[0] == 100 && b[1] is >= 64 and <= 127) ||
                (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                (b[0] == 192 && (b[1] == 168 || b[1] == 0 || (b[1] == 88 && b[2] == 99))) ||
                (b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100))) ||
                (b[0] == 203 && b[1] == 0 && b[2] == 113));
        // Only global unicast, excluding special-purpose, documentation and transition ranges.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0 && (b[0] & 0xe0) == 0x20 &&
            !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || (b[2] == 0x0d && b[3] == 0xb8))) &&
            !(b[0] == 0x20 && b[1] == 0x02) && !(b[0] == 0x3f && (b[1] & 0xf0) == 0xf0);
    }

    public static byte[] SigningBytes(string url, long expires, string script) =>
        Encoding.UTF8.GetBytes("maRunScriptFromWeb:v1\n" + url + "\n" + expires.ToString(CultureInfo.InvariantCulture) + "\n" + script);

    public string VerifyEnvelope(ReadOnlyMemory<byte> bytes, Uri requestedUrl, DateTimeOffset now)
    {
        if (bytes.Length > MaxEnvelopeBytes) throw new InvalidOperationException("Envelope too large.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!fields.Add(property.Name) || property.Name is not ("version" or "url" or "expires" or "script" or "signature"))
                throw new InvalidOperationException("Duplicate or unknown envelope field.");
        var root = document.RootElement;
        if (fields.Count != 5 || root.GetProperty("version").GetInt32() != 1)
            throw new InvalidOperationException("Unsupported envelope version.");
        string url = root.GetProperty("url").GetString();
        long expires = root.GetProperty("expires").GetInt64();
        if (url != requestedUrl.AbsoluteUri || ValidateUrl(url).AbsoluteUri != url ||
            expires <= now.ToUnixTimeSeconds() || expires > now.ToUnixTimeSeconds() + 86400)
            throw new InvalidOperationException("Envelope URL or expiration is invalid.");
        string encoded = root.GetProperty("script").GetString();
        string signature = root.GetProperty("signature").GetString();
        if (encoded == null || encoded.Length > ((MaxScriptBytes + 2) / 3) * 4 || signature == null || signature.Length > 2048)
            throw new InvalidOperationException("Invalid encoded payload size.");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKey);
        if (!rsa.VerifyData(SigningBytes(url, expires, encoded), Convert.FromBase64String(signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidOperationException("Publisher signature does not match.");
        byte[] scriptBytes = Convert.FromBase64String(encoded);
        if (scriptBytes.Length is 0 or > MaxScriptBytes || Convert.ToBase64String(scriptBytes) != encoded)
            throw new InvalidOperationException("Invalid script encoding.");
        string script = new UTF8Encoding(false, true).GetString(scriptBytes);
        if (script.Contains('\0') || script.Contains('\uFEFF') ||
            System.Text.RegularExpressions.Regex.IsMatch(script, @"(?im)^\s*//\s*(?:c#|csharp|vb|jscript|yengine\s*:|phlox\s*:|xengine\s*:)", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Only UTF-8 LSL source without engine/language directives is permitted.");
        // Force ordinary LSL compilation rather than a language/engine directive from the downloaded text.
        return "//lsl\n" + script;
    }
}
