<?php
// Fresh Metaverse — viewer/web search endpoint for os.tasia.work.gd/search.
// Supports GET ?search= ?q= ?query= ?key= ?s= and POST JSON/form/XML-ish payloads.
$searchTerm = null;
if (isset($_GET['search']) || isset($_GET['q']) || isset($_GET['query']) || isset($_GET['key']) || isset($_GET['s'])) {
    $searchTerm = trim($_GET['search'] ?? $_GET['q'] ?? $_GET['query'] ?? $_GET['key'] ?? $_GET['s'] ?? '');
} elseif ($_SERVER['REQUEST_METHOD']==='POST') {
    $raw = file_get_contents('php://input');
    if($raw){
        $j=json_decode($raw,true);
        if($j && isset($j['search'])) $searchTerm=trim($j['search']);
        elseif($j && isset($j['q'])) $searchTerm=trim($j['q']);
        elseif(strpos($raw,'<string>')!==false && preg_match('/<string>([^<]{2,})<\/string>/',$raw,$m)) $searchTerm=trim($m[1]);
        else { parse_str($raw,$p); if(isset($p['search'])) $searchTerm=trim($p['search']); if(isset($p['q'])) $searchTerm=trim($p['q']); }
        if(!$searchTerm && isset($_POST['search'])) $searchTerm=trim($_POST['search']);
    } else if(isset($_POST['search'])) $searchTerm=trim($_POST['search']);
}
if ($searchTerm !== null) {
    $term = $searchTerm;
    // Only handle non-empty viewer search; ignore empty tab loads
    if ($term !== '' && strlen($term) >= 2) {
        header('Access-Control-Allow-Origin: *');
        header('Content-Type: application/json; charset=utf-8');
        $db_host=getenv('FRESH_DB_HOST') ?: '127.0.0.1'; $db_port=getenv('FRESH_DB_PORT') ?: '5432'; $db_name=getenv('FRESH_DB_NAME') ?: 'robust'; $db_user=getenv('FRESH_DB_USER') ?: 'opensim'; $db_pass=getenv('FRESH_DB_PASS') ?: 'CHANGE_ME';
        $dsn="pgsql:host={$db_host};port={$db_port};dbname={$db_name};";
        $results=[];
        try {
            $pdo=new PDO($dsn,$db_user,$db_pass,[PDO::ATTR_ERRMODE=>PDO::ERRMODE_EXCEPTION,PDO::ATTR_TIMEOUT=>2]);
            $stmt=$pdo->prepare('SELECT "regionName", "locX", "locY", "sizeX", "sizeY", "serverURI", "quicHost", "quicPort" FROM regions WHERE "regionName" ILIKE :q ORDER BY "regionName" LIMIT 50');
            $stmt->execute([':q'=>'%'.$term.'%']);
            while($r=$stmt->fetch(PDO::FETCH_ASSOC)){
                $name=$r['regionName'] ?? $r['regionname'] ?? 'Unknown';
                $results[]=[
                    'type'=>'region',
                    'name'=>$name,
                    'x'=> (int)($r['locX'] ?? $r['locx'] ?? 0),
                    'y'=> (int)($r['locY'] ?? $r['locy'] ?? 0),
                    'sizeX'=> (int)($r['sizeX'] ?? $r['sizex'] ?? 256),
                    'sizeY'=> (int)($r['sizeY'] ?? $r['sizey'] ?? 256),
                    'server_uri'=>$r['serverURI'] ?? '',
                    'quic_host'=>$r['quicHost'] ?? 'ok.tasia.work.gd',
                    'quic_port'=>(int)($r['quicPort'] ?? 0),
                    'login_uri'=>'os.tasia.work.gd:22000',
                    'teleport'=>'secondlife://os.tasia.work.gd:22000/'.rawurlencode($name).'/128/128/25'
                ];
            }
        } catch(Exception $e){ http_response_code(503); $results=[['type'=>'error','message'=>$e->getMessage()]]; }
        echo json_encode(['grid'=>'Fresh Metaverse','login_uri'=>'os.tasia.work.gd:22000','query'=>$term,'results'=>$results,'count'=>count($results)], JSON_PRETTY_PRINT|JSON_UNESCAPED_SLASHES);
        exit;
    }
    // if term empty, fall through to normal page
}
?>
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <title>Fresh Metaverse — Search ♡ — Amber's Pastel Grid</title>
    <meta name="description" content="Fresh Metaverse Search — os.tasia.work.gd:22000 — Amber's kawaii OpenSim grid. Search regions, parcels, events.">
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link href="https://fonts.googleapis.com/css2?family=Nunito:wght@400;700;800&family=Pacifico&family=Poppins:wght@600;700&display=swap" rel="stylesheet">
    <link rel="stylesheet" href="amber-overrides.css">
    <script src="https://cdn.tailwindcss.com"></script>
    <style>
        * { box-sizing: border-box; }
        body { 
            margin: 0; 
            padding: 0; 
            min-height: 100vh; 
            background: url('https://via.placeholder.com/1920x1080/000000/000000?text=Desktop+Background') no-repeat center center fixed; 
            background-size: cover;
            font-family: 'Tahoma', sans-serif;
        }
        .desktop {
            position: absolute;
            top: 0;
            left: 0;
            width: 100%;
            height: calc(100vh - 44px);
            padding: 20px;
            display: flex;
            flex-wrap: wrap;
            flex-direction: column;
            gap: 20px;
            z-index: 1;
        }
        .desktop-icon {
            text-align: center;
            width: 80px;
            cursor: pointer;
        }
        .desktop-icon img {
            width: 48px;
            height: 48px;
        }
        .desktop-icon p {
            color: #00ffff;
            text-shadow: 0 0 5px #00ffff;
            font-size: 12px;
            margin: 5px 0 0;
        }
        .start-menu {
            display: none;
            position: fixed;
            bottom: 44px;
            left: 0;
            width: 400px;
            background: #0a0a1a;
            border: 2px solid #00ffff;
            box-shadow: 0 4px 8px rgba(0, 255, 255, 0.3), 0 0 20px rgba(0, 255, 255, 0.2);
            z-index: 1001;
        }
        .start-menu.active {
            display: flex;
            flex-direction: column;
        }
        .start-menu-header {
            background: linear-gradient(to bottom, #00ffff, #0099cc);
            padding: 5px 10px;
            border-bottom: 2px solid #33ffff;
            display: flex;
            align-items: center;
        }
        .start-menu-header img {
            width: 48px;
            height: 48px;
            margin-right: 10px;
        }
        .start-menu-header span {
            color: #000;
            font-weight: bold;
            font-size: 14px;
        }
        .start-menu-content {
            display: flex;
            flex: 1;
        }
        .start-menu-left {
            width: 60%;
            background: #0a0a1a;
            padding: 10px;
        }
        .start-menu-right {
            width: 40%;
            background: #111133;
            padding: 10px;
            border-left: 1px solid #00ffff;
        }
        .start-menu-category h3 {
            color: #00ffff;
            font-size: 14px;
            margin: 10px 0 5px;
            font-weight: bold;
            text-shadow: 0 0 5px #00ffff;
        }
        .tile {
            display: flex;
            align-items: center;
            width: 100%;
            padding: 5px 10px;
            background: #0a0a1a;
            color: #00ffff;
            border: 1px solid #00ffff;
            font-size: 13px;
            border-radius: 3px;
            cursor: pointer;
            transition: all 0.2s;
            text-align: left;
        }
        .tile:hover {
            background-color: #00ffff;
            color: #000;
        }
        .tile img {
            width: 24px;
            height: 24px;
            margin-right: 8px;
            display: inline-block;
            vertical-align: middle;
        }
        .taskbar {
            position: fixed;
            bottom: 0;
            width: 100%;
            height: 44px;
            background: rgba(0, 10, 30, 0.95);
            backdrop-filter: blur(10px);
            border-top: 1px solid #00ffff;
            box-shadow: 0 0 10px rgba(0, 255, 255, 0.3);
            z-index: 1000;
        }
        .tab-content {
            display: none;
            padding: 20px;
            background: #050510;
            min-height: calc(100vh - 44px);
            position: relative;
            z-index: 999;
        }
        .tab-content.active {
            display: block;
        }
        .content-container {
            max-width: 95%;
            margin: auto;
            padding: 20px;
            background: #0a0a1a;
            border: 1px solid #00ffff;
            border-radius: 8px;
            min-height: calc(100vh - 84px);
            box-shadow: 0 0 15px rgba(0, 255, 255, 0.2);
        }
        .loading {
            text-align: center;
            color: #00ffff;
            font-style: italic;
            text-shadow: 0 0 5px #00ffff;
        }
    </style>
    <!-- amber-overrides.css last to win — do not remove -->
</head>
<body class="bg-black text-cyan-200">
    <!-- Desktop Icons -->
    <div class="desktop">
     <?php include('desktop-source.php'); ?>
    </div>

    <!-- Taskbar -->
    <div class="taskbar flex items-center justify-between px-4 py-2">
        <button id="startButton" class="text-white bg-gradient-to-b from-[#00ffff] to-[#0099cc] hover:from-[#33ffff] hover:to-[#00ccdd] px-4 py-1 rounded-sm transition-colors font-bold text-sm">
            <img src="https://cdn-icons-png.flaticon.com/512/2516/2516823.png" style="width:16px;height:16px;vertical-align:middle;margin-right:4px;">Start
        </button>
        <div id="clock" class="text-white text-sm"></div>
        <button onclick="openTab(event, 'tawk')" class="bg-cyan-600 hover:bg-cyan-500 text-white px-4 py-1 rounded-sm transition-colors text-sm">
            Live Chat
        </button>
    </div>

    <!-- Start Menu -->
    <div id="startMenu" class="start-menu">
        <div class="start-menu-header">
            <img src="https://cdn-icons-png.flaticon.com/512/4140/4140048.png" alt="User Icon" style="width:32px;height:32px;">
            <span>🌸 Amber — Fresh Metaverse</span>
        </div>
        <div class="start-menu-content">
            <div class="start-menu-left">
                <div class="start-menu-category">
                    <h3>Explore</h3>
                    <button class="tile" onclick="openTab(event, 'web')"><img src="https://cdn-icons-png.flaticon.com/512/109/109617.png" alt="Web" style="width:20px;height:20px;">Internet Search</button>
                    <button class="tile" onclick="openTab(event, 'search')"><img src="https://cdn-icons-png.flaticon.com/512/7518/7518631.png" alt="Search" style="width:20px;height:20px;">Fresh Search ✨</button>
                    <button class="tile" onclick="openTab(event, 'opensimworld')"><img src="https://cdn-icons-png.flaticon.com/512/5449/5449262.png" alt="OSW" style="width:20px;height:20px;">OpenSimWorld</button>
                </div>
                <div class="start-menu-category">
                    <h3>Status & Community</h3>
                    <button class="tile" onclick="openTab(event, 'status')"><img src="https://cdn-icons-png.flaticon.com/512/2921/2921226.png" alt="Status" style="width:20px;height:20px;">News & Status</button>
                    <button class="tile" onclick="openTab(event, 'tos')"><img src="https://cdn-icons-png.flaticon.com/512/2693/2693507.png" alt="TOS" style="width:20px;height:20px;">Codex</button>
                    <button class="tile" onclick="openTab(event, 'fdb')"><img src="https://cdn-icons-png.flaticon.com/512/3616/3616003.png" alt="FDB" style="width:20px;height:20px;">Feedback</button>
                    <button class="tile" onclick="openTab(event, 'support')"><img src="https://cdn-icons-png.flaticon.com/512/2529/2529523.png" alt="Support" style="width:20px;height:20px;">Support</button>
                    <button class="tile" onclick="openTab(event, 'team')"><img src="https://cdn-icons-png.flaticon.com/512/2206/2206368.png" alt="Team" style="width:20px;height:20px;">Our Team</button>
                </div>
            </div>
            <div class="start-menu-right">
                    <div class="start-menu-category">
                    <h3>Tools & Fun</h3>
                    <button class="tile" onclick="openTab(event, 'guide')"><img src="https://cdn-icons-png.flaticon.com/512/2331/2331966.png" alt="Guide" style="width:20px;height:20px;">Destinations Guide</button>
                    <button class="tile" onclick="openTab(event, 'marketplace')"><img src="https://cdn-icons-png.flaticon.com/512/4413/4413494.png" alt="Market" style="width:20px;height:20px;">Marketplace</button>
                    <button class="tile" onclick="openTab(event, 'doritos')"><img src="https://cdn-icons-png.flaticon.com/512/720/720258.png" alt="Doritos" style="width:20px;height:20px;">Buy Doritos</button>
                    <button class="tile" onclick="openTab(event, 'fdc')"><img src="https://cdn-icons-png.flaticon.com/512/1082/1082139.png" alt="FDC" style="width:20px;height:20px;">AI Img</button>
                    <button class="tile" onclick="openTab(event, 'fdd')"><img src="https://cdn-icons-png.flaticon.com/512/2977/2977648.png" alt="FDD" style="width:20px;height:20px;">AI Mesh</button>
                    <button class="tile" onclick="openTab(event, 'fde')"><img src="https://cdn-icons-png.flaticon.com/512/2913/2913526.png" alt="FDE" style="width:20px;height:20px;">LSL Tools</button>
                    <button class="tile" onclick="openTab(event, 'fdf')"><img src="https://cdn-icons-png.flaticon.com/512/2913/2913526.png" alt="FDF" style="width:20px;height:20px;">Opensim Tools</button>
                    <button class="tile" onclick="openTab(event, 'retro')"><img src="https://cdn-icons-png.flaticon.com/512/2712/2712066.png" alt="Retro" style="width:20px;height:20px;">Retro Games</button>
                </div>
            </div>
        </div>
    </div>

    <!-- Tab Contents -->
    <div id="home" class="tab-content">
    <?php include('desktop-source.php'); ?>
    </div>
    <div id="search" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="web" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="tasia" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="support" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="team" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="tos" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="tawk" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="doritos" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="status" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="opensimworld" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="market" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="marketplace" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="guide" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="fdb" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="fdc" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="fdd" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="fde" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="fdf" class="tab-content">
        <div class="content-container"></div>
    </div>
    <div id="retro" class="tab-content">
        <div class="content-container"></div>
    </div>

    <script>
        function openTab(evt, tabId) {
            // Hide start menu and all tab content
            document.getElementById('startMenu').classList.remove('active');
            document.querySelectorAll('.tab-content').forEach(el => el.style.display = 'none');
            
            // Show selected tab
            const tab = document.getElementById(tabId);
            tab.style.display = 'block';
            tab.classList.add('active');

            // Load content if not already loaded (skip for home to preserve static content)
            const contentContainer = tab.querySelector('.content-container');
            if (tabId !== 'home' && contentContainer && !contentContainer.dataset.loaded) {
                contentContainer.innerHTML = '<p class="loading">Loading content...</p>';
                fetch('fetch_content.php?tab=' + encodeURIComponent(tabId))
                    .then(response => {
                        if (!response.ok) throw new Error('Failed to load content');
                        return response.text();
                    })
                    .then(data => {
                        contentContainer.innerHTML = data;
                        contentContainer.dataset.loaded = 'true';
                        const scripts = contentContainer.getElementsByTagName('script');
                        for (let script of scripts) {
                            const newScript = document.createElement('script');
                            newScript.text = script.text;
                            document.body.appendChild(newScript);
                        }
                    })
                    .catch(error => {
                        contentContainer.innerHTML = '<p>Content is currently unavailable.</p>';
                    });
            }
        }

        function showDesktop() {
            // Hide start menu and all tab content to show desktop
            document.getElementById('startMenu').classList.remove('active');
            document.querySelectorAll('.tab-content').forEach(el => el.style.display = 'none');
        }

        function escapeHTML(str) {
            return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
        }

        // Toggle Start Menu
        document.getElementById('startButton').addEventListener('click', () => {
            const startMenu = document.getElementById('startMenu');
            if (startMenu.classList.contains('active')) {
                showDesktop();
            } else {
                startMenu.classList.add('active');
                document.querySelectorAll('.tab-content').forEach(el => el.style.display = 'none');
            }
        });

        // Update Clock
        function updateClock() {
            const now = new Date();
            const timeString = now.toLocaleTimeString('en-US', { hour12: true });
            document.getElementById('clock').textContent = timeString;
        }
        setInterval(updateClock, 1000);
        updateClock();

        // Show desktop by default
        showDesktop();
    </script>
</body>
</html>
