<?php
/*
Plugin Name: Partner Allow Access
Description: Allows users from partner grids to be automatically added to the OpenSim auth database.
Version: 1.0
Author: Marty
*/

if (!defined('ABSPATH')) exit; // Prevent direct access

function partner_allow_shortcode() {
    global $wpdb;
    $auth_table = $wpdb->prefix . "opensim_auth";
    $partner_table = "partners"; // if this has prefix too, update this line

    $message = '';
    $uuid = sanitize_text_field($_GET['uuid'] ?? '');
    $grid = sanitize_text_field($_GET['grid'] ?? '');

    if ($_SERVER['REQUEST_METHOD'] == 'POST' && isset($_POST['opensim_uuid'], $_POST['opensim_grid'])) {
        $uuid = sanitize_text_field($_POST['opensim_uuid']);
        $grid = sanitize_text_field($_POST['opensim_grid']);
        $is_partner = $wpdb->get_var($wpdb->prepare("SELECT COUNT(*) FROM $partner_table WHERE gridname = %s", $grid));

        if ($is_partner) {
            if (!empty($uuid) && !empty($grid)) {
                $wpdb->insert($auth_table, [
                    'uuid' => $uuid,
                    'grid' => $grid,
                    'banned' => 0
                ]);
                $message = '<p style="color: green;">Access granted. You have been added!</p>';
            } else {
                $message = '<p style="color: red;">Error: UUID or Grid missing.</p>';
            }
        } else {
            $message = '<p style="color: red;">Your Grid is not a partner grid. Need Manual Setup</p>';
        }
    }

    return '<form method="POST">
                <label>Grid Address:</label>
                <input type="text" name="opensim_grid" placeholder="grid.we.love" required>
                <label>Your UUID:</label>
                <input type="text" name="opensim_uuid" placeholder="00000000-0000-0000-0000-000000000000" required>
                <button type="submit">Submit</button>
            </form>' . $message;
}

add_shortcode('partner_allow', 'partner_allow_shortcode');
?>