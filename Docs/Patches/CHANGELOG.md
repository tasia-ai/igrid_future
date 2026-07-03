# Stored Patch Change Log

This log tracks the feature work bundled inside the stored diffs under
`Docs/Patches` so grid operators can confirm the artifacts reflect every change
we maintain locally.

## Customer Badge Capability
- Adds a `customer_type` field to profile data models and database migrations.
- Extends region core/optional modules to send the badge over CAPS and UDP.
- Introduces `IAvatarBadgeModule` plus the region-side `BadgeModule` capability
  handler with configuration toggles.
- Updates client mocks and configuration examples so automated tests and sample
  deployments surface the new badge option.

## WoWonder Integration
- Documents the OAuth, instant messaging, and wallet synchronization endpoints
  required for WoWonder portals (`Docs/Designs/WoWonderIntegration.md`).
- Implements the Robust `WoWonderServiceConnector` with OAuth, IM, userinfo, and
  money balance endpoints plus token caching helpers.
- Packages the OAuth, IM relay, and marketplace handlers inside the
  `TasiaAddon.WoWonder` add-on assembly so upgrades can re-apply the module
  by dropping a single project back into the tree.
- Moves the legacy admin `/send` prim delivery endpoint into the
  `TasiaAddon.Marketplace` startup add-on with its own `OpenSim.ini`
  configuration block.

## Chat & IM Audit Add-on
- Adds the `TasiaAddon.ChatAudit` region module that captures public chat and
  grid instant messages to a JSONL log with an optional authentication token.
- Exposes a `/addons/chat-audit` HTTP endpoint so WordPress dashboards can
  request recent conversations without touching the simulator log files.
- Ships default configuration knobs in `OpenSim.ini.example` for easy
  enable/disable, log path overrides, and buffer sizing.
- Adds a WordPress plugin under `wordpress-chatlog/` that consumes the
  ChatAudit endpoint and renders transcripts inside the WordPress admin UI.
- Provides PHP add-on scaffolding in `Docs/Patches/php/wowonder-openim/` so the
  web integration can authenticate users, sync balances, and relay IMs.
- Bundles standalone PHP OAuth samples in `Docs/Patches/php/oauth-example/` to
  help operators integrate the ChatAudit endpoint with other dashboards.
- Captures the full diff (`customer_badge_changes.diff`) and git-formatted patch
  (`customer_badge_changes.patch`) for replaying these changes after upstream
  merges.
