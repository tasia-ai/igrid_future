<?php
/*
Plugin Name: OpenSim HyperGrid Auth Admin
Description: Manage authorized users for OpenSimulator HyperGrid authentication.
Version: 1.5
Author: Tasia with Gemini
*/

if (!defined('ABSPATH')) {
    exit; // Exit if accessed directly
}

/**
 * Global constant for the allowed username for unbanning.
 * This is hardcoded as per the user's requirement.
 */
define('OPENSIM_AUTH_UNBAN_USERNAME', 'marty');

/**
 * Adds the plugin menu item to the WordPress admin.
 */
function opensim_auth_admin_menu() {
    add_menu_page(
        'OpenSim Auth',
        'OpenSim Auth',
        'manage_options', // Required capability to access this menu
        'opensim-auth',
        'opensim_auth_admin_page',
        'dashicons-admin-users', // Icon for the menu item
        30 // Position in the menu
    );
}
add_action('admin_menu', 'opensim_auth_admin_menu');

/**
 * Checks if the necessary database tables exist and creates/updates them if not.
 * Runs on 'admin_init' to ensure the table is always available when needed.
 */
function opensim_auth_check_table() {
    global $wpdb;
    $table_name = "wp_opensim_auth";
    $partner_table = "partners";
    $banned_table = "banned_grids";

    // Check if the main opensim_auth table exists
    if ($wpdb->get_var("SHOW TABLES LIKE '$table_name'") != $table_name) {
        opensim_auth_install(); // Install if table does not exist
    } else {
        // Check for 'comment' column and add if missing (for plugin updates)
        $columns = $wpdb->get_col("DESC $table_name", 0);
        if (!in_array('comment', $columns)) {
            $wpdb->query("ALTER TABLE $table_name ADD COLUMN comment TEXT DEFAULT ''");
        }
    }

    // Check and create opensim_partners table if it doesn't exist
    if ($wpdb->get_var("SHOW TABLES LIKE '$partner_table'") != $partner_table) {
        $charset_collate = $wpdb->get_charset_collate();
        $sql_partners = "CREATE TABLE $partner_table (
            id INT AUTO_INCREMENT PRIMARY KEY,
            gridname VARCHAR(255) NOT NULL UNIQUE KEY
        ) $charset_collate;";
        require_once(ABSPATH . 'wp-admin/includes/upgrade.php');
        dbDelta($sql_partners);
    }

    // Check and create opensim_banned_grids table if it doesn't exist
    if ($wpdb->get_var("SHOW TABLES LIKE '$banned_table'") != $banned_table) {
        $charset_collate = $wpdb->get_charset_collate();
        $sql_banned = "CREATE TABLE $banned_table (
            id INT AUTO_INCREMENT PRIMARY KEY,
            gridname VARCHAR(255) NOT NULL UNIQUE KEY
        ) $charset_collate;";
        require_once(ABSPATH . 'wp-admin/includes/upgrade.php');
        dbDelta($sql_banned);
    }
}
add_action('admin_init', 'opensim_auth_check_table');

/**
 * Handles all POST requests for the OpenSim Auth plugin.
 * This function is hooked into 'admin_post_opensim_auth_action'.
 */
function opensim_auth_handle_post_actions() {
    // Verify user capabilities and nonce before processing any action
    if (!current_user_can('manage_options')) {
        wp_die('You do not have sufficient permissions to perform this action.');
    }

  /*  if (!isset($_POST['opensim_auth_nonce']) || !wp_verify_nonce($_POST['opensim_auth_nonce'], 'opensim_auth_nonce_action')) {
        wp_die('Security check failed! Please try again.');
    }*/

    global $wpdb;
    $table_name = "wp_opensim_auth";
    $partner_table = "partners";
    $banned_table = "banned_grids";

    $action_type = sanitize_text_field($_POST['action_type'] ?? '');
    $uuid = sanitize_text_field($_POST['uuid'] ?? '');
    $gridname = sanitize_text_field($_POST['gridname'] ?? '');
    $comment = sanitize_text_field($_POST['comment'] ?? '');
    $current_user = wp_get_current_user();
    $can_unban_user = ($current_user->user_login === OPENSIM_AUTH_UNBAN_USERNAME);


    switch ($action_type) {
        case 'add_uuid':
            if (empty($uuid) || empty($gridname)) {
                wp_redirect(add_query_arg('opensim_message', 'empty_fields', admin_url('admin.php?page=opensim-auth')));
                exit;
            }
            $exists = $wpdb->get_var($wpdb->prepare(
                "SELECT COUNT(*) FROM $table_name WHERE uuid = %s AND gridname = %s",
                $uuid,
                $gridname
            ));
            if (!$exists) {
                $wpdb->insert(
                    $table_name,
                    [
                        'uuid' => $uuid,
                        'gridname' => $gridname,
                        'comment' => $comment,
                        'banned' => 0
                    ],
                    ['%s', '%s', '%s', '%d']
                );
                wp_redirect(add_query_arg('opensim_message', 'added', admin_url('admin.php?page=opensim-auth')));
            } else {
                wp_redirect(add_query_arg('opensim_message', 'exists', admin_url('admin.php?page=opensim-auth')));
            }
            exit;

        case 'remove_uuid':
            if (empty($uuid) || empty($gridname)) {
                wp_redirect(add_query_arg('opensim_message', 'empty_fields', admin_url('admin.php?page=opensim-auth')));
                exit;
            }
            $is_banned = $wpdb->get_var($wpdb->prepare(
                "SELECT banned FROM $table_name WHERE uuid = %s AND gridname = %s",
                $uuid,
                $gridname
            ));
            if ($is_banned) {
                wp_redirect(add_query_arg('opensim_message', 'banned_remove_error', admin_url('admin.php?page=opensim-auth')));
            } else {
                $wpdb->delete(
                    $table_name,
                    ['uuid' => $uuid, 'gridname' => $gridname],
                    ['%s', '%s']
                );
                wp_redirect(add_query_arg('opensim_message', 'removed', admin_url('admin.php?page=opensim-auth')));
            }
            exit;

        case 'toggle_ban':
            if (empty($uuid) || empty($gridname)) {
                wp_redirect(add_query_arg('opensim_message', 'empty_fields', admin_url('admin.php?page=opensim-auth')));
                exit;
            }
            $current_banned_status = $wpdb->get_var($wpdb->prepare(
                "SELECT banned FROM $table_name WHERE uuid = %s AND gridname = %s",
                $uuid,
                $gridname
            ));

            // Only 'marty' can unban users
            if ($current_banned_status && !$can_unban_user) {
                wp_redirect(add_query_arg('opensim_message', 'permission_unban_error', admin_url('admin.php?page=opensim-auth')));
            } else {
                $new_status = $current_banned_status ? 0 : 1; // Toggle status
                $wpdb->update(
                    $table_name,
                    ['banned' => $new_status],
                    ['uuid' => $uuid, 'gridname' => $gridname],
                    ['%d'],
                    ['%s', '%s']
                );
                wp_redirect(add_query_arg('opensim_message', 'toggled_ban', admin_url('admin.php?page=opensim-auth')));
            }
            exit;

        case 'update_comment':
            if (empty($uuid) || empty($gridname)) {
                wp_redirect(add_query_arg('opensim_message', 'empty_fields', admin_url('admin.php?page=opensim-auth')));
                exit;
            }
            $new_comment = sanitize_text_field($_POST['new_comment'] ?? '');
            $wpdb->update(
                $table_name,
                ['comment' => $new_comment],
                ['uuid' => $uuid, 'gridname' => $gridname],
                ['%s'],
                ['%s', '%s']
            );
            wp_redirect(add_query_arg('opensim_message', 'comment_updated', admin_url('admin.php?page=opensim-auth')));
            exit;

        case 'export_csv':
            opensim_auth_generate_csv();
            break; // `exit` is handled inside opensim_auth_generate_csv()

        case 'import_csv':
            opensim_auth_import_csv();
            break; // `exit` is handled inside opensim_auth_import_csv()

        case 'add_partner':
            if ($can_unban_user) {
                $gridname_manage = sanitize_text_field($_POST['gridname_manage'] ?? '');
                if (!empty($gridname_manage)) {
                    $wpdb->insert($partner_table, ['gridname' => $gridname_manage], ['%s']);
                    wp_redirect(add_query_arg('opensim_message', 'partner_added', admin_url('admin.php?page=opensim-auth')));
                }
            }
            exit;

        case 'remove_partner':
            if ($can_unban_user) {
                $gridname_manage = sanitize_text_field($_POST['gridname_manage'] ?? '');
                if (!empty($gridname_manage)) {
                    $wpdb->delete($partner_table, ['gridname' => $gridname_manage], ['%s']);
                    wp_redirect(add_query_arg('opensim_message', 'partner_removed', admin_url('admin.php?page=opensim-auth')));
                }
            }
            exit;

        case 'add_banned_grid': // Changed from 'add_banned' to avoid conflict
            if ($can_unban_user) {
                $gridname_manage = sanitize_text_field($_POST['gridname_manage'] ?? '');
                if (!empty($gridname_manage)) {
                    $wpdb->insert($banned_table, ['gridname' => $gridname_manage], ['%s']);
                    wp_redirect(add_query_arg('opensim_message', 'banned_grid_added', admin_url('admin.php?page=opensim-auth')));
                }
            }
            exit;

        case 'remove_banned_grid': // Changed from 'remove_banned' to avoid conflict
            if ($can_unban_user) {
                $gridname_manage = sanitize_text_field($_POST['gridname_manage'] ?? '');
                if (!empty($gridname_manage)) {
                    $wpdb->delete($banned_table, ['gridname' => $gridname_manage], ['%s']);
                    wp_redirect(add_query_arg('opensim_message', 'banned_grid_removed', admin_url('admin.php?page=opensim-auth')));
                }
            }
            exit;

        default:
            // Fallback for unknown action_type, redirect back to the page
            wp_redirect(admin_url('admin.php?page=opensim-auth'));
            exit;
    }
}
add_action('admin_post_opensim_auth_action', 'opensim_auth_handle_post_actions');

/**
 * Generates and sends the CSV file for download.
 */
function opensim_auth_generate_csv() {
    global $wpdb;
    $table_name = "wp_opensim_auth";
    $partner_table = "partners";
    $banned_table = "banned_grids";

    // Set headers for CSV download
    header('Content-Type: text/csv; charset=utf-8');
    header('Content-Disposition: attachment; filename="opensim_auth_users_' . date('Y-m-d') . '.csv"');
    header('Pragma: no-cache');
    header('Expires: 0');

    // Open output stream
    $output = fopen('php://output', 'w');
    if (false === $output) {
        wp_die('Error: Could not open output stream for CSV export.');
    }

    // Add UTF-8 Byte Order Mark (BOM) for better compatibility with Excel
    fwrite($output, "\xEF\xBB\xBF");

    // Define CSV header row (column names)
    fputcsv($output, ['UUID', 'Gridname', 'Comment', 'Banned']);

    // Fetch all user data from the database
    // Select specific columns to prevent issues if table structure changes
    $users = $wpdb->get_results("SELECT uuid, gridname, comment, banned FROM $table_name", ARRAY_A);

    // Loop through data and add to CSV
    if ($users) {
        foreach ($users as $user) {
            fputcsv($output, [
                $user['uuid'],
                html_entity_decode($user['gridname'], ENT_QUOTES | ENT_HTML5, 'UTF-8'), // Decode HTML entities
                html_entity_decode($user['comment'], ENT_QUOTES | ENT_HTML5, 'UTF-8'),
                $user['banned'] ? 'BANNED' : 'ACTIVE'
            ]);
        }
    }

    // Close the output stream
    fclose($output);

    // Terminate script execution to prevent WordPress from adding extra output
    exit();
}

/**
 * Handles the CSV import process.
 */
function opensim_auth_import_csv() {
    global $wpdb;
     $table_name = "wp_opensim_auth";

    if (empty($_FILES['import_file']['tmp_name'])) {
        wp_redirect(add_query_arg('opensim_message', 'no_file', admin_url('admin.php?page=opensim-auth')));
        exit;
    }

    // Basic file type validation using wp_check_filetype
    $file_info = wp_check_filetype(basename($_FILES['import_file']['name']));
    if ($file_info['ext'] !== 'csv') {
        wp_redirect(add_query_arg('opensim_message', 'invalid_file_type', admin_url('admin.php?page=opensim-auth')));
        exit;
    }

    $file_path = $_FILES['import_file']['tmp_name'];
    $file = fopen($file_path, 'r');
    if (false === $file) {
        wp_redirect(add_query_arg('opensim_message', 'file_open_error', admin_url('admin.php?page=opensim-auth')));
        exit;
    }

    // Read and validate header
    $header = fgetcsv($file);
    $expectedHeaders = ['UUID', 'Gridname', 'Comment', 'Banned'];
    if ($header !== $expectedHeaders) {
        fclose($file);
        wp_redirect(add_query_arg('opensim_message', 'header_mismatch', admin_url('admin.php?page=opensim-auth')));
        exit;
    }

    $imported_count = 0;
    while (($row = fgetcsv($file)) !== false) {
        // Skip empty rows or rows with insufficient columns
        if (count($row) < 4 || implode('', $row) === '') {
            continue;
        }

        // Sanitize each value from the CSV row
        $uuid = sanitize_text_field($row[0] ?? '');
        $gridname = sanitize_text_field($row[1] ?? '');
        $comment = sanitize_text_field($row[2] ?? '');
        $banned = sanitize_text_field($row[3] ?? '');

        // Validate UUID and Gridname (essential fields)
        if (empty($uuid) || empty($gridname)) {
            // Optionally log skipped rows due to invalid data
            continue;
        }

        $banned_val = (strtolower($banned) === 'banned' || $banned == '1') ? 1 : 0;

        // Check if the record already exists
        $exists = $wpdb->get_var($wpdb->prepare(
            "SELECT COUNT(*) FROM $table_name WHERE uuid = %s AND gridname = %s",
            $uuid,
            $gridname
        ));

        if ($exists) {
            // Update existing record
            $updated = $wpdb->update(
                $table_name,
                [
                    'comment' => $comment,
                    'banned' => $banned_val
                ],
                ['uuid' => $uuid, 'gridname' => $gridname],
                ['%s', '%d'],
                ['%s', '%s']
            );
            if ($updated !== false) { // Check if update was successful (0 means no change, but not an error)
                $imported_count++;
            }
        } else {
            // Insert new record
            $inserted = $wpdb->insert(
                $table_name,
                [
                    'uuid' => $uuid,
                    'gridname' => $gridname,
                    'comment' => $comment,
                    'banned' => $banned_val
                ],
                ['%s', '%s', '%s', '%d']
            );
            if ($inserted) { // Check if insert was successful
                $imported_count++;
            }
        }
    }
    fclose($file);

    wp_redirect(add_query_arg('opensim_message', 'imported', admin_url('admin.php?page=opensim-auth&imported_count=' . $imported_count)));
    exit;
}

/**
 * Renders the admin page content for OpenSim Authorization.
 */
function opensim_auth_admin_page() {
    global $wpdb;
     $table_name = "wp_opensim_auth";
     $partner_table = "partners";
     $banned_table = "banned_grids";
    $current_user = wp_get_current_user();
    $can_unban_user = ($current_user->user_login === OPENSIM_AUTH_UNBAN_USERNAME);

    // Display admin notices
    if (isset($_GET['opensim_message'])) {
        $message_type = 'updated'; // Default success
        $message_text = '';

        switch ($_GET['opensim_message']) {
            case 'added':
                $message_text = 'User added successfully.';
                break;
            case 'exists':
                $message_type = 'error';
                $message_text = 'Error: UUID with this gridname already exists!';
                break;
            case 'removed':
                $message_text = 'User removed successfully.';
                break;
            case 'banned_remove_error':
                $message_type = 'error';
                $message_text = 'You cannot remove a banned user who is currently banned.';
                break;
            case 'permission_unban_error':
                $message_type = 'error';
                $message_text = 'You do not have permission to unban users. Only ' . OPENSIM_AUTH_UNBAN_USERNAME . ' can unban.';
                break;
            case 'toggled_ban':
                $message_text = 'User ban status updated.';
                break;
            case 'comment_updated':
                $message_text = 'Comment updated successfully.';
                break;
            case 'no_file':
                $message_type = 'error';
                $message_text = 'No file uploaded for import. Please select a CSV file.';
                break;
            case 'invalid_file_type':
                $message_type = 'error';
                $message_text = 'Invalid file type. Please upload a CSV file.';
                break;
            case 'file_open_error':
                $message_type = 'error';
                $message_text = 'Error: Could not open the uploaded file.';
                break;
            case 'header_mismatch':
                $message_type = 'error';
                $message_text = 'CSV header mismatch. Expected: ' . esc_html(implode(', ', ['UUID', 'Gridname', 'Comment', 'Banned'])) . '.';
                break;
            case 'imported':
                $imported_count = intval($_GET['imported_count'] ?? 0);
                $message_text = "Imported {$imported_count} records successfully.";
                break;
            case 'empty_fields':
                $message_type = 'error';
                $message_text = 'Error: All required fields must be filled.';
                break;
            case 'partner_added':
                $message_text = 'Partner grid added successfully.';
                break;
            case 'partner_removed':
                $message_text = 'Partner grid removed successfully.';
                break;
            case 'banned_grid_added':
                $message_text = 'Banned grid added successfully.';
                break;
            case 'banned_grid_removed':
                $message_text = 'Banned grid removed successfully.';
                break;
            default:
                // Do nothing for unknown messages
                break;
        }

        if ($message_text) {
            echo '<div class="' . esc_attr($message_type) . ' notice is-dismissible"><p>' . esc_html($message_text) . '</p></div>';
        }
    }

    // Filters
    $grid_filter = sanitize_text_field($_GET['filter_grid'] ?? '');
    $status_filter = sanitize_text_field($_GET['filter_status'] ?? '');
    ?>

    <div class="wrap">
        <h1>OpenSim Authorization Admin</h1>

        <form method="GET" style="margin-bottom:20px;">
            <input type="hidden" name="page" value="opensim-auth">
            <label for="filter_grid">Gridname:</label>
            <input type="text" name="filter_grid" id="filter_grid" value="<?php echo esc_attr($grid_filter); ?>">
            <label for="filter_status">Status:</label>
            <select name="filter_status" id="filter_status">
                <option value="">Any</option>
                <option value="active" <?php selected($status_filter, 'active'); ?>>Active</option>
                <option value="banned" <?php selected($status_filter, 'banned'); ?>>Banned</option>
            </select>
            <button type="submit" class="button">Filter</button>
        </form>

        <form method="POST" action="<?php echo esc_url(admin_url('admin-post.php')); ?>" enctype="multipart/form-data" style="margin-bottom:20px;">
            <?php wp_nonce_field('opensim_auth_nonce_action', 'opensim_auth_nonce'); ?>
            <input type="hidden" name="action" value="opensim_auth_action">
            <input type="hidden" name="action_type" value="import_csv">
            <label for="import_file">Import CSV:</label>
            <input type="file" name="import_file" id="import_file" accept=".csv" style="display:inline-block; margin-right:10px;">
            <button type="submit" class="button button-secondary">Import CSV</button>
        </form>

        <form method="POST" action="<?php echo esc_url(admin_url('admin-post.php')); ?>" style="margin-bottom:20px;">
            <?php wp_nonce_field('opensim_auth_nonce_action', 'opensim_auth_nonce'); ?>
            <input type="hidden" name="action" value="opensim_auth_action">
            <input type="hidden" name="action_type" value="export_csv">
            <button type="submit" class="button button-secondary">Export CSV</button>
        </form>

        <h2>Add New User</h2>
        <form method="POST" action="<?php echo esc_url(admin_url('admin-post.php')); ?>">
            <?php wp_nonce_field('opensim_auth_nonce_action', 'opensim_auth_nonce'); ?>
            <input type="hidden" name="action" value="opensim_auth_action">
            <input type="hidden" name="action_type" value="add_uuid">
            <table class="form-table">
                <tr>
				
                    <th scope="row"><label for="uuid">UUID:</label></th>
					
                    <td><input type="text" name="uuid" id="uuid" class="regular-text" required placeholder="e.g., xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"></td>
					<td><p style="color:red">Remember be sure what you doing its your responsibility if you add someone so please think twice!</p></td>
                </tr>
                <tr>
                    <th scope="row"><label for="gridname">Gridname:</label></th>
                    <td><input type="text" name="gridname" id="gridname" class="regular-text" required placeholder="e.g., MyGrid.com"></td>
					<td> <p style="color:red">Please dont add people from banned grids if you are not 1000% sure they are safe!</p></td>
                </tr>
                <tr>
                    <th scope="row"><label for="comment">Comment:</label></th>
                    <td><input type="text" name="comment" id="comment" class="regular-text" placeholder="comment"></td>
					<td><p style="color:green">This is likely name of person</p></td>
                </tr>
            </table>
            <?php submit_button('Add User'); ?>
        </form>

        <h2>Authorized Users</h2>
        <?php
        // Build SQL WHERE clause for filtering
        $where = "1=1";
        if ($grid_filter) {
            $where .= $wpdb->prepare(" AND gridname LIKE %s", '%' . $wpdb->esc_like($grid_filter) . '%');
        }
        if ($status_filter === 'active') {
            $where .= " AND banned = 0";
        } elseif ($status_filter === 'banned') {
            $where .= " AND banned = 1";
        }

        // Fetch users based on filters
        $users = $wpdb->get_results("SELECT * FROM $table_name WHERE $where ORDER BY gridname ASC, uuid ASC");

        if (empty($users)) {
            echo '<p>No users found matching your criteria.</p>';
        } else {
            ?>
            <table class="wp-list-table widefat fixed striped">
                <thead>
                    <tr>
                        <th>UUID</th>
                        <th>Gridname</th>
                        <th>Comment</th>
                        <th>Status</th>
                        <th>Actions</th>
                    </tr>
                </thead>
                <tbody>
                    <?php
                    foreach ($users as $user) {
                        $uuid = esc_html($user->uuid);
                        $gridname = esc_html($user->gridname);
                        $comment = esc_html($user->comment);
                        $status = $user->banned ? "<strong style='color:red;'>BANNED</strong>" : "ACTIVE";
                        $action_label = $user->banned ? "Unban" : "Ban";
                        ?>
                        <tr>
                            <td><?php echo $uuid; ?></td>
                            <td><?php echo $gridname; ?></td>
                            <td>
                                <form method="POST" action="<?php echo esc_url(admin_url('admin-post.php')); ?>" style="display:flex;gap:5px;">
                                    <?php wp_nonce_field('opensim_auth_nonce_action', 'opensim_auth_nonce', true, false); ?>
                                    <input type="hidden" name="action" value="opensim_auth_action">
                                    <input type="hidden" name="action_type" value="update_comment">
                                    <input type="hidden" name="uuid" value="<?php echo esc_attr($user->uuid); ?>">
                                    <input type="hidden" name="gridname" value="<?php echo esc_attr($user->gridname); ?>">
                                    <input type="text" name="new_comment" value="<?php echo esc_attr($user->comment); ?>" style="width:150px;">
                                    <button type="submit" class="button-secondary">💾</button>
                                </form>
                            </td>
                            <td><?php echo $status; ?></td>
                            <td>
                                <?php if (!$user->banned) : ?>
                                    <form method="POST" action="<?php echo esc_url(admin_url('admin-post.php')); ?>" style="display:inline-block; margin-right: 5px;">
                                        <?php wp_nonce_field('opensim_auth_nonce_action', 'opensim_auth_nonce', true, false); ?>
                                        <input type="hidden" name="action" value="opensim_auth_action">
                                        <input type="hidden" name="action_type" value="remove_uuid">
                                        <input type="hidden" name="uuid" value="<?php echo esc_attr($user->uuid); ?>">
                                        <input type="hidden" name="gridname" value="<?php echo esc_attr($user->gridname); ?>">
                                        <button type="submit" class="button" onclick="return confirm('Are you sure you want to remove this user?');">Remove</button>
                                    </form>
                                <?php endif; ?>

                                <form method="POST" action="<?php echo esc_url(admin_url('admin-post.php')); ?>" style="display:inline-block;">
                                    <?php wp_nonce_field('opensim_auth_nonce_action', 'opensim_auth_nonce', true, false); ?>
                                    <input type="hidden" name="action" value="opensim_auth_action">
                                    <input type="hidden" name="action_type" value="toggle_ban">
                                    <input type="hidden" name="uuid" value="<?php echo esc_attr($user->uuid); ?>">
                                    <input type="hidden" name="gridname" value="<?php echo esc_attr($user->gridname); ?>">
                                    <button type="submit" class="button <?php echo $user->banned ? 'button-primary' : ''; ?>" <?php echo ($user->banned && !$can_unban_user) ? 'disabled title="Only ' . OPENSIM_AUTH_UNBAN_USERNAME . ' can unban"' : ''; ?>>
                                        <?php echo esc_html($action_label); ?>
                                    </button>
                                </form>
                            </td>
                        </tr>
                        <?php
                    }
                    ?>
                </tbody>
            </table>
            <?php
        } // End if (empty($users))
        ?>

        <?php if ($can_unban_user) : // Only 'marty' can manage partner and banned grids ?>
            <hr>
            <h2>Manage Partner and Banned Grids </h2>
            <form method="POST" action="<?php echo esc_url(admin_url('admin-post.php')); ?>" style="display:inline-block; margin-right:20px;">
                <?php wp_nonce_field('opensim_auth_nonce_action', 'opensim_auth_nonce'); ?>
                <input type="hidden" name="action" value="opensim_auth_action">
                <input type="hidden" name="action_type" value="add_partner">
                <input type="text" name="gridname_manage" placeholder="Gridname" required>
                <button type="submit" class="button button-secondary">Add Partner Grid</button>
            </form>

            <form method="POST" action="<?php echo esc_url(admin_url('admin-post.php')); ?>" style="display:inline-block; margin-right:20px;">
                <?php wp_nonce_field('opensim_auth_nonce_action', 'opensim_auth_nonce'); ?>
                <input type="hidden" name="action" value="opensim_auth_action">
                <input type="hidden" name="action_type" value="add_banned_grid">
                <input type="text" name="gridname_manage" placeholder="Gridname" required>
                <button type="submit" class="button button-secondary">Add Banned Grid</button>
            </form>
<?php endif; ?>
<?php if (!$can_unban_user) : // Only 'marty' can manage partner and banned grids ?>
<hr>
You have no permition to change banned or partner grid
<hr>
<?php endif; ?>
            <h4>Partner Grids</h4>
            <ul>
                <?php
                $partners = $wpdb->get_results("SELECT * FROM $partner_table");
                if ($partners) {
                    foreach ($partners as $p) {
                        echo '<li>' . esc_html($p->gridname) . ' ';
                        echo '<form method="POST" action="' . esc_url(admin_url('admin-post.php')) . '" style="display:inline;">';
                        wp_nonce_field('opensim_auth_nonce_action', 'opensim_auth_nonce', true, false);
                        echo '<input type="hidden" name="action" value="opensim_auth_action">';
                        echo '<input type="hidden" name="action_type" value="remove_partner">';
                        echo '<input type="hidden" name="gridname_manage" value="' . esc_attr($p->gridname) . '">';
					if ($can_unban_user) {echo '<button type="submit" class="button-link" onclick="return confirm(\'Are you sure you want to remove this partner grid?\');">❌</button>';}
					echo '</form></li>';

                    }
                } else {
                    echo '<li>No partner grids defined.</li>';
                }
                ?>
            </ul>

            <h4>Banned Grids</h4>
            <ul>
                <?php
                $banned = $wpdb->get_results("SELECT * FROM $banned_table");
                if ($banned) {
                    foreach ($banned as $b) {
                        echo '<li>' . esc_html($b->gridname) . ' ';
                        echo '<form method="POST" action="' . esc_url(admin_url('admin-post.php')) . '" style="display:inline;">';
                        wp_nonce_field('opensim_auth_nonce_action', 'opensim_auth_nonce', true, false);
                        echo '<input type="hidden" name="action" value="opensim_auth_action">';
                        echo '<input type="hidden" name="action_type" value="remove_banned_grid">';
                        echo '<input type="hidden" name="gridname_manage" value="' . esc_attr($b->gridname) . '">';
					if ($can_unban_user) { echo '<button type="submit" class="button-link" onclick="return confirm(\'Are you sure you want to remove this banned grid?\');">❌</button>';}
					    echo '</form></li>';

                    }
                } else {
                    echo '<li>No banned grids defined.</li>';
                }
                ?>
            </ul>
        
    </div>
<?php
}

/**
 * Creates the database table on plugin activation.
 */
function opensim_auth_install() {
    global $wpdb;
    $table_name = "wp_opensim_auth";
    $partner_table = "partners";
    $banned_table = "banned_grids";
    $charset_collate = $wpdb->get_charset_collate();

    require_once(ABSPATH . 'wp-admin/includes/upgrade.php');

    // Create main authorization table
    $sql_auth = "CREATE TABLE $table_name (
        id INT AUTO_INCREMENT PRIMARY KEY,
        uuid VARCHAR(36) NOT NULL,
        gridname VARCHAR(255) NOT NULL,
        comment TEXT DEFAULT '',
        banned TINYINT(1) DEFAULT 0,
        UNIQUE KEY unique_user (uuid, gridname)
    ) $charset_collate;";
    dbDelta($sql_auth);

    // Create partners table
    $sql_partners = "CREATE TABLE $partner_table (
        id INT AUTO_INCREMENT PRIMARY KEY,
        gridname VARCHAR(255) NOT NULL UNIQUE KEY
    ) $charset_collate;";
    dbDelta($sql_partners);

    // Create banned grids table
    $sql_banned = "CREATE TABLE $banned_table (
        id INT AUTO_INCREMENT PRIMARY KEY,
        gridname VARCHAR(255) NOT NULL UNIQUE KEY
    ) $charset_collate;";
    dbDelta($sql_banned);
}
register_activation_hook(__FILE__, 'opensim_auth_install');

// Optional: Add a deactivation hook to clean up (e.g., delete table) if desired
/*
function opensim_auth_deactivate() {
    global $wpdb;
     $table_name = "opensim_auth";
    $partner_table = $wpdb->prefix . "opensim_partners";
    $banned_table = $wpdb->prefix . "opensim_banned_grids";
    $wpdb->query("DROP TABLE IF EXISTS $table_name");
    $wpdb->query("DROP TABLE IF EXISTS $partner_table");
    $wpdb->query("DROP TABLE IF EXISTS $banned_table");
}
register_deactivation_hook(__FILE__, 'opensim_auth_deactivate');
*/