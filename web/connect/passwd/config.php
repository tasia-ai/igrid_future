<?php
$dbHost = 'i.let-us.cyou';
$dbName = 'robust';
$dbUser = 'root';
$dbPass = 'CHANGE_ME_DB_PASSWORD';

// Połączenie mysqli
$conn = new mysqli($dbHost, $dbUser, $dbPass, $dbName);
if ($conn->connect_error) {
    http_response_code(500);
    echo json_encode(['error' => 'can not connect to database']);
    exit;
}

// Email
$mailFrom = 'no-reply@easierit.org';
$mailName = 'Tasia Bot';
$smtpHost = 'mx.easierit.org';
$smtpUser = 'aigrid@easierit.org';
$smtpPass = 'CHANGE_ME_SMTP_PASSWORD';

define('RESET_TOKEN_EXPIRY', 15 * 60);
define('HASH_FUNCTION', 'md5');

$debug = true;
