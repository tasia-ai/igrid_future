<?php
/**
 * I-Grid Display Name API
 *
 * Centralized display name service for the I-Grid.
 * Provides get/set/reset actions over JSON.
 *
 * Environment variables:
 *   IGRID_DB_HOST      - MySQL host (required)
 *   IGRID_DB_USER      - MySQL user (required)
 *   IGRID_DB_PASS      - MySQL password (required)
 *   IGRID_DB_ROBUST    - Database name (default: robust)
 *   IGRID_DISPLAYNAME_TOKEN          - Bearer token for write ops
 *   IGRID_DISPLAYNAME_REQUIRE_TOKEN  - 0|1 (default: 0)
 */

declare(strict_types=1);

header('Content-Type: application/json');
header('Access-Control-Allow-Origin: *');
header('Access-Control-Allow-Methods: GET, POST, OPTIONS');
header('Access-Control-Allow-Headers: Content-Type, Authorization');

if ($_SERVER['REQUEST_METHOD'] === 'OPTIONS') {
    http_response_code(204);
    exit;
}

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

$dbHost = getenv('IGRID_DB_HOST');
$dbUser = getenv('IGRID_DB_USER');
$dbPass = getenv('IGRID_DB_PASS');
$dbName = getenv('IGRID_DB_ROBUST') ?: 'robust';
$table  = 'UserAccounts';

// Token for write operations (set/reset)
$token        = trim((string) (getenv('IGRID_DISPLAYNAME_TOKEN') ?: ''));
$requireToken = in_array(
    strtolower(trim((string) (getenv('IGRID_DISPLAYNAME_REQUIRE_TOKEN') ?: '0'))),
    ['1', 'true', 'yes', 'on'],
    true
);

if ($token === '') {
    $tokenPath = __DIR__ . '/displayname_token.txt';
    if (is_file($tokenPath)) {
        $token = trim((string) file_get_contents($tokenPath));
    }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function dn_json(int $status, array $payload): void
{
    http_response_code($status);
    echo json_encode($payload, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
    exit;
}

function dn_is_uuid(string $value): bool
{
    return (bool) preg_match(
        '/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i',
        $value
    );
}

/**
 * Return ISO-8601 datetime of the next allowed change.
 * Cooldown is 7 days from $nameChanged (unix timestamp stored in DB).
 */
function dn_next_allowed_iso(?string $nameChanged): string
{
    $ts = (is_numeric($nameChanged) ? (int) $nameChanged : 0);
    if ($ts <= 0) {
        return gmdate('c', time());
    }
    return gmdate('c', $ts + 7 * 24 * 60 * 60);
}

/**
 * Authenticate write requests.
 */
function dn_require_token(string $token): void
{
    if ($token === '') {
        dn_json(500, ['ok' => false, 'error' => 'displayname token is not configured']);
    }

    $provided = '';

    // Prefer Authorization: Bearer header
    $auth = $_SERVER['HTTP_AUTHORIZATION'] ?? '';
    if (preg_match('/^Bearer\s+(.+)$/i', $auth, $m)) {
        $provided = trim($m[1]);
    }

    // Fallback: ?token= or body token
    if ($provided === '') {
        $provided = trim((string) ($_POST['token'] ?? $_GET['token'] ?? ''));
    }

    if ($provided === '' || !hash_equals($token, $provided)) {
        dn_json(401, ['ok' => false, 'error' => 'unauthorized']);
    }
}

/**
 * Determine action from path info, URI suffix, or ?action= param.
 */
function dn_action(): string
{
    $pathInfo = trim((string) ($_SERVER['PATH_INFO'] ?? ''), '/');
    if ($pathInfo !== '') {
        return strtolower($pathInfo);
    }

    $uri = trim((string) parse_url((string) ($_SERVER['REQUEST_URI'] ?? ''), PHP_URL_PATH), '/');
    if (str_ends_with($uri, '/get'))   return 'get';
    if (str_ends_with($uri, '/set'))   return 'set';
    if (str_ends_with($uri, '/reset')) return 'reset';

    $a = trim((string) ($_GET['action'] ?? $_POST['action'] ?? 'get'));
    return strtolower($a === '' ? 'get' : $a);
}

/**
 * Parse request body as JSON if Content-Type is JSON.
 */
function dn_json_body(): array
{
    $ct = strtolower(trim((string) ($_SERVER['CONTENT_TYPE'] ?? '')));
    if (str_contains($ct, '/json')) {
        $raw = (string) file_get_contents('php://input');
        $decoded = json_decode($raw, true);
        return is_array($decoded) ? $decoded : [];
    }
    return [];
}

/**
 * Get a value from JSON body first, then POST, then GET.
 */
function dn_param(string $key, string $default = ''): string
{
    static $body = null;
    if ($body === null) {
        $body = dn_json_body();
    }
    if (isset($body[$key]) && is_scalar($body[$key])) {
        return trim((string) $body[$key]);
    }
    return trim((string) ($_POST[$key] ?? $_GET[$key] ?? $default));
}

// ---------------------------------------------------------------------------
// Route
// ---------------------------------------------------------------------------

$action = dn_action();
if (!in_array($action, ['get', 'set', 'reset'], true)) {
    dn_json(400, ['ok' => false, 'error' => 'invalid action', 'valid_actions' => ['get', 'set', 'reset']]);
}

$id = strtolower(dn_param('id'));
if (!dn_is_uuid($id)) {
    dn_json(400, ['ok' => false, 'error' => 'invalid id – expected UUID v4']);
}

// Require token for write actions
if ($action !== 'get' && $requireToken) {
    dn_require_token($token);
}

// ---------------------------------------------------------------------------
// Database
// ---------------------------------------------------------------------------

if (empty($dbHost) || empty($dbUser) || $dbPass === null || $dbPass === false) {
    dn_json(500, ['ok' => false, 'error' => 'database not configured – set IGRID_DB_HOST, IGRID_DB_USER, IGRID_DB_PASS']);
}

$conn = @new mysqli($dbHost, $dbUser, $dbPass, $dbName);
if ($conn->connect_error) {
    dn_json(500, ['ok' => false, 'error' => 'database connection failed']);
}

$stmt = $conn->prepare(
    "SELECT PrincipalID, FirstName, LastName, COALESCE(DisplayName,''), NameChanged
     FROM {$table}
     WHERE PrincipalID = ?
     LIMIT 1"
);
$stmt->bind_param('s', $id);
$stmt->execute();
$stmt->bind_result($principalId, $firstName, $lastName, $displayName, $nameChanged);
$found = $stmt->fetch();
$stmt->close();

if (!$found) {
    $conn->close();
    dn_json(404, ['ok' => false, 'error' => 'user not found']);
}

$legacy = trim("{$firstName} {$lastName}");
$principalId = (string) $principalId;
$displayName  = (string) $displayName;

// ---------------------------------------------------------------------------
// GET  – retrieve current display name
// ---------------------------------------------------------------------------

if ($action === 'get') {
    $conn->close();
    dn_json(200, [
        'ok'                 => true,
        'id'                 => $principalId,
        'legacy_name'        => $legacy,
        'display_name'       => $displayName,
        'next_change_allowed' => dn_next_allowed_iso((string) $nameChanged),
    ]);
}

// ---------------------------------------------------------------------------
// SET  – update display name (7-day cooldown)
// ---------------------------------------------------------------------------

if ($action === 'set') {
    if (($_SERVER['REQUEST_METHOD'] ?? 'GET') !== 'POST') {
        $conn->close();
        dn_json(405, ['ok' => false, 'error' => 'method not allowed – use POST']);
    }

    $name = dn_param('name');
    if (mb_strlen($name) > 64) {
        $name = mb_substr($name, 0, 64);
    }

    $now = (string) time();
    $up = $conn->prepare("UPDATE {$table} SET DisplayName = ?, NameChanged = ? WHERE PrincipalID = ?");
    $up->bind_param('sss', $name, $now, $id);
    $ok = $up->execute();
    $up->close();
    $conn->close();

    if (!$ok) {
        dn_json(500, ['ok' => false, 'error' => 'update failed']);
    }

    dn_json(200, [
        'ok'                  => true,
        'id'                  => $id,
        'display_name'        => $name,
        'next_change_allowed' => dn_next_allowed_iso($now),
    ]);
}

// ---------------------------------------------------------------------------
// RESET – clear display name
// ---------------------------------------------------------------------------

if (($_SERVER['REQUEST_METHOD'] ?? 'GET') !== 'POST') {
    $conn->close();
    dn_json(405, ['ok' => false, 'error' => 'method not allowed – use POST']);
}

$now = (string) time();
$up = $conn->prepare("UPDATE {$table} SET DisplayName = '', NameChanged = ? WHERE PrincipalID = ?");
$up->bind_param('ss', $now, $id);
$ok = $up->execute();
$up->close();
$conn->close();

if (!$ok) {
    dn_json(500, ['ok' => false, 'error' => 'reset failed']);
}

dn_json(200, [
    'ok'                  => true,
    'id'                  => $id,
    'display_name'        => '',
    'next_change_allowed' => dn_next_allowed_iso($now),
]);
