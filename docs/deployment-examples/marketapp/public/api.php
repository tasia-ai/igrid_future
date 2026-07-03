<?php

declare(strict_types=1);

$configFile = __DIR__ . '/../config/config.php';
if (!is_file($configFile)) {
    http_response_code(500);
    echo json_encode(['success' => false, 'error' => 'Missing config/config.php']);
    exit;
}

$config = require $configFile;
session_name($config['app']['session_name'] ?? 'tasia_marketapp_session');
session_set_cookie_params([
    'lifetime' => 0,
    'path' => '/',
    'secure' => (!empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off'),
    'httponly' => true,
    'samesite' => 'Lax',
]);
session_start();

if (!isset($_SESSION['csrf_token']) || !is_string($_SESSION['csrf_token']) || $_SESSION['csrf_token'] === '') {
    $_SESSION['csrf_token'] = bin2hex(random_bytes(16));
}

require_once __DIR__ . '/../src/MarketplaceApp.php';

try {
    $app = new MarketplaceApp($config);
    $action = (string)($_GET['action'] ?? $_POST['action'] ?? 'status');

    switch ($action) {
        case 'status':
            respond(true, [
                'context' => $app->getContext(),
                'csrf_token' => $_SESSION['csrf_token'],
            ]);
            break;

        case 'oidc_start':
            ensurePost();
            checkLoginRateLimit();
            $oidc = marketOidcConfig($config);
            $state = bin2hex(random_bytes(16));
            $_SESSION['oidc_state'] = $state;
            $_SESSION['oidc_state_expires'] = time() + 600;
            $callbackUrl = marketCallbackUrl($oidc);
            $query = http_build_query([
                'response_type' => 'code',
                'client_id' => $oidc['client_id'],
                'redirect_uri' => $callbackUrl,
                'scope' => $oidc['scope'],
                'state' => $state,
            ]);
            respond(true, ['authorize_url' => $oidc['authorize_url'] . '?' . $query]);
            break;

        case 'list':
            ensureAuthenticated($app);
            $payload = $app->listItems([
                'q' => $_GET['q'] ?? '',
                'region' => $_GET['region'] ?? '',
                'minp' => $_GET['minp'] ?? '',
                'maxp' => $_GET['maxp'] ?? '',
                'page' => $_GET['page'] ?? 1,
                'per_page' => $_GET['per_page'] ?? 12,
            ]);
            $payload['context'] = $app->getContext();
            respond(true, $payload);
            break;

        case 'regions':
            ensureAuthenticated($app);
            respond(true, ['regions' => $app->listRegions()]);
            break;

        case 'admin_dashboard':
            ensureAuthenticated($app);
            respond(true, $app->getAdminDashboard());
            break;

        case 'admin_logs':
            ensureAuthenticated($app);
            respond(true, $app->getAdminLogs());
            break;

        case 'admin_settings_get':
            ensureAuthenticated($app);
            respond(true, ['settings' => $app->getAdminSettings()]);
            break;

        case 'admin_settings_save':
            ensurePost();
            ensureCsrf();
            ensureAuthenticated($app);
            $saved = $app->saveAdminSettings([
                'delivery_api_url' => $_POST['delivery_api_url'] ?? '',
                'region_overrides' => $_POST['region_overrides'] ?? '',
                'texture_proxy_template' => $_POST['texture_proxy_template'] ?? '',
                'delivery_api_password' => $_POST['delivery_api_password'] ?? '',
                'timeout_seconds' => $_POST['timeout_seconds'] ?? '',
                'auth_backup_enabled' => $_POST['auth_backup_enabled'] ?? '',
                'auth_backup_password' => $_POST['auth_backup_password'] ?? '',
                'auth_backup_allowed_uuid' => $_POST['auth_backup_allowed_uuid'] ?? '',
                'admin_uuids' => $_POST['admin_uuids'] ?? '',
            ]);
            respond(true, ['settings' => $saved]);
            break;

        case 'dorito_init':
            ensurePost();
            ensureCsrf();
            ensureAuthenticated($app);
            ensureAdmin($app);
            $targetUuid = strtolower(trim((string)($_POST['target_uuid'] ?? '')));
            $usd = (float)($_POST['usd_amount'] ?? 0);
            if (!preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i', $targetUuid)) {
                throw new RuntimeException('Invalid target UUID');
            }
            if ($usd <= 0) {
                throw new RuntimeException('USD amount must be positive');
            }
            $rate = (int)($config['dorito']['usd_to_dorito'] ?? 256);
            if ($rate <= 0) {
                $rate = 256;
            }
            $doritos = (int)round($usd * $rate);
            $token = strtoupper(bin2hex(random_bytes(6)));
            $accessKey = substr(str_replace('-', '', $targetUuid), -8);

            $_SESSION['dorito_challenge'] = [
                'target_uuid' => $targetUuid,
                'usd' => $usd,
                'doritos' => $doritos,
                'token' => $token,
                'access_key' => strtolower($accessKey),
                'created_at' => time(),
            ];

            respond(true, [
                'target_uuid' => $targetUuid,
                'usd' => $usd,
                'doritos' => $doritos,
                'vista_token' => $token,
                'access_key_hint' => 'Last 8 characters of target UUID',
                'security_question' => (string)($config['dorito']['security_question'] ?? 'Name of grid admin?'),
                'notice' => 'I-grid money simulation only. No real-world payment.',
            ]);
            break;

        case 'dorito_grant':
            ensurePost();
            ensureCsrf();
            ensureAuthenticated($app);
            ensureAdmin($app);
            $challenge = $_SESSION['dorito_challenge'] ?? null;
            if (!is_array($challenge)) {
                throw new RuntimeException('No active Vista challenge');
            }
            if ((time() - (int)($challenge['created_at'] ?? 0)) > 600) {
                unset($_SESSION['dorito_challenge']);
                throw new RuntimeException('Vista challenge expired');
            }

            $tokenIn = strtoupper(trim((string)($_POST['vista_token'] ?? '')));
            $rawKeyIn = strtolower(trim((string)($_POST['access_key'] ?? '')));
            $keyIn = preg_replace('/[^0-9a-z]/i', '', $rawKeyIn) ?? '';
            if (strlen($keyIn) > 8) {
                $keyIn = substr($keyIn, -8);
            }
            $answerIn = strtolower(trim((string)($_POST['security_answer'] ?? '')));
            $expectedAnswer = strtolower(trim((string)($config['dorito']['security_answer'] ?? 'marty')));

            if (!hash_equals((string)($challenge['token'] ?? ''), $tokenIn)) {
                throw new RuntimeException('Invalid Vista token');
            }
            if (!hash_equals((string)($challenge['access_key'] ?? ''), $keyIn)) {
                throw new RuntimeException('Invalid access key');
            }
            if (!hash_equals($expectedAnswer, $answerIn)) {
                throw new RuntimeException('Security answer invalid');
            }

            $result = $app->grantDoritos((string)$challenge['target_uuid'], (int)$challenge['doritos'], 'vista-token-topup');
            unset($_SESSION['dorito_challenge']);
            respond(true, [
                'message' => 'Dorito$ granted successfully.',
                'result' => $result,
            ]);
            break;

        case 'texture':
            ensureAuthenticated($app);
            $uuid = (string)($_GET['uuid'] ?? '');
            $texture = $app->fetchTexture($uuid);
            if (empty($texture['ok'])) {
                http_response_code((int)($texture['status'] ?? 404));
                header('Content-Type: text/plain; charset=utf-8');
                echo (string)($texture['message'] ?? 'Texture not available');
                exit;
            }
            http_response_code((int)$texture['status']);
            header('Content-Type: ' . (string)$texture['content_type']);
            header('Cache-Control: public, max-age=300');
            echo (string)$texture['body'];
            exit;

        case 'login':
            ensurePost();
            checkLoginRateLimit();
            $uuid = (string)($_POST['avatar_uuid'] ?? '');
            $password = (string)($_POST['password'] ?? '');
            if (!empty($config['otp']['enabled']) && !empty($config['otp']['require_for_login'])) {
                throw new RuntimeException('OTP required. Use otp_request then otp_verify.');
            }
            $context = $app->loginAvatar($uuid, $password);
            $_SESSION['login_attempts'] = [];
            $_SESSION['csrf_token'] = bin2hex(random_bytes(16));
            respond(true, ['context' => $context, 'csrf_token' => $_SESSION['csrf_token']]);
            break;

        case 'login_iauth':
            ensurePost();
            $uuid = readIauthSessionUuid();
            if ($uuid === '') {
                throw new RuntimeException('I-Auth session not found. Authenticate with I-Grid first.');
            }
            $context = $app->completeLogin($uuid);
            $_SESSION['login_attempts'] = [];
            $_SESSION['csrf_token'] = bin2hex(random_bytes(16));
            respond(true, ['context' => $context, 'csrf_token' => $_SESSION['csrf_token']]);
            break;

        case 'otp_request':
            ensurePost();
            checkLoginRateLimit();
            $uuid = strtolower(trim((string)($_POST['avatar_uuid'] ?? '')));
            if (!preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i', $uuid)) {
                throw new RuntimeException('Invalid avatar UUID');
            }

            $code = (string)random_int(100000, 999999);
            $ttl = max(60, min(600, (int)($config['otp']['ttl_seconds'] ?? 180)));
            $_SESSION['otp_pending'] = [
                'uuid' => $uuid,
                'hash' => hash('sha256', $code),
                'expires_at' => time() + $ttl,
            ];

            $fromUuid = strtolower((string)($config['im']['from_uuid'] ?? '00000000-0000-0000-0000-000000000000'));
            if (!preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i', $fromUuid)) {
                $fromUuid = '00000000-0000-0000-0000-000000000000';
            }
            $fromName = trim((string)($config['im']['from_name'] ?? 'Grid System'));
            if ($fromName === '') {
                $fromName = 'Grid System';
            }
            $robustBase = rtrim((string)($config['im']['robust_base_url'] ?? 'http://127.0.0.1:8002'), '/');
            $notice = (string)($config['otp']['message_prefix'] ?? '[I-Grid OTP]');
            $msgText = trim($notice . ' Login code: ' . $code . ' (expires in ' . (int)($ttl / 60) . ' min)');

            $sent = send_grid_im($robustBase, $fromUuid, $fromName, $uuid, $msgText);
            if (!$sent['ok']) {
                unset($_SESSION['otp_pending']);
                throw new RuntimeException('OTP IM send failed: ' . ($sent['error'] ?? 'unknown error'));
            }

            respond(true, ['message' => 'OTP sent by IM.', 'expires_in' => $ttl]);
            break;

        case 'otp_verify':
            ensurePost();
            $uuid = strtolower(trim((string)($_POST['avatar_uuid'] ?? '')));
            $otpCode = trim((string)($_POST['otp_code'] ?? ''));
            if ($otpCode === '') {
                throw new RuntimeException('OTP code is required');
            }
            $pending = $_SESSION['otp_pending'] ?? null;
            if (!is_array($pending)) {
                throw new RuntimeException('No OTP pending. Request new code.');
            }
            if (($pending['expires_at'] ?? 0) < time()) {
                unset($_SESSION['otp_pending']);
                throw new RuntimeException('OTP expired. Request new code.');
            }
            if (!hash_equals((string)($pending['uuid'] ?? ''), $uuid)) {
                throw new RuntimeException('OTP UUID mismatch.');
            }
            if (!hash_equals((string)($pending['hash'] ?? ''), hash('sha256', $otpCode))) {
                throw new RuntimeException('Invalid OTP code.');
            }

            unset($_SESSION['otp_pending']);
            $context = $app->completeLogin($uuid);
            $_SESSION['login_attempts'] = [];
            $_SESSION['csrf_token'] = bin2hex(random_bytes(16));
            respond(true, ['context' => $context, 'csrf_token' => $_SESSION['csrf_token']]);
            break;

        case 'logout':
            ensurePost();
            ensureCsrf();
            ensureAuthenticated($app);
            $app->logoutAvatar();
            respond(true, ['message' => 'Logged out']);
            break;

        case 'logout_full':
            ensurePost();
            ensureCsrf();
            ensureAuthenticated($app);
            $app->logoutAvatar();
            clearIauthSession();
            respond(true, ['message' => 'Logged out from MarketApp and I-Auth']);
            break;

        case 'purchase':
            ensurePost();
            ensureCsrf();
            ensureAuthenticated($app);
            $itemId = (int)($_POST['item_id'] ?? 0);
            $recipientUuid = (string)($_POST['recipient_uuid'] ?? '');
            $result = $app->purchase($itemId, $recipientUuid);
            respond(true, $result);
            break;

        default:
            http_response_code(404);
            respond(false, null, 'Unknown action');
    }
} catch (Throwable $e) {
    http_response_code(400);
    respond(false, null, $e->getMessage());
}

function ensurePost(): void
{
    if (($_SERVER['REQUEST_METHOD'] ?? 'GET') !== 'POST') {
        throw new RuntimeException('POST required');
    }
}

function ensureCsrf(): void
{
    $token = (string)($_POST['csrf_token'] ?? '');
    $current = (string)($_SESSION['csrf_token'] ?? '');
    if ($token === '' || $current === '' || !hash_equals($current, $token)) {
        throw new RuntimeException('Invalid request token');
    }
}

function ensureAuthenticated(MarketplaceApp $app): void
{
    $ctx = $app->getContext();
    if (empty($ctx['authenticated'])) {
        throw new RuntimeException('Login required');
    }
}

function ensureAdmin(MarketplaceApp $app): void
{
    $ctx = $app->getContext();
    if (empty($ctx['authenticated']) || empty($ctx['is_admin'])) {
        throw new RuntimeException('Admin access required');
    }
}

function checkLoginRateLimit(): void
{
    $now = time();
    $window = 600;
    $maxAttempts = 8;

    $attempts = $_SESSION['login_attempts'] ?? [];
    if (!is_array($attempts)) {
        $attempts = [];
    }

    $attempts = array_values(array_filter($attempts, static fn($ts) => is_int($ts) && ($now - $ts) <= $window));
    if (count($attempts) >= $maxAttempts) {
        throw new RuntimeException('Too many login attempts. Try again in a few minutes.');
    }

    $attempts[] = $now;
    $_SESSION['login_attempts'] = $attempts;
}

function readIauthSessionUuid(): string
{
    $cookieSessionId = trim((string)($_COOKIE['PHPSESSID'] ?? ''));
    if ($cookieSessionId === '' || !preg_match('/^[a-zA-Z0-9,-]{16,128}$/', $cookieSessionId)) {
        return '';
    }

    $activeName = session_name();
    $activeId = session_id();

    session_write_close();

    $uuid = '';
    session_name('PHPSESSID');
    session_id($cookieSessionId);
    session_start();

    $iauthUser = $_SESSION['iauth_user'] ?? null;
    if (is_array($iauthUser)) {
        $candidate = strtolower(trim((string)($iauthUser['uuid'] ?? '')));
        if (preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i', $candidate)) {
            $uuid = $candidate;
        }
    }

    session_write_close();

    session_name($activeName);
    if ($activeId !== '') {
        session_id($activeId);
    }
    session_start();

    return $uuid;
}

function marketOidcConfig(array $config): array
{
    $iauth = is_array($config['iauth'] ?? null) ? $config['iauth'] : [];
    $authorizeUrl = trim((string)($iauth['authorize_url'] ?? 'https://i.let-us.cyou/i-auth/oidc/authorize.php'));
    $clientId = trim((string)($iauth['client_id'] ?? 'marty-auth-broker'));
    $scope = trim((string)($iauth['scope'] ?? 'openid profile email groups role'));
    $callbackUrl = trim((string)($iauth['market_callback_url'] ?? ''));

    if ($authorizeUrl === '' || $clientId === '') {
        throw new RuntimeException('OIDC configuration is incomplete');
    }

    return [
        'authorize_url' => $authorizeUrl,
        'client_id' => $clientId,
        'scope' => $scope === '' ? 'openid profile email' : $scope,
        'market_callback_url' => $callbackUrl,
    ];
}

function marketCallbackUrl(array $oidc): string
{
    $configured = trim((string)($oidc['market_callback_url'] ?? ''));
    if ($configured !== '') {
        return $configured;
    }

    $https = !empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off';
    $scheme = $https ? 'https' : 'http';
    $host = trim((string)($_SERVER['HTTP_HOST'] ?? ''));
    if ($host === '') {
        throw new RuntimeException('Unable to determine callback host for OIDC');
    }
    $scriptDir = str_replace('\\', '/', dirname((string)($_SERVER['SCRIPT_NAME'] ?? '/api.php')));
    $base = rtrim($scriptDir, '/');
    return $scheme . '://' . $host . ($base === '' ? '' : $base) . '/oidc_callback.php';
}

function clearIauthSession(): void
{
    $cookieSessionId = trim((string)($_COOKIE['PHPSESSID'] ?? ''));
    if ($cookieSessionId === '' || !preg_match('/^[a-zA-Z0-9,-]{16,128}$/', $cookieSessionId)) {
        return;
    }

    $activeName = session_name();
    $activeId = session_id();

    session_write_close();

    session_name('PHPSESSID');
    session_id($cookieSessionId);
    session_start();

    unset($_SESSION['iauth_pending']);
    unset($_SESSION['iauth_user']);
    unset($_SESSION['iauth_tokens']);
    unset($_SESSION['iauth_oidc_state']);
    unset($_SESSION['iauth_oidc_url']);
    unset($_SESSION['iauth_target']);
    unset($_SESSION['iauth_return_to']);
    unset($_SESSION['iauth_ready_redirect']);
    unset($_SESSION['iauth_admin_session']);

    session_write_close();

    session_name($activeName);
    if ($activeId !== '') {
        session_id($activeId);
    }
    session_start();
}

function send_grid_im(string $robustBase, string $fromUuid, string $fromName, string $toUuid, string $message): array
{
    $payload = [
        'from_agent_id' => $fromUuid,
        'to_agent_id' => $toUuid,
        'im_session_id' => uuid_v4(),
        'timestamp' => (string)time(),
        'from_agent_name' => $fromName,
        'message' => $message,
        'dialog' => base64_encode(chr(0)),
        'from_group' => 'FALSE',
        'offline' => base64_encode(chr(0)),
        'parent_estate_id' => '0',
        'position_x' => '128',
        'position_y' => '128',
        'position_z' => '25',
        'region_id' => '00000000-0000-0000-0000-000000000000',
        'binary_bucket' => '',
    ];

    $xml = '<?xml version="1.0"?>'
        . '<methodCall><methodName>grid_instant_message</methodName><params><param><value><struct>';
    foreach ($payload as $key => $value) {
        $xml .= '<member><name>' . xml_escape((string)$key) . '</name><value><string>'
            . xml_escape((string)$value) . '</string></value></member>';
    }
    $xml .= '</struct></value></param></params></methodCall>';

    $response = http_post_payload(rtrim($robustBase, '/') . '/', $xml, 'text/xml');
    if (($response['error'] ?? '') !== '') {
        return ['ok' => false, 'error' => (string)$response['error']];
    }

    $success = xmlrpc_success_value((string)($response['body'] ?? ''));
    if ($success === true) {
        return ['ok' => true];
    }
    if ($success === false) {
        return ['ok' => false, 'error' => 'Grid messaging rejected the IM'];
    }
    return ['ok' => false, 'error' => 'Unexpected IM response'];
}

function http_post_payload(string $url, string $payload, string $contentType = 'text/plain', int $timeoutSeconds = 8): array
{
    $ch = curl_init($url);
    if ($ch === false) {
        return ['status' => 0, 'body' => '', 'error' => 'Unable to initialize curl'];
    }

    curl_setopt_array($ch, [
        CURLOPT_POST => true,
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_CONNECTTIMEOUT => 4,
        CURLOPT_TIMEOUT => $timeoutSeconds,
        CURLOPT_HTTPHEADER => [
            'Content-Type: ' . $contentType,
            'Accept: text/xml, application/xml, text/plain, */*',
        ],
        CURLOPT_POSTFIELDS => $payload,
    ]);

    $body = curl_exec($ch);
    $error = curl_error($ch);
    $status = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    curl_close($ch);

    return [
        'status' => $status,
        'body' => is_string($body) ? $body : '',
        'error' => (string)$error,
    ];
}

function xml_escape(string $value): string
{
    return htmlspecialchars($value, ENT_QUOTES | ENT_XML1, 'UTF-8');
}

function xmlrpc_success_value(string $xml): ?bool
{
    if ($xml === '') {
        return null;
    }

    if (preg_match('/<name>success<\/name>\s*<value>\s*<boolean>([01])<\/boolean>/is', $xml, $m)) {
        return $m[1] === '1';
    }
    if (preg_match('/<name>success<\/name>\s*<value>\s*<string>(.*?)<\/string>/is', $xml, $m)) {
        $normalized = strtoupper(trim(html_entity_decode((string)$m[1], ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8')));
        if (in_array($normalized, ['TRUE', '1', 'YES'], true)) {
            return true;
        }
        if (in_array($normalized, ['FALSE', '0', 'NO'], true)) {
            return false;
        }
    }
    return null;
}

function uuid_v4(): string
{
    $data = random_bytes(16);
    $data[6] = chr((ord($data[6]) & 0x0f) | 0x40);
    $data[8] = chr((ord($data[8]) & 0x3f) | 0x80);
    return vsprintf('%s%s-%s-%s-%s-%s%s%s', str_split(bin2hex($data), 4));
}

function respond(bool $success, ?array $data = null, string $error = ''): void
{
    header('Content-Type: application/json; charset=utf-8');
    $out = ['success' => $success];
    if ($data !== null) {
        $out['data'] = $data;
    }
    if (!$success) {
        $out['error'] = $error;
    }
    echo json_encode($out, JSON_UNESCAPED_SLASHES);
    exit;
}
