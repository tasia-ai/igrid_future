<?php
/**
 * TasiaNGC Web Viewer - Main Entry Point
 * PHP server that handles login and serves the web interface
 */

header('Access-Control-Allow-Origin: *');
header('Access-Control-Allow-Methods: GET, POST, OPTIONS');
header('Access-Control-Allow-Headers: Content-Type');

if ($_SERVER['REQUEST_METHOD'] === 'OPTIONS') {
    http_response_code(200);
    exit;
}

$requestUri = $_SERVER['REQUEST_URI'];
$requestPath = parse_url($requestUri, PHP_URL_PATH) ?: '/';
$method = $_SERVER['REQUEST_METHOD'];

// API Endpoints
if (strpos($requestPath, '/api/') === 0) {
    header('Content-Type: application/json');
    
    if ($requestPath === '/api/login' && $method === 'POST') {
        handleLogin();
    } elseif ($requestPath === '/api/logout' && $method === 'POST') {
        handleLogout();
    } elseif ($requestPath === '/api/region' && $method === 'GET') {
        handleRegionInfo();
    } else {
        http_response_code(404);
        echo json_encode(['error' => 'Not found']);
    }
    exit;
}

// Serve static files
$filePath = __DIR__ . '/';

if ($requestPath === '/' || $requestPath === '/index.html' || $requestPath === '') {
    readfile($filePath . 'index_viewer.html');
    exit;
}

$staticFile = $filePath . ltrim($requestPath, '/');
if (file_exists($staticFile) && is_file($staticFile)) {
    $ext = pathinfo($staticFile, PATHINFO_EXTENSION);
    $mimeTypes = [
        'html' => 'text/html',
        'js' => 'application/javascript',
        'css' => 'text/css',
        'png' => 'image/png',
        'jpg' => 'image/jpeg',
        'gif' => 'image/gif',
        'json' => 'application/json',
        'svg' => 'image/svg+xml'
    ];
    header('Content-Type: ' . ($mimeTypes[$ext] ?? 'text/plain'));
    readfile($staticFile);
    exit;
}

echo '<!DOCTYPE html><html><head><title>404</title></head><body><h1>Not Found</h1></body></html>';

function handleLogin() {
    $input = json_decode(file_get_contents('php://input'), true);
    
    $first = $input['first'] ?? '';
    $last = $input['last'] ?? '';
    $password = $input['password'] ?? '';
    $start = $input['start'] ?? 'last';
    
    if (empty($first) || empty($last) || empty($password)) {
        http_response_code(400);
        echo json_encode(['success' => false, 'error' => 'Missing required fields']);
        return;
    }
    
    // OpenSim login settings - UPDATE THESE
    $opensimHost = getenv('OPENSIM_HOST') ?: '127.0.0.1';
    $opensimPort = getenv('OPENSIM_PORT') ?: '8002';
    
    // Generate identifiers
    $mac = getMacAddress();
    $id0 = md5("OpenSim:{$mac}:PHP");
    
    // Build XML-RPC login request
    $xml = '<?xml version="1.0"?>
    <methodCall>
        <methodName>login_to_simulator</methodName>
        <params>
            <param><value><struct>
                <member><name>first</name><value><string>' . xmlEscape($first) . '</string></value></member>
                <member><name>last</name><value><string>' . xmlEscape($last) . '</string></value></member>
                <member><name>passwd</name><value><string>' . xmlEscape($password) . '</string></value></member>
                <member><name>start</name><value><string>' . xmlEscape($start) . '</string></value></member>
                <member><name>channel</name><value><string>TasiaNGC-Web</string></value></member>
                <member><name>mac</name><value><string>' . $mac . '</string></value></member>
                <member><name>id0</name><value><string>' . $id0 . '</string></value></member>
                <member><name>version</name><value><string>TasiaNGC-1.0.0</string></value></member>
                <member><name>platform</name><value><string>Win</string></value></member>
                <member><name>options</name><value><array><data>
                    <value><string>inventory-root</string></value>
                    <value><string>buddy-list</string></value>
                    <value><string>login-flags</string></value>
                    <value><string>global-textures</string></value>
                </data></array></value></member>
            </struct></value></param>
        </params>
    </methodCall>';
    
    // Send XML-RPC request to OpenSim
    $ch = curl_init();
    curl_setopt($ch, CURLOPT_URL, "http://{$opensimHost}:{$opensimPort}/");
    curl_setopt($ch, CURLOPT_POST, true);
    curl_setopt($ch, CURLOPT_POSTFIELDS, $xml);
    curl_setopt($ch, CURLOPT_HTTPHEADER, ['Content-Type: application/xml']);
    curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
    curl_setopt($ch, CURLOPT_TIMEOUT, 30);
    
    $response = curl_exec($ch);
    $curlError = curl_error($ch);
    curl_close($ch);
    
    if ($curlError) {
        echo json_encode(['success' => false, 'error' => 'Connection failed: ' . $curlError]);
        return;
    }
    
    // Parse XML-RPC response
    $result = parseXmlRpcResponse($response);
    
    if (isset($result['login']) && $result['login'] === 'true') {
        // Generate session token
        $sessionId = bin2hex(random_bytes(16));
        
        // Store session
        $sessions = [];
        if (file_exists(__DIR__ . '/sessions.json')) {
            $sessions = json_decode(file_get_contents(__DIR__ . '/sessions.json'), true) ?: [];
        }
        
        $sessions[$sessionId] = [
            'agent_id' => $result['agent_id'] ?? '',
            'session_id' => $result['session_id'] ?? '',
            'session_hash' => $result['session_hash'] ?? '',
            'sim_ip' => $result['sim_ip'] ?? '',
            'sim_port' => $result['sim_port'] ?? '',
            'circuit_code' => $result['circuit_code'] ?? '',
            'first' => $first,
            'last' => $last,
            'created' => time()
        ];
        
        file_put_contents(__DIR__ . '/sessions.json', json_encode($sessions));
        
        echo json_encode([
            'success' => true,
            'session_id' => $sessionId,
            'agent_id' => $result['agent_id'] ?? '',
            'sim_ip' => $result['sim_ip'] ?? '',
            'sim_port' => $result['sim_port'] ?? '',
            'circuit_code' => $result['circuit_code'] ?? '',
            'data' => $result
        ]);
    } else {
        $error = $result['message'] ?? 'Login failed';
        http_response_code(401);
        echo json_encode(['success' => false, 'error' => $error]);
    }
}

function handleLogout() {
    $input = json_decode(file_get_contents('php://input'), true);
    $sessionId = $input['session_id'] ?? '';
    
    if ($sessionId && file_exists(__DIR__ . '/sessions.json')) {
        $sessions = json_decode(file_get_contents(__DIR__ . '/sessions.json'), true) ?: [];
        if (isset($sessions[$sessionId])) {
            unset($sessions[$sessionId]);
            file_put_contents(__DIR__ . '/sessions.json', json_encode($sessions));
        }
    }
    
    echo json_encode(['success' => true]);
}

function handleRegionInfo() {
    // Return region information for map
    echo json_encode([
        'name' => 'TasiaNGC Grid',
        'grid_url' => getenv('GRID_URL') ?: 'http://127.0.0.1:8002'
    ]);
}

function xmlEscape($str) {
    return htmlspecialchars($str, ENT_XML1, 'UTF-8');
}

function getMacAddress() {
    // Generate a consistent MAC address based on system
    $mac = '00:00:00:00:00:00';
    if (file_exists('/sys/class/net/eth0/address')) {
        $mac = trim(file_get_contents('/sys/class/net/eth0/address'));
    } elseif (file_exists('/sys/class/net/en0/address')) {
        $mac = trim(file_get_contents('/sys/class/net/en0/address'));
    }
    return $mac ?: '00:00:00:00:00:' . str_pad(dechex(mt_rand(0, 255)), 2, '0', STR_PAD_LEFT);
}

function parseXmlRpcResponse($xml) {
    $result = [];
    
    // Simple XML parsing without DOM
    if (preg_match('/<name>login<\/name><value><string>([^<]+)/', $xml, $m)) {
        $result['login'] = $m[1];
    }
    
    foreach (['agent_id', 'session_id', 'session_hash', 'sim_ip', 'sim_port', 'circuit_code', 'message', 'region_x', 'region_y'] as $key) {
        if (preg_match("/<name>{$key}<\/name><value><string>([^<]+)/", $xml, $m)) {
            $result[$key] = $m[1];
        }
        if (preg_match("/<name>{$key}<\/name><value><i4>([^<]+)/", $xml, $m)) {
            $result[$key] = $m[1];
        }
    }
    
    // Handle seed_capability
    if (preg_match('/<name>seed_capability<\/name><value><string>([^<]+)/', $xml, $m)) {
        $result['seed_capability'] = $m[1];
    }
    
    // Handle capabilities array
    if (preg_match_all('/<name>(\w+)<\/name><value><string>([^<]+)/', $xml, $matches, PREG_SET_ORDER)) {
        $result['capabilities'] = [];
        foreach ($matches as $m) {
            $result['capabilities'][$m[1]] = $m[2];
        }
    }
    
    return $result;
}
?>
