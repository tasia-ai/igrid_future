<?php
/*
Plugin Name: OpenSim Traffic Logger
Description: Hg panel for viewing login attempts.
Version: 1.0
Author: Tasia & Marty 💖
*/

add_action('admin_menu', function () {
    add_menu_page(
        'OpenSim Traffic Logs',
        'Grid Logs',
        'manage_options',
        'opensim-traffic-logs',
        'render_opensim_traffic_logs',
        'dashicons-visibility',
        81
    );
});

function render_opensim_traffic_logs() {
    global $wpdb;
    $table = 'opensim_auth_traffic';

    $logs = $wpdb->get_results("SELECT * FROM $table ORDER BY timestamp DESC LIMIT 100", ARRAY_A);

    echo '<div class="wrap"><h1>OpenSim Grid Access Logs</h1>';
    echo '<table class="widefat striped">';
    echo '<thead><tr>
        <th>ID</th>
        <th>UUID</th>
		<th>Status</th>
        <th>Avatar Name</th>
		<th>grid name</th>
        <th>Timestamp</th>
    </tr></thead><tbody>';

    foreach ($logs as $log) {
        echo '<tr>';
        echo '<td>' . esc_html($log['id']) . '</td>';
        echo '<td>' . esc_html($log['uuid']) . '</td>';
		echo '<td>' . esc_html($log['status']) . '</td>';
        echo '<td>' . esc_html($log['avatarname']) . '</td>';
        echo '<td>' . esc_html($log['gridname']) . '</td>';
        echo '<td>' . esc_html($log['timestamp']) . '</td>';
        echo '</tr>';
    }

    echo '</tbody></table></div>';
}