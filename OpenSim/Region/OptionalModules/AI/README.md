# SmartNPC profile, appearance, and teleport API

The OpenSim AI module registers three script functions for managing a live SmartNPC:

- `osSetSmartNPCProfile`
- `osSetSmartNPCAppearance`
- `osTeleportSmartNPC`

These functions operate on a SmartNPC created with `osCreateSmartNPC`. Keep the UUID returned by that function as `npcKey`.

## Invocation model

OpenSim injects the hidden `hostID` and `scriptID` parameters. LSL scripts must not pass those parameters.

The visible LSL signatures are:

```lsl
integer osSetSmartNPCProfile(key npcKey, string aboutText, string profileImage,
    string firstLifeText, string firstLifeImage, string webUrl,
    string accountTitle, string bornOn);

integer osSetSmartNPCAppearance(key npcKey, string notecardName);

integer osTeleportSmartNPC(key npcKey, key targetAgent);
```

The module is loaded as a region module and registers the functions dynamically. The module's own ownership checks still apply when the functions are called.

## Prerequisites

- `OpenSimAIModule` is enabled in `[OpenSimAI]`.
- The NPC was created with `osCreateSmartNPC` and is still a live NPC presence.
- The calling script is attached to the NPC owner's object.
- With `[OpenSimAI] IsPrivate = true`, the calling user must be the estate owner.
- The NPC owner must match the owner of the script host object.
- Image parameters are asset UUIDs, not texture names or URLs.
- A standard OpenSim avatar appearance notecard must be in the script host object's inventory for `osSetSmartNPCAppearance`.

## `osSetSmartNPCProfile`

Updates the NPC's stored profile and applies the live profile fields that the module supports.

### Parameters

| Parameter | Type | Description |
|---|---|---|
| `npcKey` | `key` | UUID returned by `osCreateSmartNPC` or the existing SmartNPC identity. |
| `aboutText` | `string` | NPC profile description. |
| `profileImage` | `string` | UUID text of the profile image asset. Use the zero UUID for no image. |
| `firstLifeText` | `string` | Background or first-life text. |
| `firstLifeImage` | `string` | UUID text of the first-life image asset. Use the zero UUID for no image. |
| `webUrl` | `string` | Profile or website text associated with the NPC. |
| `accountTitle` | `string` | NPC title, for example `Resident` or `Guide`. |
| `bornOn` | `string` | Display date or date text. The module stores the string as supplied. |

### Behavior

- `aboutText`, `profileImage`, and `bornOn` are applied to the live NPC profile and avatar data is sent to agents in the region.
- `firstLifeText`, `firstLifeImage`, `webUrl`, and `accountTitle` are stored for the virtual-avatar profile and used as AI context where applicable.
- Profile data is stored in the PostgreSQL `robust` database table `TasiaVirtualAvatars`.
- The database connection is read from `[DatabaseService] ConnectionString`; the module keeps the existing PostgreSQL settings and selects the `robust` database for profile data.
- An existing record is updated by virtual UUID or avatar name using PostgreSQL `ON CONFLICT` handling.
- If the database connection is not configured, the live update can still succeed in memory, but the profile will not survive a simulator restart.
- The function does not recreate the NPC.

### Return value

- `1` when the profile update was accepted and, when database persistence is configured, saved.
- `0` when the module is disabled, the NPC is not registered, the host or owner check fails, the NPC presence is unavailable, or persistence fails.

A `0` result does not provide a detailed error code. Check the simulator log and verify the NPC key, owner, and database availability.

### Example

Assume `npcKey` contains the UUID returned by `osCreateSmartNPC`:

```lsl
default
{
    state_entry()
    {
        integer result = osSetSmartNPCProfile(
            npcKey,
            "A guide for the I-Grid virtual world.",
            "00000000-0000-0000-0000-000000000000",
            "Created to help residents learn the grid.",
            "00000000-0000-0000-0000-000000000000",
            "https://grid.example/profile",
            "Resident Guide",
            "14/08/2026"
        );

        if (result == 1)
            llOwnerSay("SmartNPC profile updated.");
        else
            llOwnerSay("SmartNPC profile update failed.");
    }
}
```

Replace the zero UUID with the UUID text of an uploaded image asset when an image is required.

## `osSetSmartNPCAppearance`

Changes the appearance of a live SmartNPC without deleting and recreating it.

### Parameters

| Parameter | Type | Description |
|---|---|---|
| `npcKey` | `key` | UUID of a registered SmartNPC. |
| `notecardName` | `string` | Inventory name of the appearance notecard on the script host object. |

### Notecard requirements

The notecard must contain the serialized `AvatarAppearance` data used by OpenSim's standard appearance notecard. A plain text notecard, a texture UUID, or a clothing object is not sufficient.

Use the exact inventory name. The notecard must be readable by the region asset service.

### Behavior

- Loads the notecard from the script host object's inventory.
- Deserializes the avatar appearance.
- Calls the NPC module's live appearance update method.
- Keeps the same NPC UUID, chat identity, profile, and conversation state.
- Does not change profile text or profile images; use `osSetSmartNPCProfile` separately.

### Return value

- `1` when the appearance update was accepted.
- `0` when the module is disabled, the NPC is not registered, the owner check fails, the notecard is missing or invalid, or the NPC is not present.

### Example

```lsl
default
{
    state_entry()
    {
        integer result = osSetSmartNPCAppearance(npcKey, "appearance");

        if (result == 1)
            llOwnerSay("SmartNPC appearance updated.");
        else
            llOwnerSay("SmartNPC appearance update failed.");
    }
}
```

The inventory item named `appearance` must contain a valid OpenSim avatar appearance notecard.

## `osTeleportSmartNPC`

Requests a teleport for a SmartNPC to a target presence.

### Parameters

| Parameter | Type | Description |
|---|---|---|
| `npcKey` | `key` | UUID of a registered SmartNPC. |
| `targetAgent` | `key` | Target presence UUID. Pass `NULL_KEY` to use the script host object's owner. |

### Behavior

- `NULL_KEY` selects the owner of the script host object.
- The target must be a current, non-deleted, non-in-transit presence.
- The target cannot be the SmartNPC itself.
- The target and SmartNPC must currently be in the same scene.
- The SmartNPC is placed one metre to the right of the target and looks one metre upward.
- The function submits a location teleport and returns immediately; `1` means the request was accepted, not that the viewer's teleport completion event has already arrived.

### Return value

- `1` when a same-scene teleport request was submitted.
- `0` when the module is disabled, the NPC is not registered, the owner check fails, the target is unavailable, the target is in transit, the target is the NPC itself, the scenes differ, or the entity-transfer module is unavailable.

Cross-region teleport is intentionally rejected by this implementation. Use a same-region target or arrange a separate region-transfer workflow.

### Examples

Teleport the SmartNPC to its owner:

```lsl
default
{
    state_entry()
    {
        integer result = osTeleportSmartNPC(npcKey, NULL_KEY);

        if (result == 1)
            llOwnerSay("SmartNPC teleport requested.");
        else
            llOwnerSay("SmartNPC teleport could not be requested.");
    }
}
```

Teleport to another local presence:

```lsl
integer result = osTeleportSmartNPC(npcKey, targetAgentKey);
```

## Typical flow

1. Create the SmartNPC with `osCreateSmartNPC` and retain the returned key.
2. Store the key in a variable or other persistent script state.
3. Set its profile with `osSetSmartNPCProfile`.
4. Change its appearance with `osSetSmartNPCAppearance` when a new appearance notecard is available.
5. Call `osTeleportSmartNPC(npcKey, NULL_KEY)` to move it to its owner.
6. Check the integer return value and inspect the simulator log if an operation returns `0`.

Example setup using the three new functions:

```lsl
key npcKey;

default
{
    state_entry()
    {
        integer profileResult;
        integer appearanceResult;
        integer teleportResult;

        npcKey = (key)osCreateSmartNPC(
            "Tasia",
            "Guide",
            <128.0, 128.0, 128.0>,
            "appearance",
            "default"
        );

        if (npcKey == NULL_KEY)
        {
            llOwnerSay("SmartNPC creation failed.");
            return;
        }

        profileResult = osSetSmartNPCProfile(
            npcKey,
            "A friendly SmartNPC guide.",
            "00000000-0000-0000-0000-000000000000",
            "Here to help residents.",
            "00000000-0000-0000-0000-000000000000",
            "https://grid.example/profile",
            "Guide",
            "14/08/2026"
        );

        appearanceResult = osSetSmartNPCAppearance(npcKey, "appearance");
        teleportResult = osTeleportSmartNPC(npcKey, NULL_KEY);

        if (profileResult != 1 || appearanceResult != 1 || teleportResult != 1)
            llOwnerSay("One or more SmartNPC operations failed.");
    }
}
```

The appearance notecard and any image assets must exist before the script runs.

## Operational notes

- The Linux deployment contains the new AI DLL, but already-running simulator processes keep the previously loaded assembly until the next controlled restart.
- Do not restart the grid as part of documentation or ordinary content changes; schedule a restart when runtime activation is intended.
- Profile database operations are synchronous. Avoid calling the profile function repeatedly from high-frequency timer events.
- A profile can remain in memory without database persistence, but it will not be restored after restart.
- The current teleport implementation is deliberately same-scene only.

## Related files

- `OpenSimAIModule.cs` — implementation and permission checks.
- `OpenSim.Region.OptionalModules.AI.csproj` — AI module build definition.
- `docs/deployment-examples/config-include/osslEnable.ini` — example function allow-list entries.
- `virtual-avatar-profile-test.lsl` — existing `maGrid` profile and messaging test script.
