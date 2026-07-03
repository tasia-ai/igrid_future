<?php
// Simple settings view that displays the linked avatar and balance.

session_start();

require_once __DIR__ . '/config.php';
require_once __DIR__ . '/OpenSimClient.php';

$config = include __DIR__ . '/config.php';
$client = new OpenSimClient($config);
$tokens = $_SESSION['opensim_tokens'] ?? null;
$profile = $_SESSION['opensim_profile'] ?? null;

if (!$tokens) {
    echo '<p>No OpenSim account linked. <a href="/openim/login">Link now</a>.</p>';
    exit;
}

$balance = $client->getBalance($tokens['access_token']);

echo '<h2>OpenSim Link</h2>';
echo '<p>Avatar: ' . htmlspecialchars($profile['name'] ?? $profile['agent_id'] ?? 'Unknown', ENT_QUOTES, 'UTF-8') . '</p>';
echo '<p>Balance: ' . htmlspecialchars((string)($balance['balance'] ?? '0'), ENT_QUOTES, 'UTF-8') . '</p>';
echo '<p>Last Updated: ' . htmlspecialchars($balance['updated_at'] ?? 'n/a', ENT_QUOTES, 'UTF-8') . '</p>';
