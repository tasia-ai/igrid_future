<?php
session_start();
header('Content-Type: application/json');

function generateUuidV4() {
    // Generate 16 bytes of random data
    $data = random_bytes(16);

    // Set the version (4) and the clock_seq_hi_and_reserved (1)
    $data[6] = chr(ord($data[6]) & 0x0f | 0x40); // set version to 0100
    $data[8] = chr(ord($data[8]) & 0x3f | 0x80); // set bits 6-7 to 10

    // Format the data into a standard UUID string
    return vsprintf('%s%s-%s-%s-%s-%s%s%s', str_split(bin2hex($data), 4));
}

// Config - REPLACE THESE
$apiKey = 'BEQEI8RI-FJ1g-r9KxZ4Za1PbGvOGg0DhYFOnvd5K3A'; // From https://shapes.inc/developer
$shapeUsername = 'tasia'; // e.g., 'myai'
$uuid = generateUuidV4();
$userId = 'user' . $uuid; // Unique per user
$channelId = 'viewerchat'; // Unique per conversation

// Initialize session history
if (!isset($_SESSION['history'])) {
    $_SESSION['history'] = [];
}

// Handle reset
if (isset($_POST['reset']) && $_POST['reset'] == '1') {
    $_SESSION['history'] = [];
    echo json_encode(['status' => 'reset']);
    exit;
}

// Handle message
if (isset($_POST['message'])) {
    $userMessage = trim($_POST['message']);
    if (!$userMessage) {
        echo json_encode(['error' => 'Empty message']);
        exit;
    }

    // Add user message to history
    $_SESSION['history'][] = ['role' => 'user', 'content' => $userMessage];

    // CURL call to Shapes API
    $ch = curl_init('https://api.shapes.inc/v1/chat/completions');
    curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
    curl_setopt($ch, CURLOPT_POST, true);
    curl_setopt($ch, CURLOPT_POSTFIELDS, json_encode([
        'model' => "shapesinc/{$shapeUsername}",
        'messages' => [['role' => 'user', 'content' => $userMessage]],
    ]));
    curl_setopt($ch, CURLOPT_HTTPHEADER, [
        'Authorization: Bearer ' . $apiKey,
        'Content-Type: application/json',
        'X-User-Id: ' . $userId,
        'X-Channel-Id: ' . $channelId,
    ]);
    $responseJson = curl_exec($ch);
    $httpCode = curl_getinfo($ch, CURLINFO_HTTP_CODE);
    $curlError = curl_error($ch);
    curl_close($ch);

    if ($httpCode === 200 && $responseJson) {
        $response = json_decode($responseJson, true);
        $aiResponse = $response['choices'][0]['message']['content'] ?? 'No response';
        $_SESSION['history'][] = ['role' => 'assistant', 'content' => $aiResponse];
        echo json_encode(['content' => $aiResponse]);
    } else {
        $errorMsg = $curlError ?: "API Error: HTTP $httpCode";
        $_SESSION['history'][] = ['role' => 'system', 'content' => $errorMsg];
        echo json_encode(['error' => $errorMsg]);
    }
    exit;
}

echo json_encode(['error' => 'Invalid request']);