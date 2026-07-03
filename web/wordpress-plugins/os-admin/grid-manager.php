<?php
/**
 * Plugin Name: I-Grid Manager
 * Plugin URI: http://yoursite.com/plugin
 * Description: I-Grid OpenSim Grid Manager for WordPress
 * Version: 1.0
 * Author: Marty
 * License: MIT
 */

defined('ABSPATH') or die('Direct access not allowed');

class OpenSimGridManager {
    private static $instance = null;
    private $opensim_db = null;
    private $money_db = null;
	private $robust_db = null;
    private $last_error = '';

    public static function get_instance() {
        if (self::$instance === null) {
            self::$instance = new self();
        }
        return self::$instance;
    }

    private function __construct() {
        // Plugin activation/deactivation
        register_activation_hook(__FILE__, array($this, 'activate'));
        register_deactivation_hook(__FILE__, array($this, 'deactivate'));

        // Admin hooks
        add_action('admin_menu', array($this, 'add_admin_menu'));
        add_action('admin_init', array($this, 'register_settings'));
        add_action('admin_enqueue_scripts', array($this, 'admin_scripts'));

        // AJAX handlers
        add_action('wp_ajax_update_balance', array($this, 'ajax_update_balance'));
        add_action('wp_ajax_change_password', array($this, 'ajax_change_password'));
        add_action('wp_ajax_get_user_info', array($this, 'ajax_get_user_info'));

        // Shortcodes
        add_shortcode('opensim_balance', array($this, 'balance_shortcode'));
        add_shortcode('opensim_transactions', array($this, 'transactions_shortcode'));

        // Initialize database connections
        $this->init_db_connections();
    }

    public function activate() {
        // Create necessary tables
        global $wpdb;
        require_once(ABSPATH . 'wp-admin/includes/upgrade.php');

        $charset_collate = $wpdb->get_charset_collate();
        
        $sql = "CREATE TABLE IF NOT EXISTS {$wpdb->prefix}opensim_user_links (
            wp_user_id bigint(20) NOT NULL,
            opensim_uuid varchar(36) NOT NULL,
            created_at datetime DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY  (wp_user_id),
            UNIQUE KEY opensim_uuid (opensim_uuid)
        ) $charset_collate;";
        
        dbDelta($sql);
    }

    public function deactivate() {
        // Cleanup if needed
    }

    private function init_db_connections() {
        try {
            $opensim_settings = get_option('opensim_db_settings');
            if (!empty($opensim_settings)) {
                $this->opensim_db = new wpdb(
                    $opensim_settings['user'],
                    $opensim_settings['password'],
                    $opensim_settings['database'],
                    $opensim_settings['host']
                );
            }

            $robust_settings = get_option('robust_db_settings');
            if (!empty($opensim_settings)) {
                $this->robust_db = new wpdb(
                    $robust_settings['user'],
                    $robust_settings['password'],
                    $robust_settings['database'],
                    $robust_settings['host']
                );
            }
            $money_settings = get_option('opensim_money_settings');
            if (!empty($money_settings)) {
                $this->money_db = new wpdb(
                    $money_settings['user'],
                    $money_settings['password'],
                    $money_settings['database'],
                    $money_settings['host']
                );
            }
        } catch (Exception $e) {
            $this->last_error = $e->getMessage();
            error_log('OpenSim Plugin Error: ' . $e->getMessage());
        }
    }

    public function add_admin_menu() {
        add_menu_page(
            'OpenSim Grid Manager',
            'Grid Statistics',
            'manage_options',
            'opensim-manager',
            array($this, 'render_admin_page'),
            'dashicons-grid-view'
        );

        add_submenu_page(
            'opensim-manager',
            'Users',
            'User Management',
            'manage_options',
            'opensim-users',
            array($this, 'render_users_page')
        );

        add_submenu_page(
            'opensim-manager',
            'Settings',
            'Settings',
            'manage_options',
            'opensim-settings',
            array($this, 'render_settings_page')
        );
    }

    public function register_settings() {
        register_setting('opensim_settings', 'opensim_db_settings');
        register_setting('opensim_settings', 'opensim_money_settings');
		register_setting('opensim_settings', 'robust_db_settings');
    }

    public function admin_scripts($hook) {
        if (strpos($hook, 'opensim') === false) {
            return;
        }

        wp_enqueue_style(
            'opensim-admin-style',
            plugins_url('assets/css/admin.css', __FILE__),
            array(),
            '1.0'
        );

        wp_enqueue_script(
            'opensim-admin-script',
            plugins_url('assets/js/admin.js', __FILE__),
            array('jquery'),
            '1.0',
            true
        );

        wp_localize_script('opensim-admin-script', 'opensimAjax', array(
            'nonce' => wp_create_nonce('opensim_ajax_nonce'),
            'ajaxurl' => admin_url('admin-ajax.php')
        ));
    }
    //password
	    private function change_password($uuid, $password) {
        if (!$this->robust_db) return false;

        $salt = md5(uniqid(rand(), true));
        $hash = md5(md5($password) . ":" . $salt);

        return $this->robust_db->update(
            'Auth',
            array(
                'PasswordHash' => $hash,
                'PasswordSalt' => $salt
            ),
            array('PrincipalID' => $uuid),
            array('%s', '%s'),
            array('%s')
        );
    }


    // Page Renders
    public function render_admin_page() {
        if (!current_user_can('manage_options')) {
            wp_die('Unauthorized access');
        }
        include plugin_dir_path(__FILE__) . 'templates/admin-main.php';
    }

    public function render_users_page() {
        if (!current_user_can('manage_options')) {
            wp_die('Unauthorized access');
        }
        include plugin_dir_path(__FILE__) . 'templates/admin-users.php';
    }

    public function render_settings_page() {
        if (!current_user_can('manage_options')) {
            wp_die('Unauthorized access');
        }
        include plugin_dir_path(__FILE__) . 'templates/admin-settings.php';
    }

    // User Management Methods
    public function get_user_balance($uuid) {
        if (!$this->money_db) return 0;
        
        return (float)$this->money_db->get_var(
            $this->money_db->prepare(
                "SELECT balance FROM balances WHERE user = %s",
                $uuid
            )
        );
    }

    public function update_balance($uuid, $amount) {
        if (!$this->money_db) return false;
        
        return $this->money_db->update(
            'balances',
            array('balance' => $amount),
            array('user' => $uuid),
            array('%f'),
            array('%s')
        );
    }

    public function get_transactions($uuid, $limit = 300) {
        if (!$this->money_db) return array();
        
        return $this->money_db->get_results(
            $this->money_db->prepare(
                "SELECT * FROM transactions 
            WHERE sender = %s OR receiver = %s 
            ORDER BY time DESC 
            LIMIT %d",
                $uuid, $uuid, $limit
            )
        );
    }

    // Statistics Methods
    public function get_total_users() {
        if (!$this->opensim_db) return 0;
        return (int)$this->opensim_db->get_var("SELECT COUNT(*) FROM UserAccounts");
    }

    public function get_online_users() {
        if (!$this->opensim_db) return 0;
        $timeout = time() - (15 * 60);
        return (int)$this->opensim_db->get_var(
            $this->opensim_db->prepare(
                "SELECT COUNT(*) FROM Presence WHERE LastSeen > %d",
                $timeout
            )
        );
    }

    public function get_total_regions() {
        if (!$this->opensim_db) return 0;
        return (int)$this->opensim_db->get_var("SELECT COUNT(*) FROM regions");
    }

    // AJAX Handlers
    public function ajax_update_balance() {
        check_ajax_referer('opensim_ajax_nonce', 'nonce');
        
        if (!current_user_can('manage_options')) {
            wp_send_json_error('Unauthorized');
        }

        $uuid = sanitize_text_field($_POST['uuid']);
        $amount = floatval($_POST['amount']);

        if ($this->update_balance($uuid, $amount)) {
            wp_send_json_success();
        } else {
            wp_send_json_error('Failed to update balance');
        }
    }

    public function ajax_change_password() {
        check_ajax_referer('opensim_ajax_nonce', 'nonce');
        
        if (!current_user_can('manage_options')) {
            wp_send_json_error('Unauthorized');
        }

        $uuid = sanitize_text_field($_POST['uuid']);
        $password = sanitize_text_field($_POST['password']);

        if ($this->change_password($uuid, $password)) {
            wp_send_json_success();
        } else {
            wp_send_json_error('Failed to change password');
        }
    }

    // Shortcode Handlers
    public function balance_shortcode($atts) {
        if (!is_user_logged_in()) {
            return 'Please log in to view your balance.';
        }

        $user_id = get_current_user_id();
        $uuid = get_user_meta($user_id, 'w4os_uuid', true);

        if (!$uuid) {
            return 'No OpenSim account linked.';
        }

        $balance = $this->get_user_balance($uuid);
        ob_start();
        include plugin_dir_path(__FILE__) . 'templates/balance.php';
        return ob_get_clean();
    }

    public function transactions_shortcode($atts) {
        if (!is_user_logged_in()) {
            return 'Please log in to view your transactions.';
        }

        $user_id = get_current_user_id();
        $uuid = get_user_meta($user_id, 'w4os_uuid', true);

        if (!$uuid) {
            return 'No OpenSim account linked.';
        }

        $transactions = $this->get_transactions($uuid);
        ob_start();
        include plugin_dir_path(__FILE__) . 'templates/transactions.php';
        return ob_get_clean();
    }
}

// Initialize the plugin
function init_opensim_grid_manager() {
    return OpenSimGridManager::get_instance();
}

add_action('plugins_loaded', 'init_opensim_grid_manager');