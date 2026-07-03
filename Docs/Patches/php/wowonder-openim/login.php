<?php
// Controller entry point that initiates the OAuth flow.
// Include this in a route such as /openim/login.

session_start();

require_once __DIR__ . '/config.php';
require_once __DIR__ . '/OpenSimClient.php';

$config = include __DIR__ . '/config.php';
$client = new OpenSimClient($config);

$state = bin2hex(random_bytes(16));
$codeVerifier = rtrim(strtr(base64_encode(random_bytes(32)), '+/', '-_'), '=');
$codeChallenge = rtrim(strtr(base64_encode(hash('sha256', $codeVerifier, true)), '+/', '-_'), '=');

$_SESSION['opensim_oauth_state'] = $state;
$_SESSION['opensim_code_verifier'] = $codeVerifier;

header('Location: ' . $client->getAuthorizeUrl($state, $codeChallenge));
exit;
