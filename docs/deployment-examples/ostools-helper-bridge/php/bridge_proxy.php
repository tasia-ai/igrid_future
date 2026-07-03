<?php
declare(strict_types=1);

function bridge_proxy_request(string $targetPath): void
{
    $method = $_SERVER['REQUEST_METHOD'] ?? 'GET';
    $bridgeUrl = 'http://127.0.0.1:8015' . $targetPath;

    if ($method !== 'POST') {
        http_response_code(405);
        header('Content-Type: text/plain; charset=utf-8');
        echo 'Only POST is supported.';
        return;
    }

    $payload = file_get_contents('php://input');
    if ($payload === false) {
        http_response_code(400);
        header('Content-Type: text/plain; charset=utf-8');
        echo 'Invalid request body.';
        return;
    }

    $forwardedFor = $_SERVER['REMOTE_ADDR'] ?? '127.0.0.1';

    if (function_exists('curl_init')) {
        $ch = curl_init($bridgeUrl);
        curl_setopt($ch, CURLOPT_POST, true);
        curl_setopt($ch, CURLOPT_POSTFIELDS, $payload);
        curl_setopt($ch, CURLOPT_HTTPHEADER, [
            'Content-Type: text/xml',
            'X-Forwarded-For: ' . $forwardedFor,
        ]);
        curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
        curl_setopt($ch, CURLOPT_CONNECTTIMEOUT, 5);
        curl_setopt($ch, CURLOPT_TIMEOUT, 20);

        $responseBody = curl_exec($ch);
        $httpCode = (int) curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
        $curlErr = curl_error($ch);
        curl_close($ch);

        if ($responseBody === false) {
            http_response_code(502);
            header('Content-Type: text/plain; charset=utf-8');
            echo 'Bridge unavailable: ' . $curlErr;
            return;
        }

        http_response_code($httpCode > 0 ? $httpCode : 200);
        header('Content-Type: text/xml; charset=utf-8');
        echo $responseBody;
        return;
    }

    $context = stream_context_create([
        'http' => [
            'method' => 'POST',
            'header' => "Content-Type: text/xml\r\nX-Forwarded-For: {$forwardedFor}\r\n",
            'content' => $payload,
            'timeout' => 20,
            'ignore_errors' => true,
        ],
    ]);

    $responseBody = @file_get_contents($bridgeUrl, false, $context);
    if ($responseBody === false) {
        http_response_code(502);
        header('Content-Type: text/plain; charset=utf-8');
        echo 'Bridge unavailable.';
        return;
    }

    $code = 200;
    if (!empty($http_response_header) && preg_match('/\s(\d{3})\s/', $http_response_header[0], $m)) {
        $code = (int) $m[1];
    }

    http_response_code($code);
    header('Content-Type: text/xml; charset=utf-8');
    echo $responseBody;
}
