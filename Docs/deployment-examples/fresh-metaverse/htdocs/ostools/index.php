<?php
declare(strict_types=1);
// Fresh Metaverse — ostools landing (economy) — Amber pastel. Advertised as economy = https://os.tasia.work.gd/ostools/
header('Content-Type: text/html; charset=utf-8');
$economy = 'https://os.tasia.work.gd/ostools/';
$loginUri = 'os.tasia.work.gd:22000';
$gridName = 'Fresh Metaverse';
$webProfilePattern = 'os.tasia.work.gd/ostools/profile/[AGENT_NAME]';
?>
<!doctype html>
<html lang="en"><head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Fresh Metaverse — Economy & Helpers 💖</title>
<link href="https://fonts.googleapis.com/css2?family=Nunito:wght@400;600;700;800&family=Pacifico&family=Poppins:wght@500;700&display=swap" rel="stylesheet">
<link rel="stylesheet" href="/ostools/static/amber.css">
</head><body>
<div class="blob blob-1"></div><div class="blob blob-2"></div>
<nav class="nav"><div class="nav-inner">
  <a href="/amber/" class="logo">💖 Amber's <span class="logo-pink">Fresh Metaverse</span></a>
  <div class="nav-links"><a href="/amber/">Home</a><a href="/ostools/guide.php">Guide</a><a href="https://os.tasia.work.gd/search">Search</a><a href="/ostools/profile/Amber">Profiles</a></div>
  <span class="badge">Grid: <?=htmlspecialchars($gridName)?></span>
</div></nav>
<section class="hero"><div class="container">
  <span class="eyebrow">Economy helper • Viewer buy & Land</span>
  <h1 class="kawaii-glitch" style="margin:10px 0 8px">Fresh Metaverse Economy 🌸</h1>
  <p class="muted">Helpers the viewer calls. <b>Login URI is always <code><?=htmlspecialchars($loginUri)?></code></b>. No I-Grid leftovers.</p>
  <div style="display:grid;grid-template-columns:1.2fr .8fr;gap:18px;margin-top:18px">
    <div class="card">
      <h3 style="margin:6px 0">🔧 Helper endpoints (what GridInfo advertises)</h3>
      <table>
        <tr><th>GridInfo key</th><th>Value</th></tr>
        <tr><td><code>login</code></td><td><code>http://<?=$loginUri?>/</code></td></tr>
        <tr><td><code>gridname</code></td><td><?=htmlspecialchars($gridName)?></td></tr>
        <tr><td><code>economy</code></td><td><code><?=htmlspecialchars($economy)?></code> → suffix <code>currency.php</code> / <code>landtool.php</code></td></tr>
        <tr><td><code>web_profile_url</code></td><td><code><?=htmlspecialchars($webProfilePattern)?></code></td></tr>
      </table>
      <p class="muted" style="font-size:13px;margin:10px 0 0">Viewer builds helper URLs as <code>{economy}currency.php</code>. Our nginx/Apache proxies <code>/ostools/currency.php</code> and <code>/landtool.php</code> to <code>127.0.0.1:8015</code> (bridge).</p>
      <div class="login-uri-box" style="margin-top:14px"><span class="mono"><?=htmlspecialchars($loginUri)?></span><span class="uri-label">Login URI — paste in viewer</span></div>
    </div>
    <div class="card" style="background:linear-gradient(135deg,#FFF0F5,#E8DFF5)">
      <h3 style="margin:6px 0">💖 For residents</h3>
      <ul style="line-height:1.9">
        <li><b>Buy currency</b> in-viewer uses <code>POST /ostools/currency.php</code> (bridge mode <code>custom</code> → upstream <code>127.0.0.1:1026</code>)</li>
        <li><b>Buy land</b> uses <code>POST /ostools/landtool.php</code> (same bridge)</li>
        <li><b>Profile</b> links open <code>/ostools/profile/[Name]</code> — cute pastel page backed by PGSQL <code>robust</code></li>
        <li><b>Guide</b> at <a href="/ostools/guide.php">/ostools/guide.php</a> (local PGSQL fallback, pastel cards + copy HG URI)</li>
      </ul>
      <a class="btn btn-pill btn-primary" href="/ostools/guide.php">Open Destination Guide →</a>
    </div>
  </div>
  <div class="grid" style="margin-top:18px">
    <div class="card"><b>PGSQL</b> <span class="tag">127.0.0.1:5432 robust/env credentials</span><br><span class="muted" style="font-size:13px">Robust DB provider PGSQL. This ostools no longer uses mysql i.let-us.cyou:3306.</span></div>
    <div class="card"><b>Bridge</b> <span class="tag">127.0.0.1:8015</span><br><span class="muted" style="font-size:13px">Python bridge <code>app.py</code> + <code>config.json</code> public_base_url <code><?=htmlspecialchars($economy)?></code>. Health at <a href="/ostools/healthz.php">healthz.php</a>.</span></div>
    <div class="card"><b>Profile pattern</b><br><code>os.tasia.work.gd/ostools/profile/[AGENT_NAME]</code> — viewer substitutes e.g. <code>Amber Resident</code>. Our <code>.htaccess</code> rewrites to <code>profile.php?name=...</code>.</div>
  </div>
  <div class="card" style="margin-top:18px"><h3 style="margin:6px 0">Quick test</h3><code>curl -X POST http://127.0.0.1:8015/currency.php -H 'Content-Type: text/xml' --data-binary @test.xml</code> — see <code>tests/smoke_test.py</code> and <code>bridge_proxy.php</code>. Production viewer hits <code>https://os.tasia.work.gd/ostools/currency.php</code>.</div>
</div></section>
<footer class="footer"><div class="container" style="display:flex;gap:12px;flex-wrap:wrap;justify-content:space-between"><span>© Amber's Fresh Metaverse — economy helper 💖</span><span><a href="/amber/">Home</a><a href="/ostools/healthz.php">healthz</a><a href="/ostools/guide-check.php">guide-check</a></span></div><div class="copy">economy <b><?=htmlspecialchars($economy)?></b> • web_profile_url <b><?=htmlspecialchars($webProfilePattern)?></b> • login <b><?=htmlspecialchars($loginUri)?></b></div></footer>
</body></html>
