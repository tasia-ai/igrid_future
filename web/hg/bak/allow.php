<?php
$host = "i.let-us.cyou"; // Database Host
$user = "root"; // Database Username
$pass = "CHANGE_ME_DB_PASSWORD"; // Database Password
$dbname = "opensim_auth"; // Database Name

$conn = new mysqli($host, $user, $pass, $dbname);

if ($conn->connect_error) {
    die("Database connection failed: " . $conn->connect_error);
}

// Global password to allow access
$globalPassword = "wearefamily";  // Change this!

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $avatarUUID = $_POST['uuid'] ?? '';
    $password = $_POST['password'] ?? '';

    if (!$avatarUUID || !$password) {
        die("❌ UUID and password are required.");
    }

    if ($password !== $globalPassword) {
        die("❌ Incorrect password.");
    }

    // Insert UUID into database (prevent duplicates)
    $stmt = $conn->prepare("INSERT IGNORE INTO authorized_users (uuid) VALUES (?)");
    $stmt->bind_param("s", $avatarUUID);
    $stmt->execute();

    echo "✅ Access granted! You can now teleport.";
} else {
    echo '<body style="background-color:black;color:white;"><form method="POST">
            Avatar UUID: <input type="text" name="uuid" required><br>
            Password: <input type="password" name="password" required><br>
            <button type="submit">Allow Access</button>
          </form></body>';
}
?>