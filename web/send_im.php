<?php
// Minimal example showing how to authenticate and deliver an IM via /api/v1/im/send.
// Replace placeholder values with credentials and avatar IDs from your grid.

$baseUrl = 'https://robust.example.com';
$clientId = 'client-id';
$clientSecret = 'client-secret';
$username = 'first.last';
$password = 'password';
$scope = 'im.write';

function postForm(string $url, array $data, array $headers = []): array
{
    $ch = curl_init($url);
    curl_setopt($ch, CURLOPT_POST, true);
    curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
    curl_setopt($ch, CURLOPT_POSTFIELDS, http_build_query($data));
    curl_setopt($ch, CURLOPT_HTTPHEADER, $headers);
    $response = curl_exec($ch);
    if ($response === false) {
        throw new RuntimeException('HTTP error: ' . curl_error($ch));
    }
    $status = curl_getinfo($ch, CURLINFO_HTTP_CODE);
    curl_close($ch);
    return [$status, json_decode($response, true) ?? []];
}

[$status, $tokenResponse] = postForm(
    $baseUrl . '/wowonder/oauth/token',
    [
        'grant_type' => 'password',
        'client_id' => $clientId,
        'client_secret' => $clientSecret,
        'username' => $username,
        'password' => $password,
        'scope' => $scope,
    ]
);

if ($status !== 200 || empty($tokenResponse['access_token'])) {
    throw new RuntimeException('Token request failed: ' . json_encode($tokenResponse));
}

$accessToken = $tokenResponse['access_token'];
$fromAgentId = $tokenResponse['user_id'] ?? $tokenResponse['agent_id'] ?? '00000000-0000-0000-0000-000000000000';

$imPayload = [
    'from_agent_id' => $fromAgentId,
    'to_agent_id' => '00000000-0000-0000-0000-000000000000',
    'message' => 'Hello from PHP!',
    'dialog' => 0,
    'session_id' => '00000000-0000-0000-0000-000000000000',
    'region_id' => '00000000-0000-0000-0000-000000000000',
    'position' => ['x' => 128.0, 'y' => 128.0, 'z' => 25.0],
    'offline' => true,
];

$ch = curl_init($baseUrl . '/api/v1/im/send');
curl_setopt($ch, CURLOPT_POST, true);
curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
curl_setopt($ch, CURLOPT_HTTPHEADER, [
    'Authorization: Bearer ' . $accessToken,
    'Content-Type: application/json',
]);
curl_setopt($ch, CURLOPT_POSTFIELDS, json_encode($imPayload));

$imResponse = curl_exec($ch);
if ($imResponse === false) {
    throw new RuntimeException('IM request failed: ' . curl_error($ch));
}
$imStatus = curl_getinfo($ch, CURLINFO_HTTP_CODE);
curl_close($ch);

$decoded = json_decode($imResponse, true) ?? [];

if (!empty($decoded['delivered'])) {
    echo "Delivered in-world at {$decoded['timestamp']}\n";
} elseif (!empty($decoded['offline'])) {
    echo "Stored for offline delivery at {$decoded['timestamp']}\n";
} else {
    echo 'Delivery failed (HTTP ' . $imStatus . '): ' . $imResponse . "\n";
}
