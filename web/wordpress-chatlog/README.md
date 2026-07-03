# OpenSim Chat Audit Viewer (WordPress)

This WordPress plugin displays the recent chat and instant-message transcripts captured by the `ChatAudit` add-on shipped with the Tranquillity fork of OpenSim. Drop the plugin folder into your WordPress `wp-content/plugins` directory and activate it from the **Plugins** screen.

## Configuration

1. Navigate to **OpenSim Chat Logs** in the WordPress admin menu.
2. Enter the HTTPS endpoint that exposes your grid's audit log. This is the same URL configured by `ApiPath` under the `[ChatAudit]` section of `OpenSim.ini` (for example: `https://grid.example.com/addons/chat-audit`).
3. If your grid requires an API token, paste the secret configured under `ApiToken`.
4. Adjust the record limit or event filter (`chat` or `im`) as desired and save changes.

Once configured, the page will display the most recent audit entries in a sortable table. You can also embed the log on any restricted admin page or dashboard widget using the `[opensim_chat_audit]` shortcode (only users with the `manage_options` capability can view the data).

## Security Notes

- Always serve the endpoint over HTTPS and configure a strong `ApiToken` to prevent unauthorised access.
- The plugin requires administrator permissions because the transcript may contain sensitive user conversations.
- The displayed data is only as complete as the buffer retained by the region server; older entries are pruned according to the `RecentEntryLimit` configured in OpenSim.

## Development

The plugin uses the WordPress HTTP API (`wp_remote_get`) to query the grid and does not require any external libraries. Contributions and improvements can be made directly in this repository under `wordpress-chatlog/`.
