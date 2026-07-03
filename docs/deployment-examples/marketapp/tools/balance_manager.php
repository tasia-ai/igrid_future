<?php
declare(strict_types=1);

/**
 * CLI balance manager for money.balances
 *
 * Examples:
 * php balance_manager.php --action show --uuid "43827618-1993-43d8-bf6b-fc966a943381"
 * php balance_manager.php --action add --uuid "43827618-1993-43d8-bf6b-fc966a943381" --amount 256
 * php balance_manager.php --action set --uuid "43827618-1993-43d8-bf6b-fc966a943381" --amount 1000
 */

if (PHP_SAPI !== 'cli') {
    fwrite(STDERR, "Run from CLI only.\n");
    exit(1);
}

$opts = getopt('', [
    'action:',
    'uuid:',
    'amount::',
    'host::',
    'port::',
    'user::',
    'pass::',
    'db::',
]);

$action = strtolower(trim((string)($opts['action'] ?? 'show')));
$uuid = strtolower(trim((string)($opts['uuid'] ?? '')));
$amountRaw = (string)($opts['amount'] ?? '0');

if (!valid_uuid($uuid)) {
    fail('Invalid --uuid value');
}
if (!in_array($action, ['show', 'add', 'set'], true)) {
    fail('Invalid --action. Use show|add|set');
}

$host = (string)($opts['host'] ?? 'i.let-us.cyou');
$port = (int)($opts['port'] ?? 3306);
$user = (string)($opts['user'] ?? 'root');
$pass = (string)($opts['pass'] ?? 'CHANGE_ME_DB_PASSWORD');
$dbName = (string)($opts['db'] ?? 'money');

$db = @new mysqli($host, $user, $pass, $dbName, $port);
if ($db->connect_error) {
    fail('DB connect failed: ' . $db->connect_error);
}
$db->set_charset('utf8mb4');

switch ($action) {
    case 'show':
        $balance = get_balance($db, $uuid);
        if ($balance === null) {
            echo "UUID: {$uuid}\n";
            echo "Balance: (not found)\n";
            exit(0);
        }
        echo "UUID: {$uuid}\n";
        echo "Balance: {$balance}\n";
        exit(0);

    case 'add':
        if (!is_numeric($amountRaw)) {
            fail('For add/set, --amount must be numeric');
        }
        $delta = (float)$amountRaw;
        if ($delta == 0.0) {
            fail('For add, --amount must be non-zero');
        }
        upsert_add($db, $uuid, $delta);
        echo "Added {$delta} to {$uuid}\n";
        echo "New balance: " . (get_balance($db, $uuid) ?? 0.0) . "\n";
        exit(0);

    case 'set':
        if (!is_numeric($amountRaw)) {
            fail('For add/set, --amount must be numeric');
        }
        $target = (float)$amountRaw;
        set_balance($db, $uuid, $target);
        echo "Set balance for {$uuid} to {$target}\n";
        echo "New balance: " . (get_balance($db, $uuid) ?? 0.0) . "\n";
        exit(0);
}

fail('Unhandled action');

function get_balance(mysqli $db, string $uuid): ?float
{
    $stmt = $db->prepare('SELECT balance FROM balances WHERE user = ? LIMIT 1');
    if (!$stmt) {
        fail('Prepare failed (show): ' . $db->error);
    }
    $stmt->bind_param('s', $uuid);
    $stmt->execute();
    $row = $stmt->get_result()->fetch_assoc();
    $stmt->close();
    return $row ? (float)$row['balance'] : null;
}

function upsert_add(mysqli $db, string $uuid, float $delta): void
{
    $sql = "
        INSERT INTO balances (`user`, `balance`, `status`, `type`)
        VALUES (?, ?, 0, 0)
        ON DUPLICATE KEY UPDATE
            balance = balance + VALUES(balance)";
    $stmt = $db->prepare($sql);
    if (!$stmt) {
        fail('Prepare failed (add): ' . $db->error);
    }
    $stmt->bind_param('sd', $uuid, $delta);
    if (!$stmt->execute()) {
        $stmt->close();
        fail('Execute failed (add): ' . $db->error);
    }
    $stmt->close();
}

function set_balance(mysqli $db, string $uuid, float $target): void
{
    $existsStmt = $db->prepare('SELECT 1 FROM balances WHERE user = ? LIMIT 1');
    if (!$existsStmt) {
        fail('Prepare failed (set/check): ' . $db->error);
    }
    $existsStmt->bind_param('s', $uuid);
    $existsStmt->execute();
    $exists = (bool)$existsStmt->get_result()->fetch_row();
    $existsStmt->close();

    if ($exists) {
        $stmt = $db->prepare('UPDATE balances SET balance = ? WHERE user = ?');
        if (!$stmt) {
            fail('Prepare failed (set/update): ' . $db->error);
        }
        $stmt->bind_param('ds', $target, $uuid);
        if (!$stmt->execute()) {
            $stmt->close();
            fail('Execute failed (set/update): ' . $db->error);
        }
        $stmt->close();
        return;
    }

    $stmt = $db->prepare('INSERT INTO balances (`user`, `balance`, `status`, `type`) VALUES (?, ?, 0, 0)');
    if (!$stmt) {
        fail('Prepare failed (set/insert): ' . $db->error);
    }
    $stmt->bind_param('sd', $uuid, $target);
    if (!$stmt->execute()) {
        $stmt->close();
        fail('Execute failed (set/insert): ' . $db->error);
    }
    $stmt->close();
}

function valid_uuid(string $value): bool
{
    return (bool)preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i', $value);
}

function fail(string $message): void
{
    fwrite(STDERR, $message . "\n");
    exit(1);
}
