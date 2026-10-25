# Signed web LSL scripts

```lsl
default { state_entry() { maRunScriptFromWeb("https://scripts.example.org/hello.json"); } }
```

The function returns a request key immediately. It installs a new ordinary LSL
inventory script in the calling prim after verifying the publisher signature.
It never replaces the caller or evaluates C#/VB/JavaScript. Both YEngine and
Phlox have integration tests exercising this exact one-line call and the
installed script's `state_entry`.

## Configuration

The addon is disabled by default. Copy `WebScripts.ini.example` into the region
configuration and explicitly configure:

* `Enabled = true`
* `AllowedOwners`: nonzero UUIDs of permitted object owners.
* `AllowedScriptCreators`: nonzero UUIDs of permitted caller-script creators.
* `AllowedHosts`: exact ASCII HTTPS publisher DNS hostnames, comma-separated.
* `PublisherPublicKeyFile`: local path to the publisher's **public** RSA key.
* `ScriptEngine`: `YEngine` or `InWorldz.Phlox` on this grid.
* `[OSSL] AllowMODFunctions = true`.

Only grant owners and creators you trust. The addon independently checks those
grants even when called directly through the standard `modInvokeK` API. Existing
object inventory and parcel script permissions still apply. No runtime script
permission grants are inherited. Compilation remains the normal engine's job.

The caller receives `link_message` with its request key: code **202** means the
signature was verified and compilation queued, **403** means rejected. A 202
is not proof compilation or `state_entry` completed; inspect normal script
errors. Logs omit URLs, source, response bodies and credentials.

Limits: one request per owner per minute; four concurrent requests per region;
one per prim at a time; four web-installed scripts per prim; 10-second download
timeout; 64 KiB decoded source and 100,000-byte JSON limit. Redirects, cookies,
ambient proxies, compressed responses, private/reserved IPs and non-443 ports
are rejected. DNS answers are validated and the connection is pinned to a
validated address while normal TLS hostname/certificate validation remains on.
Ownership and caller asset identity are checked again after downloading and
asset storage. Module shutdown cancels pending downloads.

## Offline PHP publisher

Generate a dedicated signing key **outside the web root**:

```sh
umask 077
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out publisher-private.pem
openssl pkey -in publisher-private.pem -pubout -out publisher-public.pem
php tools/sign-script.php --script hello.lsl \
  --url https://scripts.example.org/hello.json \
  --private-key publisher-private.pem --out hello.json
```

Install only the public key on the grid. Publish only `hello.json` over HTTPS
with `Content-Type: application/json`; never publish the signing tool/private
key as a web endpoint. The generator is CLI-only and refuses to overwrite an
existing output. Re-sign into a new file and deploy it atomically when updating.
`--expires UNIX_SECONDS` is optional; default validity is one hour, maximum one
day. URLs must use ASCII/percent encoding. URL and expiry are covered by the
signature. Expiry controls new installations; installed ordinary inventory
scripts continue normally until stopped or removed.

Envelope fields: `version` (1), `url`, `expires`, `script` (canonical Base64 UTF-8
LSL), and `signature` (Base64 RSA/SHA-256 PKCS#1 v1.5 signature). Signed bytes are
`maRunScriptFromWeb:v1\n{url}\n{expires}\n{script}` with UTF-8 encoding. Base64 is
encoding, **not encryption or copy protection**. Trusting a publisher authorizes
its LSL code to act with the object's ordinary script capabilities. Rotate the
configured public key to revoke a publisher's future installations.

## Sharing with another grid

The module's security and downloader do not depend on the new builtin API.
Stock grids can paste `portable/maRunScriptFromWeb.lsl` into the caller instead;
the same one-line call then uses `modInvokeK("maRunScriptFromWeb", url)`.
Do not define that helper where the builtin already exists.

Rebuild against the target grid's exact assemblies and supported .NET runtime;
never copy this grid's core DLLs into another installation. Copy this entire
addon directory outside the repository and use the standalone project:

```sh
dotnet build portable/WebScripts.Portable.csproj -c Release \
  -p:WebScriptsTargetFramework=net10.0 -p:OpenSimHome=/path/to/target/program
```

For older Mono.Addins discovery, add `-p:LegacyDiscovery=true`; .NET 8 targets use
`-p:WebScriptsTargetFramework=net8.0`. This requires a compatible OpenSim region
API, ScriptModuleComms and an LSL engine. Other vendors/runtimes need validation
on their host; portability does not imply one DLL works on every grid.

Build the repository version with `dotnet build Addons/TasiaAddons.WebScripts`.
It is included in the region server's normal publish dependency graph and
registered through `IPluginRegistryProvider`. To explicitly select it in a
Tranquillity configuration use `[RegionModules] Setup_WebScriptsModule = WebScriptsModule`.

Run `tools/quality-review.sh` for the complete repeatable review. PHP CLI is
required for the PHP-to-.NET signature compatibility test (`PHP_BINARY` can
select another executable). Security unit tests use no external publisher.
Engine integration replaces only HTTP transport; signature checks and actual
LSL compilation/execution remain real.
