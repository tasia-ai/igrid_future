<?php
declare(strict_types=1);

require dirname(__DIR__) . '/lib.php';

$cfg = iauth_config();
header('Content-Type: application/json; charset=utf-8');

try {
    echo json_encode(iauth_oidc_jwks($cfg), JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT);
} catch (Throwable $e) {
    http_response_code(500);
    echo json_encode(['error' => 'server_error']);
}
