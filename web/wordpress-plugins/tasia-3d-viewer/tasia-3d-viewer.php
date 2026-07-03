<?php
/**
 * Plugin Name: Tasia 3D Viewer
 * Description: 3D model viewer with PBR support using Babylon.js - embed virtual world content in pages
 * Version: 1.0.0
 * Author: Tasia AI
 */

if (!defined('ABSPATH')) exit;

class Tasia_3D_Viewer {
    public function __construct() {
        add_shortcode('3d_viewer', [$this, 'shortcode']);
        add_action('wp_enqueue_scripts', [$this, 'enqueue_scripts']);
    }
    
    public function enqueue_scripts() {
        wp_enqueue_script(
            'babylon-viewer',
            'https://cdn.jsdelivr.net/npm/@babylonjs/viewer/dist/babylon-viewer.esm.min.js',
            [],
            '1.0.0',
            ['type' => 'module']
        );
    }
    
    public function shortcode($atts) {
        $atts = shortcode_atts([
            'model' => '',
            'height' => '500px',
            'width' => '100%',
            'autoplay' => 'true',
            'camera' => 'true',
            'orientation' => 'true',
            'loading' => 'true',
            'schema' => 'minimal',
        ], $atts);
        
        $model_url = $atts['model'];
        if (!$model_url) {
            return '<div class="error">Please specify a 3D model URL using model="" attribute</div>';
        }
        
        $unique_id = 'viewer_' . uniqid();
        
        return "<div id=\"$unique_id\" style=\"width: {$atts['width']}; height: {$atts['height']};\">
            <babylon-viewer 
                src=\"" . esc_url($model_url) . "\"
                autoplay=\"{$atts['autoplay']}\"
                camera=\"{$atts['camera']}\"
                orientation=\"{$atts['orientation']}\"
                loading=\"{$atts['loading']}\"
                schema=\"{$atts['schema']}\"
            ></babylon-viewer>
        </div>";
    }
}

new Tasia_3D_Viewer();

add_action('admin_menu', function() {
    add_options_page('3D Viewer', '3D Viewer', 'manage_options', 'tasia-3d-viewer', function() {
        ?>
        <div class="wrap">
            <h1>Tasia 3D Viewer</h1>
            <p>Embed 3D models with PBR support using Babylon.js</p>
            
            <h2>Usage</h2>
            <p>Use the shortcode in any page or post:</p>
            <pre style="background: #f4f4f4; padding: 15px; border-radius: 5px;">
[3d_viewer model="https://example.com/model.glb"]
            </pre>
            
            <h3>Attributes</h3>
            <ul>
                <li><strong>model</strong> - URL to GLB/GLTF model (required)</li>
                <li><strong>height</strong> - Viewer height (default: 500px)</li>
                <li><strong>width</strong> - Viewer width (default: 100%)</li>
                <li><strong>autoplay</strong> - Auto-play animations (true/false)</li>
                <li><strong>camera</strong> - Enable camera controls (true/false)</li>
                <li><strong>orientation</strong> - Allow orientation changes (true/false)</li>
                <li><strong>loading</strong> - Show loading indicator (true/false)</li>
                <li><strong>schema</strong> - UI schema: minimal, full, or none</li>
            </ul>
            
            <h3>Example with all options</h3>
            <pre style="background: #f4f4f4; padding: 15px; border-radius: 5px;">
[3d_viewer model="https://example.com/model.glb" height="600px" autoplay="true" camera="true" schema="minimal"]
            </pre>
            
            <h3>Model Hosting</h3>
            <p>Upload .glb or .gltf files to your WordPress media library, or host on GitHub/any CDN.</p>
            <p>PBR (Physically Based Rendering) models with textures are fully supported!</p>
        </div>
        <?php
    });
});
