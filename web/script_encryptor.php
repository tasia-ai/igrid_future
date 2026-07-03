<?php
session_start();

$APP_TITLE = 'LSL Protector Standalone';
$DATA_DIR = __DIR__ . DIRECTORY_SEPARATOR . 'lsl_protector_data';
$RECORDS_FILE = $DATA_DIR . DIRECTORY_SEPARATOR . 'records.json';

if (!is_dir($DATA_DIR)) {
    @mkdir($DATA_DIR, 0775, true);
}

if (empty($_SESSION['lsl_protector_csrf'])) {
    $_SESSION['lsl_protector_csrf'] = bin2hex(random_bytes_safe(16));
}

$state = array(
    'message' => '',
    'error' => '',
    'output' => '',
    'mapping' => '',
    'source' => '',
    'label' => '',
    'record_id' => '',
    'mode' => 'protect',
    'active_tab' => 'source',
);

$options = array(
    'strip_comments' => true,
    'rename_symbols' => true,
    'trim_whitespace' => true,
    'pack_one_line' => false,
    'fake_comments' => false,
    'show_mapping' => true,
);

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $state['mode'] = isset($_POST['action_mode']) ? preg_replace('/[^a-z_]/', '', (string) $_POST['action_mode']) : 'protect';
    $state['label'] = isset($_POST['label']) ? trim((string) $_POST['label']) : '';
    $state['source'] = isset($_POST['source']) ? (string) $_POST['source'] : '';
    $state['record_id'] = isset($_POST['record_id']) ? trim((string) $_POST['record_id']) : '';
    $state['active_tab'] = isset($_POST['active_tab']) ? preg_replace('/[^a-z_]/', '', (string) $_POST['active_tab']) : 'source';

    $options = array(
        'strip_comments' => !empty($_POST['strip_comments']),
        'rename_symbols' => !empty($_POST['rename_symbols']),
        'trim_whitespace' => !empty($_POST['trim_whitespace']),
        'pack_one_line' => !empty($_POST['pack_one_line']),
        'fake_comments' => !empty($_POST['fake_comments']),
        'show_mapping' => !empty($_POST['show_mapping']),
    );

    $csrf = isset($_POST['csrf']) ? (string) $_POST['csrf'] : '';
    if (!hash_equals($_SESSION['lsl_protector_csrf'], $csrf)) {
        $state['error'] = 'Security check failed.';
    } else {
        $password = isset($_POST['password']) ? (string) $_POST['password'] : '';
        if ($state['mode'] === 'restore') {
            $state = handle_restore($state, $password, $RECORDS_FILE, $options);
        } else {
            $state = handle_protect($state, $password, $RECORDS_FILE, $options);
        }
    }
}

$records = load_records($RECORDS_FILE);
$stats = array(
    'input_chars' => strlen($state['source']),
    'output_chars' => strlen($state['output']),
    'mapping_chars' => strlen($state['mapping']),
);

function handle_protect($state, $password, $recordsFile, $options) {
    if ($state['source'] === '') {
        $state['error'] = 'Paste your LSL source first.';
        $state['active_tab'] = 'source';
        return $state;
    }

    if (strlen($password) < 6) {
        $state['error'] = 'Use a password with at least 6 characters.';
        $state['active_tab'] = 'source';
        return $state;
    }

    $label = $state['label'] !== '' ? $state['label'] : 'Untitled Script';
    $result = transform_lsl($state['source'], $options);
    $cipher = encrypt_payload(array(
        'original' => $state['source'],
        'mapping' => $result['mapping'],
        'options' => $options,
    ), $password);

    if (isset($cipher['error'])) {
        $state['error'] = $cipher['error'];
        $state['active_tab'] = 'source';
        return $state;
    }

    $records = load_records($recordsFile);
    $nextId = 1;
    foreach ($records as $record) {
        $id = isset($record['id']) ? (int) $record['id'] : 0;
        if ($id >= $nextId) {
            $nextId = $id + 1;
        }
    }

    $records[] = array(
        'id' => $nextId,
        'label' => $label,
        'created_at' => date('Y-m-d H:i:s'),
        'ciphertext' => $cipher['ciphertext'],
        'salt' => $cipher['salt'],
        'iv' => $cipher['iv'],
        'algo' => $cipher['algo'],
    );

    if (!save_records($recordsFile, $records)) {
        $state['error'] = 'Could not save encrypted original.';
        $state['active_tab'] = 'source';
        return $state;
    }

    $state['output'] = $result['output'];
    $state['mapping'] = !empty($options['show_mapping']) ? json_encode($result['mapping'], JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES) : '';
    $state['record_id'] = (string) $nextId;
    $state['message'] = 'Protected copy created. Original saved as record #' . $nextId . '.';
    $state['active_tab'] = 'output';
    return $state;
}

function handle_restore($state, $password, $recordsFile, $options) {
    $recordId = (int) $state['record_id'];
    if ($recordId <= 0) {
        $state['error'] = 'Choose a saved item first.';
        $state['active_tab'] = 'restore';
        return $state;
    }

    if ($password === '') {
        $state['error'] = 'Enter the password used when the original was saved.';
        $state['active_tab'] = 'restore';
        return $state;
    }

    $records = load_records($recordsFile);
    $record = null;
    foreach ($records as $item) {
        if ((int) $item['id'] === $recordId) {
            $record = $item;
            break;
        }
    }

    if (!$record) {
        $state['error'] = 'Saved item not found.';
        $state['active_tab'] = 'restore';
        return $state;
    }

    $decrypted = decrypt_payload($record, $password);
    if (isset($decrypted['error'])) {
        $state['error'] = $decrypted['error'];
        $state['active_tab'] = 'restore';
        return $state;
    }

    $state['output'] = isset($decrypted['original']) ? (string) $decrypted['original'] : '';
    $state['mapping'] = !empty($decrypted['mapping']) ? json_encode($decrypted['mapping'], JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES) : '';
    $state['label'] = isset($record['label']) ? (string) $record['label'] : '';
    $state['message'] = 'Original restored for record #' . $recordId . '.';
    $state['active_tab'] = 'output';
    return $state;
}

function load_records($file) {
    if (!file_exists($file)) {
        return array();
    }
    $raw = @file_get_contents($file);
    if (!is_string($raw) || $raw === '') {
        return array();
    }
    $decoded = json_decode($raw, true);
    return is_array($decoded) ? $decoded : array();
}

function save_records($file, $records) {
    $json = json_encode($records, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES);
    if (!is_string($json)) {
        return false;
    }
    return @file_put_contents($file, $json, LOCK_EX) !== false;
}

function random_bytes_safe($length) {
    if (function_exists('random_bytes')) {
        return random_bytes($length);
    }
    $bytes = '';
    while (strlen($bytes) < $length) {
        $bytes .= sha1(uniqid(mt_rand(), true), true);
    }
    return substr($bytes, 0, $length);
}

function encrypt_payload($payload, $password) {
    $json = json_encode($payload, JSON_UNESCAPED_SLASHES);
    if (!is_string($json)) {
        return array('error' => 'Could not serialize the original source.');
    }

    if (function_exists('sodium_crypto_secretbox') && function_exists('sodium_crypto_pwhash')) {
        $salt = random_bytes_safe(16);
        $nonce = random_bytes_safe(SODIUM_CRYPTO_SECRETBOX_NONCEBYTES);
        $key = sodium_crypto_pwhash(
            SODIUM_CRYPTO_SECRETBOX_KEYBYTES,
            $password,
            $salt,
            SODIUM_CRYPTO_PWHASH_OPSLIMIT_MODERATE,
            SODIUM_CRYPTO_PWHASH_MEMLIMIT_MODERATE
        );
        $ciphertext = sodium_crypto_secretbox($json, $nonce, $key);
        if (function_exists('sodium_memzero')) {
            sodium_memzero($key);
        }
        return array(
            'algo' => 'sodium_secretbox',
            'salt' => base64_encode($salt),
            'iv' => base64_encode($nonce),
            'ciphertext' => base64_encode($ciphertext),
        );
    }

    if (function_exists('openssl_encrypt')) {
        $salt = random_bytes_safe(16);
        $iv = random_bytes_safe(16);
        $key = hash_pbkdf2('sha256', $password, $salt, 120000, 32, true);
        $ciphertext = openssl_encrypt($json, 'aes-256-cbc', $key, OPENSSL_RAW_DATA, $iv);
        if ($ciphertext === false) {
            return array('error' => 'OpenSSL could not encrypt the original source.');
        }
        return array(
            'algo' => 'aes-256-cbc',
            'salt' => base64_encode($salt),
            'iv' => base64_encode($iv),
            'ciphertext' => base64_encode($ciphertext),
        );
    }

    return array('error' => 'No supported encryption library is available on this PHP installation.');
}

function decrypt_payload($record, $password) {
    $algo = isset($record['algo']) ? (string) $record['algo'] : '';
    $salt = isset($record['salt']) ? base64_decode((string) $record['salt'], true) : false;
    $iv = isset($record['iv']) ? base64_decode((string) $record['iv'], true) : false;
    $ciphertext = isset($record['ciphertext']) ? base64_decode((string) $record['ciphertext'], true) : false;

    if ($salt === false || $iv === false || $ciphertext === false) {
        return array('error' => 'Saved data is corrupted.');
    }

    if ($algo === 'sodium_secretbox' && function_exists('sodium_crypto_secretbox_open') && function_exists('sodium_crypto_pwhash')) {
        $key = sodium_crypto_pwhash(
            SODIUM_CRYPTO_SECRETBOX_KEYBYTES,
            $password,
            $salt,
            SODIUM_CRYPTO_PWHASH_OPSLIMIT_MODERATE,
            SODIUM_CRYPTO_PWHASH_MEMLIMIT_MODERATE
        );
        $plain = sodium_crypto_secretbox_open($ciphertext, $iv, $key);
        if (function_exists('sodium_memzero')) {
            sodium_memzero($key);
        }
        if ($plain === false) {
            return array('error' => 'Wrong password or damaged saved item.');
        }
    } elseif ($algo === 'aes-256-cbc' && function_exists('openssl_decrypt')) {
        $key = hash_pbkdf2('sha256', $password, $salt, 120000, 32, true);
        $plain = openssl_decrypt($ciphertext, 'aes-256-cbc', $key, OPENSSL_RAW_DATA, $iv);
        if ($plain === false) {
            return array('error' => 'Wrong password or damaged saved item.');
        }
    } else {
        return array('error' => 'This PHP install cannot decrypt the saved item with the available libraries.');
    }

    $payload = json_decode($plain, true);
    if (!is_array($payload)) {
        return array('error' => 'Decrypted data is invalid.');
    }
    return $payload;
}

function transform_lsl($source, $options) {
    $working = $source;

    if (!empty($options['strip_comments'])) {
        $working = normalize_statement_breaks($working);
        $working = strip_comments_lsl($working);
    }

    $mapping = array();
    if (!empty($options['rename_symbols'])) {
        $names = collect_identifiers($working);
        $mapping = build_mapping($names);
        $working = replace_identifiers_outside_strings($working, $mapping);
    }

    if (!empty($options['trim_whitespace'])) {
        $working = trim_whitespace_lsl($working);
    }

    if (!empty($options['pack_one_line'])) {
        $working = str_replace(array("\r", "\n"), '', $working);
    }

    if (!empty($options['fake_comments'])) {
        $working = inject_fake_comments($working);
    }

    return array('output' => $working, 'mapping' => $mapping);
}

function normalize_statement_breaks($src) {
    $out = '';
    $len = strlen($src);
    $inString = false;
    $parenDepth = 0;

    for ($i = 0; $i < $len; $i++) {
        $ch = $src[$i];

        if ($inString) {
            $out .= $ch;
            if ($ch === '\\' && $i + 1 < $len) {
                $out .= $src[$i + 1];
                $i++;
                continue;
            }
            if ($ch === '"') {
                $inString = false;
            }
            continue;
        }

        if ($ch === '"') {
            $inString = true;
            $out .= $ch;
            continue;
        }

        if ($ch === '(') {
            $parenDepth++;
            $out .= $ch;
            continue;
        }

        if ($ch === ')') {
            $parenDepth = max(0, $parenDepth - 1);
            $out .= $ch;
            continue;
        }

        $out .= $ch;
        if ($ch === ';' && $parenDepth === 0) {
            $next = ($i + 1 < $len) ? $src[$i + 1] : '';
            if ($next !== "\n" && $next !== "\r") {
                $out .= "\n";
            }
        }
    }

    return $out;
}

function strip_comments_lsl($src) {
    $out = '';
    $len = strlen($src);
    $i = 0;
    $inString = false;

    while ($i < $len) {
        $ch = $src[$i];
        $next = ($i + 1 < $len) ? $src[$i + 1] : '';

        if ($inString) {
            $out .= $ch;
            if ($ch === '\\' && $i + 1 < $len) {
                $out .= $src[$i + 1];
                $i += 2;
                continue;
            }
            if ($ch === '"') {
                $inString = false;
            }
            $i++;
            continue;
        }

        if ($ch === '"') {
            $inString = true;
            $out .= $ch;
            $i++;
            continue;
        }

        if ($ch === '/' && $next === '/') {
            $i += 2;
            while ($i < $len && $src[$i] !== "\n") {
                $i++;
            }
            continue;
        }

        if ($ch === '/' && $next === '*') {
            $i += 2;
            while ($i + 1 < $len && !($src[$i] === '*' && $src[$i + 1] === '/')) {
                $i++;
            }
            if ($i + 1 < $len) {
                $i += 2;
            }
            continue;
        }

        $out .= $ch;
        $i++;
    }

    return $out;
}

function trim_whitespace_lsl($src) {
    $lines = preg_split('/\R/', $src);
    $clean = array();
    if (!is_array($lines)) {
        return trim($src);
    }
    foreach ($lines as $line) {
        $line = trim($line);
        if ($line !== '') {
            $clean[] = $line;
        }
    }
    return implode("\n", $clean);
}

function inject_fake_comments($src) {
    $out = '';
    $len = strlen($src);
    $inString = false;
    $counter = 0;
    $tokens = array('/*01*/', '/*A2*/', '/*X3*/', '/*Q4*/', '/*Z5*/');

    for ($i = 0; $i < $len; $i++) {
        $ch = $src[$i];
        $out .= $ch;

        if ($inString) {
            if ($ch === '\\' && $i + 1 < $len) {
                $out .= $src[$i + 1];
                $i++;
                continue;
            }
            if ($ch === '"') {
                $inString = false;
            }
            continue;
        }

        if ($ch === '"') {
            $inString = true;
            continue;
        }

        if ($ch === ';' || $ch === '{' || $ch === '}') {
            $counter++;
            if ($counter % 2 === 0) {
                $out .= $tokens[$counter % count($tokens)];
            }
        }
    }

    return $out;
}

function collect_identifiers($src) {
    $keywords = array(
        'default' => true, 'state' => true, 'jump' => true, 'return' => true, 'if' => true, 'else' => true,
        'for' => true, 'do' => true, 'while' => true, 'TRUE' => true, 'FALSE' => true, 'NULL_KEY' => true,
        'integer' => true, 'float' => true, 'string' => true, 'key' => true, 'vector' => true,
        'rotation' => true, 'list' => true, 'quaternion' => true,
    );

    $events = array(
        'state_entry' => true, 'state_exit' => true, 'touch_start' => true, 'touch' => true,
        'touch_end' => true, 'timer' => true, 'listen' => true, 'link_message' => true,
        'on_rez' => true, 'changed' => true, 'attach' => true, 'dataserver' => true,
        'http_response' => true, 'object_rez' => true, 'sensor' => true, 'no_sensor' => true,
        'at_target' => true, 'not_at_target' => true, 'moving_start' => true, 'moving_end' => true,
        'run_time_permissions' => true, 'remote_data' => true, 'land_collision_start' => true,
        'land_collision' => true, 'land_collision_end' => true, 'collision_start' => true,
        'collision' => true, 'collision_end' => true, 'control' => true, 'money' => true,
        'email' => true, 'path_update' => true, 'transaction_result' => true,
        'experience_permissions' => true, 'experience_permissions_denied' => true,
    );

    $identifiers = array();

    if (preg_match_all('/\bstate\s+([A-Za-z_]\w*)\b/', $src, $matches)) {
        foreach ($matches[1] as $name) {
            if ($name !== 'default' && is_user_symbol_php($name, $keywords, $events)) {
                $identifiers[$name] = true;
            }
        }
    }

    $functionPattern = '/(?<!\w)(?:(integer|float|string|key|vector|rotation|list|quaternion)\s+)?([A-Za-z_]\w*)\s*\(([^;{}]*)\)\s*\{/m';
    if (preg_match_all($functionPattern, $src, $matches, PREG_SET_ORDER)) {
        foreach ($matches as $match) {
            $funcName = $match[2];
            $params = isset($match[3]) ? $match[3] : '';
            if (!isset($events[$funcName]) && is_user_symbol_php($funcName, $keywords, $events)) {
                $identifiers[$funcName] = true;
            }
            if (preg_match_all('/\b(?:integer|float|string|key|vector|rotation|list|quaternion)\s+([A-Za-z_]\w*)\b/', $params, $paramMatches)) {
                foreach ($paramMatches[1] as $paramName) {
                    if (is_user_symbol_php($paramName, $keywords, $events)) {
                        $identifiers[$paramName] = true;
                    }
                }
            }
        }
    }

    $varPattern = '/(?<!\w)(integer|float|string|key|vector|rotation|list|quaternion)\s+([^;{}()]+);/';
    if (preg_match_all($varPattern, $src, $matches, PREG_SET_ORDER)) {
        foreach ($matches as $match) {
            $declared = $match[2];
            foreach (collect_declared_names_from_statement($declared, $keywords, $events) as $name) {
                $identifiers[$name] = true;
            }
        }
    }

    if (preg_match_all('/\bfor\s*\(\s*([A-Za-z_]\w*)\s*=/', $src, $matches)) {
        foreach ($matches[1] as $name) {
            if (is_user_symbol_php($name, $keywords, $events)) {
                $identifiers[$name] = true;
            }
        }
    }

    $names = array_keys($identifiers);
    usort($names, function ($a, $b) {
        $len = strlen($b) - strlen($a);
        return $len !== 0 ? $len : strcmp($a, $b);
    });
    return $names;
}

function is_user_symbol_php($name, $keywords, $events) {
    if ($name === '') {
        return false;
    }
    if (isset($keywords[$name]) || isset($events[$name])) {
        return false;
    }
    if (strpos($name, 'll') === 0 || strpos($name, 'os') === 0) {
        return false;
    }
    return true;
}

function collect_declared_names_from_statement($statement, $keywords, $events) {
    $out = array();
    foreach (split_top_level_commas_php($statement) as $piece) {
        $piece = trim($piece);
        if ($piece === '') {
            continue;
        }
        $part = explode('=', $piece, 2);
        $part = trim($part[0]);
        if (preg_match('/^([A-Za-z_]\w*)$/', $part, $match)) {
            $name = $match[1];
            if (is_user_symbol_php($name, $keywords, $events)) {
                $out[] = $name;
            }
        }
    }
    return $out;
}

function split_top_level_commas_php($text) {
    $parts = array();
    $current = '';
    $depth = 0;
    $inString = false;
    $len = strlen($text);

    for ($i = 0; $i < $len; $i++) {
        $ch = $text[$i];
        if ($inString) {
            $current .= $ch;
            if ($ch === '\\' && $i + 1 < $len) {
                $current .= $text[$i + 1];
                $i++;
                continue;
            }
            if ($ch === '"') {
                $inString = false;
            }
            continue;
        }

        if ($ch === '"') {
            $inString = true;
            $current .= $ch;
            continue;
        }

        if (strpos('([<{', $ch) !== false) {
            $depth++;
        } elseif (strpos(')]>}', $ch) !== false) {
            $depth = max(0, $depth - 1);
        }

        if ($ch === ',' && $depth === 0) {
            $parts[] = $current;
            $current = '';
            continue;
        }

        $current .= $ch;
    }

    if ($current !== '') {
        $parts[] = $current;
    }
    return $parts;
}

function build_mapping($names) {
    $mapping = array();
    $index = 1;
    foreach ($names as $name) {
        $mapping[$name] = '_' . str_pad((string) $index, 6, '0', STR_PAD_LEFT);
        $index++;
    }
    return $mapping;
}

function replace_identifiers_outside_strings($src, $mapping) {
    $out = '';
    $len = strlen($src);
    $i = 0;
    $inString = false;

    while ($i < $len) {
        $ch = $src[$i];

        if ($inString) {
            $out .= $ch;
            if ($ch === '\\' && $i + 1 < $len) {
                $out .= $src[$i + 1];
                $i += 2;
                continue;
            }
            if ($ch === '"') {
                $inString = false;
            }
            $i++;
            continue;
        }

        if ($ch === '"') {
            $inString = true;
            $out .= $ch;
            $i++;
            continue;
        }

        if (preg_match('/[A-Za-z_]/', $ch)) {
            $j = $i + 1;
            while ($j < $len && preg_match('/[A-Za-z0-9_]/', $src[$j])) {
                $j++;
            }
            $word = substr($src, $i, $j - $i);
            $out .= isset($mapping[$word]) ? $mapping[$word] : $word;
            $i = $j;
            continue;
        }

        $out .= $ch;
        $i++;
    }

    return $out;
}
?>
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title><?php echo htmlspecialchars($APP_TITLE, ENT_QUOTES, 'UTF-8'); ?></title>
  <style>
    :root {
      color-scheme: dark;
      --bg: #0f1115;
      --panel: #171a21;
      --panel-2: #1f2430;
      --panel-3: #111722;
      --text: #e9eef7;
      --muted: #a5b1c2;
      --accent: #73c2fb;
      --accent-2: #8cc9ff;
      --border: #2c3444;
      --ok: #8bd17c;
      --error: #ff9aa5;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      font-family: Inter, Arial, sans-serif;
      background: linear-gradient(180deg, #0d1016 0%, #121722 100%);
      color: var(--text);
    }
    .wrap {
      max-width: 1500px;
      margin: 0 auto;
      padding: 24px;
    }
    .hero h1 {
      margin: 0 0 8px;
      font-size: 30px;
    }
    .hero p {
      margin: 0 0 18px;
      color: var(--muted);
      line-height: 1.5;
      max-width: 980px;
    }
    .panel {
      background: rgba(23, 26, 33, 0.97);
      border: 1px solid var(--border);
      border-radius: 18px;
      padding: 18px;
      box-shadow: 0 20px 60px rgba(0, 0, 0, 0.25);
    }
    .panel h2, .panel h3 {
      margin: 0 0 10px;
      font-size: 18px;
    }
    .muted {
      color: var(--muted);
      line-height: 1.5;
      font-size: 13px;
    }
    .alert {
      border-radius: 12px;
      padding: 12px 14px;
      margin-bottom: 12px;
    }
    .alert.ok {
      background: #12321f;
      color: #b5f1c2;
      border: 1px solid #26563a;
    }
    .alert.error {
      background: #3a1418;
      color: #ffd0d6;
      border: 1px solid #7b2933;
    }
    .tabs {
      display: flex;
      gap: 10px;
      flex-wrap: wrap;
      margin: 14px 0 18px;
    }
    .tab-btn {
      cursor: pointer;
      border: 1px solid var(--border);
      border-radius: 12px;
      background: var(--panel-2);
      color: var(--text);
      padding: 10px 14px;
      font-weight: 700;
    }
    .tab-btn.active {
      background: linear-gradient(180deg, #1e90ff 0%, #1877f2 100%);
      border-color: #2a7de1;
      color: #fff;
    }
    .tab-pane {
      display: none;
    }
    .tab-pane.active {
      display: block;
    }
    .row {
      display: flex;
      gap: 12px;
      flex-wrap: wrap;
      margin-bottom: 12px;
    }
    .field {
      flex: 1 1 320px;
    }
    label {
      display: block;
      font-weight: 600;
      margin-bottom: 6px;
    }
    input[type="text"], input[type="password"], select {
      width: 100%;
      background: var(--panel-2);
      color: var(--text);
      border: 1px solid var(--border);
      border-radius: 12px;
      padding: 11px 12px;
    }
    .checks {
      display: grid;
      grid-template-columns: repeat(2, minmax(220px, 1fr));
      gap: 10px 14px;
      margin: 14px 0 16px;
    }
    .check {
      display: flex;
      gap: 10px;
      align-items: center;
      padding: 10px 12px;
      border-radius: 12px;
      border: 1px solid var(--border);
      background: var(--panel-2);
    }
    .check label {
      margin: 0;
      font-weight: 500;
    }
    .editor-shell {
      margin-top: 10px;
      border: 1px solid var(--border);
      border-radius: 14px;
      overflow: hidden;
      background: var(--panel-3);
    }
    .editor-toolbar {
      display: flex;
      justify-content: space-between;
      align-items: center;
      padding: 8px 12px;
      font-size: 12px;
      color: var(--muted);
      background: #161d2a;
      border-bottom: 1px solid var(--border);
    }
    .editor {
      height: 430px;
      width: 100%;
    }
    .editor.small {
      height: 220px;
    }
    textarea.hidden-sync {
      display: none;
    }
    .btns {
      display: flex;
      gap: 10px;
      flex-wrap: wrap;
      margin-top: 14px;
    }
    button {
      cursor: pointer;
      border: 1px solid var(--border);
      border-radius: 12px;
      background: var(--panel-2);
      color: var(--text);
      padding: 10px 14px;
      font-weight: 700;
    }
    button.primary {
      background: linear-gradient(180deg, #1e90ff 0%, #1877f2 100%);
      border-color: #2a7de1;
      color: #fff;
    }
    .stats {
      display: flex;
      gap: 10px;
      flex-wrap: wrap;
      margin-bottom: 12px;
    }
    .pill {
      background: rgba(115, 194, 251, 0.12);
      color: var(--accent-2);
      border: 1px solid rgba(115, 194, 251, 0.25);
      border-radius: 999px;
      padding: 6px 10px;
      font-size: 12px;
      font-weight: 700;
    }
    code {
      background: rgba(255,255,255,0.06);
      padding: 1px 5px;
      border-radius: 6px;
    }
    @media (max-width: 900px) {
      .checks {
        grid-template-columns: 1fr;
      }
      .editor {
        height: 300px;
      }
    }
  </style>
</head>
<body>
  <div class="wrap">
    <div class="hero">
      <h1><?php echo htmlspecialchars($APP_TITLE, ENT_QUOTES, 'UTF-8'); ?></h1>
      <p>
        Tabbed standalone PHP version. Source/options, output/mapping, and restore live in separate tabs, but they still share the same workflow.
      </p>
    </div>

    <div class="panel">
      <?php if ($state['message'] !== ''): ?>
        <div class="alert ok"><?php echo htmlspecialchars($state['message'], ENT_QUOTES, 'UTF-8'); ?></div>
      <?php endif; ?>

      <?php if ($state['error'] !== ''): ?>
        <div class="alert error"><?php echo htmlspecialchars($state['error'], ENT_QUOTES, 'UTF-8'); ?></div>
      <?php endif; ?>

      <div class="tabs">
        <button type="button" class="tab-btn" data-tab="source">Source & Options</button>
        <button type="button" class="tab-btn" data-tab="output">Output & Mapping</button>
        <button type="button" class="tab-btn" data-tab="restore">Restore</button>
      </div>

      <form id="protect-form" method="post">
        <input type="hidden" name="csrf" value="<?php echo htmlspecialchars($_SESSION['lsl_protector_csrf'], ENT_QUOTES, 'UTF-8'); ?>">
        <input type="hidden" name="action_mode" value="protect">
        <input type="hidden" id="protect_active_tab" name="active_tab" value="source">

        <div id="tab_source" class="tab-pane">
          <div class="row">
            <div class="field">
              <label for="label">Label</label>
              <input id="label" name="label" type="text" value="<?php echo htmlspecialchars($state['label'], ENT_QUOTES, 'UTF-8'); ?>" placeholder="Example: HyperGate Display Module">
            </div>
            <div class="field">
              <label for="password">Password</label>
              <input id="password" name="password" type="password" autocomplete="new-password" placeholder="Password for your restore vault">
            </div>
          </div>

          <label for="source_editor">LSL source</label>
          <div class="editor-shell">
            <div class="editor-toolbar"><span>Source</span><span>Comments should show like a real editor</span></div>
            <div id="source_editor" class="editor"></div>
          </div>
          <textarea id="source" class="hidden-sync" name="source"><?php echo htmlspecialchars($state['source'], ENT_QUOTES, 'UTF-8'); ?></textarea>

          <div class="checks">
            <div class="check"><input id="strip_comments" name="strip_comments" type="checkbox" value="1"<?php echo !empty($options['strip_comments']) ? ' checked' : ''; ?>><label for="strip_comments">Strip // and /* */ comments</label></div>
            <div class="check"><input id="rename_symbols" name="rename_symbols" type="checkbox" value="1"<?php echo !empty($options['rename_symbols']) ? ' checked' : ''; ?>><label for="rename_symbols">Rename user-defined symbols</label></div>
            <div class="check"><input id="trim_whitespace" name="trim_whitespace" type="checkbox" value="1"<?php echo !empty($options['trim_whitespace']) ? ' checked' : ''; ?>><label for="trim_whitespace">Trim whitespace</label></div>
            <div class="check"><input id="pack_one_line" name="pack_one_line" type="checkbox" value="1"<?php echo !empty($options['pack_one_line']) ? ' checked' : ''; ?>><label for="pack_one_line">Pack into one long line</label></div>
            <div class="check"><input id="fake_comments" name="fake_comments" type="checkbox" value="1"<?php echo !empty($options['fake_comments']) ? ' checked' : ''; ?>><label for="fake_comments">Add tiny fake block comments</label></div>
            <div class="check"><input id="show_mapping" name="show_mapping" type="checkbox" value="1"<?php echo !empty($options['show_mapping']) ? ' checked' : ''; ?>><label for="show_mapping">Show mapping</label></div>
          </div>

          <p class="muted">
            Best flow: paste code here, tweak options, then protect. The app jumps to the output tab when it finishes.
          </p>

          <div class="btns">
            <button class="primary" type="submit" id="protect_submit">Protect &amp; Save Original</button>
            <button type="button" id="clear_source">Clear source</button>
          </div>
        </div>
      </form>

      <div id="tab_output" class="tab-pane">
        <div class="stats">
          <div class="pill">Input: <?php echo (int) $stats['input_chars']; ?> chars</div>
          <div class="pill">Output: <?php echo (int) $stats['output_chars']; ?> chars</div>
          <div class="pill">Mapping: <?php echo (int) $stats['mapping_chars']; ?> chars</div>
        </div>

        <label for="output_editor">Transformed LSL</label>
        <div class="editor-shell">
          <div class="editor-toolbar"><span>Output</span><span>Readonly</span></div>
          <div id="output_editor" class="editor"></div>
        </div>
        <textarea id="output" class="hidden-sync"><?php echo htmlspecialchars($state['output'], ENT_QUOTES, 'UTF-8'); ?></textarea>

        <div class="btns">
          <button type="button" id="copy_output">Copy output</button>
          <button type="button" id="download_output">Download .lsl</button>
        </div>

        <label for="mapping_editor" style="margin-top:14px;">Rename mapping</label>
        <div class="editor-shell">
          <div class="editor-toolbar"><span>Mapping</span><span>Readonly</span></div>
          <div id="mapping_editor" class="editor small"></div>
        </div>
        <textarea id="mapping" class="hidden-sync"><?php echo htmlspecialchars($state['mapping'], ENT_QUOTES, 'UTF-8'); ?></textarea>

        <div class="btns">
          <button type="button" id="copy_mapping">Copy mapping</button>
          <button type="button" id="download_mapping">Download mapping.json</button>
        </div>
      </div>

      <form id="restore-form" method="post">
        <input type="hidden" name="csrf" value="<?php echo htmlspecialchars($_SESSION['lsl_protector_csrf'], ENT_QUOTES, 'UTF-8'); ?>">
        <input type="hidden" name="action_mode" value="restore">
        <input type="hidden" id="restore_active_tab" name="active_tab" value="restore">

        <div id="tab_restore" class="tab-pane">
          <p class="muted">Choose a saved item, enter the same password, and the restored code appears in the output tab.</p>

          <div class="row">
            <div class="field">
              <label for="record_id">Saved item</label>
              <select id="record_id" name="record_id">
                <option value="">Select saved item</option>
                <?php foreach ($records as $record): ?>
                  <option value="<?php echo (int) $record['id']; ?>"<?php echo ((string) $record['id'] === (string) $state['record_id']) ? ' selected' : ''; ?>>
                    <?php echo htmlspecialchars($record['label'] . ' — #' . $record['id'] . ' — ' . $record['created_at'], ENT_QUOTES, 'UTF-8'); ?>
                  </option>
                <?php endforeach; ?>
              </select>
            </div>
            <div class="field">
              <label for="restore_password">Password</label>
              <input id="restore_password" name="password" type="password" autocomplete="current-password" placeholder="Restore password">
            </div>
          </div>

          <div class="btns">
            <button type="submit" id="restore_submit">Restore Original</button>
          </div>
        </div>
      </form>
    </div>
  </div>

  <script src="https://cdnjs.cloudflare.com/ajax/libs/ace/1.32.6/ace.js"></script>
  <script src="https://cdnjs.cloudflare.com/ajax/libs/ace/1.32.6/ext-searchbox.min.js"></script>
  <script>
    function makeEditor(id, value, readOnly, mode) {
      const editor = ace.edit(id, {
        theme: 'ace/theme/monokai',
        mode: mode || 'ace/mode/c_cpp',
        value: value || '',
        readOnly: !!readOnly,
        showPrintMargin: false,
        wrap: true,
        fontSize: 13,
        useSoftTabs: true,
        tabSize: 4,
        behavioursEnabled: true,
        highlightActiveLine: !readOnly,
        highlightGutterLine: !readOnly,
      });
      editor.session.setUseWorker(false);
      return editor;
    }

    const sourceTextarea = document.getElementById('source');
    const outputTextarea = document.getElementById('output');
    const mappingTextarea = document.getElementById('mapping');

    const sourceEditor = makeEditor('source_editor', sourceTextarea.value, false, 'ace/mode/c_cpp');
    const outputEditor = makeEditor('output_editor', outputTextarea.value, true, 'ace/mode/c_cpp');
    const mappingEditor = makeEditor('mapping_editor', mappingTextarea.value, true, 'ace/mode/json');

    sourceEditor.session.on('change', function () {
      sourceTextarea.value = sourceEditor.getValue();
    });

    document.getElementById('protect-form').addEventListener('submit', function () {
      sourceTextarea.value = sourceEditor.getValue();
      document.getElementById('protect_active_tab').value = 'output';
    });

    document.getElementById('restore-form').addEventListener('submit', function () {
      document.getElementById('restore_active_tab').value = 'output';
    });

    document.getElementById('clear_source').addEventListener('click', function () {
      sourceEditor.setValue('', -1);
      sourceTextarea.value = '';
      setTab('source');
    });

    async function copyText(text) {
      try {
        await navigator.clipboard.writeText(text);
      } catch (err) {
        const temp = document.createElement('textarea');
        temp.value = text;
        document.body.appendChild(temp);
        temp.select();
        document.execCommand('copy');
        document.body.removeChild(temp);
      }
    }

    document.getElementById('copy_output').addEventListener('click', function () {
      copyText(outputEditor.getValue());
    });

    document.getElementById('copy_mapping').addEventListener('click', function () {
      copyText(mappingEditor.getValue());
    });

    function downloadText(filename, text) {
      const blob = new Blob([text], { type: 'text/plain;charset=utf-8' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = filename;
      a.click();
      URL.revokeObjectURL(url);
    }

    document.getElementById('download_output').addEventListener('click', function () {
      downloadText('protected_script.lsl', outputEditor.getValue());
    });

    document.getElementById('download_mapping').addEventListener('click', function () {
      downloadText('rename_map.json', mappingEditor.getValue());
    });

    const tabButtons = Array.from(document.querySelectorAll('.tab-btn'));
    const panes = {
      source: document.getElementById('tab_source'),
      output: document.getElementById('tab_output'),
      restore: document.getElementById('tab_restore')
    };

    function setTab(tabName) {
      const target = panes[tabName] ? tabName : 'source';
      tabButtons.forEach((btn) => {
        btn.classList.toggle('active', btn.getAttribute('data-tab') === target);
      });
      Object.keys(panes).forEach((name) => {
        panes[name].classList.toggle('active', name === target);
      });
      if (target === 'source') {
        sourceEditor.resize();
      } else if (target === 'output') {
        outputEditor.resize();
        mappingEditor.resize();
      }
      try {
        localStorage.setItem('lsl_protector_active_tab', target);
      } catch (err) {}
    }

    tabButtons.forEach((btn) => {
      btn.addEventListener('click', function () {
        setTab(btn.getAttribute('data-tab'));
      });
    });

    const serverTab = <?php echo json_encode($state['active_tab']); ?>;
    let initialTab = serverTab || 'source';
    if (!initialTab || !panes[initialTab]) {
      try {
        const saved = localStorage.getItem('lsl_protector_active_tab');
        initialTab = panes[saved] ? saved : 'source';
      } catch (err) {
        initialTab = 'source';
      }
    }
    setTab(initialTab);
  </script>
</body>
</html>
