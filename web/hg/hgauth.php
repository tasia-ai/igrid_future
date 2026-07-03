<?php
header("Content-Type: application/xml");

$host = "i.let-us.cyou";
$user = "root";
$pass = "CHANGE_ME_DB_PASSWORD";
$dbname = "wordpress";
$robust_dbname = "robust";
$table_name = "wp_opensim_auth";
$robust_table = "UserAccounts";
$localAuthTable = "wp_oslogin_auth";

$disableGridnameChecks = true; // set to true to disable gridname checking

// Connect to databases
$conn = new mysqli($host, $user, $pass, $dbname);
$robust_conn = new mysqli($host, $user, $pass, $robust_dbname);

if ($conn->connect_error || $robust_conn->connect_error) {
    die('<?xml version="1.0" encoding="utf-8"?>
        <AuthorizationResponse>
        <IsAuthorized>false</IsAuthorized>
        <Message><![CDATA[Database connection failed]]></Message>
        </AuthorizationResponse>');
}

function log_traffic($conn, $uuid, $status, $gridname)
{
    $stmt = $conn->prepare("INSERT INTO opensim_auth_traffic (uuid, status, gridname) VALUES (?, ?, ?)");
    $stmt->bind_param("sss", $uuid, $status, $gridname);
    $stmt->execute();
    $stmt->close();
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

// Read and parse input
$request = @file_get_contents('php://input');
file_put_contents('/var/www/html/web/hg/error.log', date('[Y-m-d H:i:s] ') . "Request: $request \n", FILE_APPEND);

$xml3 = simplexml_load_string($request);

$avatarUUID = '';
$firstname = '';
$lastname_raw = '';
$lastname = '';
$gridname = '';

if ($xml3 && isset($xml3->ID)) {
    $avatarUUID = trim((string) $xml3->ID);
    $firstname = trim((string) $xml3->FirstName);
    $lastname_raw = trim((string) $xml3->SurName);
    $lastname = $lastname_raw;

    if (!$disableGridnameChecks) {
        $atPos = strpos($lastname_raw, '@');
        if ($atPos !== false) {
            $lastname = trim(substr($lastname_raw, 0, $atPos));
            $gridpart = trim(substr($lastname_raw, $atPos + 1));
            $gridname = $gridpart;
        } else {
            die('<?xml version="1.0" encoding="utf-8"?>
                <AuthorizationResponse>
                <IsAuthorized>false</IsAuthorized>
                <Message><![CDATA[php exception: missing gridname in surname]]></Message>
                </AuthorizationResponse>');
        }
    }
}

file_put_contents('/var/www/html/web/hg/error.log', date('[Y-m-d H:i:s] ') . "DEBUG: Parsed UUID: [$avatarUUID], Gridname: [$gridname] \n", FILE_APPEND);

if (empty($avatarUUID)) {
    log_traffic($conn, '', 'no_uuid', $gridname);
    $conn->close();
    $robust_conn->close();
    die('<?xml version="1.0" encoding="utf-8"?>
        <AuthorizationResponse>
        <IsAuthorized>false</IsAuthorized>
        <Message><![CDATA[No UUID provided]]></Message>
        </AuthorizationResponse>');
}

$maintenance = get_maintenance_policy($conn);
if ($maintenance['enabled'] && !isset($maintenance['allowed'][strtolower($avatarUUID)])) {
    log_traffic($conn, $avatarUUID, 'maintenance_blocked', $gridname);
    $conn->close();
    $robust_conn->close();
    die('<?xml version="1.0" encoding="utf-8"?>
        <AuthorizationResponse>
        <IsAuthorized>false</IsAuthorized>
        <Message><![CDATA[' . htmlspecialchars($maintenance['message'], ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8') . ']]></Message>
        </AuthorizationResponse>');
}

// Check if user is local
$stmt = $robust_conn->prepare("SELECT PrincipalID FROM $robust_table WHERE PrincipalID = ?");
$stmt->bind_param("s", $avatarUUID);
$stmt->execute();
$result = $stmt->get_result();
$local_user = $result->fetch_assoc();
$stmt->close();

if ($local_user) {
    $stmt = $conn->prepare("SELECT banned, COALESCE(NULLIF(ban_reason, ''), 'Your local account is banned') AS reason FROM $localAuthTable WHERE uuid = ? LIMIT 1");
    if ($stmt) {
        $stmt->bind_param("s", $avatarUUID);
        $stmt->execute();
        $localAuth = $stmt->get_result()->fetch_assoc();
        $stmt->close();

        if ($localAuth && intval($localAuth['banned']) === 1) {
            log_traffic($conn, $avatarUUID, 'local_banned', 'local');
            echo '<?xml version="1.0" encoding="utf-8"?>
                <AuthorizationResponse>
                <IsAuthorized>false</IsAuthorized>
                <Message><![CDATA[' . htmlspecialchars($localAuth['reason'], ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8') . ']]></Message>
                </AuthorizationResponse>';
            $conn->close();
            $robust_conn->close();
            exit();
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
    echo '<?xml version="1.0" encoding="utf-8"?>
        <AuthorizationResponse>
        <IsAuthorized>true</IsAuthorized>
        <Message><![CDATA[Local user allowed]]></Message>
        </AuthorizationResponse>';
    $conn->close();
    $robust_conn->close();
    exit();
}

if (!$disableGridnameChecks) {
    $stmt = $conn->prepare("SELECT banned FROM $table_name WHERE uuid = ? AND gridname = ?");
    $stmt->bind_param("ss", $avatarUUID, $gridname);
} else {
    $stmt = $conn->prepare("SELECT banned FROM $table_name WHERE uuid = ?");
    $stmt->bind_param("s", $avatarUUID);
}

$stmt->execute();
$result = $stmt->get_result();
$user = $result->fetch_assoc();
$stmt->close();

if ($user) {
    if ($user['banned']) {
        log_traffic($conn, $avatarUUID, 'banned', $gridname);
        echo '<?xml version="1.0" encoding="utf-8"?>
            <AuthorizationResponse>
            <IsAuthorized>false</IsAuthorized>
            <Message><![CDATA[This Avatar is banned]]></Message>
            </AuthorizationResponse>';
    } else {
        log_traffic($conn, $avatarUUID, 'authorized', $gridname);
        echo '<?xml version="1.0" encoding="utf-8"?>
            <AuthorizationResponse>
            <IsAuthorized>true</IsAuthorized>
            <Message><![CDATA[Authorized]]></Message>
            </AuthorizationResponse>';
    }
    $conn->close();
    $robust_conn->close();
    exit();
}

if (!$disableGridnameChecks) {
    $bannedGrid = false;
    if (!empty($gridname)) {
        $stmt = $conn->prepare("SELECT gridname FROM banned_grids");
        $stmt->execute();
        $result = $stmt->get_result();
        while ($row = $result->fetch_assoc()) {
            if (strcasecmp($gridname, $row['gridname']) === 0) {
                $bannedGrid = $row['gridname'];
                break;
            }
        }
        $stmt->close();
    }

    if ($bannedGrid) {
        log_traffic($conn, $avatarUUID, 'banned_grid_' . $bannedGrid, $gridname);
        echo '<?xml version="1.0" encoding="utf-8"?>
            <AuthorizationResponse>
            <IsAuthorized>false</IsAuthorized>
            <Message><![CDATA[Access from "' . htmlspecialchars($bannedGrid) . '" is disabled permamently. If you believe this is error or want exception please contact us at support@is-on.click or on discord]]></Message>
            </AuthorizationResponse>';
        $conn->close();
        $robust_conn->close();
        exit();
    }

    $matchedGrid = false;
    if (!empty($gridname)) {
        $stmt = $conn->prepare("SELECT gridname FROM partners");
        $stmt->execute();
        $result = $stmt->get_result();
        while ($row = $result->fetch_assoc()) {
            if (strcasecmp($gridname, $row['gridname']) === 0) {
                $matchedGrid = $row['gridname'];
                break;
            }
        }
        $stmt->close();
    }

    if ($matchedGrid) {
        log_traffic($conn, $avatarUUID, 'partner_guest_' . $matchedGrid, $gridname);
        echo '<?xml version="1.0" encoding="utf-8"?>
            <AuthorizationResponse>
            <IsAuthorized>false</IsAuthorized>
            <Message><![CDATA[Hello visitor from ' . htmlspecialchars($matchedGrid) . '! You are welcome on our grid but see: https://i.let-us.cyou/hg-partner-allow-access/ if you need help feel free to contact me on discord or via email support@is-on.click]]></Message>
            </AuthorizationResponse>';
        $conn->close();
        $robust_conn->close();
        exit();
    }
}

log_traffic($conn, $avatarUUID, 'unauthorized_guest', $gridname);
echo '<?xml version="1.0" encoding="utf-8"?>
    <AuthorizationResponse>
    <IsAuthorized>false</IsAuthorized>
    <Message><![CDATA[Hypergrid travel is disabled or you are not authorized. For more information contact us at support@is-on.click]]></Message>
    </AuthorizationResponse>';

$conn->close();
$robust_conn->close();
?>
