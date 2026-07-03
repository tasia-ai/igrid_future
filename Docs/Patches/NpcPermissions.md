## NPC permission handling for scripted animation invites

The script runtime now understands two region *extra settings* that help NPCs respond to scripted permission requests such as dance HUD invites.

- `npc_auto_grant_anim_perms`: When set to `true`, NPCs auto-grant `PERMISSION_TRIGGER_ANIMATION` and `PERMISSION_OVERRIDE_ANIMATIONS` requests even if the requester is not the NPC or the NPC's owner. This lets generic animation HUDs drive NPC animations without stalls.
- `npc_forward_perms_to_owner`: When set to `true`, and the NPC's owner is present in the region, permission dialogs sent to an NPC are relayed to the owner so they can approve or deny on the NPC's behalf. Permissions are still granted to the NPC key after approval.

These settings use the region *extra settings* storage (the same mechanism used by `auto_grant_attach_perms`) and can be toggled via console or database depending on your deployment.
