<?php
error_reporting(0);

$dir = __DIR__ . '/retro-games/';

function getFiles() {
    global $dir;
    if (!is_dir($dir)) return [];
    $files = array_diff(scandir($dir), array('.', '..', 'files.zip'));
    return $files;
}

$search_query = isset($_GET['search']) ? trim($_GET['search']) : '';
$category = isset($_GET['category']) ? $_GET['category'] : '';
$page = isset($_GET['page']) ? (int)$_GET['page'] : 1;
$limit = 12;
$offset = ($page - 1) * $limit;

$allFiles = getFiles();

if ($category) {
    $allFiles = array_filter($allFiles, function($file) use ($category) {
        return strpos($file, $category . '-') === 0;
    });
}

if ($search_query) {
    $allFiles = array_filter($allFiles, function($file) use ($search_query) {
        $filename = str_replace(['_', '-', '.html', '.'], ' ', pathinfo($file, PATHINFO_FILENAME));
        return stripos($filename, $search_query) !== false;
    });
}

$total_files = count($allFiles);
$files = array_slice($allFiles, $offset, $limit);
$total_pages = ceil($total_files / $limit);

function pagination($page, $total_pages, $search_query, $category) {
    $html = '<div class="pagination">';
    if ($page > 1) {
        $html .= '<a href="?page=1' . ($search_query ? '&search=' . urlencode($search_query) : '') . ($category ? '&category=' . $category : '') . '" class="page-btn">&laquo;</a>';
    }
    for ($i = max(1, $page - 2); $i <= min($page + 2, $total_pages); $i++) {
        $html .= '<a href="?page=' . $i . ($search_query ? '&search=' . urlencode($search_query) : '') . ($category ? '&category=' . $category : '') . '" class="page-btn ' . ($i == $page ? 'active' : '') . '">' . $i . '</a>';
    }
    if ($page < $total_pages) {
        $html .= '<a href="?page=' . ($page + 1) . ($search_query ? '&search=' . urlencode($search_query) : '') . ($category ? '&category=' . $category : '') . '" class="page-btn">&raquo;</a>';
    }
    $html .= '</div>';
    return $html;
}

if (isset($_GET['file'])) {
    $filename = basename($_GET['file']);
    $filePath = $dir . $filename;
    
    if (file_exists($filePath) && is_file($filePath)) {
        $content = file_get_contents($filePath);
        
        preg_match_all('/src=["\']([^"\']+)["\']/', $content, $matches);
        
        if (!empty($matches[1])) {
            $gameUrl = $matches[1][1];
            if (strpos($gameUrl, '//') === 0) {
                $gameUrl = 'https:' . $gameUrl;
            }
            
            echo '<style>
                body { background: #0a0a1a; margin: 0; padding: 0; }
                .game-container { width: 100%; height: calc(100vh - 60px); border: none; }
                .back-btn { background: #003333; color: #00ffff; padding: 10px 20px; text-decoration: none; display: inline-block; border: 1px solid #00ffff; }
            </style>';
            echo '<a href="?" class="back-btn">&laquo; Back</a>';
            echo '<iframe class="game-container" src="' . htmlspecialchars($gameUrl) . '"></iframe>';
            exit;
        }
    }
}
?>
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>RETRO ARCADE</title>
    <style>
        @import url('https://fonts.googleapis.com/css2?family=Orbitron:wght@400;700;900&display=swap');
        
        * { box-sizing: border-box; margin: 0; padding: 0; }
        
        body {
            background: #000;
            background-image: 
                linear-gradient(180deg, #0a0a1a 0%, #000 50%, #0a0a1a 100%),
                repeating-linear-gradient(0deg, transparent, transparent 2px, rgba(0,255,255,0.03) 2px, rgba(0,255,255,0.03) 4px);
            min-height: 100vh;
            font-family: 'Orbitron', monospace;
            color: #00ffff;
            overflow-x: hidden;
        }
        
        .scanlines {
            position: fixed;
            top: 0; left: 0; right: 0; bottom: 0;
            background: repeating-linear-gradient(0deg, rgba(0, 0, 0, 0.15), rgba(0, 0, 0, 0.15) 1px, transparent 1px, transparent 2px);
            pointer-events: none;
            z-index: 1000;
        }
        
        .container { max-width: 1200px; margin: 0 auto; padding: 20px; }
        
        header { text-align: center; padding: 30px 0; }
        
        h1 {
            font-size: 3rem;
            font-weight: 900;
            text-transform: uppercase;
            letter-spacing: 10px;
            color: #00ffff;
            text-shadow: 0 0 10px #00ffff, 0 0 20px #00ffff, 0 0 40px #00ffff, 0 0 80px #009999;
            animation: flicker 3s infinite alternate;
        }
        
        @keyframes flicker {
            0%, 19%, 21%, 23%, 25%, 54%, 56%, 100% { text-shadow: 0 0 10px #00ffff, 0 0 20px #00ffff, 0 0 40px #00ffff, 0 0 80px #009999; }
            20%, 24%, 55% { text-shadow: none; }
        }
        
        .subtitle { font-size: 1rem; color: #009999; letter-spacing: 5px; margin-top: 10px; }
        
        .search-form { text-align: center; margin: 30px 0; }
        
        .search-input {
            width: 400px; max-width: 90%;
            padding: 15px 25px;
            background: rgba(0, 255, 255, 0.05);
            border: 2px solid #00ffff;
            color: #00ffff;
            font-family: 'Orbitron', monospace;
            font-size: 1rem;
            outline: none;
        }
        
        .search-input::placeholder { color: #006666; }
        
        .search-input:focus {
            background: rgba(0, 255, 255, 0.1);
            box-shadow: 0 0 20px rgba(0, 255, 255, 0.3);
        }
        
        .search-button {
            padding: 15px 30px;
            background: linear-gradient(180deg, #00ffff, #006666);
            border: none;
            color: #000;
            font-family: 'Orbitron', monospace;
            font-weight: 700;
            cursor: pointer;
            margin-left: 10px;
            text-transform: uppercase;
            letter-spacing: 2px;
        }
        
        .search-button:hover {
            box-shadow: 0 0 30px rgba(0, 255, 255, 0.5);
        }
        
        .game-grid {
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
            gap: 20px;
            margin: 40px 0;
        }
        
        .game-card {
            background: linear-gradient(135deg, rgba(0, 51, 51, 0.8), rgba(0, 20, 20, 0.9));
            border: 1px solid #00ffff;
            padding: 20px;
            text-align: center;
            transition: all 0.3s;
        }
        
        .game-card:hover {
            transform: translateY(-5px);
            box-shadow: 0 0 20px rgba(0, 255, 255, 0.4);
            border-color: #33ffff;
        }
        
        .game-title {
            font-size: 0.85rem;
            color: #00ffff;
            margin-bottom: 15px;
            word-break: break-word;
            min-height: 50px;
            display: flex;
            align-items: center;
            justify-content: center;
        }
        
        .play-btn {
            display: inline-block;
            padding: 12px 40px;
            background: transparent;
            border: 2px solid #00ffff;
            color: #00ffff;
            font-family: 'Orbitron', monospace;
            font-weight: 700;
            text-decoration: none;
            text-transform: uppercase;
            letter-spacing: 3px;
            transition: all 0.3s;
        }
        
        .play-btn:hover {
            background: #00ffff;
            color: #000;
            box-shadow: 0 0 30px rgba(0, 255, 255, 0.5);
        }
        
        .pagination { display: flex; justify-content: center; gap: 10px; margin: 40px 0; flex-wrap: wrap; }
        
        .page-btn {
            width: 45px; height: 45px;
            display: flex; align-items: center; justify-content: center;
            border: 1px solid #00ffff;
            color: #00ffff;
            text-decoration: none;
            font-family: 'Orbitron', monospace;
            transition: all 0.3s;
        }
        
        .page-btn:hover, .page-btn.active {
            background: #00ffff;
            color: #000;
            box-shadow: 0 0 20px rgba(0, 255, 255, 0.5);
        }
        
        .categories { display: flex; justify-content: center; gap: 8px; margin: 20px 0; flex-wrap: wrap; }
        .cat-btn {
            padding: 8px 16px;
            background: transparent;
            border: 1px solid #006666;
            color: #006666;
            font-family: 'Orbitron', monospace;
            font-size: 0.7rem;
            text-decoration: none;
            text-transform: uppercase;
            transition: all 0.3s;
        }
        .cat-btn:hover, .cat-btn.active {
            background: #006666;
            color: #00ffff;
            border-color: #00ffff;
        }
        .cat-btn.active { background: #00ffff; color: #000; }
        
        footer { text-align: center; padding: 30px 0; margin-top: 40px; border-top: 1px solid #003333; color: #006666; font-size: 0.7rem; letter-spacing: 3px; }
        
        @media (max-width: 600px) {
            h1 { font-size: 1.8rem; letter-spacing: 5px; }
            .search-button { margin: 10px 0 0 0; width: 100%; max-width: 200px; }
        }
    </style>
</head>
<body>
    <div class="scanlines"></div>
    <div class="container">
        <header>
            <h1>Retro Arcade</h1>
            <p class="subtitle">// Select Your Game //</p>
        </header>
        
        <div class="categories">
            <a href="?" class="cat-btn <?php echo !$category ? 'active' : ''; ?>">ALL</a>
            <a href="?category=arcade" class="cat-btn <?php echo $category == 'arcade' ? 'active' : ''; ?>">ARCADE</a>
            <a href="?category=nes" class="cat-btn <?php echo $category == 'nes' ? 'active' : ''; ?>">NES</a>
            <a href="?category=snes" class="cat-btn <?php echo $category == 'snes' ? 'active' : ''; ?>">SNES</a>
            <a href="?category=genesis" class="cat-btn <?php echo $category == 'genesis' ? 'active' : ''; ?>">GENESIS</a>
            <a href="?category=nds" class="cat-btn <?php echo $category == 'nds' ? 'active' : ''; ?>">NDS</a>
            <a href="?category=psx" class="cat-btn <?php echo $category == 'psx' ? 'active' : ''; ?>">PSX</a>
            <a href="?category=gameboy" class="cat-btn <?php echo $category == 'gameboy' ? 'active' : ''; ?>">GAMEBOY</a>
            <a href="?category=n64" class="cat-btn <?php echo $category == 'n64' ? 'active' : ''; ?>">N64</a>
        </div>
        
        <form method="GET" class="search-form">
            <?php if ($category): ?><input type="hidden" name="category" value="<?php echo htmlspecialchars($category); ?>"><?php endif; ?>
            <input type="text" name="search" value="<?php echo htmlspecialchars($search_query); ?>" class="search-input" placeholder="SEARCH GAMES...">
            <button type="submit" class="search-button">Find</button>
        </form>
        
        <div class="game-grid">
            <?php if (empty($files)): ?>
                <div class="game-card" style="grid-column: 1/-1;">
                    <p class="game-title">NO GAMES FOUND</p>
                </div>
            <?php else: ?>
                <?php foreach ($files as $file): ?>
                    <div class="game-card">
                        <p class="game-title"><?php echo htmlspecialchars(str_replace(['_', '-', '.html', '.'], ' ', pathinfo($file, PATHINFO_FILENAME))); ?></p>
                        <a href="?page=<?php echo $page; ?>&file=<?php echo urlencode($file); ?>&search=<?php echo urlencode($search_query); ?>" class="play-btn">Play</a>
                    </div>
                <?php endforeach; ?>
            <?php endif; ?>
        </div>
        
        <?php echo pagination($page, $total_pages, $search_query, $category); ?>
        <p class="stats">// <?php echo $total_files; ?> GAMES AVAILABLE //</p>
        
        <?php echo pagination($page, $total_pages, $search_query, $category); ?>
    </div>
    <footer><p>// SYSTEM ONLINE //</p></footer>
</body>
</html>
