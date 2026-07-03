<?php
/**
 * Plugin Name: Tasia OS Docker Manager
 * Description: Manage OpenSim Docker containers and regions from WordPress
 * Version: 1.0.0
 * Author: Tasia AI
 * Requires at least: 5.0
 */

if (!defined('ABSPATH')) exit;

class Tasia_OS_Docker_Manager {
    private $opensim_path;
    private $docker_image;
    
    public function __construct() {
        $this->opensim_path = get_option('tasia_os_path', '/home/marty/opensim');
        $this->docker_image = get_option('tasia_os_docker_image', 'repo.is-on.click/familygrids/babyanni:rev09.3.9333');
        
        add_action('admin_menu', [$this, 'add_admin_menu']);
        add_action('admin_init', [$this, 'register_settings']);
        add_action('wp_ajax_tasia_docker_action', [$this, 'handle_ajax']);
    }
    
    public function register_settings() {
        register_setting('tasia_os_docker', 'tasia_os_path');
        register_setting('tasia_os_docker', 'tasia_os_docker_image');
        register_setting('tasia_os_docker', 'tasia_os_ssh_user');
        register_setting('tasia_os_docker', 'tasia_os_ssh_key');
        register_setting('tasia_os_docker', 'tasia_os_grid_name');
        register_setting('tasia_os_docker', 'tasia_os_grid_url');
    }
    
    public function add_admin_menu() {
        add_menu_page(
            'Tasia OS Manager',
            'OS Docker Manager',
            'manage_options',
            'tasia-os-docker',
            [$this, 'admin_page'],
            'dashicons-admin-generic',
            3
        );
    }
    
    public function admin_page() {
        $services = $this->get_services();
        $stats = $this->get_docker_stats();
        
        if (isset($_POST['action'])) {
            check_admin_referer('tasia_os_action');
            $this->handle_action($_POST['action'], $_POST['service'] ?? '');
        }
        
        ?>
        <div class="wrap">
            <h1>Tasia OpenSim Docker Manager</h1>
            
            <div class="card">
                <h2>Settings</h2>
                <form method="post" action="options.php">
                    <?php settings_fields('tasia_os_docker'); ?>
                    <table class="form-table">
                        <tr>
                            <th>OpenSim Path</th>
                            <td><input type="text" name="tasia_os_path" value="<?php echo esc_attr(get_option('tasia_os_path', '/home/marty/opensim')); ?>" class="regular-text"></td>
                        </tr>
                        <tr>
                            <th>Docker Image</th>
                            <td><input type="text" name="tasia_os_docker_image" value="<?php echo esc_attr($this->docker_image); ?>" class="regular-text"></td>
                        </tr>
                        <tr>
                            <th>Grid Name</th>
                            <td><input type="text" name="tasia_os_grid_name" value="<?php echo esc_attr(get_option('tasia_os_grid_name', 'Tasia Grid')); ?>" class="regular-text"></td>
                        </tr>
                        <tr>
                            <th>Grid URL</th>
                            <td><input type="url" name="tasia_os_grid_url" value="<?php echo esc_attr(get_option('tasia_os_grid_url', 'https://i.let-us.cyou')); ?>" class="regular-text"></td>
                        </tr>
                    </table>
                    <?php submit_button('Save Settings'); ?>
                </form>
            </div>
            
            <div class="card" style="margin-top: 20px;">
                <h2>Docker Stats</h2>
                <p><strong>Running Containers:</strong> <?php echo $stats['running']; ?></p>
                <p><strong>Total Containers:</strong> <?php echo $stats['total']; ?></p>
                <form method="post">
                    <?php wp_nonce_field('tasia_os_action'); ?>
                    <input type="hidden" name="action" value="prune">
                    <?php submit_button('Prune Unused Docker', 'secondary'); ?>
                </form>
            </div>
            
            <div class="card" style="margin-top: 20px;">
                <h2>Regions / Services</h2>
                <table class="widefat">
                    <thead>
                        <tr>
                            <th>Service Name</th>
                            <th>Hostname</th>
                            <th>Status</th>
                            <th>Actions</th>
                        </tr>
                    </thead>
                    <tbody>
                        <?php foreach ($services as $service): ?>
                        <tr>
                            <td><?php echo esc_html($service['name']); ?></td>
                            <td><?php echo esc_html($service['hostname']); ?></td>
                            <td>
                                <span class="status-<?php echo $service['status']; ?>">
                                    <?php echo ucfirst($service['status']); ?>
                                </span>
                            </td>
                            <td>
                                <form method="post" style="display:inline;">
                                    <?php wp_nonce_field('tasia_os_action'); ?>
                                    <input type="hidden" name="service" value="<?php echo esc_attr($service['hostname']); ?>">
                                    <button type="submit" name="action" value="start" class="button button-primary">Start</button>
                                    <button type="submit" name="action" value="stop" class="button">Stop</button>
                                    <button type="submit" name="action" value="restart" class="button">Restart</button>
                                    <button type="submit" name="action" value="logs" class="button">Logs</button>
                                </form>
                            </td>
                        </tr>
                        <?php endforeach; ?>
                    </tbody>
                </table>
            </div>
            
            <div class="card" style="margin-top: 20px;">
                <h2>Add New Region</h2>
                <form method="post">
                    <?php wp_nonce_field('tasia_os_action'); ?>
                    <input type="hidden" name="action" value="create">
                    <table class="form-table">
                        <tr>
                            <th>Region Name</th>
                            <td><input type="text" name="region_name" required class="regular-text" placeholder="My_Region"></td>
                        </tr>
                        <tr>
                            <th>Location X</th>
                            <td><input type="number" name="loc_x" value="1000" class="small-text"></td>
                        </tr>
                        <tr>
                            <th>Location Y</th>
                            <td><input type="number" name="loc_y" value="1000" class="small-text"></td>
                        </tr>
                        <tr>
                            <th>Size (regions)</th>
                            <td>
                                <select name="size">
                                    <option value="1">1x1</option>
                                    <option value="2">2x2</option>
                                    <option value="4">4x4</option>
                                </select>
                            </td>
                        </tr>
                    </table>
                    <?php submit_button('Create Region'); ?>
                </form>
            </div>
            
            <div class="card" style="margin-top: 20px;">
                <h2>Quick Actions</h2>
                <form method="post" style="display:inline;">
                    <?php wp_nonce_field('tasia_os_action'); ?>
                    <input type="hidden" name="action" value="start_all">
                    <?php submit_button('Start All', 'primary'); ?>
                </form>
                <form method="post" style="display:inline;">
                    <?php wp_nonce_field('tasia_os_action'); ?>
                    <input type="hidden" name="action" value="stop_all">
                    <?php submit_button('Stop All', 'secondary'); ?>
                </form>
                <form method="post" style="display:inline;">
                    <?php wp_nonce_field('tasia_os_action'); ?>
                    <input type="hidden" name="action" value="restart_all">
                    <?php submit_button('Restart All', 'secondary'); ?>
                </form>
            </div>
            
            <?php if (!empty($_GET['logs'])): ?>
            <div class="card" style="margin-top: 20px;">
                <h2>Logs: <?php echo esc_html($_GET['logs']); ?></h2>
                <pre style="background:#f5f5f5;padding:10px;max-height:400px;overflow:auto;"><?php echo esc_html($this->get_logs($_GET['logs'])); ?></pre>
            </div>
            <?php endif; ?>
        </div>
        
        <style>
            .status-running { color: green; font-weight: bold; }
            .status-stopped { color: red; }
            .status-exited { color: gray; }
        </style>
        <?php
    }
    
    private function get_services() {
        $path = $this->opensim_path;
        $services = [];
        
        if (!is_dir("$path/compose.d")) {
            return $services;
        }
        
        $files = glob("$path/compose.d/*.yml");
        foreach ($files as $file) {
            $content = file_get_contents($file);
            if (preg_match_all('/hostname:\s*(\S+)/', $content, $matches)) {
                foreach ($matches[1] as $hostname) {
                    $status = $this->get_container_status($hostname);
                    $services[] = [
                        'name' => 'sim-' . $hostname,
                        'hostname' => $hostname,
                        'status' => $status
                    ];
                }
            }
        }
        
        return $services;
    }
    
    private function get_container_status($hostname) {
        $cmd = "docker ps --filter \"name=$hostname\" --format '{{.Status}}' 2>/dev/null";
        $output = shell_exec($cmd);
        return trim($output) ? 'running' : 'stopped';
    }
    
    private function get_docker_stats() {
        $total = (int)shell_exec("docker ps -a --format '{{.ID}}' 2>/dev/null | wc -l");
        $running = (int)shell_exec("docker ps --format '{{.ID}}' 2>/dev/null | wc -l");
        
        return ['total' => $total, 'running' => $running];
    }
    
    private function handle_action($action, $service) {
        if (!current_user_can('manage_options')) return;
        
        $path = $this->opensim_path;
        
        switch ($action) {
            case 'start':
            case 'stop':
            case 'restart':
            case 'logs':
                if ($service) {
                    $cmd = "cd $path && ./manage_compose.sh $action $service 2>&1";
                    if ($action === 'logs') {
                        $logs = shell_exec($cmd);
                        wp_redirect(add_query_arg('logs', urlencode($service)));
                        exit;
                    }
                    shell_exec($cmd);
                }
                break;
                
            case 'start_all':
                shell_exec("cd $path && ./manage_compose.sh start 2>&1");
                break;
                
            case 'stop_all':
                shell_exec("cd $path && ./manage_compose.sh stop 2>&1");
                break;
                
            case 'restart_all':
                shell_exec("cd $path && ./manage_compose.sh restart 2>&1");
                break;
                
            case 'create':
                $region_name = sanitize_file_name($_POST['region_name']);
                $loc_x = intval($_POST['loc_x']);
                $loc_y = intval($_POST['loc_y']);
                $this->create_region($region_name, $loc_x, $loc_y);
                break;
                
            case 'prune':
                shell_exec("docker system prune -f 2>&1");
                break;
        }
        
        wp_redirect($_SERVER['REQUEST_URI']);
        exit;
    }
    
    private function create_region($region_name, $loc_x, $loc_y) {
        $path = $this->opensim_path;
        $image = $this->docker_image;
        
        // Create region directories
        @mkdir("/mnt/storage/grid/$region_name", 0755, true);
        @mkdir("$path/regiongen/Regions/$region_name", 0755, true);
        @mkdir("$path/services2", 0755, true);
        @mkdir("$path/compose.d", 0755, true);
        
        // Generate region config
        $region_config = $this->generate_region_config($region_name, $loc_x, $loc_y);
        file_put_contents("$path/services2/$region_name.ini", $region_config);
        
        // Generate docker compose file
        $compose = $this->generate_compose($region_name);
        file_put_contents("$path/compose.d/docker-compose-$region_name.yml", $compose);
        
        // Generate SSH config
        $ssh_config = "Port 22\nPermitRootLogin yes\n";
        file_put_contents("$path/ssh2/$region_name.txt", $ssh_config);
        
        // Restart docker compose
        shell_exec("cd $path && ./manage_compose.sh start $region_name 2>&1");
    }
    
    private function generate_region_config($name, $x, $y) {
        $grid_name = get_option('tasia_os_grid_name', 'Tasia Grid');
        return <<<INI
[GridService]
    ; Your grid name
    GridName = $grid_name
    GridURL = ${Tasia.GridURL}

[Const]
    ; Region location
    RegionLocX = $x
    RegionLocY = $y

[Database Service]
    ; Database connection (uses Robust DB)
    StorageProvider = OpenSim.Data.MySQL.dll
    ConnectionString = "Data Source=localhost;Database=robust;User ID=root;Password=CHANGE_ME_DB_PASSWORD;SslMode=none"

[Region-$name]
    RegionUUID = 
    RegionName = $name
    RegionType = Main
    Location = $x,$y
    SizeX = 256
    SizeY = 256
    SizeZ = 256
    MaxRegions = 4

    ; Add your region to the grid
    InternalAddress = 0.0.0.0
    ExternalHostName = \${Const|ExternalHostName}

    ; AllowHypergrid = true
    HostName = $name
INI;
    }
    
    private function generate_compose($region_name) {
        $image = $this->docker_image;
        return <<<YAML
services:
  sim-$region_name:
    hostname: $region_name
    image: $image
    volumes:
      - "/mnt/storage/grid/$region_name:/home/grid/opensim/bin/Regions/$region_name/oar"
      - "/home/marty/opensim/regiongen/Regions/$region_name:/home/grid/opensim/bin/Regions/$region_name"
      - "/home/marty/opensim/services2/$region_name.ini:/home/grid/opensim/bin/python_config.ini"
      - "/home/marty/opensim/assetcache:/home/grid/opensim/bin/assetcache"
      - "/home/marty/opensim/fs-assets:/home/grid/opensim/bin/fsassets"
      - "/home/marty/opensim/config-include:/home/grid/opensim/bin/config-include/"
      - "/home/marty/opensim/templates:/home/grid/opensim/bin/templates/"
      - "/home/marty/opensim/services/python.py:/home/grid/opensim/bin/python.py"
      - "/home/marty/opensim/ssh2/$region_name.txt:/etc/ssh/sshd_config"
      - "/home/marty/opensim/ssh2/runsh/$region_name:/var/run/sshd"
      - "/home/marty/opensim/bashrc:/root/.bashrc"
      - "/home/marty/opensim/bashhistory:/root/.bash_history:ro"
      - "/home/marty/opensim/shadow2/$region_name.config:/etc/shadow"
    network_mode: host
YAML;
    }
    
    private function get_logs($service) {
        $path = $this->opensim_path;
        return shell_exec("cd $path && ./manage_compose.sh logs $service 2>&1 | tail -100");
    }
}

new Tasia_OS_Docker_Manager();
