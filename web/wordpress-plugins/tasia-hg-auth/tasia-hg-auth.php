<?php
/**
 * Plugin Name: Tasia HG Auth
 * Description: HyperGrid authentication service with WordPress configuration
 * Version: 1.0.0
 * Author: Tasia AI
 */

if (!defined('ABSPATH')) exit;

class Tasia_HG_Auth {
    public function __construct() {
        add_action('admin_menu', [$this, 'add_admin_menu']);
        add_action('admin_init', [$this, 'register_settings']);
        add_filter('query_vars', [$this, 'add_query_vars']);
        add_action('init', [$this, 'add_rewrite_rules']);
        add_action('parse_request', [$this, 'handle_hg_request']);
    }
    
    public function add_rewrite_rules() {
        add_rewrite_rule('^hg/auth/?$', 'index.php?hg_auth=1', 'top');
    }
    
    public function add_query_vars($vars) {
        $vars[] = 'hg_auth';
        return $vars;
    }
    
    public function handle_hg_request($wp) {
        if (!empty($wp->query_vars['hg_auth'])) {
            $this->serve_hg_auth();
            exit;
        }
    }
    
    private function serve_hg_auth() {
        header("Content-Type: application/xml");
        
        $wp_db = DB_NAME;
        $robust_db = get_option('tasia_hg_robust_db', 'robust');
        
        $host = defined('DB_HOST') ? DB_HOST : 'localhost';
        $user = defined('DB_USER') ? DB_USER : 'root';
        $pass = defined('DB_PASSWORD') ? DB_PASSWORD : '';
        
        $wp_conn = new mysqli($host, $user, $pass, $wp_db);
        $robust_conn = new mysqli($host, $user, $pass, $robust_db);
        
        if ($wp_conn->connect_error || $robust_conn->connect_error) {
            die('<?xml version="1.0" encoding="utf-8"?>
                <AuthorizationResponse>
                <IsAuthorized>false</IsAuthorized>
                <Message><![CDATA[Database connection failed]]></Message>
                </AuthorizationResponse>');
        }
        
        // Get request data
        $xml = file_get_contents('php://input');
        $request = simplexml_load_string($xml);
        
        if (!$request) {
            die('<?xml version="1.0" encoding="utf-8"?>
                <AuthorizationResponse>
                <IsAuthorized>false</IsAuthorized>
                <Message><![CDATA[Invalid request]]></Message>
                </AuthorizationResponse>');
        }
        
        // Process authentication logic here
        // For now, output basic auth response
        
        echo '<?xml version="1.0" encoding="utf-8"?>
            <AuthorizationResponse>
            <IsAuthorized>true</IsAuthorized>
            </AuthorizationResponse>';
    }
    
    public function add_admin_menu() {
        add_submenu_page(
            'options-general.php',
            'Tasia HG Auth',
            'Tasia HG Auth',
            'manage_options',
            'tasia-hg-auth',
            [$this, 'admin_page']
        );
    }
    
    public function register_settings() {
        register_setting('tasia_hg_auth', 'tasia_hg_robust_db');
        register_setting('tasia_hg_auth', 'tasia_hg_disable_gridname_check');
        register_setting('tasia_hg_auth', 'tasia_hg_maintenance_mode');
        register_setting('tasia_hg_auth', 'tasia_hg_maintenance_message');
    }
    
    public function admin_page() {
        ?>
        <div class="wrap">
            <h1>Tasia HG Auth Settings</h1>
            <form method="post" action="options.php">
                <?php settings_fields('tasia_hg_auth'); ?>
                <?php do_settings_sections('tasia_hg_auth'); ?>
                
                <h2>Database Settings</h2>
                <table class="form-table">
                    <tr>
                        <th>Robust Database Name</th>
                        <td><input type="text" name="tasia_hg_robust_db" value="<?php echo esc_attr(get_option('tasia_hg_robust_db', 'robust')); ?>" class="regular-text"></td>
                    </tr>
                </table>
                
                <h2>HG Settings</h2>
                <table class="form-table">
                    <tr>
                        <th>Disable Grid Name Check</th>
                        <td><input type="checkbox" name="tasia_hg_disable_gridname_check" value="1" <?php checked(get_option('tasia_hg_disable_gridname_check'), 1); ?>></td>
                    </tr>
                    <tr>
                        <th>Maintenance Mode</th>
                        <td><input type="checkbox" name="tasia_hg_maintenance_mode" value="1" <?php checked(get_option('tasia_hg_maintenance_mode'), 1); ?>></td>
                    </tr>
                    <tr>
                        <th>Maintenance Message</th>
                        <td><textarea name="tasia_hg_maintenance_message" class="large-text"><?php echo esc_textarea(get_option('tasia_hg_maintenance_message', 'Grid is under maintenance')); ?></textarea></td>
                    </tr>
                </table>
                
                <?php submit_button(); ?>
            </form>
            
            <h2>API Endpoint</h2>
            <p>Use this URL for HG authentication:</p>
            <code><?php echo get_site_url(); ?>/hg/auth/</code>
        </div>
        <?php
    }
}

new Tasia_HG_Auth();
