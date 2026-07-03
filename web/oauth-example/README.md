# OAuth PHP Examples

This directory contains drop-in PHP snippets that demonstrate how to talk to the
Robust OAuth endpoints exposed by the `TasiaAddon.WoWonder` add-on. The
samples complement the `wowonder-openim` skeleton by showing how to perform a
simple OAuth 2.0 token exchange from a standalone PHP page and how to embed the
same logic inside a WoWonder add-on settings page.

## Files

- `generic-oauth-client.php` – Minimal PHP page that uses the client
  credentials grant to fetch a bearer token and call a protected REST endpoint.
- `wowonder-settings-fragment.php` – A WoWonder admin panel fragment that wires
  the Robust OAuth authorize/token flows into WoWonder's session helpers.

Copy or adapt the snippets to match your deployment, replacing the placeholder
values (`ROBUST_BASE_URL`, `CLIENT_ID`, etc.) with real configuration for your
environment. Both examples assume HTTPS endpoints and a PHP installation with
cURL enabled.
