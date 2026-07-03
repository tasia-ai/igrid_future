<?php
/**
 * Plugin Name: Tasia GitHub Manager
 * Description: View and edit GitHub repositories, create gists, and share with password protection
 * Version: 1.1.0
 * Author: Tasia AI
 */

if (!defined('ABSPATH')) exit;

class Tasia_GitHub_Manager {
    private $token;
    
    public function __construct() {
        register_activation_hook(__FILE__, [$this, 'install']);
        add_action('admin_menu', [$this, 'add_admin_menu']);
        add_action('admin_init', [$this, 'register_settings']);
        add_action('wp_ajax_github_request', [$this, 'handle_ajax']);
        add_action('init', [$this, 'add_rewrites']);
        add_filter('query_vars', [$this, 'add_query_vars']);
        add_action('parse_request', [$this, 'handle_share_request']);
    }
    
    public function install() {
        global $wpdb;
        
        $table = $wpdb->prefix . 'tasia_shares';
        
        $wpdb->query("DROP TABLE IF EXISTS $table");
        
        $sql = "CREATE TABLE $table (
            id BIGINT(20) NOT NULL AUTO_INCREMENT,
            share_id VARCHAR(64) NOT NULL,
            filename VARCHAR(255) NOT NULL,
            content LONGTEXT NOT NULL,
            content_type VARCHAR(100) DEFAULT 'text/plain',
            password_hash VARCHAR(255) DEFAULT NULL,
            expires DATETIME DEFAULT NULL,
            created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (id),
            KEY share_id (share_id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";
        
        require_once(ABSPATH . 'wp-admin/includes/upgrade.php');
        dbDelta($sql);
    }
    
    public function add_rewrites() {
        add_rewrite_rule('^share/([^/]+)/?$', 'index.php?share_file=$1', 'top');
    }
    
    public function add_query_vars($vars) {
        $vars[] = 'share_file';
        return $vars;
    }
    
    public function handle_share_request($wp) {
        if (!empty($wp->query_vars['share_file'])) {
            $this->serve_shared_file($wp->query_vars['share_file']);
            exit;
        }
    }
    
    private function serve_shared_file($id) {
        global $wpdb;
        
        $share = $wpdb->get_row($wpdb->prepare(
            "SELECT * FROM {$wpdb->prefix}tasia_shares WHERE share_id = %s",
            sanitize_text_field($id)
        ));
        
        if (!$share) {
            wp_die('Share not found', 404);
        }
        
        // Check password
        if ($share->password_hash && empty($_POST['password'])) {
            echo '<!DOCTYPE html><html><head><title>Password Protected</title></head><body>';
            echo '<form method="post"><h2>Enter Password</h2>';
            echo '<input type="password" name="password" required>';
            echo '<button type="submit">Unlock</button></form></body></html>';
            exit;
        }
        
        if ($share->password_hash) {
            if (!wp_check_password($_POST['password'], $share->password_hash)) {
                echo '<!DOCTYPE html><html><head><title>Wrong Password</title></head><body>';
                echo '<form method="post"><h2>Wrong Password</h2>';
                echo '<input type="password" name="password" required>';
                echo '<button type="submit">Try Again</button></form></body></html>';
                exit;
            }
        }
        
        // Check expiry
        if ($share->expires && strtotime($share->expires) < time()) {
            wp_die('Share expired', 410);
        }
        
        // Serve file
        header('Content-Type: ' . $share->content_type);
        echo $share->content;
        exit;
    }
    
    public function register_settings() {
        register_setting('tasia_github', 'tasia_github_token');
        register_setting('tasia_github', 'tasia_github_default_org');
    }
    
    public function add_admin_menu() {
        add_menu_page(
            'GitHub Manager',
            'GitHub Manager',
            'manage_options',
            'tasia-github',
            [$this, 'admin_page'],
            'dashicons-editor-code',
            4
        );
    }
    
    private function get_token() {
        return get_option('tasia_github_token', '');
    }
    
    private function github_api($endpoint, $method = 'GET', $body = null) {
        $token = $this->get_token();
        if (!$token) {
            return ['error' => 'No token configured'];
        }
        
        $url = 'https://api.github.com' . $endpoint;
        $args = [
            'method' => $method,
            'headers' => [
                'Authorization' => 'Bearer ' . $token,
                'Accept' => 'application/vnd.github.v3+json',
                'User-Agent' => 'Tasia-GitHub-Manager'
            ]
        ];
        
        if ($body) {
            $args['body'] = json_encode($body);
            $args['headers']['Content-Type'] = 'application/json';
        }
        
        $response = wp_remote_request($url, $args);
        
        if (is_wp_error($response)) {
            return ['error' => $response->get_error_message()];
        }
        
        return json_decode(wp_remote_retrieve_body($response), true);
    }
    
    public function admin_page() {
        $token = $this->get_token();
        
        if (isset($_POST['action']) && check_admin_referer('github_action')) {
            echo $this->handle_action($_POST['action']);
        }
        
        if (!$token) {
            ?>
            <div class="wrap">
                <h1>Tasia GitHub Manager</h1>
                <div class="card" style="max-width:600px;">
                    <h2>Setup</h2>
                    <p>Enter your GitHub Personal Access Token to manage repositories.</p>
                    <form method="post" action="options.php">
                        <?php settings_fields('tasia_github'); ?>
                        <table class="form-table">
                            <tr>
                                <th>GitHub Token</th>
                                <td>
                                    <input type="password" name="tasia_github_token" value="<?php echo esc_attr($token); ?>" class="regular-text">
                                    <p class="description">Create at: <a href="https://github.com/settings/tokens" target="_blank">github.com/settings/tasks</a></p>
                                </td>
                            </tr>
                            <tr>
                                <th>Default Organization</th>
                                <td><input type="text" name="tasia_github_default_org" value="<?php echo esc_attr(get_option('tasia_github_default_org', '')); ?>" class="regular-text"></td>
                            </tr>
                        </table>
                        <?php submit_button('Save Token'); ?>
                    </form>
                </div>
            </div>
            <?php
            return;
        }
        
        // Get user info
        $user = $this->github_api('/user');
        
        // Get repos
        $repos = $this->github_api('/user/repos?per_page=100&sort=updated');
        
        // Get gists
        $gists = $this->github_api('/gists');
        
        ?>
        <div class="wrap">
            <h1>Tasia GitHub Manager</h1>
            <p>Logged in as: <strong><?php echo esc_html($user['login'] ?? 'Unknown'); ?></strong></p>
            
            <div style="display:flex;gap:20px;">
                <!-- Repositories -->
                <div style="flex:1;">
                    <div class="card" style="background:#1a1a1a;padding:20px;border-radius:8px;">
                        <h2>Repositories</h2>
                        <table class="widefat">
                            <thead>
                                <tr>
                                    <th>Name</th>
                                    <th>Private</th>
                                    <th>Actions</th>
                                </tr>
                            </thead>
                            <tbody>
                                <?php foreach ($repos as $repo): ?>
                                <tr>
                                    <td>
                                        <a href="#" onclick="loadRepo('<?php echo esc_js($repo['full_name']); ?>')">
                                            <?php echo esc_html($repo['name']); ?>
                                        </a>
                                    </td>
                                    <td><?php echo $repo['private'] ? '🔒' : '🌐'; ?></td>
                                    <td>
                                        <button class="button" onclick="viewRepo('<?php echo esc_js($repo['full_name']); ?>')">View</button>
                                    </td>
                                </tr>
                                <?php endforeach; ?>
                            </tbody>
                        </table>
                    </div>
                </div>
                
                <!-- Gists -->
                <div style="flex:1;">
                    <div class="card" style="background:#1a1a1a;padding:20px;border-radius:8px;">
                        <h2>Gists</h2>
                        <table class="widefat">
                            <thead>
                                <tr>
                                    <th>Description</th>
                                    <th>Files</th>
                                </tr>
                            </thead>
                            <tbody>
                                <?php foreach ($gists as $gist): ?>
                                <tr>
                                    <td><?php echo esc_html($gist['description'] ?: 'No description'); ?></td>
                                    <td><?php echo count($gist['files']); ?></td>
                                </tr>
                                <?php endforeach; ?>
                            </tbody>
                        </table>
                    </div>
                </div>
            </div>
            
            <!-- File Editor -->
            <div class="card" id="file-editor" style="margin-top:20px;background:#1a1a1a;padding:20px;border-radius:8px;display:none;">
                <h2>Edit File</h2>
                <div id="file-path" style="margin:10px 0;font-weight:bold;"></div>
                <textarea id="file-content" rows="20" class="large-text" style="width:100%;background:#222;color:#eee;"></textarea>
                <div style="margin:10px 0;">
                    <button class="button button-primary" onclick="saveFile()">Save to GitHub</button>
                    <button class="button button-secondary" onclick="sharePublic()">Share Public (Gist)</button>
                    <button class="button button-secondary" onclick="showSharePassword()">Share with Password</button>
                    <button class="button" onclick="cancelEdit()">Cancel</button>
                </div>
            </div>
            
            <!-- Password Share Dialog -->
            <div id="share-password-dialog" style="display:none;position:fixed;top:50%;left:50%;transform:translate(-50%,-50%);background:#333;padding:30px;border-radius:10px;z-index:1000;">
                <h3>Password Protected Share</h3>
                <input type="password" id="share-password" placeholder="Enter password" style="padding:10px;margin:10px 0;">
                <input type="text" id="share-expire" placeholder="Expire in (hours, empty = never)" style="padding:10px;margin:10px 0;width:200px;">
                <br>
                <button class="button button-primary" onclick="shareWithPassword()">Create Share Link</button>
                <button class="button" onclick="document.getElementById('share-password-dialog').style.display='none'">Cancel</button>
            </div>
            
            <!-- Repo Contents -->
            <div class="card" id="repo-viewer" style="margin-top:20px;background:#1a1a1a;padding:20px;border-radius:8px;">
                <h2 id="repo-title">Select a repository</h2>
                <div id="repo-contents"></div>
            </div>
        </div>
        
        <script>
        var currentRepo = '';
        var currentFile = '';
        
        function loadRepo(repo) {
            currentRepo = repo;
            jQuery('#repo-title').text(repo);
            jQuery('#repo-contents').html('<p>Loading...</p>');
            
            jQuery.post(ajaxurl, {
                action: 'github_request',
                nonce: '<?php echo wp_create_nonce('github_ajax'); ?>',
                type: 'repo_contents',
                repo: repo
            }, function(res) {
                if (res.error) {
                    jQuery('#repo-contents').html('<p style="color:red">' + res.error + '</p>');
                } else {
                    var html = '<table class="widefat"><thead><tr><th>Name</th><th>Type</th><th>Size</th></tr></thead><tbody>';
                    res.forEach(function(item) {
                        html += '<tr>';
                        if (item.type === 'dir') {
                            html += '<td>📁 <a href="#" onclick="loadRepoPath(\'' + currentRepo + '/' + item.name + '\')">' + item.name + '</a></td>';
                        } else {
                            html += '<td>📄 ' + item.name + '</td>';
                            html += '<td><button class="button" onclick="viewFile(\'' + currentRepo + '/' + item.path + '\')">Edit</button></td>';
                        }
                        html += '<td>' + (item.size || '-') + '</td></tr>';
                    });
                    html += '</tbody></table>';
                    jQuery('#repo-contents').html(html);
                }
            });
        }
        
        function loadRepoPath(path) {
            jQuery.post(ajaxurl, {
                action: 'github_request',
                nonce: '<?php echo wp_create_nonce('github_ajax'); ?>',
                type: 'repo_contents',
                repo: path
            }, function(res) {
                var parts = path.split('/');
                currentRepo = parts[0];
                jQuery('#repo-title').text(path);
                // Same rendering as above
            });
        }
        
        function viewFile(path) {
            jQuery.post(ajaxurl, {
                action: 'github_request',
                nonce: '<?php echo wp_create_nonce('github_ajax'); ?>',
                type: 'file_content',
                path: path
            }, function(res) {
                if (res.content) {
                    currentFile = path;
                    jQuery('#file-path').text(path);
                    jQuery('#file-content').val(atob(res.content));
                    jQuery('#file-editor').show();
                } else if (res.error) {
                    alert(res.error);
                }
            });
        }
        
        function saveFile() {
            var content = jQuery('#file-content').val();
            jQuery.post(ajaxurl, {
                action: 'github_request',
                nonce: '<?php echo wp_create_nonce('github_ajax'); ?>',
                type: 'save_file',
                path: currentFile,
                content: btoa(content)
            }, function(res) {
                if (res.error) {
                    alert(res.error);
                } else {
                    alert('Saved to GitHub!');
                }
            });
        }
        
        function sharePublic() {
            var content = jQuery('#file-content').val();
            var filename = currentFile.split('/').pop();
            jQuery.post(ajaxurl, {
                action: 'github_request',
                nonce: '<?php echo wp_create_nonce('github_ajax'); ?>',
                type: 'create_gist',
                content: btoa(content),
                filename: filename
            }, function(res) {
                if (res.html_url) {
                    prompt('Public Gist URL:', res.html_url);
                } else if (res.error) {
                    alert(res.error);
                }
            });
        }
        
        function showSharePassword() {
            jQuery('#share-password-dialog').show();
        }
        
        function shareWithPassword() {
            var content = jQuery('#file-content').val();
            var password = jQuery('#share-password').val();
            var expire = jQuery('#share-expire').val();
            
            jQuery.post(ajaxurl, {
                action: 'github_request',
                nonce: '<?php echo wp_create_nonce('github_ajax'); ?>',
                type: 'share_password',
                content: btoa(content),
                filename: currentFile.split('/').pop(),
                password: password,
                expire: expire
            }, function(res) {
                if (res.share_url) {
                    jQuery('#share-password-dialog').hide();
                    prompt('Password Protected Share URL:', res.share_url);
                } else if (res.error) {
                    alert(res.error);
                }
            });
        }
        
        function cancelEdit() {
            jQuery('#file-editor').hide();
            currentFile = '';
        }
        
        function viewRepo(repo) {
            loadRepo(repo);
        }
        </script>
        
        <style>
            .card { background: #1a1a1a; padding: 20px; border-radius: 8px; }
            .widefat { background: #222; }
            .widefat th, .widefat td { border: 1px solid #444; padding: 8px; }
            .widefat th { background: #333; }
        </style>
        <?php
    }
    
    private function handle_action($action) {
        if (!check_admin_referer('github_action')) return;
        
        if (isset($_POST['github_type'])) {
            echo json_encode(['status' => 'ok']);
            exit;
        }
    }
    
    public function handle_ajax() {
        check_ajax_referer('github_ajax');
        
        $type = $_POST['type'];
        
        switch ($type) {
            case 'repo_contents':
                $repo = $_POST['repo'];
                $result = $this->github_api("/repos/$repo/contents");
                break;
                
            case 'file_content':
                $path = $_POST['path'];
                $parts = explode('/', $path, 2);
                $repo = $parts[0];
                $file = $parts[1] ?? '';
                $result = $this->github_api("/repos/$repo/contents/$file");
                break;
                
            case 'save_file':
                $path = $_POST['path'];
                $content = $_POST['content'];
                $parts = explode('/', $path, 2);
                $repo = $parts[0];
                $file = $parts[1];
                
                // Get current file SHA
                $current = $this->github_api("/repos/$repo/contents/$file");
                $sha = $current['sha'] ?? '';
                
                $result = $this->github_api("/repos/$repo/contents/$file", 'PUT', [
                    'message' => 'Updated via Tasia GitHub Manager',
                    'content' => $content,
                    'sha' => $sha
                ]);
                break;
                
            case 'create_gist':
                $content = base64_decode($_POST['content']);
                $filename = sanitize_file_name($_POST['filename'] ?: 'file.txt');
                $filename = $filename ?: 'file.txt';
                
                $result = $this->github_api('/gists', 'POST', [
                    'description' => 'Shared via Tasia GitHub Manager',
                    'public' => true,
                    'files' => [
                        $filename => ['content' => $content]
                    ]
                ]);
                break;
                
            case 'share_password':
                global $wpdb;
                
                $content = base64_decode($_POST['content']);
                $filename = sanitize_file_name($_POST['filename'] ?: 'file.txt');
                $password = $_POST['password'] ?? '';
                $expire_hours = isset($_POST['expire']) ? intval($_POST['expire']) : 0;
                
                $share_id = wp_generate_password(32, false);
                
                $content_type = 'text/plain';
                if (preg_match('/\.(php|html|js|css|xml|json|md|txt)$/i', $filename)) {
                    $content_type = 'text/html';
                }
                
                $expires = null;
                if ($expire_hours > 0) {
                    $expires = date('Y-m-d H:i:s', time() + ($expire_hours * 3600));
                }
                
                $password_hash = null;
                if ($password) {
                    $password_hash = wp_hash_password($password);
                }
                
                $wpdb->insert(
                    $wpdb->prefix . 'tasia_shares',
                    [
                        'share_id' => $share_id,
                        'filename' => $filename,
                        'content' => $content,
                        'content_type' => $content_type,
                        'password_hash' => $password_hash,
                        'expires' => $expires
                    ],
                    ['%s', '%s', '%s', '%s', '%s', '%s']
                );
                
                $result = [
                    'share_url' => home_url('/share/' . $share_id)
                ];
                break;
                 
            default:
                $result = ['error' => 'Unknown action'];
        }
        
        echo json_encode($result);
        exit;
    }
}

new Tasia_GitHub_Manager();
