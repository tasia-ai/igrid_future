<?php
declare(strict_types=1);

session_start();

$host = getenv('IGRID_DB_HOST');
$user = getenv('IGRID_DB_USER');
$pass = getenv('IGRID_DB_PASS');
$robustDb = getenv('IGRID_DB_ROBUST') ?: 'robust';

function h(string $v): string
{
    return htmlspecialchars($v, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
}

function valid_uuid(string $uuid): bool
{
    return (bool)preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i', $uuid);
}

function postv(string $key, string $default = ''): string
{
    return isset($_POST[$key]) ? trim((string)$_POST[$key]) : $default;
}

function log_action(string $line): void
{
    @file_put_contents(__DIR__ . '/displayname_admin.log', date('[Y-m-d H:i:s] ') . $line . PHP_EOL, FILE_APPEND);
}

function db_log(mysqli $conn, string $actor, string $action, string $targetUuid, string $payload): void
{
    $st = $conn->prepare('INSERT INTO displayname_admin_audit (actor, action, target_uuid, payload) VALUES (?, ?, ?, ?)');
    if (!$st) {
        return;
    }
    $st->bind_param('ssss', $actor, $action, $targetUuid, $payload);
    $st->execute();
    $st->close();
}

function clear_grid_user_cache(): string
{
    $script = <<<'BASH'
for c in $(docker ps --filter "label=com.docker.compose.project=opensim" --format "{{.Names}}"); do
  if docker exec "$c" tmux has-session -t opensim 2>/dev/null; then
    docker exec "$c" tmux send-keys -t opensim "reset user cache" C-m
    echo "cache-reset:$c:opensim"
  elif docker exec "$c" tmux has-session -t opensim_session 2>/dev/null; then
    docker exec "$c" tmux send-keys -t opensim_session "reset user cache" C-m
    echo "cache-reset:$c:opensim_session"
  else
    echo "skip:$c"
  fi
done
BASH;

    $cmd = 'bash -lc ' . escapeshellarg($script) . ' 2>&1';
    $out = shell_exec($cmd);
    return is_string($out) ? trim($out) : 'no output';
}

$isLogged = !empty($_SESSION['admin_logged_in']);
$role = (string)($_SESSION['admin_role'] ?? 'add_only');
if (!$isLogged || $role !== 'admin') {
    http_response_code(403);
    echo '<h1>403</h1><p>Admin session required.</p>';
    exit;
}

if (empty($host) || empty($user) || $pass === null || $pass === false) {
    http_response_code(500);
    echo '<h1>Config error</h1><p>Database not configured. Set IGRID_DB_HOST, IGRID_DB_USER, IGRID_DB_PASS.</p>';
    exit;
}

$conn = new mysqli($host, $user, $pass, $robustDb);
if ($conn->connect_error) {
    http_response_code(500);
    echo '<h1>DB error</h1><p>Cannot connect to robust database.</p>';
    exit;
}

$conn->query("CREATE TABLE IF NOT EXISTS displayname_admin_audit (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    actor VARCHAR(128) NOT NULL,
    action VARCHAR(32) NOT NULL,
    target_uuid VARCHAR(36) NOT NULL,
    payload TEXT,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_target_uuid (target_uuid),
    INDEX idx_created_at (created_at)
)");

$actor = trim((string)($_SESSION['admin_username'] ?? $_SESSION['admin_user'] ?? $_SESSION['current_user'] ?? 'admin'));
if ($actor === '') {
    $actor = 'admin';
}

$message = '';
$details = '';

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $action = strtolower(postv('action'));
    $uuid = strtolower(postv('uuid'));

    if (!valid_uuid($uuid)) {
        $message = 'Invalid UUID.';
    } else {
        if ($action === 'set') {
            $name = postv('display_name');
            if (mb_strlen($name) > 64) {
                $name = mb_substr($name, 0, 64);
            }
            $unlock = isset($_POST['unlock_cooldown']) ? 1 : 0;
            $nameChanged = $unlock ? (time() - 8 * 24 * 60 * 60) : time();

            $st = $conn->prepare('UPDATE UserAccounts SET DisplayName = ?, NameChanged = ? WHERE PrincipalID = ?');
            $nameChangedStr = (string)$nameChanged;
            $st->bind_param('sss', $name, $nameChangedStr, $uuid);
            $ok = $st->execute();
            $st->close();

            if ($ok) {
                $message = 'Display name updated.';
                log_action("set uuid={$uuid} display_name=" . json_encode($name));
                db_log($conn, $actor, 'set', $uuid, json_encode(['display_name' => $name, 'unlock' => $unlock], JSON_UNESCAPED_SLASHES));
            } else {
                $message = 'Failed to update display name.';
            }
        } elseif ($action === 'reset') {
            $nameChangedStr = (string)time();
            $st = $conn->prepare('UPDATE UserAccounts SET DisplayName = \'\', NameChanged = ? WHERE PrincipalID = ?');
            $st->bind_param('ss', $nameChangedStr, $uuid);
            $ok = $st->execute();
            $st->close();

            $message = $ok ? 'Display name reset to legacy.' : 'Failed to reset display name.';
            if ($ok) {
                log_action("reset uuid={$uuid}");
                db_log($conn, $actor, 'reset', $uuid, '{}');
            }
        } elseif ($action === 'unlock') {
            $nameChangedStr = (string)(time() - 8 * 24 * 60 * 60);
            $st = $conn->prepare('UPDATE UserAccounts SET NameChanged = ? WHERE PrincipalID = ?');
            $st->bind_param('ss', $nameChangedStr, $uuid);
            $ok = $st->execute();
            $st->close();

            $message = $ok ? 'Cooldown unlocked (set to 8 days ago).' : 'Failed to unlock cooldown.';
            if ($ok) {
                log_action("unlock uuid={$uuid}");
                db_log($conn, $actor, 'unlock', $uuid, '{}');
            }
        } elseif ($action === 'force_refresh') {
            $sel = $conn->prepare('SELECT FirstName, LastName, COALESCE(DisplayName,\'\') FROM UserAccounts WHERE PrincipalID = ? LIMIT 1');
            $sel->bind_param('s', $uuid);
            $sel->execute();
            $sel->bind_result($firstName, $lastName, $displayName);
            $found = $sel->fetch();
            $sel->close();

            if ($found) {
                $base = trim((string)$displayName);
                if ($base === '') {
                    $base = trim(((string)$firstName) . ' ' . ((string)$lastName));
                }
                if ($base === '') {
                    $base = 'Resident';
                }

                $base = preg_replace('/[\x{200B}\x{2060}]+$/u', '', $base) ?? $base;
                if (mb_strlen($base) > 63) {
                    $base = mb_substr($base, 0, 63);
                }

                $marker = (time() % 2 === 0) ? "\u{2060}" : "\u{200B}";
                $temp = $base . $marker;
                $now = (string)time();

                $u1 = $conn->prepare('UPDATE UserAccounts SET DisplayName = ?, NameChanged = ? WHERE PrincipalID = ?');
                $u1->bind_param('sss', $temp, $now, $uuid);
                $ok1 = $u1->execute();
                $u1->close();

                usleep(180000);

                $u2 = $conn->prepare('UPDATE UserAccounts SET DisplayName = ?, NameChanged = ? WHERE PrincipalID = ?');
                $u2->bind_param('sss', $base, $now, $uuid);
                $ok2 = $u2->execute();
                $u2->close();

                if ($ok1 && $ok2) {
                    $message = 'Best-effort force refresh applied (ZWSP nonce flip).';
                    log_action("force_refresh uuid={$uuid} base=" . json_encode($base));
                    db_log($conn, $actor, 'force_refresh', $uuid, json_encode(['base' => $base], JSON_UNESCAPED_SLASHES));
                } else {
                    $message = 'Failed to force refresh.';
                }
            } else {
                $message = 'User not found.';
            }
        }

        if (isset($_POST['clear_sim_cache'])) {
            $details = clear_grid_user_cache();
            log_action("cache_clear_triggered uuid={$uuid}");
            db_log($conn, $actor, 'clear_cache', $uuid, json_encode(['details' => $details], JSON_UNESCAPED_SLASHES));
        }
    }
}

$q = trim((string)($_GET['q'] ?? ''));
$rows = [];
if ($q !== '') {
    if (valid_uuid($q)) {
        $st = $conn->prepare('SELECT PrincipalID, FirstName, LastName, COALESCE(DisplayName,\'\') AS DisplayName, NameChanged FROM UserAccounts WHERE PrincipalID = ? LIMIT 50');
        $st->bind_param('s', $q);
    } else {
        $like = '%' . $q . '%';
        $st = $conn->prepare('SELECT PrincipalID, FirstName, LastName, COALESCE(DisplayName,\'\') AS DisplayName, NameChanged FROM UserAccounts WHERE CONCAT(FirstName,\' \' ,LastName) LIKE ? OR DisplayName LIKE ? ORDER BY FirstName, LastName LIMIT 50');
        $st->bind_param('ss', $like, $like);
    }
    $st->execute();
    $res = $st->get_result();
    while ($r = $res->fetch_assoc()) {
        $rows[] = $r;
    }
    $st->close();
}

$conn->close();
?>
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Display Name Admin</title>
  <style>
    body{font-family:Segoe UI,Arial,sans-serif;background:#0b1020;color:#e5e7eb;margin:0;padding:24px}
    .card{max-width:1100px;margin:0 auto;background:#111827;border:1px solid #374151;border-radius:12px;padding:18px}
    input,button{padding:8px 10px;border-radius:8px;border:1px solid #4b5563;background:#1f2937;color:#e5e7eb}
    button{cursor:pointer}
    table{width:100%;border-collapse:collapse;margin-top:12px}
    th,td{border-bottom:1px solid #374151;padding:8px;text-align:left;font-size:13px}
    .ok{color:#34d399}.warn{color:#fbbf24}.muted{color:#9ca3af}
    .rowform{display:flex;gap:6px;flex-wrap:wrap;align-items:center}
  </style>
</head>
<body>
  <div class="card">
    <h2>Display Name Admin</h2>
    <p class="muted">Manage display names in robust DB. "Force refresh" is best-effort (viewer cache cannot be guaranteed 100% server-side).</p>

    <form method="get">
      <input type="text" name="q" placeholder="UUID or name/display search" value="<?php echo h($q); ?>" style="width:360px;max-width:100%">
      <button type="submit">Search</button>
    </form>

    <?php if ($message !== ''): ?><p class="ok"><?php echo h($message); ?></p><?php endif; ?>
    <?php if ($details !== ''): ?><pre><?php echo h($details); ?></pre><?php endif; ?>

    <table>
      <thead>
        <tr>
          <th>UUID</th><th>Legacy Name</th><th>DisplayName</th><th>NameChanged</th><th>Actions</th>
        </tr>
      </thead>
      <tbody>
      <?php foreach ($rows as $r): ?>
        <tr>
          <td><code><?php echo h((string)$r['PrincipalID']); ?></code></td>
          <td><?php echo h((string)$r['FirstName'] . ' ' . (string)$r['LastName']); ?></td>
          <td><?php echo h((string)$r['DisplayName']); ?></td>
          <td><?php echo h((string)$r['NameChanged']); ?></td>
          <td>
            <form method="post" class="rowform">
              <input type="hidden" name="uuid" value="<?php echo h((string)$r['PrincipalID']); ?>">
              <input type="text" name="display_name" placeholder="new display name" style="width:170px">
              <label><input type="checkbox" name="unlock_cooldown" value="1"> unlock</label>
              <label><input type="checkbox" name="clear_sim_cache" value="1" checked> clear cache</label>
              <button type="submit" name="action" value="set">Set</button>
              <button type="submit" name="action" value="reset">Reset</button>
              <button type="submit" name="action" value="unlock">Unlock</button>
              <button type="submit" name="action" value="force_refresh">Force refresh</button>
            </form>
          </td>
        </tr>
      <?php endforeach; ?>
      <?php if (empty($rows)): ?>
        <tr><td colspan="5" class="muted">No rows (search above).</td></tr>
      <?php endif; ?>
      </tbody>
    </table>
  </div>
</body>
</html>
