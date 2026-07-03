<?php
declare(strict_types=1);

$configFile = __DIR__ . '/../config/config.php';
if (!is_file($configFile)) {
    http_response_code(500);
    echo 'Missing config/config.php';
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

require_once __DIR__ . '/../src/MarketplaceApp.php';

$error = trim((string)($_GET['error'] ?? ''));
if ($error !== '') {
    fail('OIDC authorize error: ' . $error);
}

$code = trim((string)($_GET['code'] ?? ''));
$state = trim((string)($_GET['state'] ?? ''));
if ($code === '' || $state === '') {
    fail('Missing OIDC code/state');
}

$expectedState = trim((string)($_SESSION['oidc_state'] ?? ''));
$stateExp = (int)($_SESSION['oidc_state_expires'] ?? 0);
unset($_SESSION['oidc_state'], $_SESSION['oidc_state_expires']);
if ($expectedState === '' || !hash_equals($expectedState, $state) || $stateExp < time()) {
    fail('OIDC state validation failed');
}

try {
    $oidc = oidc_config($config);
    $callbackUrl = market_callback_url($oidc);

    $token = http_post_form($oidc['token_url'], [
        'grant_type' => 'authorization_code',
        'code' => $code,
        'redirect_uri' => $callbackUrl,
    ], $oidc['client_id'], $oidc['client_secret']);

    if (($token['status'] ?? 0) < 200 || ($token['status'] ?? 0) >= 300) {
        fail('OIDC token request failed');
    }
    $tokenJson = json_decode((string)($token['body'] ?? ''), true);
    if (!is_array($tokenJson)) {
        fail('OIDC token response is invalid JSON');
    }

    $accessToken = trim((string)($tokenJson['access_token'] ?? ''));
    if ($accessToken === '') {
        fail('OIDC token response has no access_token');
    }

    $userinfo = http_get_bearer($oidc['userinfo_url'], $accessToken);
    if (($userinfo['status'] ?? 0) < 200 || ($userinfo['status'] ?? 0) >= 300) {
        fail('OIDC userinfo request failed');
    }
    $userJson = json_decode((string)($userinfo['body'] ?? ''), true);
    if (!is_array($userJson)) {
        fail('OIDC userinfo response is invalid JSON');
    }

    $uuid = strtolower(trim((string)($userJson['avatar_uuid'] ?? $userJson['sub'] ?? '')));
    if (!preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i', $uuid)) {
        fail('OIDC userinfo does not include valid avatar UUID');
    }

    $app = new MarketplaceApp($config);
    $app->completeLogin($uuid);
    $_SESSION['login_attempts'] = [];
    $_SESSION['csrf_token'] = bin2hex(random_bytes(16));

    header('Location: ./');
    exit;
} catch (Throwable $e) {
    fail($e->getMessage());
}

function oidc_config(array $config): array
{
    $iauth = is_array($config['iauth'] ?? null) ? $config['iauth'] : [];
    $authorizeUrl = trim((string)($iauth['authorize_url'] ?? 'https://i.let-us.cyou/i-auth/oidc/authorize.php'));
    $tokenUrl = trim((string)($iauth['token_url'] ?? 'https://i.let-us.cyou/i-auth/oidc/token.php'));
    $userinfoUrl = trim((string)($iauth['userinfo_url'] ?? 'https://i.let-us.cyou/i-auth/oidc/userinfo.php'));
    $clientId = trim((string)($iauth['client_id'] ?? 'marty-auth-broker'));
    $clientSecret = (string)($iauth['client_secret'] ?? 'CHANGE_ME_OIDC_CLIENT_SECRET');
    $scope = trim((string)($iauth['scope'] ?? 'openid profile email groups role'));
    $callback = trim((string)($iauth['market_callback_url'] ?? ''));

    if ($authorizeUrl === '' || $tokenUrl === '' || $userinfoUrl === '' || $clientId === '' || $clientSecret === '') {
        throw new RuntimeException('OIDC settings are incomplete in config/config.php');
    }

    return [
        'authorize_url' => $authorizeUrl,
        'token_url' => $tokenUrl,
        'userinfo_url' => $userinfoUrl,
        'client_id' => $clientId,
        'client_secret' => $clientSecret,
        'scope' => $scope,
        'market_callback_url' => $callback,
    ];
}

function market_callback_url(array $oidc): string
{
    $configured = trim((string)($oidc['market_callback_url'] ?? ''));
    if ($configured !== '') {
        return $configured;
    }

    $https = !empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off';
    $scheme = $https ? 'https' : 'http';
    $host = trim((string)($_SERVER['HTTP_HOST'] ?? ''));
    if ($host === '') {
        throw new RuntimeException('Unable to determine callback host');
    }
    $scriptDir = str_replace('\\', '/', dirname((string)($_SERVER['SCRIPT_NAME'] ?? '/oidc_callback.php')));
    $base = rtrim($scriptDir, '/');
    return $scheme . '://' . $host . ($base === '' ? '' : $base) . '/oidc_callback.php';
}

function http_post_form(string $url, array $fields, string $clientId, string $clientSecret): array
{
    $ch = curl_init($url);
    if ($ch === false) {
        throw new RuntimeException('Unable to initialize curl for token request');
    }

    $auth = base64_encode($clientId . ':' . $clientSecret);
    curl_setopt_array($ch, [
        CURLOPT_POST => true,
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_CONNECTTIMEOUT => 5,
        CURLOPT_TIMEOUT => 20,
        CURLOPT_HTTPHEADER => [
            'Authorization: Basic ' . $auth,
            'Content-Type: application/x-www-form-urlencoded',
            'Accept: application/json',
        ],
        CURLOPT_POSTFIELDS => http_build_query($fields),
    ]);

    $body = curl_exec($ch);
    $error = curl_error($ch);
    $status = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    curl_close($ch);

    if ($error !== '') {
        throw new RuntimeException('OIDC token HTTP error: ' . $error);
    }
    return ['status' => $status, 'body' => is_string($body) ? $body : ''];
}

function http_get_bearer(string $url, string $accessToken): array
{
    $ch = curl_init($url);
    if ($ch === false) {
        throw new RuntimeException('Unable to initialize curl for userinfo request');
    }

    curl_setopt_array($ch, [
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_CONNECTTIMEOUT => 5,
        CURLOPT_TIMEOUT => 20,
        CURLOPT_HTTPHEADER => [
            'Authorization: Bearer ' . $accessToken,
            'Accept: application/json',
        ],
    ]);

    $body = curl_exec($ch);
    $error = curl_error($ch);
    $status = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    curl_close($ch);

    if ($error !== '') {
        throw new RuntimeException('OIDC userinfo HTTP error: ' . $error);
    }
    return ['status' => $status, 'body' => is_string($body) ? $body : ''];
}

function fail(string $message): void
{
    http_response_code(400);
    echo '<!doctype html><meta charset="utf-8"><title>MarketApp OIDC Error</title>';
    echo '<div style="font-family:monospace;padding:16px;color:#f5f5f5;background:#111;min-height:100vh">';
    echo '<h2 style="margin-top:0">OIDC Login Failed</h2>';
    echo '<p>' . htmlspecialchars($message, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8') . '</p>';
    echo '<p><a href="./" style="color:#7dd3fc">Back to marketplace</a></p>';
    echo '</div>';
    exit;
}
