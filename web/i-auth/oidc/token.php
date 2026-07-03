<?php
declare(strict_types=1);

require dirname(__DIR__) . '/lib.php';

$cfg = iauth_config();

header('Content-Type: application/json; charset=utf-8');

if (($_SERVER['REQUEST_METHOD'] ?? 'GET') !== 'POST') {
    http_response_code(405);
    echo json_encode(['error' => 'invalid_request']);
    exit;
}

$auth = iauth_oidc_parse_client_auth();
if (!iauth_oidc_client_ok($cfg, (string)$auth['client_id'], (string)$auth['client_secret'])) {
    $authHdr = (string)($_SERVER['HTTP_AUTHORIZATION'] ?? '');
    if ($authHdr === '') {
        $authHdr = (string)($_SERVER['REDIRECT_HTTP_AUTHORIZATION'] ?? '');
    }
    @file_put_contents(
        dirname(__DIR__) . '/oauth-debug.log',
        "\n==== " . gmdate('Y-m-d H:i:s') . " | TOKEN INVALID_CLIENT ====\n" . print_r([
            'presented_client_id' => (string)$auth['client_id'],
            'presented_secret_length' => strlen((string)$auth['client_secret']),
            'has_basic_auth_header' => (stripos($authHdr, 'Basic ') === 0),
            'expected_client_id' => (string)$cfg['oidc_client_id'],
            'expected_secret_length' => strlen((string)$cfg['oidc_client_secret']),
        ], true) . "\n",
        FILE_APPEND
    );
    http_response_code(401);
    echo json_encode(['error' => 'invalid_client']);
    exit;
}

$grantType = trim((string)($_POST['grant_type'] ?? ''));
$code = trim((string)($_POST['code'] ?? ''));
$redirectUri = trim((string)($_POST['redirect_uri'] ?? ''));
if ($grantType !== 'authorization_code' || $code === '' || $redirectUri === '') {
    http_response_code(400);
    echo json_encode(['error' => 'invalid_request']);
    exit;
}

$normalizeRedirect = static function (string $uri): string {
    $uri = trim($uri);
    if ($uri === '') {
        return '';
    }
    $parts = parse_url($uri);
    if (!is_array($parts) || empty($parts['scheme']) || empty($parts['host'])) {
        return $uri;
    }
    $scheme = strtolower((string)$parts['scheme']);
    $host = strtolower((string)$parts['host']);
    $port = isset($parts['port']) ? ':' . (int)$parts['port'] : '';
    $path = (string)($parts['path'] ?? '/');
    if ($path === '') {
        $path = '/';
    }
    $query = (string)($parts['query'] ?? '');
    return $scheme . '://' . $host . $port . $path . ($query !== '' ? ('?' . $query) : '');
};

try {
    $db = iauth_db_connect($cfg['db_name'], $cfg);
    iauth_oidc_db_init($db);

    $hash = hash('sha256', $code);
    $stmt = $db->prepare('SELECT client_id, redirect_uri, user_uuid, scope, nonce, expires_at FROM wp_iauth_oidc_codes WHERE code_hash = ? LIMIT 1');
    $stmt->bind_param('s', $hash);
    $stmt->execute();
    $row = $stmt->get_result()->fetch_assoc();
    $stmt->close();

    if (!$row) {
        http_response_code(400);
        echo json_encode(['error' => 'invalid_grant']);
        $db->close();
        exit;
    }

    $storedRedirect = $normalizeRedirect((string)$row['redirect_uri']);
    $postedRedirect = $normalizeRedirect($redirectUri);
    if ((int)$row['expires_at'] < time() || !hash_equals((string)$row['client_id'], (string)$auth['client_id']) || !hash_equals($storedRedirect, $postedRedirect)) {
        http_response_code(400);
        echo json_encode(['error' => 'invalid_grant']);
        $db->close();
        exit;
    }

    $robust = iauth_db_connect($cfg['robust_db'], $cfg);
    $profile = iauth_avatar_profile($robust, strtolower((string)$row['user_uuid']));
    $robust->close();
    if (!$profile) {
        http_response_code(400);
        echo json_encode(['error' => 'invalid_grant']);
        $db->close();
        exit;
    }

    $access = bin2hex(random_bytes(32));
    $accessHash = hash('sha256', $access);
    $now = time();
    $exp = $now + max(300, (int)$cfg['oidc_access_ttl_sec']);

    $ins = $db->prepare('INSERT INTO wp_iauth_oidc_tokens (access_hash, client_id, user_uuid, scope, expires_at, created_at) VALUES (?, ?, ?, ?, ?, ?)');
    $ins->bind_param('ssssii', $accessHash, $auth['client_id'], $row['user_uuid'], $row['scope'], $exp, $now);
    $ins->execute();
    $ins->close();

    $del = $db->prepare('DELETE FROM wp_iauth_oidc_codes WHERE code_hash = ?');
    $del->bind_param('s', $hash);
    $del->execute();
    $del->close();
    $db->close();

    $email = strtolower((string)$profile['uuid']) . '@i-grid.users';
    $preferred = iauth_oidc_username($profile);
    $policy = iauth_oidc_policy($cfg);
    $defaultGroup = trim((string)($policy['default_group_name'] ?? 'I-Grid Residents'));
    if ($defaultGroup === '') {
        $defaultGroup = 'I-Grid Residents';
    }
    $defaultRole = iauth_oidc_role_for_uuid($policy, strtolower((string)$profile['uuid']));
    $scopeParts = preg_split('/\s+/', trim((string)$row['scope'])) ?: [];
    $wantGroups = in_array('groups', $scopeParts, true);
    $wantRole = in_array('role', $scopeParts, true);

    $claims = [
        'iss' => $cfg['oidc_issuer'],
        'sub' => strtolower((string)$profile['uuid']),
        'aud' => (string)$auth['client_id'],
        'iat' => $now,
        'exp' => $exp,
        'auth_time' => $now,
        'preferred_username' => $preferred,
        'username' => $preferred,
        'email' => $email,
        'upn' => $email,
        'email_verified' => true,
        'name' => (string)$profile['full_name'],
        'given_name' => (string)$profile['first_name'],
        'family_name' => (string)$profile['last_name'],
        'avatar_uuid' => strtolower((string)$profile['uuid']),
    ];
    if ($wantGroups) {
        $claims['groups'] = [$defaultGroup];
    }
    if ($wantRole) {
        $claims['role'] = $defaultRole;
    }
    if (!empty($row['nonce'])) {
        $claims['nonce'] = (string)$row['nonce'];
    }

    $idToken = iauth_oidc_sign_jwt($claims, $cfg);

    echo json_encode([
        'access_token' => $access,
        'token_type' => 'Bearer',
        'expires_in' => max(1, $exp - $now),
        'id_token' => $idToken,
        'scope' => (string)$row['scope'],
        'username' => $preferred,
        'email' => $email,
    ], JSON_UNESCAPED_SLASHES);
} catch (Throwable $e) {
    @file_put_contents(
        dirname(__DIR__) . '/oauth-debug.log',
        "\n==== " . gmdate('Y-m-d H:i:s') . " | TOKEN EXCEPTION ====\n" . $e->getMessage() . "\n",
        FILE_APPEND
    );
    http_response_code(500);
    echo json_encode(['error' => 'server_error', 'error_description' => 'token_processing_failed']);
}
