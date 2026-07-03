<?php
// Handles the OAuth redirect, exchanges the authorization code, and stores
// the resulting tokens inside the WoWonder session.

session_start();

require_once __DIR__ . '/config.php';
require_once __DIR__ . '/OpenSimClient.php';

$wo = $wo ?? ($GLOBALS['wo'] ?? []);

$config = include __DIR__ . '/config.php';
$client = new OpenSimClient($config);

if (!isset($_GET['state'], $_SESSION['opensim_oauth_state']) || $_GET['state'] !== $_SESSION['opensim_oauth_state']) {
    die('Invalid OAuth state.');
}

if (!isset($_GET['code'], $_SESSION['opensim_code_verifier'])) {
    die('Missing authorization code.');
}

$tokens = $client->exchangeCode($_GET['code'], $_SESSION['opensim_code_verifier']);

if (empty($tokens['access_token'])) {
    die('Token exchange failed.');
}

$_SESSION['opensim_tokens'] = $tokens;

$userInfo = $client->getUserInfo($tokens['access_token']);
$_SESSION['opensim_profile'] = $userInfo;

// Persist optional profile metadata inside WoWonder's user table.
if (function_exists('Wo_UpdateUserData') && isset($wo['user']['user_id'])) {
    Wo_UpdateUserData($wo['user']['user_id'], [
        'opensim_avatar' => $userInfo['agent_id'] ?? null,
        'opensim_balance' => $client->getBalance($tokens['access_token'])['balance'] ?? null,
    ]);
}
if (isset($wo['config']['site_url'])) {
    header('Location: ' . $wo['config']['site_url'] . '/openim/settings');
} else {
    echo 'Tokens stored. Configure WoWonder to redirect users to your settings page.';
}
exit;
