# Remote URL sound (no viewer changes)

Install FFmpeg with libvorbis on the region host, then explicitly enable:

```ini
[NGC.Sound]
Enable = true
AllowHTTPSOnly = true
FfmpegPath = ffmpeg
MaxFileBytes = 20971520
CacheTTLSeconds = 3600
; AllowedDomains = audio.example.com
; AllowScriptOwners = avatar-uuid
```

Set `AllowHTTPSOnly = false` if legacy HTTP is needed. Both URL schemes use the same server-side download path; viewers never contact the audio origin. Private addresses are rejected, redirects are revalidated, and DNS connections are pinned to permitted addresses. Use an explicit domain/owner allowlist and network egress policy in production. Windows may use an absolute path to ffmpeg.exe.

The existing script signature is unchanged: `string ngcPlaySoundURL(string url, float volume, key target, float cacheOverride)`. Empty return indicates dispatch, not verified audible playback. Positive cacheOverride refreshes the download. Audio is transcoded to mono 44.1 kHz Ogg Vorbis, stored as a standard Sound asset, and dispatched using the existing targeted sound message. The first 10 seconds are used; this is not continuous streaming. Legacy WAV cache entries are not reused. Old streaming and WAV-transcode configuration switches no longer apply.

## Offline and integration verification checklist

- Build the addon and solution on Windows and Linux. Check FFmpeg availability and libvorbis.
- Generate local MP3/WAV/Ogg/AAC fixtures and verify resulting Ogg Vorbis mono 44100 Hz assets, duration <= 10 seconds (allow codec granularity).
- Test HTTP opt-in, HTTPS default, redirects to denied domains/private endpoints, malformed media, oversized input, missing FFmpeg and timeout.
- Verify cache hit and positive cacheOverride with changed source content; check standard asset type and distinct asset ID for changed bytes.
- On an authorized disposable region only, verify targeted playback in Tasia Viewer and Firestorm, muted objects, and no viewer-side request to source URL.
- Confirm disk/asset retention policy before production: generated assets and expired disk files are not automatically purged by this change.
