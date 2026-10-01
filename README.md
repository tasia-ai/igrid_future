# TasiaNGC OpenSim

**TasiaNGC** - OpenSim Fork of NGC Tranquillity (Mike) with Advanced Addons made by Tasia

<img src="https://i.let-us.cyou/wp-content/uploads/2026/03/image-768x1152.png" alt="My cool logo" max-hight="5%" max-width="5%"/>

## License and Usage Approval (Important)

- See **[LICENSE.txt](./LICENSE.txt)** for full terms.
- Upstream/OpenSim components keep their original open-source licenses.
- **Tasia-authored additions/modifications require visible attribution, sharing modifications back, and prior written approval for usage (including public-source usage).**

## Tasia AI Addons

All addons are built-in and ready to use:

- **AccessLogger** - Login audit with IP, UUID, MAC, hardware ID, grid URI
- **LoginSecurity** - IP/Hardware banning, ToS prompt  
- **Profile Badges** - customer_type system (free, premium, founder, etc.)
- **ChatAudit** - Full chat/IM logging
- **MACAudit** - Hardware audit logging
- **RemoteSound** - Play remote audio URLs via script
- **SmartNPC** - Script-driven NPCs with an `expertise`, plus `osAI` for one-shot model calls
- **QUIC** - `TasiaAddons.Quic`, QUIC transport for viewers via a Quick-G bridge, on by default
- **Marketplace** - Prim delivery API
- **WoWonder** - Social network integration
- **AlertNotifications** - Push notifications
- **RestartModule** - Region restart with dialogs
- **AbuseReports** - In-world abuse reporting
- **./web** - wordpress and standalone addons including auth system, admin panel and complex hg auth.
For detailed addon documentation, see **[ADDONS.md](./md/addons.md)**
changes history: **[ADDONS.md](./md/History.md)**

### Script functions

Beyond the addons above, the script engine exposes functions that scripts call directly.
Permissions live in `osslEnable.ini` (`[OSSL]` section, `Allow_<name>`), and the editor's
autocomplete list is `bin/ScriptSyntax.xml` — a function needs an entry in both to be
visible and callable.

| Function | Purpose |
| --- | --- |
| `maRequestAsset(uuid, login_uri)` | Import an asset from another OpenSim grid over Hypergrid, keep it in the local asset database permanently, and return the **local** uuid for use with `llPlaySound`, `llSetTexture` and the rest of the audio/texture API. Returns `NULL_KEY` if the remote asset cannot be fetched. If the asset is already local, the existing uuid comes back with no refetch. |
| `osAI(systemPrompt, userPrompt, maxTokens, temperature)` | Send a prompt to the configured language model, returns the reply as a string. |
| `osCreateSmartNPC(firstName, lastName, position, notecardName, expertise)` | Spawn a smart NPC with a name, an optional notecard, and an `expertise` that steers its conversation. |
| `osSetSmartNPC(npcKey, expertise)` | Replace the expertise of an existing smart NPC. |
| `osNpcInstantMessage(npcKey, destination, message)` | Instant message to an avatar or NPC, from a smart NPC's point of view. |
| `ngcPlaySoundURL(url, volume, ...)` | Play audio fetched from a URL rather than a stored asset. |

```lsl
string local = maRequestAsset("e0c2a9de-0f1a-4b3c-8d7e-9a1b2c3d4e5f",
                             "http://example-grid.org:8002/");
if (local != NULL_KEY)
    llPlaySound(local, 1.0);
else
    llSay("could not fetch that asset");
```

### QUIC transport

`TasiaAddons.Quic` puts a QUIC listener in front of the LLUDP stack. Robust owns the
public viewer-facing port and the `QuicProxyConnector` service; regions do not expose a
per-region QUIC port of their own. Routes are registered through the Quick-G bridge, so
**Quick-G is required** — `AllowNativeQuicFallback = false` means Robust pauses QUIC
startup rather than silently downgrading if it fails.

Relevant sections in `Robust.ini`:

| Section | Purpose |
| --- | --- |
| `[ClientStack.Quic]` | Native listener settings, PKCS#12 certificate path. |
| `[QuicProxy]` | The `QuicProxyConnector` service, PEM certificate and key, and the `QuicPoolStart`/`QuicPoolEnd` range valid for per-region port allocation. Ports outside the pool are treated as stale. |
| `[QuickG]` | Bridge executable, public and private ports, region brain, and certificate paths. The private control port must never be exposed publicly. |

Certificate and key files are read from `SSL/quic/` — `quic-cert.pem`, `quic-key.pem`,
and `quic-cert.p12` for the native listener. Export the key in a format the MsQuic build
on the host can load, and set the PKCS#12 password if the file is protected.

Note on known limitations: the concurrent-write path in the QUIC client can raise
"This method may not be called when another write operation is pending" under load, and
`QuicClientConnection`/`QuicServerConfig` still sit in core because `LLUDPServer` is typed
to their concrete classes, so the region side is not yet fully plugin-extracted.

---


## Overview

OpenSim is a BSD Licensed Open Source project to develop a functioning
virtual worlds server platform capable of supporting multiple clients
and servers in a heterogeneous grid structure. OpenSim is written in
C#, and can run under Mono or the Microsoft .NET runtimes.

This is considered an alpha release.  Some stuff works, a lot doesn't.
If it breaks, you get to keep *both* pieces.

# Compiling OpenSim

Please see BUILDING.md

# Running OpenSim on Windows

You will need dotnet 8.0 runtime (https://dotnet.microsoft.com/en-us/download/dotnet/8.0)


To run OpenSim from a command prompt

 * cd to the bin/ directory where you unpacked OpenSim
 * review and change configuration files (.ini) for your needs. see the "Configuring OpenSim" section
 * run OpenSim.exe


# Running OpenSim on Linux/Mac

You will need

 * [dotnet 8.0 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
 * libgdiplus 
 
 if you have mono 6.x complete, you already have libgdiplus, otherwise you need to install it
 using a package manager for your operating system, like apt, brew, macports, etc
 for example on debian:
 
 `apt-get update && apt-get install -y apt-utils libgdiplus libc6-dev`
 
To run OpenSim, from the unpacked distribution type:

 * cd bin
 * review and change configuration files (.ini) for your needs. see the "Configuring OpenSim" section
 * run ./opensim.sh


# Configuring OpenSim

When OpenSim starts for the first time, you will be prompted with a
series of questions that look something like:

	[09-17 03:54:40] DEFAULT REGION CONFIG: Simulator Name [OpenSim Test]:

For all the options except simulator name, you can safely hit enter to accept
the default if you want to connect using a client on the same machine or over
your local network.

You will then be asked "Do you wish to join an existing estate?".  If you're
starting OpenSim for the first time then answer no (which is the default) and
provide an estate name.

Shortly afterwards, you will then be asked to enter an estate owner first name,
last name, password and e-mail (which can be left blank).  Do not forget these
details, since initially only this account will be able to manage your region
in-world.  You can also use these details to perform your first login.

Once you are presented with a prompt that looks like:

	Region (My region name) #

You have successfully started OpenSim.

If you want to create another user account to login rather than the estate
account, then type "create user" on the OpenSim console and follow the prompts.

Helpful resources:
 * http://opensimulator.org/wiki/Configuration
 * http://opensimulator.org/wiki/Configuring_Regions

# Connecting to your OpenSim

By default your sim will be available for login on port 9000.  You can login by
adding -loginuri http://127.0.0.1:9000 to the command that starts Second Life
(e.g. in the Target: box of the client icon properties on Windows).  You can
also login using the network IP address of the machine running OpenSim (e.g.
http://192.168.1.2:9000)

To login, use the avatar details that you gave for your estate ownership or the
one you set up using the "create user" command.

# Bug reports

In the very likely event of bugs biting you (err, your OpenSim) we
encourage you to see whether the problem has already been reported on
the [OpenSim mantis system](http://opensimulator.org/mantis/main_page.php).

If your bug has already been reported, you might want to add to the
bug description and supply additional information.

If your bug has not been reported yet, file a bug report ("opening a
mantis"). Useful information to include:
 * description of what went wrong
 * stack trace
 * OpenSim.log (attach as file)
 * OpenSim.ini (attach as file)


# More Information on OpenSim

More extensive information on building, running, and configuring
OpenSim, as well as how to report bugs, and participate in the OpenSim
project can always be found at http://opensimulator.org.

Thanks for trying OpenSim, we hope it is a pleasant experience.
