<?php
declare(strict_types=1);

header('Content-Type: application/json; charset=utf-8');

function ar_json(int $status, array $payload): void
{
    http_response_code($status);
    echo json_encode($payload, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
    exit;
}

if (($_SERVER['REQUEST_METHOD'] ?? 'GET') !== 'POST') {
    ar_json(405, ['ok' => false, 'error' => 'method_not_allowed']);
}

$token = trim((string)(getenv('IGRID_ABUSE_REPORT_TOKEN') ?: ''));
if ($token === '') {
    $tokenPath = __DIR__ . '/abuse_report_token.txt';
    if (is_file($tokenPath)) {
        $token = trim((string)file_get_contents($tokenPath));
    }
}

if ($token === '' || $token === 'CHANGE_ME_ABUSE_TOKEN') {
    ar_json(503, ['ok' => false, 'error' => 'token_not_configured']);
}

$authHeader = (string)($_SERVER['HTTP_AUTHORIZATION'] ?? '');
$xToken = trim((string)($_SERVER['HTTP_X_ABUSE_TOKEN'] ?? ''));
$provided = '';
if (stripos($authHeader, 'Bearer ') === 0) {
    $provided = trim(substr($authHeader, 7));
}
if ($provided === '' && $xToken !== '') {
    $provided = $xToken;
}

if ($provided === '' || !hash_equals($token, $provided)) {
    ar_json(401, ['ok' => false, 'error' => 'unauthorized']);
}

$rawBody = file_get_contents('php://input');
if (!is_string($rawBody) || trim($rawBody) === '') {
    ar_json(400, ['ok' => false, 'error' => 'empty_body']);
}

$payload = json_decode($rawBody, true);
if (!is_array($payload)) {
    ar_json(400, ['ok' => false, 'error' => 'invalid_json']);
}

$host = 'i.let-us.cyou';
$user = 'root';
$pass = 'CHANGE_ME_DB_PASSWORD';
$dbname = 'wordpress';

$db = new mysqli($host, $user, $pass, $dbname);
if ($db->connect_error) {
    ar_json(500, ['ok' => false, 'error' => 'db_connect_failed']);
}

$db->query("CREATE TABLE IF NOT EXISTS wp_igrid_abuse_reports (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    received_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    source_region_uuid VARCHAR(36) DEFAULT '',
    source_region_name VARCHAR(128) DEFAULT '',
    reported_region_name VARCHAR(128) DEFAULT '',
    reporter_uuid VARCHAR(36) DEFAULT '',
    reporter_name VARCHAR(128) DEFAULT '',
    abuser_uuid VARCHAR(36) DEFAULT '',
    category TINYINT UNSIGNED DEFAULT 0,
    checkflags TINYINT UNSIGNED DEFAULT 0,
    report_type TINYINT UNSIGNED DEFAULT 0,
    summary TEXT,
    details MEDIUMTEXT,
    object_uuid VARCHAR(36) DEFAULT '',
    screenshot_uuid VARCHAR(36) DEFAULT '',
    screenshot_uuid_original VARCHAR(36) DEFAULT '',
    screenshot_status VARCHAR(32) DEFAULT '',
    position_x DOUBLE DEFAULT NULL,
    position_y DOUBLE DEFAULT NULL,
    position_z DOUBLE DEFAULT NULL,
    remote_addr VARCHAR(64) DEFAULT '',
    raw_payload MEDIUMTEXT,
    INDEX idx_received_at (received_at),
    INDEX idx_reporter_uuid (reporter_uuid),
    INDEX idx_abuser_uuid (abuser_uuid),
    INDEX idx_source_region_uuid (source_region_uuid)
)");

$colCheck = $db->query("SHOW COLUMNS FROM wp_igrid_abuse_reports LIKE 'screenshot_uuid_original'");
if ($colCheck && $colCheck->num_rows === 0) {
    $db->query("ALTER TABLE wp_igrid_abuse_reports ADD COLUMN screenshot_uuid_original VARCHAR(36) DEFAULT '' AFTER screenshot_uuid");
}
$colCheck = $db->query("SHOW COLUMNS FROM wp_igrid_abuse_reports LIKE 'screenshot_status'");
if ($colCheck && $colCheck->num_rows === 0) {
    $db->query("ALTER TABLE wp_igrid_abuse_reports ADD COLUMN screenshot_status VARCHAR(32) DEFAULT '' AFTER screenshot_uuid_original");
}

$sourceRegionUuid = substr((string)($payload['source_region_uuid'] ?? ''), 0, 36);
$sourceRegionName = substr((string)($payload['source_region_name'] ?? ''), 0, 128);
$reportedRegionName = substr((string)($payload['reported_region_name'] ?? ''), 0, 128);
$reporterUuid = substr((string)($payload['reporter_uuid'] ?? ''), 0, 36);
$reporterName = substr((string)($payload['reporter_name'] ?? ''), 0, 128);
$abuserUuid = substr((string)($payload['abuser_uuid'] ?? ''), 0, 36);
$category = (int)($payload['category'] ?? 0);
$checkflags = (int)($payload['checkflags'] ?? 0);
$reportType = (int)($payload['report_type'] ?? 0);
$summary = (string)($payload['summary'] ?? '');
$details = (string)($payload['details'] ?? '');
$objectUuid = substr((string)($payload['object_uuid'] ?? ''), 0, 36);
$screenshotUuid = substr((string)($payload['screenshot_uuid'] ?? ''), 0, 36);
$screenshotUuidOriginal = substr((string)($payload['screenshot_uuid_original'] ?? ''), 0, 36);
$screenshotStatus = substr((string)($payload['screenshot_status'] ?? ''), 0, 32);
$positionX = isset($payload['position_x']) ? (float)$payload['position_x'] : null;
$positionY = isset($payload['position_y']) ? (float)$payload['position_y'] : null;
$positionZ = isset($payload['position_z']) ? (float)$payload['position_z'] : null;
$remoteAddr = substr((string)($_SERVER['REMOTE_ADDR'] ?? ''), 0, 64);

$stmt = $db->prepare("INSERT INTO wp_igrid_abuse_reports (
    source_region_uuid,
    source_region_name,
    reported_region_name,
    reporter_uuid,
    reporter_name,
    abuser_uuid,
    category,
    checkflags,
    report_type,
    summary,
    details,
    object_uuid,
    screenshot_uuid,
    screenshot_uuid_original,
    screenshot_status,
    position_x,
    position_y,
    position_z,
    remote_addr,
    raw_payload
) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)");

if (!$stmt) {
    $db->close();
    ar_json(500, ['ok' => false, 'error' => 'prepare_failed']);
}

$stmt->bind_param(
    'ssssssiiissssssdddss',
    $sourceRegionUuid,
    $sourceRegionName,
    $reportedRegionName,
    $reporterUuid,
    $reporterName,
    $abuserUuid,
    $category,
    $checkflags,
    $reportType,
    $summary,
    $details,
    $objectUuid,
    $screenshotUuid,
    $screenshotUuidOriginal,
    $screenshotStatus,
    $positionX,
    $positionY,
    $positionZ,
    $remoteAddr,
    $rawBody
);

if (!$stmt->execute()) {
    $stmt->close();
    $db->close();
    ar_json(500, ['ok' => false, 'error' => 'insert_failed']);
}

$insertId = (int)$stmt->insert_id;
$stmt->close();
$db->close();

ar_json(200, ['ok' => true, 'report_id' => $insertId]);
