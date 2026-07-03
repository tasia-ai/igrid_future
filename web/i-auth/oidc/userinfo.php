<?php
declare(strict_types=1);

require dirname(__DIR__) . '/lib.php';

$cfg = iauth_config();
header('Content-Type: application/json; charset=utf-8');

$hdr = (string)($_SERVER['HTTP_AUTHORIZATION'] ?? '');
if ($hdr === '') {
    $hdr = (string)($_SERVER['REDIRECT_HTTP_AUTHORIZATION'] ?? '');
}
if ($hdr === '' && function_exists('getallheaders')) {
    $headers = getallheaders();
    if (is_array($headers)) {
        foreach ($headers as $k => $v) {
            if (strtolower((string)$k) === 'authorization') {
                $hdr = (string)$v;
                break;
            }
        }
    }
}
if (stripos($hdr, 'Bearer ') !== 0) {
    http_response_code(401);
    echo json_encode(['error' => 'invalid_token']);
    exit;
}

$token = trim(substr($hdr, 7));
if ($token === '') {
    http_response_code(401);
    echo json_encode(['error' => 'invalid_token']);
    exit;
}

try {
    $db = iauth_db_connect($cfg['db_name'], $cfg);
    iauth_oidc_db_init($db);

    $hash = hash('sha256', $token);
    $stmt = $db->prepare('SELECT user_uuid, scope, expires_at FROM wp_iauth_oidc_tokens WHERE access_hash = ? LIMIT 1');
    $stmt->bind_param('s', $hash);
    $stmt->execute();
    $row = $stmt->get_result()->fetch_assoc();
    $stmt->close();
    $db->close();

    if (!$row || (int)$row['expires_at'] < time()) {
        http_response_code(401);
        echo json_encode(['error' => 'invalid_token']);
        exit;
    }

    $robust = iauth_db_connect($cfg['robust_db'], $cfg);
    $profile = iauth_avatar_profile($robust, strtolower((string)$row['user_uuid']));
    $robust->close();
    if (!$profile) {
        http_response_code(401);
        echo json_encode(['error' => 'invalid_token']);
        exit;
    }

    $email = strtolower((string)$profile['uuid']) . '@i-grid.users';
    $preferred = iauth_oidc_username($profile);
    $policy = iauth_oidc_policy($cfg);
    $defaultGroup = trim((string)($policy['default_group_name'] ?? 'I-Grid Residents'));
    if ($defaultGroup === '') {
        $defaultGroup = 'I-Grid Residents';
    }
    $defaultRole = iauth_oidc_role_for_uuid($policy, strtolower((string)$profile['uuid']));
    $scopeParts = preg_split('/\s+/', trim((string)($row['scope'] ?? ''))) ?: [];
    $wantGroups = in_array('groups', $scopeParts, true);
    $wantRole = in_array('role', $scopeParts, true);

    $payload = [
        'sub' => strtolower((string)$profile['uuid']),
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
        $payload['groups'] = [$defaultGroup];
    }
    if ($wantRole) {
        $payload['role'] = $defaultRole;
    }

    echo json_encode($payload, JSON_UNESCAPED_SLASHES);
} catch (Throwable $e) {
    http_response_code(500);
    echo json_encode(['error' => 'server_error']);
}
