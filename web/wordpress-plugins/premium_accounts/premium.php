<?php
/*
Plugin Name: Premium Member Payout
Description: Manage premium users and pay them weekly from the balances table.
Version: 1.5
Author: Tasia
*/

if (!defined('ABSPATH')) exit;

// Create premium table on activation
register_activation_hook(__FILE__, function() {
    global $wpdb;

    $premium_table = $wpdb->prefix . 'premium';
    $charset_collate = $wpdb->get_charset_collate();

    // Fully qualified table creation
    $sql = "CREATE TABLE IF NOT EXISTS `money`.`$premium_table` (
        user VARCHAR(36) PRIMARY KEY,
        added_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
    ) $charset_collate;";

    require_once(ABSPATH . 'wp-admin/includes/upgrade.php');
    dbDelta($sql);
});

// Add admin menu
add_action('admin_menu', function() {
    add_menu_page(
        'Premium Payout',
        'Premium Payout',
        'manage_options',
        'premium-payout',
        'premium_payout_admin_page',
        'dashicons-money-alt',
        25
    );
});

// Admin page
function premium_payout_admin_page() {
    global $wpdb;

    $premium_table = $wpdb->prefix . 'premium';
    $full_premium_table = "`money`.`$premium_table`"; // fully qualified
    $balances_table = "`money`.balances";
    $users_table = "`robust`.UserAccounts";

    // Handle toggle premium
    if (isset($_POST['toggle_premium']) && isset($_POST['user'])) {
        $user = sanitize_text_field($_POST['user']);

        $is_premium = $wpdb->get_var($wpdb->prepare(
            "SELECT COUNT(*) FROM $full_premium_table WHERE user = %s",
            $user
        ));

        if ($is_premium) {
            $wpdb->query($wpdb->prepare(
                "DELETE FROM $full_premium_table WHERE user = %s",
                $user
            ));
        } else {
            $wpdb->query($wpdb->prepare(
                "INSERT INTO $full_premium_table (user) VALUES (%s)",
                $user
            ));
        }
    }

    // Handle payout
    if (isset($_POST['payout'])) {
        $amount = intval($_POST['amount']);
        $premium_list = $wpdb->get_col("SELECT user FROM $full_premium_table");
        if (!empty($premium_list)) {
            $in_placeholders = implode(',', array_fill(0, count($premium_list), '%s'));
            $query = $wpdb->prepare(
                "UPDATE $balances_table SET balance = balance + %d WHERE user IN ($in_placeholders) AND balance IS NOT NULL",
                array_merge([$amount], $premium_list)
            );
            $wpdb->query($query);
            echo '<div class="updated"><p>Payout sent to premium members.</p></div>';
        }
    }

    // Fetch users that exist in robust.UserAccounts
    $users = $wpdb->get_results("
        SELECT b.user, b.balance, u.FirstName, u.LastName
        FROM $balances_table b
        INNER JOIN $users_table u ON b.user = u.PrincipalID
    ");

    echo '<div class="wrap"><h1>Premium Member Payout</h1>';
    
    // Payout form
    echo '<form method="post" style="margin-bottom:20px;">';
    echo 'Amount to payout: <input type="number" name="amount" value="100"> ';
    echo '<button type="submit" name="payout" value="1" class="button-primary">Send Payout</button>';
    echo '</form>';

    // Users table
    echo '<table class="widefat"><thead><tr><th>User</th><th>Name</th><th>Balance</th><th>Premium?</th><th>Action</th></tr></thead><tbody>';

    foreach ($users as $user) {
        $is_premium = $wpdb->get_var($wpdb->prepare(
            "SELECT COUNT(*) FROM $full_premium_table WHERE user = %s",
            $user->user
        ));
        $full_name = trim($user->FirstName . ' ' . $user->LastName);

        echo '<tr>';
        echo '<td>' . esc_html($user->user) . '</td>';
        echo '<td>' . esc_html($full_name) . '</td>';
        echo '<td>' . esc_html($user->balance) . '</td>';
        echo '<td>' . ($is_premium ? '✅' : '❌') . '</td>';
        
        // Each row has its own form for toggle
        echo '<td>
                <form method="post" style="margin:0;">
                    <input type="hidden" name="user" value="' . esc_attr($user->user) . '">
                    <button type="submit" name="toggle_premium" value="1" class="button">Toggle Premium</button>
                </form>
              </td>';
        echo '</tr>';
    }

    echo '</tbody></table>';
    echo '</div>';
}