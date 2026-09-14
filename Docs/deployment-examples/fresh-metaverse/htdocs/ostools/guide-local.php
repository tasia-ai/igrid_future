<?php
declare(strict_types=1);
header('Content-Type: text/html; charset=utf-8');
$regions = [];
try {
  $dbHost = getenv('FRESH_DB_HOST') ?: '127.0.0.1';
  $dbPort = getenv('FRESH_DB_PORT') ?: '5432';
  $dbName = getenv('FRESH_DB_NAME') ?: 'robust';
  $dbUser = getenv('FRESH_DB_USER') ?: 'opensim';
  $dbPass = getenv('FRESH_DB_PASS') ?: 'CHANGE_ME';
  $pdo = new PDO("pgsql:host=$dbHost;port=$dbPort;dbname=$dbName", $dbUser, $dbPass, [PDO::ATTR_ERRMODE=>PDO::ERRMODE_EXCEPTION, PDO::ATTR_TIMEOUT=>2]);
  $regions = $pdo->query('SELECT "regionName", "locX", "locY", "sizeX", "sizeY" FROM regions ORDER BY "regionName"')->fetchAll(PDO::FETCH_ASSOC);
} catch (Throwable $e) {
  $regions = [];
}
?>
<!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
<title>Fresh Metaverse — Teleport Guide</title>
<link href="https://fonts.googleapis.com/css2?family=Nunito:wght@700;800&family=Pacifico&display=swap" rel=stylesheet>
<style>
*{box-sizing:border-box}body{margin:0;background:#FFF7FB;font-family:Nunito,system-ui;color:#4A3040}
a{color:#C14B7A;text-decoration:none}.wrap{max-width:900px;margin:0 auto;padding:18px}
.card{background:#fff;border:1px solid #FFE0EA;border-radius:16px;padding:14px 16px;margin:10px 0;display:flex;align-items:center;gap:12px;box-shadow:0 4px 12px rgba(255,143,171,.12)}
.name{font-weight:800;flex:1}.meta{color:#8A6A7A;font-size:12px}.btn{background:linear-gradient(135deg,#FF8FAB,#FFB5D8);color:#fff;padding:8px 14px;border-radius:999px;font-weight:800;white-space:nowrap}
h1{font-family:Pacifico,cursive;text-align:center;margin:16px 0 8px}
.small{color:#8A6A7A;text-align:center;font-size:13px}
</style>
<div class=wrap>
<h1>🌸 Teleport Guide — Fresh Metaverse</h1>
<p class=small>Click <b>Teleport</b> to hop. Login URI <b>os.tasia.work.gd:22000</b></p>
<?php if (!$regions): ?>
<div class=card><span class=name>Region database is unavailable.</span><a class=btn href="/amber/map.html">Open map</a></div>
<?php endif; ?>
<?php foreach($regions as $row): $r=$row['regionName']; $n=h($r); $hg="os.tasia.work.gd:22000:".$r; ?>
<div class=card><span class=name>📍 <?= $n ?><br><span class=meta><?= (int)$row['locX'] ?>, <?= (int)$row['locY'] ?> • <?= (int)$row['sizeX'] ?>x<?= (int)$row['sizeY'] ?>m</span></span><a class=btn href="secondlife://<?= h($hg) ?>/128/128/25">Teleport →</a></div>
<?php endforeach; ?>
<p class=small style="margin-top:18px"><a href="/amber/">← Back to Amber</a> • <a href="/amber/map.html">Map</a> • <a href="/search/">Search</a></p>
</div>
<?php function h($s){return htmlspecialchars($s,ENT_QUOTES,'UTF-8');} ?>
