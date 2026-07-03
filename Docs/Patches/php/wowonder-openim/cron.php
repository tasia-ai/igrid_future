<?php
// Nightly task that refreshes tokens and updates cached OpenSim balances.
// Schedule via cron and include WoWonder bootstrap before running.

require_once __DIR__ . '/config.php';
require_once __DIR__ . '/OpenSimClient.php';

$config = include __DIR__ . '/config.php';
$client = new OpenSimClient($config);

if (!function_exists('Wo_GetAllUsers')) {
    die('WoWonder helpers not loaded.');
}

$users = Wo_GetAllUsers();

foreach ($users as $user) {
    if (empty($user['opensim_refresh_token'])) {
        continue;
    }

    $tokens = $client->refreshToken($user['opensim_refresh_token']);
    if (empty($tokens['access_token'])) {
        error_log('Failed to refresh token for user ' . $user['user_id']);
        continue;
    }

    $balance = $client->getBalance($tokens['access_token']);
    Wo_UpdateUserData($user['user_id'], [
        'opensim_access_token' => $tokens['access_token'],
        'opensim_refresh_token' => $tokens['refresh_token'] ?? $user['opensim_refresh_token'],
        'opensim_balance' => $balance['balance'] ?? $user['opensim_balance'],
    ]);

    // Custom storefront integrations can now apply debits via the viewer or
    // existing MoneyServer tools. Hook additional business logic here if
    // external purchases need to trigger in-world transfers.
}
