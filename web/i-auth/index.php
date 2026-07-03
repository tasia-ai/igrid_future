<?php
declare(strict_types=1);
session_start();

require __DIR__ . '/lib.php';

$cfg = iauth_config();
$notice = '';
$error = '';

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

$target = strtolower(trim((string)($_GET['target'] ?? 'oidc')));
if ($target !== 'panel' && $target !== 'oidc') {
    $target = 'oidc';
}

$returnTo = trim((string)($_GET['return_to'] ?? ''));
if ($returnTo === '') {
    $returnTo = ($target === 'panel') ? '/hg/admin_panel.php' : '/i-auth/index.php';
}
$returnTo = $normalizeReturnTo($returnTo, ($target === 'panel') ? '/hg/admin_panel.php' : '/i-auth/index.php');
$_SESSION['iauth_return_to'] = $returnTo;
$_SESSION['iauth_target'] = $target;
if ($target === 'panel' || $target === 'oidc') {
    $_SESSION['iauth_ready_redirect'] = $returnTo;
}

$captchaEnsure = static function (): void {
    $cur = $_SESSION['iauth_captcha'] ?? null;
    $isFresh = is_array($cur) && isset($cur['answer'], $cur['a'], $cur['b'], $cur['ts']) && ((int)$cur['ts'] + 900) >= time();
    if ($isFresh) {
        return;
    }

    $a = random_int(1, 9);
    $b = random_int(1, 9);
    $_SESSION['iauth_captcha'] = [
        'a' => $a,
        'b' => $b,
        'answer' => (string)($a + $b),
        'ts' => time(),
    ];
};

$captchaReset = static function () use ($captchaEnsure): void {
    unset($_SESSION['iauth_captcha']);
    $captchaEnsure();
};

$captchaEnsure();

$oidcAppUri = '';
$oidcScopeText = '';
if ($target === 'oidc') {
    $rt = (string)($_SESSION['iauth_return_to'] ?? '');
    $q = parse_url($rt, PHP_URL_QUERY);
    if (is_string($q) && $q !== '') {
        parse_str($q, $qp);
        $oidcAppUri = trim((string)($qp['redirect_uri'] ?? ''));
        $oidcScopeText = trim((string)($qp['scope'] ?? 'openid profile email'));
    }
}

if (isset($_POST['action']) && $_POST['action'] === 'logout') {
    unset($_SESSION['iauth_pending']);
    unset($_SESSION['iauth_user']);
    unset($_SESSION['iauth_tokens']);
    unset($_SESSION['iauth_oidc_state']);
    header('Location: index.php');
    exit;
}

if (isset($_POST['action']) && $_POST['action'] === 'request_otp') {
    $avatarInput = trim((string)($_POST['avatar_identity'] ?? $_POST['avatar_uuid'] ?? ''));
    $captchaInput = trim((string)($_POST['captcha_answer'] ?? ''));
    $captchaExpected = trim((string)(($_SESSION['iauth_captcha']['answer'] ?? '')));
    if ($captchaExpected === '' || !hash_equals($captchaExpected, $captchaInput)) {
        $error = 'Captcha failed. Please solve the math challenge.';
        $captchaReset();
    } elseif ($avatarInput === '') {
        $error = 'Avatar UUID or avatar name is required.';
    } else {
        try {
            $robust = iauth_db_connect($cfg['robust_db'], $cfg);
            $lookup = iauth_avatar_lookup($robust, $avatarInput);
            $profile = is_array($lookup['profile'] ?? null) ? $lookup['profile'] : null;
            $robust->close();
            if (!$profile) {
                $matches = is_array($lookup['matches'] ?? null) ? $lookup['matches'] : [];
                if (!empty($matches)) {
                    $sample = [];
                    foreach (array_slice($matches, 0, 3) as $m) {
                        $sample[] = trim((string)($m['full_name'] ?? ''));
                    }
                    $error = 'Multiple avatars matched that name (' . implode(', ', array_filter($sample)) . '). Please use full name or UUID.';
                } else {
                    $error = 'Avatar not found in UserAccounts. Use UUID or exact avatar name.';
                }
                $captchaReset();
            } else {
                $uuid = strtolower((string)$profile['uuid']);
                $otp = str_pad((string)random_int(0, 999999), 6, '0', STR_PAD_LEFT);
                $_SESSION['iauth_pending'] = [
                    'uuid' => $uuid,
                    'hash' => password_hash($otp, PASSWORD_DEFAULT),
                    'expires' => time() + max(60, $cfg['otp_ttl_sec']),
                    'profile' => $profile,
                ];

                $msg = 'I-Grid OTP: ' . $otp . ' (valid ' . (int)round($cfg['otp_ttl_sec'] / 60) . ' min).';
                $im = iauth_send_im_otp($cfg, $uuid, $msg);
                if ($im['error'] !== '' || $im['status'] < 200 || $im['status'] >= 300) {
                    $error = 'OTP IM send failed (HTTP ' . (int)$im['status'] . '): ' . $im['error'];
                    $captchaReset();
                } else {
                    $notice = 'OTP sent by IM to ' . $profile['full_name'] . ' (' . $uuid . '). Enter it below.';
                    $captchaReset();
                }
            }
        } catch (Throwable $e) {
            $error = $e->getMessage();
            $captchaReset();
        }
    }
}

if (isset($_POST['action']) && $_POST['action'] === 'verify_otp') {
    $otp = trim((string)($_POST['otp'] ?? ''));
    $pending = $_SESSION['iauth_pending'] ?? null;

    if (!is_array($pending) || empty($pending['uuid']) || empty($pending['hash'])) {
        $error = 'No pending OTP. Request a new OTP first.';
    } elseif ((int)($pending['expires'] ?? 0) < time()) {
        $error = 'OTP expired. Request a new one.';
        unset($_SESSION['iauth_pending']);
    } elseif (!preg_match('/^[0-9]{6}$/', $otp) || !password_verify($otp, (string)$pending['hash'])) {
        $error = 'Invalid OTP code.';
    } else {
        try {
            $profile = is_array($pending['profile'] ?? null) ? $pending['profile'] : null;
            if (!$profile) {
                $robust = iauth_db_connect($cfg['robust_db'], $cfg);
                $profile = iauth_avatar_profile($robust, (string)$pending['uuid']);
                $robust->close();
            }
            if (!$profile) {
                throw new RuntimeException('Avatar profile is no longer available.');
            }

            $_SESSION['iauth_user'] = [
                'uuid' => (string)$profile['uuid'],
                'name' => (string)$profile['full_name'],
                'username' => '',
                'email' => '',
                'keycloak_user_id' => '',
                'verified_at' => time(),
                'target' => $target,
            ];
            unset($_SESSION['iauth_pending']);
            $ret = $normalizeReturnTo(
                (string)($_SESSION['iauth_return_to'] ?? ''),
                ($target === 'panel') ? '/hg/admin_panel.php' : '/i-auth/index.php'
            );
            $_SESSION['iauth_ready_redirect'] = $ret;
            $notice = 'OTP verified. Click continue to go back.';
        } catch (Throwable $e) {
            $error = $e->getMessage();
        }
    }
}

$user = $_SESSION['iauth_user'] ?? null;
$readyRedirect = (string)($_SESSION['iauth_ready_redirect'] ?? '');
$continueHref = $readyRedirect;
$captchaRow = is_array($_SESSION['iauth_captcha'] ?? null) ? $_SESSION['iauth_captcha'] : ['a' => '?', 'b' => '?'];

if (is_array($user) && ($target === 'panel' || $target === 'oidc')) {
    $fallback = ($target === 'panel') ? '/hg/admin_panel.php' : '/i-auth/index.php';
    $readyRedirect = $normalizeReturnTo((string)($_SESSION['iauth_return_to'] ?? $returnTo), $fallback);
    if ($readyRedirect !== '') {
        $_SESSION['iauth_ready_redirect'] = $readyRedirect;
        if ($notice === '' && $error === '') {
            $notice = 'You are already authenticated. Click continue.';
        }
    }
}

if ($continueHref !== '' && $target === 'oidc') {
    $sep = (strpos($continueHref, '?') !== false) ? '&' : '?';
    if (strpos($continueHref, 'iauth_continue=') === false) {
        $continueHref .= $sep . 'iauth_continue=1';
    }
}
?>
<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>I-Grid SSO OTP</title>
    <style>
        :root {
            --bg:#030712; --panel:#071428; --line:#1be7ff44;
            --text:#d8f3ff; --muted:#7bbad1; --neon:#1be7ff; --neon2:#13ffc4; --danger:#ff5f7b;
        }
        *{box-sizing:border-box}
        body{
            margin:0;
            font-family:Consolas,Monaco,monospace;
            background:radial-gradient(1200px 600px at 10% -10%,#083b63 0%,transparent 60%),radial-gradient(900px 500px at 90% -20%,#0b3f33 0%,transparent 55%),var(--bg);
            color:var(--text);
            min-height:100vh;
            opacity:0;
            transform:translateY(8px) scale(.995);
            transition:opacity .28s ease, transform .28s ease;
        }
        body.page-ready{opacity:1;transform:translateY(0) scale(1)}
        body.page-leaving{opacity:0;transform:translateY(-6px) scale(.995)}
        .wrap{max-width:900px;margin:32px auto;padding:0 16px}
        .card{background:linear-gradient(180deg,#071428,#051024);border:1px solid var(--line);border-radius:16px;padding:18px;box-shadow:0 0 30px #00dfff2e;margin-bottom:14px;animation:cardIn .35s cubic-bezier(.2,.8,.2,1)}
        @keyframes cardIn{from{opacity:0;transform:translateY(10px) scale(.99)}to{opacity:1;transform:translateY(0) scale(1)}}
        h1{margin:0 0 6px;font-size:28px;color:var(--neon);letter-spacing:.08em}
        .sub{color:var(--muted);margin-bottom:16px}
        .row{display:flex;gap:8px;flex-wrap:wrap;align-items:center}
        input[type=text]{width:100%;padding:11px 12px;background:#07162b;border:1px solid #178ca23d;color:var(--text);border-radius:10px}
        .btn{border:1px solid #1be7ff66;border-radius:10px;padding:11px 14px;font-weight:700;cursor:pointer}
        .btn-main{background:#082238;color:var(--text)}
        .btn-alt{background:#0d1f35;color:var(--text)}
        .btn-link{background:#0b2a49;color:var(--text);text-decoration:none;display:inline-block;border-color:#1be7ff88}
        .btn:hover{box-shadow:0 0 12px #1be7ff55}
        .note{padding:10px 12px;border-radius:10px;margin-bottom:12px}
        .ok{background:#062228;border:1px solid #13ffc455;color:#8bffe2}
        .bad{background:#2a0d16;border:1px solid #ff5f7b66;color:#ff9fb0}
        .mono{font-family:Consolas,Monaco,monospace}
        .hero{font-size:.95rem;color:var(--muted);margin-bottom:12px}
        .perm-box{margin-bottom:12px;padding:10px 12px;border:1px solid #1be7ff66;border-radius:10px;background:#061425}
        .perm-title{font-weight:700;color:var(--neon);margin-bottom:4px}
        .perm-sub{color:var(--muted);font-size:.92rem;margin:0}
    </style>
</head>
<body>
<div class="wrap">
    <div class="card">
        <h1>I-Grid Authentication</h1>
        <div class="hero">Authenticate avatar by IM OTP to continue securely.</div>
        <?php if ($target === 'oidc' && $oidcAppUri !== ''): ?>
            <div class="perm-box">
                <div class="perm-title">APP: <span class="mono"><?php echo iauth_h($oidcAppUri); ?></span></div>
                <p class="perm-sub">asks for your permission to: <span class="mono"><?php echo iauth_h($oidcScopeText !== '' ? $oidcScopeText : 'openid profile email'); ?></span></p>
            </div>
        <?php endif; ?>
        <div class="sub">Profile mapping: <span class="mono">email=uuid@i-grid.users</span>, <span class="mono">username=first_last_###</span>, avatar can be found by UUID or name from UserAccounts.</div>

        <?php if ($notice !== ''): ?><div class="note ok"><?php echo iauth_h($notice); ?></div><?php endif; ?>
        <?php if ($error !== ''): ?><div class="note bad"><?php echo iauth_h($error); ?></div><?php endif; ?>

        <form method="post" class="row" style="margin-bottom:10px;">
            <input type="hidden" name="action" value="request_otp">
            <input type="text" name="avatar_identity" placeholder="Avatar UUID or full name" required>
            <input type="text" name="captcha_answer" placeholder="Captcha: <?php echo iauth_h((string)$captchaRow['a']); ?> + <?php echo iauth_h((string)$captchaRow['b']); ?> = ?" inputmode="numeric" pattern="[0-9]+" required>
            <button class="btn btn-main" type="submit">Send IM OTP</button>
        </form>

        <form method="post" class="row">
            <input type="hidden" name="action" value="verify_otp">
            <input type="text" name="otp" placeholder="6-digit OTP" maxlength="6" required>
            <button class="btn btn-alt" type="submit">Verify OTP</button>
        </form>
    </div>

    <?php if (is_array($user)): ?>
    <div class="card">
        <div><strong>Ready:</strong> <?php echo iauth_h((string)$user['name']); ?></div>
        <div class="mono">uuid: <?php echo iauth_h((string)$user['uuid']); ?></div>
        <?php if ((string)($user['username'] ?? '') !== ''): ?><div class="mono">username: <?php echo iauth_h((string)$user['username']); ?></div><?php endif; ?>
        <?php if ((string)($user['email'] ?? '') !== ''): ?><div class="mono">email: <?php echo iauth_h((string)$user['email']); ?></div><?php endif; ?>
        <div style="margin-top:12px" class="row">
            <?php if ($readyRedirect !== '' && ($target === 'panel' || $target === 'oidc')): ?>
                <a class="btn btn-link" href="<?php echo iauth_h($continueHref); ?>">Continue</a>
            <?php endif; ?>
            <form method="post">
                <input type="hidden" name="action" value="logout">
                <button class="btn btn-alt" type="submit">It's not me</button>
            </form>
        </div>
    </div>
    <?php endif; ?>
</div>
<script>
document.addEventListener('DOMContentLoaded', function () {
    document.body.classList.add('page-ready');
});

document.addEventListener('click', function (ev) {
    var a = ev.target.closest('a');
    if (!a || !a.href) return;
    if (a.target === '_blank' || a.hasAttribute('download')) return;
    ev.preventDefault();
    document.body.classList.remove('page-ready');
    document.body.classList.add('page-leaving');
    setTimeout(function () { window.location.href = a.href; }, 220);
});

document.addEventListener('submit', function () {
    document.body.classList.remove('page-ready');
    document.body.classList.add('page-leaving');
});
</script>
</body>
</html>
