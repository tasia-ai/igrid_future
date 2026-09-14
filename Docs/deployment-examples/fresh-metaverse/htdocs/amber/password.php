<?php
declare(strict_types=1);
error_reporting(E_ALL & ~E_NOTICE);

$grid_name = 'Fresh Metaverse';
$loginuri = 'os.tasia.work.gd:22000';
$invite_code = getenv('FRESH_INVITE_CODE') ?: 'CHANGE_ME';
$db_host = '127.0.0.1';
$db_port = '5432';
$db_name = 'robust';
$db_user = getenv('FRESH_DB_USER') ?: 'opensim';
$db_pass = getenv('FRESH_DB_PASS') ?: 'CHANGE_ME';

function h($s){ return htmlspecialchars((string)($s ?? ''), ENT_QUOTES, 'UTF-8'); }
function make_hash(string $password, string $salt): string { return md5(md5($password) . ':' . $salt); }
function new_salt(string $principal): string { return md5($principal . microtime(true) . random_bytes(8)); }

$msg = ''; $ok = false;
$mode = $_POST['mode'] ?? 'change';

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $first = trim($_POST['first'] ?? '');
    $last = trim($_POST['last'] ?? '');
    $email = trim($_POST['email'] ?? '');
    $old = $_POST['old_password'] ?? '';
    $pass = $_POST['password'] ?? '';
    $pass2 = $_POST['password2'] ?? '';
    $invite = trim($_POST['invite'] ?? '');

    if (!preg_match('/^[a-zA-Z0-9]{2,15}$/', $first)) $msg = 'First name 2-15 alphanum only.';
    elseif (!preg_match('/^[a-zA-Z0-9]{2,15}$/', $last)) $msg = 'Last name 2-15 alphanum only.';
    elseif (strlen($pass) < 8) $msg = 'New password min 8 chars.';
    elseif ($pass !== $pass2) $msg = 'New passwords do not match.';
    elseif ($mode === 'reset' && $invite !== $invite_code) $msg = 'Invalid invite/reset code.';
    elseif ($mode === 'reset' && !filter_var($email, FILTER_VALIDATE_EMAIL)) $msg = 'Reset requires the email on the account.';
    elseif ($mode !== 'reset' && strlen($old) < 1) $msg = 'Current password is required for password change.';
    else {
        try {
            $pdo = new PDO("pgsql:host=$db_host;port=$db_port;dbname=$db_name", $db_user, $db_pass, [PDO::ATTR_ERRMODE=>PDO::ERRMODE_EXCEPTION, PDO::ATTR_TIMEOUT=>3]);
            $stmt = $pdo->prepare('SELECT "PrincipalID", "Email" FROM "UserAccounts" WHERE lower("FirstName")=lower(:f) AND lower("LastName")=lower(:l) LIMIT 1');
            $stmt->execute([':f'=>$first, ':l'=>$last]);
            $acct = $stmt->fetch(PDO::FETCH_ASSOC);
            if (!$acct) throw new Exception('Avatar account not found.');
            $principal = (string)$acct['PrincipalID'];

            if ($mode === 'reset') {
                if (strtolower(trim((string)$acct['Email'])) !== strtolower($email)) throw new Exception('Email does not match this avatar account.');
            } else {
                $stmt = $pdo->prepare('SELECT "passwordHash", "passwordSalt" FROM "auth" WHERE "uuid"=:id LIMIT 1');
                $stmt->execute([':id'=>$principal]);
                $auth = $stmt->fetch(PDO::FETCH_ASSOC);
                if (!$auth || make_hash($old, (string)$auth['passwordSalt']) !== (string)$auth['passwordHash']) throw new Exception('Current password is incorrect.');
            }

            $salt = new_salt($principal);
            $hash = make_hash($pass, $salt);
            $key = md5(random_bytes(16));
            $stmt = $pdo->prepare('UPDATE "auth" SET "passwordHash"=:h, "passwordSalt"=:s, "webLoginKey"=:k WHERE "uuid"=:id');
            $stmt->execute([':h'=>$hash, ':s'=>$salt, ':k'=>$key, ':id'=>$principal]);
            if ($stmt->rowCount() < 1) {
                $stmt = $pdo->prepare('INSERT INTO "auth" ("uuid","passwordHash","passwordSalt","webLoginKey") VALUES (:id,:h,:s,:k)');
                $stmt->execute([':id'=>$principal, ':h'=>$hash, ':s'=>$salt, ':k'=>$key]);
            }
            $ok = true;
            $msg = ($mode === 'reset' ? 'Password reset complete.' : 'Password changed.') . ' You can now log in at <b>'.h($loginuri).'</b>.';
        } catch (Throwable $e) {
            $msg = 'Oops: ' . h($e->getMessage());
        }
    }
}
?>
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Change Password - Fresh Metaverse</title>
<link href="https://fonts.googleapis.com/css2?family=Nunito:wght@700;800&family=Pacifico&display=swap" rel="stylesheet">
<style>
*{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;background:radial-gradient(circle at 20% 20%,#FFD1DC 0%,transparent 40%),radial-gradient(circle at 80% 80%,#E8DFF5 0%,transparent 40%),linear-gradient(180deg,#FFF7FB,#F8F0FF);font-family:Nunito,system-ui;color:#4A3040}.card{background:rgba(255,255,255,.94);border:1px solid #FFE0EA;border-radius:22px;box-shadow:0 10px 30px rgba(255,143,171,.18);padding:24px;max-width:560px;width:92%}h1{font-family:Pacifico,cursive;margin:0 0 6px;font-size:28px;text-align:center}label{font-weight:800;font-size:13px;display:block;margin:10px 0 4px}input,select{width:100%;padding:10px 12px;border:1px solid #FFE0EA;border-radius:12px;background:#FFF7FB}.btn{display:block;width:100%;margin-top:16px;background:linear-gradient(135deg,#FF8FAB,#FFB5D8);color:#fff;padding:12px;border-radius:999px;font-weight:800;border:none;cursor:pointer}.msg{margin:10px 0;padding:10px 12px;border-radius:12px;font-weight:700}.ok{background:#E8FFF0;border:1px solid #B5E8C8;color:#2a6}.err{background:#FFF0F0;border:1px solid #FFB5D8;color:#a33}small{color:#8A6A7A}a{color:#C14B7A}.hint{background:#FFF7FB;border:1px solid #FFE0EA;border-radius:14px;padding:10px;margin-top:12px;font-size:13px;color:#8A6A7A}.reset-only{display:none}form[data-mode="reset"] .reset-only{display:block}form[data-mode="reset"] .change-only{display:none}
</style>
<div class="card">
<h1>🔐 Fresh Password</h1>
<p style="text-align:center;margin:0 0 12px">Change or reset your Fresh Metaverse avatar password.<br><small>Grid: <code><?=h($loginuri)?></code></small></p>
<?php if($msg): ?><div class="msg <?= $ok?'ok':'err' ?>"><?= $msg ?></div><?php endif; ?>
<form method="post" id="pwform" data-mode="<?=h($mode)?>">
  <label>Action</label>
  <select name="mode" id="mode">
    <option value="change" <?= $mode==='change'?'selected':'' ?>>Change password (current password required)</option>
    <option value="reset" <?= $mode==='reset'?'selected':'' ?>>Reset password (email + invite/reset code)</option>
  </select>
  <label>First Name</label><input name="first" required pattern="[a-zA-Z0-9]{2,15}" value="<?=h($_POST['first'] ?? '')?>">
  <label>Last Name</label><input name="last" required pattern="[a-zA-Z0-9]{2,15}" value="<?=h($_POST['last'] ?? '')?>">
  <div class="change-only"><label>Current Password</label><input name="old_password" type="password"></div>
  <div class="reset-only"><label>Email on Account</label><input name="email" type="email" value="<?=h($_POST['email'] ?? '')?>"><label>Invite / Reset Code</label><input name="invite" placeholder="Ask Amber for reset code"></div>
  <label>New Password (min 8)</label><input name="password" type="password" required minlength="8">
  <label>Confirm New Password</label><input name="password2" type="password" required minlength="8">
  <button class="btn">Save Password</button>
</form>
<div class="hint">For normal changes, use your current password. If you forgot it, use reset mode with the email on your account and the invite/reset code from Amber.</div>
<p style="text-align:center"><a href="/amber/register.php">Create account</a> • <a href="/amber/">Back home</a></p>
</div>
<script>const f=document.getElementById('pwform'),m=document.getElementById('mode');m.addEventListener('change',()=>f.dataset.mode=m.value);</script>
</html>
