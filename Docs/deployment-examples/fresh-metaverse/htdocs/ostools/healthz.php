<?php
declare(strict_types=1);

$url = 'http://127.0.0.1:8015/healthz';
$ctx = stream_context_create([
    'http' => [
        'method' => 'GET',
        'timeout' => 5,
        'ignore_errors' => true,
    ],
]);

$body = @file_get_contents($url, false, $ctx);
if ($body === false) {
    http_response_code(503);
    header('Content-Type: application/json; charset=utf-8');
    echo json_encode(['ok' => false, 'error' => 'bridge unavailable'], JSON_PRETTY_PRINT);
    exit;
}

$code = 200;
if (!empty($http_response_header) && preg_match('/\s(\d{3})\s/', $http_response_header[0], $m)) {
    $code = (int) $m[1];
}

http_response_code($code);
header('Content-Type: application/json; charset=utf-8');
echo $body;
