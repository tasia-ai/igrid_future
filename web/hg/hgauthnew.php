<?php
header("Content-Type: application/xml; charset=utf-8");

$host = "i.let-us.cyou";
$user = "root";
$pass = "CHANGE_ME_DB_PASSWORD";
$dbname = "wordpress";
$robust_dbname = "robust";
$table_name = "wp_opensim_auth";
$localAuthTable = "wp_oslogin_auth";
$viewerRulesTable = "wp_igrid_viewer_block_rules";
$viewerConfigTable = "wp_igrid_viewer_block_config";
$regionOverridesTable = "wp_igrid_region_auth_overrides";
$robust_table = "UserAccounts";

$disableGridnameChecks = true; // set to true to disable gridname checking

$conn = new mysqli($host, $user, $pass, $dbname);
$robust_conn = new mysqli($host, $user, $pass, $robust_dbname);

if ($conn->connect_error || $robust_conn->connect_error) {
    emit(false, "Database connection failed");
}

function cdata_safe($value)
{
    return str_replace(']]>', ']]]]><![CDATA[>', (string)$value);
}

function emit($allowed, $message)
{
    $flag = $allowed ? 'true' : 'false';
    echo '<?xml version="1.0" encoding="utf-8"?>' . "\n"
        . '<AuthorizationResponse>' . "\n"
        . '  <IsAuthorized>' . $flag . '</IsAuthorized>' . "\n"
        . '  <Message><![CDATA[' . cdata_safe($message) . ']]></Message>' . "\n"
        . '</AuthorizationResponse>';
    exit();
}

function log_traffic($conn, $uuid, $status, $gridname)
{
    $stmt = $conn->prepare("INSERT INTO opensim_auth_traffic (uuid, status, gridname) VALUES (?, ?, ?)");
    if (!$stmt) {
        return;
    }

    $stmt->bind_param("sss", $uuid, $status, $gridname);
    $stmt->execute();
    $stmt->close();
}

function is_local_user($robust_conn, $robust_table, $avatarUUID)
{
    $stmt = $robust_conn->prepare("SELECT PrincipalID FROM $robust_table WHERE PrincipalID = ? LIMIT 1");
    if (!$stmt) {
        return false;
    }

    $stmt->bind_param("s", $avatarUUID);
    $stmt->execute();
    $row = $stmt->get_result()->fetch_assoc();
    $stmt->close();

    return !empty($row);
}

function parse_gridname_from_surname($surname)
{
    $surname = trim((string)$surname);
    if ($surname === '') {
        return '';
    }

    $atPos = strpos($surname, '@');
    if ($atPos === false) {
        return '';
    }

    $grid = trim(substr($surname, $atPos + 1));
    return $grid === '' ? '' : strtolower($grid);
}

function get_maintenance_policy($conn)
{
    $policy = [
        'enabled' => false,
        'message' => 'System is under maintenance. Please try again later.',
        'allowed' => []
    ];

    $sql = "SELECT maintenance_mode, maintenance_message, allowed_uuids FROM opensim_maintenance WHERE id = 1 LIMIT 1";
    $result = $conn->query($sql);
    if (!$result) {
        return $policy;
    }

    $row = $result->fetch_assoc();
    if (!$row) {
        return $policy;
    }

    $policy['enabled'] = intval($row['maintenance_mode']) === 1;

    $message = trim((string)($row['maintenance_message'] ?? ''));
    if ($message !== '') {
        $policy['message'] = $message;
    }

    $rawAllowed = (string)($row['allowed_uuids'] ?? '');
    if ($rawAllowed !== '') {
        $tokens = preg_split('/[\s,;]+/', $rawAllowed);
        foreach ($tokens as $token) {
            $token = strtolower(trim((string)$token));
            if ($token !== '') {
                $policy['allowed'][$token] = true;
            }
        }
    }

    return $policy;
}

function get_region_auth_override($conn, $regionOverridesTable, $regionUUID, $regionName)
{
    $policy = [
        'enabled' => false,
        'bypass_maintenance' => false,
        'bypass_viewer_block' => false,
        'public_guest_access' => false,
    ];

    $regionUUID = strtolower(trim((string)$regionUUID));
    $regionName = trim((string)$regionName);

    if ($regionUUID !== '') {
        $stmt = $conn->prepare("SELECT enabled, bypass_maintenance, bypass_viewer_block, public_guest_access FROM $regionOverridesTable WHERE region_uuid = ? LIMIT 1");
        if ($stmt) {
            $stmt->bind_param("s", $regionUUID);
            $stmt->execute();
            $row = $stmt->get_result()->fetch_assoc();
            $stmt->close();
            if ($row && intval($row['enabled']) === 1) {
                $policy['enabled'] = true;
                $policy['bypass_maintenance'] = intval($row['bypass_maintenance']) === 1;
                $policy['bypass_viewer_block'] = intval($row['bypass_viewer_block']) === 1;
                $policy['public_guest_access'] = intval($row['public_guest_access']) === 1;
                return $policy;
            }
        }
    }

    if ($regionName !== '') {
        $stmt = $conn->prepare("SELECT enabled, bypass_maintenance, bypass_viewer_block, public_guest_access FROM $regionOverridesTable WHERE LOWER(region_name) = LOWER(?) LIMIT 1");
        if ($stmt) {
            $stmt->bind_param("s", $regionName);
            $stmt->execute();
            $row = $stmt->get_result()->fetch_assoc();
            $stmt->close();
            if ($row && intval($row['enabled']) === 1) {
                $policy['enabled'] = true;
                $policy['bypass_maintenance'] = intval($row['bypass_maintenance']) === 1;
                $policy['bypass_viewer_block'] = intval($row['bypass_viewer_block']) === 1;
                $policy['public_guest_access'] = intval($row['public_guest_access']) === 1;
                return $policy;
            }
        }
    }

    return $policy;
}

function collect_xml_values($node, &$bucket)
{
    if (!($node instanceof SimpleXMLElement)) {
        return;
    }

    $name = strtolower($node->getName());
    $value = trim((string)$node);
    if ($name !== '' && $value !== '') {
        if (!isset($bucket[$name])) {
            $bucket[$name] = [];
        }
        $bucket[$name][] = $value;
    }

    foreach ($node->children() as $child) {
        collect_xml_values($child, $bucket);
    }
}

function first_non_empty_value($values, $keys)
{
    foreach ($keys as $key) {
        $lookup = strtolower($key);
        if (!empty($values[$lookup])) {
            foreach ($values[$lookup] as $candidate) {
                $candidate = trim((string)$candidate);
                if ($candidate !== '') {
                    return $candidate;
                }
            }
        }
    }

    return '';
}

function regex_tag_value($request, $tagNames)
{
    foreach ($tagNames as $tagName) {
        $pattern = '~<' . preg_quote($tagName, '~') . '>\s*(.*?)\s*</' . preg_quote($tagName, '~') . '>~is';
        if (preg_match($pattern, $request, $match)) {
            $value = trim(html_entity_decode(strip_tags((string)$match[1]), ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8'));
            if ($value !== '') {
                return $value;
            }
        }
    }

    return '';
}

function extract_viewer_info($xml, $request)
{
    $fields = [
        'viewer' => '',
        'channel' => '',
        'version' => ''
    ];

    $keys = [
        'viewer' => [
            'Viewer', 'viewer', 'UserAgent', 'useragent', 'User_Agent', 'user_agent',
            'ClientName', 'clientname', 'client_name', 'AppName', 'appname', 'app_name'
        ],
        'channel' => [
            'Channel', 'channel', 'ViewerChannel', 'viewerchannel', 'viewer_channel',
            'ClientChannel', 'clientchannel', 'client_channel', 'AppChannel', 'appchannel', 'app_channel'
        ],
        'version' => [
            'Version', 'version', 'ViewerVersion', 'viewerversion', 'viewer_version',
            'ClientVersion', 'clientversion', 'client_version', 'AppVersion', 'appversion', 'app_version'
        ]
    ];

    $allValues = [];
    if ($xml instanceof SimpleXMLElement) {
        collect_xml_values($xml, $allValues);
    }

    foreach ($keys as $target => $tagNames) {
        $fields[$target] = first_non_empty_value($allValues, $tagNames);
        if ($fields[$target] === '' && $request !== '') {
            $fields[$target] = regex_tag_value($request, $tagNames);
        }
    }

    if ($fields['viewer'] === '') {
        $combo = trim($fields['channel'] . ' ' . $fields['version']);
        if ($combo !== '') {
            $fields['viewer'] = $combo;
        }
    }

    return $fields;
}

function viewer_rule_match($mode, $pattern, $haystack)
{
    $pattern = trim((string)$pattern);
    $haystack = trim((string)$haystack);
    if ($pattern === '' || $haystack === '') {
        return false;
    }

    $mode = strtolower(trim((string)$mode));

    if ($mode === 'equals') {
        return strcasecmp($haystack, $pattern) === 0;
    }

    if ($mode === 'regex') {
        set_error_handler(function () { return true; });
        $matched = @preg_match('/' . $pattern . '/i', $haystack) === 1;
        restore_error_handler();
        return $matched;
    }

    return stripos($haystack, $pattern) !== false;
}

function check_viewer_block_policy($conn, $viewerRulesTable, $viewerInfo)
{
    $sql = "SELECT match_pattern, match_mode, reason FROM $viewerRulesTable WHERE enabled = 1 ORDER BY id DESC";
    $res = $conn->query($sql);
    if (!$res) {
        return null;
    }

    $samples = [];
    $viewer = trim((string)($viewerInfo['viewer'] ?? ''));
    $channel = trim((string)($viewerInfo['channel'] ?? ''));
    $version = trim((string)($viewerInfo['version'] ?? ''));

    foreach ([$viewer, $channel, $version] as $sample) {
        if ($sample !== '' && !in_array($sample, $samples, true)) {
            $samples[] = $sample;
        }
    }

    $combo = trim($channel . ' ' . $version);
    if ($combo !== '' && !in_array($combo, $samples, true)) {
        $samples[] = $combo;
    }

    while ($row = $res->fetch_assoc()) {
        $pattern = trim((string)($row['match_pattern'] ?? ''));
        $mode = (string)($row['match_mode'] ?? 'contains');
        foreach ($samples as $sample) {
            if (viewer_rule_match($mode, $pattern, $sample)) {
                $reason = trim((string)($row['reason'] ?? ''));
                return $reason !== '' ? $reason : 'Viewer version blocked by policy';
            }
        }
    }

    return null;
}

function get_viewer_block_exceptions($conn, $viewerConfigTable)
{
    $allowed = [];

    $sql = "SELECT allowed_uuids FROM $viewerConfigTable WHERE id = 1 LIMIT 1";
    $res = $conn->query($sql);
    if (!$res) {
        return $allowed;
    }

    $row = $res->fetch_assoc();
    if (!$row) {
        return $allowed;
    }

    $raw = (string)($row['allowed_uuids'] ?? '');
    if ($raw === '') {
        return $allowed;
    }

    $tokens = preg_split('/[\s,;]+/', $raw);
    foreach ($tokens as $token) {
        $uuid = strtolower(trim((string)$token));
        if ($uuid !== '') {
            $allowed[$uuid] = true;
        }
    }

    return $allowed;
}

$request = @file_get_contents('php://input');
file_put_contents('/var/www/html/web/hg/error.log', date('[Y-m-d H:i:s] ') . "Request: $request\n", FILE_APPEND);

$xml = simplexml_load_string($request, 'SimpleXMLElement', LIBXML_NOCDATA);

$avatarUUID = '';
$firstname = '';
$lastname_raw = '';
$gridname = '';
$regionUUID = '';
$regionName = '';

if ($xml && isset($xml->ID)) {
    $avatarUUID = trim((string)$xml->ID);
    $firstname = trim((string)($xml->FirstName ?? ''));
    $lastname_raw = trim((string)($xml->SurName ?? ''));
    $regionUUID = strtolower(trim((string)($xml->RegionID ?? '')));
    $regionName = trim((string)($xml->RegionName ?? ''));
    if (!$disableGridnameChecks) {
        $gridname = parse_gridname_from_surname($lastname_raw);
        if ($gridname === '') {
            log_traffic($conn, $avatarUUID, 'missing_gridname', '');
            $conn->close();
            $robust_conn->close();
            emit(false, 'php exception: missing gridname in surname');
        }
    } else {
        $gridname = parse_gridname_from_surname($lastname_raw);
    }
}

$viewerInfo = extract_viewer_info($xml, $request);
$regionOverride = get_region_auth_override($conn, $regionOverridesTable, $regionUUID, $regionName);
file_put_contents(
    '/var/www/html/web/hg/error.log',
    date('[Y-m-d H:i:s] ') . 'DEBUG: Parsed UUID=[' . $avatarUUID . '] Gridname=[' . $gridname . '] RegionID=[' . $regionUUID . '] RegionName=[' . $regionName . '] Override=' . json_encode($regionOverride, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE) . ' Viewer=' . json_encode($viewerInfo, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE) . "\n",
    FILE_APPEND
);

if ($avatarUUID === '') {
    log_traffic($conn, '', 'no_uuid', $gridname);
    $conn->close();
    $robust_conn->close();
    emit(false, 'No UUID provided');
}

$maintenance = get_maintenance_policy($conn);
if ($maintenance['enabled'] && !$regionOverride['bypass_maintenance'] && !isset($maintenance['allowed'][strtolower($avatarUUID)])) {
    log_traffic($conn, $avatarUUID, 'maintenance_blocked', $gridname !== '' ? $gridname : 'local');
    $conn->close();
    $robust_conn->close();
    emit(false, $maintenance['message']);
}

$viewerAllowed = get_viewer_block_exceptions($conn, $viewerConfigTable);
if (isset($viewerAllowed[strtolower($avatarUUID)])) {
    log_traffic($conn, $avatarUUID, 'viewer_block_exception', $gridname !== '' ? $gridname : 'local');
} else {
    if (!$regionOverride['bypass_viewer_block']) {
        $viewerBlockReason = check_viewer_block_policy($conn, $viewerRulesTable, $viewerInfo);
        if ($viewerBlockReason !== null) {
            log_traffic($conn, $avatarUUID, 'viewer_blocked', $gridname !== '' ? $gridname : 'local');
            $conn->close();
            $robust_conn->close();
            emit(false, $viewerBlockReason);
        }
    }
}

if (is_local_user($robust_conn, $robust_table, $avatarUUID)) {
    $stmt = $conn->prepare("SELECT banned, COALESCE(NULLIF(ban_reason, ''), 'This local avatar is banned') AS reason FROM $localAuthTable WHERE uuid = ? LIMIT 1");
    if ($stmt) {
        $stmt->bind_param("s", $avatarUUID);
        $stmt->execute();
        $localAuth = $stmt->get_result()->fetch_assoc();
        $stmt->close();

        if ($localAuth && intval($localAuth['banned']) === 1) {
            log_traffic($conn, $avatarUUID, 'local_banned', 'local');
            $conn->close();
            $robust_conn->close();
            emit(false, $localAuth['reason']);
        }

        if ($localAuth) {
            $upd = $conn->prepare("UPDATE $localAuthTable SET last_login = NOW() WHERE uuid = ?");
            if ($upd) {
                $upd->bind_param("s", $avatarUUID);
                $upd->execute();
                $upd->close();
            }
        }
    }

    log_traffic($conn, $avatarUUID, 'local_allowed', 'local');
    $conn->close();
    $robust_conn->close();
    emit(true, 'Local user detected, authentication not required');
}

if (!$disableGridnameChecks) {
    $stmt = $conn->prepare("SELECT banned, COALESCE(NULLIF(comment, ''), COALESCE(NULLIF(ban_reason, ''), 'This Avatar is banned')) AS reason FROM $table_name WHERE uuid = ? AND LOWER(gridname) = ? LIMIT 1");
    $lowerGrid = strtolower($gridname);
    $stmt->bind_param("ss", $avatarUUID, $lowerGrid);
} else {
    $stmt = $conn->prepare("SELECT banned, COALESCE(NULLIF(comment, ''), COALESCE(NULLIF(ban_reason, ''), 'This Avatar is banned')) AS reason FROM $table_name WHERE uuid = ? LIMIT 1");
    $stmt->bind_param("s", $avatarUUID);
}

$stmt->execute();
$result = $stmt->get_result();
$userRow = $result->fetch_assoc();
$stmt->close();

if ($userRow) {
    if (intval($userRow['banned']) === 1) {
        log_traffic($conn, $avatarUUID, 'banned', $gridname);
        $conn->close();
        $robust_conn->close();
        emit(false, $userRow['reason']);
    }

    log_traffic($conn, $avatarUUID, 'authorized', $gridname);
    $conn->close();
    $robust_conn->close();
    emit(true, 'Authorized');
}

if (!$disableGridnameChecks) {
    $bannedGrid = false;
    if ($gridname !== '') {
        $stmt = $conn->prepare("SELECT COALESCE(NULLIF(reason, ''), gridname) AS reason, gridname FROM banned_grids WHERE LOWER(gridname) = ? LIMIT 1");
        if ($stmt) {
            $stmt->bind_param("s", $gridname);
            $stmt->execute();
            $result = $stmt->get_result();
            $bannedGrid = $result->fetch_assoc();
            $stmt->close();
        }
    }

    if ($bannedGrid) {
        log_traffic($conn, $avatarUUID, 'banned_grid_' . $bannedGrid['gridname'], $gridname);
        $conn->close();
        $robust_conn->close();
        emit(false, 'Access from "' . $bannedGrid['gridname'] . '" is disabled permanently. ' . $bannedGrid['reason']);
    }

    $matchedGrid = false;
    if ($gridname !== '') {
        $stmt = $conn->prepare("SELECT gridname FROM partners WHERE LOWER(gridname) = ? LIMIT 1");
        if ($stmt) {
            $stmt->bind_param("s", $gridname);
            $stmt->execute();
            $result = $stmt->get_result();
            $matchedGrid = $result->fetch_assoc();
            $stmt->close();
        }
    }

    if ($matchedGrid) {
        log_traffic($conn, $avatarUUID, 'partner_guest_' . $matchedGrid['gridname'], $gridname);
        $conn->close();
        $robust_conn->close();
        emit(false, 'Hello visitor from ' . $matchedGrid['gridname'] . '! You are welcome on our grid but see: https://i.let-us.cyou/hg-partner-allow-access/ if you need help feel free to contact me on discord or via email support@is-on.click');
    }
}

if ($regionOverride['public_guest_access']) {
    log_traffic($conn, $avatarUUID, 'authorized_public_region', $gridname !== '' ? $gridname : 'local');
    $conn->close();
    $robust_conn->close();
    emit(true, 'Authorized by public-region override');
}

log_traffic($conn, $avatarUUID, 'unauthorized_guest', $gridname);
$conn->close();
$robust_conn->close();
emit(false, 'Hypergrid travel is disabled or you are not authorized. For more information contact us at support@is-on.click');
?>
