<?php
/**
 * Plugin Name: LSL Protector Workspace
 * Description: Premium-member shortcode tool for transforming LSL into a harder-to-read deployment copy while securely storing the original for later restore by the owner using a password.
 * Version: 0.2.0
 * Author: Tasia
 */

if (!defined('ABSPATH')) {
    exit;
}

if (!class_exists('LSL_Protector_Workspace')) {
    class LSL_Protector_Workspace {
        const TABLE_SUFFIX = 'lsl_protector_records';
        const NONCE_ACTION = 'lsl_protector_action';
        const SHORTCODE = 'lsl_protector';

        private $keywords = array(
            'default', 'state', 'jump', 'return', 'if', 'else', 'for', 'do', 'while',
            'TRUE', 'FALSE', 'NULL_KEY',
            'integer', 'float', 'string', 'key', 'vector', 'rotation', 'list', 'quaternion'
        );

        private $events = array(
            'state_entry', 'state_exit', 'touch_start', 'touch', 'touch_end', 'timer', 'listen',
            'link_message', 'on_rez', 'changed', 'attach', 'dataserver', 'http_response',
            'object_rez', 'sensor', 'no_sensor', 'at_target', 'not_at_target', 'moving_start',
            'moving_end', 'run_time_permissions', 'remote_data', 'land_collision_start',
            'land_collision', 'land_collision_end', 'collision_start', 'collision', 'collision_end',
            'control', 'money', 'email', 'path_update', 'transaction_result',
            'experience_permissions', 'experience_permissions_denied'
        );

        public static function activate_static() {
            $instance = new self();
            $instance->create_table();
            flush_rewrite_rules();
        }

        public function __construct() {
            add_action('init', array($this, 'register_shortcode'));
            add_action('wp_enqueue_scripts', array($this, 'register_assets'));
        }

        public function register_assets() {
            wp_register_style('lsl-protector-style', false, array(), '0.2.0');
            wp_add_inline_style('lsl-protector-style', $this->get_css());
        }

        public function register_shortcode() {
            add_shortcode(self::SHORTCODE, array($this, 'render_shortcode'));
        }

        public function render_shortcode($atts = array()) {
            if (!is_user_logged_in()) {
                return '<div class="lsl-protector-notice">Please log in to use this tool.</div>';
            }

            $user_id = get_current_user_id();
            if (!$this->current_user_can_use($user_id)) {
                return '<div class="lsl-protector-notice">This tool is available to premium members only.</div>';
            }

            wp_enqueue_style('lsl-protector-style');

            $state = array(
                'message' => '',
                'error' => '',
                'output' => '',
                'mapping' => '',
                'source' => '',
                'label' => '',
                'record_id' => '',
                'mode' => 'protect',
            );

            if ($_SERVER['REQUEST_METHOD'] === 'POST' && isset($_POST['lsl_protector_nonce'])) {
                $state = $this->handle_post($user_id);
            }

            $records = $this->get_user_records($user_id);
            $nonce = wp_create_nonce(self::NONCE_ACTION);

            ob_start();
            ?>
            <div class="lsl-protector-wrap">
                <div class="lsl-protector-card">
                    <h2>LSL Protector Workspace</h2>
                    <p class="lsl-protector-muted">
                        This page creates a harder-to-read deployment copy of your LSL and stores the original securely so you can restore it later with your password.
                    </p>

                    <?php if (!empty($state['message'])) : ?>
                        <div class="lsl-protector-alert lsl-protector-ok"><?php echo esc_html($state['message']); ?></div>
                    <?php endif; ?>

                    <?php if (!empty($state['error'])) : ?>
                        <div class="lsl-protector-alert lsl-protector-error"><?php echo esc_html($state['error']); ?></div>
                    <?php endif; ?>

                    <form method="post" class="lsl-protector-form">
                        <input type="hidden" name="lsl_protector_nonce" value="<?php echo esc_attr($nonce); ?>">

                        <div class="lsl-protector-grid">
                            <div>
                                <label for="lsl_protector_label">Label</label>
                                <input id="lsl_protector_label" name="label" type="text" value="<?php echo esc_attr($state['label']); ?>" placeholder="Example: HyperGate Display Module">
                            </div>
                            <div>
                                <label for="lsl_protector_password">Password</label>
                                <input id="lsl_protector_password" name="password" type="password" autocomplete="new-password" placeholder="Enter a password for your restore vault">
                            </div>
                        </div>

                        <div class="lsl-protector-grid lsl-protector-grid-2">
                            <div>
                                <label for="lsl_protector_source">LSL source</label>
                                <textarea id="lsl_protector_source" name="source" placeholder="Paste your LSL here..."><?php echo esc_textarea($state['source']); ?></textarea>
                            </div>
                            <div>
                                <label for="lsl_protector_output">Transformed output</label>
                                <textarea id="lsl_protector_output" readonly><?php echo esc_textarea($state['output']); ?></textarea>
                            </div>
                        </div>

                        <div class="lsl-protector-options">
                            <label><input type="checkbox" name="strip_comments" value="1" checked> Strip comments</label>
                            <label><input type="checkbox" name="rename_symbols" value="1" checked> Rename user-defined symbols</label>
                            <label><input type="checkbox" name="trim_whitespace" value="1" checked> Trim whitespace</label>
                            <label><input type="checkbox" name="pack_one_line" value="1" checked> Pack into one long line</label>
                            <label><input type="checkbox" name="fake_comments" value="1" checked> Add tiny fake block comments</label>
                            <label><input type="checkbox" name="show_mapping" value="1" checked> Show mapping</label>
                        </div>

                        <p class="lsl-protector-muted">
                            The plugin inserts statement breaks before stripping comments, which is safer for packed LSL. The one-line mode and fake comments are only a friction layer.
                        </p>

                        <div>
                            <label for="lsl_protector_mapping">Rename mapping</label>
                            <textarea id="lsl_protector_mapping" readonly><?php echo esc_textarea($state['mapping']); ?></textarea>
                        </div>

                        <div class="lsl-protector-actions">
                            <button type="submit" name="lsl_action" value="protect">Protect &amp; Save Original</button>
                        </div>
                    </form>
                </div>

                <div class="lsl-protector-card">
                    <h3>Restore original</h3>
                    <p class="lsl-protector-muted">Choose one of your saved items, enter the same password, and restore the original source.</p>

                    <form method="post" class="lsl-protector-form">
                        <input type="hidden" name="lsl_protector_nonce" value="<?php echo esc_attr($nonce); ?>">

                        <div class="lsl-protector-grid">
                            <div>
                                <label for="lsl_record_id">Saved item</label>
                                <select id="lsl_record_id" name="record_id">
                                    <option value="">Select saved item</option>
                                    <?php foreach ($records as $record) : ?>
                                        <option value="<?php echo esc_attr((string) $record['id']); ?>" <?php selected((string) $state['record_id'], (string) $record['id']); ?>>
                                            <?php echo esc_html($record['label'] . ' — #' . $record['id'] . ' — ' . $record['created_at']); ?>
                                        </option>
                                    <?php endforeach; ?>
                                </select>
                            </div>
                            <div>
                                <label for="lsl_restore_password">Password</label>
                                <input id="lsl_restore_password" name="password" type="password" autocomplete="current-password" placeholder="Enter restore password">
                            </div>
                        </div>

                        <div class="lsl-protector-actions">
                            <button type="submit" name="lsl_action" value="restore">Restore Original</button>
                        </div>
                    </form>
                </div>

                <?php if ($state['mode'] === 'restore' && !empty($state['output'])) : ?>
                    <div class="lsl-protector-card">
                        <h3>Restored original</h3>
                        <textarea readonly><?php echo esc_textarea($state['output']); ?></textarea>
                    </div>
                <?php endif; ?>
            </div>
            <?php
            return ob_get_clean();
        }

        private function handle_post($user_id) {
            $state = array(
                'message' => '',
                'error' => '',
                'output' => '',
                'mapping' => '',
                'source' => isset($_POST['source']) ? wp_unslash($_POST['source']) : '',
                'label' => isset($_POST['label']) ? sanitize_text_field(wp_unslash($_POST['label'])) : '',
                'record_id' => isset($_POST['record_id']) ? sanitize_text_field(wp_unslash($_POST['record_id'])) : '',
                'mode' => isset($_POST['lsl_action']) ? sanitize_key(wp_unslash($_POST['lsl_action'])) : 'protect',
            );

            $nonce = isset($_POST['lsl_protector_nonce']) ? sanitize_text_field(wp_unslash($_POST['lsl_protector_nonce'])) : '';
            if (!wp_verify_nonce($nonce, self::NONCE_ACTION)) {
                $state['error'] = 'Security check failed.';
                return $state;
            }

            if (!$this->current_user_can_use($user_id)) {
                $state['error'] = 'You do not have access to this tool.';
                return $state;
            }

            $password = isset($_POST['password']) ? (string) $_POST['password'] : '';
            if ($state['mode'] === 'restore') {
                return $this->handle_restore($user_id, $password, $state);
            }
            return $this->handle_protect($user_id, $password, $state);
        }

        private function handle_protect($user_id, $password, $state) {
            $source = isset($_POST['source']) ? wp_unslash($_POST['source']) : '';
            $label = !empty($state['label']) ? $state['label'] : 'Untitled Script';

            if ($source === '') {
                $state['error'] = 'Please paste your LSL source first.';
                return $state;
            }

            if (strlen($password) < 6) {
                $state['error'] = 'Please use a password with at least 6 characters.';
                return $state;
            }

            $options = array(
                'strip_comments' => !empty($_POST['strip_comments']),
                'rename_symbols' => !empty($_POST['rename_symbols']),
                'trim_whitespace' => !empty($_POST['trim_whitespace']),
                'pack_one_line' => !empty($_POST['pack_one_line']),
                'fake_comments' => !empty($_POST['fake_comments']),
                'show_mapping' => !empty($_POST['show_mapping']),
            );

            $result = $this->transform_lsl($source, $options);
            $cipher = $this->encrypt_payload(array(
                'original' => $source,
                'mapping' => $result['mapping'],
                'options' => $options,
            ), $password);

            if (is_wp_error($cipher)) {
                $state['error'] = $cipher->get_error_message();
                return $state;
            }

            $inserted = $this->insert_record(array(
                'user_id' => $user_id,
                'label' => $label,
                'ciphertext' => $cipher['ciphertext'],
                'salt' => $cipher['salt'],
                'iv' => $cipher['iv'],
                'algo' => $cipher['algo'],
            ));

            if (is_wp_error($inserted)) {
                $state['error'] = $inserted->get_error_message();
                return $state;
            }

            $state['output'] = $result['output'];
            $state['mapping'] = !empty($options['show_mapping']) ? wp_json_encode($result['mapping'], JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES) : '';
            $state['record_id'] = (string) $inserted;
            $state['message'] = 'Protected copy created. Original saved as record #' . $inserted . '.';
            $state['source'] = $source;
            return $state;
        }

        private function handle_restore($user_id, $password, $state) {
            $record_id = absint($state['record_id']);
            if (!$record_id) {
                $state['error'] = 'Please select a saved item.';
                return $state;
            }

            if ($password === '') {
                $state['error'] = 'Please enter the password used when the original was saved.';
                return $state;
            }

            $record = $this->get_record($record_id, $user_id);
            if (!$record) {
                $state['error'] = 'Saved item not found.';
                return $state;
            }

            $decrypted = $this->decrypt_payload($record, $password);
            if (is_wp_error($decrypted)) {
                $state['error'] = $decrypted->get_error_message();
                return $state;
            }

            $state['output'] = isset($decrypted['original']) ? (string) $decrypted['original'] : '';
            $state['mapping'] = !empty($decrypted['mapping']) ? wp_json_encode($decrypted['mapping'], JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES) : '';
            $state['label'] = isset($record['label']) ? (string) $record['label'] : '';
            $state['message'] = 'Original restored for record #' . $record_id . '.';
            return $state;
        }

        private function current_user_can_use($user_id) {
            $default = current_user_can('read');
            return (bool) apply_filters('lsl_protector_user_can_use', $default, $user_id);
        }

        private function table_name() {
            global $wpdb;
            return $wpdb->prefix . self::TABLE_SUFFIX;
        }

        private function create_table() {
            global $wpdb;
            $table = $this->table_name();
            $charset = $wpdb->get_charset_collate();
            require_once ABSPATH . 'wp-admin/includes/upgrade.php';
            $sql = "CREATE TABLE {$table} (
                id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                user_id BIGINT UNSIGNED NOT NULL,
                label VARCHAR(191) NOT NULL,
                ciphertext LONGTEXT NOT NULL,
                salt VARCHAR(255) NOT NULL,
                iv VARCHAR(255) NOT NULL,
                algo VARCHAR(50) NOT NULL,
                created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (id),
                KEY user_id (user_id)
            ) {$charset};";
            dbDelta($sql);
        }

        private function insert_record($data) {
            global $wpdb;
            $result = $wpdb->insert(
                $this->table_name(),
                array(
                    'user_id' => (int) $data['user_id'],
                    'label' => (string) $data['label'],
                    'ciphertext' => (string) $data['ciphertext'],
                    'salt' => (string) $data['salt'],
                    'iv' => (string) $data['iv'],
                    'algo' => (string) $data['algo'],
                ),
                array('%d', '%s', '%s', '%s', '%s', '%s')
            );

            if ($result === false) {
                return new WP_Error('lsl_insert_failed', 'Failed to save encrypted original.');
            }
            return (int) $wpdb->insert_id;
        }

        private function get_user_records($user_id) {
            global $wpdb;
            $rows = $wpdb->get_results(
                $wpdb->prepare(
                    'SELECT id, label, created_at FROM ' . $this->table_name() . ' WHERE user_id = %d ORDER BY id DESC LIMIT 100',
                    $user_id
                ),
                ARRAY_A
            );
            return is_array($rows) ? $rows : array();
        }

        private function get_record($record_id, $user_id) {
            global $wpdb;
            $row = $wpdb->get_row(
                $wpdb->prepare(
                    'SELECT * FROM ' . $this->table_name() . ' WHERE id = %d AND user_id = %d LIMIT 1',
                    $record_id,
                    $user_id
                ),
                ARRAY_A
            );
            return is_array($row) ? $row : null;
        }

        private function get_random_bytes($length) {
            if (function_exists('random_bytes')) {
                return random_bytes($length);
            }
            $bytes = '';
            while (strlen($bytes) < $length) {
                $bytes .= wp_generate_password(64, true, true);
            }
            return substr($bytes, 0, $length);
        }

        private function encrypt_payload($payload, $password) {
            $json = wp_json_encode($payload, JSON_UNESCAPED_SLASHES);
            if (!is_string($json)) {
                return new WP_Error('lsl_encrypt_failed', 'Could not serialize the original source.');
            }

            if (function_exists('sodium_crypto_secretbox') && function_exists('sodium_crypto_pwhash')) {
                $salt = $this->get_random_bytes(16);
                $nonce = $this->get_random_bytes(SODIUM_CRYPTO_SECRETBOX_NONCEBYTES);
                $key = sodium_crypto_pwhash(
                    SODIUM_CRYPTO_SECRETBOX_KEYBYTES,
                    $password,
                    $salt,
                    SODIUM_CRYPTO_PWHASH_OPSLIMIT_MODERATE,
                    SODIUM_CRYPTO_PWHASH_MEMLIMIT_MODERATE
                );
                $ciphertext = sodium_crypto_secretbox($json, $nonce, $key);
                if (function_exists('sodium_memzero')) {
                    sodium_memzero($key);
                }
                return array(
                    'algo' => 'sodium_secretbox',
                    'salt' => base64_encode($salt),
                    'iv' => base64_encode($nonce),
                    'ciphertext' => base64_encode($ciphertext),
                );
            }

            if (function_exists('openssl_encrypt')) {
                $salt = $this->get_random_bytes(16);
                $iv = $this->get_random_bytes(16);
                $key = hash_pbkdf2('sha256', $password, $salt, 120000, 32, true);
                $ciphertext = openssl_encrypt($json, 'aes-256-cbc', $key, OPENSSL_RAW_DATA, $iv);
                if ($ciphertext === false) {
                    return new WP_Error('lsl_encrypt_failed', 'OpenSSL could not encrypt the original source.');
                }
                return array(
                    'algo' => 'aes-256-cbc',
                    'salt' => base64_encode($salt),
                    'iv' => base64_encode($iv),
                    'ciphertext' => base64_encode($ciphertext),
                );
            }

            return new WP_Error('lsl_encrypt_unavailable', 'No supported encryption library is available on this server.');
        }

        private function decrypt_payload($record, $password) {
            $algo = isset($record['algo']) ? (string) $record['algo'] : '';
            $salt = isset($record['salt']) ? base64_decode((string) $record['salt'], true) : false;
            $iv = isset($record['iv']) ? base64_decode((string) $record['iv'], true) : false;
            $ciphertext = isset($record['ciphertext']) ? base64_decode((string) $record['ciphertext'], true) : false;

            if ($salt === false || $iv === false || $ciphertext === false) {
                return new WP_Error('lsl_decrypt_failed', 'Saved data is corrupted.');
            }

            if ($algo === 'sodium_secretbox' && function_exists('sodium_crypto_secretbox_open') && function_exists('sodium_crypto_pwhash')) {
                $key = sodium_crypto_pwhash(
                    SODIUM_CRYPTO_SECRETBOX_KEYBYTES,
                    $password,
                    $salt,
                    SODIUM_CRYPTO_PWHASH_OPSLIMIT_MODERATE,
                    SODIUM_CRYPTO_PWHASH_MEMLIMIT_MODERATE
                );
                $plain = sodium_crypto_secretbox_open($ciphertext, $iv, $key);
                if (function_exists('sodium_memzero')) {
                    sodium_memzero($key);
                }
                if ($plain === false) {
                    return new WP_Error('lsl_wrong_password', 'Wrong password or damaged saved item.');
                }
            } elseif ($algo === 'aes-256-cbc' && function_exists('openssl_decrypt')) {
                $key = hash_pbkdf2('sha256', $password, $salt, 120000, 32, true);
                $plain = openssl_decrypt($ciphertext, 'aes-256-cbc', $key, OPENSSL_RAW_DATA, $iv);
                if ($plain === false) {
                    return new WP_Error('lsl_wrong_password', 'Wrong password or damaged saved item.');
                }
            } else {
                return new WP_Error('lsl_decrypt_unavailable', 'This server cannot decrypt the saved item with the available libraries.');
            }

            $payload = json_decode($plain, true);
            if (!is_array($payload)) {
                return new WP_Error('lsl_decrypt_failed', 'Decrypted data is invalid.');
            }
            return $payload;
        }

        private function transform_lsl($source, $options) {
            $working = $source;

            if (!empty($options['strip_comments'])) {
                $working = $this->normalize_statement_breaks($working);
                $working = $this->strip_comments($working);
            }

            $mapping = array();
            if (!empty($options['rename_symbols'])) {
                $names = $this->collect_identifiers($working);
                $mapping = $this->build_mapping($names);
                $working = $this->replace_identifiers_outside_strings($working, $mapping);
            }

            if (!empty($options['trim_whitespace'])) {
                $working = $this->trim_whitespace($working);
            }

            if (!empty($options['pack_one_line'])) {
                $working = $this->pack_one_line($working);
            }

            if (!empty($options['fake_comments'])) {
                $working = $this->inject_fake_comments($working);
            }

            return array(
                'output' => $working,
                'mapping' => $mapping,
            );
        }

        private function normalize_statement_breaks($src) {
            $out = '';
            $len = strlen($src);
            $in_string = false;
            $paren_depth = 0;

            for ($i = 0; $i < $len; $i++) {
                $ch = $src[$i];

                if ($in_string) {
                    $out .= $ch;
                    if ($ch === '\\' && $i + 1 < $len) {
                        $out .= $src[$i + 1];
                        $i++;
                        continue;
                    }
                    if ($ch === '"') {
                        $in_string = false;
                    }
                    continue;
                }

                if ($ch === '"') {
                    $in_string = true;
                    $out .= $ch;
                    continue;
                }

                if ($ch === '(') {
                    $paren_depth++;
                } elseif ($ch === ')') {
                    $paren_depth = max(0, $paren_depth - 1);
                }

                $out .= $ch;
                if ($ch === ';' && $paren_depth === 0) {
                    $next = ($i + 1 < $len) ? $src[$i + 1] : '';
                    if ($next !== "\n" && $next !== "\r") {
                        $out .= "\n";
                    }
                }
            }

            return $out;
        }

        private function strip_comments($src) {
            $out = '';
            $len = strlen($src);
            $i = 0;
            $in_string = false;

            while ($i < $len) {
                $ch = $src[$i];
                $next = ($i + 1 < $len) ? $src[$i + 1] : '';

                if ($in_string) {
                    $out .= $ch;
                    if ($ch === '\\' && $i + 1 < $len) {
                        $out .= $src[$i + 1];
                        $i += 2;
                        continue;
                    }
                    if ($ch === '"') {
                        $in_string = false;
                    }
                    $i++;
                    continue;
                }

                if ($ch === '"') {
                    $in_string = true;
                    $out .= $ch;
                    $i++;
                    continue;
                }

                if ($ch === '/' && $next === '/') {
                    $i += 2;
                    while ($i < $len && $src[$i] !== "\n") {
                        $i++;
                    }
                    continue;
                }

                if ($ch === '/' && $next === '*') {
                    $i += 2;
                    while ($i + 1 < $len && !($src[$i] === '*' && $src[$i + 1] === '/')) {
                        $i++;
                    }
                    if ($i + 1 < $len) {
                        $i += 2;
                    }
                    continue;
                }

                $out .= $ch;
                $i++;
            }

            return $out;
        }

        private function trim_whitespace($src) {
            $lines = preg_split('/\R/', $src);
            $clean = array();
            if (!is_array($lines)) {
                return trim($src);
            }
            foreach ($lines as $line) {
                $line = trim($line);
                if ($line !== '') {
                    $clean[] = $line;
                }
            }
            return implode("\n", $clean);
        }

        private function pack_one_line($src) {
            return str_replace(array("\r", "\n"), '', $src);
        }

        private function inject_fake_comments($src) {
            $out = '';
            $len = strlen($src);
            $in_string = false;
            $counter = 0;
            $tokens = array('/*01*/', '/*A2*/', '/*X3*/', '/*Q4*/', '/*Z5*/');

            for ($i = 0; $i < $len; $i++) {
                $ch = $src[$i];
                $out .= $ch;

                if ($in_string) {
                    if ($ch === '\\' && $i + 1 < $len) {
                        $out .= $src[$i + 1];
                        $i++;
                        continue;
                    }
                    if ($ch === '"') {
                        $in_string = false;
                    }
                    continue;
                }

                if ($ch === '"') {
                    $in_string = true;
                    continue;
                }

                if ($ch === ';' || $ch === '{' || $ch === '}') {
                    $counter++;
                    if ($counter % 2 === 0) {
                        $out .= $tokens[$counter % count($tokens)];
                    }
                }
            }

            return $out;
        }

        private function collect_identifiers($src) {
            $identifiers = array();

            if (preg_match_all('/\bstate\s+([A-Za-z_]\w*)\b/', $src, $matches)) {
                foreach ($matches[1] as $name) {
                    if ($name !== 'default' && $this->is_user_symbol($name)) {
                        $identifiers[$name] = true;
                    }
                }
            }

            $function_pattern = '/(?<!\w)(?:(integer|float|string|key|vector|rotation|list|quaternion)\s+)?([A-Za-z_]\w*)\s*\(([^;{}]*)\)\s*\{/m';
            if (preg_match_all($function_pattern, $src, $matches, PREG_SET_ORDER)) {
                foreach ($matches as $match) {
                    $func_name = $match[2];
                    $params = isset($match[3]) ? $match[3] : '';
                    if (!in_array($func_name, $this->events, true) && $this->is_user_symbol($func_name)) {
                        $identifiers[$func_name] = true;
                    }
                    if (preg_match_all('/\b(?:integer|float|string|key|vector|rotation|list|quaternion)\s+([A-Za-z_]\w*)\b/', $params, $param_matches)) {
                        foreach ($param_matches[1] as $param_name) {
                            if ($this->is_user_symbol($param_name)) {
                                $identifiers[$param_name] = true;
                            }
                        }
                    }
                }
            }

            $var_pattern = '/(?<!\w)(integer|float|string|key|vector|rotation|list|quaternion)\s+([^;{}()]+);/';
            if (preg_match_all($var_pattern, $src, $matches, PREG_SET_ORDER)) {
                foreach ($matches as $match) {
                    $declared = $match[2];
                    $names = $this->collect_declared_names_from_statement($declared);
                    foreach ($names as $name) {
                        $identifiers[$name] = true;
                    }
                }
            }

            if (preg_match_all('/\bfor\s*\(\s*([A-Za-z_]\w*)\s*=/', $src, $matches)) {
                foreach ($matches[1] as $name) {
                    if ($this->is_user_symbol($name)) {
                        $identifiers[$name] = true;
                    }
                }
            }

            $names = array_keys($identifiers);
            usort($names, array($this, 'sort_names'));
            return $names;
        }

        public function sort_names($a, $b) {
            $len_cmp = strlen($b) - strlen($a);
            if ($len_cmp !== 0) {
                return $len_cmp;
            }
            return strcmp($a, $b);
        }

        private function collect_declared_names_from_statement($statement) {
            $out = array();
            $pieces = $this->split_top_level_commas($statement);
            foreach ($pieces as $piece) {
                $piece = trim($piece);
                if ($piece === '') {
                    continue;
                }
                $parts = explode('=', $piece, 2);
                $part = trim($parts[0]);
                if (preg_match('/^([A-Za-z_]\w*)$/', $part, $match)) {
                    $name = $match[1];
                    if ($this->is_user_symbol($name)) {
                        $out[] = $name;
                    }
                }
            }
            return $out;
        }

        private function split_top_level_commas($text) {
            $parts = array();
            $current = '';
            $depth = 0;
            $in_string = false;
            $len = strlen($text);

            for ($i = 0; $i < $len; $i++) {
                $ch = $text[$i];
                if ($in_string) {
                    $current .= $ch;
                    if ($ch === '\\' && $i + 1 < $len) {
                        $current .= $text[$i + 1];
                        $i++;
                        continue;
                    }
                    if ($ch === '"') {
                        $in_string = false;
                    }
                    continue;
                }

                if ($ch === '"') {
                    $in_string = true;
                    $current .= $ch;
                    continue;
                }

                if (strpos('([<{', $ch) !== false) {
                    $depth++;
                } elseif (strpos(')]>}', $ch) !== false) {
                    $depth = max(0, $depth - 1);
                }

                if ($ch === ',' && $depth === 0) {
                    $parts[] = $current;
                    $current = '';
                    continue;
                }

                $current .= $ch;
            }

            if ($current !== '') {
                $parts[] = $current;
            }
            return $parts;
        }

        private function build_mapping($names) {
            $mapping = array();
            $index = 1;
            foreach ($names as $name) {
                $mapping[$name] = $this->make_obfuscated_name($index);
                $index++;
            }
            return $mapping;
        }

        private function make_obfuscated_name($index) {
            return '_' . str_pad((string) $index, 6, '0', STR_PAD_LEFT);
        }

        private function replace_identifiers_outside_strings($src, $mapping) {
            $out = '';
            $len = strlen($src);
            $i = 0;
            $in_string = false;

            while ($i < $len) {
                $ch = $src[$i];

                if ($in_string) {
                    $out .= $ch;
                    if ($ch === '\\' && $i + 1 < $len) {
                        $out .= $src[$i + 1];
                        $i += 2;
                        continue;
                    }
                    if ($ch === '"') {
                        $in_string = false;
                    }
                    $i++;
                    continue;
                }

                if ($ch === '"') {
                    $in_string = true;
                    $out .= $ch;
                    $i++;
                    continue;
                }

                if (preg_match('/[A-Za-z_]/', $ch)) {
                    $j = $i + 1;
                    while ($j < $len && preg_match('/[A-Za-z0-9_]/', $src[$j])) {
                        $j++;
                    }
                    $word = substr($src, $i, $j - $i);
                    $out .= isset($mapping[$word]) ? $mapping[$word] : $word;
                    $i = $j;
                    continue;
                }

                $out .= $ch;
                $i++;
            }

            return $out;
        }

        private function is_user_symbol($name) {
            if ($name === '') {
                return false;
            }
            if (in_array($name, $this->keywords, true) || in_array($name, $this->events, true)) {
                return false;
            }
            if (strpos($name, 'll') === 0 || strpos($name, 'os') === 0) {
                return false;
            }
            return true;
        }

        private function get_css() {
            return '.lsl-protector-wrap{display:grid;gap:18px}.lsl-protector-card{background:#121720;border:1px solid #273041;border-radius:18px;padding:18px;color:#edf2ff;box-shadow:0 12px 34px rgba(0,0,0,.18)}.lsl-protector-card h2,.lsl-protector-card h3{margin:0 0 10px}.lsl-protector-muted{color:#aab7cf;line-height:1.5}.lsl-protector-form{display:grid;gap:14px}.lsl-protector-grid{display:grid;grid-template-columns:1fr 1fr;gap:14px}.lsl-protector-grid-2{grid-template-columns:1fr 1fr}.lsl-protector-wrap label{display:block;font-weight:600;margin-bottom:6px}.lsl-protector-wrap input[type=text],.lsl-protector-wrap input[type=password],.lsl-protector-wrap select,.lsl-protector-wrap textarea{width:100%;background:#1b2330;color:#edf2ff;border:1px solid #2e3a50;border-radius:12px;padding:12px}.lsl-protector-wrap textarea{min-height:300px;resize:vertical;font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:13px;line-height:1.45}.lsl-protector-options{display:flex;gap:14px;flex-wrap:wrap}.lsl-protector-options label{margin:0;font-weight:500}.lsl-protector-actions{display:flex;gap:10px;flex-wrap:wrap}.lsl-protector-actions button{background:linear-gradient(180deg,#1e90ff,#1877f2);color:#fff;border:0;border-radius:12px;padding:11px 16px;font-weight:700;cursor:pointer}.lsl-protector-alert{padding:12px 14px;border-radius:12px;margin-bottom:12px}.lsl-protector-ok{background:#12321f;border:1px solid #26563a;color:#b5f1c2}.lsl-protector-error{background:#3a1418;border:1px solid #7b2933;color:#ffc4cb}.lsl-protector-notice{padding:14px 16px;border-radius:12px;background:#1b2330;border:1px solid #2e3a50;color:#edf2ff}@media (max-width:900px){.lsl-protector-grid,.lsl-protector-grid-2{grid-template-columns:1fr}}';
        }
    }

    register_activation_hook(__FILE__, array('LSL_Protector_Workspace', 'activate_static'));
    new LSL_Protector_Workspace();
}

/**
 * Example premium gate for Paid Memberships Pro:
 * add_filter('lsl_protector_user_can_use', function ($allowed, $user_id) {
 *     return function_exists('pmpro_hasMembershipLevel') && pmpro_hasMembershipLevel(null, $user_id);
 * }, 10, 2);
 */

/**
 * Example premium gate by role:
 * add_filter('lsl_protector_user_can_use', function ($allowed, $user_id) {
 *     $user = get_userdata($user_id);
 *     return $user && in_array('premium_member', (array) $user->roles, true);
 * }, 10, 2);
 */
