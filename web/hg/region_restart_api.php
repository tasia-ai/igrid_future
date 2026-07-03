<?php

declare(strict_types=1);

session_start();
header('Content-Type: application/json');

$dbHost = "i.let-us.cyou";
$dbUser = "root";
$dbPass = "CHANGE_ME_DB_PASSWORD";
$wpDb = "wordpress";
$robustDb = "robust";

$ownerUuid = strtolower(trim((string)(getenv('IGRID_OWNER_UUID') ?: '')));
$simToken = trim((string)(getenv('IGRID_RESTART_TOKEN') ?: ''));
if ($simToken === '') {
    $tokenPath = __DIR__ . '/restart_sim_token.txt';
    if (is_file($tokenPath)) {
        $simToken = trim((string)file_get_contents($tokenPath));
    }
}

if ($simToken === 'CHANGE_ME_RESTART_TOKEN') {
    $simToken = '';
}

function rr_json(int $status, array $payload): void
{
    http_response_code($status);
    echo json_encode($payload, JSON_UNESCAPED_SLASHES);
    exit;
}

function rr_is_uuid(string $value): bool
{
    return (bool)preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i', $value);
}

$raw = file_get_contents('php://input');
$payload = [];
if (is_string($raw) && trim($raw) !== '') {
    $decoded = json_decode($raw, true);
    if (!is_array($decoded)) {
        rr_json(400, ['ok' => false, 'error' => 'Invalid JSON payload']);
    }
    $payload = $decoded;
} else {
    $payload = $_POST;
}

$action = strtolower(trim((string)($payload['action'] ?? 'status')));
$regionUuid = strtolower(trim((string)($payload['region_uuid'] ?? '')));
$requesterUuid = strtolower(trim((string)($payload['requester_uuid'] ?? '')));
$requesterKey = trim((string)($payload['api_key'] ?? ''));
$delaySeconds = (int)($payload['delay_seconds'] ?? 180);
$reason = trim((string)($payload['reason'] ?? ''));

if (!in_array($action, ['schedule', 'cancel', 'status'], true)) {
    rr_json(400, ['ok' => false, 'error' => 'Invalid action']);
}

if (!rr_is_uuid($regionUuid)) {
    rr_json(400, ['ok' => false, 'error' => 'Invalid region_uuid']);
}

if ($delaySeconds < 10) {
    $delaySeconds = 10;
}
if ($delaySeconds > 86400) {
    $delaySeconds = 86400;
}

$wpConn = new mysqli($dbHost, $dbUser, $dbPass, $wpDb);
$robustConn = new mysqli($dbHost, $dbUser, $dbPass, $robustDb);
if ($wpConn->connect_error || $robustConn->connect_error) {
    rr_json(500, ['ok' => false, 'error' => 'Database connection failed']);
}

$wpConn->query("CREATE TABLE IF NOT EXISTS wp_igrid_region_restart_acl (
    id INT AUTO_INCREMENT PRIMARY KEY,
    opensim_uuid VARCHAR(36) NOT NULL UNIQUE,
    display_name VARCHAR(128) DEFAULT '',
    api_key VARCHAR(128) NOT NULL,
    can_restart TINYINT(1) DEFAULT 1,
    can_cancel TINYINT(1) DEFAULT 1,
    enabled TINYINT(1) DEFAULT 1,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
)");

$wpConn->query("CREATE TABLE IF NOT EXISTS wp_igrid_region_restart_log (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    requested_by_uuid VARCHAR(36) DEFAULT NULL,
    auth_mode VARCHAR(32) NOT NULL,
    region_uuid VARCHAR(36) NOT NULL,
    region_name VARCHAR(128) DEFAULT '',
    action VARCHAR(16) NOT NULL,
    delay_seconds INT DEFAULT NULL,
    reason TEXT DEFAULT NULL,
    endpoint_url VARCHAR(255) NOT NULL,
    http_status INT DEFAULT NULL,
    response_body MEDIUMTEXT,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_region_uuid (region_uuid),
    INDEX idx_created_at (created_at)
)");

$isPanelAdmin = !empty($_SESSION['admin_logged_in']) && (($_SESSION['admin_role'] ?? '') === 'admin');
$canRestart = false;
$canCancel = false;
$authMode = 'none';

if ($isPanelAdmin) {
    $authMode = 'panel_admin';
    $canRestart = true;
    $canCancel = true;
    if ($requesterUuid === '' && rr_is_uuid($ownerUuid)) {
        $requesterUuid = $ownerUuid;
    }
} else {
    if (!rr_is_uuid($requesterUuid) || $requesterKey === '') {
        rr_json(401, ['ok' => false, 'error' => 'Missing requester_uuid/api_key']);
    }

    $stmt = $wpConn->prepare("SELECT api_key, can_restart, can_cancel, enabled FROM wp_igrid_region_restart_acl WHERE opensim_uuid = ? LIMIT 1");
    $stmt->bind_param("s", $requesterUuid);
    $stmt->execute();
    $stmt->bind_result($aclApiKey, $aclCanRestart, $aclCanCancel, $aclEnabled);
    $acl = null;
    if ($stmt->fetch()) {
        $acl = [
            'api_key' => (string)$aclApiKey,
            'can_restart' => (int)$aclCanRestart,
            'can_cancel' => (int)$aclCanCancel,
            'enabled' => (int)$aclEnabled,
        ];
    }
    $stmt->close();

    if (!$acl || (int)$acl['enabled'] !== 1 || !hash_equals((string)$acl['api_key'], $requesterKey)) {
        rr_json(403, ['ok' => false, 'error' => 'Not authorized']);
    }

    $authMode = 'acl';
    $canRestart = (int)$acl['can_restart'] === 1;
    $canCancel = (int)$acl['can_cancel'] === 1;
}

if ($action === 'schedule' && !$canRestart) {
    rr_json(403, ['ok' => false, 'error' => 'No restart permission']);
}
if ($action === 'cancel' && !$canCancel) {
    rr_json(403, ['ok' => false, 'error' => 'No cancel permission']);
}
if ($action === 'status' && !($canRestart || $canCancel)) {
    rr_json(403, ['ok' => false, 'error' => 'No status permission']);
}

$regionStmt = $robustConn->prepare("SELECT uuid, regionName, serverURI FROM regions WHERE uuid = ? LIMIT 1");
$regionStmt->bind_param("s", $regionUuid);
$regionStmt->execute();
$regionStmt->bind_result($regionDbUuid, $regionDbName, $regionDbServerUri);
$region = null;
if ($regionStmt->fetch()) {
    $region = [
        'uuid' => (string)$regionDbUuid,
        'regionName' => (string)$regionDbName,
        'serverURI' => (string)$regionDbServerUri,
    ];
}
$regionStmt->close();

if (!$region) {
    rr_json(404, ['ok' => false, 'error' => 'Region not found']);
}

$serverUri = trim((string)($region['serverURI'] ?? ''));
if ($serverUri === '') {
    rr_json(422, ['ok' => false, 'error' => 'Region has no serverURI']);
}

if ($simToken === '') {
    rr_json(500, ['ok' => false, 'error' => 'Restart simulator token is not configured']);
}

$endpointUrl = rtrim($serverUri, '/') . '/tasia-ngc/restart/' . $regionUuid;

$forwardPayload = [
    'action' => $action,
    'requested_by' => rr_is_uuid($requesterUuid) ? $requesterUuid : '00000000-0000-0000-0000-000000000000',
    'reason' => $reason,
];
if ($action === 'schedule') {
    $forwardPayload['delay_seconds'] = $delaySeconds;
}

$ch = curl_init($endpointUrl);
curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
curl_setopt($ch, CURLOPT_POST, true);
curl_setopt($ch, CURLOPT_HTTPHEADER, [
    'Content-Type: application/json',
    'Authorization: Bearer ' . $simToken,
]);
curl_setopt($ch, CURLOPT_POSTFIELDS, json_encode($forwardPayload));
curl_setopt($ch, CURLOPT_CONNECTTIMEOUT, 4);
curl_setopt($ch, CURLOPT_TIMEOUT, 8);

$respBody = curl_exec($ch);
$httpStatus = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
$curlErr = curl_error($ch);
curl_close($ch);

if (!is_string($respBody)) {
    $respBody = '';
}

$respDecoded = json_decode($respBody, true);
$simResponseIsJson = is_array($respDecoded);
if (!$simResponseIsJson) {
    $respDecoded = ['ok' => false, 'status' => 'invalid_response', 'raw' => $respBody];
}

$logStmt = $wpConn->prepare("INSERT INTO wp_igrid_region_restart_log
    (requested_by_uuid, auth_mode, region_uuid, region_name, action, delay_seconds, reason, endpoint_url, http_status, response_body)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)");
$logDelay = ($action === 'schedule') ? $delaySeconds : null;
$logResponse = substr($respBody, 0, 65535);
$regionName = (string)$region['regionName'];
$logStmt->bind_param(
    "sssssissis",
    $requesterUuid,
    $authMode,
    $regionUuid,
    $regionName,
    $action,
    $logDelay,
    $reason,
    $endpointUrl,
    $httpStatus,
    $logResponse
);
$logStmt->execute();
$logStmt->close();

$wpConn->close();
$robustConn->close();

if ($httpStatus < 200 || $httpStatus >= 300) {
    rr_json(502, [
        'ok' => false,
        'error' => 'Simulator endpoint failed',
        'http_status' => $httpStatus,
        'curl_error' => $curlErr,
        'sim_response' => $respDecoded,
    ]);
}

if (!$simResponseIsJson) {
    rr_json(502, [
        'ok' => false,
        'error' => 'Simulator endpoint returned invalid response body',
        'http_status' => $httpStatus,
        'endpoint' => $endpointUrl,
        'hint' => 'Restart module API likely not loaded in simulator (old DLL or no restart yet).',
        'sim_response' => $respDecoded,
    ]);
}

rr_json(200, [
    'ok' => true,
    'action' => $action,
    'region_uuid' => $regionUuid,
    'region_name' => $regionName,
    'endpoint' => $endpointUrl,
    'sim_response' => $respDecoded,
]);
