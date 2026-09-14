<?php
declare(strict_types=1);
header('Content-Type: application/json; charset=utf-8');
header('Access-Control-Allow-Origin: *');

function clean_region(array $r): array {
    $name = $r['regionName'] ?? $r['regionname'] ?? $r['RegionName'] ?? $r['name'] ?? 'Unknown';
    $x = (int)($r['locx'] ?? $r['locX'] ?? $r['x'] ?? 0);
    $y = (int)($r['locy'] ?? $r['locY'] ?? $r['y'] ?? 0);
    $sizeX = (int)($r['sizex'] ?? $r['sizeX'] ?? $r['size'] ?? 256);
    $sizeY = (int)($r['sizey'] ?? $r['sizeY'] ?? $r['size'] ?? $sizeX);
    if ($sizeX < 32) $sizeX *= 256;
    if ($sizeY < 32) $sizeY *= 256;
    return [
        'name' => (string)$name,
        'x' => $x,
        'y' => $y,
        'sizeX' => $sizeX ?: 256,
        'sizeY' => $sizeY ?: 256,
        'teleport' => 'secondlife://os.tasia.work.gd:22000/' . rawurlencode((string)$name) . '/128/128/25',
        'hg' => 'os.tasia.work.gd:22000:' . (string)$name,
    ];
}

$regions = [];
try {
    $dbHost = getenv('FRESH_DB_HOST') ?: '127.0.0.1';
    $dbPort = getenv('FRESH_DB_PORT') ?: '5432';
    $dbName = getenv('FRESH_DB_NAME') ?: 'robust';
    $dbUser = getenv('FRESH_DB_USER') ?: 'opensim';
    $dbPass = getenv('FRESH_DB_PASS') ?: 'CHANGE_ME';
    $pdo = new PDO("pgsql:host=$dbHost;port=$dbPort;dbname=$dbName", $dbUser, $dbPass, [
        PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
        PDO::ATTR_TIMEOUT => 2,
    ]);
    $stmt = $pdo->query('SELECT * FROM regions ORDER BY "regionName"');
    foreach ($stmt->fetchAll(PDO::FETCH_ASSOC) as $row) $regions[] = clean_region($row);
} catch (Throwable $e) {
    $deploy = 'H:/grid/igrid-package/generated/deploy.json';
    if (is_file($deploy)) {
        $json = json_decode((string)file_get_contents($deploy), true);
        foreach (($json['sims'] ?? []) as $row) $regions[] = clean_region($row);
    }
}

echo json_encode([
    'grid' => 'Fresh Metaverse',
    'login_uri' => 'os.tasia.work.gd:22000',
    'map_tile_url' => 'https://os.tasia.work.gd:22000/MapService/',
    'regions' => $regions,
    'count' => count($regions),
], JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES);
