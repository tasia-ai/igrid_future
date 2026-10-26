using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using OpenMetaverse;

namespace TasiaAddons.InternalScriptRpc;

internal sealed record RpcRequest(int Version, string RequestId, long Timestamp, string Nonce, string Operation, string Owner, JsonElement Payload);
internal sealed record RpcResponse(string RequestId, bool Ok, string Code, JsonElement Payload);

internal static class RpcSecurity
{
    internal const int MaxBody = 512 * 1024;
    internal static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

    internal static HashSet<UUID> ParseOwners(string value) => (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(x => UUID.TryParse(x, out UUID id) && id != UUID.Zero ? id : throw new ArgumentException("AllowedOwners must contain only nonzero UUIDs."))
        .ToHashSet();

    internal static void ValidateSecret(string secret)
    {
        if (Encoding.UTF8.GetByteCount(secret ?? "") < 32) throw new InvalidOperationException("InternalScriptRpc SigningKey must contain at least 32 UTF-8 bytes.");
    }

    internal static string Sign(string secret, string timestamp, string nonce, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp + "\n" + nonce + "\n" + body))).ToLowerInvariant();
    }

    internal static bool Verify(string secret, string timestamp, string nonce, string body, string signature)
    {
        if (signature?.Length != 64) return false;
        byte[] expected = Convert.FromHexString(Sign(secret, timestamp, nonce, body));
        byte[] supplied;
        try { supplied = Convert.FromHexString(signature); } catch { return false; }
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    internal static string CreateRequest(string operation, UUID owner, JsonElement payload, out string timestamp, out string nonce, out string requestId)
    {
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        requestId = UUID.Random().ToString();
        return JsonSerializer.Serialize(new { version = 1, requestId, timestamp = long.Parse(timestamp), nonce, operation, owner = owner.ToString(), payload });
    }

    internal static bool TryParseRequest(string body, out RpcRequest request)
    {
        request = null;
        try
        {
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 12 });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 7) return false;
            string[] expected = { "version", "requestId", "timestamp", "nonce", "operation", "owner", "payload" };
            if (root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 7 || expected.Any(name => !root.TryGetProperty(name, out _))) return false;
            request = JsonSerializer.Deserialize<RpcRequest>(root.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return request is { Version: 1 } && UUID.TryParse(request.RequestId, out _) && request.Nonce?.Length is >= 32 and <= 96 &&
                request.Operation?.Length is >= 1 and <= 64 && request.Operation.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') &&
                UUID.TryParse(request.Owner, out UUID owner) && owner != UUID.Zero && request.Payload.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
        }
        catch { return false; }
    }

    internal static bool TimestampValid(long timestamp)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long skew = (long)Skew.TotalSeconds;
        return timestamp >= now - skew && timestamp <= now + skew;
    }

    internal static bool ConsumeNonce(ConcurrentDictionary<string, long> cache, string nonce)
    {
        lock (cache)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var entry in cache) if (entry.Value < now) cache.TryRemove(entry.Key, out _);
            return cache.Count < 20000 && cache.TryAdd(nonce, now + 180);
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maxBytes, 4096));
        byte[] buffer = new byte[4096];
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes + 1 - (int)output.Length)), cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maxBytes) throw new InvalidDataException("RPC response exceeded its byte limit.");
            output.Write(buffer, 0, read);
        }
    }

    internal static JsonElement Json(string value) => JsonSerializer.SerializeToElement(value);
}
