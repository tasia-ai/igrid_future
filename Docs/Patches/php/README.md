# PHP Artifacts for WoWonder Integration

This folder ships a lightweight WoWonder add-on skeleton that pairs with the
Robust OAuth, instant-messaging, and money synchronization endpoints described
in `Docs/Designs/WoWonderIntegration.md`, plus stand-alone OAuth examples you
can adapt for other PHP front ends.

Copy the `wowonder-openim` directory into your WoWonder installation (for
example, `wowonder/requests/opentosim/`) and follow the inline comments to hook
it into your site's add-on registration flow. The scripts are preconfigured to
talk to the `/wowonder/oauth/*`, `/wowonder/send_im`, and
`/wowonder/money/balance` endpoints exposed by the
`TasiaAddon.WoWonder` add-on's `WoWonderServiceConnector`. Generic
integrations can reference `Docs/examples/send_im.php` for a minimal
Bearer-token IM sender against `/api/v1/im/send`.

If you need a more generic PHP integration, the `oauth-example` directory
contains drop-in snippets demonstrating how to call the OAuth token endpoint
and render chat audit data outside of WoWonder.
