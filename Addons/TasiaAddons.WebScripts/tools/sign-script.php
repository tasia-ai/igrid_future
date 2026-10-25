#!/usr/bin/env php
<?php
declare(strict_types=1);
// Offline CLI tool. Never publish this tool or a private key as a web endpoint.
if (PHP_SAPI !== 'cli') { http_response_code(403); exit; }
try {
    $options = getopt('', ['script:', 'url:', 'private-key:', 'out:', 'expires:']);
    foreach (['script','url','private-key','out'] as $name) {
        if (!isset($options[$name]) || !is_string($options[$name])) {
            throw new RuntimeException('Usage: php sign-script.php --script hello.lsl --url https://scripts.example.org/hello.json --private-key /secure/publisher.pem --out hello.json [--expires UNIX_SECONDS]');
        }
    }
    $url = $options['url'];
    $parts = parse_url($url);
    if ($parts === false || strlen($url) > 2048 || ($parts['scheme'] ?? '') !== 'https' ||
        !isset($parts['host']) || filter_var($parts['host'], FILTER_VALIDATE_IP) !== false ||
        isset($parts['user']) || isset($parts['pass']) || isset($parts['fragment']) ||
        (isset($parts['port']) && $parts['port'] !== 443) || str_contains($url, "\n") || str_contains($url, "\r")) {
        throw new RuntimeException('Use a canonical HTTPS URL with a DNS hostname, no credentials or fragment, and port 443.');
    }
    if (preg_match('/[^\x21-\x7e]/', $url)) throw new RuntimeException('Percent-encode non-ASCII URL characters before signing.');
    $url = 'https://' . strtolower($parts['host']) . (($parts['path'] ?? '') === '' ? '/' : $parts['path']) .
        (isset($parts['query']) ? '?' . $parts['query'] : '');
    $expires = $options['expires'] ?? (string)(time() + 3600);
    if (!is_string($expires) || !ctype_digit($expires) || (int)$expires <= time() || (int)$expires > time() + 86400) {
        throw new RuntimeException('Expiry must be a Unix timestamp within the next 24 hours.');
    }
    if (!is_file($options['script']) || filesize($options['script']) > 65536) {
        throw new RuntimeException('LSL source must be a regular file of at most 65536 bytes.');
    }
    $script = file_get_contents($options['script']);
    if ($script === false || $script === '' || strlen($script) > 65536 || !preg_match('//u', $script) ||
        str_contains($script, "\0") || str_contains($script, "\xEF\xBB\xBF") ||
        preg_match('/^\s*\/\/\s*(?:c#|csharp|vb|jscript|yengine\s*:|phlox\s*:|xengine\s*:)/im', $script)) {
        throw new RuntimeException('Use nonempty UTF-8 LSL source without BOM, NULs or engine/language directives.');
    }
    $keyPath = $options['private-key'];
    if (DIRECTORY_SEPARATOR === '/' && (fileperms($keyPath) & 0077) !== 0) {
        throw new RuntimeException('Private key must have owner-only permissions (chmod 600).');
    }
    $key = openssl_pkey_get_private(file_get_contents($keyPath));
    $details = $key === false ? false : openssl_pkey_get_details($key);
    if ($details === false || $details['type'] !== OPENSSL_KEYTYPE_RSA || $details['bits'] < 3072) {
        throw new RuntimeException('Use an RSA private key with at least 3072 bits.');
    }
    $encoded = base64_encode($script);
    $message = "maRunScriptFromWeb:v1\n" . $url . "\n" . (string)(int)$expires . "\n" . $encoded;
    if (!openssl_sign($message, $signature, $key, OPENSSL_ALGO_SHA256)) {
        throw new RuntimeException('Signing failed.');
    }
    $envelope = json_encode(['version'=>1, 'url'=>$url, 'expires'=>(int)$expires, 'script'=>$encoded,
        'signature'=>base64_encode($signature)], JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR) . "\n";
    $output = fopen($options['out'], 'x');
    if ($output === false) throw new RuntimeException('Output already exists or cannot be created; choose a new file.');
    try {
        if (fwrite($output, $envelope) !== strlen($envelope)) throw new RuntimeException('Incomplete output write.');
    } finally { fclose($output); }
    fwrite(STDOUT, "Signed LSL envelope created. Publish only the JSON file and keep the private key offline.\n");
} catch (Throwable $error) {
    fwrite(STDERR, "Signing rejected: " . $error->getMessage() . "\n");
    exit(1);
}
