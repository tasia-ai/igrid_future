<?php
/**
 * Plugin Name: OpenSim Presence ↔ GridUser Sync
 * Description: Keeps GridUser.Online/Login/Logout in sync with Presence heartbeats. Includes stale cleanup and a 5‑minute WP‑Cron.
 * Version:     1.0.0
 * Author:      Tasia for Marty
 */

if (!defined('ABSPATH')) { exit; }

class OpenSim_Presence_Bridge {
    const OPTION_KEY = 'opensim_presence_bridge_options';
    const CRON_HOOK  = 'opensim_presence_bridge_cron';

    public static function defaults() {
        return [
            'db_host'        => 'i.let-us.cyou',
            'db_name'        => 'robust',
            'db_user'        => 'root',
            'db_pass'        => 'CHANGE_ME_DB_PASSWORD',
            'presence_table' => 'Presence',
            'griduser_table' => 'GridUser',
            'stale_seconds'  => 600,  // 2h
            'recent_window'  => 120,   // 5m considers user online
            'enable_cleanup' => 1,     // delete stale Presence rows
            'dry_run'        => 0,     // if 1, only logs actions
        ];
    }

    public static function init() {
        // Admin UI
        add_action('admin_menu', [__CLASS__, 'admin_menu']);
        add_action('admin_init', [__CLASS__, 'register_settings']);

        // Cron schedule every 5 minutes
        add_filter('cron_schedules', function($s) {
            if (!isset($s['every_five_minutes'])) {
                $s['every_five_minutes'] = [
                    'interval' => 5 * 60,
                    'display'  => __('Every 5 Minutes', 'opensim')
                ];
            }
            return $s;
        });

        // Cron hook
        add_action(self::CRON_HOOK, [__CLASS__, 'run_sync']);

        // Activation/Deactivation
        register_activation_hook(__FILE__, [__CLASS__, 'activate']);
        register_deactivation_hook(__FILE__, [__CLASS__, 'deactivate']);
    }

    public static function activate() {
        if (!wp_next_scheduled(self::CRON_HOOK)) {
            wp_schedule_event(time() + 60, 'every_five_minutes', self::CRON_HOOK);
        }
    }

    public static function deactivate() {
        $ts = wp_next_scheduled(self::CRON_HOOK);
        if ($ts) wp_unschedule_event($ts, self::CRON_HOOK);
    }

    public static function admin_menu() {
        add_options_page(
            'OpenSim Presence Sync',
            'OpenSim Presence Sync',
            'manage_options',
            'opensim-presence-bridge',
            [__CLASS__, 'render_settings_page']
        );
    }

    public static function register_settings() {
        register_setting(self::OPTION_KEY, self::OPTION_KEY, [
            'type' => 'array',
            'sanitize_callback' => [__CLASS__, 'sanitize_options'],
            'default' => self::defaults(),
        ]);
    }

    public static function sanitize_options($input) {
        $d = self::defaults();
        $out = [
            'db_host'        => sanitize_text_field($input['db_host'] ?? $d['db_host']),
            'db_name'        => sanitize_text_field($input['db_name'] ?? $d['db_name']),
            'db_user'        => sanitize_text_field($input['db_user'] ?? $d['db_user']),
            'db_pass'        => $input['db_pass'] ?? $d['db_pass'], // keep as‑is
            'presence_table' => sanitize_text_field($input['presence_table'] ?? $d['presence_table']),
            'griduser_table' => sanitize_text_field($input['griduser_table'] ?? $d['griduser_table']),
            'stale_seconds'  => max(60, intval($input['stale_seconds'] ?? $d['stale_seconds'])),
            'recent_window'  => max(60, intval($input['recent_window'] ?? $d['recent_window'])),
            'enable_cleanup' => isset($input['enable_cleanup']) ? 1 : 0,
            'dry_run'        => isset($input['dry_run']) ? 1 : 0,
        ];
        return $out;
    }

    private static function get_options() {
        $opts = get_option(self::OPTION_KEY, []);
        return wp_parse_args($opts, self::defaults());
    }

    private static function pdo(&$err = null) {
        $o = self::get_options();
        $dsn = sprintf('mysql:host=%s;dbname=%s;charset=utf8mb4', $o['db_host'], $o['db_name']);
        try {
            $pdo = new PDO($dsn, $o['db_user'], $o['db_pass'], [
                PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
                PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
            ]);
            // Make sure we are in UTC for epoch comparisons
            $pdo->exec("SET time_zone = '+00:00'");
            return $pdo;
        } catch (Throwable $e) {
            $err = $e->getMessage();
            error_log('[OpenSim Bridge] DB connect failed: ' . $err);
            return null;
        }
    }

    public static function render_settings_page() {
        if (!current_user_can('manage_options')) return;
        $opts = self::get_options();

        if (isset($_POST['opensim_presence_bridge_run_now']) && check_admin_referer('opensim_presence_bridge_run_now')) {
            self::run_sync();
            echo '<div class="updated"><p>Sync executed. Check debug.log for details.</p></div>';
        }

        ?>
        <div class="wrap">
            <h1>OpenSim Presence ↔ GridUser Sync</h1>
            <form method="post" action="options.php">
                <?php settings_fields(self::OPTION_KEY); ?>
                <?php $o = esc_html__('', 'opensim'); ?>
                <?php $opts = self::get_options(); ?>

                <table class="form-table" role="presentation">
                    <tr><th scope="row">DB Host</th>
                        <td><input type="text" name="<?php echo self::OPTION_KEY; ?>[db_host]" value="<?php echo esc_attr($opts['db_host']); ?>" class="regular-text" /></td></tr>
                    <tr><th scope="row">DB Name</th>
                        <td><input type="text" name="<?php echo self::OPTION_KEY; ?>[db_name]" value="<?php echo esc_attr($opts['db_name']); ?>" class="regular-text" /></td></tr>
                    <tr><th scope="row">DB User</th>
                        <td><input type="text" name="<?php echo self::OPTION_KEY; ?>[db_user]" value="<?php echo esc_attr($opts['db_user']); ?>" class="regular-text" /></td></tr>
                    <tr><th scope="row">DB Password</th>
                        <td><input type="password" name="<?php echo self::OPTION_KEY; ?>[db_pass]" value="<?php echo esc_attr($opts['db_pass']); ?>" class="regular-text" autocomplete="new-password" /></td></tr>
                    <tr><th scope="row">Presence Table</th>
                        <td><input type="text" name="<?php echo self::OPTION_KEY; ?>[presence_table]" value="<?php echo esc_attr($opts['presence_table']); ?>" class="regular-text" /></td></tr>
                    <tr><th scope="row">GridUser Table</th>
                        <td><input type="text" name="<?php echo self::OPTION_KEY; ?>[griduser_table]" value="<?php echo esc_attr($opts['griduser_table']); ?>" class="regular-text" /></td></tr>
                    <tr><th scope="row">Stale Seconds (cleanup)</th>
                        <td><input type="number" min="60" name="<?php echo self::OPTION_KEY; ?>[stale_seconds]" value="<?php echo esc_attr($opts['stale_seconds']); ?>" /></td></tr>
                    <tr><th scope="row">Online Window Seconds</th>
                        <td><input type="number" min="60" name="<?php echo self::OPTION_KEY; ?>[recent_window]" value="<?php echo esc_attr($opts['recent_window']); ?>" /></td></tr>
                    <tr><th scope="row">Options</th>
                        <td>
                            <label><input type="checkbox" name="<?php echo self::OPTION_KEY; ?>[enable_cleanup]" <?php checked($opts['enable_cleanup'], 1); ?>/> Delete stale Presence rows</label><br/>
                            <label><input type="checkbox" name="<?php echo self::OPTION_KEY; ?>[dry_run]" <?php checked($opts['dry_run'], 1); ?>/> Dry‑run (log only)</label>
                        </td>
                    </tr>
                </table>
                <?php submit_button('Save Settings'); ?>
            </form>

            <form method="post" style="margin-top:1rem;">
                <?php wp_nonce_field('opensim_presence_bridge_run_now'); ?>
                <input type="hidden" name="opensim_presence_bridge_run_now" value="1" />
                <?php submit_button('Run Sync Now', 'secondary'); ?>
            </form>
        </div>
        <?php
    }

    /**
     * Core sync logic:
     * 1) Optionally delete stale Presence rows older than stale_seconds.
     * 2) Compute currently online users = Presence.LastSeen within recent_window.
     * 3) GridUser: set Online=1 (+Login timestamp) for online set.
     * 4) GridUser: set Online=0 (+Logout timestamp) for those previously online but not in the set.
     */
    public static function run_sync() {
        $o = self::get_options();
        $err = null;
        $pdo = self::pdo($err);
        if (!$pdo) return;

        $presence = preg_replace('/[^A-Za-z0-9_]/', '', $o['presence_table']);
        $griduser = preg_replace('/[^A-Za-z0-9_]/', '', $o['griduser_table']);

        $now = time();
        $cutoff_stale  = $now - intval($o['stale_seconds']);
        $cutoff_recent = $now - intval($o['recent_window']);

        try {
            if ($o['enable_cleanup']) {
                $sql = "DELETE FROM `$presence` WHERE `LastSeen` < :cutoff";
                if ($o['dry_run']) {
                    error_log("[OpenSim Bridge] DRY‑RUN: would DELETE stale Presence before " . $cutoff_stale);
                } else {
                    $stmt = $pdo->prepare($sql);
                    $stmt->execute([':cutoff' => $cutoff_stale]);
                    error_log('[OpenSim Bridge] Deleted stale Presence rows: ' . $stmt->rowCount());
                }
            }

            // Pull current online set from Presence
            $stmt = $pdo->prepare("SELECT `UserID` FROM `$presence` WHERE `LastSeen` >= :recent GROUP BY `UserID`");
            $stmt->execute([':recent' => $cutoff_recent]);
            $onlineIds = $stmt->fetchAll(PDO::FETCH_COLUMN, 0) ?: [];

            // Build temp tables safely for set operations
            $pdo->exec("CREATE TEMPORARY TABLE tmp_online (UserID CHAR(36) PRIMARY KEY) ENGINE=Memory");
            if (!empty($onlineIds)) {
                $ins = $pdo->prepare("INSERT INTO tmp_online (UserID) VALUES (?)");
                foreach ($onlineIds as $uid) { $ins->execute([$uid]); }
            }

            // Mark online users in GridUser
            $sqlOnline = "UPDATE `$griduser` gu
                           JOIN tmp_online t ON t.UserID = gu.UserID
                           SET gu.Online = 1,
                               gu.Login  = IFNULL(GREATEST(gu.Login, UNIX_TIMESTAMP()), UNIX_TIMESTAMP())";
            if ($o['dry_run']) {
                error_log('[OpenSim Bridge] DRY‑RUN: would UPDATE GridUser -> Online=1 for ' . count($onlineIds) . ' user(s)');
            } else {
                $n = $pdo->exec($sqlOnline);
                error_log('[OpenSim Bridge] Updated GridUser Online=1 rows: ' . intval($n));
            }

            // Mark offline: GridUser rows that are Online=1 but NOT in tmp_online
            $sqlOffline = "UPDATE `$griduser` gu
                            LEFT JOIN tmp_online t ON t.UserID = gu.UserID
                            SET gu.Online = 0, gu.Logout = UNIX_TIMESTAMP()
                            WHERE gu.Online = 1 AND t.UserID IS NULL";
            if ($o['dry_run']) {
                error_log('[OpenSim Bridge] DRY‑RUN: would UPDATE GridUser -> Online=0 for previously online users not present');
            } else {
                $n2 = $pdo->exec($sqlOffline);
                error_log('[OpenSim Bridge] Updated GridUser Online=0 rows: ' . intval($n2));
            }

            // Cleanup temp table
            $pdo->exec('DROP TEMPORARY TABLE IF EXISTS tmp_online');

        } catch (Throwable $e) {
            error_log('[OpenSim Bridge] Sync error: ' . $e->getMessage());
        }
    }
}

OpenSim_Presence_Bridge::init();