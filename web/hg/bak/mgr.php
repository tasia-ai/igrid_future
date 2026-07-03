<?php
session_start();

// 🔐 Admin password (CHANGE THIS)
$adminPassword = "SuperSecureAdminPass"; 

if ($_POST['password'] ?? '' === $adminPassword) {
    $_SESSION['admin'] = true;
}

if (!($_SESSION['admin'] ?? false)) {
    echo '<form method="POST">
            <label>Admin Password:</label>
            <input type="password" name="password" required>
            <button type="submit">Login</button>
          </form>';
    exit();
}

$host = "i.let-us.cyou";
$user = "root";  
$pass = "CHANGE_ME_DB_PASSWORD";  
$dbname = "opensim_auth";

$conn = new mysqli($host, $user, $pass, $dbname);
if ($conn->connect_error) {
    die("Database connection failed.");
}

// 🔄 Remove user
if (isset($_POST['remove_uuid'])) {
    $uuid = $_POST['remove_uuid'];
    $stmt = $conn->prepare("DELETE FROM authorized_users WHERE uuid = ?");
    $stmt->bind_param("s", $uuid);
    $stmt->execute();
}

// 🚫 Ban user
if (isset($_POST['ban_uuid'])) {
    $uuid = $_POST['ban_uuid'];
    $stmt = $conn->prepare("UPDATE authorized_users SET banned = 1 WHERE uuid = ?");
    $stmt->bind_param("s", $uuid);
    $stmt->execute();
}

// ✅ Unban user
if (isset($_POST['unban_uuid'])) {
    $uuid = $_POST['unban_uuid'];
    $stmt = $conn->prepare("UPDATE authorized_users SET banned = 0 WHERE uuid = ?");
    $stmt->bind_param("s", $uuid);
    $stmt->execute();
}

// ➕ Add new user
if (isset($_POST['add_uuid'])) {
    $uuid = trim($_POST['add_uuid']);
    if (!empty($uuid)) {
        $stmt = $conn->prepare("INSERT INTO authorized_users (uuid, banned) VALUES (?, 0)");
        $stmt->bind_param("s", $uuid);
        $stmt->execute();
    }
}

// 📋 Fetch all users
$result = $conn->query("SELECT uuid, banned FROM authorized_users");

echo "<h2>Admin Panel</h2>";

// ➕ Add user form
echo "<form method='POST'>
        <label>Add New User (UUID):</label>
        <input type='text' name='add_uuid' required>
        <button type='submit'>Add User</button>
      </form><br>";

echo "<table border='1'>
<tr><th>UUID</th><th>Status</th><th>Actions</th></tr>";

while ($row = $result->fetch_assoc()) {
    echo "<tr>
            <td>{$row['uuid']}</td>
            <td>" . ($row['banned'] ? "BANNED" : "ACTIVE") . "</td>
            <td>
                <form method='POST' style='display:inline'>
                    <input type='hidden' name='remove_uuid' value='{$row['uuid']}'>
                    <button type='submit'>Remove</button>
                </form>
                <form method='POST' style='display:inline'>
                    <input type='hidden' name='" . ($row['banned'] ? "unban_uuid" : "ban_uuid") . "' value='{$row['uuid']}'>
                    <button type='submit'>" . ($row['banned'] ? "Unban" : "Ban") . "</button>
                </form>
            </td>
          </tr>";
}
echo "</table>";

$conn->close();
?>