<?php
session_start(); // Start session for state (cookies, no local storage)
if (!isset($_SESSION['history'])) {
    $_SESSION['history'] = [];
}
?>

<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <title>AI Chat App</title>
    <style>
        body { font-family: Arial, sans-serif; margin: 20px; background: #fff; color: #000; }
        .chat-box { border: 1px solid #ccc; padding: 10px; height: 350px; overflow-y: auto; background: #f9f9f9; }
        .user { color: #0000ff; }
        .assistant { color: #008000; }
        .system { color: #ff0000; }
        .image { max-width: 200px; margin: 10px 0; display: block; }
        #message { width: 70%; padding: 8px; border: 1px solid #ccc; }
        button { padding: 8px 12px; cursor: pointer; }
    </style>
</head>
<body>
    <h1>Chat with Your AI</h1>
    <div class="chat-box" id="chat-box">
        <?php foreach ($_SESSION['history'] as $msg): ?>
            <p class="<?= $msg['role'] ?>">
                <strong><?= ucfirst($msg['role']) ?>:</strong> <?= htmlspecialchars($msg['content']) ?>
                <?php
                // Display images from !imagine responses
                if (preg_match('/(https?:\/\/\S+\.(jpg|png|gif))/i', $msg['content'], $matches)) {
                    echo '<br><img src="' . $matches[0] . '" alt="Generated Image" class="image">';
                }
                ?>
            </p>
        <?php endforeach; ?>
    </div>
    <input type="text" id="message" placeholder="Type message (e.g., !imagine a spaceship)" autofocus>
    <button onclick="sendMessage()">Send</button>
    <button onclick="resetChat()">Reset</button>

    <script>
        function sendMessage() {
            const input = document.getElementById('message');
            const message = input.value.trim();
            if (!message) return;

            const chatBox = document.getElementById('chat-box');
            // Add user message to UI
            const userP = document.createElement('p');
            userP.className = 'user';
            userP.innerHTML = `<strong>User:</strong> ${message.replace(/</g, '&lt;')}`;
            chatBox.appendChild(userP);
            input.value = '';
            chatBox.scrollTop = chatBox.scrollHeight;

            // AJAX request
            const xhr = new XMLHttpRequest();
            xhr.open('POST', 'api.php', true);
            xhr.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded');
            xhr.onreadystatechange = function () {
                if (xhr.readyState === 4 && xhr.status === 200) {
                    const response = JSON.parse(xhr.responseText);
                    if (response.error) {
                        const errorP = document.createElement('p');
                        errorP.className = 'system';
                        errorP.innerHTML = `<strong>System:</strong> ${response.error.replace(/</g, '&lt;')}`;
                        chatBox.appendChild(errorP);
                    } else {
                        const aiP = document.createElement('p');
                        aiP.className = 'assistant';
                        let content = response.content.replace(/</g, '&lt;');
                        // Check for image URL
                        const imgMatch = content.match(/https?:\/\/\S+\.(jpg|png|gif)/i);
                        if (imgMatch) {
                            content += `<br><img src="${imgMatch[0]}" alt="Generated Image" class="image">`;
                        }
                        aiP.innerHTML = `<strong>Assistant:</strong> ${content}`;
                        chatBox.appendChild(aiP);
                    }
                    chatBox.scrollTop = chatBox.scrollHeight;
                } else if (xhr.readyState === 4) {
                    const errorP = document.createElement('p');
                    errorP.className = 'system';
                    errorP.innerHTML = `<strong>System:</strong> Error: ${xhr.statusText || 'Request failed'}`;
                    chatBox.appendChild(errorP);
                    chatBox.scrollTop = chatBox.scrollHeight;
                }
            };
            xhr.send(`message=${encodeURIComponent(message)}`);
        }

        function resetChat() {
            const xhr = new XMLHttpRequest();
            xhr.open('POST', 'api.php', true);
            xhr.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded');
            xhr.onreadystatechange = function () {
                if (xhr.readyState === 4 && xhr.status === 200) {
                    document.getElementById('chat-box').innerHTML = '';
                }
            };
            xhr.send('reset=1');
        }

        // Enter key to send
        document.getElementById('message').addEventListener('keypress', function (e) {
            if (e.key === 'Enter') sendMessage();
        });
    </script>
</body>
</html>