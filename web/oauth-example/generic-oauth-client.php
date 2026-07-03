<?php
// Minimal example of calling the Robust OAuth token endpoint from a standalone page.
// Replace the constants below with the values issued by your Robust add-on.
const ROBUST_BASE_URL = 'https://robust.example.com';
const CLIENT_ID = 'your-client-id';
const CLIENT_SECRET = 'your-client-secret';
const SCOPES = 'chat.audit.read';

function fetchAccessToken(): array
{
    $payload = http_build_query([
        'grant_type' => 'client_credentials',
        'scope' => SCOPES,
    ]);

    $ch = curl_init(ROBUST_BASE_URL . '/oauth/token');
    curl_setopt_array($ch, [
        CURLOPT_POST => true,
        CURLOPT_POSTFIELDS => $payload,
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_HTTPAUTH => CURLAUTH_BASIC,
        CURLOPT_USERPWD => CLIENT_ID . ':' . CLIENT_SECRET,
        CURLOPT_HTTPHEADER => [
            'Content-Type: application/x-www-form-urlencoded',
            'Accept: application/json',
        ],
    ]);

    $response = curl_exec($ch);
    if ($response === false) {
        throw new RuntimeException('Token request failed: ' . curl_error($ch));
    }

    $status = curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    curl_close($ch);

    $json = json_decode($response, true);
    if ($status !== 200 || !is_array($json) || empty($json['access_token'])) {
        throw new RuntimeException('Unexpected token response: ' . $response);
    }

    return $json;
}

function callChatAudit(array $token): array
{
    $ch = curl_init(ROBUST_BASE_URL . '/addons/chat-audit?limit=20');
    curl_setopt_array($ch, [
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_HTTPHEADER => [
            'Accept: application/json',
            'Authorization: Bearer ' . $token['access_token'],
        ],
    ]);

    $response = curl_exec($ch);
    if ($response === false) {
        throw new RuntimeException('Chat audit request failed: ' . curl_error($ch));
    }

    $status = curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    curl_close($ch);

    if ($status !== 200) {
        throw new RuntimeException('Chat audit returned HTTP ' . $status . ': ' . $response);
    }

    $json = json_decode($response, true);
    if (!is_array($json)) {
        throw new RuntimeException('Unable to parse chat audit response: ' . $response);
    }

    return $json;
}

try {
    $token = fetchAccessToken();
    $messages = callChatAudit($token);
} catch (Throwable $error) {
    http_response_code(500);
    echo '<h1>OAuth demo failed</h1>';
    echo '<pre>' . htmlspecialchars($error->getMessage()) . '</pre>';
    exit;
}
?>
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <title>Chat Audit OAuth Demo</title>
    <style>
        body { font-family: system-ui, sans-serif; margin: 2rem; }
        table { border-collapse: collapse; width: 100%; }
        th, td { border: 1px solid #ccc; padding: 0.5rem; text-align: left; }
        th { background: #f6f8fa; }
        tbody tr:nth-child(odd) { background: #fafafa; }
    </style>
</head>
<body>
    <h1>Chat Audit OAuth Demo</h1>
    <p>Fetched <?php echo count($messages); ?> records from the `/addons/chat-audit` endpoint.</p>
    <table>
        <thead>
            <tr>
                <th>Timestamp</th>
                <th>Region</th>
                <th>Sender</th>
                <th>Target</th>
                <th>Message</th>
            </tr>
        </thead>
        <tbody>
            <?php foreach ($messages as $entry): ?>
                <tr>
                    <td><?php echo htmlspecialchars($entry['timestamp'] ?? ''); ?></td>
                    <td><?php echo htmlspecialchars($entry['region'] ?? ''); ?></td>
                    <td><?php echo htmlspecialchars($entry['from_name'] ?? ''); ?></td>
                    <td><?php echo htmlspecialchars($entry['to_name'] ?? ''); ?></td>
                    <td><?php echo nl2br(htmlspecialchars($entry['message'] ?? '')); ?></td>
                </tr>
            <?php endforeach; ?>
        </tbody>
    </table>
</body>
</html>
