# NGC Remote Sound Module

The Remote Sound module exposes the `ngcPlaySoundURL` script call that streams HTTPS audio content to nearby avatars with the same semantics as `llPlaySound`. The module performs strict validation, caching, and rate limiting so that untrusted scripts cannot overwhelm the simulator or fetch content from unauthorised domains.

## Installation

1. Build the project and copy `TasiaAddon.RemoteSound.dll` (and its addin manifest) to your add-on modules directory.
2. Add the `[NGC.Sound]` section to your region configuration (see below) and restart the simulator.
3. Enable the module in your `OpenSim.ini` (or include file) by adding `TasiaAddon.RemoteSound.dll` to the `[Startup]` `region_modules` list or by placing the assembly in an auto-loaded add-on location.

## Configuration

```
[NGC.Sound]
Enable = true
AllowHTTPSOnly = true
MaxFileBytes = 10485760
CacheDir = ./data/ngc-sound-cache
CacheTTLSeconds = 3600
PerObjectRatePerSec = 2
PerRegionRatePerSec = 20
AllowedDomains = cutegrid.net,cdn.cutegrid.net,example.com
DeniedDomains =
AllowScriptOwners = 00000000-0000-0000-0000-000000000000
DefaultFallbackSound = 01234567-89ab-cdef-0123-456789abcdef
```

* **AllowHTTPSOnly** – require `https://` URLs. Set to `false` to permit `http://` sources.
* **MaxFileBytes** – refuse audio files larger than this limit.
* **CacheDir/CacheTTLSeconds** – disk + memory cache directory and lifetime. The module revalidates cached content using `ETag`/`Last-Modified` where the upstream server provides them.
* **PerObjectRatePerSec / PerRegionRatePerSec** – rate limiters that apply to each object and the entire region (expressed in requests per second).
* **AllowedDomains / DeniedDomains** – optional domain allow/deny lists (comma or semicolon separated).
* **AllowScriptOwners** – restrict usage to specific avatar UUIDs. Leave blank to allow all owners.
* **DefaultFallbackSound** – optional UUID that will play if the remote fetch fails.

## Script usage

```
string ngcPlaySoundURL(string url, float volume, float radius, float cacheHint);
```

* Returns an empty string on success, or an error message on failure.
* `volume` is clamped to the `[0.0, 1.0]` range.
* `radius` controls the audible distance and defaults to 20 metres.
* `cacheHint > 0` bypasses the cached copy and forces a revalidation of the remote file.

### Demo LSL script

See [`examples/RemoteSoundDemo.lsl`](examples/RemoteSoundDemo.lsl) for a touch-to-play example that reports failures via `llOwnerSay`.

## Troubleshooting

* **"Remote sound module disabled"** – the module did not load because `[NGC.Sound]` is missing or `Enable = false`.
* **"Per-object remote sound rate exceeded"** – the object is exceeding the configured rate limit. Reduce the number of rapid requests or raise `PerObjectRatePerSec`.
* **"Domain not permitted"** – the URL host is not included in `AllowedDomains` or is present in `DeniedDomains`.
* **Cached content not refreshing** – call `ngcPlaySoundURL` with `cacheHint = 1` to bypass the cache for a single invocation.

## Testing

Unit tests live in `Tests/TasiaAddon.RemoteSound.Tests`. Execute them with `dotnet test` or as part of the solution test suite.
