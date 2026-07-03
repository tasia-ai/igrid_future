# TasiaAddon script API

This fork registers a dedicated script API assembly for Tasia add-on functions so they stay out of the upstream OSSL implementation while still behaving like standard `os*` calls.

## Assembly

* **Project:** `Source/TasiaAddon.Scripts`
* **Namespace:** `TasiaAddon.Scripts`
* **API class:** `OSSL_TasiaAddonsApi`

The API is added to YEngine alongside the core OSSL API so that scripts can call custom functions without touching `OSSL_Api`.

## Remote sound wrapper

`OSSL_TasiaAddonsApi` exposes `osNgcPlaySoundURL`, which forwards the request to the region's `IRemoteSoundModule` implementation (provided by the RemoteSound add-on module). The function follows the same permission/threat checks as other OSSL calls and is controlled through the normal `Allow_osNgcPlaySoundURL` configuration key.

## Configuration

Add the permission flag to your OSSL configuration (for example in `bin/config-include/osslEnable.ini`):

```
[OSSL]
Allow_osNgcPlaySoundURL = "${OSSL|osslParcelOG}ESTATE_MANAGER,ESTATE_OWNER"
```

## Example LSL usage

```lsl
// Plays a WAV file hosted at a URL for the toucher
osNgcPlaySoundURL("http://example.com/test.wav", 1.0, llDetectedKey(0));
```
