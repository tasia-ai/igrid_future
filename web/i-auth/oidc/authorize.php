<?php
declare(strict_types=1);
session_start();

require dirname(__DIR__) . '/lib.php';

$cfg = iauth_config();

$responseType = trim((string)($_GET['response_type'] ?? ''));
$clientId = trim((string)($_GET['client_id'] ?? ''));
$redirectUri = trim((string)($_GET['redirect_uri'] ?? ''));
$scope = trim((string)($_GET['scope'] ?? 'openid profile email'));
$state = (string)($_GET['state'] ?? '');
$nonce = (string)($_GET['nonce'] ?? '');
$iauthContinue = (string)($_GET['iauth_continue'] ?? '');

if ($responseType !== 'code' || $clientId === '' || $redirectUri === '') {
    http_response_code(400);
    echo 'invalid_request';
    exit;
}

$parts = parse_url($redirectUri);
if (!is_array($parts) || empty($parts['scheme']) || empty($parts['host']) || !in_array(strtolower((string)$parts['scheme']), ['http', 'https'], true)) {
    http_response_code(400);
    echo 'invalid_redirect_uri';
    exit;
}

$scopes = preg_split('/\s+/', $scope) ?: [];
if (!in_array('openid', $scopes, true)) {
    http_response_code(400);
    echo 'invalid_scope';
    exit;
}

if (!hash_equals((string)$cfg['oidc_client_id'], $clientId)) {
    http_response_code(400);
    echo 'unauthorized_client';
    exit;
}

if (!iauth_oidc_redirect_allowed($cfg, $redirectUri)) {
    http_response_code(400);
    echo 'redirect_uri_not_allowed';
    exit;
}

$self = (isset($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off' ? 'https' : 'http') . '://' . ($_SERVER['HTTP_HOST'] ?? 'i.let-us.cyou') . ($_SERVER['REQUEST_URI'] ?? '/i-auth/oidc/authorize');
if ($iauthContinue !== '1') {
    $login = '/i-auth/index.php?target=oidc&return_to=' . rawurlencode($self);
    header('Location: ' . $login);
    exit;
}

$iauthUser = isset($_SESSION['iauth_user']) && is_array($_SESSION['iauth_user']) ? $_SESSION['iauth_user'] : null;
$uuid = strtolower(trim((string)($iauthUser['uuid'] ?? '')));
if (!$iauthUser || !iauth_valid_uuid($uuid)) {
    $login = '/i-auth/index.php?target=oidc&return_to=' . rawurlencode($self);
    header('Location: ' . $login);
    exit;
}

try {
    $db = iauth_db_connect($cfg['db_name'], $cfg);
    iauth_oidc_db_init($db);

    $code = bin2hex(random_bytes(32));
    $hash = hash('sha256', $code);
    $now = time();
    $exp = $now + max(60, (int)$cfg['oidc_code_ttl_sec']);

    $stmt = $db->prepare('INSERT INTO wp_iauth_oidc_codes (code_hash, client_id, redirect_uri, user_uuid, scope, nonce, expires_at, created_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)');
    $stmt->bind_param('ssssssii', $hash, $clientId, $redirectUri, $uuid, $scope, $nonce, $exp, $now);
    $stmt->execute();
    $stmt->close();
    $db->close();

    $sep = (strpos($redirectUri, '?') !== false) ? '&' : '?';
    $loc = $redirectUri . $sep . 'code=' . rawurlencode($code);
    if ($state !== '') {
        $loc .= '&state=' . rawurlencode($state);
    }
    header('Location: ' . $loc);
    exit;
} catch (Throwable $e) {
    http_response_code(500);
    echo 'server_error';
    exit;
}
