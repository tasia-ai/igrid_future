<?php
// Example snippet for a WoWonder admin/settings page that integrates the
// OpenSim OAuth flow provided by the TasiaAddon.WoWonder add-on.
// Drop this file next to the wowonder-openim skeleton and `require` it from
// a WoWonder controller or page after WoWonder has bootstrapped its globals.

session_start();

if (!isset($wo) && isset($GLOBALS['wo'])) {
    $wo = $GLOBALS['wo'];
}

if (!is_array($wo ?? null) || empty($wo['loggedin'])) {
    die('WoWonder session not initialised. Include this file from a WoWonder page.');
}

require_once __DIR__ . '/../wowonder-openim/config.php';
require_once __DIR__ . '/../wowonder-openim/OpenSimClient.php';

$config = include __DIR__ . '/../wowonder-openim/config.php';
$client = new OpenSimClient($config);

$tokens = $_SESSION['opensim_tokens'] ?? null;
$profile = $_SESSION['opensim_profile'] ?? null;

$csrfToken = function_exists('Wo_CreateSession')
    ? Wo_CreateSession()
    : ($_SESSION['opensim_csrf'] ?? ($_SESSION['opensim_csrf'] = bin2hex(random_bytes(16))));

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $hash = $_POST['hash'] ?? '';
    $action = $_POST['action'] ?? '';

    $validHash = false;
    if (function_exists('Wo_CheckSession')) {
        $validHash = Wo_CheckSession($hash) === true;
    } elseif (hash_equals($csrfToken, $hash)) {
        $validHash = true;
    }

    if (!$validHash) {
        die('Invalid session token.');
    }

    if ($action === 'refresh' && !empty($tokens['refresh_token'])) {
        $tokens = $client->refreshToken($tokens['refresh_token']);
        if (!empty($tokens['access_token'])) {
            $_SESSION['opensim_tokens'] = $tokens;
        }
    }

    if ($action === 'unlink') {
        unset($_SESSION['opensim_tokens'], $_SESSION['opensim_profile']);
        $tokens = $profile = null;
        if (function_exists('Wo_UpdateUserData')) {
            Wo_UpdateUserData($wo['user']['user_id'], [
                'opensim_avatar' => null,
                'opensim_balance' => null,
            ]);
        }
    }
}

?>
<div class="opensim-settings-card">
    <h2>OpenSim Link</h2>
    <?php if (!$tokens): ?>
        <p>No OpenSim account is linked to this WoWonder profile.</p>
        <?php
        $loginUrl = isset($wo['config']['site_url'])
            ? $wo['config']['site_url'] . '/openim/login'
            : '/openim/login';
        ?>
        <a class="btn btn-primary" href="<?php echo htmlspecialchars($loginUrl, ENT_QUOTES, 'UTF-8'); ?>">
            Link OpenSim Account
        </a>
    <?php else: ?>
        <dl>
            <dt>Avatar</dt>
            <dd><?php echo htmlspecialchars($profile['name'] ?? $profile['agent_id'] ?? 'Unknown', ENT_QUOTES, 'UTF-8'); ?></dd>
            <dt>Access Token Expires</dt>
            <dd><?php echo htmlspecialchars($tokens['expires_in'] ?? 'unknown', ENT_QUOTES, 'UTF-8'); ?> seconds</dd>
        </dl>
        <form method="post" class="opensim-settings-actions">
            <input type="hidden" name="hash" value="<?php echo htmlspecialchars($csrfToken, ENT_QUOTES, 'UTF-8'); ?>">
            <button class="btn btn-secondary" type="submit" name="action" value="refresh">Refresh Token</button>
            <button class="btn btn-danger" type="submit" name="action" value="unlink">Unlink Account</button>
        </form>
        <?php
        $balance = $client->getBalance($tokens['access_token']);
        if (!empty($balance)) {
            echo '<p>Current balance: ' . htmlspecialchars((string)($balance['balance'] ?? '0'), ENT_QUOTES, 'UTF-8') . '</p>';
        }
        ?>
    <?php endif; ?>
</div>

<style>
.opensim-settings-card {
    border: 1px solid #ddd;
    border-radius: 6px;
    padding: 16px;
    background: #fff;
    max-width: 480px;
}
.opensim-settings-actions {
    display: flex;
    gap: 8px;
    margin-top: 12px;
}
.opensim-settings-actions .btn {
    padding: 8px 12px;
}
</style>
