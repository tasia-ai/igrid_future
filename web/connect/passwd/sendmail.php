<?php
use PHPMailer\PHPMailer\PHPMailer;
use PHPMailer\PHPMailer\Exception;
require 'vendor/autoload.php';
require 'config.php';

function sendEmail($to, $subject, $body) {
    global $smtpHost, $smtpUser, $smtpPass, $mailFrom, $mailName;

    $mail = new PHPMailer(true);
    try {
        $mail->isSMTP();
        $mail->Host = $smtpHost;
        $mail->SMTPAuth = true;
        $mail->Username = $smtpUser;
        $mail->Password = $smtpPass;
        $mail->SMTPSecure = 'tls';
        $mail->Port = 587;

        $mail->setFrom($mailFrom, $mailName);
        $mail->addAddress($to);
        $mail->Subject = $subject;
        $mail->Body    = $body;

        $mail->send();
    } catch (Exception $e) {
        if ($debug) echo json_encode(["error" => "Mailer Error: " . $mail->ErrorInfo]);
    }
}
?>
