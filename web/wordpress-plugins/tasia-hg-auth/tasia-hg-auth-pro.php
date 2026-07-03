<?php
/**
 * Plugin Name: Tasia HG Auth Pro
 * Description: Full-featured HyperGrid auth with user management, bans, and maintenance mode
 * Version: 1.0.0
 * Author: Tasia AI
 */

if (!defined('ABSPATH')) exit;

class Tasia_HG_Auth_Pro {
    private $db_host, $db_user, $db_pass;
    private $wp_db, $robust_db;
    
    public function __construct() {
        global $wpdb;
        $this->db_host = defined('DB_HOST') ? DB_HOST : 'localhost';
        $this->db_user = defined('DB_USER') ? DB_USER : 'root';
        $this->db_pass = defined('DB_PASSWORD') ? DB_PASSWORD : '';
        $this->wp_db = $wpdb->prefix . 'oslogin_auth';
        
        add_action('admin_menu', [$this, 'add_admin_menu']);
        add_action('admin_init', [$this, 'register_settings']);
        add_action('wp_ajax_tasia_hg_auth', [$this, 'handle_ajax']);
        
        // Create tables on activation
        register_activation_hook(__FILE__, [$this, 'install']);
    }
    
    public function install() {
        global $wpdb;
        $charset = $wpdb->get_charset_collate();
        
        // Main auth table
        $wpdb->query("CREATE TABLE IF NOT EXISTS {$wpdb->prefix}oslogin_auth (
            id INT AUTO_INCREMENT PRIMARY KEY,
            uuid VARCHAR(36) UNIQUE,
            first_name VARCHAR(50),
            last_name VARCHAR(50),
            email VARCHAR(100),
            password_hash VARCHAR(255),
            banned TINYINT DEFAULT 0,
            ban_reason TEXT,
            ban_date DATETIME,
            last_login DATETIME,
            created_at DATETIME DEFAULT CURRENT_TIMESTAMP
        ) $charset;");
        
        // Maintenance table
        $wpdb->query("CREATE TABLE IF NOT EXISTS {$wpdb->prefix}opensim_maintenance (
            id INT AUTO_INCREMENT PRIMARY KEY,
            maintenance_mode TINYINT DEFAULT 0,
            maintenance_message TEXT,
            allowed_uuids TEXT
        ) $charset;");
        
        // Insert default maintenance row
        $wpdb->query("INSERT IGNORE INTO {$wpdb->prefix}opensim_maintenance (id) VALUES (1)");
        
        // Auth traffic log
        $wpdb->query("CREATE TABLE IF NOT EXISTS {$wpdb->prefix}opensim_auth_traffic (
            id INT AUTO_INCREMENT PRIMARY KEY,
            uuid VARCHAR(36),
            status VARCHAR(20),
            gridname VARCHAR(100),
            ip_address VARCHAR(45),
            created_at DATETIME DEFAULT CURRENT_TIMESTAMP
        ) $charset;");
    }
    
    public function register_settings() {
        register_setting('tasia_hg_auth_pro', 'tasia_hg_robust_db');
        register_setting('tasia_hg_auth_pro', 'tasia_hg_disable_gridname');
        register_setting('tasia_hg_auth_pro', 'tasia_hg_allow_registration');
        register_setting('tasia_hg_auth_pro', 'tasia_hg_default_ban');
    }
    
    public function add_admin_menu() {
        add_menu_page(
            'HG Auth Pro',
            'HG Auth Pro',
            'manage_options',
            'tasia-hg-auth-pro',
            [$this, 'admin_page'],
            'dashicons-shield',
            80
        );
    }
    
    public function admin_page() {
        global $wpdb;
        
        // Handle actions
        if (isset($_POST['action']) && check_admin_referer('tasia_hg_action')) {
            $this->handle_action($_POST['action']);
        }
        
        // Get data
        $maintenance = $wpdb->get_row("SELECT * FROM {$wpdb->prefix}opensim_maintenance WHERE id = 1");
        $users = $wpdb->get_results("SELECT * FROM {$wpdb->prefix}oslogin_auth ORDER BY last_login DESC LIMIT 100");
        $recent_traffic = $wpdb->get_results("SELECT * FROM {$wpdb->prefix}opensim_auth_traffic ORDER BY created_at DESC LIMIT 50");
        
        $banned_count = $wpdb->get_var("SELECT COUNT(*) FROM {$wpdb->prefix}oslogin_auth WHERE banned = 1");
        $active_count = $wpdb->get_var("SELECT COUNT(*) FROM {$wpdb->prefix}oslogin_auth WHERE banned = 0");
        ?>
        <div class="wrap">
            <h1>Tasia HG Auth Pro</h1>
            
            <!-- Stats -->
            <div style="display:flex;gap:20px;margin:20px 0;">
                <div class="card" style="padding:20px;background:#1a1a1a;border-radius:8px;">
                    <h3>Total Users</h3>
                    <p style="font-size:24px;font-weight:bold;"><?php echo $active_count + $banned_count; ?></p>
                </div>
                <div class="card" style="padding:20px;background:#1a1a1a;border-radius:8px;">
                    <h3>Active Users</h3>
                    <p style="font-size:24px;font-weight:bold;color:green;"><?php echo $active_count; ?></p>
                </div>
                <div class="card" style="padding:20px;background:#1a1a1a;border-radius:8px;">
                    <h3>Banned Users</h3>
                    <p style="font-size:24px;font-weight:bold;color:red;"><?php echo $banned_count; ?></p>
                </div>
            </div>
            
            <!-- Maintenance Mode -->
            <div class="card" style="margin:20px 0;padding:20px;background:#1a1a1a;border-radius:8px;">
                <h2>Maintenance Mode</h2>
                <form method="post">
                    <?php wp_nonce_field('tasia_hg_action'); ?>
                    <input type="hidden" name="action" value="update_maintenance">
                    <table class="form-table">
                        <tr>
                            <th>Enable Maintenance</th>
                            <td><input type="checkbox" name="maintenance_mode" value="1" <?php checked($maintenance->maintenance_mode, 1); ?>></td>
                        </tr>
                        <tr>
                            <th>Message</th>
                            <td><textarea name="maintenance_message" rows="3"><?php echo esc_textarea($maintenance->maintenance_message ?? ''); ?></textarea></td>
                        </tr>
                        <tr>
                            <th>Allowed UUIDs (comma-separated)</th>
                            <td><input type="text" name="allowed_uuids" value="<?php echo esc_attr($maintenance->allowed_uuids ?? ''); ?>" class="regular-text"></td>
                        </tr>
                    </table>
                    <?php submit_button('Save Maintenance Settings'); ?>
                </form>
            </div>
            
            <!-- Settings -->
            <div class="card" style="margin:20px 0;padding:20px;background:#1a1a1a;border-radius:8px;">
                <h2>Settings</h2>
                <form method="post" action="options.php">
                    <?php settings_fields('tasia_hg_auth_pro'); ?>
                    <?php do_settings_sections('tasia_hg_auth_pro'); ?>
                    <table class="form-table">
                        <tr>
                            <th>Robust Database Name</th>
                            <td><input type="text" name="tasia_hg_robust_db" value="<?php echo esc_attr(get_option('tasia_hg_robust_db', 'robust')); ?>" class="regular-text"></td>
                        </tr>
                        <tr>
                            <th>Disable Grid Name Check</th>
                            <td><input type="checkbox" name="tasia_hg_disable_gridname" value="1" <?php checked(get_option('tasia_hg_disable_gridname'), 1); ?>></td>
                        </tr>
                        <tr>
                            <th>Allow User Registration</th>
                            <td><input type="checkbox" name="tasia_hg_allow_registration" value="1" <?php checked(get_option('tasia_hg_allow_registration'), 1); ?>></td>
                        </tr>
                    </table>
                    <?php submit_button(); ?>
                </form>
            </div>
            
            <!-- User Management -->
            <div class="card" style="margin:20px 0;padding:20px;background:#1a1a1a;border-radius:8px;">
                <h2>User Management</h2>
                <table class="widefat">
                    <thead>
                        <tr>
                            <th>Name</th>
                            <th>UUID</th>
                            <th>Status</th>
                            <th>Last Login</th>
                            <th>Actions</th>
                        </tr>
                    </thead>
                    <tbody>
                        <?php foreach ($users as $user): ?>
                        <tr class="<?php echo $user->banned ? 'banned' : ''; ?>">
                            <td><?php echo esc_html($user->first_name . ' ' . $user->last_name); ?></td>
                            <td><code><?php echo esc_html($user->uuid); ?></code></td>
                            <td>
                                <?php if ($user->banned): ?>
                                <span style="color:red;font-weight:bold;">BANNED</span>
                                <?php else: ?>
                                <span style="color:green;">Active</span>
                                <?php endif; ?>
                            </td>
                            <td><?php echo $user->last_login ? esc_html($user->last_login) : 'Never'; ?></td>
                            <td>
                                <?php if ($user->banned): ?>
                                <form method="post" style="display:inline;">
                                    <?php wp_nonce_field('tasia_hg_action'); ?>
                                    <input type="hidden" name="action" value="unban_user">
                                    <input type="hidden" name="uuid" value="<?php echo esc_attr($user->uuid); ?>">
                                    <button type="submit" class="button button-primary">Unban</button>
                                </form>
                                <?php else: ?>
                                <form method="post" style="display:inline;">
                                    <?php wp_nonce_field('tasia_hg_action'); ?>
                                    <input type="hidden" name="action" value="ban_user">
                                    <input type="hidden" name="uuid" value="<?php echo esc_attr($user->uuid); ?>">
                                    <input type="text" name="ban_reason" placeholder="Reason" style="width:150px;">
                                    <button type="submit" class="button button-secondary" onclick="return confirm('Ban this user?');">Ban</button>
                                </form>
                                <?php endif; ?>
                            </td>
                        </tr>
                        <?php endforeach; ?>
                    </tbody>
                </table>
            </div>
            
            <!-- Recent Traffic -->
            <div class="card" style="margin:20px 0;padding:20px;background:#1a1a1a;border-radius:8px;">
                <h2>Recent Auth Traffic</h2>
                <table class="widefat">
                    <thead>
                        <tr>
                            <th>Time</th>
                            <th>UUID</th>
                            <th>Grid</th>
                            <th>IP</th>
                            <th>Status</th>
                        </tr>
                    </thead>
                    <tbody>
                        <?php foreach ($recent_traffic as $log): ?>
                        <tr>
                            <td><?php echo esc_html($log->created_at); ?></td>
                            <td><code><?php echo esc_html($log->uuid); ?></code></td>
                            <td><?php echo esc_html($log->gridname); ?></td>
                            <td><?php echo esc_html($log->ip_address); ?></td>
                            <td>
                                <?php if ($log->status === 'success'): ?>
                                <span style="color:green;">✓</span>
                                <?php else: ?>
                                <span style="color:red;">✗</span>
                                <?php endif; ?>
                            </td>
                        </tr>
                        <?php endforeach; ?>
                    </tbody>
                </table>
            </div>
            
            <!-- API Endpoint -->
            <div class="card" style="margin:20px 0;padding:20px;background:#1a1a1a;border-radius:8px;">
                <h2>API Endpoint</h2>
                <p>Use this URL for HG authentication:</p>
                <code><?php echo get_site_url(); ?>/hg/auth/</code>
            </div>
        </div>
        
        <style>
            .banned { background: #331111; }
            .card { background: #1a1a1a; padding: 20px; margin: 20px 0; border-radius: 8px; }
        </style>
        <?php
    }
    
    private function handle_action($action) {
        global $wpdb;
        
        switch ($action) {
            case 'update_maintenance':
                $wpdb->update(
                    $wpdb->prefix . 'opensim_maintenance',
                    [
                        'maintenance_mode' => isset($_POST['maintenance_mode']) ? 1 : 0,
                        'maintenance_message' => $_POST['maintenance_message'] ?? '',
                        'allowed_uuids' => $_POST['allowed_uuids'] ?? ''
                    ],
                    ['id' => 1]
                );
                break;
                
            case 'ban_user':
                $uuid = sanitize_text_field($_POST['uuid']);
                $reason = sanitize_text_field($_POST['ban_reason']);
                $wpdb->update(
                    $wpdb->prefix . 'oslogin_auth',
                    [
                        'banned' => 1,
                        'ban_reason' => $reason,
                        'ban_date' => current_time('mysql')
                    ],
                    ['uuid' => $uuid]
                );
                break;
                
            case 'unban_user':
                $uuid = sanitize_text_field($_POST['uuid']);
                $wpdb->update(
                    $wpdb->prefix . 'oslogin_auth',
                    [
                        'banned' => 0,
                        'ban_reason' => null,
                        'ban_date' => null
                    ],
                    ['uuid' => $uuid]
                );
                break;
        }
        
        wp_redirect($_SERVER['REQUEST_URI']);
        exit;
    }
    
    // HG Auth handler (for rewrite rule)
    public function handle_auth_request() {
        header("Content-Type: application/xml");
        
        $robust_db = get_option('tasia_hg_robust_db', 'robust');
        
        $conn = new mysqli($this->db_host, $this->db_user, $this->db_pass, DB_NAME);
        $robust_conn = new mysqli($this->db_host, $this->db_user, $this->db_pass, $robust_db);
        
        if ($conn->connect_error || $robust_conn->connect_error) {
            die('<?xml version="1.0" encoding="utf-8"?>
                <AuthorizationResponse>
                <IsAuthorized>false</IsAuthorized>
                <Message><![CDATA[Database connection failed]]></Message>
                </AuthorizationResponse>');
        }
        
        $xml = file_get_contents('php://input');
        $request = simplexml_load_string($xml);
        
        if (!$request) {
            die('<?xml version="1.0" encoding="utf-8"?>
                <AuthorizationResponse>
                <IsAuthorized>false</IsAuthorized>
                <Message><![CDATA[Invalid request]]></Message>
                </AuthorizationResponse>');
        }
        
        // Check maintenance mode
        $maintenance = $conn->query("SELECT * FROM {$conn->prefix}opensim_maintenance WHERE id = 1")->fetch_assoc();
        
        $uuid = (string)$request->firstname; // Usually uuid
        
        if ($maintenance['maintenance_mode'] == 1) {
            $allowed = explode(',', $maintenance['allowed_uuids']);
            $allowed = array_map('trim', $allowed);
            if (!in_array($uuid, $allowed)) {
                die('<?xml version="1.0" encoding="utf-8"?>
                    <AuthorizationResponse>
                    <IsAuthorized>false</IsAuthorized>
                    <Message><![CDATA[' . ($maintenance['maintenance_message'] ?? 'Grid under maintenance') . ']]></Message>
                    </AuthorizationResponse>');
            }
        }
        
        // Check user ban
        $user = $conn->query("SELECT * FROM {$conn->prefix}oslogin_auth WHERE uuid = '$uuid'")->fetch_assoc();
        
        if ($user && $user['banned']) {
            $this->log_traffic($conn, $uuid, 'banned', '');
            die('<?xml version="1.0" encoding="utf-8"?>
                <AuthorizationResponse>
                <IsAuthorized>false</IsAuthorized>
                <Message><![CDATA[You have been banned: ' . ($user['ban_reason'] ?? 'No reason') . ']]></Message>
                </AuthorizationResponse>');
        }
        
        // Auth logic here...
        
        echo '<?xml version="1.0" encoding="utf-8"?>
            <AuthorizationResponse>
            <IsAuthorized>true</IsAuthorized>
            </AuthorizationResponse>';
    }
    
    private function log_traffic($conn, $uuid, $status, $gridname) {
        $ip = $_SERVER['REMOTE_ADDR'] ?? 'unknown';
        $stmt = $conn->prepare("INSERT INTO {$conn->prefix}opensim_auth_traffic (uuid, status, gridname, ip_address) VALUES (?, ?, ?, ?)");
        $stmt->bind_param("ssss", $uuid, $status, $gridname, $ip);
        $stmt->execute();
        $stmt->close();
    }
}

new Tasia_HG_Auth_Pro();
