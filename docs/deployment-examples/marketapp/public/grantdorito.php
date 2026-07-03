<?php
declare(strict_types=1);
?><!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>I-Grid Dorito Grant</title>
  <style>
    :root { --bg:#030712; --panel:#071228; --line:#1be7ff44; --text:#d8f3ff; --muted:#7bbad1; --neon:#13ffc4; --danger:#ff5f7b; }
    *{box-sizing:border-box}
    body{margin:0;font-family:Consolas,Monaco,monospace;background:radial-gradient(900px 420px at 10% -10%,#083b63 0%,transparent 60%),var(--bg);color:var(--text)}
    .wrap{max-width:780px;margin:0 auto;padding:16px}
    .panel{background:linear-gradient(180deg,#071428,#051024);border:1px solid var(--line);border-radius:14px;padding:14px;box-shadow:0 0 22px #00e5ff1f}
    h1{margin:0 0 10px;color:var(--neon);font-size:22px;letter-spacing:.06em}
    .muted{color:var(--muted)}
    .row{display:flex;gap:10px;flex-wrap:wrap;align-items:center}
    input,button{background:#07162b;border:1px solid #178ca23d;color:var(--text);padding:10px;border-radius:8px}
    input{min-width:220px;flex:1}
    button{cursor:pointer;border-color:#1be7ff77}
    button:hover{box-shadow:0 0 12px #1be7ff55}
    .grid{display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-top:10px}
    .grid .full{grid-column:1/-1}
    .step{border:1px solid #1be7ff33;border-radius:10px;padding:10px;background:#061425}
    .step h3{margin:0 0 8px;color:var(--neon);font-size:14px;letter-spacing:.04em}
    .msg{margin-top:8px;font-weight:700}
    .ok{color:var(--neon)}
    .err{color:var(--danger)}
    @media (max-width:760px){.grid{grid-template-columns:1fr}}
  </style>
</head>
<body>
<div class="wrap">
  <div class="panel">
    <h1>I-Grid Dorito$ Grant</h1>
    <div class="muted">Simulation only. No real-world payment.</div>

    <div id="authBlock" class="grid" style="margin-top:12px">
      <input id="avatar_uuid" class="full" placeholder="Admin Avatar UUID">
      <button id="otpRequestBtn" type="button">Send IM OTP</button>
      <input id="otp_code" class="full" placeholder="Enter OTP code">
      <button id="loginBtn" type="button">Verify OTP</button>
      <div id="authMsg" class="msg"></div>
    </div>

    <div id="grantBlock" style="display:none;margin-top:10px">
      <div class="step" id="wizStep1">
        <h3>Step 1 - Target and Amount</h3>
        <div class="grid" style="margin-top:0">
          <input id="target_uuid" class="full" placeholder="Target Avatar UUID">
          <input id="usd_amount" type="number" step="0.01" min="0.01" value="1.00" placeholder="USD Amount">
          <button id="initBtn" type="button">Next: Generate Vista Token</button>
        </div>
      </div>

      <div class="step" id="wizStep2" style="margin-top:10px;display:none">
        <h3>Step 2 - Vista Token Check</h3>
        <div class="grid" style="margin-top:0">
          <input id="vista_token" class="full" placeholder="Vista Token">
          <input id="access_key" class="full" placeholder="Access Key (last 8 of UUID)">
          <button id="toSecurityBtn" type="button">Next: Security Check</button>
        </div>
      </div>

      <div class="step" id="wizStep3" style="margin-top:10px;display:none">
        <h3>Step 3 - Security Question</h3>
        <div class="grid" style="margin-top:0">
          <input id="security_answer" class="full" placeholder="Security answer">
          <button id="grantBtn" type="button">Confirm Dorito Grant</button>
        </div>
      </div>

      <div id="grantMsg" class="msg" style="margin-top:10px"></div>
    </div>

    <div class="muted" style="margin-top:12px;font-size:12px">Creator: Tasia (Tasia AI model)</div>
  </div>
</div>

<script>
const el = id => document.getElementById(id);
const uuidRe = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
let csrfToken = '';

const api = (action, payload = {}, method = 'GET') => {
  if (method === 'GET') {
    const qs = new URLSearchParams({ action, ...payload }).toString();
    return fetch('api.php?' + qs, { credentials: 'same-origin' }).then(r => r.json());
  }
  const body = new URLSearchParams({ action, ...payload });
  if (csrfToken && action !== 'login') body.set('csrf_token', csrfToken);
  return fetch('api.php', { method: 'POST', body, credentials: 'same-origin' }).then(r => r.json());
};

function showMsg(id, text, ok = false) {
  const node = el(id);
  node.textContent = text || '';
  node.classList.remove('ok', 'err');
  if (!text) return;
  node.classList.add(ok ? 'ok' : 'err');
}

function showWizardStep(step) {
  el('wizStep1').style.display = (step === 1 ? 'block' : 'none');
  el('wizStep2').style.display = (step === 2 ? 'block' : 'none');
  el('wizStep3').style.display = (step === 3 ? 'block' : 'none');
}

async function checkStatus() {
  const res = await api('status', {}, 'GET');
  if (!res.success) return;
  csrfToken = res.data?.csrf_token || '';
  const ctx = res.data?.context || {};
  if (ctx.authenticated && ctx.is_admin) {
    el('authBlock').style.display = 'none';
    el('grantBlock').style.display = 'grid';
    el('target_uuid').value = ctx.uuid || '';
  }
}

el('loginBtn').addEventListener('click', async () => {
  const uuid = el('avatar_uuid').value.trim();
  const otpCode = el('otp_code').value.trim();
  if (!uuidRe.test(uuid)) return showMsg('authMsg', 'Invalid avatar UUID format.');
  if (!otpCode) return showMsg('authMsg', 'OTP code required.');

  const res = await api('otp_verify', { avatar_uuid: uuid, otp_code: otpCode }, 'POST');
  if (!res.success) return showMsg('authMsg', res.error || 'Login failed.');

  csrfToken = res.data?.csrf_token || '';
  const ctx = res.data?.context || {};
  if (!ctx.is_admin) return showMsg('authMsg', 'Admin access required.');

  showMsg('authMsg', 'Authenticated.', true);
  el('authBlock').style.display = 'none';
  el('grantBlock').style.display = 'grid';
  el('target_uuid').value = ctx.uuid || '';
});

el('otpRequestBtn').addEventListener('click', async () => {
  const uuid = el('avatar_uuid').value.trim();
  if (!uuidRe.test(uuid)) return showMsg('authMsg', 'Invalid avatar UUID format.');

  const res = await api('otp_request', { avatar_uuid: uuid }, 'POST');
  if (!res.success) return showMsg('authMsg', res.error || 'Failed to send OTP.');
  showMsg('authMsg', 'OTP sent by IM. Enter code and verify.', true);
});

el('initBtn').addEventListener('click', async () => {
  const target = el('target_uuid').value.trim();
  const usd = el('usd_amount').value.trim();
  if (!uuidRe.test(target)) return showMsg('grantMsg', 'Invalid target UUID format.');
  if (!usd || Number(usd) <= 0) return showMsg('grantMsg', 'USD amount must be positive.');

  const res = await api('dorito_init', { target_uuid: target, usd_amount: usd }, 'POST');
  if (!res.success) return showMsg('grantMsg', res.error || 'Failed to generate Vista token.');

  const data = res.data || {};
  const normalizedTarget = target.replace(/[^0-9a-f]/ig, '').toLowerCase();
  const autoAccessKey = normalizedTarget.length >= 8 ? normalizedTarget.slice(-8) : '';
  el('vista_token').value = data.vista_token || '';
  if (autoAccessKey) {
    el('access_key').value = autoAccessKey;
  }
  showMsg('grantMsg', `Token ready. ${data.security_question || 'Answer security question.'} Doritos: ${data.doritos || 0}`, true);
  showWizardStep(2);
});

el('toSecurityBtn').addEventListener('click', () => {
  if (!el('vista_token').value.trim() || !el('access_key').value.trim()) {
    showMsg('grantMsg', 'Fill Vista token and access key first.');
    return;
  }
  showWizardStep(3);
});

el('grantBtn').addEventListener('click', async () => {
  const payload = {
    vista_token: el('vista_token').value.trim(),
    access_key: el('access_key').value.trim(),
    security_answer: el('security_answer').value.trim(),
  };
  if (!payload.vista_token || !payload.access_key || !payload.security_answer) {
    return showMsg('grantMsg', 'Fill token, access key, and security answer.');
  }

  const res = await api('dorito_grant', payload, 'POST');
  if (!res.success) return showMsg('grantMsg', res.error || 'Dorito grant failed.');

  const out = res.data?.result || {};
  showMsg('grantMsg', `Dorito$ granted: ${out.granted || 0} to ${out.target_name || out.target_uuid || '-'}. Balance: ${out.new_balance ?? 'n/a'}`, true);
  el('security_answer').value = '';
  showWizardStep(1);
});

checkStatus();
</script>
</body>
</html>
