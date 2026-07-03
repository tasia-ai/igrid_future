<?php
session_start();

$host = "i.let-us.cyou";
$user = "root";
$pass = "CHANGE_ME_DB_PASSWORD";
$dbname = "wordpress";
$robust_dbname = "robust";
$bootstrap_admin_password = "CHANGE_ME_ADMIN_PASSWORD";
$bootstrap_add_only_password = "CHANGE_ME_ADDONLY_PASSWORD";

function post_value(string $key, string $default = ""): string
{
    return isset($_POST[$key]) ? trim((string)$_POST[$key]) : $default;
}

function backup_requests_block_flag_path(): string
{
    return __DIR__ . '/backup_requests.stop';
}

function backup_requests_blocked(): bool
{
    return is_file(backup_requests_block_flag_path());
}

function backup_recent_marker_path(string $scope): string
{
    $base = rtrim(sys_get_temp_dir(), '/');
    return $base . '/hg-backup-' . sha1(strtolower(trim($scope))) . '.stamp';
}

function backup_recently_triggered(string $scope, int $cooldownSeconds, int &$retryAfter = 0): bool
{
    $retryAfter = 0;
    $path = backup_recent_marker_path($scope);
    if (!is_file($path)) {
        return false;
    }

    $mtime = @filemtime($path);
    if ($mtime === false) {
        return false;
    }

    $age = time() - (int)$mtime;
    if ($age < max(1, $cooldownSeconds)) {
        $retryAfter = max(1, $cooldownSeconds - $age);
        return true;
    }

    return false;
}

function backup_mark_triggered(string $scope): void
{
    $path = backup_recent_marker_path($scope);
    @touch($path);
}

function h(string $value): string
{
    return htmlspecialchars($value, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
}

function valid_panel_username(string $username): bool
{
    return (bool)preg_match('/^[a-z0-9_.-]{3,64}$/', $username);
}

function valid_uuid(string $uuid): bool
{
    return (bool)preg_match('/^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/', $uuid);
}

function parse_uuid_lines(string $raw): array
{
    $out = [];
    $parts = preg_split('/[\r\n,;\s]+/', trim($raw)) ?: [];
    foreach ($parts as $p) {
        $u = strtolower(trim((string)$p));
        if ($u !== '' && valid_uuid($u)) {
            $out[$u] = true;
        }
    }
    return array_keys($out);
}

function screenshot_asset_url(string $uuid): string
{
    return 'https://i.let-us.cyou/connect/assets/texture.php?uuid=' . rawurlencode($uuid);
}

function uuid_v4(): string
{
    $bytes = random_bytes(16);
    $bytes[6] = chr((ord($bytes[6]) & 0x0f) | 0x40);
    $bytes[8] = chr((ord($bytes[8]) & 0x3f) | 0x80);
    $hex = bin2hex($bytes);
    return sprintf(
        '%s-%s-%s-%s-%s',
        substr($hex, 0, 8),
        substr($hex, 8, 4),
        substr($hex, 12, 4),
        substr($hex, 16, 4),
        substr($hex, 20, 12)
    );
}

function xml_escape(string $value): string
{
    return htmlspecialchars($value, ENT_QUOTES | ENT_XML1 | ENT_SUBSTITUTE, 'UTF-8');
}

function http_post_payload(string $url, string $body, string $contentType, int $timeoutSeconds = 8): array
{
    if (!function_exists('curl_init')) {
        return [
            'ok' => false,
            'status' => 0,
            'body' => '',
            'error' => 'cURL extension not available',
        ];
    }

    $ch = curl_init($url);
    curl_setopt_array($ch, [
        CURLOPT_POST => true,
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_CONNECTTIMEOUT => 4,
        CURLOPT_TIMEOUT => $timeoutSeconds,
        CURLOPT_HTTPHEADER => [
            'Content-Type: ' . $contentType,
        ],
        CURLOPT_POSTFIELDS => $body,
    ]);

    $rawBody = curl_exec($ch);
    $error = curl_error($ch);
    $status = (int)curl_getinfo($ch, CURLINFO_HTTP_CODE);
    curl_close($ch);

    return [
        'ok' => $error === '' && $status >= 200 && $status < 300,
        'status' => $status,
        'body' => is_string($rawBody) ? $rawBody : '',
        'error' => $error,
    ];
}

function alert_response_ok(string $body): bool
{
    $xml = @simplexml_load_string($body);
    if ($xml !== false) {
        $result = '';
        if (isset($xml->Result)) {
            $result = (string)$xml->Result;
        } elseif (isset($xml->RESULT)) {
            $result = (string)$xml->RESULT;
        }

        if ($result !== '') {
            return strcasecmp($result, 'Success') === 0 || strcasecmp($result, 'True') === 0;
        }
    }

    return stripos($body, '<Result>Success</Result>') !== false || stripos($body, '<RESULT>True</RESULT>') !== false;
}

function xmlrpc_success_value(string $xmlBody): ?bool
{
    $xml = @simplexml_load_string($xmlBody);
    if ($xml === false) {
        return null;
    }

    if (isset($xml->fault)) {
        return false;
    }

    if (isset($xml->params->param->value->boolean)) {
        return ((string)$xml->params->param->value->boolean) === '1';
    }
    if (isset($xml->params->param->value->int)) {
        return ((int)$xml->params->param->value->int) !== 0;
    }
    if (isset($xml->params->param->value->i4)) {
        return ((int)$xml->params->param->value->i4) !== 0;
    }
    if (isset($xml->params->param->value->string)) {
        $value = strtolower(trim((string)$xml->params->param->value->string));
        if ($value === 'true' || $value === '1' || $value === 'ok' || $value === 'success' || $value === 'saved') {
            return true;
        }
        if ($value === 'false' || $value === '0' || $value === 'fail' || $value === 'error') {
            return false;
        }
    }

    if (!isset($xml->params->param->value->struct->member)) {
        return null;
    }

    foreach ($xml->params->param->value->struct->member as $member) {
        $memberName = strtolower(trim((string)$member->name));
        if ($memberName !== 'success' && $memberName !== 'saved' && $memberName !== 'ok') {
            continue;
        }

        $valueNode = $member->value;
        $value = '';
        if (isset($valueNode->string)) {
            $value = (string)$valueNode->string;
        } elseif (isset($valueNode->boolean)) {
            $value = (string)$valueNode->boolean;
        } else {
            $value = (string)$valueNode;
        }

        $normalized = strtoupper(trim($value));
        return $normalized === 'TRUE' || $normalized === '1';
    }

    return null;
}

function xmlrpc_fault_string(string $xmlBody): ?string
{
    $fault = xmlrpc_named_value($xmlBody, 'faultString');
    if ($fault !== null && $fault !== '') {
        return $fault;
    }

    if (preg_match('/<faultString>\s*([^<]+)\s*<\/faultString>/is', $xmlBody, $m)) {
        return trim(html_entity_decode((string)$m[1], ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8'));
    }

    return null;
}

function xmlrpc_brief_response(string $xmlBody): string
{
    $text = trim(preg_replace('/\s+/', ' ', strip_tags($xmlBody)) ?? '');
    if ($text === '') {
        return 'empty response body';
    }
    if (strlen($text) > 220) {
        return substr($text, 0, 220) . '...';
    }
    return $text;
}

function xmlrpc_build_request(string $method, array $params): string
{
    $xml = '<?xml version="1.0"?>'
        . '<methodCall><methodName>' . xml_escape($method) . '</methodName><params><param><value><struct>';

    foreach ($params as $key => $value) {
        $xml .= '<member><name>' . xml_escape((string)$key) . '</name><value><string>'
            . xml_escape((string)$value) . '</string></value></member>';
    }

    $xml .= '</struct></value></param></params></methodCall>';
    return $xml;
}

function xmlrpc_named_value(string $xmlBody, string $name): ?string
{
    $nameEscaped = preg_quote($name, '/');
    $pattern = '/<member>\s*<name>' . $nameEscaped . '<\/name>\s*<value>\s*(?:<string>)?([^<]*)(?:<\/string>)?\s*<\/value>\s*<\/member>/is';
    if (preg_match($pattern, $xmlBody, $m)) {
        return trim(html_entity_decode((string)$m[1], ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8'));
    }

    $patternBool = '/<member>\s*<name>' . $nameEscaped . '<\/name>\s*<value>\s*<boolean>([01])<\/boolean>\s*<\/value>\s*<\/member>/is';
    if (preg_match($patternBool, $xmlBody, $m)) {
        return trim((string)$m[1]);
    }

    return null;
}

function rpc_saved_flag(string $xmlBody): ?bool
{
    $v = xmlrpc_named_value($xmlBody, 'saved');
    if ($v === null) {
        return null;
    }
    if ($v === '1' || strcasecmp($v, 'true') === 0) {
        return true;
    }
    if ($v === '0' || strcasecmp($v, 'false') === 0) {
        return false;
    }
    return null;
}

function validate_archive_requester(mysqli $conn, string $uuid, string $apiKey): bool
{
    if (!valid_uuid($uuid) || $apiKey === '') {
        return false;
    }

    $stmt = $conn->prepare("SELECT enabled FROM wp_igrid_region_restart_acl WHERE opensim_uuid = ? AND api_key = ? LIMIT 1");
    if (!$stmt) {
        return false;
    }
    $stmt->bind_param("ss", $uuid, $apiKey);
    $stmt->execute();
    $row = $stmt->get_result()->fetch_assoc();
    $stmt->close();

    return $row && (int)($row['enabled'] ?? 0) === 1;
}

function infra_shell(string $command): array
{
    if (function_exists('exec')) {
        $lines = [];
        $exitCode = 0;
        exec($command . ' 2>&1', $lines, $exitCode);
        $output = implode("\n", $lines);
        if ($output !== '') {
            $output .= "\n";
        }
        return [
            'ok' => $exitCode === 0,
            'output' => $output,
            'exit_code' => $exitCode,
        ];
    }

    if (function_exists('shell_exec')) {
        $output = shell_exec($command . ' 2>&1');
        return [
            'ok' => true,
            'output' => is_string($output) ? $output : "",
            'exit_code' => null,
        ];
    }

    return ['ok' => false, 'output' => "shell execution functions are disabled\n", 'exit_code' => null];
}

function shell_quote_single(string $value): string
{
    return "'" . str_replace("'", "'\"'\"'", $value) . "'";
}

function opensim_console_arg(string $value): string
{
    $v = str_replace(["\\", "\r", "\n", "\t"], ["\\\\", '', '', ' '], $value);
    return str_replace(' ', '\\ ', $v);
}

function infra_running_sim_containers(): array
{
    $res = infra_shell("docker ps --format '{{.Names}}'");
    $out = is_string($res['output'] ?? null) ? (string)$res['output'] : '';
    $rows = preg_split('/\r\n|\r|\n/', $out) ?: [];
    $containers = [];
    foreach ($rows as $row) {
        $name = trim((string)$row);
        if ($name !== '' && strpos($name, 'opensim-sim-') === 0) {
            $containers[] = $name;
        }
    }
    return array_values(array_unique($containers));
}

function infra_norm_token(string $value): string
{
    return strtolower((string)preg_replace('/[^a-z0-9]+/', '', $value));
}

function infra_pick_sim_container_for_region(string $regionName): ?string
{
    $containers = infra_running_sim_containers();
    if (empty($containers)) {
        return null;
    }

    $targetNorm = infra_norm_token($regionName);
    foreach ($containers as $container) {
        if (!preg_match('/^opensim-sim-(.+)-\d+$/', $container, $m)) {
            continue;
        }
        $containerRegion = (string)$m[1];
        if ($containerRegion === $regionName) {
            return $container;
        }
        if (infra_norm_token($containerRegion) === $targetNorm) {
            return $container;
        }
    }
    return null;
}

function opensim_console_command(string $containerName, string $consoleCommand): array
{
    $prepInner = 'mkdir -p /home/grid/opensim/bin/exports/oar /home/grid/opensim/bin/exports/iar';
    $prep = infra_shell('docker exec ' . escapeshellarg($containerName) . ' sh -lc ' . escapeshellarg($prepInner));
    if (!$prep['ok']) {
        return ['ok' => false, 'error' => 'Failed preparing export folders', 'output' => (string)$prep['output']];
    }

    $sendInner = 'tmux send-keys -t opensim ' . shell_quote_single($consoleCommand) . ' C-m';
    $send = infra_shell('docker exec ' . escapeshellarg($containerName) . ' sh -lc ' . escapeshellarg($sendInner));
    if (!$send['ok']) {
        return ['ok' => false, 'error' => 'Failed sending command to simulator console', 'output' => (string)$send['output']];
    }

    usleep(450000);
    $capture = infra_shell('docker exec ' . escapeshellarg($containerName) . ' sh -lc ' . escapeshellarg('tmux capture-pane -pt opensim -S -80'));
    $capOut = is_string($capture['output'] ?? null) ? trim((string)$capture['output']) : '';

    if (stripos((string)$send['output'], 'can\'t find session') !== false || stripos($capOut, 'can\'t find session') !== false) {
        return ['ok' => false, 'error' => 'Simulator tmux session not found (expected: opensim)', 'output' => $capOut];
    }

    return ['ok' => true, 'error' => '', 'output' => $capOut];
}

function restart_sim_token_value(): string
{
    $simToken = trim((string)(getenv('IGRID_RESTART_TOKEN') ?: ''));
    if ($simToken === '') {
        $tokenPath = __DIR__ . '/restart_sim_token.txt';
        if (is_file($tokenPath)) {
            $simToken = trim((string)file_get_contents($tokenPath));
        }
    }
    if ($simToken === '' || $simToken === 'CHANGE_ME_RESTART_TOKEN') {
        return '';
    }
    return $simToken;
}

function restart_schedule_region(array $region, string $requestedBy, int $delaySeconds, string $reason, string $simToken): array
{
    $regionUuid = strtolower((string)($region['uuid'] ?? ''));
    $regionName = (string)($region['regionName'] ?? $regionUuid);
    $serverUri = rtrim((string)($region['serverURI'] ?? ''), '/');
    if (!valid_uuid($regionUuid) || $serverUri === '') {
        return ['ok' => false, 'status' => 0, 'body' => 'invalid region data', 'name' => $regionName, 'uuid' => $regionUuid];
    }

    $endpointUrl = $serverUri . '/tasia-ngc/restart/' . $regionUuid;
    $payload = json_encode([
        'action' => 'schedule',
        'requested_by' => $requestedBy,
        'delay_seconds' => max(0, $delaySeconds),
        'reason' => $reason,
    ]);

    $ch = curl_init($endpointUrl);
    curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
    curl_setopt($ch, CURLOPT_POST, true);
    curl_setopt($ch, CURLOPT_HTTPHEADER, [
        'Content-Type: application/json',
        'Authorization: Bearer ' . $simToken,
    ]);
    curl_setopt($ch, CURLOPT_POSTFIELDS, $payload);
    curl_setopt($ch, CURLOPT_CONNECTTIMEOUT, 4);
    curl_setopt($ch, CURLOPT_TIMEOUT, 8);
    $body = curl_exec($ch);
    $err = curl_error($ch);
    $status = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    curl_close($ch);

    if ($err !== '') {
        return ['ok' => false, 'status' => 0, 'body' => $err, 'name' => $regionName, 'uuid' => $regionUuid];
    }
    if ($status < 200 || $status >= 300) {
        return ['ok' => false, 'status' => $status, 'body' => (string)$body, 'name' => $regionName, 'uuid' => $regionUuid];
    }
    return ['ok' => true, 'status' => $status, 'body' => (string)$body, 'name' => $regionName, 'uuid' => $regionUuid];
}

function restart_cancel_region(array $region, string $requestedBy, string $reason, string $simToken): array
{
    $regionUuid = strtolower((string)($region['uuid'] ?? ''));
    $regionName = (string)($region['regionName'] ?? $regionUuid);
    $serverUri = rtrim((string)($region['serverURI'] ?? ''), '/');
    if (!valid_uuid($regionUuid) || $serverUri === '') {
        return ['ok' => false, 'status' => 0, 'body' => 'invalid region data', 'name' => $regionName, 'uuid' => $regionUuid];
    }

    $endpointUrl = $serverUri . '/tasia-ngc/restart/' . $regionUuid;
    $payload = json_encode([
        'action' => 'cancel',
        'requested_by' => $requestedBy,
        'reason' => $reason,
    ]);

    $ch = curl_init($endpointUrl);
    curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
    curl_setopt($ch, CURLOPT_POST, true);
    curl_setopt($ch, CURLOPT_HTTPHEADER, [
        'Content-Type: application/json',
        'Authorization: Bearer ' . $simToken,
    ]);
    curl_setopt($ch, CURLOPT_POSTFIELDS, $payload);
    curl_setopt($ch, CURLOPT_CONNECTTIMEOUT, 4);
    curl_setopt($ch, CURLOPT_TIMEOUT, 8);
    $body = curl_exec($ch);
    $err = curl_error($ch);
    $status = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    curl_close($ch);

    if ($err !== '') {
        return ['ok' => false, 'status' => 0, 'body' => $err, 'name' => $regionName, 'uuid' => $regionUuid];
    }
    if ($status < 200 || $status >= 300) {
        return ['ok' => false, 'status' => $status, 'body' => (string)$body, 'name' => $regionName, 'uuid' => $regionUuid];
    }
    return ['ok' => true, 'status' => $status, 'body' => (string)$body, 'name' => $regionName, 'uuid' => $regionUuid];
}

function region_endpoint_host_port(array $region): array
{
    $serverUri = trim((string)($region['serverURI'] ?? ''));
    if ($serverUri === '') {
        return ['', 0];
    }
    $parts = parse_url($serverUri);
    if (!is_array($parts) || empty($parts['host'])) {
        return ['', 0];
    }
    $host = (string)$parts['host'];
    $scheme = strtolower((string)($parts['scheme'] ?? 'http'));
    $port = isset($parts['port']) ? (int)$parts['port'] : ($scheme === 'https' ? 443 : 80);
    return [$host, $port];
}

function region_port_is_open(string $host, int $port, int $timeoutSec = 2): bool
{
    if ($host === '' || $port <= 0) {
        return false;
    }
    $errno = 0;
    $errstr = '';
    $fp = @fsockopen($host, $port, $errno, $errstr, max(1, $timeoutSec));
    if ($fp !== false) {
        fclose($fp);
        return true;
    }
    return false;
}

function archive_base_dir(): string
{
    return '/mnt/storage/grid';
}

function archive_collect_files(string $ext): array
{
    $base = archive_base_dir();
    $rows = [];
    foreach (glob($base . '/*/*.' . $ext) ?: [] as $path) {
        if (!is_file($path)) {
            continue;
        }
        $rows[] = [
            'path' => $path,
            'mtime' => (int)@filemtime($path),
            'size' => (int)@filesize($path),
            'rel' => ltrim(substr($path, strlen($base)), '/'),
        ];
    }
    usort($rows, static function (array $a, array $b): int {
        return ($b['mtime'] ?? 0) <=> ($a['mtime'] ?? 0);
    });
    return $rows;
}

function archive_is_path_allowed(string $path): bool
{
    $base = realpath(archive_base_dir());
    $real = realpath($path);
    return $base !== false && $real !== false && strpos($real, $base . DIRECTORY_SEPARATOR) === 0;
}

function archive_stream_file(string $path): void
{
    if (!is_file($path) || !is_readable($path) || !archive_is_path_allowed($path)) {
        http_response_code(404);
        echo 'File not found';
        exit;
    }

    $size = (int)filesize($path);
    $name = basename($path);
    $start = 0;
    $end = $size > 0 ? $size - 1 : 0;
    $status = 200;

    if (isset($_SERVER['HTTP_RANGE']) && preg_match('/bytes=(\d*)-(\d*)/i', (string)$_SERVER['HTTP_RANGE'], $m)) {
        if ($m[1] !== '') {
            $start = max(0, (int)$m[1]);
        }
        if ($m[2] !== '') {
            $end = min($end, (int)$m[2]);
        }
        if ($start <= $end) {
            $status = 206;
        } else {
            $start = 0;
            $end = $size > 0 ? $size - 1 : 0;
        }
    }

    http_response_code($status);
    header('Content-Type: application/octet-stream');
    header('Content-Disposition: attachment; filename="' . rawurlencode($name) . '"');
    header('Accept-Ranges: bytes');
    header('Cache-Control: private, no-store, no-cache, must-revalidate');
    header('Pragma: no-cache');

    $length = max(0, $end - $start + 1);
    if ($status === 206) {
        header('Content-Range: bytes ' . $start . '-' . $end . '/' . $size);
    }
    header('Content-Length: ' . $length);

    $fp = fopen($path, 'rb');
    if ($fp === false) {
        http_response_code(500);
        echo 'Open file failed';
        exit;
    }

    if ($start > 0) {
        fseek($fp, $start);
    }

    $remaining = $length;
    while ($remaining > 0 && !feof($fp)) {
        $chunk = fread($fp, min(1024 * 1024, $remaining));
        if ($chunk === false || $chunk === '') {
            break;
        }
        echo $chunk;
        flush();
        $remaining -= strlen($chunk);
        if (connection_aborted()) {
            break;
        }
    }
    fclose($fp);
    exit;
}

function selfservice_owns_archive_file(string $path, string $selfUuid): bool
{
    if (!valid_uuid($selfUuid)) {
        return false;
    }
    $marker = '-by-' . strtolower($selfUuid) . '-';
    return strpos(strtolower(basename($path)), $marker) !== false;
}

function selfservice_archive_access_error(string $ext, string $rel, string $path, ?array $selfAcl, mysqli $robustConn): ?string
{
    $selfUuid = strtolower((string)($selfAcl['avatar_uuid'] ?? ''));
    if (!selfservice_owns_archive_file($path, $selfUuid)) {
        return 'This archive is not owned by your self-service account.';
    }

    if ($ext === 'oar') {
        if (!$selfAcl || (int)($selfAcl['can_backup_region'] ?? 0) !== 1) {
            return 'OAR access is not allowed for this account.';
        }

        $allowedRegionUuids = parse_uuid_lines((string)($selfAcl['allowed_regions'] ?? ''));
        if (empty($allowedRegionUuids)) {
            return 'No allowed regions configured.';
        }

        $requestedRegionDir = trim((string)strtok(str_replace('\\', '/', $rel), '/'));
        if ($requestedRegionDir === '') {
            return 'Invalid OAR location.';
        }

        $allowedRegionNames = [];
        $nameStmt = $robustConn->prepare("SELECT regionName FROM regions WHERE uuid = ? LIMIT 1");
        if ($nameStmt) {
            foreach ($allowedRegionUuids as $ru) {
                $nameStmt->bind_param('s', $ru);
                $nameStmt->execute();
                $r = $nameStmt->get_result()->fetch_assoc();
                if ($r && (string)($r['regionName'] ?? '') !== '') {
                    $allowedRegionNames[] = (string)$r['regionName'];
                }
            }
            $nameStmt->close();
        }

        $ok = false;
        foreach ($allowedRegionNames as $rn) {
            if ($requestedRegionDir === $rn || infra_norm_token($requestedRegionDir) === infra_norm_token($rn)) {
                $ok = true;
                break;
            }
        }
        if (!$ok) {
            return 'This OAR is not in your allowed region list.';
        }
    }

    if ($ext === 'iar') {
        if (!$selfAcl || (int)($selfAcl['can_export_iar'] ?? 0) !== 1) {
            return 'IAR access is not allowed for this account.';
        }
        $allowedAvatars = parse_uuid_lines((string)($selfAcl['allowed_avatar_uuids'] ?? ''));
        if (empty($allowedAvatars)) {
            $allowedAvatars = [strtolower((string)$selfAcl['avatar_uuid'])];
        }
        $base = strtolower(basename($path));
        $ok = false;
        foreach ($allowedAvatars as $u) {
            if (strpos($base, strtolower($u) . '-') === 0) {
                $ok = true;
                break;
            }
        }
        if (!$ok) {
            return 'This IAR is not on your allowed avatar list.';
        }
    }

    return null;
}

function archive_delete_with_fallback(string $path, string $rel): array
{
    if (@unlink($path)) {
        return ['ok' => true, 'error' => ''];
    }

    $relNorm = str_replace('\\', '/', ltrim($rel, '/'));
    $parts = explode('/', $relNorm);
    if (count($parts) < 2) {
        return ['ok' => false, 'error' => 'Host unlink failed and archive path format is invalid.'];
    }

    $regionName = trim((string)$parts[0]);
    $base = basename($relNorm);
    if ($regionName === '' || $base === '') {
        return ['ok' => false, 'error' => 'Host unlink failed and archive path is invalid.'];
    }

    $container = infra_pick_sim_container_for_region($regionName);
    if ($container === null) {
        return ['ok' => false, 'error' => 'Host unlink failed and no matching simulator container found for region ' . $regionName . '.'];
    }

    $target = '/home/grid/opensim/bin/Regions/' . $regionName . '/oar/' . $base;
    $rmCmd = 'docker exec -u 0 ' . escapeshellarg($container) . ' sh -lc ' . escapeshellarg('rm -f -- ' . shell_quote_single($target));
    $res = infra_shell($rmCmd);
    if (!$res['ok']) {
        $out = trim((string)($res['output'] ?? ''));
        return ['ok' => false, 'error' => $out !== '' ? $out : 'Delete fallback command failed.'];
    }

    if (is_file($path)) {
        return ['ok' => false, 'error' => 'Delete command ran but file still exists.'];
    }

    return ['ok' => true, 'error' => ''];
}

function parse_services_from_sh(string $scriptPath): array
{
    if (!is_file($scriptPath)) {
        return [];
    }

    $content = file_get_contents($scriptPath);
    if (!is_string($content) || $content === '') {
        return [];
    }

    if (!preg_match('/services=\((.*?)\)/s', $content, $m)) {
        return [];
    }

    $services = [];
    if (preg_match_all('/"([^"]+)"/', $m[1], $list)) {
        foreach ($list[1] as $name) {
            $name = trim((string)$name);
            if ($name !== '') {
                $services[] = $name;
            }
        }
    }
    return array_values(array_unique($services));
}

function infra_allowed_path(string $path): bool
{
    $prefixes = [
        '/home/marty/opensim/compose.d/',
        '/home/marty/opensim/docker-compose.yml',
        '/home/marty/opensim/services2/',
        '/home/marty/opensim/regiongen/Regions/',
        '/home/marty/opensim/config-include/',
        '/home/marty/opensim/config-public/',
        '/home/marty/opensim/regiongen/config-include/',
        '/home/marty/opensim/regiongen/config-include2/',
        '/home/marty/opensim/sh.sh',
        '/home/marty/opensim/gen2.sh',
    ];

    foreach ($prefixes as $prefix) {
        if ($path === rtrim($prefix, '/')) {
            return true;
        }
        if (strpos($path, $prefix) === 0) {
            return true;
        }
    }
    return false;
}

function region_name_valid(string $name): bool
{
    return (bool)preg_match('/^[A-Za-z0-9][A-Za-z0-9_-]{1,63}$/', $name);
}

function infra_next_http_port(): int
{
    $maxPort = 2010;
    $files = glob('/home/marty/opensim/regiongen/Regions/*/Opensim.ini') ?: [];
    foreach ($files as $file) {
        $text = @file_get_contents($file);
        if (!is_string($text) || $text === '') {
            continue;
        }
        if (preg_match('/^\s*http_listener_port\s*=\s*(\d+)\s*$/mi', $text, $m)) {
            $port = (int)$m[1];
            if ($port > $maxPort) {
                $maxPort = $port;
            }
        }
    }
    return $maxPort + 1;
}

function infra_next_ssh_port(): int
{
    $maxPort = 1810;
    $genFile = '/home/marty/opensim/gen2.sh';
    if (is_file($genFile)) {
        $text = @file_get_contents($genFile);
        if (is_string($text) && $text !== '') {
            if (preg_match_all('/\.\/sshgen\.sh\s+[A-Za-z0-9_-]+\s+(\d+)/', $text, $all)) {
                foreach ($all[1] as $p) {
                    $port = (int)$p;
                    if ($port > $maxPort) {
                        $maxPort = $port;
                    }
                }
            }
        }
    }
    return $maxPort + 1;
}

function infra_pick_template_region(): string
{
    $preferred = ['Abody', 'Amber', 'Blue', 'Grid_Welcome'];
    foreach ($preferred as $name) {
        $compose = '/home/marty/opensim/compose.d/docker-compose-' . $name . '.yml';
        $regionDir = '/home/marty/opensim/regiongen/Regions/' . $name;
        if (is_file($compose) && is_dir($regionDir)) {
            return $name;
        }
    }

    $files = glob('/home/marty/opensim/compose.d/docker-compose-*.yml') ?: [];
    foreach ($files as $file) {
        $base = basename($file);
        $name = preg_replace('/^docker-compose-/', '', $base);
        $name = preg_replace('/\.yml$/', '', (string)$name);
        if (!is_string($name) || $name === '') {
            continue;
        }
        $regionDir = '/home/marty/opensim/regiongen/Regions/' . $name;
        if (is_dir($regionDir) && region_name_valid($name)) {
            return $name;
        }
    }

    return 'Abody';
}

function infra_container_for_service(string $service): string
{
    $service = trim($service);
    if ($service === 'grid-main') {
        return 'opensim-grid-main-1';
    }
    if ($service === 'gt') {
        return 'opensim-gt-1';
    }
    if (strpos($service, 'sim-') === 0) {
        return 'opensim-sim-' . substr($service, 4) . '-1';
    }
    return '';
}

function infra_console_readonly(string $service, int $lines = 120, string $session = 'opensim'): array
{
    $container = infra_container_for_service($service);
    if ($container === '') {
        return ['ok' => false, 'output' => 'Unknown service name.'];
    }

    $lines = max(20, min(500, $lines));
    return infra_console_capture_container($container, $lines, $session);
}

function infra_console_capture_container(string $container, int $lines = 120, string $session = 'opensim'): array
{
    $lines = max(20, min(500, $lines));
    $cmd = 'docker exec ' . escapeshellarg($container)
        . ' sh -lc ' . escapeshellarg('tmux capture-pane -pt ' . escapeshellarg($session) . ' -S -' . $lines);
    $res = infra_shell($cmd);
    if (!$res['ok']) {
        $out = trim((string)($res['output'] ?? ''));
        if ($out === '') {
            $out = 'Failed to read console. Container/session may be unavailable.';
        }
        return ['ok' => false, 'output' => $out];
    }

    $out = trim((string)($res['output'] ?? ''));
    if ($out === '') {
        $out = '(no console output in selected range)';
    }
    return ['ok' => true, 'output' => $out];
}

function infra_start_healthcheck_container(string $container): array
{
    $restart = infra_shell('docker restart ' . escapeshellarg($container));
    if (!$restart['ok']) {
        $out = trim((string)($restart['output'] ?? ''));
        return ['ok' => false, 'output' => ($out !== '' ? $out : 'Container restart failed.')];
    }

    $inner = 'tmux has-session -t HealthCheck 2>/dev/null || (cd /home/grid/opensim/bin && tmux new-session -d -s HealthCheck python3 python.py)';
    $res = infra_shell('docker exec ' . escapeshellarg($container) . ' sh -lc ' . escapeshellarg($inner));
    if (!$res['ok']) {
        $out = trim((string)($res['output'] ?? ''));
        return ['ok' => false, 'output' => ($out !== '' ? $out : 'HealthCheck start failed.')];
    }
    $combined = trim((string)($restart['output'] ?? '') . "\n" . (string)($res['output'] ?? ''));
    return ['ok' => true, 'output' => $combined];
}

$conn = new mysqli($host, $user, $pass, $dbname);
if ($conn->connect_error) {
    die("Database connection failed");
}

$conn->query("CREATE TABLE IF NOT EXISTS wp_igrid_panel_users (
    id INT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(64) NOT NULL UNIQUE,
    password_hash VARCHAR(255) NOT NULL,
    role ENUM('admin','add_only') NOT NULL DEFAULT 'add_only',
    enabled TINYINT(1) NOT NULL DEFAULT 1,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
)");

$conn->query("CREATE TABLE IF NOT EXISTS wp_igrid_selfservice_acl (
    id INT AUTO_INCREMENT PRIMARY KEY,
    avatar_uuid VARCHAR(36) NOT NULL UNIQUE,
    display_name VARCHAR(128) DEFAULT '',
    enabled TINYINT(1) NOT NULL DEFAULT 1,
    can_restart_region TINYINT(1) NOT NULL DEFAULT 0,
    can_backup_region TINYINT(1) NOT NULL DEFAULT 0,
    can_export_iar TINYINT(1) NOT NULL DEFAULT 0,
    can_hg_add_user TINYINT(1) NOT NULL DEFAULT 0,
    can_panel_admin TINYINT(1) NOT NULL DEFAULT 0,
    allowed_regions TEXT,
    allowed_avatar_uuids TEXT,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
)");

$self_acl_avatar_col = $conn->query("SHOW COLUMNS FROM wp_igrid_selfservice_acl LIKE 'allowed_avatar_uuids'");
if ($self_acl_avatar_col && (int)$self_acl_avatar_col->num_rows === 0) {
    $conn->query("ALTER TABLE wp_igrid_selfservice_acl ADD COLUMN allowed_avatar_uuids TEXT AFTER allowed_regions");
}
$self_acl_hg_add_col = $conn->query("SHOW COLUMNS FROM wp_igrid_selfservice_acl LIKE 'can_hg_add_user'");
if ($self_acl_hg_add_col && (int)$self_acl_hg_add_col->num_rows === 0) {
    $conn->query("ALTER TABLE wp_igrid_selfservice_acl ADD COLUMN can_hg_add_user TINYINT(1) NOT NULL DEFAULT 0 AFTER can_export_iar");
}
$self_acl_panel_admin_col = $conn->query("SHOW COLUMNS FROM wp_igrid_selfservice_acl LIKE 'can_panel_admin'");
if ($self_acl_panel_admin_col && (int)$self_acl_panel_admin_col->num_rows === 0) {
    $conn->query("ALTER TABLE wp_igrid_selfservice_acl ADD COLUMN can_panel_admin TINYINT(1) NOT NULL DEFAULT 0 AFTER can_hg_add_user");
}

$panel_user_count = $conn->query("SELECT COUNT(*) AS c FROM wp_igrid_panel_users");
if ($panel_user_count) {
    $panel_user_count_row = $panel_user_count->fetch_assoc();
    if ((int)($panel_user_count_row['c'] ?? 0) === 0) {
        $admin_hash = password_hash($bootstrap_admin_password, PASSWORD_DEFAULT);
        $add_only_hash = password_hash($bootstrap_add_only_password, PASSWORD_DEFAULT);
        $stmt = $conn->prepare("INSERT INTO wp_igrid_panel_users (username, password_hash, role, enabled) VALUES (?, ?, ?, 1)");
        $username = 'admin';
        $role = 'admin';
        $stmt->bind_param("sss", $username, $admin_hash, $role);
        $stmt->execute();
        $username = 'addonly';
        $role = 'add_only';
        $stmt->bind_param("sss", $username, $add_only_hash, $role);
        $stmt->execute();
        $stmt->close();
    }
}

if (isset($_POST['login'])) {
    $username = strtolower(post_value('username'));
    $password = post_value('password');
    $login_role = 'add_only';
    $ok = false;

    if ($username !== '' && $password !== '') {
        $stmt = $conn->prepare("SELECT username, password_hash, role, enabled FROM wp_igrid_panel_users WHERE username = ? LIMIT 1");
        $stmt->bind_param("s", $username);
        $stmt->execute();
        $stmt->bind_result($db_username, $db_password_hash, $db_role, $db_enabled);
        $row = null;
        if ($stmt->fetch()) {
            $row = [
                'username' => (string)$db_username,
                'password_hash' => (string)$db_password_hash,
                'role' => (string)$db_role,
                'enabled' => (int)$db_enabled,
            ];
        }
        $stmt->close();

        if ($row && (int)$row['enabled'] === 1 && password_verify($password, (string)$row['password_hash'])) {
            $ok = true;
            $login_role = ((string)$row['role'] === 'admin') ? 'admin' : 'add_only';
            $username = (string)$row['username'];
        }
    }

    if ($ok) {
        $_SESSION['admin_logged_in'] = true;
        $_SESSION['admin_role'] = $login_role;
        $_SESSION['admin_username'] = $username;
    } else {
        $login_error = "Invalid username or password";
    }
}

$self_login_error = '';
$self_login_info = '';

$iauth_user = isset($_SESSION['iauth_user']) && is_array($_SESSION['iauth_user']) ? $_SESSION['iauth_user'] : null;
$iauth_uuid = '';
if ($iauth_user && isset($iauth_user['uuid'])) {
    $candidate = strtolower(trim((string)$iauth_user['uuid']));
    if (valid_uuid($candidate)) {
        $iauth_uuid = $candidate;
    }
}

if ($iauth_uuid !== '') {
    $stmt = $conn->prepare("SELECT avatar_uuid, enabled, can_panel_admin FROM wp_igrid_selfservice_acl WHERE avatar_uuid = ? LIMIT 1");
    $stmt->bind_param("s", $iauth_uuid);
    $stmt->execute();
    $row = $stmt->get_result()->fetch_assoc();
    $stmt->close();

    if ($row && (int)($row['enabled'] ?? 0) === 1) {
        $_SESSION['selfservice_logged_in'] = true;
        $_SESSION['selfservice_uuid'] = $iauth_uuid;
        if ((int)($row['can_panel_admin'] ?? 0) === 1) {
            $_SESSION['admin_logged_in'] = true;
            $_SESSION['admin_role'] = 'admin';
            $_SESSION['admin_username'] = 'iauth:' . $iauth_uuid;
            $_SESSION['iauth_admin_session'] = 1;
        } elseif (!empty($_SESSION['iauth_admin_session'])) {
            unset($_SESSION['admin_logged_in']);
            unset($_SESSION['admin_role']);
            unset($_SESSION['admin_username']);
            unset($_SESSION['iauth_admin_session']);
        }
        $self_login_info = 'I-Grid authentication connected.';
    } else {
        unset($_SESSION['selfservice_logged_in']);
        unset($_SESSION['selfservice_uuid']);
        if (!empty($_SESSION['iauth_admin_session'])) {
            unset($_SESSION['admin_logged_in']);
            unset($_SESSION['admin_role']);
            unset($_SESSION['admin_username']);
            unset($_SESSION['iauth_admin_session']);
        }
        $self_login_error = 'I-Grid authenticated UUID has no enabled self-service ACL: ' . $iauth_uuid;
    }
}

if (isset($_POST['logout'])) {
    unset($_SESSION['admin_logged_in']);
    unset($_SESSION['admin_role']);
    unset($_SESSION['admin_username']);
    unset($_SESSION['iauth_admin_session']);
}

if (isset($_POST['self_logout'])) {
    unset($_SESSION['selfservice_logged_in']);
    unset($_SESSION['selfservice_uuid']);
    unset($_SESSION['iauth_pending']);
    unset($_SESSION['iauth_user']);
    unset($_SESSION['iauth_tokens']);
    unset($_SESSION['iauth_oidc_state']);
    unset($_SESSION['iauth_oidc_url']);
    unset($_SESSION['iauth_target']);
    unset($_SESSION['iauth_return_to']);
    unset($_SESSION['iauth_admin_session']);
}

if ($iauth_uuid === '') {
    unset($_SESSION['selfservice_logged_in']);
    unset($_SESSION['selfservice_uuid']);
    if (!empty($_SESSION['iauth_admin_session'])) {
        unset($_SESSION['admin_logged_in']);
        unset($_SESSION['admin_role']);
        unset($_SESSION['admin_username']);
        unset($_SESSION['iauth_admin_session']);
    }
}

$selfservice_logged_in = !empty($_SESSION['selfservice_logged_in']) && valid_uuid((string)($_SESSION['selfservice_uuid'] ?? ''));
$current_role = (isset($_SESSION['admin_role']) && (string)$_SESSION['admin_role'] === 'admin') ? 'admin' : 'add_only';
$current_user = isset($_SESSION['admin_username']) ? (string)$_SESSION['admin_username'] : '';
$is_admin = $current_role === 'admin';
$is_add_only = $current_role === 'add_only' && !$selfservice_logged_in;
$is_selfservice = $selfservice_logged_in && !$is_admin;

$system_user_uuid = strtolower(trim((string)(getenv('IGRID_SYSTEM_USER_UUID') ?: '43827618-1993-43d8-bf6b-fc966a943381')));
if (!valid_uuid($system_user_uuid)) {
    $system_user_uuid = '43827618-1993-43d8-bf6b-fc966a943381';
}
$system_user_label = trim((string)(getenv('IGRID_SYSTEM_USER_LABEL') ?: 'System User'));

$hg_actor_uuid = $system_user_uuid;
$hg_actor_label = $system_user_label;
if ($is_selfservice && valid_uuid((string)($_SESSION['selfservice_uuid'] ?? ''))) {
    $hg_actor_uuid = strtolower((string)$_SESSION['selfservice_uuid']);
    $hg_actor_label = 'self:' . $hg_actor_uuid;
} elseif (valid_uuid($iauth_uuid)) {
    $hg_actor_uuid = strtolower($iauth_uuid);
    $hg_actor_label = 'panel-iauth:' . $hg_actor_uuid;
} elseif ($current_user !== '') {
    $hg_actor_label = 'panel:' . $current_user;
}

if (!isset($_SESSION['admin_logged_in']) && !$selfservice_logged_in) {
    ?>
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>I-Grid Access Panel</title>
    <style>
        @import url('https://fonts.googleapis.com/css2?family=Inter:wght@400;600;700&display=swap');
        :root {
            --bg: #0f172a;
            --card: rgba(255,255,255,0.92);
            --text: #0f172a;
            --muted: #475569;
            --accent: #0ea5e9;
            --accent-2: #f97316;
        }
        * { box-sizing: border-box; }
        body {
            margin: 0;
            min-height: 100vh;
            font-family: Inter, sans-serif;
            color: var(--text);
            background:
                radial-gradient(circle at 10% 20%, rgba(14,165,233,.28), transparent 45%),
                radial-gradient(circle at 90% 15%, rgba(249,115,22,.22), transparent 40%),
                linear-gradient(140deg, #0b1226, #111827 45%, #0f172a);
            display: grid;
            place-items: center;
            padding: 20px;
            opacity: 0;
            transform: translateY(8px) scale(.995);
            transition: opacity .28s ease, transform .28s ease;
        }
        body.page-ready { opacity: 1; transform: translateY(0) scale(1); }
        body.page-leaving { opacity: 0; transform: translateY(-6px) scale(.995); }
        .card {
            width: min(460px, 100%);
            background: var(--card);
            border-radius: 16px;
            padding: 24px;
            box-shadow: 0 20px 50px rgba(0, 0, 0, .35);
            border: 1px solid rgba(255,255,255,.45);
            animation: cardIn .35s cubic-bezier(.2,.8,.2,1);
        }
        @keyframes cardIn {
            from { opacity: 0; transform: translateY(10px) scale(.99); }
            to { opacity: 1; transform: translateY(0) scale(1); }
        }
        h1 { margin: 0 0 6px; font-size: 1.35rem; }
        p { margin: 0 0 16px; color: var(--muted); }
        input, button {
            width: 100%;
            padding: 11px 12px;
            border-radius: 10px;
            border: 1px solid #cbd5e1;
            font: inherit;
        }
        button {
            background: linear-gradient(90deg, var(--accent), #0284c7);
            color: #fff;
            border: none;
            font-weight: 700;
            cursor: pointer;
        }
        .rowbtn { display: flex; gap: 8px; margin-bottom: 12px; }
        .rowbtn button, .rowbtn a { flex: 1 1 0; }
        .hint {
            font-size: .9rem;
            color: #64748b;
            margin-bottom: 8px;
        }
        .linkbtn {
            display: inline-block;
            text-align: center;
            padding: 11px 12px;
            border-radius: 10px;
            text-decoration: none;
            font-weight: 700;
            background: linear-gradient(90deg, #f97316, #ea580c);
            color: #fff;
        }
        .err {
            margin-top: 10px;
            color: #b91c1c;
            font-weight: 600;
        }
    </style>
</head>
<body>
    <div class="card">
        <h1>I-Grid Access Panel</h1>
        <p>Login required</p>
        <div class="rowbtn">
            <button type="button" onclick="toggleAdminLogin()">Admin Login</button>
            <a class="linkbtn" href="/i-auth/index.php?target=panel&amp;return_to=%2Fhg%2Fadmin_panel.php">Authenticate<br>with I-Grid</a>
        </div>
        <form method="post" id="adminLogin" style="display:<?php echo !empty($login_error) ? 'block' : 'none'; ?>;">
            <input type="text" name="username" required placeholder="Username" style="margin-bottom:10px;">
            <input type="password" name="password" required placeholder="Password">
            <div style="height:10px"></div>
            <button type="submit" name="login">Login</button>
        </form>
        <?php if (!empty($login_error)): ?><div class="err"><?php echo h($login_error); ?></div><?php endif; ?>

        <hr style="margin:18px 0;border:none;border-top:1px solid #cbd5e1;">
        <h1 style="font-size:1.1rem;">I-Grid Self-Service</h1>
        <p>Use "Authenticate with I-Grid" button above. Access is granted only when self-service ACL is enabled for your UUID.</p>
        <?php if (!empty($iauth_uuid)): ?>
            <div style="margin-top:10px;color:#0f766e;font-weight:600;">I-Grid identity: <?php echo h($iauth_uuid); ?></div>
        <?php endif; ?>
        <?php if (!empty($self_login_info)): ?><div style="margin-top:10px;color:#0f766e;font-weight:600;"><?php echo h($self_login_info); ?></div><?php endif; ?>
        <?php if (!empty($self_login_error)): ?><div class="err"><?php echo h($self_login_error); ?></div><?php endif; ?>
    </div>
<script>
document.addEventListener('DOMContentLoaded', function () {
    document.body.classList.add('page-ready');
});

function toggleAdminLogin() {
    var el = document.getElementById('adminLogin');
    if (!el) return;
    el.style.display = (el.style.display === 'none' || el.style.display === '') ? 'block' : 'none';
}

function animateNavigate(url) {
    document.body.classList.remove('page-ready');
    document.body.classList.add('page-leaving');
    setTimeout(function () { window.location.href = url; }, 220);
}

document.addEventListener('click', function (ev) {
    var a = ev.target.closest('a.linkbtn');
    if (!a || !a.href) return;
    ev.preventDefault();
    animateNavigate(a.href);
});

document.addEventListener('submit', function () {
    document.body.classList.remove('page-ready');
    document.body.classList.add('page-leaving');
});
</script>
</body>
</html>
    <?php
    $conn->close();
    exit();
}

$robust_conn = new mysqli($host, $user, $pass, $robust_dbname);
if ($robust_conn->connect_error) {
    $conn->close();
    die("Robust database connection failed");
}

// Ensure required tables exist.
$conn->query("CREATE TABLE IF NOT EXISTS wp_opensim_auth (
    id INT AUTO_INCREMENT PRIMARY KEY,
    uuid VARCHAR(36) NOT NULL,
    banned TINYINT(1) DEFAULT 0,
    gridname VARCHAR(255) NOT NULL DEFAULT '',
    comment TEXT DEFAULT '',
    added_by_uuid VARCHAR(36) NOT NULL DEFAULT '',
    added_by_label VARCHAR(128) NOT NULL DEFAULT '',
    added_at TIMESTAMP NULL DEFAULT CURRENT_TIMESTAMP,
    updated_by_uuid VARCHAR(36) NOT NULL DEFAULT '',
    updated_by_label VARCHAR(128) NOT NULL DEFAULT '',
    updated_at TIMESTAMP NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    ban_reason TEXT DEFAULT NULL,
    ban_date TIMESTAMP NULL,
    UNIQUE KEY uq_uuid_grid (uuid, gridname)
)");

$hg_added_by_uuid_col = $conn->query("SHOW COLUMNS FROM wp_opensim_auth LIKE 'added_by_uuid'");
if ($hg_added_by_uuid_col && (int)$hg_added_by_uuid_col->num_rows === 0) {
    $conn->query("ALTER TABLE wp_opensim_auth ADD COLUMN added_by_uuid VARCHAR(36) NOT NULL DEFAULT '' AFTER comment");
}
$hg_added_by_label_col = $conn->query("SHOW COLUMNS FROM wp_opensim_auth LIKE 'added_by_label'");
if ($hg_added_by_label_col && (int)$hg_added_by_label_col->num_rows === 0) {
    $conn->query("ALTER TABLE wp_opensim_auth ADD COLUMN added_by_label VARCHAR(128) NOT NULL DEFAULT '' AFTER added_by_uuid");
}
$hg_added_at_col = $conn->query("SHOW COLUMNS FROM wp_opensim_auth LIKE 'added_at'");
if ($hg_added_at_col && (int)$hg_added_at_col->num_rows === 0) {
    $conn->query("ALTER TABLE wp_opensim_auth ADD COLUMN added_at TIMESTAMP NULL DEFAULT CURRENT_TIMESTAMP AFTER added_by_label");
}
$hg_updated_by_uuid_col = $conn->query("SHOW COLUMNS FROM wp_opensim_auth LIKE 'updated_by_uuid'");
if ($hg_updated_by_uuid_col && (int)$hg_updated_by_uuid_col->num_rows === 0) {
    $conn->query("ALTER TABLE wp_opensim_auth ADD COLUMN updated_by_uuid VARCHAR(36) NOT NULL DEFAULT '' AFTER added_at");
}
$hg_updated_by_label_col = $conn->query("SHOW COLUMNS FROM wp_opensim_auth LIKE 'updated_by_label'");
if ($hg_updated_by_label_col && (int)$hg_updated_by_label_col->num_rows === 0) {
    $conn->query("ALTER TABLE wp_opensim_auth ADD COLUMN updated_by_label VARCHAR(128) NOT NULL DEFAULT '' AFTER updated_by_uuid");
}
$hg_updated_at_col = $conn->query("SHOW COLUMNS FROM wp_opensim_auth LIKE 'updated_at'");
if ($hg_updated_at_col && (int)$hg_updated_at_col->num_rows === 0) {
    $conn->query("ALTER TABLE wp_opensim_auth ADD COLUMN updated_at TIMESTAMP NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP AFTER updated_by_label");
}

$stmtHgBackfill = $conn->prepare("UPDATE wp_opensim_auth
    SET added_by_uuid = ?, added_by_label = ?, updated_by_uuid = IF(updated_by_uuid = '', ?, updated_by_uuid), updated_by_label = IF(updated_by_label = '', ?, updated_by_label)
    WHERE added_by_uuid = '' OR added_by_uuid IS NULL");
if ($stmtHgBackfill) {
    $stmtHgBackfill->bind_param("ssss", $system_user_uuid, $system_user_label, $system_user_uuid, $system_user_label);
    $stmtHgBackfill->execute();
    $stmtHgBackfill->close();
}

$conn->query("CREATE TABLE IF NOT EXISTS banned_grids (
    id INT AUTO_INCREMENT PRIMARY KEY,
    gridname VARCHAR(255) NOT NULL UNIQUE,
    reason TEXT DEFAULT NULL,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
)");

$conn->query("CREATE TABLE IF NOT EXISTS partners (
    id INT AUTO_INCREMENT PRIMARY KEY,
    gridname VARCHAR(255) NOT NULL UNIQUE,
    note TEXT DEFAULT NULL,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
)");

$conn->query("CREATE TABLE IF NOT EXISTS opensim_maintenance (
    id INT AUTO_INCREMENT PRIMARY KEY,
    maintenance_mode TINYINT(1) DEFAULT 0,
    maintenance_message TEXT,
    allowed_uuids TEXT,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
)");

$conn->query("CREATE TABLE IF NOT EXISTS wp_igrid_region_restart_acl (
    id INT AUTO_INCREMENT PRIMARY KEY,
    opensim_uuid VARCHAR(36) NOT NULL UNIQUE,
    display_name VARCHAR(128) DEFAULT '',
    api_key VARCHAR(128) NOT NULL,
    can_restart TINYINT(1) DEFAULT 1,
    can_cancel TINYINT(1) DEFAULT 1,
    enabled TINYINT(1) DEFAULT 1,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
)");

$conn->query("CREATE TABLE IF NOT EXISTS wp_igrid_region_restart_log (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    requested_by_uuid VARCHAR(36) DEFAULT NULL,
    auth_mode VARCHAR(32) NOT NULL,
    region_uuid VARCHAR(36) NOT NULL,
    region_name VARCHAR(128) DEFAULT '',
    action VARCHAR(16) NOT NULL,
    delay_seconds INT DEFAULT NULL,
    reason TEXT DEFAULT NULL,
    endpoint_url VARCHAR(255) NOT NULL,
    http_status INT DEFAULT NULL,
    response_body MEDIUMTEXT,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_region_uuid (region_uuid),
    INDEX idx_created_at (created_at)
)");

$conn->query("CREATE TABLE IF NOT EXISTS wp_igrid_viewer_block_rules (
    id INT AUTO_INCREMENT PRIMARY KEY,
    match_pattern VARCHAR(191) NOT NULL,
    match_mode ENUM('contains','equals','regex') NOT NULL DEFAULT 'contains',
    reason VARCHAR(255) NOT NULL DEFAULT 'Viewer version blocked by policy',
    enabled TINYINT(1) NOT NULL DEFAULT 1,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY uniq_pattern_mode (match_pattern, match_mode)
)");

$conn->query("CREATE TABLE IF NOT EXISTS wp_igrid_viewer_block_config (
    id INT AUTO_INCREMENT PRIMARY KEY,
    allowed_uuids TEXT,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
)");

$conn->query("CREATE TABLE IF NOT EXISTS wp_igrid_region_auth_overrides (
    id INT AUTO_INCREMENT PRIMARY KEY,
    region_uuid VARCHAR(36) NOT NULL UNIQUE,
    region_name VARCHAR(191) NOT NULL DEFAULT '',
    enabled TINYINT(1) NOT NULL DEFAULT 1,
    bypass_maintenance TINYINT(1) NOT NULL DEFAULT 0,
    bypass_viewer_block TINYINT(1) NOT NULL DEFAULT 0,
    public_guest_access TINYINT(1) NOT NULL DEFAULT 0,
    note VARCHAR(255) NOT NULL DEFAULT '',
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
)");

$maintenance_count = $conn->query("SELECT COUNT(*) AS c FROM opensim_maintenance")->fetch_assoc();
if ((int)$maintenance_count['c'] === 0) {
    $conn->query("INSERT INTO opensim_maintenance (maintenance_mode, maintenance_message, allowed_uuids) VALUES (0, 'System is under maintenance. Please try again later.', '')");
}

$viewer_block_cfg_count = $conn->query("SELECT COUNT(*) AS c FROM wp_igrid_viewer_block_config")->fetch_assoc();
if ((int)($viewer_block_cfg_count['c'] ?? 0) === 0) {
    $conn->query("INSERT INTO wp_igrid_viewer_block_config (allowed_uuids) VALUES ('')");
}

$conn->query("CREATE TABLE IF NOT EXISTS wp_iauth_oidc_settings (
    id TINYINT PRIMARY KEY,
    redirect_allowlist_enabled TINYINT(1) NOT NULL DEFAULT 0,
    redirect_allowlist_text TEXT,
    default_group_name VARCHAR(191) NOT NULL DEFAULT 'I-Grid Residents',
    default_role VARCHAR(16) NOT NULL DEFAULT 'user',
    admin_role_uuids TEXT,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
)");
$iauth_oidc_group_col = $conn->query("SHOW COLUMNS FROM wp_iauth_oidc_settings LIKE 'default_group_name'");
if ($iauth_oidc_group_col && (int)$iauth_oidc_group_col->num_rows === 0) {
    $conn->query("ALTER TABLE wp_iauth_oidc_settings ADD COLUMN default_group_name VARCHAR(191) NOT NULL DEFAULT 'I-Grid Residents' AFTER redirect_allowlist_text");
}
$iauth_oidc_role_col = $conn->query("SHOW COLUMNS FROM wp_iauth_oidc_settings LIKE 'default_role'");
if ($iauth_oidc_role_col && (int)$iauth_oidc_role_col->num_rows === 0) {
    $conn->query("ALTER TABLE wp_iauth_oidc_settings ADD COLUMN default_role VARCHAR(16) NOT NULL DEFAULT 'user' AFTER default_group_name");
}
$iauth_oidc_admin_uuid_col = $conn->query("SHOW COLUMNS FROM wp_iauth_oidc_settings LIKE 'admin_role_uuids'");
if ($iauth_oidc_admin_uuid_col && (int)$iauth_oidc_admin_uuid_col->num_rows === 0) {
    $conn->query("ALTER TABLE wp_iauth_oidc_settings ADD COLUMN admin_role_uuids TEXT AFTER default_role");
}
$iauth_oidc_settings_count = $conn->query("SELECT COUNT(*) AS c FROM wp_iauth_oidc_settings")->fetch_assoc();
if ((int)($iauth_oidc_settings_count['c'] ?? 0) === 0) {
    $conn->query("INSERT INTO wp_iauth_oidc_settings (id, redirect_allowlist_enabled, redirect_allowlist_text, default_group_name, default_role, admin_role_uuids) VALUES (1, 0, '', 'I-Grid Residents', 'user', '')");
}

$self_acl = null;
$infra_console_output = '';
$infra_console_service = '';
$infra_console_lines = 120;
$self_console_output = '';
$self_console_region_uuid = '';
$self_console_lines = 120;
$backup_console_output = '';
$backup_console_region_uuid = '';
$backup_console_command = '';
$backup_console_lines = 120;
$message = '';
$post_should_redirect = false;

if (isset($_SESSION['panel_flash_message'])) {
    $message = (string)$_SESSION['panel_flash_message'];
    unset($_SESSION['panel_flash_message']);
}

$self_uuid = $is_selfservice ? strtolower((string)($_SESSION['selfservice_uuid'] ?? '')) : '';
if ($self_uuid !== '' && valid_uuid($self_uuid)) {
    $stmt = $conn->prepare("SELECT avatar_uuid, display_name, enabled, can_restart_region, can_backup_region, can_export_iar, can_hg_add_user, can_panel_admin, allowed_regions, allowed_avatar_uuids
        FROM wp_igrid_selfservice_acl WHERE avatar_uuid = ? LIMIT 1");
    $stmt->bind_param("s", $self_uuid);
    $stmt->execute();
    $self_acl = $stmt->get_result()->fetch_assoc();
    $stmt->close();
    if (!$self_acl || (int)($self_acl['enabled'] ?? 0) !== 1) {
        $self_acl = null;
        unset($_SESSION['selfservice_logged_in']);
        unset($_SESSION['selfservice_uuid']);
        $is_selfservice = false;
    }
}

if (isset($_GET['download_archive'])) {
    if (!$is_admin && !$is_selfservice) {
        http_response_code(403);
        echo 'Login required';
        exit;
    }

    $ext = strtolower(trim((string)($_GET['type'] ?? '')));
    $rel = trim((string)($_GET['file'] ?? ''));
    if (($ext !== 'oar' && $ext !== 'iar') || $rel === '') {
        http_response_code(400);
        echo 'Invalid download request';
        exit;
    }

    $path = archive_base_dir() . '/' . ltrim($rel, '/');
    if (!archive_is_path_allowed($path) || !is_file($path)) {
        http_response_code(404);
        echo 'Archive file not found';
        exit;
    }
    if (strtolower((string)pathinfo($path, PATHINFO_EXTENSION)) !== $ext) {
        http_response_code(400);
        echo 'Type mismatch';
        exit;
    }

    if ($is_selfservice) {
        $err = selfservice_archive_access_error($ext, $rel, $path, $self_acl, $robust_conn);
        if ($err !== null) {
            http_response_code(403);
            echo $err;
            exit;
        }
    }

    archive_stream_file($path);
}

if (isset($_POST['action'])) {
    $action = post_value('action');
    if ($is_admin) {
        $allowed_actions = [
            'ban_user', 'unban_user', 'update_maintenance',
            'hg_add_user', 'hg_remove_user', 'hg_ban_user', 'hg_unban_user',
            'add_banned_grid', 'remove_banned_grid',
            'add_partner_grid', 'remove_partner_grid',
            'restart_acl_add', 'restart_acl_remove', 'restart_acl_update',
            'self_acl_add', 'self_acl_update', 'self_acl_remove',
            'panel_user_add', 'panel_user_update', 'panel_user_remove',
            'alerts_send_im', 'alerts_send_all_regions',
            'presence_fix_stale', 'presence_force_offline',
            'abuse_report_remove',
            'viewer_block_config_update',
            'iauth_oidc_settings_save',
            'viewer_rule_add', 'viewer_rule_update', 'viewer_rule_remove',
            'region_auth_override_save', 'region_auth_override_remove',
            'archive_save_oar', 'archive_save_iar',
            'backup_admin_region_oar', 'backup_admin_avatar_iar', 'backup_console_exec', 'backup_console_view',
            'rolling_restart_schedule', 'rolling_restart_abort', 'rolling_shutdown_execute',
            'archive_delete_file',
            'infra_services_run', 'infra_plan_add_region', 'infra_plan_remove_region',
            'infra_execute_plan', 'infra_editor_load', 'infra_editor_save', 'infra_console_view', 'infra_healthcheck_run', 'infra_healthcheck_region'
        ];
    } elseif ($is_selfservice) {
        $allowed_actions = [
            'self_region_restart', 'self_region_oar', 'self_avatar_iar', 'self_console_view', 'self_healthcheck_region', 'self_hg_add_user',
            'archive_delete_file',
        ];
    } else {
        $allowed_actions = ['hg_add_user'];
    }

    if (!in_array($action, $allowed_actions, true)) {
        $message = "You do not have permission for this action.";
        $action = '';
    }

    switch ($action) {
        case 'ban_user': {
            $uuid = post_value('uuid');
            $reason = post_value('ban_reason');
            $stmt = $conn->prepare("UPDATE wp_oslogin_auth SET banned = 1, ban_reason = ?, ban_date = NOW() WHERE uuid = ?");
            $stmt->bind_param("ss", $reason, $uuid);
            $stmt->execute();
            $stmt->close();
            $message = "Local user banned.";
            break;
        }
        case 'unban_user': {
            $uuid = post_value('uuid');
            $stmt = $conn->prepare("UPDATE wp_oslogin_auth SET banned = 0, ban_reason = NULL, ban_date = NULL WHERE uuid = ?");
            $stmt->bind_param("s", $uuid);
            $stmt->execute();
            $stmt->close();
            $message = "Local user unbanned.";
            break;
        }
        case 'update_maintenance': {
            $maintenance_mode = isset($_POST['maintenance_mode']) ? 1 : 0;
            $maintenance_message = post_value('maintenance_message');
            $allowed_uuids = post_value('allowed_uuids');
            $stmt = $conn->prepare("UPDATE opensim_maintenance SET maintenance_mode = ?, maintenance_message = ?, allowed_uuids = ? WHERE id = 1");
            $stmt->bind_param("iss", $maintenance_mode, $maintenance_message, $allowed_uuids);
            $stmt->execute();
            $stmt->close();
            $message = "Maintenance updated.";
            break;
        }
        case 'panel_user_add': {
            $username = strtolower(post_value('username'));
            $password = post_value('password');
            $role = post_value('role') === 'admin' ? 'admin' : 'add_only';
            $enabled = isset($_POST['enabled']) ? 1 : 0;

            if (!valid_panel_username($username)) {
                $message = "Invalid username. Use 3-64 chars: a-z 0-9 . _ -";
                break;
            }
            if ($password === '') {
                $message = "Password is required for new panel users.";
                break;
            }

            $password_hash = password_hash($password, PASSWORD_DEFAULT);
            $stmt = $conn->prepare("INSERT INTO wp_igrid_panel_users (username, password_hash, role, enabled) VALUES (?, ?, ?, ?)
                ON DUPLICATE KEY UPDATE password_hash = VALUES(password_hash), role = VALUES(role), enabled = VALUES(enabled)");
            $stmt->bind_param("sssi", $username, $password_hash, $role, $enabled);
            $stmt->execute();
            $stmt->close();
            $message = "Panel user saved.";
            break;
        }
        case 'panel_user_update': {
            $username = strtolower(post_value('username'));
            $password = post_value('password');
            $role = post_value('role') === 'admin' ? 'admin' : 'add_only';
            $enabled = isset($_POST['enabled']) ? 1 : 0;

            if (!valid_panel_username($username)) {
                $message = "Invalid username.";
                break;
            }
            if ($username === strtolower($current_user) && $enabled === 0) {
                $message = "You cannot disable your own active session user.";
                break;
            }

            if ($password !== '') {
                $password_hash = password_hash($password, PASSWORD_DEFAULT);
                $stmt = $conn->prepare("UPDATE wp_igrid_panel_users SET role = ?, enabled = ?, password_hash = ? WHERE username = ?");
                $stmt->bind_param("siss", $role, $enabled, $password_hash, $username);
            } else {
                $stmt = $conn->prepare("UPDATE wp_igrid_panel_users SET role = ?, enabled = ? WHERE username = ?");
                $stmt->bind_param("sis", $role, $enabled, $username);
            }
            $stmt->execute();
            $stmt->close();
            $message = "Panel user updated.";
            break;
        }
        case 'panel_user_remove': {
            $username = strtolower(post_value('username'));
            if (!valid_panel_username($username)) {
                $message = "Invalid username.";
                break;
            }
            if ($username === strtolower($current_user)) {
                $message = "You cannot remove your own active session user.";
                break;
            }

            $stmt = $conn->prepare("DELETE FROM wp_igrid_panel_users WHERE username = ?");
            $stmt->bind_param("s", $username);
            $stmt->execute();
            $stmt->close();
            $message = "Panel user removed.";
            break;
        }
        case 'hg_add_user': {
            $uuid = strtolower(post_value('uuid'));
            $gridname = strtolower(post_value('gridname'));
            $comment = post_value('comment');
            if ($uuid !== '' && $gridname !== '') {
                $stmt = $conn->prepare("INSERT INTO wp_opensim_auth (uuid, gridname, banned, comment, added_by_uuid, added_by_label, updated_by_uuid, updated_by_label)
                    VALUES (?, ?, 0, ?, ?, ?, ?, ?)
                    ON DUPLICATE KEY UPDATE
                        comment = VALUES(comment),
                        banned = 0,
                        ban_reason = NULL,
                        ban_date = NULL,
                        added_by_uuid = IF(added_by_uuid = '' OR added_by_uuid IS NULL, VALUES(added_by_uuid), added_by_uuid),
                        added_by_label = IF(added_by_label = '' OR added_by_label IS NULL, VALUES(added_by_label), added_by_label),
                        updated_by_uuid = VALUES(updated_by_uuid),
                        updated_by_label = VALUES(updated_by_label)");
                $stmt->bind_param("sssssss", $uuid, $gridname, $comment, $hg_actor_uuid, $hg_actor_label, $hg_actor_uuid, $hg_actor_label);
                $stmt->execute();
                $stmt->close();
                $message = "HG user added/allowed.";
            }
            break;
        }
        case 'hg_remove_user': {
            $uuid = strtolower(post_value('uuid'));
            $gridname = strtolower(post_value('gridname'));
            $stmt = $conn->prepare("DELETE FROM wp_opensim_auth WHERE uuid = ? AND LOWER(gridname) = ?");
            $stmt->bind_param("ss", $uuid, $gridname);
            $stmt->execute();
            $stmt->close();
            $message = "HG user removed (access revoked).";
            break;
        }
        case 'hg_ban_user': {
            $uuid = strtolower(post_value('uuid'));
            $gridname = strtolower(post_value('gridname'));
            $reason = post_value('ban_reason');
            $stmt = $conn->prepare("UPDATE wp_opensim_auth SET banned = 1, ban_reason = ?, ban_date = NOW(), updated_by_uuid = ?, updated_by_label = ? WHERE uuid = ? AND LOWER(gridname) = ?");
            $stmt->bind_param("sssss", $reason, $hg_actor_uuid, $hg_actor_label, $uuid, $gridname);
            $stmt->execute();
            $stmt->close();
            $message = "HG user banned.";
            break;
        }
        case 'hg_unban_user': {
            $uuid = strtolower(post_value('uuid'));
            $gridname = strtolower(post_value('gridname'));
            $stmt = $conn->prepare("UPDATE wp_opensim_auth SET banned = 0, ban_reason = NULL, ban_date = NULL, updated_by_uuid = ?, updated_by_label = ? WHERE uuid = ? AND LOWER(gridname) = ?");
            $stmt->bind_param("ssss", $hg_actor_uuid, $hg_actor_label, $uuid, $gridname);
            $stmt->execute();
            $stmt->close();
            $message = "HG user unbanned.";
            break;
        }
        case 'add_banned_grid': {
            $gridname = strtolower(post_value('gridname'));
            $reason = post_value('reason');
            if ($gridname !== '') {
                $stmt = $conn->prepare("INSERT INTO banned_grids (gridname, reason) VALUES (?, ?) ON DUPLICATE KEY UPDATE reason = VALUES(reason)");
                $stmt->bind_param("ss", $gridname, $reason);
                $stmt->execute();
                $stmt->close();
                $message = "Banned grid saved.";
            }
            break;
        }
        case 'remove_banned_grid': {
            $gridname = strtolower(post_value('gridname'));
            $stmt = $conn->prepare("DELETE FROM banned_grids WHERE LOWER(gridname) = ?");
            $stmt->bind_param("s", $gridname);
            $stmt->execute();
            $stmt->close();
            $message = "Banned grid removed.";
            break;
        }
        case 'add_partner_grid': {
            $gridname = strtolower(post_value('gridname'));
            $note = post_value('note');
            if ($gridname !== '') {
                $stmt = $conn->prepare("INSERT INTO partners (gridname, note) VALUES (?, ?) ON DUPLICATE KEY UPDATE note = VALUES(note)");
                $stmt->bind_param("ss", $gridname, $note);
                $stmt->execute();
                $stmt->close();
                $message = "Partner grid saved.";
            }
            break;
        }
        case 'remove_partner_grid': {
            $gridname = strtolower(post_value('gridname'));
            $stmt = $conn->prepare("DELETE FROM partners WHERE LOWER(gridname) = ?");
            $stmt->bind_param("s", $gridname);
            $stmt->execute();
            $stmt->close();
            $message = "Partner grid removed.";
            break;
        }
        case 'alerts_send_im': {
            $toUuid = strtolower(post_value('im_to_uuid'));
            $fromUuid = strtolower(post_value('im_from_uuid', '00000000-0000-0000-0000-000000000000'));
            $fromName = post_value('im_from_name', 'Grid System');
            $imMessage = post_value('im_message');

            if (!valid_uuid($toUuid)) {
                $message = "Recipient UUID is invalid.";
                break;
            }
            if ($imMessage === '') {
                $message = "IM message cannot be empty.";
                break;
            }

            if (!valid_uuid($fromUuid)) {
                $fromUuid = '00000000-0000-0000-0000-000000000000';
            }
            if ($fromName === '') {
                $fromName = 'Grid System';
            }

            $robustBase = 'http://' . $host . ':8002';
            $payload = [
                'from_agent_id' => $fromUuid,
                'to_agent_id' => $toUuid,
                'im_session_id' => uuid_v4(),
                'timestamp' => (string)time(),
                'from_agent_name' => $fromName,
                'message' => $imMessage,
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
            $successValue = xmlrpc_success_value((string)$response['body']);

            if ((string)$response['error'] !== '') {
                $message = 'IM request failed: ' . (string)$response['error'];
                break;
            }

            if ($successValue === true) {
                $message = 'IM sent successfully to ' . $toUuid . '.';
            } elseif ($successValue === false) {
                $message = 'IM was rejected by grid messaging service for ' . $toUuid . '. (User likely offline/unroutable)';
            } else {
                $message = 'IM response was unexpected. HTTP ' . (string)$response['status'] . '.';
            }
            break;
        }
        case 'alerts_send_all_regions': {
            $fromUuid = strtolower(post_value('alert_from_uuid', '00000000-0000-0000-0000-000000000000'));
            $fromName = post_value('alert_from_name', 'Grid System');
            $alertMessage = post_value('alert_message');

            if ($alertMessage === '') {
                $message = 'Alert message cannot be empty.';
                break;
            }
            if (!valid_uuid($fromUuid)) {
                $fromUuid = '00000000-0000-0000-0000-000000000000';
            }
            if ($fromName === '') {
                $fromName = 'Grid System';
            }

            $estates = $robust_conn->query("SELECT DISTINCT em.EstateID FROM estate_map em INNER JOIN regions r ON r.uuid = em.RegionID ORDER BY em.EstateID ASC");
            if (!$estates) {
                $message = 'Unable to enumerate estates for broadcast.';
                break;
            }

            $robustBase = 'http://' . $host . ':8002';
            $total = 0;
            $ok = 0;
            $failed = [];

            while ($row = $estates->fetch_assoc()) {
                $estateId = (int)($row['EstateID'] ?? 0);
                if ($estateId <= 0) {
                    continue;
                }

                $total++;
                $payload = [
                    'EstateID' => $estateId,
                    'FromID' => $fromUuid,
                    'FromName' => $fromName,
                    'Message' => $alertMessage,
                ];

                $body = json_encode($payload);
                if (!is_string($body)) {
                    $failed[] = (string)$estateId . ':json';
                    continue;
                }

                $response = http_post_payload($robustBase . '/alert', $body, 'application/json');
                $isOk = (bool)$response['ok'] && alert_response_ok((string)$response['body']);
                if ($isOk) {
                    $ok++;
                } else {
                    $failed[] = (string)$estateId . ':http' . (string)$response['status'];
                }
            }

            if ($total === 0) {
                $message = 'No estates found for broadcast.';
                break;
            }

            $message = 'Broadcast alert sent to ' . $ok . '/' . $total . ' estates.';
            if (!empty($failed)) {
                $message .= ' Failed: ' . implode(', ', array_slice($failed, 0, 8));
                if (count($failed) > 8) {
                    $message .= ' ...';
                }
            }
            break;
        }
        case 'presence_fix_stale': {
            $staleUsers = [];
            $staleStmt = $robust_conn->prepare("SELECT g.UserID
                FROM GridUser g
                LEFT JOIN Presence p ON p.UserID = g.UserID
                WHERE g.Online = 'True'
                  AND (
                    p.UserID IS NULL
                    OR p.RegionID IS NULL
                    OR p.RegionID = '00000000-0000-0000-0000-000000000000'
                  )");
            $staleStmt->execute();
            $staleStmt->bind_result($staleUserId);
            while ($staleStmt->fetch()) {
                $uuid = (string)$staleUserId;
                if (valid_uuid($uuid)) {
                    $staleUsers[] = strtolower($uuid);
                }
            }
            $staleStmt->close();

            if (empty($staleUsers)) {
                $message = 'Presence cleanup: no stale online users found.';
                break;
            }

            $fixGridStmt = $robust_conn->prepare("UPDATE GridUser SET Online = 'False', Logout = CAST(UNIX_TIMESTAMP() AS CHAR) WHERE UserID = ? AND Online = 'True'");
            $fixPresenceStmt = $robust_conn->prepare("UPDATE Presence SET RegionID = '00000000-0000-0000-0000-000000000000' WHERE UserID = ?");

            $fixed = 0;
            foreach ($staleUsers as $uuid) {
                $fixGridStmt->bind_param("s", $uuid);
                $fixGridStmt->execute();
                if ($fixGridStmt->affected_rows > 0) {
                    $fixed++;
                }

                $fixPresenceStmt->bind_param("s", $uuid);
                $fixPresenceStmt->execute();
            }

            $fixGridStmt->close();
            $fixPresenceStmt->close();
            $message = 'Presence cleanup complete. Marked ' . $fixed . ' users offline from ' . count($staleUsers) . ' stale records.';
            break;
        }
        case 'presence_force_offline': {
            $uuid = strtolower(post_value('uuid'));
            if (!valid_uuid($uuid)) {
                $message = 'Presence force-offline failed: invalid UUID.';
                break;
            }

            $fixGridStmt = $robust_conn->prepare("UPDATE GridUser SET Online = 'False', Logout = CAST(UNIX_TIMESTAMP() AS CHAR) WHERE UserID = ?");
            $fixGridStmt->bind_param("s", $uuid);
            $fixGridStmt->execute();
            $fixGridStmt->close();

            $fixPresenceStmt = $robust_conn->prepare("UPDATE Presence SET RegionID = '00000000-0000-0000-0000-000000000000' WHERE UserID = ?");
            $fixPresenceStmt->bind_param("s", $uuid);
            $fixPresenceStmt->execute();
            $fixPresenceStmt->close();

            $message = 'Forced offline for user ' . $uuid . '.';
            break;
        }
        case 'abuse_report_remove': {
            $reportId = (int)post_value('report_id', '0');
            if ($reportId <= 0) {
                $message = 'Invalid report id.';
                break;
            }

            $stmt = $conn->prepare("DELETE FROM wp_igrid_abuse_reports WHERE id = ? LIMIT 1");
            $stmt->bind_param("i", $reportId);
            $stmt->execute();
            $deleted = (int)$stmt->affected_rows;
            $stmt->close();

            if ($deleted > 0) {
                $message = 'Abuse report #' . $reportId . ' removed.';
            } else {
                $message = 'Abuse report #' . $reportId . ' not found.';
            }
            break;
        }
        case 'viewer_block_config_update': {
            $allowed_uuids = post_value('viewer_allowed_uuids');
            $stmt = $conn->prepare("UPDATE wp_igrid_viewer_block_config SET allowed_uuids = ? WHERE id = 1");
            $stmt->bind_param("s", $allowed_uuids);
            $stmt->execute();
            $stmt->close();
            $message = 'Viewer block exceptions updated.';
            break;
        }
        case 'iauth_oidc_settings_save': {
            $enabled = isset($_POST['redirect_allowlist_enabled']) ? 1 : 0;
            $allowlist = trim((string)($_POST['redirect_allowlist_text'] ?? ''));
            $defaultGroup = trim((string)($_POST['default_group_name'] ?? 'I-Grid Residents'));
            $adminRoleUuids = trim((string)($_POST['admin_role_uuids'] ?? ''));
            if ($defaultGroup === '') {
                $defaultGroup = 'I-Grid Residents';
            }
            $defaultRole = strtolower(trim((string)($_POST['default_role'] ?? 'user')));
            if ($defaultRole !== 'admin') {
                $defaultRole = 'user';
            }

            $stmt = $conn->prepare("UPDATE wp_iauth_oidc_settings SET redirect_allowlist_enabled = ?, redirect_allowlist_text = ?, default_group_name = ?, default_role = ?, admin_role_uuids = ? WHERE id = 1");
            $stmt->bind_param("issss", $enabled, $allowlist, $defaultGroup, $defaultRole, $adminRoleUuids);
            $stmt->execute();
            $stmt->close();
            $message = 'I-Auth OIDC redirect policy saved. ' . ($enabled ? 'Allowlist is ENABLED.' : 'Allowlist is DISABLED.');
            break;
        }
        case 'viewer_rule_add': {
            $pattern = trim(post_value('match_pattern'));
            $mode = strtolower(post_value('match_mode', 'contains'));
            $reason = trim(post_value('reason', 'Viewer version blocked by policy'));
            $enabled = isset($_POST['enabled']) ? 1 : 0;

            if ($pattern === '') {
                $message = 'Viewer rule pattern is required.';
                break;
            }
            if (!in_array($mode, ['contains', 'equals', 'regex'], true)) {
                $mode = 'contains';
            }
            if ($reason === '') {
                $reason = 'Viewer version blocked by policy';
            }

            $stmt = $conn->prepare("INSERT INTO wp_igrid_viewer_block_rules (match_pattern, match_mode, reason, enabled)
                VALUES (?, ?, ?, ?)
                ON DUPLICATE KEY UPDATE reason = VALUES(reason), enabled = VALUES(enabled)");
            $stmt->bind_param("sssi", $pattern, $mode, $reason, $enabled);
            $stmt->execute();
            $stmt->close();
            $message = 'Viewer block rule saved.';
            break;
        }
        case 'viewer_rule_update': {
            $ruleId = (int)post_value('rule_id', '0');
            $pattern = trim(post_value('match_pattern'));
            $mode = strtolower(post_value('match_mode', 'contains'));
            $reason = trim(post_value('reason', 'Viewer version blocked by policy'));
            $enabled = isset($_POST['enabled']) ? 1 : 0;

            if ($ruleId <= 0 || $pattern === '') {
                $message = 'Invalid viewer rule update request.';
                break;
            }
            if (!in_array($mode, ['contains', 'equals', 'regex'], true)) {
                $mode = 'contains';
            }
            if ($reason === '') {
                $reason = 'Viewer version blocked by policy';
            }

            $stmt = $conn->prepare("UPDATE wp_igrid_viewer_block_rules
                SET match_pattern = ?, match_mode = ?, reason = ?, enabled = ?
                WHERE id = ?");
            $stmt->bind_param("sssii", $pattern, $mode, $reason, $enabled, $ruleId);
            $stmt->execute();
            $stmt->close();
            $message = 'Viewer block rule updated.';
            break;
        }
        case 'viewer_rule_remove': {
            $ruleId = (int)post_value('rule_id', '0');
            if ($ruleId <= 0) {
                $message = 'Invalid viewer rule id.';
                break;
            }

            $stmt = $conn->prepare("DELETE FROM wp_igrid_viewer_block_rules WHERE id = ? LIMIT 1");
            $stmt->bind_param("i", $ruleId);
            $stmt->execute();
            $stmt->close();
            $message = 'Viewer block rule removed.';
            break;
        }
        case 'region_auth_override_save': {
            $regionUuid = strtolower(post_value('region_uuid'));
            $regionName = trim(post_value('region_name'));
            $enabled = isset($_POST['enabled']) ? 1 : 0;
            $bypassMaintenance = isset($_POST['bypass_maintenance']) ? 1 : 0;
            $bypassViewer = isset($_POST['bypass_viewer_block']) ? 1 : 0;
            $publicGuest = isset($_POST['public_guest_access']) ? 1 : 0;
            $note = trim(post_value('note'));

            if (!valid_uuid($regionUuid)) {
                $message = 'Region override save failed: invalid region UUID.';
                break;
            }

            $stmt = $conn->prepare("INSERT INTO wp_igrid_region_auth_overrides
                (region_uuid, region_name, enabled, bypass_maintenance, bypass_viewer_block, public_guest_access, note)
                VALUES (?, ?, ?, ?, ?, ?, ?)
                ON DUPLICATE KEY UPDATE
                    region_name = VALUES(region_name),
                    enabled = VALUES(enabled),
                    bypass_maintenance = VALUES(bypass_maintenance),
                    bypass_viewer_block = VALUES(bypass_viewer_block),
                    public_guest_access = VALUES(public_guest_access),
                    note = VALUES(note)");
            $stmt->bind_param("ssiiiis", $regionUuid, $regionName, $enabled, $bypassMaintenance, $bypassViewer, $publicGuest, $note);
            $stmt->execute();
            $stmt->close();

            $message = 'Region auth override saved.';
            break;
        }
        case 'region_auth_override_remove': {
            $regionUuid = strtolower(post_value('region_uuid'));
            if (!valid_uuid($regionUuid)) {
                $message = 'Region override remove failed: invalid region UUID.';
                break;
            }

            $stmt = $conn->prepare("DELETE FROM wp_igrid_region_auth_overrides WHERE region_uuid = ? LIMIT 1");
            $stmt->bind_param("s", $regionUuid);
            $stmt->execute();
            $stmt->close();
            $message = 'Region auth override removed.';
            break;
        }
        case 'archive_save_oar': {
            $requesterUuid = strtolower(post_value('archive_requester_uuid'));
            $requesterApiKey = post_value('archive_requester_api_key');
            $regionUuid = strtolower(post_value('archive_region_uuid'));
            $filename = trim(post_value('archive_filename'));
            $perm = trim(post_value('archive_perm'));
            $noassets = isset($_POST['archive_noassets']) ? 'true' : 'false';
            $publish = isset($_POST['archive_publish']) ? 'true' : 'false';

            if (!validate_archive_requester($conn, $requesterUuid, $requesterApiKey)) {
                $message = 'Archive request denied: invalid requester UUID/API key.';
                break;
            }
            if (!valid_uuid($regionUuid)) {
                $message = 'Archive request failed: invalid region UUID.';
                $post_should_redirect = true;
                break;
            }

            if (backup_requests_blocked()) {
                $message = 'Backup requests are temporarily blocked by administrator.';
                $post_should_redirect = true;
                break;
            }

            $retryAfter = 0;
            if (backup_recently_triggered('oar-region:' . $regionUuid, 600, $retryAfter)) {
                $message = 'Backup OAR request ignored (cooldown active). Retry in about ' . $retryAfter . 's.';
                $post_should_redirect = true;
                break;
            }

            $stmt = $robust_conn->prepare("SELECT uuid, regionName, serverURI FROM regions WHERE uuid = ? LIMIT 1");
            $stmt->bind_param("s", $regionUuid);
            $stmt->execute();
            $region = $stmt->get_result()->fetch_assoc();
            $stmt->close();

            if (!$region) {
                $message = 'Archive request failed: region not found.';
                break;
            }

            if ($filename === '') {
                $safeRegion = preg_replace('/[^A-Za-z0-9_.-]+/', '_', (string)($region['regionName'] ?? 'region'));
                $filename = '/home/grid/opensim/bin/Regions/' . (string)$region['regionName'] . '/oar/' . $safeRegion . '-' . gmdate('Ymd-His') . '.oar';
            }

            $container = infra_pick_sim_container_for_region((string)$region['regionName']);
            if ($container === null) {
                $message = 'OAR export failed: no running simulator container matched region ' . (string)$region['regionName'] . '.';
                break;
            }

            $cmd = 'save oar ' . opensim_console_arg($filename);
            if ($noassets === 'true') {
                $cmd .= ' --noassets';
            }
            if ($publish === 'true') {
                $cmd .= ' --publish';
            }
            $run = opensim_console_command($container, $cmd);
            if (!$run['ok']) {
                $message = 'OAR export failed: ' . (string)$run['error'] . '. ' . (string)$run['output'];
            } else {
                backup_mark_triggered('oar-region:' . $regionUuid);
                $extra = $perm !== '' ? ' (perm ignored in console mode)' : '';
                $message = 'OAR export command sent to ' . $container . ' for region ' . (string)$region['regionName'] . '. File: ' . $filename . $extra;
            }
            $post_should_redirect = true;
            break;
        }
        case 'archive_save_iar': {
            $requesterUuid = strtolower(post_value('archive_requester_uuid'));
            $requesterApiKey = post_value('archive_requester_api_key');
            $userUuid = strtolower(post_value('archive_user_uuid'));
            $regionUuid = strtolower(post_value('archive_region_uuid'));
            $invPath = trim(post_value('archive_inv_path', '/'));
            $filename = trim(post_value('archive_filename'));
            $perm = trim(post_value('archive_perm'));
            $noassets = isset($_POST['archive_noassets']) ? 'true' : 'false';

            if (!validate_archive_requester($conn, $requesterUuid, $requesterApiKey)) {
                $message = 'Archive request denied: invalid requester UUID/API key.';
                break;
            }
            if (!valid_uuid($userUuid)) {
                $message = 'IAR request failed: invalid user UUID.';
                $post_should_redirect = true;
                break;
            }

            if (backup_requests_blocked()) {
                $message = 'Backup requests are temporarily blocked by administrator.';
                $post_should_redirect = true;
                break;
            }

            $retryAfter = 0;
            if (backup_recently_triggered('iar-avatar:' . $userUuid, 180, $retryAfter)) {
                $message = 'Backup IAR request ignored (cooldown active). Retry in about ' . $retryAfter . 's.';
                $post_should_redirect = true;
                break;
            }

            $region = null;
            if (valid_uuid($regionUuid)) {
                $stmt = $robust_conn->prepare("SELECT uuid, regionName, serverURI FROM regions WHERE uuid = ? LIMIT 1");
                $stmt->bind_param("s", $regionUuid);
                $stmt->execute();
                $region = $stmt->get_result()->fetch_assoc();
                $stmt->close();
            }
            if (!$region) {
                $region = $robust_conn->query("SELECT uuid, regionName, serverURI FROM regions ORDER BY regionName ASC LIMIT 1")->fetch_assoc();
            }
            if (!$region) {
                $message = 'IAR request failed: no region available to service request.';
                break;
            }

            if ($filename === '') {
                $filename = '/home/grid/opensim/bin/Regions/' . (string)$region['regionName'] . '/oar/' . $userUuid . '-' . gmdate('Ymd-His') . '.iar';
            }
            if ($invPath === '') {
                $invPath = '/';
            }

            $uStmt = $robust_conn->prepare("SELECT FirstName, LastName FROM UserAccounts WHERE PrincipalID = ? LIMIT 1");
            $uStmt->bind_param("s", $userUuid);
            $uStmt->execute();
            $uRow = $uStmt->get_result()->fetch_assoc();
            $uStmt->close();
            if (!$uRow) {
                $message = 'IAR request failed: user UUID not found in UserAccounts.';
                break;
            }

            $firstName = trim((string)($uRow['FirstName'] ?? ''));
            $lastName = trim((string)($uRow['LastName'] ?? ''));
            if ($firstName === '' || $lastName === '') {
                $message = 'IAR request failed: user account name is incomplete.';
                break;
            }

            $container = infra_pick_sim_container_for_region((string)$region['regionName']);
            if ($container === null) {
                $message = 'IAR export failed: no running simulator container matched region ' . (string)$region['regionName'] . '.';
                break;
            }

            $cmd = 'save iar ' . opensim_console_arg($firstName)
                . ' ' . opensim_console_arg($lastName)
                . ' ' . opensim_console_arg($invPath)
                . ' ' . opensim_console_arg($filename);
            if ($noassets === 'true') {
                $cmd .= ' --noassets';
            }

            $run = opensim_console_command($container, $cmd);
            if (!$run['ok']) {
                $message = 'IAR export failed: ' . (string)$run['error'] . '. ' . (string)$run['output'];
            } else {
                backup_mark_triggered('iar-avatar:' . $userUuid);
                $extra = $perm !== '' ? ' (perm ignored in console mode)' : '';
                $message = 'IAR export command sent to ' . $container . ' for user ' . $userUuid . '. File: ' . $filename . $extra;
            }
            $post_should_redirect = true;
            break;
        }
        case 'backup_admin_region_oar': {
            $regionUuid = strtolower(post_value('backup_region_uuid'));
            $filename = trim(post_value('backup_filename'));
            $noassets = isset($_POST['backup_noassets']) ? 'true' : 'false';
            $publish = isset($_POST['backup_publish']) ? 'true' : 'false';

            if (!valid_uuid($regionUuid)) {
                $message = 'Backup OAR failed: invalid region UUID.';
                $post_should_redirect = true;
                break;
            }

            if (backup_requests_blocked()) {
                $message = 'Backup requests are temporarily blocked by administrator.';
                $post_should_redirect = true;
                break;
            }

            $retryAfter = 0;
            if (backup_recently_triggered('oar-region:' . $regionUuid, 600, $retryAfter)) {
                $message = 'Backup OAR request ignored (cooldown active). Retry in about ' . $retryAfter . 's.';
                $post_should_redirect = true;
                break;
            }

            $stmt = $robust_conn->prepare("SELECT uuid, regionName FROM regions WHERE uuid = ? LIMIT 1");
            $stmt->bind_param("s", $regionUuid);
            $stmt->execute();
            $region = $stmt->get_result()->fetch_assoc();
            $stmt->close();
            if (!$region) {
                $message = 'Backup OAR failed: region not found.';
                break;
            }

            if ($filename === '') {
                $safeRegion = preg_replace('/[^A-Za-z0-9_.-]+/', '_', (string)$region['regionName']);
                $filename = '/home/grid/opensim/bin/Regions/' . (string)$region['regionName'] . '/oar/' . $safeRegion . '-' . gmdate('Ymd-His') . '.oar';
            }

            $container = infra_pick_sim_container_for_region((string)$region['regionName']);
            if ($container === null) {
                $message = 'Backup OAR failed: no running simulator container matched region ' . (string)$region['regionName'] . '.';
                break;
            }

            $cmd = 'save oar ' . opensim_console_arg($filename);
            if ($noassets === 'true') {
                $cmd .= ' --noassets';
            }
            if ($publish === 'true') {
                $cmd .= ' --publish';
            }
            $run = opensim_console_command($container, $cmd);
            if (!$run['ok']) {
                $message = 'Backup OAR failed: ' . (string)$run['error'] . '. ' . (string)$run['output'];
            } else {
                backup_mark_triggered('oar-region:' . $regionUuid);
                $message = 'Backup OAR command sent to ' . $container . '. File: ' . $filename;
            }
            $post_should_redirect = true;
            break;
        }
        case 'backup_admin_avatar_iar': {
            $userUuid = strtolower(post_value('backup_avatar_uuid'));
            $regionUuid = strtolower(post_value('backup_region_uuid'));
            $invPath = trim(post_value('backup_inv_path', '/'));
            $filename = trim(post_value('backup_filename'));
            $noassets = isset($_POST['backup_noassets']) ? 'true' : 'false';

            if (!valid_uuid($userUuid)) {
                $message = 'Backup IAR failed: invalid avatar UUID.';
                $post_should_redirect = true;
                break;
            }

            if (backup_requests_blocked()) {
                $message = 'Backup requests are temporarily blocked by administrator.';
                $post_should_redirect = true;
                break;
            }

            $retryAfter = 0;
            if (backup_recently_triggered('iar-avatar:' . $userUuid, 180, $retryAfter)) {
                $message = 'Backup IAR request ignored (cooldown active). Retry in about ' . $retryAfter . 's.';
                $post_should_redirect = true;
                break;
            }

            $region = null;
            if (valid_uuid($regionUuid)) {
                $stmt = $robust_conn->prepare("SELECT uuid, regionName FROM regions WHERE uuid = ? LIMIT 1");
                $stmt->bind_param("s", $regionUuid);
                $stmt->execute();
                $region = $stmt->get_result()->fetch_assoc();
                $stmt->close();
            }
            if (!$region) {
                $region = $robust_conn->query("SELECT uuid, regionName FROM regions ORDER BY regionName ASC LIMIT 1")->fetch_assoc();
            }
            if (!$region) {
                $message = 'Backup IAR failed: no region available.';
                break;
            }

            if ($invPath === '') {
                $invPath = '/';
            }
            if ($filename === '') {
                $filename = '/home/grid/opensim/bin/Regions/' . (string)$region['regionName'] . '/oar/' . $userUuid . '-' . gmdate('Ymd-His') . '.iar';
            }

            $uStmt = $robust_conn->prepare("SELECT FirstName, LastName FROM UserAccounts WHERE PrincipalID = ? LIMIT 1");
            $uStmt->bind_param("s", $userUuid);
            $uStmt->execute();
            $uRow = $uStmt->get_result()->fetch_assoc();
            $uStmt->close();
            if (!$uRow) {
                $message = 'Backup IAR failed: avatar not found in UserAccounts.';
                break;
            }
            $firstName = trim((string)($uRow['FirstName'] ?? ''));
            $lastName = trim((string)($uRow['LastName'] ?? ''));
            if ($firstName === '' || $lastName === '') {
                $message = 'Backup IAR failed: avatar name is incomplete.';
                break;
            }

            $container = infra_pick_sim_container_for_region((string)$region['regionName']);
            if ($container === null) {
                $message = 'Backup IAR failed: no running simulator container matched region ' . (string)$region['regionName'] . '.';
                break;
            }

            $cmd = 'save iar ' . opensim_console_arg($firstName)
                . ' ' . opensim_console_arg($lastName)
                . ' ' . opensim_console_arg($invPath)
                . ' ' . opensim_console_arg($filename);
            if ($noassets === 'true') {
                $cmd .= ' --noassets';
            }
            $run = opensim_console_command($container, $cmd);
            if (!$run['ok']) {
                $message = 'Backup IAR failed: ' . (string)$run['error'] . '. ' . (string)$run['output'];
            } else {
                backup_mark_triggered('iar-avatar:' . $userUuid);
                $message = 'Backup IAR command sent to ' . $container . '. File: ' . $filename;
            }
            $post_should_redirect = true;
            break;
        }
        case 'backup_console_exec': {
            $regionUuid = strtolower(post_value('backup_console_region_uuid'));
            $commandText = trim((string)($_POST['backup_console_command'] ?? ''));
            $backup_console_region_uuid = $regionUuid;
            $backup_console_command = $commandText;
            $backup_console_lines = max(20, min(500, (int)post_value('backup_console_lines', '120')));

            if (!valid_uuid($regionUuid)) {
                $message = 'Console command failed: invalid region UUID.';
                break;
            }
            if ($commandText === '') {
                $message = 'Console command failed: command is empty.';
                break;
            }

            $stmt = $robust_conn->prepare("SELECT uuid, regionName FROM regions WHERE uuid = ? LIMIT 1");
            $stmt->bind_param("s", $regionUuid);
            $stmt->execute();
            $region = $stmt->get_result()->fetch_assoc();
            $stmt->close();
            if (!$region) {
                $message = 'Console command failed: region not found.';
                break;
            }

            $container = infra_pick_sim_container_for_region((string)$region['regionName']);
            if ($container === null) {
                $message = 'Console command failed: no running simulator container matched region ' . (string)$region['regionName'] . '.';
                break;
            }

            $run = opensim_console_command($container, $commandText);
            $tail = infra_console_capture_container($container, $backup_console_lines, 'opensim');
            $backup_console_output = (string)$tail['output'];
            if (!$run['ok']) {
                $message = 'Console command failed for ' . (string)$region['regionName'] . ': ' . (string)$run['error'];
            } else {
                $message = 'Console command sent to ' . (string)$region['regionName'] . '.';
            }
            break;
        }
        case 'backup_console_view': {
            $regionUuid = strtolower(post_value('backup_console_region_uuid'));
            $backup_console_region_uuid = $regionUuid;
            $backup_console_lines = max(20, min(500, (int)post_value('backup_console_lines', '120')));

            if (!valid_uuid($regionUuid)) {
                $message = 'Console read failed: invalid region UUID.';
                break;
            }

            $stmt = $robust_conn->prepare("SELECT uuid, regionName FROM regions WHERE uuid = ? LIMIT 1");
            $stmt->bind_param("s", $regionUuid);
            $stmt->execute();
            $region = $stmt->get_result()->fetch_assoc();
            $stmt->close();
            if (!$region) {
                $message = 'Console read failed: region not found.';
                break;
            }

            $container = infra_pick_sim_container_for_region((string)$region['regionName']);
            if ($container === null) {
                $message = 'Console read failed: no running simulator container matched region ' . (string)$region['regionName'] . '.';
                break;
            }

            $tail = infra_console_capture_container($container, $backup_console_lines, 'opensim');
            $backup_console_output = (string)$tail['output'];
            if ($tail['ok']) {
                $message = 'Console snapshot loaded for ' . (string)$region['regionName'] . '.';
            } else {
                $message = 'Console snapshot failed for ' . (string)$region['regionName'] . '.';
            }
            break;
        }
        case 'archive_delete_file': {
            $ext = strtolower(trim(post_value('archive_type')));
            $rel = trim(post_value('archive_file'));
            if (($ext !== 'oar' && $ext !== 'iar') || $rel === '') {
                $message = 'Delete failed: invalid archive target.';
                break;
            }

            $path = archive_base_dir() . '/' . ltrim($rel, '/');
            if (!archive_is_path_allowed($path) || !is_file($path)) {
                $message = 'Delete failed: archive file not found.';
                break;
            }
            if (strtolower((string)pathinfo($path, PATHINFO_EXTENSION)) !== $ext) {
                $message = 'Delete failed: type mismatch.';
                break;
            }

            if ($is_selfservice) {
                $err = selfservice_archive_access_error($ext, $rel, $path, $self_acl, $robust_conn);
                if ($err !== null) {
                    $message = 'Delete failed: ' . $err;
                    break;
                }
            }

            $del = archive_delete_with_fallback($path, $rel);
            if (!$del['ok']) {
                $message = 'Delete failed: ' . (string)$del['error'];
            } else {
                $message = 'Archive file removed: ' . $rel;
            }
            break;
        }
        case 'restart_acl_add': {
            $uuid = strtolower(post_value('opensim_uuid'));
            $display = post_value('display_name');
            $apiKey = post_value('api_key');
            $canRestart = isset($_POST['can_restart']) ? 1 : 0;
            $canCancel = isset($_POST['can_cancel']) ? 1 : 0;
            $enabled = isset($_POST['enabled']) ? 1 : 0;

            if ($uuid !== '' && $apiKey !== '') {
                $stmt = $conn->prepare("INSERT INTO wp_igrid_region_restart_acl (opensim_uuid, display_name, api_key, can_restart, can_cancel, enabled)
                    VALUES (?, ?, ?, ?, ?, ?)
                    ON DUPLICATE KEY UPDATE
                        display_name = VALUES(display_name),
                        api_key = VALUES(api_key),
                        can_restart = VALUES(can_restart),
                        can_cancel = VALUES(can_cancel),
                        enabled = VALUES(enabled)");
                $stmt->bind_param("sssiii", $uuid, $display, $apiKey, $canRestart, $canCancel, $enabled);
                $stmt->execute();
                $stmt->close();
                $message = "Restart ACL entry saved.";
            }
            break;
        }
        case 'restart_acl_update': {
            $uuid = strtolower(post_value('opensim_uuid'));
            $display = post_value('display_name');
            $canRestart = isset($_POST['can_restart']) ? 1 : 0;
            $canCancel = isset($_POST['can_cancel']) ? 1 : 0;
            $enabled = isset($_POST['enabled']) ? 1 : 0;

            if ($uuid !== '') {
                if (post_value('api_key') !== '') {
                    $apiKey = post_value('api_key');
                    $stmt = $conn->prepare("UPDATE wp_igrid_region_restart_acl
                        SET display_name = ?, api_key = ?, can_restart = ?, can_cancel = ?, enabled = ?
                        WHERE opensim_uuid = ?");
                    $stmt->bind_param("ssiiis", $display, $apiKey, $canRestart, $canCancel, $enabled, $uuid);
                } else {
                    $stmt = $conn->prepare("UPDATE wp_igrid_region_restart_acl
                        SET display_name = ?, can_restart = ?, can_cancel = ?, enabled = ?
                        WHERE opensim_uuid = ?");
                    $stmt->bind_param("siiis", $display, $canRestart, $canCancel, $enabled, $uuid);
                }
                $stmt->execute();
                $stmt->close();
                $message = "Restart ACL entry updated.";
            }
            break;
        }
        case 'restart_acl_remove': {
            $uuid = strtolower(post_value('opensim_uuid'));
            $stmt = $conn->prepare("DELETE FROM wp_igrid_region_restart_acl WHERE opensim_uuid = ?");
            $stmt->bind_param("s", $uuid);
            $stmt->execute();
            $stmt->close();
            $message = "Restart ACL entry removed.";
            break;
        }
        case 'self_acl_add': {
            $uuid = strtolower(post_value('self_avatar_uuid'));
            $display = post_value('self_display_name');
            $enabled = isset($_POST['self_enabled']) ? 1 : 0;
            $canRestart = isset($_POST['self_can_restart_region']) ? 1 : 0;
            $canBackup = isset($_POST['self_can_backup_region']) ? 1 : 0;
            $canIar = isset($_POST['self_can_export_iar']) ? 1 : 0;
            $canHgAdd = isset($_POST['self_can_hg_add_user']) ? 1 : 0;
            $canPanelAdmin = isset($_POST['self_can_panel_admin']) ? 1 : 0;
            $allowedRegionsRaw = post_value('self_allowed_regions');
            $allowedAvatarsRaw = post_value('self_allowed_avatars');

            if (!valid_uuid($uuid)) {
                $message = 'Self-service ACL failed: invalid avatar UUID.';
                break;
            }

            $normalizedRegions = implode("\n", parse_uuid_lines($allowedRegionsRaw));
            $avatarList = parse_uuid_lines($allowedAvatarsRaw);
            if (empty($avatarList)) {
                $avatarList = [$uuid];
            }
            $normalizedAvatars = implode("\n", $avatarList);
            $stmt = $conn->prepare("INSERT INTO wp_igrid_selfservice_acl
                (avatar_uuid, display_name, enabled, can_restart_region, can_backup_region, can_export_iar, can_hg_add_user, can_panel_admin, allowed_regions, allowed_avatar_uuids)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                ON DUPLICATE KEY UPDATE
                    display_name = VALUES(display_name),
                    enabled = VALUES(enabled),
                    can_restart_region = VALUES(can_restart_region),
                    can_backup_region = VALUES(can_backup_region),
                    can_export_iar = VALUES(can_export_iar),
                    can_hg_add_user = VALUES(can_hg_add_user),
                    can_panel_admin = VALUES(can_panel_admin),
                    allowed_regions = VALUES(allowed_regions),
                    allowed_avatar_uuids = VALUES(allowed_avatar_uuids)");
            $stmt->bind_param("ssiiiiiiss", $uuid, $display, $enabled, $canRestart, $canBackup, $canIar, $canHgAdd, $canPanelAdmin, $normalizedRegions, $normalizedAvatars);
            $stmt->execute();
            $stmt->close();
            $message = 'Self-service ACL entry saved.';
            break;
        }
        case 'self_acl_update': {
            $uuid = strtolower(post_value('self_avatar_uuid'));
            $display = post_value('self_display_name');
            $enabled = isset($_POST['self_enabled']) ? 1 : 0;
            $canRestart = isset($_POST['self_can_restart_region']) ? 1 : 0;
            $canBackup = isset($_POST['self_can_backup_region']) ? 1 : 0;
            $canIar = isset($_POST['self_can_export_iar']) ? 1 : 0;
            $canHgAdd = isset($_POST['self_can_hg_add_user']) ? 1 : 0;
            $canPanelAdmin = isset($_POST['self_can_panel_admin']) ? 1 : 0;
            $allowedRegionsRaw = post_value('self_allowed_regions');
            $allowedAvatarsRaw = post_value('self_allowed_avatars');

            if (!valid_uuid($uuid)) {
                $message = 'Self-service ACL update failed: invalid avatar UUID.';
                break;
            }

            $normalizedRegions = implode("\n", parse_uuid_lines($allowedRegionsRaw));
            $avatarList = parse_uuid_lines($allowedAvatarsRaw);
            if (empty($avatarList)) {
                $avatarList = [$uuid];
            }
            $normalizedAvatars = implode("\n", $avatarList);
            $stmt = $conn->prepare("UPDATE wp_igrid_selfservice_acl
                SET display_name = ?, enabled = ?, can_restart_region = ?, can_backup_region = ?, can_export_iar = ?, can_hg_add_user = ?, can_panel_admin = ?, allowed_regions = ?, allowed_avatar_uuids = ?
                WHERE avatar_uuid = ?");
            $stmt->bind_param("siiiiiisss", $display, $enabled, $canRestart, $canBackup, $canIar, $canHgAdd, $canPanelAdmin, $normalizedRegions, $normalizedAvatars, $uuid);
            $stmt->execute();
            $stmt->close();
            $message = 'Self-service ACL entry updated.';
            break;
        }
        case 'self_acl_remove': {
            $uuid = strtolower(post_value('self_avatar_uuid'));
            if (!valid_uuid($uuid)) {
                $message = 'Self-service ACL remove failed: invalid avatar UUID.';
                break;
            }
            $stmt = $conn->prepare("DELETE FROM wp_igrid_selfservice_acl WHERE avatar_uuid = ?");
            $stmt->bind_param("s", $uuid);
            $stmt->execute();
            $stmt->close();
            $message = 'Self-service ACL entry removed.';
            break;
        }
        case 'self_region_restart': {
            if (!$self_acl) {
                $message = 'Self-service session is not active.';
                break;
            }
            if ((int)($self_acl['can_restart_region'] ?? 0) !== 1) {
                $message = 'Permission denied: region restart not allowed.';
                break;
            }
            $regionUuid = strtolower(post_value('self_region_uuid'));
            $delaySeconds = max(10, min(86400, (int)post_value('self_delay_seconds', '180')));
            $reason = post_value('self_reason', 'Self-service restart');
            $allowedRegions = parse_uuid_lines((string)($self_acl['allowed_regions'] ?? ''));
            if (!valid_uuid($regionUuid) || !in_array($regionUuid, $allowedRegions, true)) {
                $message = 'Permission denied: region is not in allowed list.';
                break;
            }

            $regionStmt = $robust_conn->prepare("SELECT uuid, regionName, serverURI FROM regions WHERE uuid = ? LIMIT 1");
            $regionStmt->bind_param("s", $regionUuid);
            $regionStmt->execute();
            $region = $regionStmt->get_result()->fetch_assoc();
            $regionStmt->close();
            if (!$region) {
                $message = 'Region not found.';
                break;
            }

            $simToken = trim((string)(getenv('IGRID_RESTART_TOKEN') ?: ''));
            if ($simToken === '') {
                $tokenPath = __DIR__ . '/restart_sim_token.txt';
                if (is_file($tokenPath)) {
                    $simToken = trim((string)file_get_contents($tokenPath));
                }
            }
            if ($simToken === '' || $simToken === 'CHANGE_ME_RESTART_TOKEN') {
                $message = 'Restart token is not configured.';
                break;
            }

            $endpointUrl = rtrim((string)$region['serverURI'], '/') . '/tasia-ngc/restart/' . $regionUuid;
            $payload = json_encode([
                'action' => 'schedule',
                'requested_by' => (string)$self_acl['avatar_uuid'],
                'delay_seconds' => $delaySeconds,
                'reason' => $reason,
            ]);
            $ch = curl_init($endpointUrl);
            curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
            curl_setopt($ch, CURLOPT_POST, true);
            curl_setopt($ch, CURLOPT_HTTPHEADER, [
                'Content-Type: application/json',
                'Authorization: Bearer ' . $simToken,
            ]);
            curl_setopt($ch, CURLOPT_POSTFIELDS, $payload);
            curl_setopt($ch, CURLOPT_CONNECTTIMEOUT, 4);
            curl_setopt($ch, CURLOPT_TIMEOUT, 8);
            $body = curl_exec($ch);
            $err = curl_error($ch);
            $status = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
            curl_close($ch);

            if ($err !== '') {
                $message = 'Restart request failed: ' . $err;
            } elseif ($status < 200 || $status >= 300) {
                $message = 'Restart request failed (HTTP ' . $status . '): ' . (string)$body;
            } else {
                $message = 'Restart scheduled for region ' . (string)$region['regionName'] . ' in ' . $delaySeconds . ' seconds.';
            }
            break;
        }
        case 'self_region_oar': {
            if (!$self_acl) {
                $message = 'Self-service session is not active.';
                $post_should_redirect = true;
                break;
            }
            if ((int)($self_acl['can_backup_region'] ?? 0) !== 1) {
                $message = 'Permission denied: region OAR backup not allowed.';
                $post_should_redirect = true;
                break;
            }
            $regionUuid = strtolower(post_value('self_region_uuid'));
            $allowedRegions = parse_uuid_lines((string)($self_acl['allowed_regions'] ?? ''));
            if (!valid_uuid($regionUuid) || !in_array($regionUuid, $allowedRegions, true)) {
                $message = 'Permission denied: region is not in allowed list.';
                $post_should_redirect = true;
                break;
            }

            if (backup_requests_blocked()) {
                $message = 'Backup requests are temporarily blocked by administrator.';
                $post_should_redirect = true;
                break;
            }

            $retryAfter = 0;
            if (backup_recently_triggered('oar-region:' . $regionUuid, 600, $retryAfter)) {
                $message = 'Backup OAR request ignored (cooldown active). Retry in about ' . $retryAfter . 's.';
                $post_should_redirect = true;
                break;
            }

            $stmt = $robust_conn->prepare("SELECT uuid, regionName, serverURI FROM regions WHERE uuid = ? LIMIT 1");
            $stmt->bind_param("s", $regionUuid);
            $stmt->execute();
            $region = $stmt->get_result()->fetch_assoc();
            $stmt->close();
            if (!$region) {
                $message = 'Region not found.';
                break;
            }

            $ownerUuid = strtolower((string)$self_acl['avatar_uuid']);
            $filename = '/home/grid/opensim/bin/Regions/' . (string)$region['regionName'] . '/oar/'
                . preg_replace('/[^A-Za-z0-9_.-]+/', '_', (string)$region['regionName'])
                . '-by-' . $ownerUuid . '-' . gmdate('Ymd-His') . '.oar';

            $container = infra_pick_sim_container_for_region((string)$region['regionName']);
            if ($container === null) {
                $message = 'OAR export failed: no running simulator container matched region ' . (string)$region['regionName'] . '.';
                break;
            }

            $cmd = 'save oar ' . opensim_console_arg($filename);
            if (isset($_POST['self_publish'])) {
                $cmd .= ' --publish';
            }
            $run = opensim_console_command($container, $cmd);
            if (!$run['ok']) {
                $message = 'OAR export failed: ' . (string)$run['error'] . '. ' . (string)$run['output'];
            } else {
                backup_mark_triggered('oar-region:' . $regionUuid);
                $message = 'OAR export command sent to ' . $container . ' for region ' . (string)$region['regionName'] . '. File: ' . $filename;
            }
            $post_should_redirect = true;
            break;
        }
        case 'self_avatar_iar': {
            if (!$self_acl) {
                $message = 'Self-service session is not active.';
                $post_should_redirect = true;
                break;
            }
            if ((int)($self_acl['can_export_iar'] ?? 0) !== 1) {
                $message = 'Permission denied: avatar IAR export not allowed.';
                $post_should_redirect = true;
                break;
            }

            if (backup_requests_blocked()) {
                $message = 'Backup requests are temporarily blocked by administrator.';
                $post_should_redirect = true;
                break;
            }

            $allowedRegions = parse_uuid_lines((string)($self_acl['allowed_regions'] ?? ''));
            if (empty($allowedRegions)) {
                $message = 'No allowed regions configured for this account.';
                break;
            }
            $regionUuid = strtolower(post_value('self_region_uuid'));
            if (!valid_uuid($regionUuid) || !in_array($regionUuid, $allowedRegions, true)) {
                $regionUuid = $allowedRegions[0];
            }

            $stmt = $robust_conn->prepare("SELECT uuid, regionName, serverURI FROM regions WHERE uuid = ? LIMIT 1");
            $stmt->bind_param("s", $regionUuid);
            $stmt->execute();
            $region = $stmt->get_result()->fetch_assoc();
            $stmt->close();
            if (!$region) {
                $message = 'Region not found for IAR export.';
                break;
            }

            $allowedAvatars = parse_uuid_lines((string)($self_acl['allowed_avatar_uuids'] ?? ''));
            if (empty($allowedAvatars)) {
                $allowedAvatars = [strtolower((string)$self_acl['avatar_uuid'])];
            }
            $userUuid = strtolower(post_value('self_avatar_uuid'));
            if (!valid_uuid($userUuid) || !in_array($userUuid, $allowedAvatars, true)) {
                $message = 'Permission denied: avatar is not in allowed IAR list.';
                $post_should_redirect = true;
                break;
            }

            $retryAfter = 0;
            if (backup_recently_triggered('iar-avatar:' . $userUuid, 180, $retryAfter)) {
                $message = 'Backup IAR request ignored (cooldown active). Retry in about ' . $retryAfter . 's.';
                $post_should_redirect = true;
                break;
            }
            $ownerUuid = strtolower((string)$self_acl['avatar_uuid']);
            $filename = '/home/grid/opensim/bin/Regions/' . (string)$region['regionName'] . '/oar/' . $userUuid . '-by-' . $ownerUuid . '-' . gmdate('Ymd-His') . '.iar';

            $uStmt = $robust_conn->prepare("SELECT FirstName, LastName FROM UserAccounts WHERE PrincipalID = ? LIMIT 1");
            $uStmt->bind_param("s", $userUuid);
            $uStmt->execute();
            $uRow = $uStmt->get_result()->fetch_assoc();
            $uStmt->close();
            if (!$uRow) {
                $message = 'IAR export failed: avatar account not found.';
                break;
            }
            $firstName = trim((string)($uRow['FirstName'] ?? ''));
            $lastName = trim((string)($uRow['LastName'] ?? ''));
            if ($firstName === '' || $lastName === '') {
                $message = 'IAR export failed: avatar account name is incomplete.';
                break;
            }

            $container = infra_pick_sim_container_for_region((string)$region['regionName']);
            if ($container === null) {
                $message = 'IAR export failed: no running simulator container matched region ' . (string)$region['regionName'] . '.';
                break;
            }

            $cmd = 'save iar ' . opensim_console_arg($firstName)
                . ' ' . opensim_console_arg($lastName)
                . ' ' . opensim_console_arg('/')
                . ' ' . opensim_console_arg($filename);
            $run = opensim_console_command($container, $cmd);
            if (!$run['ok']) {
                $message = 'IAR export failed: ' . (string)$run['error'] . '. ' . (string)$run['output'];
            } else {
                backup_mark_triggered('iar-avatar:' . $userUuid);
                $message = 'IAR export command sent to ' . $container . ' for avatar ' . $userUuid . '. File: ' . $filename;
            }
            $post_should_redirect = true;
            break;
        }
        case 'self_hg_add_user': {
            if (!$self_acl || (int)($self_acl['can_hg_add_user'] ?? 0) !== 1) {
                $message = 'Permission denied: self-service HG allow is not enabled.';
                break;
            }

            $uuid = strtolower(post_value('self_hg_uuid'));
            $gridname = strtolower(post_value('self_hg_gridname'));
            $comment = trim(post_value('self_hg_comment'));
            if (!valid_uuid($uuid)) {
                $message = 'HG allow failed: invalid UUID.';
                break;
            }
            if ($gridname === '') {
                $message = 'HG allow failed: grid name is required.';
                break;
            }

            if ($comment === '') {
                $comment = 'self-service by ' . strtolower((string)$self_acl['avatar_uuid']);
            }

            $stmt = $conn->prepare("INSERT INTO wp_opensim_auth (uuid, gridname, banned, comment, added_by_uuid, added_by_label, updated_by_uuid, updated_by_label)
                VALUES (?, ?, 0, ?, ?, ?, ?, ?)
                ON DUPLICATE KEY UPDATE
                    comment = VALUES(comment),
                    banned = 0,
                    ban_reason = NULL,
                    ban_date = NULL,
                    added_by_uuid = IF(added_by_uuid = '' OR added_by_uuid IS NULL, VALUES(added_by_uuid), added_by_uuid),
                    added_by_label = IF(added_by_label = '' OR added_by_label IS NULL, VALUES(added_by_label), added_by_label),
                    updated_by_uuid = VALUES(updated_by_uuid),
                    updated_by_label = VALUES(updated_by_label)");
            $stmt->bind_param("sssssss", $uuid, $gridname, $comment, $hg_actor_uuid, $hg_actor_label, $hg_actor_uuid, $hg_actor_label);
            $stmt->execute();
            $stmt->close();
            $message = 'HG user allow saved by self-service.';
            break;
        }
        case 'rolling_restart_schedule': {
            if (!$is_admin) {
                $message = 'Permission denied: admin only.';
                break;
            }

            $simToken = restart_sim_token_value();
            if ($simToken === '') {
                $message = 'Restart token is not configured.';
                break;
            }

            $requestedBy = trim(post_value('rolling_requested_by'));
            if ($requestedBy === '') {
                $requestedBy = 'panel-admin:' . ($current_user !== '' ? $current_user : 'unknown');
            }

            $reason = trim(post_value('rolling_reason', 'Rolling restart'));
            $firstDelay = max(0, min(86400 * 7, (int)post_value('rolling_first_delay_seconds', '180')));
            $gapSeconds = max(10, min(86400, (int)post_value('rolling_gap_seconds', '180')));

            $targetMode = strtolower(post_value('rolling_target_mode', 'all'));
            $targetUuids = parse_uuid_lines(post_value('rolling_region_uuids'));

            if ($targetMode === 'selected' && empty($targetUuids)) {
                $message = 'Rolling restart failed: no region UUIDs provided for selected mode.';
                break;
            }

            if ($targetMode === 'selected') {
                $placeholders = implode(',', array_fill(0, count($targetUuids), '?'));
                $types = str_repeat('s', count($targetUuids));
                $sql = "SELECT uuid, regionName, serverURI FROM regions WHERE uuid IN ($placeholders) ORDER BY regionName ASC";
                $stmt = $robust_conn->prepare($sql);
                $stmt->bind_param($types, ...$targetUuids);
                $stmt->execute();
                $res = $stmt->get_result();
            } else {
                $res = $robust_conn->query("SELECT uuid, regionName, serverURI FROM regions ORDER BY regionName ASC");
            }

            $regions = [];
            if ($res) {
                while ($row = $res->fetch_assoc()) {
                    $regions[] = $row;
                }
            }
            if (isset($stmt) && $stmt instanceof mysqli_stmt) {
                $stmt->close();
            }

            if (empty($regions)) {
                $message = 'Rolling restart failed: no regions matched.';
                break;
            }

            $okCount = 0;
            $failCount = 0;
            $lines = [];
            foreach ($regions as $idx => $region) {
                $delay = $firstDelay + ($idx * $gapSeconds);
                $resOne = restart_schedule_region($region, $requestedBy, $delay, $reason, $simToken);
                if (!empty($resOne['ok'])) {
                    $okCount++;
                    $lines[] = 'OK ' . (string)$resOne['name'] . ' (' . (string)$resOne['uuid'] . ') at +' . $delay . 's';
                } else {
                    $failCount++;
                    $lines[] = 'FAIL ' . (string)$resOne['name'] . ' (' . (string)$resOne['uuid'] . '): ' . (string)$resOne['body'];
                }
            }

            $message = 'Rolling restart submitted. OK: ' . $okCount . ', FAIL: ' . $failCount . "\n" . implode("\n", $lines);
            break;
        }
        case 'rolling_restart_abort': {
            if (!$is_admin) {
                $message = 'Permission denied: admin only.';
                break;
            }

            $simToken = restart_sim_token_value();
            if ($simToken === '') {
                $message = 'Restart token is not configured.';
                break;
            }

            $requestedBy = trim(post_value('rolling_requested_by'));
            if ($requestedBy === '') {
                $requestedBy = 'panel-admin:' . ($current_user !== '' ? $current_user : 'unknown');
            }
            $reason = trim(post_value('rolling_abort_reason', 'Rolling abort'));

            $targetMode = strtolower(post_value('rolling_target_mode', 'all'));
            $targetUuids = parse_uuid_lines(post_value('rolling_region_uuids'));
            if ($targetMode === 'selected' && empty($targetUuids)) {
                $message = 'Rolling abort failed: no region UUIDs provided for selected mode.';
                break;
            }

            if ($targetMode === 'selected') {
                $placeholders = implode(',', array_fill(0, count($targetUuids), '?'));
                $types = str_repeat('s', count($targetUuids));
                $sql = "SELECT uuid, regionName, serverURI FROM regions WHERE uuid IN ($placeholders) ORDER BY regionName ASC";
                $stmt = $robust_conn->prepare($sql);
                $stmt->bind_param($types, ...$targetUuids);
                $stmt->execute();
                $res = $stmt->get_result();
            } else {
                $res = $robust_conn->query("SELECT uuid, regionName, serverURI FROM regions ORDER BY regionName ASC");
            }

            $regions = [];
            if ($res) {
                while ($row = $res->fetch_assoc()) {
                    $regions[] = $row;
                }
            }
            if (isset($stmt) && $stmt instanceof mysqli_stmt) {
                $stmt->close();
            }

            if (empty($regions)) {
                $message = 'Rolling abort failed: no regions matched.';
                break;
            }

            $okCount = 0;
            $failCount = 0;
            $lines = [];
            foreach ($regions as $region) {
                $resOne = restart_cancel_region($region, $requestedBy, $reason, $simToken);
                if (!empty($resOne['ok'])) {
                    $okCount++;
                    $lines[] = 'OK ' . (string)$resOne['name'] . ' (' . (string)$resOne['uuid'] . ') cancel';
                } else {
                    $failCount++;
                    $lines[] = 'FAIL ' . (string)$resOne['name'] . ' (' . (string)$resOne['uuid'] . '): ' . (string)$resOne['body'];
                }
            }

            $message = 'Rolling abort submitted. OK: ' . $okCount . ', FAIL: ' . $failCount . "\n" . implode("\n", $lines);
            break;
        }
        case 'rolling_shutdown_execute': {
            if (!$is_admin) {
                $message = 'Permission denied: admin only.';
                break;
            }

            $targetMode = strtolower(post_value('rolling_target_mode', 'all'));
            $targetUuids = parse_uuid_lines(post_value('rolling_region_uuids'));
            if ($targetMode === 'selected' && empty($targetUuids)) {
                $message = 'Rolling shutdown failed: no region UUIDs provided for selected mode.';
                break;
            }

            if ($targetMode === 'selected') {
                $placeholders = implode(',', array_fill(0, count($targetUuids), '?'));
                $types = str_repeat('s', count($targetUuids));
                $sql = "SELECT uuid, regionName, serverURI FROM regions WHERE uuid IN ($placeholders) ORDER BY regionName ASC";
                $stmt = $robust_conn->prepare($sql);
                $stmt->bind_param($types, ...$targetUuids);
                $stmt->execute();
                $res = $stmt->get_result();
            } else {
                $res = $robust_conn->query("SELECT uuid, regionName, serverURI FROM regions ORDER BY regionName ASC");
            }

            $regions = [];
            if ($res) {
                while ($row = $res->fetch_assoc()) {
                    $regions[] = $row;
                }
            }
            if (isset($stmt) && $stmt instanceof mysqli_stmt) {
                $stmt->close();
            }
            if (empty($regions)) {
                $message = 'Rolling shutdown failed: no regions matched.';
                break;
            }

            $firstDelay = max(0, min(3600, (int)post_value('rolling_first_delay_seconds', '0')));
            $gapSeconds = max(1, min(600, (int)post_value('rolling_gap_seconds', '30')));
            $waitOff = max(3, min(120, (int)post_value('rolling_shutdown_wait_seconds', '30')));

            $okCount = 0;
            $failCount = 0;
            $lines = [];
            foreach ($regions as $idx => $region) {
                if ($idx === 0 && $firstDelay > 0) {
                    sleep($firstDelay);
                }
                if ($idx > 0 && $gapSeconds > 0) {
                    sleep($gapSeconds);
                }

                $regionName = (string)($region['regionName'] ?? '');
                $regionUuid = (string)($region['uuid'] ?? '');
                $container = infra_pick_sim_container_for_region($regionName);
                if ($container === null) {
                    $failCount++;
                    $lines[] = 'FAIL ' . $regionName . ' (' . $regionUuid . '): container not found';
                    continue;
                }

                $cmd = "tmux kill-session -t HealthCheck 2>/dev/null || true; tmux send-keys -t opensim q C-m";
                $run = infra_shell('docker exec ' . escapeshellarg($container) . ' sh -lc ' . escapeshellarg($cmd));
                if (!$run['ok']) {
                    $failCount++;
                    $lines[] = 'FAIL ' . $regionName . ' (' . $regionUuid . '): ' . trim((string)$run['output']);
                    continue;
                }

                [$hostEp, $portEp] = region_endpoint_host_port($region);
                $isOff = false;
                $lastProbe = 'n/a';
                for ($t = 0; $t < $waitOff; $t++) {
                    $open = region_port_is_open($hostEp, $portEp, 2);
                    $lastProbe = $open ? 'open' : 'closed';
                    if (!$open) {
                        $isOff = true;
                        break;
                    }
                    sleep(1);
                }

                if ($isOff) {
                    $okCount++;
                    $lines[] = 'OK ' . $regionName . ' (' . $regionUuid . '): HealthCheck off, q sent, port ' . $portEp . ' closed';
                } else {
                    $failCount++;
                    $lines[] = 'FAIL ' . $regionName . ' (' . $regionUuid . '): port ' . $portEp . ' still ' . $lastProbe . ' after ' . $waitOff . 's';
                }
            }

            $message = 'Rolling shutdown executed. OK: ' . $okCount . ', FAIL: ' . $failCount . "\n" . implode("\n", $lines);
            break;
        }
        case 'self_console_view': {
            if (!$self_acl) {
                $message = 'Self-service session is not active.';
                break;
            }

            $regionUuid = strtolower(post_value('self_console_region_uuid'));
            $self_console_region_uuid = $regionUuid;
            $self_console_lines = max(20, min(500, (int)post_value('self_console_lines', '120')));

            $allowedRegions = parse_uuid_lines((string)($self_acl['allowed_regions'] ?? ''));
            if (!valid_uuid($regionUuid) || !in_array($regionUuid, $allowedRegions, true)) {
                $message = 'Permission denied: region is not in allowed list.';
                break;
            }

            $stmt = $robust_conn->prepare("SELECT uuid, regionName FROM regions WHERE uuid = ? LIMIT 1");
            $stmt->bind_param("s", $regionUuid);
            $stmt->execute();
            $region = $stmt->get_result()->fetch_assoc();
            $stmt->close();
            if (!$region) {
                $message = 'Region not found.';
                break;
            }

            $container = infra_pick_sim_container_for_region((string)$region['regionName']);
            if ($container === null) {
                $message = 'Console read failed: no running simulator container matched region ' . (string)$region['regionName'] . '.';
                break;
            }

            $res = infra_console_capture_container($container, $self_console_lines, 'opensim');
            $self_console_output = (string)$res['output'];
            if ($res['ok']) {
                $message = 'Read-only console snapshot loaded for ' . (string)$region['regionName'] . '.';
            } else {
                $message = 'Console snapshot failed for ' . (string)$region['regionName'] . '.';
            }
            break;
        }
        case 'self_healthcheck_region': {
            if (!$self_acl) {
                $message = 'Self-service session is not active.';
                break;
            }
            if ((int)($self_acl['can_restart_region'] ?? 0) !== 1) {
                $message = 'Permission denied: restart permission required for HealthCheck control.';
                break;
            }

            $regionUuid = strtolower(post_value('self_health_region_uuid'));
            $allowedRegions = parse_uuid_lines((string)($self_acl['allowed_regions'] ?? ''));
            if (!valid_uuid($regionUuid) || !in_array($regionUuid, $allowedRegions, true)) {
                $message = 'Permission denied: region is not in allowed list.';
                break;
            }

            $stmt = $robust_conn->prepare("SELECT uuid, regionName FROM regions WHERE uuid = ? LIMIT 1");
            $stmt->bind_param("s", $regionUuid);
            $stmt->execute();
            $region = $stmt->get_result()->fetch_assoc();
            $stmt->close();
            if (!$region) {
                $message = 'Region not found.';
                break;
            }

            $container = infra_pick_sim_container_for_region((string)$region['regionName']);
            if ($container === null) {
                $message = 'HealthCheck start failed: no running simulator container matched region ' . (string)$region['regionName'] . '.';
                break;
            }

            $res = infra_start_healthcheck_container($container);
            if ($res['ok']) {
                $message = 'HealthCheck restarted for region ' . (string)$region['regionName'] . '.';
            } else {
                $message = 'HealthCheck start failed for region ' . (string)$region['regionName'] . ': ' . (string)$res['output'];
            }
            break;
        }
        case 'infra_services_run': {
            $infraCommand = strtolower(post_value('infra_command'));
            $infraTarget = post_value('infra_target', 'all');
            $allowedCommands = ['start', 'stop', 'restart', 'status', 'logs'];

            if (!in_array($infraCommand, $allowedCommands, true)) {
                $message = 'Infrastructure command is invalid.';
                break;
            }

            if ($infraTarget === 'all') {
                $res = infra_shell('/home/marty/opensim/manage_compose.sh ' . escapeshellarg($infraCommand));
                $message = "Infra command '{$infraCommand} all' executed.\n" . (string)$res['output'];
                break;
            }

            $safeTarget = trim($infraTarget);
            if (!preg_match('/^[A-Za-z0-9._-]{2,80}$/', $safeTarget)) {
                $message = 'Invalid service target.';
                break;
            }

            $res = infra_shell('/home/marty/opensim/manage_compose.sh ' . escapeshellarg($infraCommand) . ' ' . escapeshellarg($safeTarget));
            $message = "Infra command '{$infraCommand} {$safeTarget}' executed.\n" . (string)$res['output'];
            break;
        }
        case 'infra_healthcheck_run': {
            $res = infra_shell('/home/marty/opensim/sh.sh');
            $message = "HealthCheck start executed via sh.sh.\n" . (string)$res['output'];
            break;
        }
        case 'infra_healthcheck_region': {
            $regionUuid = strtolower(post_value('infra_health_region_uuid'));
            if (!valid_uuid($regionUuid)) {
                $message = 'HealthCheck start failed: invalid region UUID.';
                break;
            }

            $stmt = $robust_conn->prepare("SELECT uuid, regionName FROM regions WHERE uuid = ? LIMIT 1");
            $stmt->bind_param("s", $regionUuid);
            $stmt->execute();
            $region = $stmt->get_result()->fetch_assoc();
            $stmt->close();
            if (!$region) {
                $message = 'HealthCheck start failed: region not found.';
                break;
            }

            $container = infra_pick_sim_container_for_region((string)$region['regionName']);
            if ($container === null) {
                $message = 'HealthCheck start failed: no running simulator container matched region ' . (string)$region['regionName'] . '.';
                break;
            }

            $res = infra_start_healthcheck_container($container);
            if ($res['ok']) {
                $message = 'HealthCheck restarted for region ' . (string)$region['regionName'] . '.';
            } else {
                $message = 'HealthCheck start failed for region ' . (string)$region['regionName'] . ': ' . (string)$res['output'];
            }
            break;
        }
        case 'infra_console_view': {
            $service = trim(post_value('infra_console_service'));
            $lines = (int)post_value('infra_console_lines', '120');
            $infra_console_service = $service;
            $infra_console_lines = max(20, min(500, $lines));

            if ($service === '') {
                $message = 'Choose a service to read console output.';
                break;
            }

            $res = infra_console_readonly($service, $infra_console_lines, 'opensim');
            $infra_console_output = (string)$res['output'];
            if ($res['ok']) {
                $message = 'Console snapshot loaded for ' . $service . '.';
            } else {
                $message = 'Console snapshot failed for ' . $service . '.';
            }
            break;
        }
        case 'infra_plan_add_region': {
            $regionName = trim(post_value('region_name'));
            $templateRegion = trim(post_value('template_region', ''));
            $httpPortRaw = trim(post_value('region_http_port', ''));
            $sshPortRaw = trim(post_value('region_ssh_port', ''));
            $httpPort = $httpPortRaw === '' ? infra_next_http_port() : (int)$httpPortRaw;
            $sshPort = $sshPortRaw === '' ? infra_next_ssh_port() : (int)$sshPortRaw;
            $startNow = isset($_POST['start_now']) ? 1 : 0;

            if ($templateRegion === '') {
                $templateRegion = infra_pick_template_region();
            }

            if (!region_name_valid($regionName) || !region_name_valid($templateRegion)) {
                $message = 'Region name or template name is invalid.';
                break;
            }
            if ($httpPort < 1024 || $httpPort > 65535 || $sshPort < 1024 || $sshPort > 65535) {
                $message = 'HTTP/SSH ports must be in range 1024-65535.';
                break;
            }

            $plan = [
                'type' => 'add_region',
                'region_name' => $regionName,
                'template_region' => $templateRegion,
                'http_port' => $httpPort,
                'ssh_port' => $sshPort,
                'start_now' => $startNow,
                'service_name' => 'sim-' . $regionName,
                'compose_file' => '/home/marty/opensim/compose.d/docker-compose-' . $regionName . '.yml',
                'template_compose_file' => '/home/marty/opensim/compose.d/docker-compose-' . $templateRegion . '.yml',
                'service_ini' => '/home/marty/opensim/services2/' . $regionName . '.ini',
                'region_dir' => '/home/marty/opensim/regiongen/Regions/' . $regionName,
                'template_region_dir' => '/home/marty/opensim/regiongen/Regions/' . $templateRegion,
            ];

            $_SESSION['infra_plan'] = $plan;
            $message = "Prepared plan: ADD region {$regionName} from template {$templateRegion} (http {$httpPort}, ssh {$sshPort}). Click Execute Plan to apply.";
            break;
        }
        case 'infra_plan_remove_region': {
            $regionName = trim(post_value('remove_region_name'));
            if (!region_name_valid($regionName)) {
                $message = 'Region name is invalid.';
                break;
            }

            $plan = [
                'type' => 'remove_region',
                'region_name' => $regionName,
                'service_name' => 'sim-' . $regionName,
                'compose_file' => '/home/marty/opensim/compose.d/docker-compose-' . $regionName . '.yml',
                'service_ini' => '/home/marty/opensim/services2/' . $regionName . '.ini',
                'region_dir' => '/home/marty/opensim/regiongen/Regions/' . $regionName,
                'ssh_cfg' => '/home/marty/opensim/ssh2/' . $regionName . '.txt',
                'ssh_run_dir' => '/home/marty/opensim/ssh2/runsh/' . $regionName,
                'shadow_cfg' => '/home/marty/opensim/shadow2/' . $regionName . '.config',
            ];

            $_SESSION['infra_plan'] = $plan;
            $message = "Prepared plan: REMOVE region {$regionName}. Click Execute Plan to apply.";
            break;
        }
        case 'infra_execute_plan': {
            $plan = isset($_SESSION['infra_plan']) && is_array($_SESSION['infra_plan']) ? $_SESSION['infra_plan'] : null;
            if (!$plan || empty($plan['type'])) {
                $message = 'No prepared plan found.';
                break;
            }

            $output = [];
            if ($plan['type'] === 'add_region') {
                $templateCompose = (string)$plan['template_compose_file'];
                $newCompose = (string)$plan['compose_file'];
                $templateRegionDir = (string)$plan['template_region_dir'];
                $newRegionDir = (string)$plan['region_dir'];
                $newServiceIni = (string)$plan['service_ini'];

                if (!is_file($templateCompose) || !is_dir($templateRegionDir)) {
                    $message = 'Plan execute failed: template compose file or template region directory not found.';
                    break;
                }

                $composeText = (string)file_get_contents($templateCompose);
                $composeText = str_replace($plan['template_region'], $plan['region_name'], $composeText);
                $composeText = preg_replace('/http_listener_port\s*=\s*[0-9]+/', 'http_listener_port = ' . (int)$plan['http_port'], $composeText);
                file_put_contents($newCompose, $composeText);
                $output[] = 'Wrote compose file: ' . $newCompose;

                if (!is_dir(dirname($newServiceIni))) {
                    mkdir(dirname($newServiceIni), 0775, true);
                }
                $serviceIniText = "[enable]\nopensim = true\nrobust = false\n\n[programs]\n;robust = dotnet Robust.dll\nopensim = dotnet OpenSim.dll -inifile ./Regions/$HOSTNAME/Opensim.ini\n";
                file_put_contents($newServiceIni, $serviceIniText);
                $output[] = 'Wrote service file: ' . $newServiceIni;

                if (!is_dir($newRegionDir)) {
                    $res = infra_shell('cp -a ' . escapeshellarg($templateRegionDir) . ' ' . escapeshellarg($newRegionDir));
                    $output[] = trim((string)$res['output']);
                }

                $opensimIni = $newRegionDir . '/Opensim.ini';
                if (is_file($opensimIni)) {
                    $iniText = (string)file_get_contents($opensimIni);
                    $iniText = preg_replace('/RegionFolderName\s*=\s*"[^"]+"/', 'RegionFolderName = "' . $plan['region_name'] . '"', $iniText, 1);
                    $iniText = preg_replace('/http_listener_port\s*=\s*[0-9]+/', 'http_listener_port = ' . (int)$plan['http_port'], $iniText, 1);
                    file_put_contents($opensimIni, $iniText);
                    $output[] = 'Updated opensim config port/name: ' . $opensimIni;
                }

                $regionIniFiles = glob($newRegionDir . '/Region/*.ini') ?: [];
                if (!empty($regionIniFiles)) {
                    $targetRegionIni = $regionIniFiles[0];
                    $regionText = (string)file_get_contents($targetRegionIni);
                    $regionText = preg_replace('/^\[[^\]]+\]/m', '[' . $plan['region_name'] . ']', $regionText, 1);
                    $regionText = preg_replace('/^InternalPort\s*=\s*[0-9]+/m', 'InternalPort=' . (int)$plan['http_port'], $regionText, 1);
                    $regionText = preg_replace('/^RegionUUID\s*=\s*[0-9a-fA-F\-]+/m', 'RegionUUID=' . uuid_v4(), $regionText, 1);
                    file_put_contents($targetRegionIni, $regionText);
                    $output[] = 'Updated region .ini: ' . $targetRegionIni;
                }

                $sshRes = infra_shell('/home/marty/opensim/sshgen.sh ' . escapeshellarg((string)$plan['region_name']) . ' ' . escapeshellarg((string)$plan['ssh_port']));
                $output[] = trim((string)$sshRes['output']);

                $shPath = '/home/marty/opensim/sh.sh';
                $shText = (string)file_get_contents($shPath);
                $serviceEntry = '    "sim-' . $plan['region_name'] . '"';
                if (strpos($shText, $serviceEntry) === false) {
                    $shText = preg_replace('/\)\s*\n\s*for service in/', "{$serviceEntry}\n)\n\nfor service in", $shText, 1);
                    file_put_contents($shPath, $shText);
                    $output[] = 'Added service to sh.sh list.';
                }

                $gen2Path = '/home/marty/opensim/gen2.sh';
                $genLine = './sshgen.sh ' . $plan['region_name'] . ' ' . (int)$plan['ssh_port'];
                $gen2Text = is_file($gen2Path) ? (string)file_get_contents($gen2Path) : '';
                if ($gen2Text !== '' && strpos($gen2Text, $genLine) === false) {
                    $gen2Text = rtrim($gen2Text) . "\n" . $genLine . "\n";
                    file_put_contents($gen2Path, $gen2Text);
                    $output[] = 'Added ssh mapping to gen2.sh.';
                }

                if (!empty($plan['start_now'])) {
                    $startRes = infra_shell('/home/marty/opensim/manage_compose.sh start ' . escapeshellarg((string)$plan['service_name']));
                    $output[] = trim((string)$startRes['output']);
                }

                unset($_SESSION['infra_plan']);
                $message = "Plan executed: region added.\n" . implode("\n", array_filter($output));
                break;
            }

            if ($plan['type'] === 'remove_region') {
                $svc = (string)$plan['service_name'];
                $stopRes = infra_shell('/home/marty/opensim/manage_compose.sh stop ' . escapeshellarg($svc));
                $output[] = trim((string)$stopRes['output']);

                $removePaths = ['compose_file', 'service_ini', 'region_dir', 'ssh_cfg', 'ssh_run_dir', 'shadow_cfg'];
                foreach ($removePaths as $k) {
                    $path = (string)($plan[$k] ?? '');
                    if ($path === '') {
                        continue;
                    }
                    if (!infra_allowed_path($path)) {
                        continue;
                    }
                    if (is_dir($path)) {
                        $res = infra_shell('rm -rf ' . escapeshellarg($path));
                        $output[] = trim((string)$res['output']);
                    } elseif (is_file($path)) {
                        @unlink($path);
                        $output[] = 'Removed file: ' . $path;
                    }
                }

                $shPath = '/home/marty/opensim/sh.sh';
                $shText = (string)file_get_contents($shPath);
                $shText = preg_replace('/^\s*"sim-' . preg_quote((string)$plan['region_name'], '/') . '"\s*\n/m', '', $shText);
                file_put_contents($shPath, $shText);

                $gen2Path = '/home/marty/opensim/gen2.sh';
                if (is_file($gen2Path)) {
                    $gen2Text = (string)file_get_contents($gen2Path);
                    $gen2Text = preg_replace('/^\.\/sshgen\.sh\s+' . preg_quote((string)$plan['region_name'], '/') . '\s+[0-9]+\s*\n/m', '', $gen2Text);
                    file_put_contents($gen2Path, $gen2Text);
                }

                unset($_SESSION['infra_plan']);
                $message = "Plan executed: region removed.\n" . implode("\n", array_filter($output));
                break;
            }

            $message = 'Unknown plan type.';
            break;
        }
        case 'infra_editor_load': {
            $path = trim(post_value('edit_path'));
            if ($path === '' || !infra_allowed_path($path)) {
                $message = 'Path is not editable from panel.';
                break;
            }
            if (!is_file($path)) {
                $message = 'Selected file does not exist.';
                break;
            }
            $_SESSION['infra_edit_path'] = $path;
            $_SESSION['infra_edit_content'] = (string)file_get_contents($path);
            $message = 'Loaded file for editing: ' . $path;
            break;
        }
        case 'infra_editor_save': {
            $path = trim(post_value('edit_path'));
            $content = (string)($_POST['edit_content'] ?? '');
            if ($path === '' || !infra_allowed_path($path)) {
                $message = 'Path is not editable from panel.';
                break;
            }
            if (!is_file($path)) {
                $message = 'Cannot save: file does not exist.';
                break;
            }
            file_put_contents($path, $content);
            $_SESSION['infra_edit_path'] = $path;
            $_SESSION['infra_edit_content'] = $content;
            $message = 'Saved file: ' . $path;
            break;
        }
    }
}

if ($post_should_redirect) {
    $_SESSION['panel_flash_message'] = $message;
    header('Location: ' . ($_SERVER['REQUEST_URI'] ?? 'admin_panel.php'), true, 303);
    exit;
}

$maintenance = $conn->query("SELECT * FROM opensim_maintenance LIMIT 1")->fetch_assoc();
$panel_users = $conn->query("SELECT username, role, enabled, created_at, updated_at FROM wp_igrid_panel_users ORDER BY username ASC");
$local_users = $conn->query("SELECT uuid, first_name, last_name, banned, COALESCE(ban_reason, '') AS ban_reason, last_login FROM wp_oslogin_auth ORDER BY last_login DESC LIMIT 300");
$hg_users = $conn->query("SELECT uuid, gridname, banned, COALESCE(NULLIF(ban_reason,''), comment, '') AS reason, ban_date, added_by_uuid, added_by_label, added_at, updated_by_uuid, updated_by_label, updated_at FROM wp_opensim_auth ORDER BY id DESC LIMIT 500");
$banned_grids = $conn->query("SELECT gridname, COALESCE(reason, '') AS reason, created_at FROM banned_grids ORDER BY gridname ASC");
$partners = $conn->query("SELECT gridname, COALESCE(note, '') AS note, created_at FROM partners ORDER BY gridname ASC");
$viewer_block_config = $conn->query("SELECT * FROM wp_igrid_viewer_block_config LIMIT 1")->fetch_assoc();
$iauth_oidc_settings = $conn->query("SELECT * FROM wp_iauth_oidc_settings WHERE id = 1 LIMIT 1")->fetch_assoc();
$viewer_block_rules = $conn->query("SELECT id, match_pattern, match_mode, reason, enabled, updated_at FROM wp_igrid_viewer_block_rules ORDER BY id DESC LIMIT 300");
$region_auth_overrides = $conn->query("SELECT region_uuid, region_name, enabled, bypass_maintenance, bypass_viewer_block, public_guest_access, note, updated_at FROM wp_igrid_region_auth_overrides ORDER BY region_name ASC, region_uuid ASC");
$region_options = $robust_conn->query("SELECT uuid, regionName FROM regions ORDER BY regionName ASC");
$self_region_options = $robust_conn->query("SELECT uuid, regionName FROM regions ORDER BY regionName ASC");
$backup_region_options = $robust_conn->query("SELECT uuid, regionName FROM regions ORDER BY regionName ASC");
$backup_avatar_options = $robust_conn->query("SELECT PrincipalID AS uuid, COALESCE(FirstName,'') AS first_name, COALESCE(LastName,'') AS last_name FROM UserAccounts ORDER BY FirstName ASC, LastName ASC LIMIT 2000");
$selfservice_acl_rows = $conn->query("SELECT avatar_uuid, display_name, enabled, can_restart_region, can_backup_region, can_export_iar, can_hg_add_user, can_panel_admin, allowed_regions, allowed_avatar_uuids, updated_at FROM wp_igrid_selfservice_acl ORDER BY display_name ASC, avatar_uuid ASC");
$restart_acl = $conn->query("SELECT opensim_uuid, display_name, api_key, can_restart, can_cancel, enabled, updated_at FROM wp_igrid_region_restart_acl ORDER BY display_name ASC, opensim_uuid ASC");
$restart_regions = $robust_conn->query("SELECT uuid, regionName, serverURI, last_seen FROM regions ORDER BY regionName ASC");
$restart_logs = $conn->query("SELECT requested_by_uuid, region_name, action, delay_seconds, reason, http_status, created_at FROM wp_igrid_region_restart_log ORDER BY id DESC LIMIT 50");
$im_targets = $robust_conn->query("SELECT PrincipalID AS uuid, COALESCE(FirstName, '') AS first_name, COALESCE(LastName, '') AS last_name FROM UserAccounts ORDER BY FirstName ASC, LastName ASC LIMIT 1200");
$presence_zero_uuid = '00000000-0000-0000-0000-000000000000';

$online_im_targets = $robust_conn->query("SELECT p.UserID AS uuid, COALESCE(ua.FirstName, '') AS first_name, COALESCE(ua.LastName, '') AS last_name, COALESCE(r.regionName, '') AS region_name
    FROM Presence p
    LEFT JOIN UserAccounts ua ON ua.PrincipalID = p.UserID
    LEFT JOIN regions r ON r.uuid = p.RegionID
    WHERE p.RegionID <> '" . $presence_zero_uuid . "'
    ORDER BY ua.FirstName ASC, ua.LastName ASC
    LIMIT 200");

$presence_online_count_row = $robust_conn->query("SELECT COUNT(*) AS c FROM GridUser WHERE Online = 'True'")->fetch_assoc();
$presence_active_count_row = $robust_conn->query("SELECT COUNT(*) AS c FROM Presence
    WHERE RegionID <> '" . $presence_zero_uuid . "'")->fetch_assoc();
$presence_stale_count_row = $robust_conn->query("SELECT COUNT(*) AS c
    FROM GridUser g
    LEFT JOIN Presence p ON p.UserID = g.UserID
    WHERE g.Online = 'True'
      AND (
        p.UserID IS NULL
        OR p.RegionID IS NULL
        OR p.RegionID = '" . $presence_zero_uuid . "'
      )")->fetch_assoc();

$presence_online_count = (int)($presence_online_count_row['c'] ?? 0);
$presence_active_count = (int)($presence_active_count_row['c'] ?? 0);
$presence_stale_count = (int)($presence_stale_count_row['c'] ?? 0);

$presence_active_rows = $robust_conn->query("SELECT p.UserID AS uuid, COALESCE(ua.FirstName, '') AS first_name, COALESCE(ua.LastName, '') AS last_name,
        p.RegionID AS presence_region_id, COALESCE(r.regionName, '') AS presence_region_name, p.LastSeen AS presence_last_seen
    FROM Presence p
    LEFT JOIN UserAccounts ua ON ua.PrincipalID = p.UserID
    LEFT JOIN regions r ON r.uuid = p.RegionID
    WHERE p.RegionID <> '" . $presence_zero_uuid . "'
    ORDER BY p.LastSeen DESC
    LIMIT 300");

$presence_stale_rows = $robust_conn->query("SELECT g.UserID AS uuid, COALESCE(ua.FirstName, '') AS first_name, COALESCE(ua.LastName, '') AS last_name,
        g.LastRegionID AS grid_region_id, COALESCE(gr.regionName, '') AS grid_region_name, g.Login AS grid_login,
        COALESCE(p.RegionID, '') AS presence_region_id, COALESCE(pr.regionName, '') AS presence_region_name,
        p.LastSeen AS presence_last_seen
    FROM GridUser g
    LEFT JOIN UserAccounts ua ON ua.PrincipalID = g.UserID
    LEFT JOIN regions gr ON gr.uuid = g.LastRegionID
    LEFT JOIN Presence p ON p.UserID = g.UserID
    LEFT JOIN regions pr ON pr.uuid = p.RegionID
    WHERE g.Online = 'True'
      AND (
        p.UserID IS NULL
        OR p.RegionID IS NULL
        OR p.RegionID = '" . $presence_zero_uuid . "'
      )
    ORDER BY COALESCE(p.LastSeen, '1970-01-01 00:00:00') ASC
    LIMIT 500");

$online_users_total_row = $robust_conn->query("SELECT COUNT(*) AS c FROM GridUser")->fetch_assoc();
$online_users_live_row = $robust_conn->query("SELECT COUNT(*) AS c
    FROM GridUser g
    LEFT JOIN Presence p ON p.UserID = g.UserID
    WHERE g.Online = 'True'
      AND p.RegionID IS NOT NULL
      AND p.RegionID <> '" . $presence_zero_uuid . "'")->fetch_assoc();

$online_users_total = (int)($online_users_total_row['c'] ?? 0);
$online_users_live = (int)($online_users_live_row['c'] ?? 0);
$online_users_offline = $online_users_total - $online_users_live;
if ($online_users_offline < 0) {
    $online_users_offline = 0;
}

$online_users_rows = $robust_conn->query("SELECT
        g.UserID AS uuid,
        COALESCE(ua.FirstName, '') AS first_name,
        COALESCE(ua.LastName, '') AS last_name,
        g.Online AS online_flag,
        g.LastPosition AS last_position,
        g.LastRegionID AS grid_region_id,
        COALESCE(gr.regionName, '') AS grid_region_name,
        p.RegionID AS presence_region_id,
        COALESCE(pr.regionName, '') AS presence_region_name,
        p.LastSeen AS presence_last_seen,
        FROM_UNIXTIME(CASE WHEN g.Login REGEXP '^[0-9]+$' THEN CAST(g.Login AS UNSIGNED) ELSE NULL END) AS login_at,
        FROM_UNIXTIME(CASE WHEN g.Logout REGEXP '^[0-9]+$' THEN CAST(g.Logout AS UNSIGNED) ELSE NULL END) AS logout_at
    FROM GridUser g
    LEFT JOIN UserAccounts ua ON ua.PrincipalID = g.UserID
    LEFT JOIN Presence p ON p.UserID = g.UserID
    LEFT JOIN regions pr ON pr.uuid = p.RegionID
    LEFT JOIN regions gr ON gr.uuid = g.LastRegionID
    ORDER BY
        CASE
            WHEN g.Online = 'True' AND p.RegionID IS NOT NULL AND p.RegionID <> '" . $presence_zero_uuid . "' THEN 0
            WHEN g.Online = 'True' THEN 1
            ELSE 2
        END,
        COALESCE(ua.FirstName, '') ASC,
        COALESCE(ua.LastName, '') ASC,
        g.UserID ASC
    LIMIT 2000");

$abuse_reports = $conn->query("SELECT
        id,
        received_at,
        source_region_name,
        reported_region_name,
        reporter_name,
        reporter_uuid,
        abuser_uuid,
        screenshot_uuid,
        screenshot_uuid_original,
        screenshot_status,
        category,
        report_type,
        summary,
        details,
        position_x,
        position_y,
        position_z
    FROM wp_igrid_abuse_reports
    ORDER BY id DESC
    LIMIT 300");

$infra_services = parse_services_from_sh('/home/marty/opensim/sh.sh');
sort($infra_services);

$infra_status_map = [];
$psRes = infra_shell("docker ps -a --format '{{.Names}}|{{.Status}}'");
if (!empty($psRes['output'])) {
    $lines = preg_split('/\r?\n/', trim((string)$psRes['output']));
    foreach ($lines as $line) {
        if ($line === '' || strpos($line, '|') === false) {
            continue;
        }
        [$name, $status] = explode('|', $line, 2);
        $infra_status_map[(string)$name] = (string)$status;
    }
}

$infra_rows = [];
foreach ($infra_services as $svc) {
    $expected = 'opensim-' . $svc . '-1';
    $status = $infra_status_map[$expected] ?? '';
    if ($status === '') {
        foreach ($infra_status_map as $containerName => $containerStatus) {
            if (strpos($containerName, '-' . $svc . '-') !== false) {
                $status = $containerStatus;
                $expected = $containerName;
                break;
            }
        }
    }
    if ($status === '') {
        $status = 'not found';
    }

    $infra_rows[] = [
        'service' => $svc,
        'container' => $expected,
        'status' => $status,
    ];
}

$infra_plan = isset($_SESSION['infra_plan']) && is_array($_SESSION['infra_plan']) ? $_SESSION['infra_plan'] : null;
$infra_edit_path = isset($_SESSION['infra_edit_path']) ? (string)$_SESSION['infra_edit_path'] : '';
$infra_edit_content = isset($_SESSION['infra_edit_content']) ? (string)$_SESSION['infra_edit_content'] : '';
$infra_next_http_port = infra_next_http_port();
$infra_next_ssh_port = infra_next_ssh_port();
$infra_auto_template = infra_pick_template_region();

$infra_editable_files = [];
foreach (glob('/home/marty/opensim/compose.d/*.yml') ?: [] as $f) {
    $infra_editable_files[] = $f;
}
if (is_file('/home/marty/opensim/docker-compose.yml')) {
    $infra_editable_files[] = '/home/marty/opensim/docker-compose.yml';
}
foreach (glob('/home/marty/opensim/services2/*.ini') ?: [] as $f) {
    $infra_editable_files[] = $f;
}
foreach (glob('/home/marty/opensim/regiongen/Regions/*/Opensim.ini') ?: [] as $f) {
    $infra_editable_files[] = $f;
}
foreach (glob('/home/marty/opensim/config-include/*.ini') ?: [] as $f) {
    $infra_editable_files[] = $f;
}
foreach (glob('/home/marty/opensim/config-public/*.ini') ?: [] as $f) {
    $infra_editable_files[] = $f;
}
foreach (glob('/home/marty/opensim/regiongen/config-include/*.ini') ?: [] as $f) {
    $infra_editable_files[] = $f;
}
foreach (glob('/home/marty/opensim/regiongen/config-include2/*.ini') ?: [] as $f) {
    $infra_editable_files[] = $f;
}
$infra_editable_files[] = '/home/marty/opensim/sh.sh';
$infra_editable_files[] = '/home/marty/opensim/gen2.sh';
sort($infra_editable_files);

$self_region_map = [];
if ($self_region_options) {
    while ($sr = $self_region_options->fetch_assoc()) {
        $uuid = strtolower((string)($sr['uuid'] ?? ''));
        $name = (string)($sr['regionName'] ?? '');
        if (valid_uuid($uuid)) {
            $self_region_map[$uuid] = $name;
        }
    }
}

$backup_region_map = [];
if ($backup_region_options) {
    while ($br = $backup_region_options->fetch_assoc()) {
        $uuid = strtolower((string)($br['uuid'] ?? ''));
        $name = (string)($br['regionName'] ?? '');
        if (valid_uuid($uuid)) {
            $backup_region_map[$uuid] = $name;
        }
    }
}

$backup_avatar_map = [];
if ($backup_avatar_options) {
    while ($ba = $backup_avatar_options->fetch_assoc()) {
        $uuid = strtolower((string)($ba['uuid'] ?? ''));
        if (!valid_uuid($uuid)) {
            continue;
        }
        $name = trim((string)($ba['first_name'] ?? '') . ' ' . (string)($ba['last_name'] ?? ''));
        $backup_avatar_map[$uuid] = $name !== '' ? $name : $uuid;
    }
}

$self_allowed_regions = $self_acl ? parse_uuid_lines((string)($self_acl['allowed_regions'] ?? '')) : [];
$self_allowed_avatars = $self_acl ? parse_uuid_lines((string)($self_acl['allowed_avatar_uuids'] ?? '')) : [];
if ($self_acl && empty($self_allowed_avatars)) {
    $self_allowed_avatars = [strtolower((string)$self_acl['avatar_uuid'])];
}

$archive_files_oar = archive_collect_files('oar');
$archive_files_iar = archive_collect_files('iar');

$self_archive_files_oar = [];
$allowed_region_names = [];
foreach ($self_allowed_regions as $ru) {
    if (isset($self_region_map[$ru])) {
        $allowed_region_names[] = (string)$self_region_map[$ru];
    }
}
$allowed_region_names = array_values(array_unique(array_filter($allowed_region_names, static function ($v) {
    return $v !== '';
})));

if ($is_selfservice && $self_acl && (int)($self_acl['can_backup_region'] ?? 0) === 1) {
    foreach ($archive_files_oar as $f) {
        if (!selfservice_owns_archive_file((string)($f['path'] ?? ''), (string)$self_acl['avatar_uuid'])) {
            continue;
        }
        $rel = str_replace('\\', '/', (string)($f['rel'] ?? ''));
        $dir = trim((string)strtok($rel, '/'));
        if ($dir === '') {
            continue;
        }
        foreach ($allowed_region_names as $rn) {
            if ($dir === $rn || infra_norm_token($dir) === infra_norm_token($rn)) {
                $self_archive_files_oar[] = $f;
                break;
            }
        }
        if (count($self_archive_files_oar) >= 120) {
            break;
        }
    }
}

$self_archive_files_iar = [];
if ($is_selfservice && $self_acl && (int)($self_acl['can_export_iar'] ?? 0) === 1) {
    foreach ($archive_files_iar as $f) {
        if (!selfservice_owns_archive_file((string)($f['path'] ?? ''), (string)$self_acl['avatar_uuid'])) {
            continue;
        }
        $base = strtolower(basename((string)$f['path']));
        foreach ($self_allowed_avatars as $u) {
            if (strpos($base, strtolower($u) . '-') === 0) {
                $self_archive_files_iar[] = $f;
                break;
            }
        }
        if (count($self_archive_files_iar) >= 120) {
            break;
        }
    }
}
?>
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>I-Grid Access Panel</title>
    <style>
        @import url('https://fonts.googleapis.com/css2?family=Inter:wght@400;600;700&display=swap');
        @keyframes jumbo {
            from { background-position: 50% 50%, 50% 50%; }
            to { background-position: 350% 50%, 350% 50%; }
        }
        :root {
            --ink: #0f172a;
            --muted: #475569;
            --paper: rgba(255,255,255,.93);
            --line: #dbe4ee;
            --ok: #15803d;
            --bad: #b91c1c;
            --pri: #0ea5e9;
        }
        * { box-sizing: border-box; }
        body {
            margin: 0;
            font-family: Inter, sans-serif;
            color: var(--ink);
            background: #0b1220;
        }
        .jumbo {
            position: fixed;
            inset: 0;
            --stripes: repeating-linear-gradient(100deg, #fff 0%, #fff 7%, transparent 10%, transparent 12%, #fff 16%);
            --rainbow: repeating-linear-gradient(100deg, #38bdf8 10%, #22d3ee 15%, #0ea5e9 20%, #fb923c 25%, #38bdf8 30%);
            background-image: var(--stripes), var(--rainbow);
            background-size: 280%, 190%;
            background-position: 50% 50%, 50% 50%;
            filter: blur(10px) invert(100%);
            mask-image: radial-gradient(ellipse at 100% 0%, black 40%, transparent 70%);
            pointer-events: none;
            z-index: 0;
        }
        .jumbo::after {
            content: "";
            position: absolute;
            inset: 0;
            background-image: var(--stripes), var(--rainbow);
            background-size: 200%, 100%;
            animation: jumbo 60s linear infinite;
            mix-blend-mode: difference;
        }
        .wrap {
            position: relative;
            z-index: 1;
            max-width: 1400px;
            margin: 24px auto;
            padding: 0 14px;
        }
        .topbar {
            display: flex;
            justify-content: space-between;
            gap: 10px;
            align-items: center;
            background: var(--paper);
            border: 1px solid rgba(255,255,255,.45);
            border-radius: 16px;
            padding: 16px 18px;
            box-shadow: 0 14px 38px rgba(0,0,0,.24);
        }
        .topbar h1 { margin: 0; font-size: 1.2rem; }
        .notice {
            margin-top: 12px;
            background: #e0f2fe;
            border: 1px solid #7dd3fc;
            color: #0c4a6e;
            border-radius: 12px;
            padding: 10px 12px;
            font-weight: 600;
        }
        .tabbar {
            margin-top: 12px;
            display: flex;
            flex-wrap: wrap;
            gap: 8px;
        }
        .tab-btn {
            border-radius: 999px;
            border: 1px solid #cbd5e1;
            background: #e2e8f0;
            color: #0f172a;
            font-weight: 700;
            padding: 8px 14px;
            cursor: pointer;
        }
        .tab-btn.is-active {
            background: linear-gradient(90deg, #0ea5e9, #0369a1);
            color: #ffffff;
            border-color: transparent;
            box-shadow: 0 8px 18px rgba(2, 132, 199, .35);
        }
        .grid {
            margin-top: 14px;
            display: grid;
            grid-template-columns: repeat(12, 1fr);
            gap: 14px;
        }
        .card {
            grid-column: span 12;
            background: var(--paper);
            border: 1px solid rgba(255,255,255,.45);
            border-radius: 16px;
            padding: 16px;
            box-shadow: 0 14px 32px rgba(0,0,0,.2);
            overflow: hidden;
        }
        .rolling-hub {
            border: 1px solid #bae6fd;
            background: linear-gradient(120deg, #e0f2fe, #f0f9ff, #e0f2fe);
            background-size: 220% 220%;
            border-radius: 12px;
            padding: 10px;
        }
        .rolling-strip {
            height: 6px;
            border-radius: 999px;
            background: linear-gradient(90deg, #0284c7, #06b6d4, #22d3ee, #0284c7);
            background-size: 220% 100%;
            margin-bottom: 8px;
        }
        section.card[data-tab="rolling"].is-active-tab .rolling-hub {
            animation: rollingPulse 2.1s ease-in-out infinite;
        }
        section.card[data-tab="rolling"].is-active-tab .rolling-strip {
            animation: rollingMove 1.6s linear infinite;
        }
        @keyframes rollingMove {
            from { background-position: 0% 50%; }
            to { background-position: 220% 50%; }
        }
        @keyframes rollingPulse {
            0%, 100% { box-shadow: 0 0 0 rgba(14,165,233,0); }
            50% { box-shadow: 0 0 0 6px rgba(14,165,233,0.12); }
        }
        .full { grid-column: span 12; }
        .card h2 { margin: 0 0 10px; font-size: 1.05rem; }
        .card p { margin: 0 0 10px; color: var(--muted); }
        .half { grid-column: span 6; }
        .third { grid-column: span 4; }
        .row {
            display: flex;
            flex-wrap: wrap;
            gap: 8px;
            align-items: center;
        }
        input, textarea, select, button {
            border-radius: 10px;
            border: 1px solid #cbd5e1;
            font: inherit;
            padding: 9px 10px;
        }
        textarea { width: 100%; min-height: 80px; }
        input[type="text"] { min-width: 180px; }
        button {
            border: none;
            background: #1e293b;
            color: #fff;
            font-weight: 700;
            cursor: pointer;
        }
        .btn-pri { background: linear-gradient(90deg, #0ea5e9, #0369a1); }
        .btn-ok { background: linear-gradient(90deg, #16a34a, #15803d); }
        .btn-bad { background: linear-gradient(90deg, #dc2626, #991b1b); }
        .btn-soft { background: #334155; }
        .table-wrap { overflow: auto; border: 1px solid var(--line); border-radius: 12px; }
        table { width: 100%; border-collapse: collapse; min-width: 780px; }
        th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid #e5e7eb; vertical-align: top; }
        th { background: #f8fafc; font-size: .86rem; color: #334155; text-transform: uppercase; letter-spacing: .04em; }
        tr:hover td { background: #f8fafc; }
        .tag { font-weight: 700; font-size: .78rem; padding: 4px 7px; border-radius: 999px; display: inline-block; }
        .tag-ok { color: #065f46; background: #d1fae5; }
        .tag-bad { color: #7f1d1d; background: #fee2e2; }
        .mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: .83rem; }
        @media (max-width: 980px) {
            .half, .third { grid-column: span 12; }
            .topbar { flex-direction: column; align-items: flex-start; }
            .tabbar { width: 100%; }
            .tab-btn { flex: 1 1 auto; text-align: center; }
        }
    </style>
</head>
<body>
    <div class="jumbo"></div>
    <div class="wrap">
        <div class="topbar">
            <h1>I-Grid Access Panel</h1>
            <div class="row">
                <span class="mono"><?php echo h($current_user); ?></span>
                <span class="tag <?php echo $is_admin ? 'tag-ok' : 'tag-bad'; ?>"><?php echo $is_admin ? 'ADMIN' : ($is_selfservice ? 'SELF-SERVICE' : 'ADD-ONLY'); ?></span>
                <?php if ($is_selfservice && !$is_admin): ?>
                    <form method="post"><button class="btn-soft" type="submit" name="self_logout">Logout</button></form>
                <?php else: ?>
                    <form method="post"><button class="btn-soft" type="submit" name="logout">Logout</button></form>
                <?php endif; ?>
            </div>
        </div>
        <?php if (!empty($message)): ?><div class="notice"><?php echo h($message); ?></div><?php endif; ?>

        <div class="tabbar" id="panelTabs">
            <?php if ($is_admin): ?>
                <button type="button" class="tab-btn" data-tab="panel">Panel Users</button>
                <button type="button" class="tab-btn" data-tab="regions">Regions</button>
                <button type="button" class="tab-btn" data-tab="rolling">Rolling</button>
                <button type="button" class="tab-btn" data-tab="backup">Backup</button>
                <button type="button" class="tab-btn" data-tab="infra">Infra</button>
                <button type="button" class="tab-btn" data-tab="presence">Presence</button>
                <button type="button" class="tab-btn" data-tab="online">Online</button>
                <button type="button" class="tab-btn" data-tab="alerts">Alerts</button>
                <button type="button" class="tab-btn" data-tab="reports">Reports</button>
                <button type="button" class="tab-btn" data-tab="viewer">Viewer Block</button>
                <button type="button" class="tab-btn" data-tab="self">Self-Service</button>
            <?php endif; ?>
            <?php if ($is_selfservice && !$is_admin): ?>
                <button type="button" class="tab-btn" data-tab="self">Self-Service</button>
            <?php else: ?>
                <button type="button" class="tab-btn" data-tab="access">Access Lists</button>
            <?php endif; ?>
        </div>

        <div class="grid">
            <?php if ($is_admin): ?>
            <section class="card half" data-tab="access">
                <h2>Maintenance Mode</h2>
                <form method="post">
                    <input type="hidden" name="action" value="update_maintenance">
                    <label><input type="checkbox" name="maintenance_mode" <?php echo !empty($maintenance['maintenance_mode']) ? 'checked' : ''; ?>> Enable maintenance</label>
                    <div style="height:8px"></div>
                    <textarea name="maintenance_message"><?php echo h((string)($maintenance['maintenance_message'] ?? '')); ?></textarea>
                    <div style="height:8px"></div>
                    <textarea name="allowed_uuids" placeholder="Allowed UUIDs, comma-separated"><?php echo h((string)($maintenance['allowed_uuids'] ?? '')); ?></textarea>
                    <div style="height:8px"></div>
                    <button class="btn-pri" type="submit">Save Maintenance</button>
                </form>
            </section>
            <?php endif; ?>

            <?php if ($is_admin): ?>
            <section class="card half" data-tab="access">
                <h2>I-Auth OIDC Redirect Policy</h2>
                <p>Keep disabled for now. When enabled, only allowlisted redirect URIs can be used by OIDC authorize flow. Use scopes <code>openid groups role</code> in clients.</p>
                <form method="post">
                    <input type="hidden" name="action" value="iauth_oidc_settings_save">
                    <label><input type="checkbox" name="redirect_allowlist_enabled" <?php echo !empty($iauth_oidc_settings['redirect_allowlist_enabled']) ? 'checked' : ''; ?>> Enable redirect URI allowlist</label>
                    <div style="height:8px"></div>
                    <input type="text" name="default_group_name" value="<?php echo h((string)($iauth_oidc_settings['default_group_name'] ?? 'I-Grid Residents')); ?>" placeholder="Default OIDC group (e.g. I-Grid Residents)">
                    <div style="height:8px"></div>
                    <select name="default_role">
                        <option value="user" <?php echo ((string)($iauth_oidc_settings['default_role'] ?? 'user') === 'user') ? 'selected' : ''; ?>>user (default)</option>
                        <option value="admin" <?php echo ((string)($iauth_oidc_settings['default_role'] ?? 'user') === 'admin') ? 'selected' : ''; ?>>admin</option>
                    </select>
                    <div style="height:8px"></div>
                    <textarea name="admin_role_uuids" placeholder="UUIDs forced to admin role (comma/newline)" style="min-height:90px"><?php echo h((string)($iauth_oidc_settings['admin_role_uuids'] ?? '')); ?></textarea>
                    <div style="height:8px"></div>
                    <textarea name="redirect_allowlist_text" placeholder="One rule per line. Exact URI or prefix* wildcard.
Examples:
https://kc.example.com/realms/main/broker/igrid-otp/endpoint
https://app.example.com/callback*" style="min-height:120px"><?php echo h((string)($iauth_oidc_settings['redirect_allowlist_text'] ?? '')); ?></textarea>
                    <div style="height:8px"></div>
                    <button class="btn-soft" type="submit">Save OIDC Redirect Policy</button>
                </form>
            </section>
            <?php endif; ?>

            <?php if (!$is_selfservice): ?>
            <section class="card <?php echo $is_admin ? 'half' : 'full'; ?>" data-tab="access">
                <h2>HG User Access</h2>
                <p>Add explicit `(uuid + grid)` allow rule. Delete removes access.</p>
                <form method="post" class="row">
                    <input type="hidden" name="action" value="hg_add_user">
                    <input type="text" name="uuid" placeholder="UUID" required>
                    <input type="text" name="gridname" placeholder="grid.example.org:8002" required>
                    <input type="text" name="comment" placeholder="Comment">
                    <button class="btn-ok" type="submit">Add / Allow</button>
                </form>
            </section>
            <?php endif; ?>

            <?php if ($is_admin): ?>
            <section class="card" data-tab="viewer">
                <h2>Viewer Version Block</h2>
                <p>Block viewer versions/channels by pattern. Enforced by <code>hgauthnew.php</code>.</p>
                <form method="post" style="margin-bottom:12px;">
                    <input type="hidden" name="action" value="viewer_block_config_update">
                    <label class="mono" style="display:block;margin-bottom:6px;">Viewer block UUID exceptions (comma/newline separated)</label>
                    <textarea name="viewer_allowed_uuids" placeholder="UUIDs that bypass viewer block rules"><?php echo h((string)($viewer_block_config['allowed_uuids'] ?? '')); ?></textarea>
                    <div style="height:8px"></div>
                    <button class="btn-soft" type="submit">Save Viewer Exceptions</button>
                </form>
                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="viewer_rule_add">
                    <input type="text" name="match_pattern" placeholder="e.g. 7.1.9 or Firestorm" required>
                    <select name="match_mode">
                        <option value="contains">contains</option>
                        <option value="equals">equals</option>
                        <option value="regex">regex</option>
                    </select>
                    <input type="text" name="reason" placeholder="Block reason shown to user" value="Viewer version blocked by policy">
                    <label><input type="checkbox" name="enabled" checked> enabled</label>
                    <button class="btn-bad" type="submit">Add Block Rule</button>
                </form>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>ID</th><th>Pattern</th><th>Mode</th><th>Reason</th><th>Status</th><th>Updated</th><th>Actions</th></tr></thead>
                        <tbody>
                        <?php if ($viewer_block_rules): ?>
                            <?php while ($vr = $viewer_block_rules->fetch_assoc()): ?>
                                <tr>
                                    <td class="mono"><?php echo h((string)$vr['id']); ?></td>
                                    <td class="mono"><?php echo h((string)$vr['match_pattern']); ?></td>
                                    <td><?php echo h((string)$vr['match_mode']); ?></td>
                                    <td><?php echo h((string)$vr['reason']); ?></td>
                                    <td><?php echo !empty($vr['enabled']) ? '<span class="tag tag-bad">BLOCKING</span>' : '<span class="tag">DISABLED</span>'; ?></td>
                                    <td><?php echo h((string)$vr['updated_at']); ?></td>
                                    <td>
                                        <form method="post" class="row" style="margin-bottom:6px;">
                                            <input type="hidden" name="action" value="viewer_rule_update">
                                            <input type="hidden" name="rule_id" value="<?php echo h((string)$vr['id']); ?>">
                                            <input type="text" name="match_pattern" value="<?php echo h((string)$vr['match_pattern']); ?>" required>
                                            <select name="match_mode">
                                                <option value="contains" <?php echo ((string)$vr['match_mode'] === 'contains') ? 'selected' : ''; ?>>contains</option>
                                                <option value="equals" <?php echo ((string)$vr['match_mode'] === 'equals') ? 'selected' : ''; ?>>equals</option>
                                                <option value="regex" <?php echo ((string)$vr['match_mode'] === 'regex') ? 'selected' : ''; ?>>regex</option>
                                            </select>
                                            <input type="text" name="reason" value="<?php echo h((string)$vr['reason']); ?>">
                                            <label><input type="checkbox" name="enabled" <?php echo !empty($vr['enabled']) ? 'checked' : ''; ?>> enabled</label>
                                            <button class="btn-pri" type="submit">Update</button>
                                        </form>
                                        <form method="post" onsubmit="return confirm('Remove viewer block rule #<?php echo h((string)$vr['id']); ?>?');">
                                            <input type="hidden" name="action" value="viewer_rule_remove">
                                            <input type="hidden" name="rule_id" value="<?php echo h((string)$vr['id']); ?>">
                                            <button class="btn-soft" type="submit">Remove</button>
                                        </form>
                                    </td>
                                </tr>
                            <?php endwhile; ?>
                        <?php else: ?>
                            <tr><td colspan="7">No viewer block rules configured.</td></tr>
                        <?php endif; ?>
                        </tbody>
                    </table>
                </div>
            </section>
            <?php endif; ?>

            <?php if ($is_add_only): ?>
            <section class="card" data-tab="access">
                <h2>Limited Access Mode</h2>
                <p>This account can only add/allow HG users in <code>wp_opensim_auth</code>.</p>
            </section>
            <?php endif; ?>

            <?php if ($is_admin): ?>
            <section class="card" data-tab="panel">
                <h2>Panel Users</h2>
                <p>Add or update who can log into this panel. Username format: <code>a-z 0-9 . _ -</code> (3-64 chars).</p>
                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="panel_user_add">
                    <input type="text" name="username" placeholder="username" pattern="[a-z0-9_.-]{3,64}" required>
                    <input type="password" name="password" placeholder="password" required>
                    <select name="role">
                        <option value="add_only">Add-Only</option>
                        <option value="admin">Admin</option>
                    </select>
                    <label><input type="checkbox" name="enabled" checked> enabled</label>
                    <button class="btn-ok" type="submit">Add User</button>
                </form>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>Username</th><th>Role</th><th>Status</th><th>Updated</th><th>Actions</th></tr></thead>
                        <tbody>
                        <?php while ($pu = $panel_users->fetch_assoc()): ?>
                            <tr>
                                <td class="mono"><?php echo h((string)$pu['username']); ?></td>
                                <td><?php echo ((string)$pu['role'] === 'admin') ? '<span class="tag tag-ok">ADMIN</span>' : '<span class="tag" style="background:#e2e8f0;color:#334155;">ADD-ONLY</span>'; ?></td>
                                <td><?php echo !empty($pu['enabled']) ? '<span class="tag tag-ok">ENABLED</span>' : '<span class="tag tag-bad">DISABLED</span>'; ?></td>
                                <td><?php echo h((string)$pu['updated_at']); ?></td>
                                <td>
                                    <form method="post" class="row">
                                        <input type="hidden" name="action" value="panel_user_update">
                                        <input type="hidden" name="username" value="<?php echo h((string)$pu['username']); ?>">
                                        <select name="role">
                                            <option value="add_only" <?php echo ((string)$pu['role'] === 'add_only') ? 'selected' : ''; ?>>Add-Only</option>
                                            <option value="admin" <?php echo ((string)$pu['role'] === 'admin') ? 'selected' : ''; ?>>Admin</option>
                                        </select>
                                        <label><input type="checkbox" name="enabled" <?php echo !empty($pu['enabled']) ? 'checked' : ''; ?>> enabled</label>
                                        <input type="password" name="password" placeholder="new password (optional)">
                                        <button class="btn-pri" type="submit">Update</button>
                                    </form>
                                    <?php if (strtolower($current_user) !== strtolower((string)$pu['username'])): ?>
                                    <form method="post" style="margin-top:6px;">
                                        <input type="hidden" name="action" value="panel_user_remove">
                                        <input type="hidden" name="username" value="<?php echo h((string)$pu['username']); ?>">
                                        <button class="btn-bad" type="submit">Remove</button>
                                    </form>
                                    <?php else: ?>
                                    <div class="mono" style="margin-top:6px;">Current session user</div>
                                    <?php endif; ?>
                                </td>
                            </tr>
                        <?php endwhile; ?>
                        </tbody>
                    </table>
                </div>
            </section>

            <section class="card third" data-tab="access">
                <h2>Ban Grid</h2>
                <form method="post" class="row">
                    <input type="hidden" name="action" value="add_banned_grid">
                    <input type="text" name="gridname" placeholder="badgrid.example:8002" required>
                    <input type="text" name="reason" placeholder="Reason">
                    <button class="btn-bad" type="submit">Save</button>
                </form>
            </section>

            <section class="card third" data-tab="access">
                <h2>Partner Grid</h2>
                <form method="post" class="row">
                    <input type="hidden" name="action" value="add_partner_grid">
                    <input type="text" name="gridname" placeholder="partner.example:8002" required>
                    <input type="text" name="note" placeholder="Note">
                    <button class="btn-ok" type="submit">Save</button>
                </form>
            </section>

            <section class="card third" data-tab="access">
                <h2>User Sync</h2>
                <p>Sync local users from robust.UserAccounts.</p>
                <button class="btn-pri" onclick="fetch('sync_users.php').then(r => r.text()).then(t => document.getElementById('syncResult').textContent = t)">Run Sync</button>
                <pre id="syncResult" style="margin-top:10px;max-height:180px;overflow:auto;background:#0f172a;color:#e2e8f0;padding:8px;border-radius:10px;"></pre>
            </section>

            <section class="card" data-tab="access">
                <h2>Region Restart ACL</h2>
                <p>Assign who can schedule/cancel region restarts through API. Owner can keep direct control and grant selected people.</p>
                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="restart_acl_add">
                    <input type="text" name="opensim_uuid" placeholder="User UUID" required>
                    <input type="text" name="display_name" placeholder="Display name">
                    <input type="text" name="api_key" placeholder="API key" required>
                    <label><input type="checkbox" name="can_restart" checked> restart</label>
                    <label><input type="checkbox" name="can_cancel" checked> cancel</label>
                    <label><input type="checkbox" name="enabled" checked> enabled</label>
                    <button class="btn-ok" type="submit">Add ACL User</button>
                </form>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>Name</th><th>UUID</th><th>API Key</th><th>Permissions</th><th>Updated</th><th>Actions</th></tr></thead>
                        <tbody>
                        <?php while ($acl = $restart_acl->fetch_assoc()): ?>
                            <tr>
                                <td><input type="text" form="acl_update_<?php echo h((string)$acl['opensim_uuid']); ?>" name="display_name" value="<?php echo h((string)$acl['display_name']); ?>"></td>
                                <td class="mono"><?php echo h((string)$acl['opensim_uuid']); ?></td>
                                <td class="mono">(hidden) <input type="text" form="acl_update_<?php echo h((string)$acl['opensim_uuid']); ?>" name="api_key" placeholder="new key (optional)"></td>
                                <td>
                                    <label><input type="checkbox" form="acl_update_<?php echo h((string)$acl['opensim_uuid']); ?>" name="can_restart" <?php echo !empty($acl['can_restart']) ? 'checked' : ''; ?>> restart</label>
                                    <label><input type="checkbox" form="acl_update_<?php echo h((string)$acl['opensim_uuid']); ?>" name="can_cancel" <?php echo !empty($acl['can_cancel']) ? 'checked' : ''; ?>> cancel</label>
                                    <label><input type="checkbox" form="acl_update_<?php echo h((string)$acl['opensim_uuid']); ?>" name="enabled" <?php echo !empty($acl['enabled']) ? 'checked' : ''; ?>> enabled</label>
                                </td>
                                <td><?php echo h((string)$acl['updated_at']); ?></td>
                                <td>
                                    <div class="row">
                                        <form method="post" id="acl_update_<?php echo h((string)$acl['opensim_uuid']); ?>">
                                            <input type="hidden" name="action" value="restart_acl_update">
                                            <input type="hidden" name="opensim_uuid" value="<?php echo h((string)$acl['opensim_uuid']); ?>">
                                            <button class="btn-pri" type="submit">Update</button>
                                        </form>
                                        <form method="post">
                                            <input type="hidden" name="action" value="restart_acl_remove">
                                            <input type="hidden" name="opensim_uuid" value="<?php echo h((string)$acl['opensim_uuid']); ?>">
                                            <button class="btn-bad" type="submit">Remove</button>
                                        </form>
                                    </div>
                                </td>
                            </tr>
                        <?php endwhile; ?>
                        </tbody>
                    </table>
                </div>
            </section>

            <?php if ($is_admin): ?>
            <section class="card" data-tab="self">
                <h2>Self-Service Addon ACL</h2>
                <p>UUID-based login via IM OTP. No access unless an enabled ACL entry exists. This module is separate from remote admin ACL.</p>
                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="self_acl_add">
                    <input type="text" name="self_avatar_uuid" placeholder="Avatar UUID" required>
                    <input type="text" name="self_display_name" placeholder="Display name">
                    <label><input type="checkbox" name="self_enabled" checked> enabled</label>
                    <label><input type="checkbox" name="self_can_restart_region"> restart</label>
                    <label><input type="checkbox" name="self_can_backup_region"> region OAR</label>
                    <label><input type="checkbox" name="self_can_export_iar"> IAR</label>
                    <label><input type="checkbox" name="self_can_hg_add_user"> HG allow</label>
                    <label><input type="checkbox" name="self_can_panel_admin"> panel admin</label>
                    <input type="text" name="self_allowed_regions" placeholder="Allowed region UUIDs (comma/newline)">
                    <input type="text" name="self_allowed_avatars" placeholder="Allowed avatar UUIDs for IAR (comma/newline)">
                    <button class="btn-ok" type="submit">Add ACL</button>
                </form>

                <div class="table-wrap">
                    <table>
                        <thead><tr><th>Name</th><th>UUID</th><th>Status</th><th>Perms</th><th>Allowed Regions</th><th>Allowed Avatars (IAR)</th><th>Updated</th><th>Actions</th></tr></thead>
                        <tbody>
                        <?php if ($selfservice_acl_rows): ?>
                            <?php while ($sa = $selfservice_acl_rows->fetch_assoc()): ?>
                                <tr>
                                    <td><input type="text" form="self_acl_update_<?php echo h((string)$sa['avatar_uuid']); ?>" name="self_display_name" value="<?php echo h((string)$sa['display_name']); ?>"></td>
                                    <td class="mono"><?php echo h((string)$sa['avatar_uuid']); ?></td>
                                    <td><?php echo !empty($sa['enabled']) ? '<span class="tag tag-ok">ENABLED</span>' : '<span class="tag tag-bad">DISABLED</span>'; ?></td>
                                    <td>
                                        <label><input type="checkbox" form="self_acl_update_<?php echo h((string)$sa['avatar_uuid']); ?>" name="self_can_restart_region" <?php echo !empty($sa['can_restart_region']) ? 'checked' : ''; ?>> restart</label>
                                        <label><input type="checkbox" form="self_acl_update_<?php echo h((string)$sa['avatar_uuid']); ?>" name="self_can_backup_region" <?php echo !empty($sa['can_backup_region']) ? 'checked' : ''; ?>> OAR</label>
                                        <label><input type="checkbox" form="self_acl_update_<?php echo h((string)$sa['avatar_uuid']); ?>" name="self_can_export_iar" <?php echo !empty($sa['can_export_iar']) ? 'checked' : ''; ?>> IAR</label>
                                        <label><input type="checkbox" form="self_acl_update_<?php echo h((string)$sa['avatar_uuid']); ?>" name="self_can_hg_add_user" <?php echo !empty($sa['can_hg_add_user']) ? 'checked' : ''; ?>> HG allow</label>
                                        <label><input type="checkbox" form="self_acl_update_<?php echo h((string)$sa['avatar_uuid']); ?>" name="self_can_panel_admin" <?php echo !empty($sa['can_panel_admin']) ? 'checked' : ''; ?>> panel admin</label>
                                        <label><input type="checkbox" form="self_acl_update_<?php echo h((string)$sa['avatar_uuid']); ?>" name="self_enabled" <?php echo !empty($sa['enabled']) ? 'checked' : ''; ?>> enabled</label>
                                    </td>
                                    <td><textarea form="self_acl_update_<?php echo h((string)$sa['avatar_uuid']); ?>" name="self_allowed_regions" style="min-height:90px"><?php echo h((string)$sa['allowed_regions']); ?></textarea></td>
                                    <td><textarea form="self_acl_update_<?php echo h((string)$sa['avatar_uuid']); ?>" name="self_allowed_avatars" style="min-height:90px"><?php echo h((string)$sa['allowed_avatar_uuids']); ?></textarea></td>
                                    <td><?php echo h((string)$sa['updated_at']); ?></td>
                                    <td>
                                        <div class="row">
                                            <form method="post" id="self_acl_update_<?php echo h((string)$sa['avatar_uuid']); ?>">
                                                <input type="hidden" name="action" value="self_acl_update">
                                                <input type="hidden" name="self_avatar_uuid" value="<?php echo h((string)$sa['avatar_uuid']); ?>">
                                                <button class="btn-pri" type="submit">Update</button>
                                            </form>
                                            <form method="post" onsubmit="return confirm('Remove self-service ACL for <?php echo h((string)$sa['avatar_uuid']); ?>?');">
                                                <input type="hidden" name="action" value="self_acl_remove">
                                                <input type="hidden" name="self_avatar_uuid" value="<?php echo h((string)$sa['avatar_uuid']); ?>">
                                                <button class="btn-bad" type="submit">Remove</button>
                                            </form>
                                        </div>
                                    </td>
                                </tr>
                            <?php endwhile; ?>
                        <?php else: ?>
                            <tr><td colspan="8">No self-service ACL entries yet.</td></tr>
                        <?php endif; ?>
                        </tbody>
                    </table>
                </div>
            </section>
            <?php endif; ?>

            <?php if ($is_admin): ?>
            <section class="card" data-tab="rolling">
                <h2>Rolling Operations</h2>
                <p>Run rolling actions one-by-one by delay. This subpage is for batch region operations.</p>
                <div class="rolling-hub" style="margin-bottom:10px;">
                    <div class="rolling-strip"></div>
                    <div class="mono">Animated queue mode: active when this subpage is selected.</div>
                </div>

                <h3 style="margin-top:6px;">Rolling Restart</h3>
                <p>Set first delay and delay between each region. Use abort to cancel queued/scheduled restarts.</p>
                <form method="post" class="row" style="margin-bottom:10px;">
                    <select name="rolling_target_mode" style="min-width:180px;">
                        <option value="all">All regions (A-Z)</option>
                        <option value="selected">Selected UUID list</option>
                    </select>
                    <input type="text" name="rolling_region_uuids" placeholder="Region UUIDs (comma/newline) for selected mode" style="min-width:420px;max-width:100%;">
                    <input type="number" name="rolling_first_delay_seconds" value="180" min="0" max="604800" style="width:140px;" placeholder="First delay">
                    <input type="number" name="rolling_gap_seconds" value="180" min="10" max="86400" style="width:140px;" placeholder="Gap seconds">
                    <input type="text" name="rolling_reason" value="Rolling restart" placeholder="Reason" style="min-width:220px;">
                    <input type="text" name="rolling_abort_reason" value="Rolling abort" placeholder="Abort reason" style="min-width:220px;">
                    <input type="text" name="rolling_requested_by" placeholder="Requested by (optional)" style="min-width:220px;">
                    <button class="btn-pri" type="submit" name="action" value="rolling_restart_schedule">Schedule Rolling Restart</button>
                    <button class="btn-bad" type="submit" name="action" value="rolling_restart_abort" onclick="return confirm('Abort scheduled restarts for selected regions?');">Abort Rolling Restart</button>
                </form>

                <h3 style="margin-top:10px;">Rolling Shutdown</h3>
                <p>Sends <code>q</code> to region terminal, stops HealthCheck, then verifies endpoint port is down.</p>
                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="rolling_shutdown_execute">
                    <select name="rolling_target_mode" style="min-width:180px;">
                        <option value="all">All regions (A-Z)</option>
                        <option value="selected">Selected UUID list</option>
                    </select>
                    <input type="text" name="rolling_region_uuids" placeholder="Region UUIDs (comma/newline) for selected mode" style="min-width:420px;max-width:100%;">
                    <input type="number" name="rolling_first_delay_seconds" value="0" min="0" max="3600" style="width:140px;" placeholder="First delay">
                    <input type="number" name="rolling_gap_seconds" value="30" min="1" max="600" style="width:140px;" placeholder="Gap seconds">
                    <input type="number" name="rolling_shutdown_wait_seconds" value="30" min="3" max="120" style="width:160px;" placeholder="Wait port-off sec">
                    <button class="btn-bad" type="submit" onclick="return confirm('Send q shutdown one by one and stop HealthCheck?');">Rolling Shutdown (q)</button>
                </form>
            </section>
            <?php endif; ?>

            <section class="card" data-tab="regions">
                <h2>Region List and Restart Status</h2>
                <p>Use Status to check each region. Default restart delay is 180 seconds (3 minutes).</p>
                <div class="row" style="margin-bottom:10px;">
                    <input id="rrRequester" type="text" placeholder="Requester UUID (owner/ACL user)" style="min-width:340px;">
                    <input id="rrApiKey" type="text" placeholder="Requester API key (optional for panel admin)" style="min-width:240px;">
                    <button class="btn-soft" type="button" id="rrCheckAll">Check All Status</button>
                </div>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>Region</th><th>UUID</th><th>Server URI</th><th>Last Seen</th><th>Delay</th><th>Reason</th><th>Actions</th></tr></thead>
                        <tbody>
                        <?php while ($r = $restart_regions->fetch_assoc()): ?>
                            <tr>
                                <td><?php echo h((string)$r['regionName']); ?></td>
                                <td class="mono"><?php echo h((string)$r['uuid']); ?></td>
                                <td class="mono"><?php echo h((string)$r['serverURI']); ?></td>
                                <td class="mono"><?php echo h((string)($r['last_seen'] ?? '')); ?></td>
                                <td><input id="delay_<?php echo h((string)$r['uuid']); ?>" type="text" value="180" style="width:90px;"></td>
                                <td><input id="reason_<?php echo h((string)$r['uuid']); ?>" type="text" placeholder="Maintenance"></td>
                                <td>
                                    <div class="row">
                                        <button
                                            class="btn-pri restart-action-btn"
                                            type="button"
                                            data-action="schedule"
                                            data-region-uuid="<?php echo h((string)$r['uuid']); ?>"
                                            data-region-name="<?php echo h((string)$r['regionName']); ?>"
                                        >Restart in 3m</button>
                                        <button
                                            class="btn-bad restart-action-btn"
                                            type="button"
                                            data-action="cancel"
                                            data-region-uuid="<?php echo h((string)$r['uuid']); ?>"
                                            data-region-name="<?php echo h((string)$r['regionName']); ?>"
                                        >Cancel</button>
                                        <button
                                            class="btn-soft restart-action-btn"
                                            type="button"
                                            data-action="status"
                                            data-region-uuid="<?php echo h((string)$r['uuid']); ?>"
                                            data-region-name="<?php echo h((string)$r['regionName']); ?>"
                                        >Status</button>
                                    </div>
                                </td>
                            </tr>
                        <?php endwhile; ?>
                        </tbody>
                    </table>
                </div>
                <pre id="restartResult" style="margin-top:10px;max-height:240px;overflow:auto;background:#0f172a;color:#e2e8f0;padding:8px;border-radius:10px;"></pre>
            </section>

            <section class="card" data-tab="regions">
                <h2>Region Auth Overrides</h2>
                <p>Lift selected restrictions per region. Use this to make specific regions public while still keeping ban-based controls.</p>
                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="region_auth_override_save">
                    <select name="region_uuid" required style="min-width:340px;max-width:100%;">
                        <option value="">Select region...</option>
                        <?php if ($region_options): ?>
                            <?php while ($ro = $region_options->fetch_assoc()): ?>
                                <option value="<?php echo h((string)$ro['uuid']); ?>"><?php echo h((string)$ro['regionName']); ?> (<?php echo h((string)$ro['uuid']); ?>)</option>
                            <?php endwhile; ?>
                        <?php endif; ?>
                    </select>
                    <input type="text" name="region_name" placeholder="Region name label (optional)">
                    <label><input type="checkbox" name="enabled" checked> enabled</label>
                    <label><input type="checkbox" name="bypass_maintenance"> bypass maintenance</label>
                    <label><input type="checkbox" name="bypass_viewer_block"> bypass viewer block</label>
                    <label><input type="checkbox" name="public_guest_access"> public guest access</label>
                    <input type="text" name="note" placeholder="Note">
                    <button class="btn-pri" type="submit">Save Override</button>
                </form>

                <div class="table-wrap">
                    <table>
                        <thead><tr><th>Region</th><th>UUID</th><th>Enabled</th><th>Bypass Maintenance</th><th>Bypass Viewer</th><th>Public Guest</th><th>Note</th><th>Updated</th><th>Actions</th></tr></thead>
                        <tbody>
                        <?php if ($region_auth_overrides): ?>
                            <?php while ($ov = $region_auth_overrides->fetch_assoc()): ?>
                                <tr>
                                    <td><?php echo h((string)$ov['region_name']); ?></td>
                                    <td class="mono"><?php echo h((string)$ov['region_uuid']); ?></td>
                                    <td><?php echo !empty($ov['enabled']) ? 'yes' : 'no'; ?></td>
                                    <td><?php echo !empty($ov['bypass_maintenance']) ? 'yes' : 'no'; ?></td>
                                    <td><?php echo !empty($ov['bypass_viewer_block']) ? 'yes' : 'no'; ?></td>
                                    <td><?php echo !empty($ov['public_guest_access']) ? 'yes' : 'no'; ?></td>
                                    <td><?php echo h((string)$ov['note']); ?></td>
                                    <td class="mono"><?php echo h((string)$ov['updated_at']); ?></td>
                                    <td>
                                        <div class="row">
                                            <form method="post" class="row" style="margin-bottom:6px;">
                                                <input type="hidden" name="action" value="region_auth_override_save">
                                                <input type="hidden" name="region_uuid" value="<?php echo h((string)$ov['region_uuid']); ?>">
                                                <input type="text" name="region_name" value="<?php echo h((string)$ov['region_name']); ?>" placeholder="Region name label">
                                                <label><input type="checkbox" name="enabled" <?php echo !empty($ov['enabled']) ? 'checked' : ''; ?>> enabled</label>
                                                <label><input type="checkbox" name="bypass_maintenance" <?php echo !empty($ov['bypass_maintenance']) ? 'checked' : ''; ?>> maintenance</label>
                                                <label><input type="checkbox" name="bypass_viewer_block" <?php echo !empty($ov['bypass_viewer_block']) ? 'checked' : ''; ?>> viewer</label>
                                                <label><input type="checkbox" name="public_guest_access" <?php echo !empty($ov['public_guest_access']) ? 'checked' : ''; ?>> public</label>
                                                <input type="text" name="note" value="<?php echo h((string)$ov['note']); ?>" placeholder="Note">
                                                <button class="btn-soft" type="submit">Update</button>
                                            </form>
                                            <form method="post" onsubmit="return confirm('Remove region auth override for <?php echo h((string)$ov['region_name']); ?>?');">
                                                <input type="hidden" name="action" value="region_auth_override_remove">
                                                <input type="hidden" name="region_uuid" value="<?php echo h((string)$ov['region_uuid']); ?>">
                                                <button class="btn-bad" type="submit">Remove</button>
                                            </form>
                                        </div>
                                    </td>
                                </tr>
                            <?php endwhile; ?>
                        <?php else: ?>
                            <tr><td colspan="9">No region auth overrides configured.</td></tr>
                        <?php endif; ?>
                        </tbody>
                    </table>
                </div>
            </section>

            <section class="card" data-tab="backup">
                <h2>Archives (OAR / IAR)</h2>
                <p>Backup tab uses simulator console bridge. Full admin export (all regions / all avatars), plus RW console.</p>
                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="backup_admin_region_oar">
                    <select name="backup_region_uuid" required style="min-width:340px;max-width:100%;">
                        <option value="">Select region for OAR...</option>
                        <?php foreach ($backup_region_map as $uuid => $name): ?>
                            <option value="<?php echo h($uuid); ?>" <?php echo $backup_console_region_uuid === $uuid ? 'selected' : ''; ?>><?php echo h($name . ' (' . $uuid . ')'); ?></option>
                        <?php endforeach; ?>
                    </select>
                    <input type="text" name="backup_filename" placeholder="Optional OAR filename (default auto)">
                    <label><input type="checkbox" name="backup_noassets"> noassets</label>
                    <label><input type="checkbox" name="backup_publish"> publish</label>
                    <button class="btn-pri" type="submit">Export OAR</button>
                </form>

                <form method="post" class="row">
                    <input type="hidden" name="action" value="backup_admin_avatar_iar">
                    <select name="backup_avatar_uuid" required style="min-width:340px;max-width:100%;">
                        <option value="">Select avatar for IAR...</option>
                        <?php foreach ($backup_avatar_map as $uuid => $name): ?>
                            <option value="<?php echo h($uuid); ?>"><?php echo h($name . ' (' . $uuid . ')'); ?></option>
                        <?php endforeach; ?>
                    </select>
                    <select name="backup_region_uuid" style="min-width:340px;max-width:100%;">
                        <option value="">Auto (first region)</option>
                        <?php foreach ($backup_region_map as $uuid => $name): ?>
                            <option value="<?php echo h($uuid); ?>"><?php echo h($name . ' (' . $uuid . ')'); ?></option>
                        <?php endforeach; ?>
                    </select>
                    <input type="text" name="backup_inv_path" placeholder="Inventory path (default /)" value="/">
                    <input type="text" name="backup_filename" placeholder="Optional IAR filename (default auto)">
                    <label><input type="checkbox" name="backup_noassets"> noassets</label>
                    <button class="btn-pri" type="submit">Export IAR</button>
                </form>

                <h3 style="margin-top:14px;">Region Console (RW)</h3>
                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="backup_console_exec">
                    <select name="backup_console_region_uuid" required style="min-width:340px;max-width:100%;">
                        <option value="">Select region...</option>
                        <?php foreach ($backup_region_map as $uuid => $name): ?>
                            <option value="<?php echo h($uuid); ?>" <?php echo $backup_console_region_uuid === $uuid ? 'selected' : ''; ?>><?php echo h($name . ' (' . $uuid . ')'); ?></option>
                        <?php endforeach; ?>
                    </select>
                    <input type="text" name="backup_console_command" placeholder="OpenSim console command" value="<?php echo h($backup_console_command); ?>" style="min-width:420px;">
                    <input type="number" name="backup_console_lines" min="20" max="500" value="<?php echo h((string)$backup_console_lines); ?>" style="width:120px;">
                    <button class="btn-bad" type="submit">Send RW Command</button>
                </form>
                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="backup_console_view">
                    <select name="backup_console_region_uuid" required style="min-width:340px;max-width:100%;">
                        <option value="">Select region...</option>
                        <?php foreach ($backup_region_map as $uuid => $name): ?>
                            <option value="<?php echo h($uuid); ?>" <?php echo $backup_console_region_uuid === $uuid ? 'selected' : ''; ?>><?php echo h($name . ' (' . $uuid . ')'); ?></option>
                        <?php endforeach; ?>
                    </select>
                    <input type="number" name="backup_console_lines" min="20" max="500" value="<?php echo h((string)$backup_console_lines); ?>" style="width:120px;">
                    <button class="btn-soft" type="submit">Read Console (RO)</button>
                </form>
                <pre style="margin-top:8px;max-height:260px;overflow:auto;background:#0f172a;color:#e2e8f0;padding:8px;border-radius:10px;"><?php echo h($backup_console_output !== '' ? $backup_console_output : 'No backup console output yet.'); ?></pre>

                <h3 style="margin-top:14px;">Download OAR</h3>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>File</th><th>Size</th><th>Modified</th><th>Action</th></tr></thead>
                        <tbody>
                        <?php if (!empty($archive_files_oar)): ?>
                            <?php foreach (array_slice($archive_files_oar, 0, 120) as $af): ?>
                                <tr>
                                    <td class="mono"><?php echo h((string)$af['rel']); ?></td>
                                    <td><?php echo h((string)number_format(((int)$af['size']) / 1048576, 2)); ?> MB</td>
                                    <td class="mono"><?php echo h(gmdate('Y-m-d H:i:s', (int)$af['mtime'])); ?> UTC</td>
                                    <td>
                                        <div class="row">
                                            <a class="btn-soft" href="?download_archive=1&amp;type=oar&amp;file=<?php echo rawurlencode((string)$af['rel']); ?>">Download</a>
                                            <form method="post" onsubmit="return confirm('Remove this OAR file?');">
                                                <input type="hidden" name="action" value="archive_delete_file">
                                                <input type="hidden" name="archive_type" value="oar">
                                                <input type="hidden" name="archive_file" value="<?php echo h((string)$af['rel']); ?>">
                                                <button class="btn-bad" type="submit">Remove</button>
                                            </form>
                                        </div>
                                    </td>
                                </tr>
                            <?php endforeach; ?>
                        <?php else: ?>
                            <tr><td colspan="4">No OAR files found.</td></tr>
                        <?php endif; ?>
                        </tbody>
                    </table>
                </div>

                <h3 style="margin-top:14px;">Download IAR</h3>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>File</th><th>Size</th><th>Modified</th><th>Action</th></tr></thead>
                        <tbody>
                        <?php if (!empty($archive_files_iar)): ?>
                            <?php foreach (array_slice($archive_files_iar, 0, 120) as $af): ?>
                                <tr>
                                    <td class="mono"><?php echo h((string)$af['rel']); ?></td>
                                    <td><?php echo h((string)number_format(((int)$af['size']) / 1048576, 2)); ?> MB</td>
                                    <td class="mono"><?php echo h(gmdate('Y-m-d H:i:s', (int)$af['mtime'])); ?> UTC</td>
                                    <td>
                                        <div class="row">
                                            <a class="btn-soft" href="?download_archive=1&amp;type=iar&amp;file=<?php echo rawurlencode((string)$af['rel']); ?>">Download</a>
                                            <form method="post" onsubmit="return confirm('Remove this IAR file?');">
                                                <input type="hidden" name="action" value="archive_delete_file">
                                                <input type="hidden" name="archive_type" value="iar">
                                                <input type="hidden" name="archive_file" value="<?php echo h((string)$af['rel']); ?>">
                                                <button class="btn-bad" type="submit">Remove</button>
                                            </form>
                                        </div>
                                    </td>
                                </tr>
                            <?php endforeach; ?>
                        <?php else: ?>
                            <tr><td colspan="4">No IAR files found.</td></tr>
                        <?php endif; ?>
                        </tbody>
                    </table>
                </div>
            </section>

            <section class="card" data-tab="regions">
                <h2>Recent Region Restart API Calls</h2>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>When</th><th>Region</th><th>Action</th><th>By UUID</th><th>Delay</th><th>HTTP</th><th>Reason</th></tr></thead>
                        <tbody>
                        <?php while ($log = $restart_logs->fetch_assoc()): ?>
                            <tr>
                                <td><?php echo h((string)$log['created_at']); ?></td>
                                <td><?php echo h((string)$log['region_name']); ?></td>
                                <td><?php echo h((string)$log['action']); ?></td>
                                <td class="mono"><?php echo h((string)$log['requested_by_uuid']); ?></td>
                                <td><?php echo h((string)$log['delay_seconds']); ?></td>
                                <td><?php echo h((string)$log['http_status']); ?></td>
                                <td><?php echo h((string)$log['reason']); ?></td>
                            </tr>
                        <?php endwhile; ?>
                        </tbody>
                    </table>
                </div>
            </section>

            <section class="card" data-tab="infra">
                <h2>Container Control</h2>
                <p>Manage compose services used by <code>sh.sh</code>. Supports all services and one-by-one actions.</p>
                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="infra_console_view">
                    <select name="infra_console_service" required>
                        <option value="">Console service...</option>
                        <?php foreach ($infra_rows as $ir): ?>
                            <?php $svcName = (string)$ir['service']; ?>
                            <option value="<?php echo h($svcName); ?>" <?php echo $infra_console_service === $svcName ? 'selected' : ''; ?>><?php echo h($svcName); ?></option>
                        <?php endforeach; ?>
                    </select>
                    <input type="number" name="infra_console_lines" min="20" max="500" value="<?php echo h((string)$infra_console_lines); ?>" style="width:120px;">
                    <button class="btn-soft" type="submit">Read Console (RO)</button>
                </form>
                <pre style="margin-top:8px;max-height:260px;overflow:auto;background:#0f172a;color:#e2e8f0;padding:8px;border-radius:10px;"><?php echo h($infra_console_output !== '' ? $infra_console_output : 'No console snapshot loaded yet.'); ?></pre>

                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="infra_services_run">
                    <input type="hidden" name="infra_target" value="all">
                    <input type="hidden" name="infra_command" value="start">
                    <button class="btn-ok" type="submit">Start All</button>
                </form>
                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="infra_services_run">
                    <input type="hidden" name="infra_target" value="all">
                    <input type="hidden" name="infra_command" value="restart">
                    <button class="btn-pri" type="submit">Restart All</button>
                </form>
                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="infra_healthcheck_run">
                    <button class="btn-soft" type="submit">Start HealthCheck (sh.sh)</button>
                </form>
                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="infra_healthcheck_region">
                    <select name="infra_health_region_uuid" required style="min-width:340px;max-width:100%;">
                        <option value="">Select region for HealthCheck...</option>
                        <?php foreach ($backup_region_map as $uuid => $name): ?>
                            <option value="<?php echo h($uuid); ?>"><?php echo h($name . ' (' . $uuid . ')'); ?></option>
                        <?php endforeach; ?>
                    </select>
                    <button class="btn-soft" type="submit">Start HealthCheck (Region)</button>
                </form>
                <form method="post" class="row">
                    <input type="hidden" name="action" value="infra_services_run">
                    <input type="hidden" name="infra_target" value="all">
                    <input type="hidden" name="infra_command" value="status">
                    <button class="btn-soft" type="submit">Status All</button>
                </form>

                <div class="table-wrap" style="margin-top:10px;">
                    <table>
                        <thead><tr><th>Service</th><th>Container</th><th>Status</th><th>Actions</th></tr></thead>
                        <tbody>
                        <?php foreach ($infra_rows as $ir): ?>
                            <tr>
                                <td class="mono"><?php echo h((string)$ir['service']); ?></td>
                                <td class="mono"><?php echo h((string)$ir['container']); ?></td>
                                <td><?php echo h((string)$ir['status']); ?></td>
                                <td>
                                    <div class="row">
                                        <form method="post">
                                            <input type="hidden" name="action" value="infra_services_run">
                                            <input type="hidden" name="infra_target" value="<?php echo h((string)$ir['service']); ?>">
                                            <input type="hidden" name="infra_command" value="start">
                                            <button class="btn-ok" type="submit">Start</button>
                                        </form>
                                        <form method="post">
                                            <input type="hidden" name="action" value="infra_services_run">
                                            <input type="hidden" name="infra_target" value="<?php echo h((string)$ir['service']); ?>">
                                            <input type="hidden" name="infra_command" value="restart">
                                            <button class="btn-pri" type="submit">Restart</button>
                                        </form>
                                        <form method="post">
                                            <input type="hidden" name="action" value="infra_services_run">
                                            <input type="hidden" name="infra_target" value="<?php echo h((string)$ir['service']); ?>">
                                            <input type="hidden" name="infra_command" value="stop">
                                            <button class="btn-bad" type="submit">Stop</button>
                                        </form>
                                        <form method="post">
                                            <input type="hidden" name="action" value="infra_services_run">
                                            <input type="hidden" name="infra_target" value="<?php echo h((string)$ir['service']); ?>">
                                            <input type="hidden" name="infra_command" value="status">
                                            <button class="btn-soft" type="submit">Status</button>
                                        </form>
                                    </div>
                                </td>
                            </tr>
                        <?php endforeach; ?>
                        </tbody>
                    </table>
                </div>
            </section>

            <section class="card half" data-tab="infra">
                <h2>Region Plan: Add</h2>
                <p>Prepare and execute a full region add plan: auto-pick next free HTTP/SSH ports, auto-pick a base region template from current config, patch per-region values, keep global config as-is.</p>
                <p class="mono">Auto next: HTTP <?php echo h((string)$infra_next_http_port); ?>, SSH <?php echo h((string)$infra_next_ssh_port); ?>, Base template <?php echo h((string)$infra_auto_template); ?></p>
                <form method="post">
                    <input type="hidden" name="action" value="infra_plan_add_region">
                    <div class="row" style="margin-bottom:8px;">
                        <input type="text" name="region_name" placeholder="New Region Name" value="martynka" required>
                        <input type="text" name="template_region" placeholder="Optional base region (auto if blank)">
                    </div>
                    <div class="row" style="margin-bottom:8px;">
                        <input type="text" name="region_http_port" placeholder="HTTP Port (auto next if blank)">
                        <input type="text" name="region_ssh_port" placeholder="SSH Port (auto next if blank)">
                    </div>
                    <label><input type="checkbox" name="start_now" checked> start service after execute</label>
                    <div style="height:8px;"></div>
                    <button class="btn-pri" type="submit">Prepare Add Plan</button>
                </form>
            </section>

            <section class="card half" data-tab="infra">
                <h2>Region Plan: Remove</h2>
                <p>Prepare and execute region removal: stop service, remove compose/service/region/ssh files, and remove <code>sh.sh</code> mapping.</p>
                <form method="post" class="row" style="margin-bottom:8px;">
                    <input type="hidden" name="action" value="infra_plan_remove_region">
                    <input type="text" name="remove_region_name" placeholder="Region Name" required>
                    <button class="btn-bad" type="submit">Prepare Remove Plan</button>
                </form>
                <?php if ($infra_plan): ?>
                    <div class="mono" style="background:#0f172a;color:#e2e8f0;padding:10px;border-radius:10px;white-space:pre-wrap;">
Plan Type: <?php echo h((string)$infra_plan['type']); ?>
Region: <?php echo h((string)($infra_plan['region_name'] ?? '')); ?>
Service: <?php echo h((string)($infra_plan['service_name'] ?? '')); ?>
Compose: <?php echo h((string)($infra_plan['compose_file'] ?? '')); ?>
                    </div>
                    <div style="height:8px;"></div>
                    <form method="post">
                        <input type="hidden" name="action" value="infra_execute_plan">
                        <button class="btn-ok" type="submit">Execute Plan</button>
                    </form>
                <?php else: ?>
                    <p>No plan prepared yet.</p>
                <?php endif; ?>
            </section>

            <section class="card" data-tab="infra">
                <h2>Config File Editor</h2>
                <p>Edit per-region and global configs: compose files, services configs, region Opensim configs, GridCommon/global ini files, <code>sh.sh</code>, and <code>gen2.sh</code>.</p>
                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="infra_editor_load">
                    <select name="edit_path" style="min-width:420px;max-width:100%;">
                        <?php foreach ($infra_editable_files as $editablePath): ?>
                            <option value="<?php echo h((string)$editablePath); ?>" <?php echo $infra_edit_path === (string)$editablePath ? 'selected' : ''; ?>><?php echo h((string)$editablePath); ?></option>
                        <?php endforeach; ?>
                    </select>
                    <button class="btn-soft" type="submit">Load</button>
                </form>

                <form method="post">
                    <input type="hidden" name="action" value="infra_editor_save">
                    <input type="text" name="edit_path" value="<?php echo h($infra_edit_path); ?>" placeholder="Loaded file path" style="width:100%;margin-bottom:8px;">
                    <textarea name="edit_content" style="min-height:360px;font-family:ui-monospace, SFMono-Regular, Menlo, monospace;white-space:pre;"><?php echo h($infra_edit_content); ?></textarea>
                    <div style="height:8px;"></div>
                    <button class="btn-pri" type="submit">Save File</button>
                </form>
            </section>

            <section class="card" data-tab="presence">
                <h2>Presence Health</h2>
                <p>Find and clean stale online flags (users shown online while not actually present in a region).</p>
                <div class="row" style="margin-bottom:10px;">
                    <span class="tag" style="background:#e2e8f0;color:#334155;">GridUser online flags: <?php echo h((string)$presence_online_count); ?></span>
                    <span class="tag tag-ok">Active presence (RegionID set): <?php echo h((string)$presence_active_count); ?></span>
                    <span class="tag tag-bad">Stale online records: <?php echo h((string)$presence_stale_count); ?></span>
                </div>
                <form method="post" class="row">
                    <input type="hidden" name="action" value="presence_fix_stale">
                    <button class="btn-bad" type="submit">Fix Stale Online Flags</button>
                </form>
            </section>

            <section class="card" data-tab="presence">
                <h2>Stale Online Records</h2>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>Name</th><th>UUID</th><th>GridUser Last Region</th><th>Presence Region</th><th>Presence LastSeen</th><th>Action</th></tr></thead>
                        <tbody>
                        <?php $hasStaleRows = false; ?>
                        <?php if ($presence_stale_rows): ?>
                            <?php while ($ps = $presence_stale_rows->fetch_assoc()): ?>
                                <?php $hasStaleRows = true; $staleName = trim((string)$ps['first_name'] . ' ' . (string)$ps['last_name']); ?>
                                <tr>
                                    <td><?php echo h($staleName === '' ? '(unnamed)' : $staleName); ?></td>
                                    <td class="mono"><?php echo h((string)$ps['uuid']); ?></td>
                                    <td><?php echo h((string)$ps['grid_region_name']); ?></td>
                                    <td><?php echo h((string)$ps['presence_region_name']); ?></td>
                                    <td><?php echo h((string)$ps['presence_last_seen']); ?></td>
                                    <td>
                                        <form method="post" class="row">
                                            <input type="hidden" name="action" value="presence_force_offline">
                                            <input type="hidden" name="uuid" value="<?php echo h((string)$ps['uuid']); ?>">
                                            <button class="btn-soft" type="submit">Force Offline</button>
                                        </form>
                                    </td>
                                </tr>
                            <?php endwhile; ?>
                        <?php endif; ?>
                        <?php if (!$hasStaleRows): ?>
                            <tr><td colspan="6">No stale online records found with current threshold.</td></tr>
                        <?php endif; ?>
                        </tbody>
                    </table>
                </div>
            </section>

            <section class="card" data-tab="presence">
                <h2>Active Presence (Reliable Online)</h2>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>Name</th><th>UUID</th><th>Region</th><th>LastSeen</th></tr></thead>
                        <tbody>
                        <?php $hasActiveRows = false; ?>
                        <?php if ($presence_active_rows): ?>
                            <?php while ($pa = $presence_active_rows->fetch_assoc()): ?>
                                <?php $hasActiveRows = true; $activeName = trim((string)$pa['first_name'] . ' ' . (string)$pa['last_name']); ?>
                                <tr>
                                    <td><?php echo h($activeName === '' ? '(unnamed)' : $activeName); ?></td>
                                    <td class="mono"><?php echo h((string)$pa['uuid']); ?></td>
                                    <td><?php echo h((string)$pa['presence_region_name']); ?></td>
                                    <td><?php echo h((string)$pa['presence_last_seen']); ?></td>
                                </tr>
                            <?php endwhile; ?>
                        <?php endif; ?>
                        <?php if (!$hasActiveRows): ?>
                            <tr><td colspan="4">No active presence users found for this threshold.</td></tr>
                        <?php endif; ?>
                        </tbody>
                    </table>
                </div>
            </section>

            <section class="card" data-tab="online">
                <h2>Users Online/Offline</h2>
                <p>All users from <code>robust.GridUser</code>. Online status is validated with <code>Presence.RegionID</code>.</p>
                <div class="row" style="margin-bottom:10px;">
                    <span class="tag" style="background:#e2e8f0;color:#334155;">Total users: <?php echo h((string)$online_users_total); ?></span>
                    <span class="tag tag-ok">Online now: <?php echo h((string)$online_users_live); ?></span>
                    <span class="tag" style="background:#f1f5f9;color:#334155;">Offline: <?php echo h((string)$online_users_offline); ?></span>
                    <span class="tag tag-bad">Stale flags: <?php echo h((string)$presence_stale_count); ?></span>
                </div>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>Name</th><th>UUID</th><th>Status</th><th>Region</th><th>Position</th><th>Last Login</th><th>Last Logout</th><th>Presence LastSeen</th></tr></thead>
                        <tbody>
                        <?php $hasOnlineRows = false; ?>
                        <?php if ($online_users_rows): ?>
                            <?php while ($ou = $online_users_rows->fetch_assoc()): ?>
                                <?php
                                    $hasOnlineRows = true;
                                    $ouUuid = (string)($ou['uuid'] ?? '');
                                    $ouName = trim((string)$ou['first_name'] . ' ' . (string)$ou['last_name']);
                                    if ($ouName === '') {
                                        if (strpos($ouUuid, ';') !== false) {
                                            $parts = explode(';', $ouUuid);
                                            $ouName = (string)end($parts);
                                        }
                                    }
                                    if ($ouName === '') {
                                        $ouName = '(unnamed)';
                                    }

                                    $ouPresenceRegionId = (string)($ou['presence_region_id'] ?? '');
                                    $ouIsLiveOnline = ((string)($ou['online_flag'] ?? '') === 'True') && ($ouPresenceRegionId !== '') && ($ouPresenceRegionId !== $presence_zero_uuid);
                                    $ouIsStale = ((string)($ou['online_flag'] ?? '') === 'True') && !$ouIsLiveOnline;

                                    $ouRegion = $ouIsLiveOnline ? (string)($ou['presence_region_name'] ?? '') : (string)($ou['grid_region_name'] ?? '');
                                    if ($ouRegion === '') {
                                        $ouRegion = '-';
                                    }

                                    $ouPosition = (string)($ou['last_position'] ?? '');
                                    if ($ouPosition === '') {
                                        $ouPosition = '-';
                                    }

                                    $ouLogin = (string)($ou['login_at'] ?? '');
                                    if ($ouLogin === '') {
                                        $ouLogin = '-';
                                    }

                                    $ouLogout = (string)($ou['logout_at'] ?? '');
                                    if ($ouLogout === '') {
                                        $ouLogout = '-';
                                    }

                                    $ouLastSeen = (string)($ou['presence_last_seen'] ?? '');
                                    if ($ouLastSeen === '') {
                                        $ouLastSeen = '-';
                                    }
                                ?>
                                <tr>
                                    <td><?php echo h($ouName); ?></td>
                                    <td class="mono"><?php echo h($ouUuid); ?></td>
                                    <td>
                                        <?php if ($ouIsLiveOnline): ?>
                                            <span class="tag tag-ok">ONLINE</span>
                                        <?php elseif ($ouIsStale): ?>
                                            <span class="tag tag-bad">STALE FLAG</span>
                                        <?php else: ?>
                                            <span class="tag" style="background:#f1f5f9;color:#334155;">OFFLINE</span>
                                        <?php endif; ?>
                                    </td>
                                    <td><?php echo h($ouRegion); ?></td>
                                    <td class="mono"><?php echo h($ouPosition); ?></td>
                                    <td><?php echo h($ouLogin); ?></td>
                                    <td><?php echo h($ouLogout); ?></td>
                                    <td><?php echo h($ouLastSeen); ?></td>
                                </tr>
                            <?php endwhile; ?>
                        <?php endif; ?>
                        <?php if (!$hasOnlineRows): ?>
                            <tr><td colspan="8">No user rows found in GridUser.</td></tr>
                        <?php endif; ?>
                        </tbody>
                    </table>
                </div>
            </section>

            <section class="card" data-tab="alerts">
                <h2>Alerts and IM</h2>
                <p>Send direct IM to one avatar or broadcast one alert to all estates/regions in one click.</p>

                <div class="row" style="align-items:flex-start;">
                    <div style="flex:1 1 460px; min-width: 320px;">
                        <h3 style="margin:0 0 8px;">Send IM to Someone</h3>
                        <form method="post">
                            <input type="hidden" name="action" value="alerts_send_im">
                            <div class="row" style="margin-bottom:8px;">
                                <input id="im_to_uuid" type="text" name="im_to_uuid" list="im_target_list" placeholder="Recipient UUID" required style="min-width:320px;">
                            </div>
                            <datalist id="im_target_list">
                                <?php if ($im_targets): ?>
                                    <?php while ($target = $im_targets->fetch_assoc()): ?>
                                        <?php $fullName = trim((string)$target['first_name'] . ' ' . (string)$target['last_name']); ?>
                                        <option value="<?php echo h((string)$target['uuid']); ?>" label="<?php echo h($fullName === '' ? '(unnamed)' : $fullName); ?>"></option>
                                    <?php endwhile; ?>
                                <?php endif; ?>
                            </datalist>
                            <div class="row" style="margin-bottom:8px;">
                                <input type="text" name="im_from_name" value="Grid System" placeholder="From name">
                                <input type="text" name="im_from_uuid" value="00000000-0000-0000-0000-000000000000" placeholder="From UUID (optional)">
                            </div>
                            <textarea name="im_message" placeholder="Message" required></textarea>
                            <div style="height:8px;"></div>
                            <button class="btn-pri" type="submit">Send IM</button>
                        </form>
                    </div>

                    <div style="flex:1 1 460px; min-width: 320px;">
                        <h3 style="margin:0 0 8px;">Broadcast Alert to All Regions</h3>
                        <form method="post">
                            <input type="hidden" name="action" value="alerts_send_all_regions">
                            <div class="row" style="margin-bottom:8px;">
                                <input type="text" name="alert_from_name" value="Grid System" placeholder="From name">
                                <input type="text" name="alert_from_uuid" value="00000000-0000-0000-0000-000000000000" placeholder="From UUID (optional)">
                            </div>
                            <textarea name="alert_message" placeholder="Alert shown in all regions" required></textarea>
                            <div style="height:8px;"></div>
                            <button class="btn-ok" type="submit">Send To All Regions</button>
                        </form>
                    </div>
                </div>

                <div style="height:12px;"></div>
                <h3 style="margin:0 0 8px;">Online Targets (quick fill)</h3>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>Name</th><th>UUID</th><th>Last Region</th><th>Action</th></tr></thead>
                        <tbody>
                        <?php if ($online_im_targets): ?>
                            <?php while ($online = $online_im_targets->fetch_assoc()): ?>
                                <?php $onlineName = trim((string)$online['first_name'] . ' ' . (string)$online['last_name']); ?>
                                <tr>
                                    <td><?php echo h($onlineName === '' ? '(unnamed)' : $onlineName); ?></td>
                                    <td class="mono"><?php echo h((string)$online['uuid']); ?></td>
                                    <td><?php echo h((string)$online['region_name']); ?></td>
                                    <td>
                                        <button type="button" class="btn-soft pick-im-target" data-uuid="<?php echo h((string)$online['uuid']); ?>">Use for IM</button>
                                    </td>
                                </tr>
                            <?php endwhile; ?>
                        <?php else: ?>
                            <tr><td colspan="4">Could not load online target list.</td></tr>
                        <?php endif; ?>
                        </tbody>
                    </table>
                </div>
            </section>

            <section class="card" data-tab="reports">
                <h2>Abuse Reports</h2>
                <p>Reports forwarded from simulator abuse tool to PHP endpoint and stored in <code>wp_igrid_abuse_reports</code>.</p>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>ID</th><th>Received</th><th>Reporter</th><th>Abuser UUID</th><th>Region</th><th>Summary</th><th>Details</th><th>Screenshot</th><th>Shot Status</th><th>Position</th><th>Action</th></tr></thead>
                        <tbody>
                        <?php $hasAbuseRows = false; ?>
                        <?php if ($abuse_reports): ?>
                            <?php while ($ar = $abuse_reports->fetch_assoc()): ?>
                                <?php
                                    $hasAbuseRows = true;
                                    $arRegion = (string)($ar['source_region_name'] ?? '');
                                    if ($arRegion === '') {
                                        $arRegion = (string)($ar['reported_region_name'] ?? '');
                                    }
                                    if ($arRegion === '') {
                                        $arRegion = '-';
                                    }
                                    $arReporter = trim((string)$ar['reporter_name']);
                                    if ($arReporter === '') {
                                        $arReporter = (string)($ar['reporter_uuid'] ?? '-');
                                    }
                                    $screenshotUuid = (string)($ar['screenshot_uuid'] ?? '');
                                    $screenshotOriginal = (string)($ar['screenshot_uuid_original'] ?? '');
                                    $screenshotStatus = (string)($ar['screenshot_status'] ?? '');

                                    $screenshotDisplayUuid = $screenshotUuid;
                                    if (!valid_uuid($screenshotDisplayUuid) || strtolower($screenshotDisplayUuid) === '00000000-0000-0000-0000-000000000000') {
                                        $screenshotDisplayUuid = $screenshotOriginal;
                                    }

                                    $hasScreenshot = valid_uuid($screenshotDisplayUuid) && strtolower($screenshotDisplayUuid) !== '00000000-0000-0000-0000-000000000000';
                                    $screenshotUrl = $hasScreenshot ? screenshot_asset_url($screenshotDisplayUuid) : '';
                                    $px = (string)($ar['position_x'] ?? '');
                                    $py = (string)($ar['position_y'] ?? '');
                                    $pz = (string)($ar['position_z'] ?? '');
                                    $pos = ($px !== '' || $py !== '' || $pz !== '') ? ($px . ', ' . $py . ', ' . $pz) : '-';
                                ?>
                                <tr>
                                    <td class="mono"><?php echo h((string)$ar['id']); ?></td>
                                    <td><?php echo h((string)$ar['received_at']); ?></td>
                                    <td><?php echo h($arReporter); ?></td>
                                    <td class="mono"><?php echo h((string)$ar['abuser_uuid']); ?></td>
                                    <td><?php echo h($arRegion); ?></td>
                                    <td><?php echo h((string)$ar['summary']); ?></td>
                                    <td><?php echo h((string)$ar['details']); ?></td>
                                    <td>
                                        <?php if ($hasScreenshot): ?>
                                            <a href="<?php echo h($screenshotUrl); ?>" target="_blank" rel="noopener">view</a>
                                            <span class="mono" style="display:block;margin-top:4px;"><?php echo h($screenshotDisplayUuid); ?></span>
                                        <?php else: ?>
                                            -
                                        <?php endif; ?>
                                    </td>
                                    <td><?php echo h($screenshotStatus === '' ? '-' : $screenshotStatus); ?></td>
                                    <td class="mono"><?php echo h($pos); ?></td>
                                    <td>
                                        <?php if ($is_admin): ?>
                                            <form method="post" onsubmit="return confirm('Delete abuse report #<?php echo h((string)$ar['id']); ?>?');">
                                                <input type="hidden" name="action" value="abuse_report_remove">
                                                <input type="hidden" name="report_id" value="<?php echo h((string)$ar['id']); ?>">
                                                <button class="btn-bad" type="submit" style="min-width:128px;">Delete Report</button>
                                            </form>
                                        <?php else: ?>
                                            <span class="mono" style="color:#64748b;">admin only</span>
                                        <?php endif; ?>
                                    </td>
                                </tr>
                            <?php endwhile; ?>
                        <?php endif; ?>
                        <?php if (!$hasAbuseRows): ?>
                            <tr><td colspan="11">No abuse reports found yet.</td></tr>
                        <?php endif; ?>
                        </tbody>
                    </table>
                </div>
            </section>

            <section class="card" data-tab="access">
                <h2>HG Users (`wp_opensim_auth`)</h2>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>UUID</th><th>Grid</th><th>Status</th><th>Reason/Comment</th><th>Added By</th><th>Updated By</th><th>Actions</th></tr></thead>
                        <tbody>
                        <?php while ($u = $hg_users->fetch_assoc()): ?>
                            <tr>
                                <td class="mono"><?php echo h((string)$u['uuid']); ?></td>
                                <td class="mono"><?php echo h((string)$u['gridname']); ?></td>
                                <td><?php echo !empty($u['banned']) ? '<span class="tag tag-bad">BANNED</span>' : '<span class="tag tag-ok">ALLOWED</span>'; ?></td>
                                <td><?php echo h((string)$u['reason']); ?></td>
                                <td class="mono">
                                    <?php
                                    $addedByLabel = trim((string)($u['added_by_label'] ?? ''));
                                    $addedByUuid = trim((string)($u['added_by_uuid'] ?? ''));
                                    $addedAt = trim((string)($u['added_at'] ?? ''));
                                    $addedByText = $addedByLabel !== '' ? $addedByLabel : ($addedByUuid !== '' ? $addedByUuid : '-');
                                    ?>
                                    <?php echo h($addedByText); ?>
                                    <?php if ($addedAt !== '' && $addedAt !== '0000-00-00 00:00:00'): ?>
                                        <div class="small"><?php echo h($addedAt); ?></div>
                                    <?php endif; ?>
                                </td>
                                <td class="mono">
                                    <?php
                                    $updatedByLabel = trim((string)($u['updated_by_label'] ?? ''));
                                    $updatedByUuid = trim((string)($u['updated_by_uuid'] ?? ''));
                                    $updatedAt = trim((string)($u['updated_at'] ?? ''));
                                    $updatedByText = $updatedByLabel !== '' ? $updatedByLabel : ($updatedByUuid !== '' ? $updatedByUuid : '-');
                                    ?>
                                    <?php echo h($updatedByText); ?>
                                    <?php if ($updatedAt !== '' && $updatedAt !== '0000-00-00 00:00:00'): ?>
                                        <div class="small"><?php echo h($updatedAt); ?></div>
                                    <?php endif; ?>
                                </td>
                                <td>
                                    <div class="row">
                                        <?php if (!empty($u['banned'])): ?>
                                            <form method="post">
                                                <input type="hidden" name="action" value="hg_unban_user">
                                                <input type="hidden" name="uuid" value="<?php echo h((string)$u['uuid']); ?>">
                                                <input type="hidden" name="gridname" value="<?php echo h((string)$u['gridname']); ?>">
                                                <button class="btn-ok" type="submit">Unban</button>
                                            </form>
                                        <?php else: ?>
                                            <form method="post" class="row">
                                                <input type="hidden" name="action" value="hg_ban_user">
                                                <input type="hidden" name="uuid" value="<?php echo h((string)$u['uuid']); ?>">
                                                <input type="hidden" name="gridname" value="<?php echo h((string)$u['gridname']); ?>">
                                                <input type="text" name="ban_reason" placeholder="Ban reason" required>
                                                <button class="btn-bad" type="submit">Ban</button>
                                            </form>
                                        <?php endif; ?>
                                        <form method="post">
                                            <input type="hidden" name="action" value="hg_remove_user">
                                            <input type="hidden" name="uuid" value="<?php echo h((string)$u['uuid']); ?>">
                                            <input type="hidden" name="gridname" value="<?php echo h((string)$u['gridname']); ?>">
                                            <button class="btn-soft" type="submit">Remove</button>
                                        </form>
                                    </div>
                                </td>
                            </tr>
                        <?php endwhile; ?>
                        </tbody>
                    </table>
                </div>
            </section>

            <section class="card half" data-tab="access">
                <h2>Banned Grids</h2>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>Grid</th><th>Reason</th><th>Action</th></tr></thead>
                        <tbody>
                        <?php while ($g = $banned_grids->fetch_assoc()): ?>
                            <tr>
                                <td class="mono"><?php echo h((string)$g['gridname']); ?></td>
                                <td><?php echo h((string)$g['reason']); ?></td>
                                <td>
                                    <form method="post">
                                        <input type="hidden" name="action" value="remove_banned_grid">
                                        <input type="hidden" name="gridname" value="<?php echo h((string)$g['gridname']); ?>">
                                        <button class="btn-bad" type="submit">Remove</button>
                                    </form>
                                </td>
                            </tr>
                        <?php endwhile; ?>
                        </tbody>
                    </table>
                </div>
            </section>

            <section class="card half" data-tab="access">
                <h2>Partner Grids</h2>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>Grid</th><th>Note</th><th>Action</th></tr></thead>
                        <tbody>
                        <?php while ($p = $partners->fetch_assoc()): ?>
                            <tr>
                                <td class="mono"><?php echo h((string)$p['gridname']); ?></td>
                                <td><?php echo h((string)$p['note']); ?></td>
                                <td>
                                    <form method="post">
                                        <input type="hidden" name="action" value="remove_partner_grid">
                                        <input type="hidden" name="gridname" value="<?php echo h((string)$p['gridname']); ?>">
                                        <button class="btn-bad" type="submit">Remove</button>
                                    </form>
                                </td>
                            </tr>
                        <?php endwhile; ?>
                        </tbody>
                    </table>
                </div>
            </section>

            <section class="card" data-tab="access">
                <h2>Local Users (`wp_oslogin_auth`)</h2>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>Name</th><th>UUID</th><th>Last Login</th><th>Status</th><th>Reason</th><th>Action</th></tr></thead>
                        <tbody>
                        <?php while ($u = $local_users->fetch_assoc()): ?>
                            <tr>
                                <td><?php echo h(trim((string)$u['first_name'] . ' ' . (string)$u['last_name'])); ?></td>
                                <td class="mono"><?php echo h((string)$u['uuid']); ?></td>
                                <td><?php echo h((string)$u['last_login']); ?></td>
                                <td><?php echo !empty($u['banned']) ? '<span class="tag tag-bad">BANNED</span>' : '<span class="tag tag-ok">ACTIVE</span>'; ?></td>
                                <td><?php echo h((string)$u['ban_reason']); ?></td>
                                <td>
                                    <?php if (!empty($u['banned'])): ?>
                                        <form method="post">
                                            <input type="hidden" name="action" value="unban_user">
                                            <input type="hidden" name="uuid" value="<?php echo h((string)$u['uuid']); ?>">
                                            <button class="btn-ok" type="submit">Unban</button>
                                        </form>
                                    <?php else: ?>
                                        <form method="post" class="row">
                                            <input type="hidden" name="action" value="ban_user">
                                            <input type="hidden" name="uuid" value="<?php echo h((string)$u['uuid']); ?>">
                                            <input type="text" name="ban_reason" placeholder="Ban reason" required>
                                            <button class="btn-bad" type="submit">Ban</button>
                                        </form>
                                    <?php endif; ?>
                                </td>
                            </tr>
                        <?php endwhile; ?>
                        </tbody>
                    </table>
                </div>
            </section>
            <?php endif; ?>

            <?php if ($is_selfservice): ?>
            <section class="card" data-tab="self">
                <h2>Self-Service Actions</h2>
                <p>Use only allowed actions for your UUID. Region actions are limited to your allowed region UUID list.</p>

                <?php if ($self_acl): ?>
                    <div class="row" style="margin-bottom:8px;">
                        <span class="tag tag-ok">UUID: <?php echo h((string)$self_acl['avatar_uuid']); ?></span>
                        <span class="tag">restart: <?php echo !empty($self_acl['can_restart_region']) ? 'yes' : 'no'; ?></span>
                        <span class="tag">OAR: <?php echo !empty($self_acl['can_backup_region']) ? 'yes' : 'no'; ?></span>
                        <span class="tag">IAR: <?php echo !empty($self_acl['can_export_iar']) ? 'yes' : 'no'; ?></span>
                        <span class="tag">HG allow: <?php echo !empty($self_acl['can_hg_add_user']) ? 'yes' : 'no'; ?></span>
                        <span class="tag">panel admin: <?php echo !empty($self_acl['can_panel_admin']) ? 'yes' : 'no'; ?></span>
                        <span class="tag">allowed avatars: <?php echo h((string)count($self_allowed_avatars)); ?></span>
                    </div>
                <?php endif; ?>

                <?php if (empty($self_allowed_regions)): ?>
                    <p>No allowed regions configured for this UUID. Ask an administrator to set at least one region UUID in self-service ACL.</p>
                <?php endif; ?>

                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="self_region_restart">
                    <select name="self_region_uuid" required style="min-width:340px;max-width:100%;" <?php echo empty($self_allowed_regions) ? 'disabled' : ''; ?>>
                        <option value="">Select allowed region...</option>
                        <?php foreach ($self_allowed_regions as $arUuid): ?>
                            <option value="<?php echo h($arUuid); ?>"><?php echo h(($self_region_map[$arUuid] ?? $arUuid) . ' (' . $arUuid . ')'); ?></option>
                        <?php endforeach; ?>
                    </select>
                    <input type="text" name="self_delay_seconds" value="180" placeholder="Delay seconds">
                    <input type="text" name="self_reason" value="Self-service restart" placeholder="Reason">
                    <button class="btn-pri" type="submit" <?php echo empty($self_allowed_regions) ? 'disabled' : ''; ?>>Schedule Restart</button>
                </form>

                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="self_hg_add_user">
                    <input type="text" name="self_hg_uuid" placeholder="HG user UUID" required <?php echo empty($self_acl['can_hg_add_user']) ? 'disabled' : ''; ?>>
                    <input type="text" name="self_hg_gridname" placeholder="grid.example.org:8002" required <?php echo empty($self_acl['can_hg_add_user']) ? 'disabled' : ''; ?>>
                    <input type="text" name="self_hg_comment" placeholder="Comment (optional)" <?php echo empty($self_acl['can_hg_add_user']) ? 'disabled' : ''; ?>>
                    <button class="btn-ok" type="submit" <?php echo empty($self_acl['can_hg_add_user']) ? 'disabled' : ''; ?>>Allow HG User</button>
                </form>

                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="self_region_oar">
                    <select name="self_region_uuid" required style="min-width:340px;max-width:100%;" <?php echo empty($self_allowed_regions) ? 'disabled' : ''; ?>>
                        <option value="">Select allowed region for OAR...</option>
                        <?php foreach ($self_allowed_regions as $arUuid): ?>
                            <option value="<?php echo h($arUuid); ?>"><?php echo h(($self_region_map[$arUuid] ?? $arUuid) . ' (' . $arUuid . ')'); ?></option>
                        <?php endforeach; ?>
                    </select>
                    <label><input type="checkbox" name="self_publish"> publish</label>
                    <button class="btn-soft" type="submit" <?php echo empty($self_allowed_regions) ? 'disabled' : ''; ?>>Export Region OAR</button>
                </form>

                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="self_healthcheck_region">
                    <select name="self_health_region_uuid" required style="min-width:340px;max-width:100%;" <?php echo empty($self_allowed_regions) ? 'disabled' : ''; ?>>
                        <option value="">Select region for HealthCheck...</option>
                        <?php foreach ($self_allowed_regions as $arUuid): ?>
                            <option value="<?php echo h($arUuid); ?>"><?php echo h(($self_region_map[$arUuid] ?? $arUuid) . ' (' . $arUuid . ')'); ?></option>
                        <?php endforeach; ?>
                    </select>
                    <button class="btn-soft" type="submit" <?php echo empty($self_allowed_regions) ? 'disabled' : ''; ?>>Start HealthCheck (Region)</button>
                </form>

                <form method="post" class="row">
                    <input type="hidden" name="action" value="self_avatar_iar">
                    <select name="self_region_uuid" style="min-width:340px;max-width:100%;" <?php echo empty($self_allowed_regions) ? 'disabled' : ''; ?>>
                        <option value="">Auto (first allowed region)</option>
                        <?php foreach ($self_allowed_regions as $arUuid): ?>
                            <option value="<?php echo h($arUuid); ?>"><?php echo h(($self_region_map[$arUuid] ?? $arUuid) . ' (' . $arUuid . ')'); ?></option>
                        <?php endforeach; ?>
                    </select>
                    <select name="self_avatar_uuid" style="min-width:340px;max-width:100%;" <?php echo empty($self_allowed_avatars) ? 'disabled' : ''; ?>>
                        <?php foreach ($self_allowed_avatars as $au): ?>
                            <option value="<?php echo h($au); ?>"><?php echo h($au); ?></option>
                        <?php endforeach; ?>
                    </select>
                    <button class="btn-ok" type="submit" <?php echo empty($self_allowed_regions) || empty($self_allowed_avatars) ? 'disabled' : ''; ?>>Export Allowed Avatar IAR</button>
                </form>

                <h3 style="margin-top:14px;">Region Console (Read-Only)</h3>
                <form method="post" class="row" style="margin-bottom:10px;">
                    <input type="hidden" name="action" value="self_console_view">
                    <select name="self_console_region_uuid" required style="min-width:340px;max-width:100%;" <?php echo empty($self_allowed_regions) ? 'disabled' : ''; ?>>
                        <option value="">Select allowed region...</option>
                        <?php foreach ($self_allowed_regions as $arUuid): ?>
                            <option value="<?php echo h($arUuid); ?>" <?php echo $self_console_region_uuid === $arUuid ? 'selected' : ''; ?>><?php echo h(($self_region_map[$arUuid] ?? $arUuid) . ' (' . $arUuid . ')'); ?></option>
                        <?php endforeach; ?>
                    </select>
                    <input type="number" name="self_console_lines" min="20" max="500" value="<?php echo h((string)$self_console_lines); ?>" style="width:120px;">
                    <button class="btn-soft" type="submit" <?php echo empty($self_allowed_regions) ? 'disabled' : ''; ?>>Read Console (RO)</button>
                </form>
                <pre style="margin-top:8px;max-height:260px;overflow:auto;background:#0f172a;color:#e2e8f0;padding:8px;border-radius:10px;"><?php echo h($self_console_output !== '' ? $self_console_output : 'No self-service console snapshot loaded yet.'); ?></pre>

                <h3 style="margin-top:14px;">Download My Allowed OAR Files</h3>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>File</th><th>Size</th><th>Modified</th><th>Action</th></tr></thead>
                        <tbody>
                        <?php if (!empty($self_archive_files_oar)): ?>
                            <?php foreach ($self_archive_files_oar as $af): ?>
                                <tr>
                                    <td class="mono"><?php echo h((string)$af['rel']); ?></td>
                                    <td><?php echo h((string)number_format(((int)$af['size']) / 1048576, 2)); ?> MB</td>
                                    <td class="mono"><?php echo h(gmdate('Y-m-d H:i:s', (int)$af['mtime'])); ?> UTC</td>
                                    <td>
                                        <div class="row">
                                            <a class="btn-soft" href="?download_archive=1&amp;type=oar&amp;file=<?php echo rawurlencode((string)$af['rel']); ?>">Download</a>
                                            <form method="post" onsubmit="return confirm('Remove this OAR file?');">
                                                <input type="hidden" name="action" value="archive_delete_file">
                                                <input type="hidden" name="archive_type" value="oar">
                                                <input type="hidden" name="archive_file" value="<?php echo h((string)$af['rel']); ?>">
                                                <button class="btn-bad" type="submit">Remove</button>
                                            </form>
                                        </div>
                                    </td>
                                </tr>
                            <?php endforeach; ?>
                        <?php else: ?>
                            <tr><td colspan="4">No allowed OAR files found.</td></tr>
                        <?php endif; ?>
                        </tbody>
                    </table>
                </div>

                <h3 style="margin-top:14px;">Download My Allowed IAR Files</h3>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>File</th><th>Size</th><th>Modified</th><th>Action</th></tr></thead>
                        <tbody>
                        <?php if (!empty($self_archive_files_iar)): ?>
                            <?php foreach ($self_archive_files_iar as $af): ?>
                                <tr>
                                    <td class="mono"><?php echo h((string)$af['rel']); ?></td>
                                    <td><?php echo h((string)number_format(((int)$af['size']) / 1048576, 2)); ?> MB</td>
                                    <td class="mono"><?php echo h(gmdate('Y-m-d H:i:s', (int)$af['mtime'])); ?> UTC</td>
                                    <td>
                                        <div class="row">
                                            <a class="btn-soft" href="?download_archive=1&amp;type=iar&amp;file=<?php echo rawurlencode((string)$af['rel']); ?>">Download</a>
                                            <form method="post" onsubmit="return confirm('Remove this IAR file?');">
                                                <input type="hidden" name="action" value="archive_delete_file">
                                                <input type="hidden" name="archive_type" value="iar">
                                                <input type="hidden" name="archive_file" value="<?php echo h((string)$af['rel']); ?>">
                                                <button class="btn-bad" type="submit">Remove</button>
                                            </form>
                                        </div>
                                    </td>
                                </tr>
                            <?php endforeach; ?>
                        <?php else: ?>
                            <tr><td colspan="4">No allowed IAR files found.</td></tr>
                        <?php endif; ?>
                        </tbody>
                    </table>
                </div>
            </section>
            <?php endif; ?>
        </div>
    </div>
    <script>
    var panelTabStorageKey = 'igrid_panel_active_tab';

    function setActiveTab(tabName) {
        var buttons = document.querySelectorAll('.tab-btn[data-tab]');
        var sections = document.querySelectorAll('section.card[data-tab]');

        buttons.forEach(function (button) {
            button.classList.toggle('is-active', button.getAttribute('data-tab') === tabName);
        });

        sections.forEach(function (section) {
            var isActive = section.getAttribute('data-tab') === tabName;
            section.style.display = isActive ? '' : 'none';
            section.classList.toggle('is-active-tab', isActive);
        });
    }

    function rrAppendResult(lines) {
        var target = document.getElementById('restartResult');
        if (!target) {
            return;
        }
        var stamp = new Date().toISOString();
        target.textContent = '[' + stamp + '] ' + lines + '\n\n' + target.textContent;
    }

    function rrClampDelay(value) {
        var parsed = parseInt(value, 10);
        if (!Number.isFinite(parsed)) {
            parsed = 180;
        }
        if (parsed < 10) {
            parsed = 10;
        }
        if (parsed > 86400) {
            parsed = 86400;
        }
        return parsed;
    }

    async function regionRestartAction(action, regionUuid, regionName, triggerButton) {
        if (!regionUuid) {
            rrAppendResult('Missing region UUID for action: ' + action);
            return;
        }

        var requesterEl = document.getElementById('rrRequester');
        var apiKeyEl = document.getElementById('rrApiKey');
        var delayEl = document.getElementById('delay_' + regionUuid);
        var reasonEl = document.getElementById('reason_' + regionUuid);

        var requesterUuid = requesterEl ? requesterEl.value.trim() : '';
        var requesterKey = apiKeyEl ? apiKeyEl.value.trim() : '';
        var delaySeconds = rrClampDelay(delayEl ? delayEl.value : '180');
        var reason = reasonEl ? reasonEl.value.trim() : '';

        var payload = {
            action: action,
            region_uuid: regionUuid
        };

        if (requesterUuid !== '') {
            payload.requester_uuid = requesterUuid;
        }
        if (requesterKey !== '') {
            payload.api_key = requesterKey;
        }
        if (action === 'schedule') {
            payload.delay_seconds = delaySeconds;
        }
        if (reason !== '') {
            payload.reason = reason;
        }

        if (triggerButton) {
            triggerButton.disabled = true;
        }

        rrAppendResult('Sending ' + action + ' request for ' + regionName + ' (' + regionUuid + ')');

        try {
            var response = await fetch('region_restart_api.php', {
                method: 'POST',
                credentials: 'same-origin',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(payload)
            });

            var raw = await response.text();
            var parsed;
            try {
                parsed = JSON.parse(raw);
            } catch (parseError) {
                parsed = { ok: false, error: 'Non-JSON response', raw: raw };
            }

            rrAppendResult(
                'HTTP ' + response.status + ' ' + response.statusText + '\n'
                + JSON.stringify(parsed, null, 2)
            );
        } catch (error) {
            rrAppendResult('Request failed: ' + (error && error.message ? error.message : String(error)));
        } finally {
            if (triggerButton) {
                triggerButton.disabled = false;
            }
        }
    }

    async function rrCheckAllStatus() {
        var statusButtons = Array.prototype.slice.call(document.querySelectorAll('.restart-action-btn[data-action="status"]'));
        if (statusButtons.length === 0) {
            rrAppendResult('No regions found for status check.');
            return;
        }

        rrAppendResult('Checking status for ' + statusButtons.length + ' regions...');
        for (var i = 0; i < statusButtons.length; i++) {
            var btn = statusButtons[i];
            await regionRestartAction(
                'status',
                btn.getAttribute('data-region-uuid') || '',
                btn.getAttribute('data-region-name') || 'Unknown Region',
                null
            );
            await new Promise(function (resolve) { setTimeout(resolve, 120); });
        }
    }

    document.addEventListener('DOMContentLoaded', function () {
        var defaultTab = <?php echo $is_admin ? "'regions'" : ($is_selfservice ? "'self'" : "'access'"); ?>;
        var savedTab = '';
        try {
            savedTab = window.localStorage.getItem(panelTabStorageKey) || '';
        } catch (e) {
            savedTab = '';
        }

        if (!document.querySelector('.tab-btn[data-tab="' + savedTab + '"]')) {
            savedTab = defaultTab;
        }
        setActiveTab(savedTab);

        var checkAllButton = document.getElementById('rrCheckAll');
        if (checkAllButton) {
            checkAllButton.addEventListener('click', function () {
                rrCheckAllStatus();
            });
        }
    });

    document.addEventListener('click', function (event) {
        var tabButton = event.target.closest('.tab-btn[data-tab]');
        if (tabButton) {
            event.preventDefault();
            var tabName = tabButton.getAttribute('data-tab') || 'access';
            setActiveTab(tabName);
            try {
                window.localStorage.setItem(panelTabStorageKey, tabName);
            } catch (e) {
            }
            return;
        }

        var pickImButton = event.target.closest('.pick-im-target');
        if (pickImButton) {
            event.preventDefault();
            var targetInput = document.getElementById('im_to_uuid');
            if (targetInput) {
                targetInput.value = pickImButton.getAttribute('data-uuid') || '';
                targetInput.focus();
            }
            return;
        }

        var btn = event.target.closest('.restart-action-btn');
        if (!btn) {
            return;
        }
        event.preventDefault();
        regionRestartAction(
            btn.getAttribute('data-action') || 'status',
            btn.getAttribute('data-region-uuid') || '',
            btn.getAttribute('data-region-name') || 'Unknown Region',
            btn
        );
    });
    </script>
</body>
</html>
<?php
$conn->close();
$robust_conn->close();
?>
