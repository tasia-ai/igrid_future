<?php
// sync_users.php - Sync users from UserAccounts to wp_oslogin_auth

$host = "i.let-us.cyou";
$user = "root";
$pass = "CHANGE_ME_DB_PASSWORD";
$dbname = "wordpress";
$robust_dbname = "robust";

$conn = new mysqli($host, $user, $pass, $dbname);
$robust_conn = new mysqli($host, $user, $pass, $robust_dbname);

if ($conn->connect_error || $robust_conn->connect_error) {
    die("Database connection failed: " . $conn->connect_error . " | " . $robust_conn->connect_error);
}

$create_table_sql = "CREATE TABLE IF NOT EXISTS wp_oslogin_auth (
    id INT AUTO_INCREMENT PRIMARY KEY,
    uuid VARCHAR(36) NOT NULL UNIQUE,
    first_name VARCHAR(64) NOT NULL,
    last_name VARCHAR(64) NOT NULL,
    email VARCHAR(255),
    created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    last_login TIMESTAMP NULL,
    banned TINYINT(1) DEFAULT 0,
    ban_reason TEXT,
    ban_date TIMESTAMP NULL,
    gridname VARCHAR(255) DEFAULT 'local',
    INDEX idx_uuid (uuid),
    INDEX idx_banned (banned),
    INDEX idx_gridname (gridname)
)";

if (!$conn->query($create_table_sql)) {
    die("Error creating table: " . $conn->error);
}

$create_maintenance_table = "CREATE TABLE IF NOT EXISTS opensim_maintenance (
    id INT AUTO_INCREMENT PRIMARY KEY,
    maintenance_mode TINYINT(1) DEFAULT 0,
    maintenance_message TEXT,
    allowed_uuids TEXT,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
)";

if (!$conn->query($create_maintenance_table)) {
    die("Error creating maintenance table: " . $conn->error);
}

$check_maintenance = $conn->query("SELECT COUNT(*) as count FROM opensim_maintenance");
$maintenance_count = $check_maintenance->fetch_assoc()['count'];

if ($maintenance_count == 0) {
    $conn->query("INSERT INTO opensim_maintenance (maintenance_mode, maintenance_message, allowed_uuids) VALUES (0, 'System is under maintenance. Please try again later.', '')");
}

echo "Starting user sync...\n";

$stmt = $robust_conn->prepare("SELECT PrincipalID, FirstName, LastName, Email, Created FROM UserAccounts");
$stmt->execute();
$result = $stmt->get_result();

$synced_count = 0;
$updated_count = 0;

while ($row = $result->fetch_assoc()) {
    $uuid = $row['PrincipalID'];
    $firstName = $row['FirstName'];
    $lastName = $row['LastName'];
    $email = $row['Email'] ?? '';
    $created_date = date('Y-m-d H:i:s', $row['Created']);

    $check_stmt = $conn->prepare("SELECT uuid FROM wp_oslogin_auth WHERE uuid = ?");
    $check_stmt->bind_param("s", $uuid);
    $check_stmt->execute();
    $existing = $check_stmt->get_result()->fetch_assoc();
    $check_stmt->close();

    if (!$existing) {
        $insert_stmt = $conn->prepare("INSERT INTO wp_oslogin_auth (uuid, first_name, last_name, email, created_date, gridname) VALUES (?, ?, ?, ?, ?, 'local')");
        $insert_stmt->bind_param("sssss", $uuid, $firstName, $lastName, $email, $created_date);
        if ($insert_stmt->execute()) {
            $synced_count++;
        }
        $insert_stmt->close();
    } else {
        $update_stmt = $conn->prepare("UPDATE wp_oslogin_auth SET first_name = ?, last_name = ?, email = ? WHERE uuid = ?");
        $update_stmt->bind_param("ssss", $firstName, $lastName, $email, $uuid);
        if ($update_stmt->execute()) {
            $updated_count++;
        }
        $update_stmt->close();
    }
}

$stmt->close();
echo "\nSync completed!\n";
echo "New users synced: $synced_count\n";
echo "Users updated: $updated_count\n";

if (isset($argv[1]) && $argv[1] === '--cleanup') {
    $cleanup_stmt = $conn->prepare("DELETE FROM wp_oslogin_auth WHERE uuid NOT IN (SELECT PrincipalID FROM robust.UserAccounts) AND gridname = 'local'");
    $cleanup_stmt->execute();
    $removed_count = $cleanup_stmt->affected_rows;
    $cleanup_stmt->close();
    echo "Removed users: $removed_count\n";
}

$conn->close();
$robust_conn->close();
echo "\nUser sync completed successfully!\n";
?>
