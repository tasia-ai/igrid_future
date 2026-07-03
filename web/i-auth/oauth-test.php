<?php
ini_set('display_errors', '1');
ini_set('display_startup_errors', '1');
error_reporting(E_ALL);

set_error_handler(function ($severity, $message, $file, $line) {
    throw new ErrorException($message, 0, $severity, $file, $line);
});

register_shutdown_function(function () {
    $err = error_get_last();
    if ($err !== null) {
        file_put_contents(
            __DIR__ . '/oauth-debug.log',
            "\n==== FATAL SHUTDOWN ====\n" . print_r($err, true) . "\n",
            FILE_APPEND
        );
    }
});

session_start();

define('DEBUG_MODE', true);

$config = [
    'issuer'        => 'https://i.let-us.cyou/i-auth/oidc',
    'client_id'     => 'marty-auth-broker',
    'client_secret' => 'CHANGE_ME_OIDC_CLIENT_SECRET',
    'redirect_uri'  => 'https://i.let-us.cyou/i-auth/oauth-test.php',
    'scope'         => 'openid profile email',
];

function h($v) {
    return htmlspecialchars((string)$v, ENT_QUOTES, 'UTF-8');
}

function debug_log_file($title, $data) {
    if (!DEBUG_MODE) return;
    $line = "\n==== " . date('Y-m-d H:i:s') . " | " . $title . " ====\n";
    $line .= print_r($data, true) . "\n";
    file_put_contents(__DIR__ . '/oauth-debug.log', $line, FILE_APPEND);
}

function pretty($data) {
    return json_encode($data, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES);
}

function http_get_json($url)
{
    $ch = curl_init($url);
    curl_setopt_array($ch, [
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_FOLLOWLOCATION => true,
        CURLOPT_TIMEOUT => 30,
        CURLOPT_SSL_VERIFYPEER => true,
        CURLOPT_HTTPHEADER => ['Accept: application/json'],
    ]);

    $response = curl_exec($ch);
    $error = curl_error($ch);
    $errno = curl_errno($ch);
    $status = curl_getinfo($ch, CURLINFO_HTTP_CODE);
    curl_close($ch);

    debug_log_file('GET ' . $url, [
        'status' => $status,
        'curl_errno' => $errno,
        'curl_error' => $error,
        'raw_response' => $response,
    ]);

    if ($response === false) {
        throw new Exception("GET failed: " . $error);
    }

    $json = json_decode($response, true);
    if (!is_array($json)) {
        throw new Exception("GET invalid JSON. HTTP {$status}. Raw body: " . $response);
    }

    return $json;
}

function http_post_form_json($url, $data)
{
    $ch = curl_init($url);
    curl_setopt_array($ch, [
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_FOLLOWLOCATION => true,
        CURLOPT_TIMEOUT => 30,
        CURLOPT_POST => true,
        CURLOPT_POSTFIELDS => http_build_query($data),
        CURLOPT_HTTPHEADER => [
            'Accept: application/json',
            'Content-Type: application/x-www-form-urlencoded',
        ],
        CURLOPT_SSL_VERIFYPEER => true,
    ]);

    $response = curl_exec($ch);
    $error = curl_error($ch);
    $errno = curl_errno($ch);
    $status = curl_getinfo($ch, CURLINFO_HTTP_CODE);
    curl_close($ch);

    debug_log_file('POST ' . $url, [
        'post_data' => $data,
        'status' => $status,
        'curl_errno' => $errno,
        'curl_error' => $error,
        'raw_response' => $response,
    ]);

    if ($response === false) {
        throw new Exception("POST failed: " . $error);
    }

    if ($status >= 400) {
        throw new Exception("POST HTTP {$status}. Raw body: " . $response);
    }

    $json = json_decode($response, true);
    if (!is_array($json)) {
        throw new Exception("POST invalid JSON. HTTP {$status}. Raw body: " . $response);
    }

    return $json;
}

function http_get_bearer_json($url, $token)
{
    $ch = curl_init($url);
    curl_setopt_array($ch, [
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_FOLLOWLOCATION => true,
        CURLOPT_TIMEOUT => 30,
        CURLOPT_SSL_VERIFYPEER => true,
        CURLOPT_HTTPHEADER => [
            'Accept: application/json',
            'Authorization: Bearer ' . $token,
        ],
    ]);

    $response = curl_exec($ch);
    $error = curl_error($ch);
    $errno = curl_errno($ch);
    $status = curl_getinfo($ch, CURLINFO_HTTP_CODE);
    curl_close($ch);

    debug_log_file('GET userinfo ' . $url, [
        'status' => $status,
        'curl_errno' => $errno,
        'curl_error' => $error,
        'raw_response' => $response,
    ]);

    if ($response === false) {
        throw new Exception("userinfo failed: " . $error);
    }

    if ($status >= 400) {
        throw new Exception("userinfo HTTP {$status}. Raw body: " . $response);
    }

    $json = json_decode($response, true);
    if (!is_array($json)) {
        throw new Exception("userinfo invalid JSON. HTTP {$status}. Raw body: " . $response);
    }

    return $json;
}

$error = null;
$discovery = null;
$tokens = null;
$userinfo = null;

try {
    $wellKnown = rtrim($config['issuer'], '/') . '/.well-known/openid-configuration';
    $discovery = http_get_json($wellKnown);
} catch (Throwable $e) {
    $error = $e->getMessage();
    debug_log_file('DISCOVERY ERROR', $error);
}

$action = $_GET['action'] ?? '';

if ($action === 'logout') {
    session_destroy();
    header('Location: ' . $config['redirect_uri']);
    exit;
}

if ($action === 'login') {
    if (!$discovery) {
        die('Discovery failed. Check issuer URL.');
    }

    $state = bin2hex(random_bytes(16));
    $nonce = bin2hex(random_bytes(16));

    $_SESSION['oauth_state'] = $state;
    $_SESSION['oauth_nonce'] = $nonce;

    $authUrl = $discovery['authorization_endpoint'] . '?' . http_build_query([
        'client_id'     => $config['client_id'],
        'redirect_uri'  => $config['redirect_uri'],
        'response_type' => 'code',
        'scope'         => $config['scope'],
        'state'         => $state,
        'nonce'         => $nonce,
    ]);

    debug_log_file('REDIRECT TO AUTH', $authUrl);

    header('Location: ' . $authUrl);
    exit;
}

if (isset($_GET['code']) || isset($_GET['error'])) {
    try {
        debug_log_file('CALLBACK GET', $_GET);
        debug_log_file('SESSION BEFORE CALLBACK', $_SESSION);

        if (isset($_GET['error'])) {
            throw new Exception(
                'Provider returned error: ' .
                ($_GET['error'] ?? '') .
                ' | description: ' .
                ($_GET['error_description'] ?? '')
            );
        }

        if (!$discovery) {
            throw new Exception('Discovery failed.');
        }

        if (!isset($_GET['state'], $_SESSION['oauth_state']) || !hash_equals($_SESSION['oauth_state'], $_GET['state'])) {
            throw new Exception('Invalid state.');
        }

        $tokens = http_post_form_json(
            $discovery['token_endpoint'],
            [
                'grant_type'    => 'authorization_code',
                'code'          => $_GET['code'],
                'redirect_uri'  => $config['redirect_uri'],
                'client_id'     => $config['client_id'],
                'client_secret' => $config['client_secret'],
            ]
        );

        $_SESSION['tokens'] = $tokens;

        if (!empty($tokens['access_token']) && !empty($discovery['userinfo_endpoint'])) {
            $userinfo = http_get_bearer_json($discovery['userinfo_endpoint'], $tokens['access_token']);
            $_SESSION['userinfo'] = $userinfo;
        }

        header('Location: ' . $config['redirect_uri']);
        exit;
    } catch (Throwable $e) {
        $error = $e->getMessage();
        debug_log_file('CALLBACK EXCEPTION', [
            'message' => $e->getMessage(),
            'file' => $e->getFile(),
            'line' => $e->getLine(),
            'trace' => $e->getTraceAsString(),
        ]);
    }
}

if (isset($_SESSION['tokens'])) {
    $tokens = $_SESSION['tokens'];
}
if (isset($_SESSION['userinfo'])) {
    $userinfo = $_SESSION['userinfo'];
}
?>
<!doctype html>
<html>
<head>
    <meta charset="utf-8">
    <title>OAuth Test Debug</title>
    <style>
        body { font-family: Arial, sans-serif; max-width: 1000px; margin: 30px auto; background:#111; color:#eee; padding:0 16px; }
        .box { background:#1b1b1b; border:1px solid #333; border-radius:12px; padding:16px; margin-bottom:16px; }
        pre { white-space:pre-wrap; word-break:break-word; background:#0d0d0d; border:1px solid #333; padding:12px; border-radius:8px; }
        a.btn { display:inline-block; padding:10px 14px; background:#2f6fed; color:#fff; text-decoration:none; border-radius:8px; margin-right:8px; }
        a.red { background:#b93838; }
    </style>
</head>
<body>
    <h1>OAuth Test Debug</h1>

    <div class="box">
        <p><b>Issuer:</b> <?= h($config['issuer']) ?></p>
        <p><b>Redirect URI:</b> <?= h($config['redirect_uri']) ?></p>
        <a class="btn" href="?action=login">Login</a>
        <a class="btn red" href="?action=logout">Clear session</a>
    </div>

    <?php if ($error): ?>
    <div class="box">
        <h2>Error</h2>
        <pre><?= h($error) ?></pre>
    </div>
    <?php endif; ?>

    <div class="box">
        <h2>GET</h2>
        <pre><?= h(print_r($_GET, true)) ?></pre>
    </div>

    <div class="box">
        <h2>SESSION</h2>
        <pre><?= h(print_r($_SESSION, true)) ?></pre>
    </div>

    <?php if ($discovery): ?>
    <div class="box">
        <h2>Discovery</h2>
        <pre><?= h(pretty($discovery)) ?></pre>
    </div>
    <?php endif; ?>

    <?php if ($tokens): ?>
    <div class="box">
        <h2>Tokens</h2>
        <pre><?= h(pretty($tokens)) ?></pre>
    </div>
    <?php endif; ?>

    <?php if ($userinfo): ?>
    <div class="box">
        <h2>Userinfo</h2>
        <pre><?= h(pretty($userinfo)) ?></pre>
    </div>
    <?php endif; ?>

    <div class="box">
        <h2>Debug log file</h2>
        <pre><?= h(__DIR__ . '/oauth-debug.log') ?></pre>
    </div>
</body>
</html>