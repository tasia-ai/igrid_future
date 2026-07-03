<?php
/*
Plugin Name: Tasia OpenSim Password Reset
Description: WordPress shortcode for OpenSim password reset with terminal-style AI UI.
Version: 1.0.0
Author: Tasia
*/

if (!defined('ABSPATH')) {
    exit;
}

final class Tasia_OpenSim_Password_Reset
{
    private const SHORTCODE = 'tasia_password_reset';
    private const AJAX_ACTION = 'tasia_password_reset';
    private const TOKEN_EXPIRY_SECONDS = 900; // 15 minutes

    public function __construct()
    {
        add_shortcode(self::SHORTCODE, [$this, 'render_shortcode']);
        add_action('wp_ajax_' . self::AJAX_ACTION, [$this, 'handle_ajax']);
        add_action('wp_ajax_nopriv_' . self::AJAX_ACTION, [$this, 'handle_ajax']);
    }

    public static function activate(): void
    {
        global $wpdb;

        require_once ABSPATH . 'wp-admin/includes/upgrade.php';

        $table = self::get_tokens_table_name();
        $charset = $wpdb->get_charset_collate();

        $sql = "CREATE TABLE {$table} (
            principal_id VARCHAR(36) NOT NULL,
            reset_code VARCHAR(6) NOT NULL,
            created_at DATETIME NOT NULL,
            PRIMARY KEY (principal_id),
            KEY created_at (created_at)
        ) {$charset};";

        dbDelta($sql);
    }

    private static function get_tokens_table_name(): string
    {
        global $wpdb;
        return $wpdb->prefix . 'tasia_reset_tokens';
    }

    private function get_opensim_db(): mysqli
    {
        static $db = null;

        if ($db instanceof mysqli) {
            return $db;
        }

        $host = defined('TASIA_OPENSIM_DB_HOST') ? TASIA_OPENSIM_DB_HOST : '';
        $name = defined('TASIA_OPENSIM_DB_NAME') ? TASIA_OPENSIM_DB_NAME : '';
        $user = defined('TASIA_OPENSIM_DB_USER') ? TASIA_OPENSIM_DB_USER : '';
        $pass = defined('TASIA_OPENSIM_DB_PASS') ? TASIA_OPENSIM_DB_PASS : '';

        if ($host === '' || $name === '' || $user === '') {
            throw new RuntimeException('OpenSim DB constants are missing.');
        }

        mysqli_report(MYSQLI_REPORT_OFF);

        $db = @new mysqli($host, $user, $pass, $name);

        if ($db->connect_error) {
            throw new RuntimeException('Could not connect to OpenSim database.');
        }

        $db->set_charset('utf8mb4');

        return $db;
    }

    private function maybe_log(string $message): void
    {
        if (defined('WP_DEBUG') && WP_DEBUG) {
            error_log('[Tasia Reset] ' . $message);
        }
    }

    private function cleanup_old_tokens(): void
    {
        global $wpdb;

        $table = self::get_tokens_table_name();
        $cutoff = gmdate('Y-m-d H:i:s', time() - 86400);

        $wpdb->query(
            $wpdb->prepare(
                "DELETE FROM {$table} WHERE created_at < %s",
                $cutoff
            )
        );
    }

    private function find_user(mysqli $db, string $first, string $last): ?array
    {
        $stmt = $db->prepare("
            SELECT PrincipalID, Email
            FROM UserAccounts
            WHERE FirstName = ? AND LastName = ?
            LIMIT 1
        ");

        if (!$stmt) {
            throw new RuntimeException('Failed to prepare user lookup.');
        }

        $stmt->bind_param('ss', $first, $last);
        $stmt->execute();

        $result = $stmt->get_result();
        $user = $result ? $result->fetch_assoc() : null;

        $stmt->close();

        return $user ?: null;
    }

    private function mask_email(string $email): string
    {
        $email = trim($email);

        if (!str_contains($email, '@')) {
            return '***';
        }

        [$local, $domain] = explode('@', $email, 2);

        $localMasked = mb_substr($local, 0, 2) . str_repeat('*', max(1, mb_strlen($local) - 2));

        return $localMasked . '@' . $domain;
    }

    private function store_code(string $principalId, string $code): void
    {
        global $wpdb;

        $table = self::get_tokens_table_name();

        $wpdb->replace(
            $table,
            [
                'principal_id' => $principalId,
                'reset_code'   => $code,
                'created_at'   => gmdate('Y-m-d H:i:s'),
            ],
            ['%s', '%s', '%s']
        );
    }

    private function get_code_row(string $principalId): ?array
    {
        global $wpdb;

        $table = self::get_tokens_table_name();

        $row = $wpdb->get_row(
            $wpdb->prepare(
                "SELECT principal_id, reset_code, created_at
                 FROM {$table}
                 WHERE principal_id = %s
                 LIMIT 1",
                $principalId
            ),
            ARRAY_A
        );

        return $row ?: null;
    }

    private function delete_code(string $principalId): void
    {
        global $wpdb;

        $table = self::get_tokens_table_name();

        $wpdb->delete(
            $table,
            ['principal_id' => $principalId],
            ['%s']
        );
    }

    private function send_reset_email(string $to, string $firstName, string $resetCode): bool
    {
        $subject = 'OpenSim Password Reset Code';

        $message = "Hello {$firstName},\n\n";
        $message .= "Your OpenSim password reset code is: {$resetCode}\n\n";
        $message .= "This code will expire in 15 minutes.\n";
        $message .= "If you did not request this password reset, please ignore this email.\n\n";
        $message .= "Best regards,\n";
        $message .= "Tasia AI Support";

        $fromEmail = defined('TASIA_RESET_FROM_EMAIL') && TASIA_RESET_FROM_EMAIL !== ''
            ? TASIA_RESET_FROM_EMAIL
            : get_option('admin_email');

        $fromName = defined('TASIA_RESET_FROM_NAME') && TASIA_RESET_FROM_NAME !== ''
            ? TASIA_RESET_FROM_NAME
            : wp_specialchars_decode(get_bloginfo('name'), ENT_QUOTES);

        $smtpHost = defined('TASIA_RESET_SMTP_HOST') ? TASIA_RESET_SMTP_HOST : '';
        $smtpPort = defined('TASIA_RESET_SMTP_PORT') ? (int) TASIA_RESET_SMTP_PORT : 587;
        $smtpUser = defined('TASIA_RESET_SMTP_USER') ? TASIA_RESET_SMTP_USER : '';
        $smtpPass = defined('TASIA_RESET_SMTP_PASS') ? TASIA_RESET_SMTP_PASS : '';
        $smtpSecure = defined('TASIA_RESET_SMTP_SECURE') ? TASIA_RESET_SMTP_SECURE : 'tls';

        if ($smtpHost !== '' && $smtpUser !== '' && $smtpPass !== '') {
            try {
                if (!class_exists('\PHPMailer\PHPMailer\PHPMailer')) {
                    require_once ABSPATH . WPINC . '/PHPMailer/PHPMailer.php';
                    require_once ABSPATH . WPINC . '/PHPMailer/SMTP.php';
                    require_once ABSPATH . WPINC . '/PHPMailer/Exception.php';
                }

                $mail = new \PHPMailer\PHPMailer\PHPMailer(true);
                $mail->CharSet = 'UTF-8';
                $mail->isSMTP();
                $mail->Host = $smtpHost;
                $mail->Port = $smtpPort;
                $mail->SMTPAuth = true;
                $mail->Username = $smtpUser;
                $mail->Password = $smtpPass;

                if ($smtpSecure !== '') {
                    $mail->SMTPSecure = $smtpSecure;
                }

                $mail->setFrom($fromEmail, $fromName);
                $mail->addReplyTo($fromEmail, $fromName);
                $mail->addAddress($to);
                $mail->Subject = $subject;
                $mail->Body = $message;
                $mail->isHTML(false);

                return $mail->send();
            } catch (Throwable $e) {
                $this->maybe_log('SMTP send failed: ' . $e->getMessage());
                return false;
            }
        }

        $headers = [
            'Content-Type: text/plain; charset=UTF-8',
            'From: ' . $fromName . ' <' . $fromEmail . '>',
            'Reply-To: ' . $fromEmail,
        ];

        return wp_mail($to, $subject, $message, $headers);
    }

    private function update_opensim_password(mysqli $db, string $principalId, string $newPassword): bool
    {
        $salt = md5(uniqid((string) wp_rand(), true));
        $hash = md5(md5($newPassword) . ':' . $salt);

        $stmt = $db->prepare("
            UPDATE auth
            SET passwordSalt = ?, passwordHash = ?
            WHERE UUID = ?
            LIMIT 1
        ");

        if (!$stmt) {
            throw new RuntimeException('Failed to prepare password update.');
        }

        $stmt->bind_param('sss', $salt, $hash, $principalId);
        $ok = $stmt->execute();
        $affected = $stmt->affected_rows;
        $stmt->close();

        return $ok && $affected >= 0;
    }

    public function handle_ajax(): void
    {
        check_ajax_referer('tasia_password_reset_nonce', 'nonce');

        $mode = sanitize_text_field(wp_unslash($_POST['mode'] ?? ''));
        $first = trim(sanitize_text_field(wp_unslash($_POST['first'] ?? '')));
        $last  = trim(sanitize_text_field(wp_unslash($_POST['last'] ?? '')));

        try {
            $this->cleanup_old_tokens();
            $db = $this->get_opensim_db();

            if ($mode === 'request') {
                if ($first === '' || $last === '') {
                    wp_send_json_error(['message' => 'Please provide first and last name.'], 400);
                }

                $user = $this->find_user($db, $first, $last);

                if (!$user) {
                    wp_send_json_error(['message' => "User '{$first} {$last}' not found."], 404);
                }

                $email = trim((string) ($user['Email'] ?? ''));

                if ($email === '' || !is_email($email)) {
                    wp_send_json_error(['message' => 'No valid email address found for this user.'], 400);
                }

                $existing = $this->get_code_row($user['PrincipalID']);
                if ($existing) {
                    $existingTime = strtotime($existing['created_at'] . ' UTC');
                    if ($existingTime !== false && (time() - $existingTime) < 60) {
                        wp_send_json_error(['message' => 'A reset code was requested very recently. Please wait a minute and try again.'], 429);
                    }
                }

                $code = (string) random_int(100000, 999999);
                $this->store_code($user['PrincipalID'], $code);

                $sent = $this->send_reset_email($email, $first, $code);

                if (!$sent) {
                    $this->delete_code($user['PrincipalID']);
                    wp_send_json_error(['message' => 'Could not send reset email. Please check SMTP or mail configuration.'], 500);
                }

                wp_send_json_success([
                    'message' => 'Reset code sent to ' . $this->mask_email($email),
                ]);
            }

            if ($mode === 'confirm') {
                $code = trim((string) wp_unslash($_POST['code'] ?? ''));
                $newPassword = (string) wp_unslash($_POST['newPassword'] ?? '');

                if ($first === '' || $last === '' || $code === '' || $newPassword === '') {
                    wp_send_json_error(['message' => 'Missing required fields.'], 400);
                }

                if (!preg_match('/^\d{6}$/', $code)) {
                    wp_send_json_error(['message' => 'Reset code must be 6 digits.'], 400);
                }

                if (strlen($newPassword) < 6) {
                    wp_send_json_error(['message' => 'Password must be at least 6 characters long.'], 400);
                }

                $user = $this->find_user($db, $first, $last);

                if (!$user) {
                    wp_send_json_error(['message' => 'User not found.'], 404);
                }

                $row = $this->get_code_row($user['PrincipalID']);

                if (!$row) {
                    wp_send_json_error(['message' => 'No active reset code found.'], 404);
                }

                $createdTs = strtotime($row['created_at'] . ' UTC');
                if ($createdTs === false || (time() - $createdTs) > self::TOKEN_EXPIRY_SECONDS) {
                    $this->delete_code($user['PrincipalID']);
                    wp_send_json_error(['message' => 'Reset code expired. Please request a new one.'], 400);
                }

                if (!hash_equals((string) $row['reset_code'], $code)) {
                    wp_send_json_error(['message' => 'Invalid reset code.'], 400);
                }

                $updated = $this->update_opensim_password($db, $user['PrincipalID'], $newPassword);

                if (!$updated) {
                    wp_send_json_error(['message' => 'Failed to update password.'], 500);
                }

                $this->delete_code($user['PrincipalID']);

                wp_send_json_success([
                    'message' => 'Password reset successfully.',
                ]);
            }

            wp_send_json_error(['message' => 'Unknown action.'], 400);
        } catch (Throwable $e) {
            $this->maybe_log($e->getMessage());
            wp_send_json_error(['message' => 'System error. Check plugin configuration and debug log.'], 500);
        }
    }

    public function render_shortcode(): string
    {
        $id = 'tasia-reset-' . wp_rand(1000, 999999);
        $ajaxUrl = admin_url('admin-ajax.php');
        $nonce = wp_create_nonce('tasia_password_reset_nonce');

        ob_start();
        ?>
        <div id="<?php echo esc_attr($id); ?>" class="tasia-reset-wrap">
            <div class="tasia-ai-shell">
                <div class="tasia-ai-header">
                    <span class="tasia-dot"></span>
                    <span class="tasia-title">Tasia AI // OpenSim Password Reset</span>
                    <span class="tasia-status">ONLINE</span>
                </div>

                <div class="tasia-terminal" aria-live="polite"></div>

                <div class="tasia-input-row">
                    <span class="tasia-prompt">:</span>
                    <input
                        type="text"
                        class="tasia-cmdline"
                        autocomplete="off"
                        spellcheck="false"
                        aria-label="Tasia password reset input"
                    />
                </div>

                <noscript>
                    <div class="tasia-noscript">JavaScript is required for this password reset terminal.</div>
                </noscript>
            </div>
        </div>

        <style>
            #<?php echo esc_html($id); ?>.tasia-reset-wrap {
                width: 100%;
                max-width: 760px;
                margin: 24px auto;
            }

            #<?php echo esc_html($id); ?> .tasia-ai-shell {
                position: relative;
                overflow: hidden;
                border-radius: 20px;
                border: 1px solid rgba(0, 255, 200, 0.35);
                background:
                    radial-gradient(circle at top, rgba(0, 255, 200, 0.12), transparent 35%),
                    linear-gradient(180deg, rgba(5, 12, 18, 0.98), rgba(2, 6, 10, 0.98));
                box-shadow:
                    0 0 0 1px rgba(0,255,200,0.08) inset,
                    0 0 28px rgba(0,255,200,0.15),
                    0 20px 60px rgba(0,0,0,0.45);
                color: #b7fff1;
                font-family: Consolas, Monaco, Menlo, monospace;
                padding: 0;
            }

            #<?php echo esc_html($id); ?> .tasia-ai-shell::before {
                content: "";
                position: absolute;
                inset: 0;
                pointer-events: none;
                background:
                    repeating-linear-gradient(
                        180deg,
                        rgba(255,255,255,0.035) 0px,
                        rgba(255,255,255,0.035) 1px,
                        transparent 2px,
                        transparent 4px
                    );
                opacity: 0.2;
            }

            #<?php echo esc_html($id); ?> .tasia-ai-header {
                display: flex;
                align-items: center;
                gap: 12px;
                padding: 14px 18px;
                border-bottom: 1px solid rgba(0,255,200,0.18);
                background: rgba(0, 255, 200, 0.05);
            }

            #<?php echo esc_html($id); ?> .tasia-dot {
                width: 10px;
                height: 10px;
                border-radius: 50%;
                background: #5fffd7;
                box-shadow: 0 0 12px #5fffd7;
                flex: 0 0 auto;
            }

            #<?php echo esc_html($id); ?> .tasia-title {
                font-size: 14px;
                letter-spacing: 0.04em;
                color: #d8fff8;
            }

            #<?php echo esc_html($id); ?> .tasia-status {
                margin-left: auto;
                font-size: 11px;
                padding: 4px 8px;
                border: 1px solid rgba(95,255,215,0.35);
                border-radius: 999px;
                color: #8dffe8;
                background: rgba(95,255,215,0.08);
            }

            #<?php echo esc_html($id); ?> .tasia-terminal {
                min-height: 320px;
                max-height: 420px;
                overflow-y: auto;
                padding: 20px 18px 10px 18px;
                white-space: pre-wrap;
                word-break: break-word;
                font-size: 15px;
                line-height: 1.55;
            }

            #<?php echo esc_html($id); ?> .tasia-line {
                margin: 0 0 8px 0;
            }

            #<?php echo esc_html($id); ?> .tasia-line.dim {
                color: #7bc8bc;
            }

            #<?php echo esc_html($id); ?> .tasia-line.ok {
                color: #7dffb1;
                text-shadow: 0 0 10px rgba(125,255,177,0.22);
            }

            #<?php echo esc_html($id); ?> .tasia-line.err {
                color: #ff8ea1;
                text-shadow: 0 0 10px rgba(255,142,161,0.18);
            }

            #<?php echo esc_html($id); ?> .tasia-line.user {
                color: #dffff8;
            }

            #<?php echo esc_html($id); ?> .tasia-input-row {
                display: flex;
                align-items: center;
                gap: 10px;
                padding: 14px 18px 18px 18px;
                border-top: 1px solid rgba(0,255,200,0.12);
            }

            #<?php echo esc_html($id); ?> .tasia-prompt {
                color: #67ffe0;
                text-shadow: 0 0 12px rgba(103,255,224,0.25);
                user-select: none;
            }

            #<?php echo esc_html($id); ?> .tasia-cmdline {
                width: 100%;
                border: 1px solid rgba(95,255,215,0.20);
                outline: none;
                border-radius: 12px;
                padding: 12px 14px;
                background: rgba(0, 10, 14, 0.8);
                color: #eafffb;
                font: inherit;
                box-shadow: 0 0 0 1px rgba(0,255,200,0.05) inset;
            }

            #<?php echo esc_html($id); ?> .tasia-cmdline:focus {
                border-color: rgba(95,255,215,0.45);
                box-shadow:
                    0 0 0 1px rgba(95,255,215,0.18) inset,
                    0 0 18px rgba(95,255,215,0.12);
            }

            #<?php echo esc_html($id); ?> .tasia-noscript {
                padding: 0 18px 18px;
                color: #ff9aa8;
            }
        </style>

        <script>
        (function () {
            const root = document.getElementById(<?php echo wp_json_encode($id); ?>);
            if (!root) return;

            const terminal = root.querySelector('.tasia-terminal');
            const input = root.querySelector('.tasia-cmdline');
            const ajaxUrl = <?php echo wp_json_encode($ajaxUrl); ?>;
            const nonce = <?php echo wp_json_encode($nonce); ?>;

            let state = 'askName';
            let firstName = '';
            let lastName = '';
            let resetCode = '';

            function print(text, cls) {
                const line = document.createElement('div');
                line.className = 'tasia-line' + (cls ? ' ' + cls : '');
                line.textContent = text;
                terminal.appendChild(line);
                terminal.scrollTop = terminal.scrollHeight;
            }

            async function api(payload) {
                const form = new FormData();
                form.append('action', <?php echo wp_json_encode(self::AJAX_ACTION); ?>);
                form.append('nonce', nonce);

                for (const key in payload) {
                    form.append(key, payload[key]);
                }

                const res = await fetch(ajaxUrl, {
                    method: 'POST',
                    credentials: 'same-origin',
                    body: form
                });

                let data = null;

                try {
                    data = await res.json();
                } catch (e) {
                    throw new Error('Server returned invalid response.');
                }

                if (!res.ok || !data || !data.success) {
                    throw new Error((data && data.data && data.data.message) ? data.data.message : 'Request failed.');
                }

                return data.data;
            }

            function setPasswordMode(enabled) {
                input.type = enabled ? 'password' : 'text';
            }

            print('Tasia AI Password Reset Interface', 'ok');
            print('Identity verification required.', 'dim');
            print('Enter your full avatar name: First Last', 'dim');

            input.addEventListener('keydown', async function (e) {
                if (e.key !== 'Enter') return;

                const value = input.value.trim();
                if (!value || state === 'busy' || state === 'done') return;

                print(': ' + value, 'user');
                input.value = '';

                if (state === 'askName') {
                    const parts = value.split(/\s+/);

                    if (parts.length < 2) {
                        print('Please enter first and last name.', 'err');
                        return;
                    }

                    firstName = parts[0];
                    lastName = parts.slice(1).join(' ');

                    state = 'busy';
                    print('Locating avatar record...', 'dim');

                    try {
                        const response = await api({
                            mode: 'request',
                            first: firstName,
                            last: lastName
                        });

                        print(response.message, 'ok');
                        print('Enter the 6-digit reset code from your email.', 'dim');
                        state = 'askCode';
                    } catch (err) {
                        print(err.message, 'err');
                        print('Try again. Enter your full avatar name.', 'dim');
                        state = 'askName';
                    }

                    return;
                }

                if (state === 'askCode') {
                    if (!/^\d{6}$/.test(value)) {
                        print('Reset code must be 6 digits.', 'err');
                        return;
                    }

                    resetCode = value;
                    setPasswordMode(true);
                    print('Enter your new password.', 'dim');
                    state = 'askPassword';
                    return;
                }

                if (state === 'askPassword') {
                    if (value.length < 6) {
                        print('Password must be at least 6 characters long.', 'err');
                        return;
                    }

                    state = 'busy';
                    print('Updating credentials...', 'dim');

                    try {
                        const response = await api({
                            mode: 'confirm',
                            first: firstName,
                            last: lastName,
                            code: resetCode,
                            newPassword: value
                        });

                        print(response.message, 'ok');
                        print('Reset complete. You can close this window.', 'dim');
                        setPasswordMode(false);
                        input.disabled = true;
                        state = 'done';
                    } catch (err) {
                        print(err.message, 'err');
                        print('Process reset. Enter your full avatar name again.', 'dim');

                        firstName = '';
                        lastName = '';
                        resetCode = '';
                        setPasswordMode(false);
                        state = 'askName';
                    }
                }
            });
        })();
        </script>
        <?php
        return ob_get_clean();
    }
}

register_activation_hook(__FILE__, ['Tasia_OpenSim_Password_Reset', 'activate']);
new Tasia_OpenSim_Password_Reset();