<?php
/*
Plugin Name: OpenSim Allow Access
Description: Allows users to enter a global password to get added to the OpenSim authorized users database.
Version: 1.0
Author: Your Name
*/

if (!defined('ABSPATH')) exit; // Prevent direct access

// Create settings menu in WordPress admin
function opensim_allow_menu() {
    add_options_page('OpenSim Allow Settings', 'OpenSim Allow', 'manage_options', 'opensim-allow', 'opensim_allow_settings_page');
}
add_action('admin_menu', 'opensim_allow_menu');

// Register setting for password
function opensim_allow_register_settings() {
    register_setting('opensim_allow_settings', 'opensim_global_password');
}
add_action('admin_init', 'opensim_allow_register_settings');

// Settings Page Content
function opensim_allow_settings_page() {
    ?>
    <div class="wrap">
        <h2>OpenSim Allow Settings</h2>
        <form method="post" action="options.php">
            <?php settings_fields('opensim_allow_settings'); ?>
            <table class="form-table">
                <tr>
                    <th>Global Password:</th>
                    <td><input type="password" name="opensim_global_password" value="<?php echo esc_attr(get_option('opensim_global_password', '')); ?>" /></td>
                </tr>
            </table>
            <?php submit_button(); ?>
        </form>
    </div>
    <?php
}

// Shortcode for user authentication
function opensim_allow_shortcode() {
    global $wpdb;
    $table_name = $wpdb->prefix . "opensim_auth";
    
    $message = '';
    $uuid = sanitize_text_field($_GET['uuid'] ?? '');
    
    if ($_SERVER['REQUEST_METHOD'] == 'POST' && isset($_POST['opensim_password'], $_POST['opensim_uuid'])) {
        $entered_password = sanitize_text_field($_POST['opensim_password']);
        $global_password = get_option('opensim_global_password', '');
        $uuid = sanitize_text_field($_POST['opensim_uuid']);
        
        if ($entered_password === $global_password) {
            if (!empty($uuid)) {
                $wpdb->insert($table_name, ['uuid' => $uuid, 'banned' => 0]);
                $message = '<p style="color: green;">Access granted. You have been added!</p>';
            } else {
                $message = '<p style="color: red;">Error: No UUID provided.</p>';
            }
        } else {
            $message = '<p style="color: red;">Incorrect password.</p>';
        }
    }
    
    return '<form method="POST">
                <label>Enter UUID:</label>
                <input type="text" name="opensim_uuid" required>
                <label>Enter Password:</label>
                <input type="password" name="opensim_password" required>
                <button type="submit">Submit</button>
            </form>' . $message;
}
add_shortcode('opensim_allow', 'opensim_allow_shortcode');
?>