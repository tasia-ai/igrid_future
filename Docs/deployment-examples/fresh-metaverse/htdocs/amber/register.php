<?php
// Amber's Fresh Metaverse — Cute Registration (PGSQL, Robust-direct)
// Creates accounts directly in PGSQL robust DB (useraccounts + auth + griduser + inventory stub)
// Works without Diva WiFi. Matches UserAccountService.CreateUser logic (hash = md5(md5(password)+":"+salt))

error_reporting(E_ALL & ~E_NOTICE);
$grid_name = "Fresh Metaverse";
$loginuri = "os.tasia.work.gd:22000";

// --- DB config (same as oswelcome-amber.php) ---
$db_host = "127.0.0.1";
$db_port = "5432";
$db_name = "robust";
$db_user = getenv('FRESH_DB_USER') ?: "opensim";
$db_pass = getenv('FRESH_DB_PASS') ?: "CHANGE_ME";
$invite_code = getenv('FRESH_INVITE_CODE') ?: "CHANGE_ME";

function h($s){ return htmlspecialchars($s ?? "", ENT_QUOTES, 'UTF-8'); }
$msg = ""; $ok = false;

if($_SERVER['REQUEST_METHOD'] === 'POST'){
    $first = trim($_POST['first'] ?? "");
    $last = trim($_POST['last'] ?? "");
    $email = trim($_POST['email'] ?? "");
    $pass = $_POST['password'] ?? "";
    $pass2 = $_POST['password2'] ?? "";
    $invite = trim($_POST['invite'] ?? "");
    // validation
    if($invite !== $invite_code) $msg = "Invalid invite code — ask Amber for the code. 🌸";
    elseif(!preg_match('/^[a-zA-Z0-9]{2,15}$/', $first)) $msg = "First name 2-15 alphanum only.";
    elseif(!preg_match('/^[a-zA-Z0-9]{2,15}$/', $last)) $msg = "Last name 2-15 alphanum only.";
    elseif(!filter_var($email, FILTER_VALIDATE_EMAIL)) $msg = "Invalid email.";
    elseif(strlen($pass) < 8) $msg = "Password min 8 chars.";
    elseif($pass !== $pass2) $msg = "Passwords don't match.";
    else{
        // Try Robust API first (proper way — creates inventory + auth + griduser atomically)
        $robust_ok = false; $robust_msg = "";
        $principal_try = strtolower(trim(shell_exec('powershell -Command "[guid]::NewGuid().ToString()"') ?: sprintf('%04x%04x-%04x-%04x-%04x-%04x%04x%04x', mt_rand(0,0xffff), mt_rand(0,0xffff), mt_rand(0,0xffff), mt_rand(0,0x0fff)|0x4000, mt_rand(0,0x3fff)|0x8000, mt_rand(0,0xffff), mt_rand(0,0xffff), mt_rand(0,0xffff))));
        if(!preg_match('/^[0-9a-f-]{36}$/', $principal_try)) $principal_try = sprintf('%s-%s-%s-%s-%s', bin2hex(random_bytes(4)), bin2hex(random_bytes(2)), bin2hex(random_bytes(2)), bin2hex(random_bytes(2)), bin2hex(random_bytes(6)));
        $payload = json_encode(["FirstName"=>$first, "LastName"=>$last, "Email"=>$email, "Password"=>$pass, "PrincipalID"=>$principal_try, "ScopeID"=>"00000000-0000-0000-0000-000000000000"]);
        $ctx = stream_context_create(["http"=>["method"=>"POST","header"=>"Content-Type: application/json\r\n","content"=>$payload,"timeout"=>5]]);
        $resp = @file_get_contents("http://127.0.0.1:22001/CreateUser", false, $ctx);
        if($resp !== false){
            $j = json_decode($resp, true);
            if(isset($j["Success"]) && $j["Success"]){
                $robust_ok = true;
                $msg = "Welcome, $first $last! ✨ Created via Robust API. Login URI: <b>".h($loginuri)."</b>";
                $ok = true;
            } elseif(isset($j["Message"])) $robust_msg = $j["Message"];
            elseif(isset($j["error"])) $robust_msg = $j["error"];
        }
        if(!$robust_ok){
            // Fallback: direct PGSQL (as before) — ensures account even if Robust API is down
            try{
                $pdo = new PDO("pgsql:host=$db_host;port=$db_port;dbname=$db_name", $db_user, $db_pass, [PDO::ATTR_ERRMODE=>PDO::ERRMODE_EXCEPTION]);
                $chk = $pdo->prepare('SELECT 1 FROM "UserAccounts" WHERE lower("FirstName")=lower(:f) AND lower("LastName")=lower(:l) LIMIT 1');
                $chk->execute([':f'=>$first, ':l'=>$last]);
                if($chk->fetch()) throw new Exception("Avatar name already taken — try another." . ($robust_msg ? " (Robust: $robust_msg)" : ""));
                $chk = $pdo->prepare('SELECT 1 FROM "UserAccounts" WHERE lower("Email")=lower(:e) LIMIT 1');
                $chk->execute([':e'=>$email]);
                if($chk->fetch()) throw new Exception("Email already registered." . ($robust_msg ? " (Robust: $robust_msg)" : ""));
                $principal = $principal_try;
                $scope = "00000000-0000-0000-0000-000000000000";
                $created = time();
                $salt = md5($principal . microtime(true) . random_bytes(8));
                $hash = md5(md5($pass) . ":" . $salt);
                $serviceUrls = "HomeURI=http%3a%2f%2fos.tasia.work.gd%3a22000%2f InventoryServerURI=http%3a%2f%2fos.tasia.work.gd%3a22000%2f AssetServerURI=http%3a%2f%2fos.tasia.work.gd%3a22000%2f";
                $pdo->prepare('INSERT INTO "UserAccounts" ("PrincipalID","ScopeID","FirstName","LastName","Email","ServiceURLs","Created","UserLevel","UserFlags","UserTitle") VALUES (:id,:scope,:f,:l,:e,:urls,:c,0,0,\'\')')
                    ->execute([':id'=>$principal, ':scope'=>$scope, ':f'=>$first, ':l'=>$last, ':e'=>$email, ':urls'=>$serviceUrls, ':c'=>$created]);
                $pdo->prepare('INSERT INTO "auth" ("UUID","passwordHash","passwordSalt","webLoginKey") VALUES (:id,:h,:s,:k)')
                    ->execute([':id'=>$principal, ':h'=>$hash, ':s'=>$salt, ':k'=>md5(random_bytes(16))]);
                $pdo->prepare('INSERT INTO "GridUser" ("UserID","HomeRegionID","HomePosition","HomeLookAt","LastRegionID","LastPosition","LastLookAt","Online","Login","Logout") VALUES (:id,:home,\'[128,128,22]\',\'[0,1,0]\',:home,\'[128,128,22]\',\'[0,1,0]\',\'false\',0,0) ON CONFLICT ("UserID") DO NOTHING')
                    ->execute([':id'=>$principal, ':home'=>'10a07d4c-cf60-5e33-9786-2c18d95f4cb2']);
                $msg = "Welcome, $first $last! ✨ Created via direct DB (Robust API fallback" . ($robust_msg ? ": $robust_msg" : "") . "). Inventory will be created on first login. Login URI: <b>".h($loginuri)."</b>";
                $ok = true;
            }catch(Exception $e){ $msg = "Oops: ".h($e->getMessage() . ($robust_msg ? " [Robust: $robust_msg]" : "")); }
        }
    }
}
?>
<!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
<title>Join Fresh Metaverse — Amber 🌸</title>
<link href="https://fonts.googleapis.com/css2?family=Nunito:wght@700;800&family=Pacifico&display=swap" rel=stylesheet>
<style>
*{box-sizing:border-box}
body{margin:0;min-height:100vh;display:grid;place-items:center;background:radial-gradient(circle at 20% 20%,#FFD1DC 0%,transparent 40%),radial-gradient(circle at 80% 80%,#E8DFF5 0%,transparent 40%),linear-gradient(180deg,#FFF7FB,#F8F0FF);font-family:Nunito,system-ui;color:#4A3040}
.card{background:rgba(255,255,255,.92);backdrop-filter:blur(10px);border:1px solid #FFE0EA;border-radius:22px;box-shadow:0 10px 30px rgba(255,143,171,.18);padding:24px;max-width:520px;width:92%}
h1{font-family:Pacifico,cursive;margin:0 0 6px;font-size:28px;text-align:center}
label{font-weight:800;font-size:13px;display:block;margin:10px 0 4px}
input{width:100%;padding:10px 12px;border:1px solid #FFE0EA;border-radius:12px;background:#FFF7FB}
.btn{display:block;width:100%;margin-top:16px;background:linear-gradient(135deg,#FF8FAB,#FFB5D8);color:#fff;padding:12px;border-radius:999px;font-weight:800;border:none;cursor:pointer;box-shadow:0 8px 20px rgba(255,143,171,.3)}
.msg{margin:10px 0;padding:10px 12px;border-radius:12px;font-weight:700}
.ok{background:#E8FFF0;border:1px solid #B5E8C8;color:#2a6}
.err{background:#FFF0F0;border:1px solid #FFB5D8;color:#a33}
small{color:#8A6A7A}
a{color:#C14B7A}
</style>
<div class=card>
<h1>🌸 Join Fresh Metaverse</h1>
<p style="text-align:center;margin:0 0 12px">Create your avatar — PGSQL-powered, no WiFi needed. <small>Grid: <code><?=h($loginuri)?></code></small></p>
<?php if($msg): ?><div class="msg <?= $ok?'ok':'err' ?>"><?= $msg ?></div><?php endif; ?>
<?php if($ok): ?>
<p style="text-align:center"><a href="/" class="btn" style="display:inline-block;width:auto;padding:10px 18px;text-decoration:none">🏠 Back Home</a> <a href="https://tasiaviewer.work/os/" target="_blank" class="btn" style="display:inline-block;width:auto;background:#fff;color:#4A3040;border:2px solid #FFE0EA;box-shadow:none">💖 Get Tasia Viewer</a></p>
<?php else: ?>
<form method=post>
<label>First Name</label><input name=first required pattern="[a-zA-Z0-9]{2,15}" placeholder="Amber">
<label>Last Name</label><input name=last required pattern="[a-zA-Z0-9]{2,15}" placeholder="Star">
<label>Email</label><input name=email type=email required placeholder="you@example.com">
<label>Password (min 8)</label><input name=password type=password required minlength=8>
<label>Confirm Password</label><input name=password2 type=password required minlength=8>
<label>Invite Code 🌸</label><input name=invite required placeholder="Ask Amber for code" title="Invite code required">
<button class=btn>✨ Create Avatar — Join Now!</button>
</form>
<?php endif; ?>
<p style="text-align:center;margin-top:12px"><small>After creation, add <code><?=h($loginuri)?></code> in Firestorm/Tasia Viewer → Login.</small><br><a href="/amber/password.php">Change or reset password</a></p>
</div>
