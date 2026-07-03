<?php
/**
 * Plugin Name: Tasia Web Viewer
 * Description: Embedded web viewer for TasiaNGC grid - connect to virtual world directly from browser
 * Version: 1.0.0
 * Author: Tasia AI
 */

if (!defined('ABSPATH')) exit;

class Tasia_Web_Viewer {
    private $viewer_path;
    
    public function __construct() {
        $this->viewer_path = plugin_dir_path(__FILE__) . 'viewer';
        
        add_action('init', [$this, 'add_rewrites']);
        add_filter('query_vars', [$this, 'add_query_vars']);
        add_action('parse_request', [$this, 'handle_viewer_request']);
        add_shortcode('web_viewer', [$this, 'shortcode']);
        
        register_activation_hook(__FILE__, [$this, 'activate']);
    }
    
    public function activate() {
        $this->add_rewrites();
        flush_rewrite_rules();
    }
    
    public function add_rewrites() {
        add_rewrite_rule('^viewer/?$', 'index.php?web_viewer=1', 'top');
        add_rewrite_rule('^viewer/(.*)$', 'index.php?web_viewer_file=$1', 'top');
    }
    
    public function add_query_vars($vars) {
        $vars[] = 'web_viewer';
        $vars[] = 'web_viewer_file';
        return $vars;
    }
    
    public function handle_viewer_request($wp) {
        if (!empty($wp->query_vars['web_viewer']) || !empty($wp->query_vars['web_viewer_file'])) {
            $this->serve_viewer($wp);
            exit;
        }
    }
    
    private function serve_viewer($wp) {
        $grid_url = get_option('tasia_grid_url', 'http://localhost:9000');
        $grid_name = get_option('tasia_grid_name', 'TasiaNGC');
        
        if (!empty($wp->query_vars['web_viewer_file'])) {
            $file = $wp->query_vars['web_viewer_file'];
            $viewer_dir = $this->viewer_path;
            
            if (is_dir($viewer_dir)) {
                $file_path = $viewer_dir . '/' . $file;
                if (file_exists($file_path) && !is_dir($file_path)) {
                    $ext = pathinfo($file, PATHINFO_EXTENSION);
                    $mime_types = [
                        'html' => 'text/html',
                        'js' => 'application/javascript',
                        'css' => 'text/css',
                        'json' => 'application/json',
                        'png' => 'image/png',
                        'jpg' => 'image/jpeg',
                        'jpeg' => 'image/jpeg',
                        'gif' => 'image/gif',
                        'svg' => 'image/svg+xml',
                        'woff' => 'font/woff',
                        'woff2' => 'font/woff2',
                        'ttf' => 'font/ttf',
                    ];
                    $mime = $mime_types[$ext] ?? 'application/octet-stream';
                    header('Content-Type: ' . $mime);
                    header('Cache-Control: public, max-age=31536000');
                    readfile($file_path);
                    exit;
                }
            }
        }
        
        header('Content-Type: text/html');
        echo '<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Welcome to ' . esc_html($grid_name) . '</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { 
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
            background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%);
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
        }
        .container { text-align: center; color: white; padding: 40px; }
        h1 { font-size: 3rem; margin-bottom: 20px; }
        p { font-size: 1.2rem; opacity: 0.8; margin-bottom: 30px; }
        .btn {
            display: inline-block;
            padding: 15px 40px;
            background: #e94560;
            color: white;
            text-decoration: none;
            border-radius: 30px;
            font-size: 1.1rem;
            font-weight: bold;
            transition: transform 0.2s, box-shadow 0.2s;
        }
        .btn:hover {
            transform: translateY(-2px);
            box-shadow: 0 10px 30px rgba(233, 69, 96, 0.4);
        }
        .info {
            margin-top: 40px;
            padding: 20px;
            background: rgba(255,255,255,0.1);
            border-radius: 10px;
            max-width: 500px;
        }
        .info p { font-size: 0.9rem; margin-bottom: 10px; }
        .grid-url { 
            background: rgba(0,0,0,0.3); 
            padding: 10px 20px; 
            border-radius: 5px;
            font-family: monospace;
        }
    </style>
</head>
<body>
    <div class="container">
        <h1>' . esc_html($grid_name) . '</h1>
        <p>Enter the virtual world directly from your browser</p>
        
        <div class="info">
            <p><strong>Grid URL:</strong></p>
            <div class="grid-url">' . esc_html($grid_url) . '</div>
            <p style="margin-top: 15px;"><em>Web viewer loading...</em></p>
        </div>
    </div>
</body>
</html>';
        exit;
    }
    
    public function shortcode($atts) {
        $atts = shortcode_atts([
            'height' => '600px',
            'width' => '100%'
        ], $atts);
        
        $viewer_url = get_option('tasia_viewer_url', home_url('/viewer'));
        
        return '<iframe src="' . esc_url($viewer_url) . '" 
                width="' . esc_attr($atts['width']) . '" 
                height="' . esc_attr($atts['height']) . '" 
                style="border: none; border-radius: 10px;"></iframe>';
    }
    
    public static function settings_init() {
        register_setting('tasia_viewer', 'tasia_grid_url');
        register_setting('tasia_viewer', 'tasia_grid_name');
        register_setting('tasia_viewer', 'tasia_viewer_url');
        
        add_settings_section('tasia_viewer_main', 'Viewer Settings', null, 'tasia-viewer');
        
        add_settings_field('tasia_grid_url', 'Grid URL', function() {
            echo '<input type="url" name="tasia_grid_url" value="' . esc_attr(get_option('tasia_grid_url', 'http://localhost:9000')) . '" class="regular-text">';
            echo '<p class="description">Your OpenSim grid URL (e.g., http://grid.yourdomain.com:9000)</p>';
        }, 'tasia-viewer', 'tasia_viewer_main');
        
        add_settings_field('tasia_grid_name', 'Grid Name', function() {
            echo '<input type="text" name="tasia_grid_name" value="' . esc_attr(get_option('tasia_grid_name', 'TasiaNGC')) . '" class="regular-text">';
        }, 'tasia-viewer', 'tasia_viewer_main');
        
        add_settings_field('tasia_viewer_url', 'Viewer URL', function() {
            echo '<input type="url" name="tasia_viewer_url" value="' . esc_attr(get_option('tasia_viewer_url', home_url('/viewer'))) . '" class="regular-text" readonly>';
        }, 'tasia-viewer', 'tasia_viewer_main');
    }
}

new Tasia_Web_Viewer();

add_action('admin_init', ['Tasia_Web_Viewer', 'settings_init']);

add_action('admin_menu', function() {
    add_options_page('Web Viewer', 'Web Viewer', 'manage_options', 'tasia-viewer', function() {
        ?>
        <div class="wrap">
            <h1>Tasia Web Viewer Settings</h1>
            <form method="post" action="options.php">
                <?php settings_fields('tasia_viewer'); ?>
                <?php do_settings_sections('tasia-viewer'); ?>
                <?php submit_button(); ?>
            </form>
            
            <hr>
            <h2>Setup Instructions</h2>
            <ol>
                <li>Set your Grid URL (where your OpenSim simulator is running)</li>
                <li>The viewer URL is automatically generated at: <strong><?php echo esc_html(home_url('/viewer')); ?></strong></li>
                <li>Place built Andromeda Viewer files in: <code>wp-content/plugins/tasia-web-viewer/viewer/</code></li>
                <li>Use shortcode <code>[web_viewer height="600px"]</code> to embed in pages</li>
            </ol>
            <p><strong>To build Andromeda Viewer:</strong></p>
            <pre>git clone https://github.com/Terreii/andromeda-viewer.git
cd andromeda-viewer
npm install
npm run build
# Copy the 'build' folder contents to viewer/</pre>
        </div>
        <?php
    });
});
