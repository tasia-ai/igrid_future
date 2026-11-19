#nullable enable

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using log4net;
using Microsoft.Extensions.Caching.Memory;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using TasiaAddons.Abstractions;

namespace TasiaAddons.RemoteSound;

public class RemoteSoundModule : INonSharedRegionModule, ITasiaAddonsFeature, IRemoteSoundModule
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(RemoteSoundModule));

    private readonly HttpClient m_httpClient;
    private readonly MemoryCache m_memoryCache;
    private readonly ConcurrentDictionary<Guid, RateCounter> m_objectRates = new();
    private readonly RateCounter m_regionRate = new();
    private readonly SemaphoreSlim m_fetchSemaphore = new(4);

    private RemoteSoundSettings m_settings = RemoteSoundSettings.Default;
    private Scene? m_scene;
    private IScriptModuleComms? m_comms;
    private ISoundModule? m_soundModule;
    private bool m_enabled;
    private ITasiaAddonsContext? m_context;

    public RemoteSoundModule()
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = ConnectPublicAsync
        };
        m_httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        m_memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
    }

    public string Name => "NGC Remote Sound";

    public Type ReplaceableInterface => null!;

    public void Configure(ITasiaAddonsContext context)
    {
        m_context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public void Initialise(IConfigSource source)
    {
        IConfig? config = source.Configs["NGC.Sound"];
        if (config is null || !config.GetBoolean("Enable", true))
        {
            Log.Info("[NGC.SOUND]: Module disabled by configuration");
            m_enabled = false;
            return;
        }

        m_settings = RemoteSoundSettings.FromConfig(config);
        Directory.CreateDirectory(m_settings.CacheDirectory);
        m_enabled = true;
        Log.Info("[NGC.SOUND]: Remote sound module initialised");
    }

    public void AddRegion(Scene scene)
    {
        if (!m_enabled)
            return;

        m_scene = scene;
        m_soundModule = scene.RequestModuleInterface<ISoundModule>();
        if (m_soundModule is null)
        {
            Log.Warn("[NGC.SOUND]: No sound module available; disabling remote playback");
            m_enabled = false;
            return;
        }

        m_comms = scene.RequestModuleInterface<IScriptModuleComms>();
        if (m_comms is null)
        {
            Log.Warn("[NGC.SOUND]: ScriptModuleComms not available; disabling remote playback");
            m_enabled = false;
            return;
        }

        scene.RegisterModuleInterface<IRemoteSoundModule>(this);
        scene.EventManager.OnObjectBeingRemovedFromScene += HandleObjectRemoved;
        Log.InfoFormat("[NGC.SOUND]: Added to region {0}", scene.RegionInfo.RegionName);
    }

    public void RemoveRegion(Scene scene)
    {
        if (!m_enabled)
            return;

        scene.UnregisterModuleInterface<IRemoteSoundModule>(this);
        scene.EventManager.OnObjectBeingRemovedFromScene -= HandleObjectRemoved;
        if (ReferenceEquals(m_scene, scene))
        {
            m_scene = null;
            m_comms = null;
            m_soundModule = null;
        }
    }

    public void RegionLoaded(Scene scene)
    {
        if (!m_enabled || scene != m_scene || m_comms is null)
            return;

        try
        {
            m_comms.RegisterScriptInvocations(this);
        }
        catch (Exception e)
        {
            Log.Error("[NGC.SOUND]: Failed to register script invocation", e);
            m_enabled = false;
        }
    }

    public void Close()
    {
        m_memoryCache.Dispose();
        m_httpClient.Dispose();
        m_fetchSemaphore.Dispose();
    }

    private void HandleObjectRemoved(SceneObjectGroup obj)
    {
        if (obj?.RootPart is null)
            return;

        Guid key = obj.RootPart.UUID.Guid;
        m_objectRates.TryRemove(key, out _);

    }

    [ScriptInvocation]
    public string ngcPlaySoundURL(UUID hostID, UUID scriptID, string url, double volume, UUID target, double cacheOverride)
        => PlaySoundUrl(hostID, scriptID, url, volume, target, cacheOverride);

    public string PlaySoundUrl(UUID hostID, UUID scriptID, string url, double volume, UUID target, double cacheOverride)
    {
        if (!m_enabled || m_scene is null)
            return "Remote sound module disabled";

        if (string.IsNullOrWhiteSpace(url))
            return "URL is required";

        if (url.Length > 2048)
            return "URL exceeds maximum length";

        volume = double.IsFinite(volume) ? Utils.Clamp(volume, 0.0, 1.0) : 0.0;

        if (target.IsZero())
            return "Target is required";

        ScenePresence? targetPresence = m_scene.GetScenePresence(target);
        if (targetPresence is null || targetPresence.IsChildAgent)
            return "Target is not in this region";

        SceneObjectPart? part = m_scene.GetSceneObjectPart(hostID);
        if (part is null)
            return "Host object not found";

        if (!m_settings.ScriptOwnerWhitelist.IsAllowed(part.OwnerID))
            return "Script owner is not permitted to play remote audio";

        if (!CheckRateLimits(part, out string? rateError))
            return rateError ?? "Remote sound rate limit exceeded";

        Uri uri;
        try
        {
            uri = new Uri(url, UriKind.Absolute);
        }
        catch (Exception)
        {
            return "Invalid URL";
        }

        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            if (!m_settings.AllowHttp)
                return "HTTPS is required";
        }
        else if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return "Unsupported URL scheme";
        }

        if (!IsAllowedUri(uri))
            return "Domain not permitted";

        try
        {
            CachedSoundData data = FetchSound(uri, cacheOverride > 0);
            if (data.Data.Length == 0)
                return "No audio data returned";

            UUID soundId = EnsureAsset(data, uri, part.OwnerID);
            if (soundId.IsZero())
                return "Failed to store audio asset";

            PlaySound(part, soundId, volume, target);
            m_context?.AuditService.RecordEvent(
                "RemoteSound",
                "PlaySoundURL",
                part.OwnerID,
                GetOwnerName(part),
                uri.ToString(),
                m_scene?.RegionInfo.RegionHandle,
                m_scene?.RegionInfo.RegionName);
            return string.Empty;
        }
        catch (IOException ex)
        {
            Log.WarnFormat("[NGC.SOUND]: IO error handling cache for {0}: {1}", uri, ex.Message);
            return "Audio cache failure";
        }
        catch (HttpRequestException ex)
        {
            Log.WarnFormat("[NGC.SOUND]: HTTP error fetching {0}: {1}", uri, ex.Message);
            return "Failed to download audio";
        }
        catch (Exception ex)
        {
            Log.Error("[NGC.SOUND]: Unexpected error during playback", ex);
            if (!m_settings.DefaultFallbackSound.IsZero())
            {
                PlaySound(part, m_settings.DefaultFallbackSound, volume, target);
                m_context?.AuditService.RecordEvent(
                    "RemoteSound",
                    "FallbackSound",
                    part.OwnerID,
                    GetOwnerName(part),
                    m_settings.DefaultFallbackSound.ToString(),
                    m_scene?.RegionInfo.RegionHandle,
                    m_scene?.RegionInfo.RegionName);
                return string.Empty;
            }
            return "Remote playback failed";
        }
    }

    private string? GetOwnerName(SceneObjectPart part)
    {
        if (m_scene?.UserAccountService is null)
            return null;

        UserAccount? account = m_scene.UserAccountService.GetUserAccount(m_scene.RegionInfo.ScopeID, part.OwnerID);
        if (account is null)
            return null;

        return string.Format(CultureInfo.InvariantCulture, "{0} {1}", account.FirstName, account.LastName);
    }

    private bool CheckRateLimits(SceneObjectPart part, out string? error)
    {
        error = null;
        if (!m_settings.ObjectRateLimiter.TryConsume(m_objectRates.GetOrAdd(part.UUID.Guid, _ => new RateCounter()), m_settings.PerObjectRatePerSecond))
        {
            error = "Per-object remote sound rate exceeded";
            return false;
        }

        if (!m_settings.RegionRateLimiter.TryConsume(m_regionRate, m_settings.PerRegionRatePerSecond))
        {
            error = "Region remote sound rate exceeded";
            return false;
        }

        return true;
    }

    private CachedSoundData FetchSound(Uri uri, bool forceRefresh)
    {
        string cacheKey = RemoteSoundSettings.GetCacheKey(uri);
        if (!forceRefresh && m_memoryCache.TryGetValue(cacheKey, out CachedSoundData? cached) && cached is not null && !cached.IsExpired)
            return cached;

        string diskPath = Path.Combine(m_settings.CacheDirectory, cacheKey + ".vorbis-v2");
        if (!forceRefresh && File.Exists(diskPath))
        {
            CachedSoundData? diskData = CachedSoundData.FromFile(diskPath);
            if (diskData is not null && diskData.ContentType == "audio/ogg" && !diskData.IsExpired)
            {
                m_memoryCache.Set(cacheKey, diskData, new MemoryCacheEntryOptions
                {
                    AbsoluteExpiration = diskData.ExpiresAt,
                    Size = diskData.Data.Length
                });
                return diskData;
            }
        }

        return DownloadAndCache(uri, cacheKey, diskPath, forceRefresh);
    }

    private CachedSoundData DownloadAndCache(Uri uri, string cacheKey, string diskPath, bool forceRefresh)
    {
        if (!m_fetchSemaphore.Wait(TimeSpan.FromSeconds(2)))
            throw new HttpRequestException("Remote audio workers busy");
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            Uri current = uri;
            for (int redirects = 0; redirects <= 5; redirects++)
            {
                if (!IsAllowedUri(current))
                    throw new HttpRequestException("Remote audio URL is not permitted");
                using HttpRequestMessage request = new(HttpMethod.Get, current);
                using HttpResponseMessage response = m_httpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token).GetAwaiter().GetResult();
                int status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (response.Headers.Location is null || redirects == 5)
                        throw new HttpRequestException("Invalid remote audio redirect");
                    current = new Uri(current, response.Headers.Location);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > m_settings.MaxFileBytes)
                    throw new HttpRequestException("Remote audio exceeds size limit");
                ValidateContentType(response.Content.Headers.ContentType?.MediaType);
                using Stream stream = response.Content.ReadAsStreamAsync(timeout.Token).GetAwaiter().GetResult();
                byte[] input = ReadToBuffer(stream, m_settings.MaxFileBytes, timeout.Token);
                byte[] output = EncodeVorbis(input);
                CachedSoundData data = new(output, "audio/ogg",
                    DateTime.UtcNow.AddSeconds(m_settings.CacheTtl));
                WriteDisk(diskPath, data);
                m_memoryCache.Set(cacheKey, data, new MemoryCacheEntryOptions
                {
                    AbsoluteExpiration = data.ExpiresAt,
                    Size = data.Data.Length
                });
                return data;
            }
            throw new HttpRequestException("Too many redirects");
        }
        finally { m_fetchSemaphore.Release(); }
    }

    private bool IsAllowedUri(Uri uri) =>
        (uri.Scheme == "https" || (m_settings.AllowHttp && uri.Scheme == "http"))
        && string.IsNullOrEmpty(uri.UserInfo)
        && m_settings.DomainPolicy.IsAllowed(uri.IdnHost);

    // Pin the connection to an approved DNS result, including after redirects.
    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
        foreach (IPAddress address in addresses)
        {
            if (!IsPublicAddress(address))
                continue;
            Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("No permitted public audio endpoint");
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] b = address.GetAddressBytes();
        if (b.Length == 4)
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && (b[1] == 168 || b[1] == 0 || b[1] == 88))
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)));
        // Only globally routed IPv6 unicast; exclude transition and special ranges.
        return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && (b[1] == 0x02 || b[1] == 0x01));
    }

    private static void ValidateContentType(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType) || mediaType == "application/octet-stream"
            || mediaType == "application/ogg" || mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return;
        throw new HttpRequestException("Unsupported remote audio content type");
    }

    private static byte[] ReadToBuffer(Stream stream, long maxBytes, CancellationToken token)
    {
        using MemoryStream ms = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            int read;
            while ((read = stream.ReadAsync(buffer.AsMemory(), token).AsTask().GetAwaiter().GetResult()) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new HttpRequestException("Remote audio exceeds size limit");

                ms.Write(buffer, 0, read);
            }
            return ms.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void WriteDisk(string path, CachedSoundData data)
    {
        CachedSoundData.EnsureDirectory(path);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { data.ToFile(temp); File.Move(temp, path, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private byte[] EncodeVorbis(byte[] input)
    {
        // Standard viewer sound assets: mono, 44.1 kHz Ogg Vorbis.
        // The local temporary input is the only permitted FFmpeg protocol.
        string directory = Path.Combine(Path.GetTempPath(), "ngc-sound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "input");
            string output = Path.Combine(directory, "sound.ogg");
            File.WriteAllBytes(source, input);
            ProcessStartInfo info = new(m_settings.FfmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            foreach (string arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error",
                "-protocol_whitelist", "file", "-format_whitelist", "wav,mp3,ogg,aac,flac,mov,matroska,webm",
                "-i", source, "-map", "0:a:0", "-vn", "-sn", "-dn", "-map_metadata", "-1",
                "-ac", "1", "-ar", "44100", "-c:a", "libvorbis", "-q:a", "4",
                "-t", "10", "-f", "ogg", output })
                info.ArgumentList.Add(arg);
            using Process process = Process.Start(info) ?? throw new IOException("Cannot start FFmpeg");
            Task<string> errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new IOException("Remote sound encoding timed out");
            }
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new IOException("FFmpeg could not encode remote audio");
            byte[] bytes = File.ReadAllBytes(output);
            if (bytes.Length < 4 || !bytes.AsSpan(0, 4).SequenceEqual("OggS"u8))
                throw new IOException("Invalid encoded sound");
            return bytes;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private UUID EnsureAsset(CachedSoundData data, Uri uri, UUID creatorID)
    {
        if (m_scene is null || data.Data is null || data.Data.Length == 0)
           return UUID.Zero;

        // Content-addressed identity prevents the old WAV or stale viewer cache
        // from being reused when the URL's content changes.
        byte[] hash = SHA256.HashData(data.Data);
        UUID assetId = UUID.Parse(new Guid(hash.AsSpan(0, 16)).ToString());
        AssetBase asset = m_scene.AssetService.Get(assetId.ToString());
        if (asset is not null)
            return asset.FullID;
        const sbyte assetType = (sbyte)AssetType.Sound;

        string assetName = Path.GetFileName(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(assetName))
            assetName = "Remote sound";
        if (assetName.Length > AssetBase.MAX_ASSET_NAME)
            assetName = assetName[..AssetBase.MAX_ASSET_NAME];

        asset = new AssetBase(assetId, assetName, assetType, creatorID.ToString())
        {
            Data = data.Data
        };
        asset.Metadata.ContentType = data.ContentType;

        string storedId = m_scene.AssetService.Store(asset);
        return UUID.TryParse(storedId, out UUID storedUuid)
            ? storedUuid
            : UUID.Zero;
    }

    private void PlaySound(SceneObjectPart part, UUID soundId, double volume, UUID target)
    {
        if (m_scene is null || m_soundModule is null || target.IsZero())
            return;

        ScenePresence? presence = m_scene.GetScenePresence(target);
        if (presence is null || presence.IsChildAgent)
            return;

        Vector3 position = part.AbsolutePosition;
        UUID parentId = part.ParentGroup?.UUID ?? UUID.Zero;
        double clampedVolume = Utils.Clamp(volume, 0.0, 1.0);

        presence.ControllingClient.SendTriggeredSound(
            soundId,
            part.OwnerID,
            part.UUID,
            parentId,
            m_scene.RegionInfo.RegionHandle,
            position,
            (float)clampedVolume);

        Log.InfoFormat("[NGC.SOUND]: Triggered sound asset {0} for {1} at {2} vol={3:0.00}",
            soundId, target, position, clampedVolume);
    }

    private static string cachedExtension(Uri uri)
    {
        string ext = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(ext))
            return ".bin";
        return ext.ToLowerInvariant();
    }
}

internal class RemoteSoundSettings
{
    public static RemoteSoundSettings Default => new();

    public bool AllowHttp { get; private set; }
    public int CacheTtl { get; private set; }
    public string CacheDirectory { get; private set; } = "./data/ngc-sound-cache";
    public long MaxFileBytes { get; private set; }
    public double PerObjectRatePerSecond { get; private set; }
    public double PerRegionRatePerSecond { get; private set; }
    public UUID DefaultFallbackSound { get; private set; }
    public DomainPolicy DomainPolicy { get; private set; } = DomainPolicy.AllowAll;
    public ScriptOwnerWhitelist ScriptOwnerWhitelist { get; private set; } = ScriptOwnerWhitelist.AllowAll;
    public RateLimiter ObjectRateLimiter { get; } = new();
    public RateLimiter RegionRateLimiter { get; } = new();

    public bool EnableTranscode { get; private set; }
    public bool ForceReencodeWav { get; private set; }
    public string FfmpegPath { get; private set; } = "ffmpeg";

    public double MaxChunkSeconds { get; private set; }
    public bool EnableStreaming { get; private set; }

    public static RemoteSoundSettings FromConfig(IConfig config)
    {
        RemoteSoundSettings settings = new();
        settings.AllowHttp = !config.GetBoolean("AllowHTTPSOnly", true);
        settings.CacheTtl = config.GetInt("CacheTTLSeconds", 3600);
        settings.MaxFileBytes = config.GetLong("MaxFileBytes", 20 * 1024 * 1024);
        settings.CacheDirectory = config.GetString("CacheDir", settings.CacheDirectory);
        settings.PerObjectRatePerSecond = Math.Max(0.1, config.GetDouble("PerObjectRatePerSec", 2));
        settings.PerRegionRatePerSecond = Math.Max(0.1, config.GetDouble("PerRegionRatePerSec", 20));
        string fallback = config.GetString("DefaultFallbackSound", UUID.Zero.ToString());
        if (!UUID.TryParse(fallback, out UUID fallbackId))
            fallbackId = UUID.Zero;
        settings.DefaultFallbackSound = fallbackId;
        settings.DomainPolicy = DomainPolicy.FromConfig(
            config.GetString("AllowedDomains", string.Empty),
            config.GetString("DeniedDomains", string.Empty));
        settings.ScriptOwnerWhitelist = ScriptOwnerWhitelist.FromConfig(
            config.GetString("AllowScriptOwners", string.Empty));

        settings.EnableTranscode = config.GetBoolean("TranscodeToWav", true);
        settings.FfmpegPath = config.GetString("FfmpegPath", settings.FfmpegPath);
        settings.ForceReencodeWav = config.GetBoolean("ForceReencodeWav", false);

        settings.MaxChunkSeconds = Math.Max(2.0, config.GetDouble("MaxChunkSeconds", 8.0));
        settings.EnableStreaming = config.GetBoolean("EnableStreaming", true);

        return settings;
    }

    public static string GetCacheKey(Uri uri)
    {
        byte[] data = SHA256.HashData(Encoding.UTF8.GetBytes(uri.ToString()));
        return new Guid(data.AsSpan(0, 16)).ToString("N");
    }
}

internal class CachedSoundData
{
    public CachedSoundData(byte[] data, string contentType, DateTime expiresAt)
    {
        Data = data;
        ContentType = contentType;
        ExpiresAt = expiresAt;
    }

    public byte[] Data { get; }
    public string ContentType { get; }
    public DateTime ExpiresAt { get; private set; }
    public string? ETag { get; set; }
    public DateTime? LastModified { get; set; }

    public bool IsExpired => DateTime.UtcNow > ExpiresAt;

    public void Refresh(int ttlSeconds)
    {
        ExpiresAt = DateTime.UtcNow.AddSeconds(ttlSeconds);
    }

    public void ToFile(string path)
    {
        using FileStream fs = File.Create(path, 81920, FileOptions.WriteThrough);
        using BinaryWriter writer = new(fs, Encoding.UTF8, leaveOpen: true);
        writer.Write(ExpiresAt.ToBinary());
        writer.Write(ContentType);
        writer.Write(ETag ?? string.Empty);
        writer.Write(LastModified?.ToBinary() ?? 0L);
        writer.Write(Data.Length);
        writer.Write(Data);
        writer.Flush();
    }

    public static CachedSoundData? FromFile(string path)
    {
        if (!File.Exists(path))
            return null;

        using FileStream fs = File.OpenRead(path);
        using BinaryReader reader = new(fs, Encoding.UTF8, leaveOpen: true);
        DateTime expires = DateTime.FromBinary(reader.ReadInt64());
        string contentType = reader.ReadString();
        string eTag = reader.ReadString();
        long lmBinary = reader.ReadInt64();
        int length = reader.ReadInt32();
        if (length <= 0 || length > 2 * 1024 * 1024 || length > fs.Length - fs.Position) return null;
        byte[] data = reader.ReadBytes(length);

        CachedSoundData sound = new(data, contentType, expires)
        {
            ETag = string.IsNullOrEmpty(eTag) ? null : eTag,
            LastModified = lmBinary == 0 ? null : DateTime.FromBinary(lmBinary)
        };
        return sound;
    }

    public static void EnsureDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }
}

internal class RateLimiter
{
    public bool TryConsume(RateCounter counter, double ratePerSecond)
    {
        lock (counter)
        {
            counter.Trim();
            if (counter.Count >= ratePerSecond)
                return false;

            counter.Add();
            return true;
        }
    }
}

internal class RateCounter
{
    private readonly Queue<DateTime> m_events = new Queue<DateTime>();

    public int Count => m_events.Count;

    public void Add()
    {
        m_events.Enqueue(DateTime.UtcNow);
    }

    public void Trim()
    {
        DateTime threshold = DateTime.UtcNow.AddSeconds(-1);
        while (m_events.Count > 0 && m_events.Peek() < threshold)
            m_events.Dequeue();
    }
}

internal class DomainPolicy
{
    private readonly HashSet<string> m_allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> m_deny = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool m_allowAll = true;

    public static DomainPolicy AllowAll => new DomainPolicy();

    public static DomainPolicy FromConfig(string allowed, string denied)
    {
        DomainPolicy policy = new DomainPolicy();
        if (!string.IsNullOrWhiteSpace(allowed))
        {
            policy.m_allowAll = false;
            foreach (string entry in allowed.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                policy.m_allow.Add(entry.Trim());
        }
        if (!string.IsNullOrWhiteSpace(denied))
        {
            foreach (string entry in denied.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                policy.m_deny.Add(entry.Trim());
        }
        return policy;
    }

    public bool IsAllowed(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (m_deny.Contains(host))
            return false;

        if (m_allowAll)
            return true;

        return m_allow.Contains(host);
    }
}

internal class ScriptOwnerWhitelist
{
    private readonly HashSet<UUID> m_allowed = new HashSet<UUID>();
    private bool m_allowAll = true;

    public static ScriptOwnerWhitelist AllowAll => new ScriptOwnerWhitelist();

    public static ScriptOwnerWhitelist FromConfig(string owners)
    {
        ScriptOwnerWhitelist wl = new ScriptOwnerWhitelist();
        if (!string.IsNullOrWhiteSpace(owners))
        {
            wl.m_allowAll = false;
            foreach (string entry in owners.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (UUID.TryParse(entry.Trim(), out UUID id))
                    wl.m_allowed.Add(id);
            }
        }
        return wl;
    }

    public bool IsAllowed(UUID owner)
    {
        return m_allowAll || m_allowed.Contains(owner);
    }
}
