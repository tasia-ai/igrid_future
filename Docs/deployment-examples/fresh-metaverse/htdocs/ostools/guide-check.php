<?php
declare(strict_types=1);
// Fresh Metaverse — PGSQL 127.0.0.1:5432 robust with environment-provided credentials.
// Formerly hardcoded I-Grid (i.let-us.cyou:8002) / AI Grid. Now unified to Fresh.

$dsnFresh = 'pgsql:host=127.0.0.1;port=5432;dbname=robust;';
$dbUser = getenv('FRESH_DB_USER') ?: 'opensim';
$dbPass = getenv('FRESH_DB_PASS') ?: 'CHANGE_ME';

function fresh_pdo(string $dsn, string $user, string $pass): PDO {
    $pdo = new PDO($dsn, $user, $pass, [PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION]);
    return $pdo;
}
try {
    $dbFresh = fresh_pdo($dsnFresh, $dbUser, $dbPass);
} catch (PDOException $e) {
    header('Content-Type: text/plain; charset=utf-8');
    http_response_code(500);
    echo "Fresh Metaverse DB unavailable: " . $e->getMessage();
    exit;
}
// Legacy $db1 name kept for compatibility but points to Fresh DB (teleporter table may not exist)
$db1 = $dbFresh;
$db2 = $dbFresh; // formerly AI Grid
$db3 = $dbFresh; // formerly I-Grid
$db4 = $dbFresh; // formerly ossearch events

// Option to enable or disable the URI check
$checkURIs = false; // Set to true to enable check, false to disable

// Function to check a single URI for AI Grid or I-Grid
function checkURI($url) {
    $curl = curl_init();
    curl_setopt($curl, CURLOPT_URL, $url . "/simstatus");
    curl_setopt($curl, CURLOPT_RETURNTRANSFER, true);
    $response = curl_exec($curl);
    curl_close($curl);
    return trim($response) === "OK";
}
/*----*/
// Set header for plain text output
header('Content-Type: text/plain');

// Section 1: Teleporter (TargetRegion, TargetLanding)
echo "Section Teleporter:\n";
$query1 = $db1->query("SELECT GridName, TargetRegion FROM Destinations");
while ($row = $query1->fetch(PDO::FETCH_ASSOC)) {
    echo $row['GridName'] . "|" . $row['TargetRegion'] . "\n";
}
/*----*/
/* Section 2: Fresh Metaverse (regionName, serverURI) */
echo "\nSection Fresh Metaverse:\n";
try {
    $query2 = $db2->query('SELECT "regionName", "serverURI" FROM regions ORDER BY "regionName"');
    while ($row = $query2->fetch(PDO::FETCH_ASSOC)) {
        $region = (string)($row['regionName'] ?? '');
        $serverUri = (string)($row['serverURI'] ?? '');
        // Normalize to os.tasia.work.gd:22000:<RegionName> if serverURI is internal http
        // viewer expects hg URI form host:port:RegionName
        $hgUri = 'os.tasia.work.gd:22000:' . $region;
        if ($serverUri !== '' && preg_match('#https?://([^/]+)#', $serverUri, $m)) {
            // if PGSQL already has correct external host, prefer it
            if (str_contains($m[1], 'os.tasia') || str_contains($m[1], 'ok.tasia')) {
                $hgUri = $m[1] . ':' . $region;
            }
        }
        if (!$checkURIs || checkURI($hgUri)) {
            echo $region . "|" . $hgUri . "\n";
        }
    }
} catch (Throwable $e) { echo "regions query failed: " . $e->getMessage() . "\n"; }

/* Section 3: Fresh Metaverse — legacy alias kept for viewers that still parse Section I-Grid */
echo "\nSection I-Grid (alias Fresh Metaverse):\n";
try {
    $query3 = $db3->query('SELECT "regionName", "serverURI" FROM regions ORDER BY "regionName"');
    while ($row = $query3->fetch(PDO::FETCH_ASSOC)) {
        $region = (string)($row['regionName'] ?? '');
        $hgUri = 'os.tasia.work.gd:22000:' . $region;
        if (!$checkURIs || checkURI($hgUri)) {
            echo $region . "|" . $hgUri . "\n";
        }
    }
} catch (Throwable $e) { echo "regions alias query failed: " . $e->getMessage() . "\n"; }

// Section 4: Events (from regions as pseudo-events; real events table may not exist on PGSQL)
echo "\nSection Events:\n";
try {
    $query4 = $db4->query('SELECT regionName AS name, "regionName" AS simname, 0 AS dateUTC FROM regions LIMIT 20');
    while ($row = $query4->fetch(PDO::FETCH_ASSOC)) {
        echo $row['name'] . "_" . $row['dateUTC'] . "|" . $row['simname'] . "\n";
    }
} catch (Throwable $e) {
    echo "No events table (PGSQL robust): " . $e->getMessage() . "\n";
}
?>
/* Fresh Metaverse — guide-check updated 2026-09-14 for PGSQL robust */
