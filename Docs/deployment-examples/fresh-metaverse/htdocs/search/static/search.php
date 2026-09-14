<?php
// Fresh Metaverse — OpenSim Search (PGSQL, reads Robust DB like oswelcome-amber.php)
// Grid: Fresh Metaverse | Login: os.tasia.work.gd:22000 | DB: 127.0.0.1/5432/robust with environment-provided credentials
// Viewer also calls /search?search=term -> handled in index.php; this is the human UI.
$q = trim($_GET['q'] ?? $_POST['q'] ?? '');
$results = [];
$err = '';
if ($q !== '') {
    $db_host=getenv('FRESH_DB_HOST') ?: '127.0.0.1'; $db_port=getenv('FRESH_DB_PORT') ?: '5432'; $db_name=getenv('FRESH_DB_NAME') ?: 'robust'; $db_user=getenv('FRESH_DB_USER') ?: 'opensim'; $db_pass=getenv('FRESH_DB_PASS') ?: 'CHANGE_ME';
    $dsn="pgsql:host={$db_host};port={$db_port};dbname={$db_name};";
    try{
        $pdo=new PDO($dsn,$db_user,$db_pass,[PDO::ATTR_ERRMODE=>PDO::ERRMODE_EXCEPTION,PDO::ATTR_TIMEOUT=>2]);
        $stmt=$pdo->prepare('SELECT regionName, locX, locY, sizeX, sizeY FROM regions WHERE regionName ILIKE :q ORDER BY regionName LIMIT 30');
        $stmt->execute([':q'=>'%'.$q.'%']);
        $results=$stmt->fetchAll(PDO::FETCH_ASSOC);
        if(!$results){
            // try quoted identifiers
            $stmt=$pdo->prepare('SELECT "RegionName" as regionname, "locX" as locx FROM regions WHERE "RegionName" ILIKE :q LIMIT 30');
            try{ $stmt->execute([':q'=>'%'.$q.'%']); $results=$stmt->fetchAll(PDO::FETCH_ASSOC);}catch(Exception $e2){}
        }
    }catch(Exception $e){ $err=$e->getMessage(); }
}
?>
<div style="max-width:820px; margin:auto; font-family:'Nunito',sans-serif;">
  <h2 style="color:#4A3040; text-align:center; font-family:'Poppins',sans-serif;">🔍 Fresh Search ✨</h2>
  <p style="text-align:center; color:#8A6A7A; font-size:13px;">Search regions on <b>Fresh Metaverse</b> • <code>os.tasia.work.gd:22000</code> • API: <code>?q=term</code> or <code>/search?search=term</code></p>
  <form method="get" action="" style="display:flex; gap:8px; justify-content:center; margin:14px 0;">
    <input type="hidden" name="tab" value="search">
    <input name="q" value="<?php echo htmlspecialchars($q); ?>" placeholder="Search region name… e.g. Welcome" style="flex:1; max-width:420px; padding:10px 14px; border:2px solid #FFB5D8; border-radius:999px; outline:none;">
    <button type="submit" style="background:linear-gradient(135deg,#FF8FAB,#FFB5D8); color:white; border:none; padding:10px 18px; border-radius:999px; font-weight:800; cursor:pointer;">Search</button>
  </form>
  <?php if($q!==''): ?>
    <div style="background:white; border:1px solid #FFE0EA; border-radius:16px; padding:14px; box-shadow:0 8px 24px rgba(255,181,216,0.12);">
      <b>Results for “<?php echo htmlspecialchars($q); ?>”</b> — <?php echo count($results); ?> found
      <?php if($err): ?><div style="color:#A33; font-size:12px; margin-top:6px;">DB: <?php echo htmlspecialchars($err); ?> (demo mode if DB offline)</div><?php endif; ?>
      <?php if($results): ?>
        <ul style="list-style:none; padding:0; margin:10px 0;">
        <?php foreach($results as $r):
          $name=$r['regionname'] ?? $r['regionName'] ?? $r['RegionName'] ?? 'Unknown';
          $x=(int)($r['locx'] ?? $r['locX'] ?? 0); $y=(int)($r['locy'] ?? $r['locY'] ?? 0);
          $hg = 'secondlife://os.tasia.work.gd:22000/'.rawurlencode($name).'/128/128/25';
        ?>
          <li style="padding:8px 10px; border-bottom:1px solid #FFF0F5; display:flex; justify-content:space-between; align-items:center;">
            <span><b><?php echo htmlspecialchars($name); ?></b> <small style="color:#8A6A7A;">@ <?php echo $x; ?>,<?php echo $y; ?></small></span>
            <a href="<?php echo htmlspecialchars($hg); ?>" style="background:#FFF0F5; border:1px solid #FFE0EA; padding:6px 10px; border-radius:999px; font-size:12px; font-weight:700; text-decoration:none;">Teleport ↗</a>
          </li>
        <?php endforeach; ?></ul>
      <?php else: ?><p style="color:#8A6A7A;">No regions matched. Try a broader term.</p><?php endif; ?>
      <p style="font-size:11px; color:#8A6A7A;">Viewer API: <code>https://os.tasia.work.gd/search?search=<?php echo htmlspecialchars($q); ?></code> returns JSON. Also <code>?q=</code>, <code>?query=</code> work.</p>
    </div>
  <?php else: ?>
    <p style="text-align:center; color:#8A6A7A;">Try searching “Welcome”, “Amber”, “Plaza”… or browse <a href="#" onclick="parent.openTab(event,'guide');return false;">Destinations Guide</a>.</p>
  <?php endif; ?>
</div>
