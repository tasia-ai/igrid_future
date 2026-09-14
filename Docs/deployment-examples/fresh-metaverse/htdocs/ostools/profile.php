<?php
declare(strict_types=1);
// Fresh Metaverse — web_profile_url = os.tasia.work.gd/ostools/profile/[AGENT_NAME]
// Viewer replaces [AGENT_NAME] with e.g. "Amber Resident" (space) or "Amber.Resident".
// This script answers both /ostools/profile.php?name=... and pretty /ostools/profile/Name via .htaccess rewrite.

header('Content-Type: text/html; charset=utf-8');

// normalize input: from query, path_info or routed param
$raw = (string)($_GET['name'] ?? $_GET['agent'] ?? '');
if ($raw === '' && isset($_SERVER['REQUEST_URI'])) {
    $uri = (string)$_SERVER['REQUEST_URI'];
    // try /ostools/profile/Amber or /ostools/profile/Amber%20Resident
    if (preg_match('#/ostools/profile/([^?]+)#i', $uri, $m)) {
        $raw = urldecode($m[1]);
    } elseif (preg_match('#/profile/([^?]+)#', $uri, $m)) {
        $raw = urldecode($m[1]);
    }
}
$raw = trim($raw);
if ($raw === '' && isset($_GET['agent_name'])) $raw = (string)$_GET['agent_name'];
// viewer may send [AGENT_NAME] literally on test — show help
if ($raw === '' || $raw === '[AGENT_NAME]') { $raw = ''; }

function split_agent_name(string $raw): array {
    $raw = trim($raw);
    if ($raw === '') return ['',''];
    // Allow "First Last", "First.Last", "First_Last"
    $raw = str_replace(['.', '_'], ' ', $raw);
    $parts = preg_split('/\s+/', $raw);
    if (count($parts) === 1) return [$parts[0], 'Resident'];
    // join remainder as last (supports "Anne Marie Last")
    $first = array_shift($parts);
    $last = implode(' ', $parts);
    return [$first, $last];
}

[$first, $last] = split_agent_name($raw);
$displayName = $first !== '' ? trim($first.' '.$last) : '';

$dsn='pgsql:host=127.0.0.1;port=5432;dbname=robust;';
$user=getenv('FRESH_DB_USER') ?: 'opensim'; $pass=getenv('FRESH_DB_PASS') ?: 'CHANGE_ME';
$found=null; $error=null; $profile=null;
if ($displayName !== '') {
    try{
        $pdo=new PDO($dsn,$user,$pass,[PDO::ATTR_ERRMODE=>PDO::ERRMODE_EXCEPTION]);
        // useraccounts is lowercase PGSQL
        $stmt=$pdo->prepare('SELECT "PrincipalID","FirstName","LastName","Created","UserLevel" FROM useraccounts WHERE lower("FirstName")=lower(:first) AND lower("LastName")=lower(:last) LIMIT 1');
        $stmt->execute(['first'=>$first,'last'=>$last]);
        $found=$stmt->fetch(PDO::FETCH_ASSOC);
        if($found){
            $uuid=$found['PrincipalID'];
            // join profile text if exists
            $st=$pdo->prepare('SELECT * FROM userprofile WHERE useruuid=:uuid LIMIT 1');
            $st->execute(['uuid'=>$uuid]);
            $profile=$st->fetch(PDO::FETCH_ASSOC);
        }
    }catch(Throwable $e){ $error=$e->getMessage(); }
}
?>
<!doctype html>
<html lang="en"><head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title><?= $displayName?htmlspecialchars($displayName).' — Fresh Metaverse 💖' : 'Fresh Metaverse — Profile 💖' ?></title>
<link href="https://fonts.googleapis.com/css2?family=Nunito:wght@400;600;700;800&family=Pacifico&family=Poppins:wght@500;700&display=swap" rel="stylesheet">
<link rel="stylesheet" href="/ostools/static/amber.css">
</head><body>
<div class="blob blob-1"></div><div class="blob blob-2"></div>
<nav class="nav"><div class="nav-inner">
  <a href="/amber/" class="logo">💖 Amber's <span class="logo-pink">Fresh Metaverse</span></a>
  <div class="nav-links"><a href="/amber/">Home</a><a href="/ostools/">Economy</a><a href="/ostools/guide.php">Guide</a></div>
  <span class="badge">web_profile_url</span>
</div></nav>
<section class="section"><div class="container">
  <span class="eyebrow">Fresh Metaverse • os.tasia.work.gd:22000</span>
  <?php if($displayName===''): ?>
    <h1 class="kawaii-glitch" style="margin:12px 0 6px">Profiles 💖</h1>
    <p class="muted">Viewer calls <code>os.tasia.work.gd/ostools/profile/[AGENT_NAME]</code> — e.g. <code>os.tasia.work.gd/ostools/profile/Amber Resident</code>.</p>
    <div class="card">
      <form method="get" action="/ostools/profile.php" style="display:flex;gap:8px;flex-wrap:wrap;align-items:center">
        <input name="name" placeholder="Amber Resident" style="flex:1;min-width:220px;padding:10px 14px;border-radius:999px;border:2px solid #FFE0EA;font-weight:700" value="<?=htmlspecialchars($raw)?>">
        <button class="btn btn-pill btn-primary">View profile →</button>
      </form>
      <p class="muted" style="font-size:13px;margin:8px 0 0">Backend: PGSQL <code>127.0.0.1:5432 robust/env credentials</code> tables <code>useraccounts</code> + <code>userprofile</code>.</p>
    </div>
  <?php else: ?>
    <h1 class="kawaii-glitch" style="margin:12px 0 6px"><?=htmlspecialchars($displayName)?> 🌸</h1>
    <p class="muted">HG: <code>os.tasia.work.gd:22000:<?=htmlspecialchars($displayName)?></code> • URL <code>/ostools/profile/<?=urlencode($displayName)?></code></p>
    <?php if($error): ?><div class="card" style="border-color:#FFB5B5;background:#FFF0F0">DB error: <?=htmlspecialchars($error)?></div>
    <?php elseif(!$found): ?><div class="card" style="border-color:#FFD1B5;background:#FFF7EB">No resident found for <b><?=htmlspecialchars($displayName)?></b> on Fresh Metaverse. Check spelling (First Last) or create an account at <a href="https://os.tasia.work.gd/amber/register.php">Amber Registration</a>.</div>
    <?php else: ?>
      <div class="card" style="background:linear-gradient(180deg,white,#FFF7FB);text-align:center;max-width:520px">
        <div style="width:96px;height:96px;margin:0 auto 12px;background:linear-gradient(135deg,#FFB5D8,#C9B6FF);border-radius:50%;display:flex;align-items:center;justify-content:center;font-size:48px">🌸</div>
        <h2 style="margin:6px 0"><?=htmlspecialchars($found['FirstName'].' '.$found['LastName'])?></h2>
        <div class="muted" style="font-size:13px">UUID <code class="mono"><?=htmlspecialchars($found['PrincipalID'])?></code> • Level <?=htmlspecialchars((string)$found['UserLevel'])?></div>
        <?php if($profile): ?>
          <div style="text-align:left;margin-top:14px;background:#FFF0F5;padding:12px;border-radius:14px">
            <div style="font-weight:800">About</div>
            <div style="white-space:pre-wrap"><?= $profile['profileAboutText']?htmlspecialchars($profile['profileAboutText']):'<span class="muted">— no about text yet —</span>' ?></div>
            <?php if(!empty($profile['profileURL'])): ?><div style="margin-top:8px">🔗 <a href="<?=htmlspecialchars($profile['profileURL'])?>" target="_blank"><?=htmlspecialchars($profile['profileURL'])?></a></div><?php endif; ?>
          </div>
        <?php else: ?><div class="muted" style="margin-top:10px">No extended profile stored.</div><?php endif; ?>
      </div>
    <?php endif; ?>
    <div style="margin-top:14px"><a class="btn btn-pill btn-ghost" href="/ostools/profile.php">← Search another</a> <a class="btn btn-pill btn-primary" href="/ostools/">Economy</a></div>
  <?php endif; ?>
  <div class="card" style="margin-top:18px;font-size:13px">Pattern configured in Robust.ini: <code>web_profile_url = os.tasia.work.gd/ostools/profile/[AGENT_NAME]</code> — viewer substitutes [AGENT_NAME]. Pretty URL works via <code>.htaccess</code> → <code>profile.php?name=...</code>.</div>
</div></section>
<footer class="footer"><div class="copy">Profile helper 💖 Fresh Metaverse • login <b>os.tasia.work.gd:22000</b></div></footer>
</body></html>
