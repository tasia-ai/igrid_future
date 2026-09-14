<?php
// Configuration
const BASEURL = "http://opensimworld.com/index.php/osgate";
const DATA_FILE = "regions.txt";
$curStart = isset($_GET['start']) ? (int)$_GET['start'] : 0;
$itemsPerPage = 9;
$listData = "";
$category = isset($_GET['category']) ? $_GET['category'] : 'Popular';
$categories = ['Popular', 'Latest', 'Random'];
$errorMessage = "";

// Fetch or read region list
if (in_array($category, $categories) && !isset($_GET['regionIndex'])) {
    $url = BASEURL . "/gate1/?q=" . urlencode($category);
    $listData = @file_get_contents($url);
    if ($listData === false) {
        $listData = @file_get_contents(DATA_FILE) ?: "";
        $errorMessage = "Error fetching region list from API. Using cached data.";
    } else {
        // Write to file with locking
        $file = fopen(DATA_FILE, 'w');
        if (flock($file, LOCK_EX)) {
            fwrite($file, $listData);
            flock($file, LOCK_UN);
        }
        fclose($file);
    }
} else {
    // Read from file
    $file = fopen(DATA_FILE, 'r');
    if ($file && flock($file, LOCK_SH)) {
        $listData = stream_get_contents($file);
        flock($file, LOCK_UN);
        fclose($file);
    } else {
        $listData = "";
        $errorMessage = "Error reading cached region data.";
    }
}

// Parse region list
function getListItems($listData, $start, $itemsPerPage) {
    $tok = explode("\n", trim($listData));
    $title = $tok[0] ?? "Regions";
    $items = [];
    $destAddr = [];

    for ($i = $start + 1; $i < count($tok) && $i <= $start + $itemsPerPage; $i++) {
        if (!empty($tok[$i])) {
            $e = explode("#", $tok[$i]);
            if (count($e) < 5) continue; // Skip malformed entries
            $serverRegion = $e[3] ?? "";
            $serverParts = explode(":", $serverRegion, 3);
            $server = count($serverParts) >= 2 ? $serverParts[0]: "unknown";
            $port = $serverParts[1];
            $regionName = $serverParts[2] ?? $e[2] ?? "Unknown";
            $regionName2 = rawurlencode($regionName);
            $coords = isset($e[4]) ? trim($e[4], "<>") : "0,0,0";
            $coordsParts = explode(",", $coords);
            $coordsFormatted = implode("/", array_map('trim', $coordsParts));
            $items[] = [
                'index' => $i,
                'name' => $regionName,
                'users' => $e[1] ?? "0",
                'server' => $server . "Port:" . $port,
                'coords' => $coords,
                'teleport_url' => "x-grid-info://$server:$port/$regionName2/$coordsFormatted"
            ];
            $destAddr[] = $e;
        }
    }

    return ['title' => $title, 'items' => $items, 'destAddr' => $destAddr, 'total' => count($tok) - 1];
}

// Get region details for sub-page
function getRegionDetails($listData, $index) {
    $tok = explode("\n", trim($listData));
    if ($index >= 1 && $index < count($tok) && !empty($tok[$index])) {
        $e = explode("#", $tok[$index]);
        if (count($e) < 5) return null; // Malformed entry
        $serverRegion = $e[3] ?? "";
        $serverParts = explode(":", $serverRegion, 3);
        $server = count($serverParts) >= 2 ? $serverParts[0] : "unknown";
        $port = $serverParts[1];
        $regionName = $serverParts[2] ?? $e[2] ?? "Unknown";
        $regionName2 = rawurlencode($regionName);
        $coords = isset($e[4]) ? trim($e[4], "<>") : "0,0,0";
        $coordsParts = explode(",", $coords);
        $coordsFormatted = implode("/", array_map('trim', $coordsParts));
        return [
            'name' => $regionName,
            'server' => $server,
            'users' => $e[1] ?? "0",
            'coords' => $coords,
            'teleport_url' => "x-grid-info://$server:$port/$regionName2/$coordsFormatted"
        ];
    }
    return null;
}
?>

<!DOCTYPE html>
<html lang="en" data-bs-theme="dark">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Region Teleport</title>
    <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/css/bootstrap.min.css" rel="stylesheet">
    <style>
        body {
            background-color: #121212;
            color: #ffffff;
        }
        .card {
            background-color: #1e1e1e;
            border: 1px solid #333;
        }
        .list-group-item {
            background-color: #2c2c2c;
            border: 1px solid #444;
        }
        .btn-primary, .btn-success, .btn-info {
            transition: transform 0.2s;
        }
        .btn-primary:hover, .btn-success:hover, .btn-info:hover {
            transform: scale(1.05);
        }
        .container {
            max-width: 1200px; /* Increased to accommodate side columns */
        }
        .alert-success {
            background-color: #2e7d32;
            border-color: #1b5e20;
        }
        .alert-danger {
            background-color: #d32f2f;
            border-color: #b71c1c;
        }
        .form-wrapper {
            display: flex;
            flex-direction: column;
            gap: 10px;
            max-width: 200px; /* Limit width of form */
        }
        .form-select, .btn-primary {
            width: 100%; /* Full width within form-wrapper */
        }
        .pagination-buttons {
            display: flex;
            flex-direction: column;
            gap: 10px;
            max-width: 200px; /* Limit width of pagination */
            align-items: flex-end; /* Align buttons to the right */
        }
        .pagination-buttons .btn {
            width: 100%; /* Full width within pagination wrapper */
        }
        .table-column {
            flex-grow: 1; /* Allow table to take remaining space */
        }
    </style>
</head>
<body>
    <div class="container mt-5">
        <?php if ($errorMessage): ?>
            <div class="alert alert-danger"><?php echo htmlspecialchars($errorMessage); ?></div>
        <?php endif; ?>
        <?php if (isset($_GET['regionIndex']) && is_numeric($_GET['regionIndex'])): ?>
            <?php $region = getRegionDetails($listData, (int)$_GET['regionIndex']); ?>
            <?php if ($region): ?>
                <div class="card mb-4">
                    <div class="card-header">
                        <h2 class="h4 mb-0"><?php echo htmlspecialchars($region['name']); ?> Details</h2>
                    </div>
                    <div class="card-body">
                        <p><strong>Region Name:</strong> <?php echo htmlspecialchars($region['name']); ?></p>
                        <p><strong>Server:</strong> <?php echo htmlspecialchars($region['server']); ?></p>
                        <p><strong>Users:</strong> <?php echo htmlspecialchars($region['users']); ?></p>
                        <p><strong>Coordinates:</strong> <?php echo htmlspecialchars($region['coords']); ?></p>
                        <p>Link: <?php echo htmlspecialchars($region['teleport_url']); ?></p>
                        <a href="<?php echo htmlspecialchars($region['teleport_url']); ?>" class="btn btn-success me-2">Teleport</a>
                        <a href="?category=<?php echo urlencode($category); ?>&start=<?php echo $curStart; ?>" class="btn btn-secondary">Back to List</a>
                    </div>
                </div>
            <?php else: ?>
                <?php $errorMessage = "Invalid region selected."; ?>
                <div class="alert alert-danger"><?php echo htmlspecialchars($errorMessage); ?></div>
                <a href="?category=<?php echo urlencode($category); ?>&start=<?php echo $curStart; ?>" class="btn btn-secondary">Back to List</a>
            <?php endif; ?>
        <?php else: ?>
            <div class="row mb-4 align-items-start">
                <!-- Left Column: Form -->
                <div class="col-md-3 col-lg-2 form-wrapper">
                    <form method="get">
                        <select name="category" class="form-select mb-2" onchange="this.form.submit()">
                            <?php foreach ($categories as $cat): ?>
                                <option value="<?php echo $cat; ?>" <?php echo $category === $cat ? 'selected' : ''; ?>>
                                    <?php echo $cat; ?>
                                </option>
                            <?php endforeach; ?>
                        </select>
                        <button type="submit" class="btn btn-primary">Refresh</button>
                    </form>
                </div>
                <!-- Center Column: Table -->
                <div class="col-md-6 col-lg-8 table-column">
                    <?php if ($listData): ?>
                        <?php $data = getListItems($listData, $curStart, $itemsPerPage); ?>
                        <div class="card">
                            <div class="card-header">
                                <h2 class="h4 mb-0"><?php echo htmlspecialchars($data['title']); ?></h2>
                            </div>
                            <ul class="list-group list-group-flush">
                                <?php foreach ($data['items'] as $item): ?>
                                    <li class="list-group-item d-flex justify-content-between align-items-center">
                                        <span>[<?php echo $item['index']; ?>] <?php echo htmlspecialchars($item['name']); ?> (<?php echo $item['users']; ?> users, <?php echo htmlspecialchars($item['server']); ?>)</span>
                                        <a href="?regionIndex=<?php echo $item['index']; ?>&category=<?php echo urlencode($category); ?>&start=<?php echo $curStart; ?>" class="btn btn-sm btn-info">View Details</a>
                                    </li>
                                <?php endforeach; ?>
                            </ul>
                        </div>
                    <?php else: ?>
                        <p class="text-muted">Loading regions...</p>
                    <?php endif; ?>
                </div>
                <!-- Right Column: Pagination -->
                <div class="col-md-3 col-lg-2 pagination-buttons">
                    <a href="?start=<?php echo max(0, $curStart - $itemsPerPage); ?>&category=<?php echo urlencode($category); ?>" 
                       class="btn btn-secondary <?php echo $curStart <= 0 ? 'disabled' : ''; ?>">Previous</a>
                    <a href="?start=<?php echo $curStart + $itemsPerPage; ?>&category=<?php echo urlencode($category); ?>" 
                       class="btn btn-secondary <?php echo $curStart + $itemsPerPage >= $data['total'] ? 'disabled' : ''; ?>">Next</a>
                </div>
            </div>
        <?php endif; ?>
    </div>
    <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/js/bootstrap.bundle.min.js"></script>
</body>
</html>