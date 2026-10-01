#nullable enable

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
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

    // streaming state per host object
    private readonly ConcurrentDictionary<UUID, ActiveStream> m_activeStreams = new();

    private RemoteSoundSettings m_settings = RemoteSoundSettings.Default;
    private Scene? m_scene;
    private IScriptModuleComms? m_comms;
    private ISoundModule? m_soundModule;
    private bool m_enabled;
    private ITasiaAddonsContext? m_context;

    private sealed class ActiveStream
    {
        public UUID HostId { get; }
        public List<UUID> ChunkAssets { get; }
        public double Volume { get; }
        public UUID Target { get; }
        public double ChunkSeconds { get; }
        public int CurrentIndex;
        public bool Cancelled;

        public ActiveStream(UUID hostId, List<UUID> chunkAssets, double volume, UUID target, double chunkSeconds)
        {
            HostId = hostId;
            ChunkAssets = chunkAssets;
            Volume = volume;
            Target = target;
            ChunkSeconds = chunkSeconds;
            CurrentIndex = 0;
            Cancelled = false;
        }
    }

    public RemoteSoundModule()
    {
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };
        m_httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        m_memoryCache = new MemoryCache(new MemoryCacheOptions());
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

        // cancel any active stream for this object
        if (m_activeStreams.TryRemove(obj.RootPart.UUID, out ActiveStream? stream))
            stream.Cancelled = true;
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

        volume = Utils.Clamp(volume, 0.0, 1.0);

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

        if (!m_settings.DomainPolicy.IsAllowed(uri.Host))
            return "Domain not permitted";

        try
        {
            CachedSoundData data = FetchSound(uri, cacheOverride > 0);
            if (data.Data.Length == 0)
                return "No audio data returned";

            double estimatedDuration = EstimateDurationSeconds(data, uri);

            if (m_settings.EnableStreaming && estimatedDuration > m_settings.MaxChunkSeconds)
            {
                string streamError = StartStreaming(part, data, uri, volume, target, estimatedDuration);
                if (!string.IsNullOrEmpty(streamError))
                    return streamError;

                m_context?.AuditService.RecordEvent(
                    "RemoteSound",
                    "PlaySoundURLStream",
                    part.OwnerID,
                    GetOwnerName(part),
                    uri.ToString(),
                    m_scene?.RegionInfo.RegionHandle,
                    m_scene?.RegionInfo.RegionName);

                return string.Empty;
            }

            UUID soundId = EnsureAsset(data, uri);
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

    // very rough duration estimate (used only to decide streaming vs single-shot)
    private double EstimateDurationSeconds(CachedSoundData data, Uri uri)
    {
        // if we normalized to PCM WAV (44100 * 2 bytes * 1 channel)
        if (data.ContentType.Equals("audio/wav", StringComparison.OrdinalIgnoreCase) ||
            data.ContentType.Equals("audio/x-wav", StringComparison.OrdinalIgnoreCase) ||
            data.ContentType.Equals("audio/wave", StringComparison.OrdinalIgnoreCase) ||
            data.ContentType.Equals("audio/vnd.wave", StringComparison.OrdinalIgnoreCase))
        {
            double bytesPerSecond = 44100 * 2; // mono, 16-bit
            if (bytesPerSecond > 0)
                return data.Data.Length / bytesPerSecond;
        }

        // fallback: assume ~128kbps (16KB/s)
        double genericBytesPerSecond = 16 * 1024;
        return data.Data.Length / genericBytesPerSecond;
    }

    private string StartStreaming(SceneObjectPart part, CachedSoundData data, Uri uri, double volume, UUID target, double estimatedDuration)
    {
        if (m_scene is null || m_soundModule is null)
            return "Scene or sound module unavailable";

        UUID fullAssetId = EnsureAsset(data, uri);
        if (fullAssetId.IsZero())
            return "Failed to store audio asset";

        List<UUID> chunks = new() { fullAssetId };

        double chunkSeconds = Math.Min(m_settings.MaxChunkSeconds, estimatedDuration);
        if (chunkSeconds <= 0.1)
            chunkSeconds = m_settings.MaxChunkSeconds;

        ActiveStream stream = new(part.UUID, chunks, volume, target, chunkSeconds);

        if (m_activeStreams.TryRemove(part.UUID, out ActiveStream? oldStream))
            oldStream.Cancelled = true;

        m_activeStreams[part.UUID] = stream;

        ThreadPool.QueueUserWorkItem(_ => RunStream(stream));

        Log.InfoFormat(
            "[NGC.SOUND]: Started streaming for {0} ({1} chunks, est={2:0.0}s, chunkLen={3:0.0}s)",
            part.UUID, chunks.Count, estimatedDuration, chunkSeconds);

        return string.Empty;
    }

    private void RunStream(ActiveStream stream)
    {
        try
        {
            while (!stream.Cancelled && stream.CurrentIndex < stream.ChunkAssets.Count)
            {
                if (m_scene is null || m_soundModule is null)
                    break;

                SceneObjectPart? part = m_scene.GetSceneObjectPart(stream.HostId);
                if (part is null)
                    break;

                UUID assetId = stream.ChunkAssets[stream.CurrentIndex];

                PlaySound(part, assetId, stream.Volume, stream.Target);
                stream.CurrentIndex++;

                if (stream.CurrentIndex >= stream.ChunkAssets.Count)
                    break;

                int sleepMs = (int)(stream.ChunkSeconds * 1000);
                if (sleepMs < 100)
                    sleepMs = 100;

                Thread.Sleep(sleepMs);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[NGC.SOUND]: Error in streaming playback loop", ex);
        }
        finally
        {
            m_activeStreams.TryRemove(stream.HostId, out _);
        }
    }

    private CachedSoundData FetchSound(Uri uri, bool forceRefresh)
    {
        string cacheKey = RemoteSoundSettings.GetCacheKey(uri);
        if (!forceRefresh && m_memoryCache.TryGetValue(cacheKey, out CachedSoundData? cached) && cached is not null && !cached.IsExpired)
            return cached;

        string diskPath = Path.Combine(m_settings.CacheDirectory, cacheKey + cachedExtension(uri));
        if (!forceRefresh && File.Exists(diskPath))
        {
            CachedSoundData? diskData = CachedSoundData.FromFile(diskPath);
            if (diskData is not null && !diskData.IsExpired)
            {
                m_memoryCache.Set(cacheKey, diskData, new MemoryCacheEntryOptions
                {
                    AbsoluteExpiration = diskData.ExpiresAt
                });
                return diskData;
            }
        }

        return DownloadAndCache(uri, cacheKey, diskPath, forceRefresh);
    }

    private CachedSoundData DownloadAndCache(Uri uri, string cacheKey, string diskPath, bool forceRefresh)
    {
        m_fetchSemaphore.Wait();
        try
        {
            string? eTag = null;
            DateTimeOffset? lastModified = null;
            bool haveValidDiskCache = false;

            try
            {
                using HttpRequestMessage head = new(HttpMethod.Head, uri);
                using HttpResponseMessage headResponse = m_httpClient.Send(head);
                if (headResponse.IsSuccessStatusCode)
                {
                    string headCt = DetectContentType(headResponse.Content.Headers.ContentType?.MediaType, uri);
                    ValidateContentType(headCt);

                    long? contentLength = headResponse.Content.Headers.ContentLength;
                    if (contentLength.HasValue && contentLength.Value > m_settings.MaxFileBytes)
                        throw new HttpRequestException("Remote audio exceeds size limit");

                    eTag = headResponse.Headers.ETag?.Tag;
                    lastModified = headResponse.Content.Headers.LastModified;

                    if (!forceRefresh && TryReadDisk(diskPath, eTag, lastModified, out CachedSoundData? diskCached) && diskCached is not null)
                    {
                        haveValidDiskCache = true;
                        m_memoryCache.Set(cacheKey, diskCached, new MemoryCacheEntryOptions
                        {
                            AbsoluteExpiration = diskCached.ExpiresAt
                        });
                        return diskCached;
                    }
                }
                else if (headResponse.StatusCode != System.Net.HttpStatusCode.MethodNotAllowed &&
                         headResponse.StatusCode != System.Net.HttpStatusCode.NotImplemented)
                {
                    throw new HttpRequestException($"Remote server rejected HEAD for {uri}: {(int)headResponse.StatusCode}");
                }
            }
            catch (HttpRequestException) when (!forceRefresh)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.DebugFormat("[NGC.SOUND]: HEAD request for {0} failed: {1}", uri, ex.Message);
            }

            if (!haveValidDiskCache)
            {
                eTag = null;
                lastModified = null;
            }

            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            if (haveValidDiskCache && !string.IsNullOrEmpty(eTag))
                request.Headers.TryAddWithoutValidation("If-None-Match", eTag);
            if (haveValidDiskCache && lastModified.HasValue)
                request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified.Value.ToString("R", CultureInfo.InvariantCulture));

            using HttpResponseMessage response = m_httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                if (!File.Exists(diskPath))
                    throw new HttpRequestException("Remote server signalled 304 but cache missing");

                CachedSoundData diskData = CachedSoundData.FromFile(diskPath)
                    ?? throw new HttpRequestException("Failed to read cached audio");
                diskData.Refresh(m_settings.CacheTtl);
                m_memoryCache.Set(cacheKey, diskData, new MemoryCacheEntryOptions
                {
                    AbsoluteExpiration = diskData.ExpiresAt
                });
                return diskData;
            }

            response.EnsureSuccessStatusCode();

            string ct = DetectContentType(response.Content.Headers.ContentType?.MediaType, uri);
            ValidateContentType(ct);

            using Stream stream = response.Content.ReadAsStream();
            byte[] buffer = ReadToBuffer(stream, m_settings.MaxFileBytes);

            buffer = NormalizeToPcmWav(buffer, ct, uri, out string finalContentType);

            CachedSoundData data = new(
                buffer,
                finalContentType,
                DateTime.UtcNow.AddSeconds(m_settings.CacheTtl))
            {
                ETag = response.Headers.ETag?.Tag,
                LastModified = response.Content.Headers.LastModified?.UtcDateTime
            };

            WriteDisk(diskPath, data);

            m_memoryCache.Set(cacheKey, data, new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = data.ExpiresAt
            });
            return data;
        }
        finally
        {
            m_fetchSemaphore.Release();
        }
    }

    private bool TryReadDisk(string path, string? eTag, DateTimeOffset? lastModified, out CachedSoundData? data)
    {
        data = CachedSoundData.FromFile(path);
        if (data is null)
            return false;

        if (!string.IsNullOrEmpty(eTag) && !string.Equals(data.ETag, eTag, StringComparison.Ordinal))
            return false;

        if (lastModified.HasValue && data.LastModified.HasValue &&
            Math.Abs((data.LastModified.Value - lastModified.Value.UtcDateTime).TotalSeconds) > 1)
            return false;

        data.Refresh(m_settings.CacheTtl);
        return true;
    }

    private static string DetectContentType(string? mediaType, Uri uri)
    {
        mediaType = (mediaType ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrEmpty(mediaType) || mediaType == "application/octet-stream")
        {
            string ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            return ext switch
            {
                ".ogg" or ".oga"       => "audio/ogg",
                ".wav" or ".wave"      => "audio/wav",
                ".mp3"                 => "audio/mpeg",
                _                      => mediaType
            };
        }
        return mediaType;
    }

    private static void ValidateContentType(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
            throw new HttpRequestException("Remote audio missing Content-Type");

        mediaType = mediaType.ToLowerInvariant();
        // Remote playback relies on PCM WAV, matching the standard upload requirements.
        // Reject other formats (mp3/ogg/etc.) since they will not play correctly in-world.
        if (mediaType != "audio/wav" && mediaType != "audio/x-wav" && mediaType != "audio/wave" && mediaType != "audio/vnd.wave")
        {
            throw new HttpRequestException($"Unsupported audio content-type: {mediaType}; only PCM WAV is accepted");
        }
    }

    private static byte[] ReadToBuffer(Stream stream, long maxBytes)
    {
        using MemoryStream ms = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
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
        data.ToFile(path);
    }

    private byte[] NormalizeToPcmWav(byte[] buffer, string contentType, Uri uri, out string finalContentType)
    {
        // Na razie zakładamy, że serwer już daje poprawny PCM WAV,
        // bo i tak akceptujemy tylko WAV w ValidateContentType.
        // Tu można później dołożyć realne ffmpeg jeśli będzie potrzeba.

        if (buffer is null)
        {
            finalContentType = "audio/wav";
            return Array.Empty<byte>();
        }

        finalContentType = "audio/wav";
        return buffer;
    }

    private UUID EnsureAsset(CachedSoundData data, Uri uri)
    {
        if (m_scene is null)
           return UUID.Zero;

        UUID assetId = UUID.Parse(RemoteSoundSettings.GetCacheKey(uri));
        AssetBase asset = m_scene.AssetService.Get(assetId.ToString());
        if (asset is not null)
            return asset.FullID;

        // Dobierz typ assetu na podstawie Content-Type
        sbyte assetType = data.ContentType switch
        {
            "audio/wav" => (sbyte)AssetType.SoundWAV,
            "audio/x-wav" => (sbyte)AssetType.SoundWAV,
            "audio/wave" => (sbyte)AssetType.SoundWAV,
            "audio/vnd.wave" => (sbyte)AssetType.SoundWAV,
            _ => (sbyte)AssetType.Sound
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
