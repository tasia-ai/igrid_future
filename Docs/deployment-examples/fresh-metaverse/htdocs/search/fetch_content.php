<?php
// fetch_content.php
if (!isset($_GET['tab'])) {
    http_response_code(400);
    echo '<p>Invalid tab request.</p>';
    exit;
}

$tab = filter_var($_GET['tab'], FILTER_SANITIZE_STRING);

// Fresh Metaverse — updated from I-Grid (search.i.let-us.cyou) to os.tasia.work.gd/search
// Keeps fallback URLs pointing to Fresh grid so fetch still works if local missing; also keeps PGSQL search working
$contentSources = [
    'home' => ['type' => 'local', 'source' => 'static/welcome.php', 'url' => 'https://os.tasia.work.gd/search/static/welcome.php'],
    'search' => ['type' => 'local', 'source' => 'static/search.php', 'url' => 'https://os.tasia.work.gd/search/static/search.php'],
    'web' => ['type' => 'local', 'source' => 'static/websearch.php', 'url' => 'https://os.tasia.work.gd/search/static/websearch.php'],
    'tasia' => ['type' => 'local', 'source' => 'static/chat/index.php', 'url' => 'https://os.tasia.work.gd/search/static/chat/index.php'],
    'team' => ['type' => 'local', 'source' => 'static/team.php', 'url' => 'https://os.tasia.work.gd/search/static/team.php'],
    'tos' => ['type' => 'local', 'source' => 'static/codex.php', 'url' => 'https://os.tasia.work.gd/search/static/codex.php'],
    'tawk' => ['type' => 'local', 'source' => 'static/livechat.php', 'url' => 'https://os.tasia.work.gd/search/static/livechat.php'],
    'doritos' => ['type' => 'local', 'source' => 'static/buy.php', 'url' => 'https://os.tasia.work.gd/search/static/buy.php'],
    'status' => ['type' => 'local', 'source' => 'static/status.php', 'url' => 'https://os.tasia.work.gd/search/static/status.php'],
    'opensimworld' => ['type' => 'local', 'source' => 'static/proxy.php', 'url' => 'https://os.tasia.work.gd/search/static/proxy.php'],
    'market' => ['type' => 'local', 'source' => 'static/market.php', 'url' => 'https://os.tasia.work.gd/search/market.php'],
    'marketplace' => ['type' => 'local', 'source' => 'static/market.php', 'url' => 'https://os.tasia.work.gd/search/static/market.php'],
    'guide' => ['type' => 'local', 'source' => 'static/guide.php', 'url' => 'https://os.tasia.work.gd/search/static/guide.php'],
    'support' => ['type' => 'url','source' => 'static/support/support.php', 'url' => 'https://os.tasia.work.gd/search/static/support/support.php'],
    'fdc' => ['type' => 'url','source' => 'static/imagegen-iframe.php', 'url' => 'https://os.tasia.work.gd/search/static/imagegen-iframe.php'],
    'fdd' => ['type' => 'url','source' => 'soon.txt', 'url' => 'https://os.tasia.work.gd/search/static/imagegen-iframe.php'],
    'fde' => ['type' => 'url', 'url' => 'https://fresh-projects.top/web/Fresh-Metavers-Script-Tools.html'],
    'fdf' => ['type' => 'url', 'url' => 'https://fresh-projects.top/web/Downlaod-Tools.html'],
    'retro' => ['type' => 'local', 'source' => 'static/retro-proxy.php', 'url' => 'https://os.tasia.work.gd/search/static/retro-proxy.php'],
];

if (!array_key_exists($tab, $contentSources)) {
    http_response_code(404);
    echo '<p>Tab content not found.</p>';
    exit;
}

$source = $contentSources[$tab];

// Handle local files
if ($source['type'] === 'local') {
    if (file_exists($source['source'])) {
        ob_start();
        include_once $source['source'];
        $content = ob_get_clean();
        echo $content ?: '<p>Content is currently unavailable.</p>';
    } elseif (isset($source['url'])) {
        $ch = curl_init($source['url']);
        curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
        curl_setopt($ch, CURLOPT_FOLLOWLOCATION, true);
        curl_setopt($ch, CURLOPT_TIMEOUT, 10);
        $content = curl_exec($ch);
        $httpCode = curl_getinfo($ch, CURLINFO_HTTP_CODE);
        curl_close($ch);
        echo $content && $httpCode == 200 ? $content : '<p>Content is currently unavailable.</p>';
    } else {
        echo '<p>Content is currently unavailable.</p>';
    }
}
// Handle external URLs
elseif ($source['type'] === 'url') {
    $ch = curl_init($source['url']);
    curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
    curl_setopt($ch, CURLOPT_FOLLOWLOCATION, true);
    curl_setopt($ch, CURLOPT_TIMEOUT, 10);
    $content = curl_exec($ch);
    $httpCode = curl_getinfo($ch, CURLINFO_HTTP_CODE);
    curl_close($ch);
    echo $content && $httpCode == 200 ? $content : '<p>Content is currently unavailable.</p>';
}
?>