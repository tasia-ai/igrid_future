<?php
/**
 * Plugin Name: Tasia Voice Settings
 * Description: Configure voice chat settings from WordPress
 * Version: 1.0.0
 * Author: Tasia AI
 */

if (!defined('ABSPATH')) exit;

class Tasia_Voice_Settings {
    public function __construct() {
        add_action('admin_menu', [$this, 'add_admin_menu']);
        add_action('admin_init', [$this, 'register_settings']);
    }
    
    public function add_admin_menu() {
        add_options_page(
            'Tasia Voice Settings',
            'Voice Chat',
            'manage_options',
            'tasia-voice',
            [$this, 'admin_page']
        );
    }
    
    public function register_settings() {
        register_setting('tasia_voice', 'tasia_voice_enabled');
        register_setting('tasia_voice', 'tasia_voice_provider');
        register_setting('tasia_voice', 'tasia_voice_vivox_server');
        register_setting('tasia_voice', 'tasia_voice_vivox_sip');
        register_setting('tasia_voice', 'tasia_voice_vivox_user');
        register_setting('tasia_voice', 'tasia_voice_vivox_pass');
        register_setting('tasia_voice', 'tasia_voice_freeswitch_host');
        register_setting('tasia_voice', 'tasia_voice_freeswitch_port');
        register_setting('tasia_voice', 'tasia_voice_freeswitch_password');
    }
    
    public function admin_page() {
        ?>
        <div class="wrap">
            <h1>Tasia Voice Chat Settings</h1>
            <form method="post" action="options.php">
                <?php settings_fields('tasia_voice'); ?>
                <?php do_settings_sections('tasia_voice'); ?>
                
                <h2>General</h2>
                <table class="form-table">
                    <tr>
                        <th>Enable Voice</th>
                        <td><input type="checkbox" name="tasia_voice_enabled" value="1" <?php checked(get_option('tasia_voice_enabled'), 1); ?>></td>
                    </tr>
                    <tr>
                        <th>Voice Provider</th>
                        <td>
                            <select name="tasia_voice_provider">
                                <option value="vivox" <?php selected(get_option('tasia_voice_provider'), 'vivox'); ?>>Vivox (Recommended)</option>
                                <option value="freeswitch" <?php selected(get_option('tasia_voice_provider'), 'freeswitch'); ?>>FreeSWITCH</option>
                                <option value="none" <?php selected(get_option('tasia_voice_provider'), 'none'); ?>>Disabled</option>
                            </select>
                        </td>
                    </tr>
                </table>
                
                <h2>Vivox Settings</h2>
                <p>Get your Vivox credentials from <a href="https://www.vivox.com/" target="_blank">vivox.com</a></p>
                <table class="form-table">
                    <tr>
                        <th>Vivox Server</th>
                        <td><input type="text" name="tasia_voice_vivox_server" value="<?php echo esc_attr(get_option('tasia_voice_vivox_server')); ?>" class="regular-text" placeholder="www.example.vivox.com"></td>
                    </tr>
                    <tr>
                        <th>SIP URI</th>
                        <td><input type="text" name="tasia_voice_vivox_sip" value="<?php echo esc_attr(get_option('tasia_voice_vivox_sip')); ?>" class="regular-text" placeholder="example.vivox.com"></td>
                    </tr>
                    <tr>
                        <th>Admin Username</th>
                        <td><input type="text" name="tasia_voice_vivox_user" value="<?php echo esc_attr(get_option('tasia_voice_vivox_user')); ?>" class="regular-text"></td>
                    </tr>
                    <tr>
                        <th>Admin Password</th>
                        <td><input type="password" name="tasia_voice_vivox_pass" value="<?php echo esc_attr(get_option('tasia_voice_vivox_pass')); ?>" class="regular-text"></td>
                    </tr>
                </table>
                
                <h2>FreeSWITCH Settings</h2>
                <table class="form-table">
                    <tr>
                        <th>Host</th>
                        <td><input type="text" name="tasia_voice_freeswitch_host" value="<?php echo esc_attr(get_option('tasia_voice_freeswitch_host', '127.0.0.1')); ?>" class="regular-text"></td>
                    </tr>
                    <tr>
                        <th>Port</th>
                        <td><input type="number" name="tasia_voice_freeswitch_port" value="<?php echo esc_attr(get_option('tasia_voice_freeswitch_port', '8021')); ?>" class="small-text"></td>
                    </tr>
                    <tr>
                        <th>Password</th>
                        <td><input type="password" name="tasia_voice_freeswitch_password" value="<?php echo esc_attr(get_option('tasia_voice_freeswitch_password')); ?>" class="regular-text"></td>
                    </tr>
                </table>
                
                <?php submit_button(); ?>
            </form>
            
            <h2>Export Configuration</h2>
            <p>Copy this to your OpenSim.ini:</p>
            <textarea class="large-text" rows="10" readonly><?php echo $this->generate_opensim_ini(); ?></textarea>
        </div>
        <?php
    }
    
    public function generate_opensim_ini() {
        $enabled = get_option('tasia_voice_enabled');
        $provider = get_option('tasia_voice_provider');
        
        if (!$enabled || $provider == 'none') {
            return "; Voice is disabled";
        }
        
        if ($provider == 'vivox') {
            return <<<INI
[VivoxVoice]
    enabled = true
    vivox_server = {$this->esc(get_option('tasia_voice_vivox_server'))}
    vivox_sip_uri = {$this->esc(get_option('tasia_voice_vivox_sip'))}
    vivox_admin_user = {$this->esc(get_option('tasia_voice_vivox_user'))}
    vivox_admin_password = {$this->esc(get_option('tasia_voice_vivox_pass'))}
    vivox_channel_type = positional
INI;
        }
        
        if ($provider == 'freeswitch') {
            return <<<INI
[FreeSwitchVoice]
    enabled = true
    freeswitch_server = {$this->esc(get_option('tasia_voice_freeswitch_host', '127.0.0.1'))}
    freeswitch_port = {$this->esc(get_option('tasia_voice_freeswitch_port', '8021'))}
    freeswitch_password = {$this->esc(get_option('tasia_voice_freeswitch_password'))}
    freeswitch_realm = {$_SERVER['HTTP_HOST']}
INI;
        }
        
        return "; Configure voice settings";
    }
    
    private function esc($val) {
        return $val ?: '';
    }
    
    public function get_config() {
        return [
            'enabled' => get_option('tasia_voice_enabled'),
            'provider' => get_option('tasia_voice_provider', 'vivox'),
            'vivox' => [
                'server' => get_option('tasia_voice_vivox_server'),
                'sip' => get_option('tasia_voice_vivox_sip'),
                'user' => get_option('tasia_voice_vivox_user'),
                'pass' => get_option('tasia_voice_vivox_pass'),
            ],
            'freeswitch' => [
                'host' => get_option('tasia_voice_freeswitch_host'),
                'port' => get_option('tasia_voice_freeswitch_port'),
                'password' => get_option('tasia_voice_freeswitch_password'),
            ]
        ];
    }
}

new Tasia_Voice_Settings();
