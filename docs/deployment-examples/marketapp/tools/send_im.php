<?php
declare(strict_types=1);

/**
 * CLI IM sender for OpenSim Robust XML-RPC.
 *
 * Usage:
 * php send_im.php --to "43827618-1993-43d8-bf6b-fc966a943381" --message "Hello from I-Grid" \
 *   [--robust "http://i.let-us.cyou:8002"] \
 *   [--from "00000000-0000-0000-0000-000000000000"] \
 *   [--from-name "I-Grid Security"]
 */

if (PHP_SAPI !== 'cli') {
    fwrite(STDERR, "Run from CLI only.\n");
    exit(1);
}

$opts = getopt('', ['to:', 'message:', 'robust::', 'from::', 'from-name::']);

$to = strtolower(trim((string)($opts['to'] ?? '')));
$message = (string)($opts['message'] ?? '');
$robustBase = rtrim((string)($opts['robust'] ?? 'http://i.let-us.cyou:8002'), '/');
$fromUuid = strtolower(trim((string)($opts['from'] ?? '00000000-0000-0000-0000-000000000000')));
$fromName = trim((string)($opts['from-name'] ?? 'I-Grid Security'));

if (!valid_uuid($to)) {
    fail('Invalid --to UUID');
}
if (!valid_uuid($fromUuid)) {
    $fromUuid = '00000000-0000-0000-0000-000000000000';
}
if ($message === '') {
    fail('Message cannot be empty');
}
if ($fromName === '') {
    $fromName = 'I-Grid Security';
}

$payload = [
    'from_agent_id' => $fromUuid,
    'to_agent_id' => $to,
    'im_session_id' => uuid_v4(),
    'timestamp' => (string)time(),
    'from_agent_name' => $fromName,
    'message' => $message,
    'dialog' => base64_encode(chr(0)),
    'from_group' => 'FALSE',
    'offline' => base64_encode(chr(0)),
    'parent_estate_id' => '0',
    'position_x' => '128',
    'position_y' => '128',
    'position_z' => '25',
    'region_id' => '00000000-0000-0000-0000-000000000000',
    'binary_bucket' => '',
];

$xml = '<?xml version="1.0"?>'
    . '<methodCall><methodName>grid_instant_message</methodName><params><param><value><struct>';
foreach ($payload as $key => $value) {
    $xml .= '<member><name>' . xml_escape((string)$key) . '</name><value><string>'
        . xml_escape((string)$value) . '</string></value></member>';
}
$xml .= '</struct></value></param></params></methodCall>';

$response = http_post_payload($robustBase . '/', $xml, 'text/xml');
if ($response['error'] !== '') {
    fail('HTTP error: ' . $response['error']);
}

$successValue = xmlrpc_success_value($response['body']);
if ($successValue === true) {
    echo "IM sent successfully to {$to}\n";
    exit(0);
}
if ($successValue === false) {
    fail('Grid messaging rejected IM (user offline/unroutable)');
}

fail('Unexpected XML-RPC response (HTTP ' . $response['status'] . ')');

function http_post_payload(string $url, string $payload, string $contentType = 'text/plain', int $timeoutSeconds = 8): array
{
    $ch = curl_init($url);
    if ($ch === false) {
        return ['status' => 0, 'body' => '', 'error' => 'Unable to initialize curl'];
    }

    curl_setopt_array($ch, [
        CURLOPT_POST => true,
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_CONNECTTIMEOUT => 4,
        CURLOPT_TIMEOUT => $timeoutSeconds,
        CURLOPT_HTTPHEADER => [
            'Content-Type: ' . $contentType,
            'Accept: text/xml, application/xml, text/plain, */*',
        ],
        CURLOPT_POSTFIELDS => $payload,
    ]);

    $body = curl_exec($ch);
    $error = curl_error($ch);
    $status = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    curl_close($ch);

    return [
        'status' => $status,
        'body' => is_string($body) ? $body : '',
        'error' => (string)$error,
    ];
}

function xmlrpc_success_value(string $xml): ?bool
{
    if ($xml === '') {
        return null;
    }

    if (preg_match('/<name>success<\/name>\s*<value>\s*<boolean>([01])<\/boolean>/is', $xml, $m)) {
        return $m[1] === '1';
    }
    if (preg_match('/<name>success<\/name>\s*<value>\s*<string>(.*?)<\/string>/is', $xml, $m)) {
        $normalized = strtoupper(trim(html_entity_decode((string)$m[1], ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8')));
        if (in_array($normalized, ['TRUE', '1', 'YES'], true)) {
            return true;
        }
        if (in_array($normalized, ['FALSE', '0', 'NO'], true)) {
            return false;
        }
    }
    return null;
}

function xml_escape(string $value): string
{
    return htmlspecialchars($value, ENT_QUOTES | ENT_XML1, 'UTF-8');
}

function valid_uuid(string $value): bool
{
    return (bool)preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i', $value);
}

function uuid_v4(): string
{
    $data = random_bytes(16);
    $data[6] = chr((ord($data[6]) & 0x0f) | 0x40);
    $data[8] = chr((ord($data[8]) & 0x3f) | 0x80);
    return vsprintf('%s%s-%s-%s-%s-%s%s%s', str_split(bin2hex($data), 4));
}

function fail(string $message): void
{
    fwrite(STDERR, $message . "\n");
    exit(1);
}
