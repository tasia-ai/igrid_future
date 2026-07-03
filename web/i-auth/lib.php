<?php
declare(strict_types=1);

function iauth_config(): array
{
    $scheme = (!empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off') ? 'https' : 'http';
    $host = $_SERVER['HTTP_HOST'] ?? 'i.let-us.cyou';
    $baseUrl = $scheme . '://' . $host . '/i-auth';

    return [
        'db_host' => getenv('IAUTH_DB_HOST') ?: 'i.let-us.cyou',
        'db_user' => getenv('IAUTH_DB_USER') ?: 'root',
        'db_pass' => getenv('IAUTH_DB_PASS') ?: 'CHANGE_ME_DB_PASSWORD',
        'db_name' => getenv('IAUTH_DB_NAME') ?: 'wordpress',
        'robust_db' => getenv('IAUTH_ROBUST_DB') ?: 'robust',

        'im_robust_url' => rtrim((string)(getenv('IAUTH_IM_ROBUST_URL') ?: 'http://i.let-us.cyou:8002'), '/'),
        'im_from_uuid' => strtolower(trim((string)(getenv('IAUTH_IM_FROM_UUID') ?: '00000000-0000-0000-0000-000000000000'))),
        'im_from_name' => trim((string)(getenv('IAUTH_IM_FROM_NAME') ?: 'I-Grid Security')),
        'otp_ttl_sec' => (int)(getenv('IAUTH_OTP_TTL_SEC') ?: 300),

        'keycloak_base' => rtrim((string)(getenv('IAUTH_KEYCLOAK_BASE') ?: 'https://keycloak.example.com'), '/'),
        'keycloak_realm' => (string)(getenv('IAUTH_KEYCLOAK_REALM') ?: 'i-grid'),
        'keycloak_client_id' => (string)(getenv('IAUTH_KEYCLOAK_CLIENT_ID') ?: 'i-auth-web'),
        'keycloak_client_secret' => (string)(getenv('IAUTH_KEYCLOAK_CLIENT_SECRET') ?: ''),
        'keycloak_redirect_uri' => (string)(getenv('IAUTH_KEYCLOAK_REDIRECT_URI') ?: ($baseUrl . '/callback.php')),

        'oidc_issuer' => rtrim((string)(getenv('IAUTH_OIDC_ISSUER') ?: ($baseUrl . '/oidc')), '/'),
        'oidc_client_id' => (string)(getenv('IAUTH_OIDC_CLIENT_ID') ?: 'marty-auth-broker'),
        'oidc_client_secret' => (string)(getenv('IAUTH_OIDC_CLIENT_SECRET') ?: 'CHANGE_ME_OIDC_CLIENT_SECRET'),
        'oidc_code_ttl_sec' => (int)(getenv('IAUTH_OIDC_CODE_TTL_SEC') ?: 180),
        'oidc_access_ttl_sec' => (int)(getenv('IAUTH_OIDC_ACCESS_TTL_SEC') ?: 3600),
        'oidc_keys_dir' => (string)(getenv('IAUTH_OIDC_KEYS_DIR') ?: (__DIR__ . '/.keys')),
    ];
}

function iauth_h(string $v): string
{
    return htmlspecialchars($v, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
}

function iauth_valid_uuid(string $uuid): bool
{
    return (bool)preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i', $uuid);
}

function iauth_db_connect(string $dbName, array $cfg): mysqli
{
    $db = @new mysqli($cfg['db_host'], $cfg['db_user'], $cfg['db_pass'], $dbName);
    if ($db->connect_errno) {
        throw new RuntimeException('DB connection failed: ' . $db->connect_error);
    }
    $db->set_charset('utf8mb4');
    return $db;
}

function iauth_avatar_profile(mysqli $robust, string $avatarUuid): ?array
{
    $stmt = $robust->prepare('SELECT PrincipalID, FirstName, LastName FROM UserAccounts WHERE PrincipalID = ? LIMIT 1');
    $stmt->bind_param('s', $avatarUuid);
    $stmt->execute();
    $row = $stmt->get_result()->fetch_assoc();
    $stmt->close();

    if (!$row) {
        return null;
    }

    $first = trim((string)($row['FirstName'] ?? ''));
    $last = trim((string)($row['LastName'] ?? ''));
    if ($first === '') {
        $first = 'Resident';
    }
    if ($last === '') {
        $last = 'User';
    }

    return [
        'uuid' => strtolower((string)$row['PrincipalID']),
        'first_name' => $first,
        'last_name' => $last,
        'full_name' => trim($first . ' ' . $last),
    ];
}

function iauth_avatar_lookup(mysqli $robust, string $avatarInput): array
{
    $input = trim($avatarInput);
    if ($input === '') {
        return ['profile' => null, 'matches' => []];
    }

    if (iauth_valid_uuid($input)) {
        return ['profile' => iauth_avatar_profile($robust, strtolower($input)), 'matches' => []];
    }

    $normalized = preg_replace('/\s+/', ' ', $input) ?? $input;
    $normalized = trim($normalized);
    if ($normalized === '') {
        return ['profile' => null, 'matches' => []];
    }

    $matches = [];
    $stmt = $robust->prepare("SELECT PrincipalID, FirstName, LastName
        FROM UserAccounts
        WHERE LOWER(CONCAT(TRIM(COALESCE(FirstName,'')), ' ', TRIM(COALESCE(LastName,'')))) = LOWER(?)
        ORDER BY FirstName ASC, LastName ASC
        LIMIT 5");
    if ($stmt) {
        $stmt->bind_param('s', $normalized);
        $stmt->execute();
        $res = $stmt->get_result();
        while ($row = $res->fetch_assoc()) {
            $first = trim((string)($row['FirstName'] ?? ''));
            $last = trim((string)($row['LastName'] ?? ''));
            if ($first === '') {
                $first = 'Resident';
            }
            if ($last === '') {
                $last = 'User';
            }
            $matches[] = [
                'uuid' => strtolower((string)($row['PrincipalID'] ?? '')),
                'first_name' => $first,
                'last_name' => $last,
                'full_name' => trim($first . ' ' . $last),
            ];
        }
        $stmt->close();
    }

    if (count($matches) === 0 && strpos($normalized, ' ') === false) {
        $stmt = $robust->prepare("SELECT PrincipalID, FirstName, LastName
            FROM UserAccounts
            WHERE LOWER(COALESCE(FirstName,'')) = LOWER(?) OR LOWER(COALESCE(LastName,'')) = LOWER(?)
            ORDER BY FirstName ASC, LastName ASC
            LIMIT 5");
        if ($stmt) {
            $stmt->bind_param('ss', $normalized, $normalized);
            $stmt->execute();
            $res = $stmt->get_result();
            while ($row = $res->fetch_assoc()) {
                $first = trim((string)($row['FirstName'] ?? ''));
                $last = trim((string)($row['LastName'] ?? ''));
                if ($first === '') {
                    $first = 'Resident';
                }
                if ($last === '') {
                    $last = 'User';
                }
                $matches[] = [
                    'uuid' => strtolower((string)($row['PrincipalID'] ?? '')),
                    'first_name' => $first,
                    'last_name' => $last,
                    'full_name' => trim($first . ' ' . $last),
                ];
            }
            $stmt->close();
        }
    }

    if (count($matches) === 1) {
        return ['profile' => $matches[0], 'matches' => []];
    }

    return ['profile' => null, 'matches' => $matches];
}

function iauth_http_post(string $url, string $body, string $contentType = 'application/json', int $timeoutSec = 10, array $extraHeaders = []): array
{
    $ch = curl_init($url);
    if ($ch === false) {
        return ['status' => 0, 'body' => '', 'error' => 'Unable to initialize curl'];
    }

    $headers = array_merge([
        'Content-Type: ' . $contentType,
        'Accept: application/json, text/xml, application/xml, text/plain, */*',
    ], $extraHeaders);

    curl_setopt_array($ch, [
        CURLOPT_POST => true,
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_CONNECTTIMEOUT => 4,
        CURLOPT_TIMEOUT => $timeoutSec,
        CURLOPT_HTTPHEADER => $headers,
        CURLOPT_POSTFIELDS => $body,
    ]);

    $raw = curl_exec($ch);
    $err = (string)curl_error($ch);
    $status = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    curl_close($ch);

    return ['status' => $status, 'body' => is_string($raw) ? $raw : '', 'error' => $err];
}

function iauth_xml_escape(string $value): string
{
    return htmlspecialchars($value, ENT_QUOTES | ENT_XML1 | ENT_SUBSTITUTE, 'UTF-8');
}

function iauth_send_im_otp(array $cfg, string $toAvatarUuid, string $message): array
{
    $payload = [
        'from_agent_id' => $cfg['im_from_uuid'],
        'to_agent_id' => $toAvatarUuid,
        'im_session_id' => iauth_uuid_v4(),
        'timestamp' => (string)time(),
        'from_agent_name' => $cfg['im_from_name'],
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

    $xml = '<?xml version="1.0"?><methodCall><methodName>grid_instant_message</methodName><params><param><value><struct>';
    foreach ($payload as $key => $value) {
        $xml .= '<member><name>' . iauth_xml_escape((string)$key) . '</name><value><string>'
            . iauth_xml_escape((string)$value) . '</string></value></member>';
    }
    $xml .= '</struct></value></param></params></methodCall>';

    return iauth_http_post($cfg['im_robust_url'] . '/', $xml, 'text/xml', 8);
}

function iauth_uuid_v4(): string
{
    $data = random_bytes(16);
    $data[6] = chr((ord($data[6]) & 0x0f) | 0x40);
    $data[8] = chr((ord($data[8]) & 0x3f) | 0x80);
    return vsprintf('%s%s-%s-%s-%s-%s%s%s', str_split(bin2hex($data), 4));
}

function iauth_keycloak_admin_token(array $cfg): string
{
    if ($cfg['keycloak_client_secret'] === '') {
        throw new RuntimeException('Missing IAUTH_KEYCLOAK_CLIENT_SECRET.');
    }

    $url = $cfg['keycloak_base'] . '/realms/' . rawurlencode($cfg['keycloak_realm']) . '/protocol/openid-connect/token';
    $body = http_build_query([
        'grant_type' => 'client_credentials',
        'client_id' => $cfg['keycloak_client_id'],
        'client_secret' => $cfg['keycloak_client_secret'],
    ]);
    $resp = iauth_http_post($url, $body, 'application/x-www-form-urlencoded', 10);
    if ($resp['error'] !== '' || $resp['status'] < 200 || $resp['status'] >= 300) {
        throw new RuntimeException('Keycloak token request failed: HTTP ' . $resp['status'] . ' ' . $resp['error']);
    }

    $json = json_decode($resp['body'], true);
    if (!is_array($json) || empty($json['access_token'])) {
        throw new RuntimeException('Keycloak token response missing access_token.');
    }
    return (string)$json['access_token'];
}

function iauth_keycloak_api(array $cfg, string $method, string $path, string $token, ?array $jsonBody = null): array
{
    $url = $cfg['keycloak_base'] . '/admin/realms/' . rawurlencode($cfg['keycloak_realm']) . $path;
    $ch = curl_init($url);
    if ($ch === false) {
        return ['status' => 0, 'body' => '', 'error' => 'curl init failed'];
    }

    $headers = [
        'Authorization: Bearer ' . $token,
        'Accept: application/json',
    ];
    if ($jsonBody !== null) {
        $headers[] = 'Content-Type: application/json';
    }

    $opts = [
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_CONNECTTIMEOUT => 4,
        CURLOPT_TIMEOUT => 12,
        CURLOPT_HTTPHEADER => $headers,
        CURLOPT_CUSTOMREQUEST => $method,
    ];
    if ($jsonBody !== null) {
        $opts[CURLOPT_POSTFIELDS] = json_encode($jsonBody, JSON_UNESCAPED_SLASHES);
    }
    curl_setopt_array($ch, $opts);

    $raw = curl_exec($ch);
    $err = (string)curl_error($ch);
    $status = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    curl_close($ch);
    return ['status' => $status, 'body' => is_string($raw) ? $raw : '', 'error' => $err];
}

function iauth_make_username(string $first, string $last): string
{
    $base = strtolower(trim($first . '_' . $last));
    $base = preg_replace('/[^a-z0-9_]+/', '_', $base) ?: 'resident_user';
    $base = trim($base, '_');
    if ($base === '') {
        $base = 'resident_user';
    }
    return $base . '_' . str_pad((string)random_int(0, 999), 3, '0', STR_PAD_LEFT);
}

function iauth_oidc_username(array $avatarProfile): string
{
    $first = (string)($avatarProfile['first_name'] ?? 'resident');
    $last = (string)($avatarProfile['last_name'] ?? 'user');
    $base = strtolower(trim($first . '_' . $last));
    $base = preg_replace('/[^a-z0-9_]+/', '_', $base) ?: 'resident_user';
    $base = trim($base, '_');
    if ($base === '') {
        $base = 'resident_user';
    }

    $uuid = strtolower((string)($avatarProfile['uuid'] ?? '00000000-0000-0000-0000-000000000000'));
    $seed = crc32($uuid);
    if ($seed < 0) {
        $seed = $seed * -1;
    }
    $suffix = str_pad((string)($seed % 1000), 3, '0', STR_PAD_LEFT);
    return $base . '_' . $suffix;
}

function iauth_uuid_lines(string $raw): array
{
    $parts = preg_split('/[\s,;]+/', strtolower($raw)) ?: [];
    $out = [];
    foreach ($parts as $p) {
        $v = trim((string)$p);
        if ($v === '' || !iauth_valid_uuid($v)) {
            continue;
        }
        $out[$v] = true;
    }
    return array_keys($out);
}

function iauth_ensure_keycloak_user(array $cfg, array $avatarProfile): array
{
    $token = iauth_keycloak_admin_token($cfg);
    $uuid = strtolower($avatarProfile['uuid']);
    $email = $uuid . '@i-grid.users';

    $searchResp = iauth_keycloak_api($cfg, 'GET', '/users?email=' . rawurlencode($email), $token, null);
    if ($searchResp['status'] >= 200 && $searchResp['status'] < 300) {
        $list = json_decode($searchResp['body'], true);
        if (is_array($list) && !empty($list[0]['id'])) {
            return [
                'id' => (string)$list[0]['id'],
                'username' => (string)$list[0]['username'],
                'email' => $email,
                'full_name' => $avatarProfile['full_name'],
            ];
        }
    }

    $username = iauth_make_username($avatarProfile['first_name'], $avatarProfile['last_name']);
    for ($i = 0; $i < 8; $i++) {
        $check = iauth_keycloak_api($cfg, 'GET', '/users?username=' . rawurlencode($username) . '&exact=true', $token, null);
        $exists = false;
        if ($check['status'] >= 200 && $check['status'] < 300) {
            $arr = json_decode($check['body'], true);
            $exists = is_array($arr) && count($arr) > 0;
        }
        if (!$exists) {
            break;
        }
        $username = iauth_make_username($avatarProfile['first_name'], $avatarProfile['last_name']);
    }

    $payload = [
        'username' => $username,
        'enabled' => true,
        'email' => $email,
        'emailVerified' => true,
        'firstName' => $avatarProfile['first_name'],
        'lastName' => $avatarProfile['last_name'],
        'attributes' => [
            'avatar_uuid' => [$uuid],
            'display_name' => [$avatarProfile['full_name']],
        ],
    ];
    $create = iauth_keycloak_api($cfg, 'POST', '/users', $token, $payload);
    if ($create['status'] < 200 || $create['status'] >= 300) {
        throw new RuntimeException('Keycloak user create failed: HTTP ' . $create['status'] . ' ' . $create['body']);
    }

    $search = iauth_keycloak_api($cfg, 'GET', '/users?email=' . rawurlencode($email), $token, null);
    if ($search['status'] < 200 || $search['status'] >= 300) {
        throw new RuntimeException('Keycloak user verify lookup failed: HTTP ' . $search['status']);
    }
    $arr = json_decode($search['body'], true);
    if (!is_array($arr) || empty($arr[0]['id'])) {
        throw new RuntimeException('Keycloak user created but not found in lookup.');
    }

    return [
        'id' => (string)$arr[0]['id'],
        'username' => $username,
        'email' => $email,
        'full_name' => $avatarProfile['full_name'],
    ];
}

function iauth_keycloak_login_url(array $cfg, string $loginHint, string $state): string
{
    $query = http_build_query([
        'client_id' => $cfg['keycloak_client_id'],
        'response_type' => 'code',
        'scope' => 'openid profile email',
        'redirect_uri' => $cfg['keycloak_redirect_uri'],
        'state' => $state,
        'login_hint' => $loginHint,
    ]);
    return $cfg['keycloak_base'] . '/realms/' . rawurlencode($cfg['keycloak_realm']) . '/protocol/openid-connect/auth?' . $query;
}

function iauth_oidc_db_init(mysqli $db): void
{
    $db->query("CREATE TABLE IF NOT EXISTS wp_iauth_oidc_codes (
        code_hash CHAR(64) PRIMARY KEY,
        client_id VARCHAR(191) NOT NULL,
        redirect_uri TEXT NOT NULL,
        user_uuid CHAR(36) NOT NULL,
        scope VARCHAR(255) NOT NULL,
        nonce VARCHAR(255) DEFAULT NULL,
        expires_at INT NOT NULL,
        created_at INT NOT NULL
    )");

    $db->query("CREATE TABLE IF NOT EXISTS wp_iauth_oidc_tokens (
        access_hash CHAR(64) PRIMARY KEY,
        client_id VARCHAR(191) NOT NULL,
        user_uuid CHAR(36) NOT NULL,
        scope VARCHAR(255) NOT NULL,
        expires_at INT NOT NULL,
        created_at INT NOT NULL
    )");

    $now = time();
    $db->query("DELETE FROM wp_iauth_oidc_codes WHERE expires_at < " . (int)$now);
    $db->query("DELETE FROM wp_iauth_oidc_tokens WHERE expires_at < " . (int)$now);
}

function iauth_urlencode_b64url(string $raw): string
{
    return rtrim(strtr(base64_encode($raw), '+/', '-_'), '=');
}

function iauth_oidc_get_or_create_private_key(array $cfg): OpenSSLAsymmetricKey
{
    $dir = rtrim((string)$cfg['oidc_keys_dir'], '/');
    if ($dir === '') {
        $dir = rtrim(sys_get_temp_dir(), '/') . '/i-auth-keys';
    }
    $path = $dir . '/oidc_rsa_private.pem';

    if (!is_dir($dir)) {
        @mkdir($dir, 0700, true);
    }
    if (!is_dir($dir) || !is_writable($dir)) {
        $fallbackDir = rtrim(sys_get_temp_dir(), '/') . '/i-auth-keys';
        if (!is_dir($fallbackDir)) {
            @mkdir($fallbackDir, 0700, true);
        }
        if (is_dir($fallbackDir) && is_writable($fallbackDir)) {
            $dir = $fallbackDir;
            $path = $dir . '/oidc_rsa_private.pem';
        } else {
            try {
                $db = iauth_db_connect($cfg['db_name'], $cfg);
                $db->query("CREATE TABLE IF NOT EXISTS wp_iauth_oidc_keys (
                    id TINYINT PRIMARY KEY,
                    private_pem LONGTEXT NOT NULL,
                    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
                )");

                $row = $db->query('SELECT private_pem FROM wp_iauth_oidc_keys WHERE id = 1 LIMIT 1');
                $data = $row ? $row->fetch_assoc() : null;
                $pem = is_array($data) ? (string)($data['private_pem'] ?? '') : '';

                if ($pem === '') {
                    $res = openssl_pkey_new([
                        'private_key_bits' => 2048,
                        'private_key_type' => OPENSSL_KEYTYPE_RSA,
                    ]);
                    if ($res === false) {
                        throw new RuntimeException('OpenSSL key generation failed.');
                    }
                    openssl_pkey_export($res, $pem);
                    $stmt = $db->prepare('INSERT INTO wp_iauth_oidc_keys (id, private_pem) VALUES (1, ?) ON DUPLICATE KEY UPDATE private_pem = VALUES(private_pem)');
                    $stmt->bind_param('s', $pem);
                    $stmt->execute();
                    $stmt->close();
                }
                $db->close();

                $key = openssl_pkey_get_private($pem);
                if ($key === false) {
                    throw new RuntimeException('Failed to load DB-backed OIDC key.');
                }
                return $key;
            } catch (Throwable $e) {
                throw new RuntimeException('OIDC key storage is not writable (filesystem and DB fallback failed): ' . $e->getMessage());
            }
        }
    }

    if (!is_file($path)) {
        $res = openssl_pkey_new([
            'private_key_bits' => 2048,
            'private_key_type' => OPENSSL_KEYTYPE_RSA,
        ]);
        if ($res === false) {
            throw new RuntimeException('OpenSSL key generation failed.');
        }
        $pem = '';
        openssl_pkey_export($res, $pem);
        if ($pem === '' || @file_put_contents($path, $pem) === false) {
            throw new RuntimeException('Failed to write OIDC private key: ' . $path);
        }
        @chmod($path, 0600);
    }

    $pem = (string)@file_get_contents($path);
    $key = openssl_pkey_get_private($pem);
    if ($key === false) {
        throw new RuntimeException('Failed to load OIDC private key: ' . $path);
    }
    return $key;
}

function iauth_oidc_kid(OpenSSLAsymmetricKey $key): string
{
    $d = openssl_pkey_get_details($key);
    if (!is_array($d) || empty($d['key'])) {
        throw new RuntimeException('Unable to read public key details.');
    }
    return substr(hash('sha256', (string)$d['key']), 0, 32);
}

function iauth_oidc_sign_jwt(array $claims, array $cfg): string
{
    $key = iauth_oidc_get_or_create_private_key($cfg);
    $header = [
        'alg' => 'RS256',
        'typ' => 'JWT',
        'kid' => iauth_oidc_kid($key),
    ];

    $h = iauth_urlencode_b64url((string)json_encode($header, JSON_UNESCAPED_SLASHES));
    $p = iauth_urlencode_b64url((string)json_encode($claims, JSON_UNESCAPED_SLASHES));
    $data = $h . '.' . $p;

    $sig = '';
    if (!openssl_sign($data, $sig, $key, OPENSSL_ALGO_SHA256)) {
        throw new RuntimeException('JWT signing failed.');
    }
    return $data . '.' . iauth_urlencode_b64url($sig);
}

function iauth_oidc_jwks(array $cfg): array
{
    $key = iauth_oidc_get_or_create_private_key($cfg);
    $d = openssl_pkey_get_details($key);
    if (!is_array($d) || empty($d['rsa']['n']) || empty($d['rsa']['e'])) {
        throw new RuntimeException('Unable to build JWKS from key details.');
    }

    return [
        'keys' => [[
            'kty' => 'RSA',
            'use' => 'sig',
            'alg' => 'RS256',
            'kid' => iauth_oidc_kid($key),
            'n' => iauth_urlencode_b64url((string)$d['rsa']['n']),
            'e' => iauth_urlencode_b64url((string)$d['rsa']['e']),
        ]],
    ];
}

function iauth_oidc_client_ok(array $cfg, string $clientId, string $clientSecret): bool
{
    if ($clientId === '' || $clientSecret === '') {
        return false;
    }
    return hash_equals((string)$cfg['oidc_client_id'], $clientId) && hash_equals((string)$cfg['oidc_client_secret'], $clientSecret);
}

function iauth_oidc_parse_client_auth(): array
{
    $id = '';
    $secret = '';

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
    if (stripos($hdr, 'Basic ') === 0) {
        $decoded = base64_decode(substr($hdr, 6), true);
        if (is_string($decoded) && strpos($decoded, ':') !== false) {
            [$id, $secret] = explode(':', $decoded, 2);
            return ['client_id' => (string)$id, 'client_secret' => (string)$secret];
        }
    }

    $id = trim((string)($_POST['client_id'] ?? ''));
    $secret = trim((string)($_POST['client_secret'] ?? ''));
    return ['client_id' => $id, 'client_secret' => $secret];
}

function iauth_oidc_policy(array $cfg): array
{
    try {
        $db = iauth_db_connect($cfg['db_name'], $cfg);
        $row = $db->query('SELECT redirect_allowlist_enabled, redirect_allowlist_text, default_group_name, default_role, admin_role_uuids FROM wp_iauth_oidc_settings WHERE id = 1 LIMIT 1');
        $cfgRow = $row ? $row->fetch_assoc() : null;
        $db->close();

        if (!is_array($cfgRow)) {
            return ['enabled' => false, 'rules' => [], 'default_group_name' => 'I-Grid Residents', 'default_role' => 'user', 'admin_role_uuids' => []];
        }

        $enabled = (int)($cfgRow['redirect_allowlist_enabled'] ?? 0) === 1;
        $raw = (string)($cfgRow['redirect_allowlist_text'] ?? '');
        $rules = [];
        foreach (preg_split('/\r\n|\r|\n/', $raw) ?: [] as $line) {
            $line = trim($line);
            if ($line === '' || strpos($line, '#') === 0) {
                continue;
            }
            $rules[] = $line;
        }
        $defaultGroup = trim((string)($cfgRow['default_group_name'] ?? 'I-Grid Residents'));
        if ($defaultGroup === '') {
            $defaultGroup = 'I-Grid Residents';
        }
        $defaultRole = strtolower(trim((string)($cfgRow['default_role'] ?? 'user')));
        if ($defaultRole !== 'admin') {
            $defaultRole = 'user';
        }
        $adminRoleUuids = iauth_uuid_lines((string)($cfgRow['admin_role_uuids'] ?? ''));

        return [
            'enabled' => $enabled,
            'rules' => array_values(array_unique($rules)),
            'default_group_name' => $defaultGroup,
            'default_role' => $defaultRole,
            'admin_role_uuids' => $adminRoleUuids,
        ];
    } catch (Throwable $e) {
        return ['enabled' => false, 'rules' => [], 'default_group_name' => 'I-Grid Residents', 'default_role' => 'user', 'admin_role_uuids' => []];
    }
}

function iauth_oidc_role_for_uuid(array $policy, string $avatarUuid): string
{
    $uuid = strtolower(trim($avatarUuid));
    $admins = is_array($policy['admin_role_uuids'] ?? null) ? $policy['admin_role_uuids'] : [];
    if (in_array($uuid, $admins, true)) {
        return 'admin';
    }
    $defaultRole = strtolower(trim((string)($policy['default_role'] ?? 'user')));
    return $defaultRole === 'admin' ? 'admin' : 'user';
}

function iauth_oidc_redirect_allowed(array $cfg, string $redirectUri): bool
{
    $policy = iauth_oidc_policy($cfg);
    if (empty($policy['enabled'])) {
        return true;
    }

    $redirectUri = trim($redirectUri);
    if ($redirectUri === '') {
        return false;
    }

    $rules = is_array($policy['rules']) ? $policy['rules'] : [];
    if (empty($rules)) {
        return false;
    }

    foreach ($rules as $rule) {
        $rule = trim((string)$rule);
        if ($rule === '') {
            continue;
        }
        if (substr($rule, -1) === '*') {
            $prefix = substr($rule, 0, -1);
            if ($prefix !== '' && strpos($redirectUri, $prefix) === 0) {
                return true;
            }
            continue;
        }
        if (hash_equals($rule, $redirectUri)) {
            return true;
        }
    }

    return false;
}
