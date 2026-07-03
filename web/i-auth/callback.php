<?php
declare(strict_types=1);
session_start();

require __DIR__ . '/lib.php';

$cfg = iauth_config();
$error = '';
$notice = '';

$normalizeReturnTo = static function (string $raw, string $fallback): string {
    $raw = trim($raw);
    if ($raw === '') {
        return $fallback;
    }
    if ($raw[0] === '/') {
        return $raw;
    }
    if (preg_match('#^https?://#i', $raw)) {
        $parts = parse_url($raw);
        $host = strtolower((string)($parts['host'] ?? ''));
        $currentHost = strtolower((string)($_SERVER['HTTP_HOST'] ?? ''));
        if ($host !== '' && $currentHost !== '' && $host === $currentHost) {
            $path = (string)($parts['path'] ?? '/');
            $query = (string)($parts['query'] ?? '');
            return $query !== '' ? ($path . '?' . $query) : $path;
        }
    }
    return $fallback;
};

$code = trim((string)($_GET['code'] ?? ''));
$state = trim((string)($_GET['state'] ?? ''));
$expectedState = (string)($_SESSION['iauth_oidc_state'] ?? '');

if ($code === '') {
    $error = 'Missing authorization code.';
} elseif ($state === '' || $expectedState === '' || !hash_equals($expectedState, $state)) {
    $error = 'Invalid OIDC state.';
} else {
    $tokenUrl = $cfg['keycloak_base'] . '/realms/' . rawurlencode($cfg['keycloak_realm']) . '/protocol/openid-connect/token';
    $body = http_build_query([
        'grant_type' => 'authorization_code',
        'client_id' => $cfg['keycloak_client_id'],
        'client_secret' => $cfg['keycloak_client_secret'],
        'code' => $code,
        'redirect_uri' => $cfg['keycloak_redirect_uri'],
    ]);
    $resp = iauth_http_post($tokenUrl, $body, 'application/x-www-form-urlencoded', 10);
    if ($resp['error'] !== '' || $resp['status'] < 200 || $resp['status'] >= 300) {
        $error = 'Token exchange failed: HTTP ' . (int)$resp['status'] . ' ' . $resp['error'];
    } else {
        $json = json_decode($resp['body'], true);
        if (!is_array($json) || empty($json['access_token'])) {
            $error = 'Token exchange response is invalid.';
        } else {
            $_SESSION['iauth_tokens'] = [
                'access_token' => (string)$json['access_token'],
                'id_token' => (string)($json['id_token'] ?? ''),
                'refresh_token' => (string)($json['refresh_token'] ?? ''),
                'expires_in' => (int)($json['expires_in'] ?? 0),
                'saved_at' => time(),
            ];
            $notice = 'SSO token session established.';

            $returnTo = $normalizeReturnTo((string)($_SESSION['iauth_return_to'] ?? ''), '/i-auth/index.php');
            header('Location: ' . $returnTo);
            exit;
        }
    }
}

$tokens = $_SESSION['iauth_tokens'] ?? null;
?>
<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>I-Grid SSO Callback</title>
    <style>
        :root { --bg:#030712; --line:#1be7ff44; --text:#d8f3ff; --muted:#7bbad1; --neon:#1be7ff; --ok:#8bffe2; --bad:#ff9fb0; }
        * { box-sizing: border-box; }
        body {
            margin: 0;
            font-family: Consolas, Monaco, monospace;
            background: radial-gradient(1200px 600px at 10% -10%,#083b63 0%,transparent 60%), radial-gradient(900px 500px at 90% -20%,#0b3f33 0%,transparent 55%), var(--bg);
            color: var(--text);
            opacity: 0;
            transform: translateY(8px) scale(.995);
            transition: opacity .28s ease, transform .28s ease;
        }
        body.page-ready { opacity: 1; transform: translateY(0) scale(1); }
        body.page-leaving { opacity: 0; transform: translateY(-6px) scale(.995); }
        .wrap { max-width: 860px; margin: 30px auto; padding: 0 16px; }
        .card {
            background: linear-gradient(180deg,#071428,#051024);
            border: 1px solid var(--line);
            border-radius: 14px;
            padding: 16px;
            box-shadow: 0 0 30px #00dfff2e;
            animation: cardIn .35s cubic-bezier(.2,.8,.2,1);
        }
        @keyframes cardIn { from { opacity: 0; transform: translateY(10px) scale(.99);} to {opacity:1; transform:translateY(0) scale(1);} }
        h2 { color: var(--neon); letter-spacing: .06em; }
        .ok { color: var(--ok); }
        .bad { color: var(--bad); }
        .mono { font-family: ui-monospace, Menlo, Consolas, monospace; word-break: break-all; color: var(--muted); }
        a { color: var(--neon); }
    </style>
</head>
<body>
<div class="wrap">
    <div class="card">
        <h2>Keycloak Callback</h2>
        <?php if ($error !== ''): ?>
            <p class="bad"><?php echo iauth_h($error); ?></p>
        <?php else: ?>
            <p class="ok"><?php echo iauth_h($notice); ?></p>
        <?php endif; ?>

        <?php if (is_array($tokens)): ?>
            <p><strong>Access token saved in session.</strong></p>
            <p class="mono">expires_in: <?php echo iauth_h((string)$tokens['expires_in']); ?> seconds</p>
        <?php endif; ?>

        <p><a href="index.php">Back to i-auth</a></p>
    </div>
</div>
<script>
document.addEventListener('DOMContentLoaded', function () {
    document.body.classList.add('page-ready');
});

document.addEventListener('click', function (ev) {
    var a = ev.target.closest('a');
    if (!a || !a.href) return;
    ev.preventDefault();
    document.body.classList.remove('page-ready');
    document.body.classList.add('page-leaving');
    setTimeout(function () { window.location.href = a.href; }, 220);
});
</script>
</body>
</html>
