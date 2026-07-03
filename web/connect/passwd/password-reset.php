<?php
// Simple error handling
function sendXmlError($message) {
    header('Content-Type: application/xml');
    echo "<response><e>" . htmlspecialchars($message) . "</e></response>";
    exit;
}

function sendXmlSuccess($message) {
    header('Content-Type: application/xml');
    echo "<response><status>" . htmlspecialchars($message) . "</status></response>";
    exit;
}

// Email function using SMTP
function sendResetEmail($to, $firstName, $resetCode) {
    $subject = "OpenSim Password Reset Code";
    $message = "Hello $firstName,\n\n";
    $message .= "Your OpenSim password reset code is: $resetCode\n\n";
    $message .= "This code will expire in 15 minutes.\n";
    $message .= "If you did not request this password reset, please ignore this email.\n\n";
    $message .= "Best regards,\nOpenSim Support Team";
    
    $headers = array();
    $headers[] = "MIME-Version: 1.0";
    $headers[] = "Content-type: text/plain; charset=UTF-8";
    $headers[] = "From: Tasia Bot <no-reply@easierit.org>";
    $headers[] = "Reply-To: no-reply@easierit.org";
    $headers[] = "X-Mailer: PHP/" . phpversion();
    
    return mail($to, $subject, $message, implode("\r\n", $headers));
}

// Alternative SMTP function (if you want to use SMTP instead of mail())
function sendResetEmailSMTP($to, $firstName, $resetCode) {
    $smtpHost = 'mx.easierit.org';
    $smtpUser = 'aigrid@easierit.org';
    $smtpPass = 'CHANGE_ME_SMTP_PASSWORD';
    $smtpPort = 587;
    
    $subject = "OpenSim Password Reset Code";
    $message = "Hello $firstName,\n\n";
    $message .= "Your OpenSim password reset code is: $resetCode\n\n";
    $message .= "This code will expire in 15 minutes.\n";
    $message .= "If you did not request this password reset, please ignore this email.\n\n";
    $message .= "Best regards,\nOpenSim Support Team";
    
    // Simple socket-based SMTP (basic implementation)
    $socket = fsockopen($smtpHost, $smtpPort, $errno, $errstr, 30);
    if (!$socket) {
        return false;
    }
    
    $response = fgets($socket, 256);
    
    fputs($socket, "HELO " . $_SERVER['HTTP_HOST'] . "\r\n");
    $response = fgets($socket, 256);
    
    fputs($socket, "STARTTLS\r\n");
    $response = fgets($socket, 256);
    
    stream_socket_enable_crypto($socket, true, STREAM_CRYPTO_METHOD_TLS_CLIENT);
    
    fputs($socket, "HELO " . $_SERVER['HTTP_HOST'] . "\r\n");
    $response = fgets($socket, 256);
    
    fputs($socket, "AUTH LOGIN\r\n");
    $response = fgets($socket, 256);
    
    fputs($socket, base64_encode($smtpUser) . "\r\n");
    $response = fgets($socket, 256);
    
    fputs($socket, base64_encode($smtpPass) . "\r\n");
    $response = fgets($socket, 256);
    
    if (substr($response, 0, 3) != "235") {
        fclose($socket);
        return false;
    }
    
    fputs($socket, "MAIL FROM: <no-reply@easierit.org>\r\n");
    $response = fgets($socket, 256);
    
    fputs($socket, "RCPT TO: <$to>\r\n");
    $response = fgets($socket, 256);
    
    fputs($socket, "DATA\r\n");
    $response = fgets($socket, 256);
    
    $email_data = "To: $to\r\n";
    $email_data .= "From: Tasia Bot <no-reply@easierit.org>\r\n";
    $email_data .= "Subject: $subject\r\n";
    $email_data .= "Content-Type: text/plain; charset=UTF-8\r\n\r\n";
    $email_data .= $message . "\r\n";
    
    fputs($socket, $email_data);
    fputs($socket, ".\r\n");
    $response = fgets($socket, 256);
    
    fputs($socket, "QUIT\r\n");
    fclose($socket);
    
    return substr($response, 0, 3) == "250";
}

// Set headers first
header('Content-Type: application/xml');

// Database config
$dbHost = 'i.let-us.cyou';
$dbName = 'robust';
$dbUser = 'root';
$dbPass = 'CHANGE_ME_DB_PASSWORD';

try {
    // Connect to database
    $mysqli = new mysqli($dbHost, $dbUser, $dbPass, $dbName);
    if ($mysqli->connect_error) {
        sendXmlError("Database connection failed");
    }

    // Create table if needed
    $mysqli->query("CREATE TABLE IF NOT EXISTS tasia_reset (
        PrincipalID VARCHAR(36) PRIMARY KEY,
        reset_code VARCHAR(6) NOT NULL,
        created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
    )");

    // Read input
    $input = file_get_contents('php://input');
    if (!$input) {
        sendXmlError("No input received");
    }

    // Parse XML
    $xml = simplexml_load_string($input);
    if (!$xml) {
        sendXmlError("Invalid XML format");
    }

    $action = (string)$xml->action;
    $first = trim((string)$xml->first);
    $last = trim((string)$xml->last);

    if ($action === 'request') {
        if (!$first || !$last) {
            sendXmlError("Please provide first and last name");
        }

        // Find user
        $stmt = $mysqli->prepare("SELECT PrincipalID, Email FROM UserAccounts WHERE FirstName = ? AND LastName = ? LIMIT 1");
        if (!$stmt) {
            sendXmlError("Database query failed");
        }

        $stmt->bind_param("ss", $first, $last);
        $stmt->execute();
        $result = $stmt->get_result();
        $user = $result->fetch_assoc();

        if (!$user) {
            sendXmlError("User '$first $last' not found in database");
        }

        $email = $user['Email'];
        if (!$email || !filter_var($email, FILTER_VALIDATE_EMAIL)) {
            sendXmlError("No valid email address found for this user");
        }

        // Generate code
        $resetCode = sprintf("%06d", rand(100000, 999999));
        
        // Save code
        $stmt2 = $mysqli->prepare("REPLACE INTO tasia_reset (PrincipalID, reset_code) VALUES (?, ?)");
        $stmt2->bind_param("ss", $user['PrincipalID'], $resetCode);
        $stmt2->execute();

        // Send email
        $emailSent = false;
        
        // Try SMTP first
        try {
            $emailSent = sendResetEmailSMTP($email, $first, $resetCode);
        } catch (Exception $e) {
            // SMTP failed, try basic mail()
            $emailSent = sendResetEmail($email, $first, $resetCode);
        }
        
        if ($emailSent) {
            sendXmlSuccess("Reset code has been sent to your email address: " . substr($email, 0, 3) . "***@" . substr(strrchr($email, '@'), 1));
        } else {
            // Email failed but code is generated - for testing purposes
            sendXmlSuccess("Email sending failed, but reset code generated: $resetCode (Fix email configuration)");
        }
    }

    if ($action === 'confirm') {
        $code = trim((string)$xml->code);
        $newPassword = (string)$xml->newPassword;

        if (!$first || !$last || !$code || !$newPassword) {
            sendXmlError("Missing required fields");
        }

        // Find user
        $stmt = $mysqli->prepare("SELECT PrincipalID FROM UserAccounts WHERE FirstName = ? AND LastName = ? LIMIT 1");
        $stmt->bind_param("ss", $first, $last);
        $stmt->execute();
        $result = $stmt->get_result();
        $user = $result->fetch_assoc();

        if (!$user) {
            sendXmlError("User not found");
        }

        // Check code
        $stmt2 = $mysqli->prepare("SELECT reset_code FROM tasia_reset WHERE PrincipalID = ? AND created_at > DATE_SUB(NOW(), INTERVAL 15 MINUTE)");
        $stmt2->bind_param("s", $user['PrincipalID']);
        $stmt2->execute();
        $result2 = $stmt2->get_result();
        $codeData = $result2->fetch_assoc();

        if (!$codeData || $codeData['reset_code'] !== $code) {
            sendXmlError("Invalid or expired reset code");
        }

        // Update password (using simple MD5 for now - improve security later)
        $salt = md5(uniqid());
        $hash = md5(md5($newPassword) . ':' . $salt);

        $stmt3 = $mysqli->prepare("UPDATE auth SET passwordSalt = ?, passwordHash = ? WHERE UUID = ?");
        $stmt3->bind_param("sss", $salt, $hash, $user['PrincipalID']);
        
        if ($stmt3->execute()) {
            // Delete reset code
            $mysqli->prepare("DELETE FROM tasia_reset WHERE PrincipalID = ?")->execute([$user['PrincipalID']]);
            sendXmlSuccess("Password reset successfully!");
        } else {
            sendXmlError("Failed to update password");
        }
    }

    sendXmlError("Unknown action: $action");

} catch (Exception $e) {
    sendXmlError("System error: " . $e->getMessage());
}
?>