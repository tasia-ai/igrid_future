<!doctype html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <title>Fresh Metaverse — Feed + Status ♡</title>
    <style>
    body { background-color:#FFF7FB;font-family: 'Nunito','Poppins',sans-serif; padding: 20px; color: #4A3040 !important; }
        .card { 
            border-bottom: 1px solid #ddd; 
            padding: 8px; /* Reduced padding for tighter fit */
            margin: 10px 0; 
            border-radius: 8px; 
            display: inline-block; /* Shrink to content width */
        }
        .card2 { 
            border: 1px solid #ddd;
            padding: 8px; /* Reduced padding for tighter fit */
            margin: 10px; 
            border-radius: 8px;
            display: inline-block; /* Shrink to content width */
            
        }
        .muted { color: #666; }
        .badge-img { 
            vertical-align: middle; 
            margin-left: 10px; 
            max-height: 24px; 
        }
        h4 { 
            margin: 0; /* Remove default margin for tighter fit */
            display: block; /* Ensure h4 is inline with badge */
        }
    </style>
</head>
<body>
<center>
<?php
error_reporting(E_ALL);
ini_set('display_errors', 1);

function fetch_json($url, $timeout = 10) {
    if (function_exists('curl_init')) {
        $ch = curl_init($url);
        curl_setopt_array($ch, [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_FOLLOWLOCATION => true,
            CURLOPT_TIMEOUT => $timeout,
            CURLOPT_CONNECTTIMEOUT => $timeout,
            CURLOPT_USERAGENT => 'Mozilla/5.0 (PHP Fetcher)',
        ]);
        $data = curl_exec($ch);
        if ($data === false) {
            return ['error' => 'cURL error: ' . curl_error($ch)];
        }
        curl_close($ch);
        return json_decode($data, true);
    } else {
        $data = @file_get_contents($url);
        if ($data === false) {
            return ['error' => 'Failed to fetch URL: ' . $url];
        }
        return json_decode($data, true);
    }
}

function process_wordpress_feed($url) {
    $feed = fetch_json($url);
    if (isset($feed['error'])) {
        return "<div>Error: {$feed['error']}</div>";
    }
    if (!$feed || empty($feed['items'])) {
        return "<div>Error: Could not parse WordPress JSON feed.</div>";
    }

    $output = '';
    foreach ($feed['items'] as $item) {
        $date = $item['date_published'] ?? '';
        $content = $item['content_html'] ?? $item['content_text'] ?? '';

        $output .= '<div class="card">';
        if ($date) {
            $output .= '<div class="muted">Published: ' . htmlspecialchars($date) . '</div>';
        }
        if ($content) {
            $output .= '<div>' . $content . '</div>';
        }
        $output .= '</div>';
    }
    return $output;
}

function process_kuma_status($url) {
    $data = fetch_json($url);
    if (isset($data['error'])) {
        return "<div>Error: {$data['error']}</div>";
    }
    if (!$data) {
        return "<div>Error: Could not parse JSON from Kuma.</div>";
    }

    $title = $data['statusPage']['title'] ?? 'Grid and Regions';
    $output = "";/*"<h2>Status: " . htmlspecialchars($title) . "</h2>";*/
    $groups = $data['publicGroupList'] ?? [];
    if (empty($groups)) {
        $output .= "<div>No monitor groups found.</div>";
        return $output;
    }

    foreach ($groups as $group) {
        $group_name = $group['name'] ?? 'Unnamed Group';
       /* $output .= "<h3>" . htmlspecialchars($group_name) . "</h3>";*/

        $monitors = $group['monitorList'] ?? [];
        if (empty($monitors)) {
            $output .= "<div class='muted'>No monitors in this group.</div>";
            continue;
        }

        foreach ($monitors as $m) {
            $name = $m['name'] ?? 'Monitor';
            $monitor_id = $m['id'] ?? null;

            $output .= '<div class="card2">';
            $output .= '<h4>' . htmlspecialchars($name) . '</h4>';
            if ($monitor_id) {
                $badge_url = "https://status.is-on.click/api/badge/$monitor_id/status";
                $output .= '<img src="' . htmlspecialchars($badge_url) . '" alt="Status Badge for ' . htmlspecialchars($name) . '" class="badge-img">';
            } else {
                $output .= '<div class="muted">No badge available (missing monitor ID).</div>';
            }
            $output .= '</div>';
        }
    }
    return $output;
}

// Fresh Metaverse — show own grid stats from Robust PGSQL + try legacy feeds with fallback
echo '<p style="font-size:22px !important; color:#4A3040;"><strong>🌸 Fresh Metaverse — Live</strong> <small style="font-size:12px; color:#8A6A7A;">os.tasia.work.gd:22000</small></p>';
// Try to show Robust live stats via oswelcome-amber.php JSON (PGSQL)
$freshApi = @file_get_contents('http://127.0.0.1:80/Web/oswelcome-amber.php?api=json', false, stream_context_create(['http'=>['timeout'=>2]]));
if(!$freshApi) $freshApi = @file_get_contents('http://os.tasia.work.gd:22000/../Web/oswelcome-amber.php?api=json', false, stream_context_create(['http'=>['timeout'=>2]]));
if($freshApi && ($j=json_decode($freshApi,true))){
    echo '<div style="background:white; border:1px solid #FFE0EA; border-radius:16px; padding:12px; display:inline-block; box-shadow:0 4px 12px rgba(255,181,216,0.12);">';
    echo '<b>Status: '.htmlspecialchars($j['Status'] ?? '—').'</b> &nbsp; Online: '.(int)($j['UsersOnline'] ?? 0).' &nbsp; Regions: '.(int)($j['TotalRegions'] ?? 0).' &nbsp; Users: '.(int)($j['TotalUsers'] ?? 0);
    echo '</div><br><br>';
}
echo '<p style="font-size:16px !important; color:#8A6A7A;"><strong>Legacy Grid News Feed</strong> (falls back to Fresh if i.let-us.cyou offline)</p>';
$feed = process_wordpress_feed('https://i.let-us.cyou/category/viewer/feed/json');
if(strpos($feed,'Error')!==false){
    echo '<div style="color:#8A6A7A;">No external feed — showing Fresh welcome. 🌷</div>';
}else echo $feed;

echo '<br><p style="font-size:16px !important;"><strong>Grid Services Status</strong></p>';
$kuma = process_kuma_status('https://status.is-on.click/api/status-page/igrid');
if(strpos($kuma,'Error')!==false) echo '<div style="color:#8A6A7A;">External status unavailable — check <a href="https://os.tasia.work.gd:22000/wifi/" target="_blank">Wifi page</a>.</div>';
else echo $kuma;
?>
</center>
</body>
</html>