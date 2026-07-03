<?php
declare(strict_types=1);
?><!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>TASIA-NGC Marketplace</title>
  <style>
    :root {
      --bg:#030712; --panel:#071228; --card:#0b1a35; --line:#1be7ff44;
      --text:#d8f3ff; --muted:#7bbad1; --neon:#1be7ff; --neon2:#13ffc4; --danger:#ff5f7b;
    }
    *{box-sizing:border-box}
    body{margin:0;font-family:Consolas,Monaco,monospace;background:radial-gradient(1200px 600px at 10% -10%,#083b63 0%,transparent 60%),radial-gradient(900px 500px at 90% -20%,#0b3f33 0%,transparent 55%),var(--bg);color:var(--text)}
    .wrap{max-width:1180px;margin:0 auto;padding:18px}
    .panel{background:linear-gradient(180deg,#071428,#051024);border:1px solid var(--line);border-radius:14px;padding:14px;box-shadow:0 0 22px #00e5ff1f}
    h1{margin:0 0 12px;font-size:22px;letter-spacing:.08em;color:var(--neon)}
    .row{display:flex;gap:10px;flex-wrap:wrap;align-items:center}
    input,button,select,textarea{background:#07162b;border:1px solid #178ca23d;color:var(--text);padding:9px 10px;border-radius:8px}
    input{min-width:190px}
    button{cursor:pointer;border-color:#1be7ff77}
    button:hover{box-shadow:0 0 12px #1be7ff55}
    .muted{color:var(--muted)}
    .msg{margin-top:8px;font-weight:700}
    .grid{margin-top:14px;display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:12px}
    .pager{margin-top:12px;display:flex;gap:10px;align-items:center;flex-wrap:wrap}
    .card{background:linear-gradient(180deg,#0a1730,#071225);border:1px solid #1be7ff3a;border-radius:12px;padding:10px}
    .thumb{height:140px;border:1px dashed #1be7ff35;border-radius:8px;background:#061022;display:flex;align-items:center;justify-content:center;color:#4ea4bd;font-size:12px;overflow:hidden}
    .thumb img{width:100%;height:100%;object-fit:cover}
    .title{font-weight:700;margin:8px 0 6px;min-height:38px}
    .meta{display:flex;justify-content:space-between;font-size:12px;color:var(--muted);gap:8px}
    .price{color:var(--neon2);font-weight:700}
    .top{display:grid;grid-template-columns:1.2fr 1fr;gap:12px}
    .hidden{display:none!important}
    .app-locked .wrap{filter:blur(3px);pointer-events:none;user-select:none}
    .lockscreen{position:fixed;inset:0;display:flex;align-items:center;justify-content:center;background:radial-gradient(circle at 50% 20%,#0d2745 0%,#030712 65%);z-index:9999}
    .lock-card{width:min(540px,92vw);background:linear-gradient(180deg,#071428,#051024);border:1px solid #1be7ff4d;border-radius:16px;padding:20px;box-shadow:0 0 30px #00dfff3b}
    .lock-title{font-size:28px;color:var(--neon);letter-spacing:.12em;margin:0 0 8px}
    .lock-sub{margin:0 0 14px;color:var(--muted)}
    .lock-form{display:flex;flex-direction:column;gap:10px}
    .lock-msg{min-height:20px;color:var(--danger);font-weight:700}
    .admin-tabs{display:flex;gap:8px;flex-wrap:wrap;margin-top:10px}
    .admin-tab-btn{border:1px solid #1be7ff66;background:#061425;color:var(--text)}
    .admin-tab-btn.active{background:#0b2a49;box-shadow:0 0 10px #1be7ff55}
    .app-tabs{display:flex;gap:8px;flex-wrap:wrap;margin:12px 0}
    .app-tab-btn{border:1px solid #1be7ff66;background:#061425;color:var(--text)}
    .app-tab-btn.active{background:#0b2a49;box-shadow:0 0 10px #1be7ff55}
    .admin-tab{margin-top:10px}
    .admin-grid{display:grid;grid-template-columns:1fr 1fr;gap:10px}
    .admin-grid .full{grid-column:1 / -1}
    .admin-grid label{display:flex;flex-direction:column;gap:6px;font-size:12px;color:var(--muted)}
    textarea{min-height:110px;resize:vertical}
    @media (max-width:900px){.top{grid-template-columns:1fr}}
  </style>
</head>
<body class="app-locked">
<div id="lockscreen" class="lockscreen">
  <div class="lock-card">
    <h2 class="lock-title">I-Grid Marketplace</h2>
    <p class="lock-sub">Login uses I-Grid Authentication (i-auth). Continue there and come back automatically.</p>
    <form id="loginForm" class="lock-form">
      <button id="iauthLoginBtn" type="button">Authenticate with I-Grid</button>
      <div class="muted" style="font-size:12px">You will be redirected to <span class="mono">/i-auth</span> and returned here.</div>
      <div id="lockMsg" class="lock-msg"></div>
    </form>
  </div>
</div>

<div class="wrap">
  <h1>TASIA-NGC MARKETAPP :: TRON STANDALONE</h1>
  <div id="appTabs" class="app-tabs">
    <button id="appTabMarketBtn" class="app-tab-btn active" type="button">Marketplace</button>
    <button id="appTabAdminBtn" class="app-tab-btn hidden" type="button">Admin</button>
  </div>

  <div id="marketTabContent">
  <div class="top">
    <div class="panel">
      <div class="row">
        <button id="logoutBtn">Logout</button>
      </div>
      <div class="row" style="margin-top:10px">
        <input id="recipient_uuid" placeholder="Gift recipient UUID (optional)">
      </div>
      <div id="ctx" class="muted" style="margin-top:8px">Not authenticated</div>
      <div id="msg" class="msg"></div>
    </div>
    <div class="panel">
      <div class="row">
        <input id="q" placeholder="Search item name">
        <select id="region" style="min-width:220px">
          <option value="">All regions</option>
        </select>
      </div>
      <div class="row" style="margin-top:10px">
        <input id="minp" placeholder="Min price" type="number" step="0.01">
        <input id="maxp" placeholder="Max price" type="number" step="0.01">
        <button id="filterBtn">Filter</button>
      </div>
    </div>
  </div>

  <div id="doritoPanel" class="panel hidden" style="margin-top:12px">
    <div class="row" style="justify-content:space-between">
      <strong style="color:var(--neon2)">Dorito$ Grant Wizard</strong>
      <span class="muted">Simulation only. No real-world payment.</span>
    </div>
    <div class="admin-grid" style="margin-top:10px">
      <label>
        Target Avatar UUID
        <input id="dorito_target_uuid" type="text" placeholder="4382...">
      </label>
      <label>
        USD Amount
        <input id="dorito_usd_amount" type="number" step="0.01" min="0.01" value="1">
      </label>
      <div class="full row">
        <button id="doritoInitBtn" type="button">Generate Vista Token</button>
        <div id="doritoInitMsg" class="muted"></div>
      </div>
      <label>
        Vista Token
        <input id="dorito_vista_token" type="text" placeholder="auto-generated token">
      </label>
      <label>
        Access Key (last 8 of UUID)
        <input id="dorito_access_key" type="text" placeholder="e.g. a943381">
      </label>
      <label class="full">
        Security Answer
        <input id="dorito_security_answer" type="text" placeholder="answer security question">
      </label>
      <div class="full row">
        <button id="doritoGrantBtn" type="button">Confirm Grant</button>
        <div id="doritoGrantMsg" class="muted"></div>
      </div>
    </div>
  </div>

  <div id="grid" class="grid"></div>
  <div id="pager" class="pager">
    <button id="prevPageBtn" type="button">Prev</button>
    <span id="pageInfo" class="muted">Page 1 / 1</span>
    <button id="nextPageBtn" type="button">Next</button>
  </div>
  </div>

  <div id="adminPanel" class="panel hidden" style="margin-top:12px">
    <div class="row" style="justify-content:space-between">
      <strong style="color:var(--neon)">Admin Panel</strong>
      <button id="adminRefreshBtn" type="button">Refresh</button>
    </div>
    <div class="admin-tabs">
      <button class="admin-tab-btn active" data-admin-tab="dashboard" type="button">Dashboard</button>
      <button class="admin-tab-btn" data-admin-tab="settings" type="button">Settings</button>
      <button class="admin-tab-btn" data-admin-tab="logs" type="button">Logs</button>
    </div>

    <div id="adminTabDashboard" class="admin-tab">
      <div id="adminSummary" class="muted" style="margin-top:8px"></div>
      <div id="adminRoutes" class="muted" style="margin-top:6px"></div>
      <div id="adminFailures" class="muted" style="margin-top:8px"></div>
    </div>

    <div id="adminTabSettings" class="admin-tab hidden">
      <form id="adminSettingsForm" class="admin-grid">
        <label>
          Delivery API URL
          <input id="set_delivery_api_url" name="delivery_api_url" type="text">
        </label>
        <label>
          Delivery Timeout Seconds
          <input id="set_timeout_seconds" name="timeout_seconds" type="number" min="5" max="120">
        </label>
        <label class="full">
          Region Overrides (uuid|label|url per line)
          <textarea id="set_region_overrides" name="region_overrides"></textarea>
        </label>
        <label class="full">
          Texture Proxy Template
          <input id="set_texture_proxy_template" name="texture_proxy_template" type="text">
        </label>
        <label>
          Delivery API Password
          <input id="set_delivery_api_password" name="delivery_api_password" type="text">
        </label>
        <label>
          Backup Login Enabled (1/0)
          <input id="set_auth_backup_enabled" name="auth_backup_enabled" type="number" min="0" max="1">
        </label>
        <label>
          Backup Password
          <input id="set_auth_backup_password" name="auth_backup_password" type="text">
        </label>
        <label>
          Backup Allowed UUID
          <input id="set_auth_backup_allowed_uuid" name="auth_backup_allowed_uuid" type="text">
        </label>
        <label class="full">
          Admin UUIDs (one per line)
          <textarea id="set_admin_uuids" name="admin_uuids"></textarea>
        </label>
        <div class="full row">
          <button id="adminSaveBtn" type="submit">Save Settings</button>
          <div id="adminSettingsMsg" class="muted"></div>
        </div>
      </form>
    </div>

    <div id="adminTabLogs" class="admin-tab hidden">
      <div id="adminLogs" class="muted"></div>
  </div>

  <div class="muted" style="margin-top:12px;font-size:12px">Creator: Tasia (Tasia AI model)</div>
</div>

</div>

<script>
const el = id => document.getElementById(id);
const uuidRe = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
let csrfToken = '';
let currentAppTab = 'market';
let currentPage = 1;
let totalPages = 1;

const api = (action, payload = {}, method = 'GET') => {
  if (method === 'GET') {
    const qs = new URLSearchParams({ action, ...payload }).toString();
    return fetch('api.php?' + qs, { credentials: 'same-origin' }).then(r => r.json());
  }
  const body = new URLSearchParams({ action, ...payload });
  if (csrfToken && action !== 'login' && action !== 'oidc_start') {
    body.set('csrf_token', csrfToken);
  }
  return fetch('api.php', { method: 'POST', body, credentials: 'same-origin' }).then(r => r.json());
};

const msg = t => { el('msg').textContent = t || ''; };
const lockMsg = t => { el('lockMsg').textContent = t || ''; };
const textureProxyTemplate = 'api.php?action=texture&uuid={uuid}';
let isAdmin = false;

function setCtx(ctx) {
  if (!ctx || !ctx.authenticated) {
    el('ctx').textContent = 'Not authenticated';
    isAdmin = false;
    return;
  }
  isAdmin = !!ctx.is_admin;
  el('ctx').textContent = `Logged as ${ctx.name} (${ctx.uuid}) | Balance: ${ctx.balance ?? 'N/A'}`;
  el('appTabAdminBtn').classList.toggle('hidden', !isAdmin);
  el('doritoPanel').classList.toggle('hidden', !isAdmin);
  if (isAdmin) {
    el('dorito_target_uuid').value = ctx.uuid || '';
  }
  if (!isAdmin && currentAppTab === 'admin') {
    switchAppTab('market');
  }
}

function switchAppTab(tab) {
  currentAppTab = tab;
  const market = tab === 'market';
  el('marketTabContent').classList.toggle('hidden', !market);
  el('adminPanel').classList.toggle('hidden', market || !isAdmin);
  el('appTabMarketBtn').classList.toggle('active', market);
  el('appTabAdminBtn').classList.toggle('active', !market);

  if (!market && isAdmin) {
    loadAdminDashboard();
    loadAdminSettings();
    loadAdminLogs();
  }
}

function lockApp(locked) {
  document.body.classList.toggle('app-locked', locked);
  el('lockscreen').classList.toggle('hidden', !locked);
}

async function startIauthFlow() {
  lockMsg('Redirecting to I-Grid authentication...');
  const res = await api('oidc_start', {}, 'POST');
  if (!res.success) {
    lockMsg(res.error || 'Failed to start I-Auth flow.');
    return;
  }
  const url = (res.data && res.data.authorize_url) || '';
  if (!url) {
    lockMsg('I-Auth authorize URL is missing.');
    return;
  }
  window.location.href = url;
}

function cardHtml(item) {
  const p = Number(item.price || 0).toFixed(2);
  let preview = item.texture_url || '';
  if (!preview && item.texture_uuid) {
    preview = textureProxyTemplate.replace('{uuid}', item.texture_uuid);
  }
  const thumb = preview ? `<img src="${preview}" alt="thumb">` : 'NO PREVIEW';
  return `
    <div class="card">
      <div class="thumb">${thumb}</div>
      <div class="title">${item.post_title || 'Unnamed item'}</div>
      <div class="meta"><span class="price">$${p}</span><span>${item.region_label || item.region_uuid || '-'}</span></div>
      <div class="row" style="margin-top:8px"><button data-buy="${item.ID}">Buy</button></div>
    </div>
  `;
}

async function loadRegions() {
  const res = await api('regions', {}, 'GET');
  if (!res.success) {
    return;
  }
  const options = ['<option value="">All regions</option>'];
  for (const r of (res.data.regions || [])) {
    const uuid = String(r.uuid || '');
    const label = String(r.label || uuid);
    if (!uuid) continue;
    options.push(`<option value="${uuid}">${label}</option>`);
  }
  el('region').innerHTML = options.join('');
}

async function loadAdminDashboard() {
  if (!isAdmin) {
    return;
  }
  const res = await api('admin_dashboard', {}, 'GET');
  if (!res.success) {
    el('adminSummary').textContent = 'Admin data unavailable: ' + (res.error || 'unknown error');
    return;
  }

  const summary = res.data.summary || {};
  el('adminSummary').textContent = `Items for sale: ${summary.items_for_sale ?? 0} | Orders: ${summary.orders_total ?? 'n/a'} | Region routes: ${summary.regions_with_overrides ?? 0}`;

  const routes = res.data.region_routes || {};
  const routeLines = Object.entries(routes).map(([uuid, url]) => `${uuid} -> ${url}`);
  el('adminRoutes').innerHTML = routeLines.length
    ? '<strong style="color:var(--neon2)">Region Delivery Routes</strong><br>' + routeLines.join('<br>')
    : '<strong style="color:var(--neon2)">Region Delivery Routes</strong><br>None';

  const failures = res.data.recent_failures || [];
  if (!failures.length) {
    el('adminFailures').innerHTML = '<strong style="color:var(--neon2)">Recent Delivery Failures</strong><br>None';
  } else {
    const lines = failures.map(f => `#${f.id} order:${f.order_id} ${f.status} :: ${f.message}`);
    el('adminFailures').innerHTML = '<strong style="color:var(--neon2)">Recent Delivery Failures</strong><br>' + lines.join('<br>');
  }
}

async function loadAdminSettings() {
  if (!isAdmin) return;
  const res = await api('admin_settings_get', {}, 'GET');
  if (!res.success) {
    el('adminSettingsMsg').textContent = 'Failed to load settings: ' + (res.error || 'unknown error');
    return;
  }
  const s = res.data.settings || {};
  el('set_delivery_api_url').value = s.delivery_api_url || '';
  el('set_region_overrides').value = s.region_overrides || '';
  el('set_texture_proxy_template').value = s.texture_proxy_template || '';
  el('set_delivery_api_password').value = s.delivery_api_password || '';
  el('set_timeout_seconds').value = s.timeout_seconds || 25;
  el('set_auth_backup_enabled').value = s.auth_backup_enabled ? 1 : 0;
  el('set_auth_backup_password').value = s.auth_backup_password || '';
  el('set_auth_backup_allowed_uuid').value = s.auth_backup_allowed_uuid || '';
  el('set_admin_uuids').value = s.admin_uuids || '';
  el('adminSettingsMsg').textContent = '';
}

async function loadAdminLogs() {
  if (!isAdmin) return;
  const res = await api('admin_logs', {}, 'GET');
  if (!res.success) {
    el('adminLogs').textContent = 'Failed to load logs: ' + (res.error || 'unknown error');
    return;
  }
  const logs = res.data.logs || [];
  if (!logs.length) {
    el('adminLogs').innerHTML = '<strong style="color:var(--neon2)">Logs</strong><br>No logs';
    return;
  }
  const lines = logs.map(l => `#${l.id} [${l.created_at}] order:${l.order_id} ${l.status} :: ${l.message}`);
  el('adminLogs').innerHTML = '<strong style="color:var(--neon2)">Logs</strong><br>' + lines.join('<br>');
}

function switchAdminTab(tab) {
  const tabs = ['dashboard', 'settings', 'logs'];
  for (const t of tabs) {
    el('adminTab' + t.charAt(0).toUpperCase() + t.slice(1)).classList.toggle('hidden', t !== tab);
  }
  document.querySelectorAll('[data-admin-tab]').forEach(btn => {
    btn.classList.toggle('active', btn.getAttribute('data-admin-tab') === tab);
  });

  if (tab === 'dashboard') loadAdminDashboard();
  if (tab === 'settings') loadAdminSettings();
  if (tab === 'logs') loadAdminLogs();
}

async function fetchStatus() {
  const res = await api('status', {}, 'GET');
  if (!res.success) {
    lockApp(true);
    return;
  }
  csrfToken = (res.data && res.data.csrf_token) || '';
  const ctx = res.data && res.data.context;
  setCtx(ctx || null);
  const authed = !!(ctx && ctx.authenticated);
  lockApp(!authed);
  if (authed) {
    switchAppTab('market');
    await loadRegions();
    await loadAdminDashboard();
    await loadAdminSettings();
    await loadAdminLogs();
    await loadItems();
  } else {
    lockMsg('Please authenticate with I-Grid to continue.');
  }
}

async function loadItems(page = 1) {
  currentPage = Math.max(1, Number(page) || 1);
  const payload = {
    q: el('q').value.trim(),
    region: el('region').value.trim(),
    minp: el('minp').value.trim(),
    maxp: el('maxp').value.trim(),
    page: currentPage,
    per_page: 24,
  };
  const res = await api('list', payload, 'GET');
  if (!res.success) {
    msg(res.error || 'Failed to load items');
    if ((res.error || '').toLowerCase().includes('login')) {
      lockApp(true);
    }
    return;
  }
  setCtx(res.data.context || null);
  const items = res.data.items || [];
  totalPages = Math.max(1, Number(res.data.total_pages || 1));
  currentPage = Math.min(currentPage, totalPages);
  el('grid').innerHTML = items.map(cardHtml).join('') || '<div class="muted">No items found</div>';
  el('pageInfo').textContent = `Page ${currentPage} / ${totalPages}`;
  el('prevPageBtn').disabled = currentPage <= 1;
  el('nextPageBtn').disabled = currentPage >= totalPages;
}

el('loginForm').addEventListener('submit', (e) => {
  e.preventDefault();
  startIauthFlow();
});

el('iauthLoginBtn').addEventListener('click', () => {
  startIauthFlow();
});

el('logoutBtn').addEventListener('click', async () => {
  const res = await api('logout_full', {}, 'POST');
  if (!res.success) {
    msg(res.error || 'Logout failed');
    return;
  }
  el('grid').innerHTML = '';
  setCtx(null);
  switchAppTab('market');
  lockApp(true);
  lockMsg('Logged out. Click Authenticate with I-Grid to sign in again.');
  msg('Logged out from MarketApp and I-Auth');
});

el('appTabMarketBtn').addEventListener('click', () => switchAppTab('market'));
el('appTabAdminBtn').addEventListener('click', () => {
  if (!isAdmin) return;
  switchAppTab('admin');
});

el('filterBtn').addEventListener('click', () => loadItems(1));
el('prevPageBtn').addEventListener('click', () => {
  if (currentPage > 1) loadItems(currentPage - 1);
});
el('nextPageBtn').addEventListener('click', () => {
  if (currentPage < totalPages) loadItems(currentPage + 1);
});
el('adminRefreshBtn').addEventListener('click', async () => {
  await loadAdminDashboard();
  await loadAdminSettings();
  await loadAdminLogs();
});

document.querySelectorAll('[data-admin-tab]').forEach(btn => {
  btn.addEventListener('click', () => switchAdminTab(btn.getAttribute('data-admin-tab')));
});

el('adminSettingsForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  const payload = {
    delivery_api_url: el('set_delivery_api_url').value,
    region_overrides: el('set_region_overrides').value,
    texture_proxy_template: el('set_texture_proxy_template').value,
    delivery_api_password: el('set_delivery_api_password').value,
    timeout_seconds: el('set_timeout_seconds').value,
    auth_backup_enabled: el('set_auth_backup_enabled').value,
    auth_backup_password: el('set_auth_backup_password').value,
    auth_backup_allowed_uuid: el('set_auth_backup_allowed_uuid').value,
    admin_uuids: el('set_admin_uuids').value,
  };
  const res = await api('admin_settings_save', payload, 'POST');
  if (!res.success) {
    el('adminSettingsMsg').textContent = 'Save failed: ' + (res.error || 'unknown error');
    return;
  }
  el('adminSettingsMsg').textContent = 'Settings saved.';
  await loadAdminDashboard();
  await loadAdminLogs();
});

el('doritoInitBtn').addEventListener('click', async () => {
  const targetUuid = el('dorito_target_uuid').value.trim();
  const usdAmount = el('dorito_usd_amount').value.trim();
  if (!uuidRe.test(targetUuid)) {
    el('doritoInitMsg').textContent = 'Invalid target UUID format.';
    return;
  }
  if (!usdAmount || Number(usdAmount) <= 0) {
    el('doritoInitMsg').textContent = 'USD amount must be positive.';
    return;
  }

  const res = await api('dorito_init', { target_uuid: targetUuid, usd_amount: usdAmount }, 'POST');
  if (!res.success) {
    el('doritoInitMsg').textContent = res.error || 'Failed to generate Vista token.';
    return;
  }

  const data = res.data || {};
  el('dorito_vista_token').value = data.vista_token || '';
  el('doritoInitMsg').textContent = `Token generated. ${data.security_question || 'Security question required.'} Doritos to grant: ${data.doritos || 0}`;
});

el('doritoGrantBtn').addEventListener('click', async () => {
  const payload = {
    vista_token: el('dorito_vista_token').value.trim(),
    access_key: el('dorito_access_key').value.trim(),
    security_answer: el('dorito_security_answer').value.trim(),
  };

  if (!payload.vista_token || !payload.access_key || !payload.security_answer) {
    el('doritoGrantMsg').textContent = 'Fill Vista token, access key, and security answer.';
    return;
  }

  const res = await api('dorito_grant', payload, 'POST');
  if (!res.success) {
    el('doritoGrantMsg').textContent = res.error || 'Dorito grant failed.';
    return;
  }

  const out = res.data?.result || {};
  el('doritoGrantMsg').textContent = `Dorito$ granted: ${out.granted || 0} to ${out.target_name || out.target_uuid || '-'}. New balance: ${out.new_balance ?? 'n/a'}`;
  el('dorito_security_answer').value = '';
  await loadItems(currentPage);
  await loadAdminLogs();
});

el('grid').addEventListener('click', async (e) => {
  const btn = e.target.closest('[data-buy]');
  if (!btn) return;
  const itemId = Number(btn.getAttribute('data-buy'));
  const recipient = el('recipient_uuid').value.trim();
  if (recipient && !uuidRe.test(recipient)) {
    msg('Recipient UUID format invalid');
    return;
  }

  btn.disabled = true;
  const res = await api('purchase', { item_id: itemId, recipient_uuid: recipient }, 'POST');
  btn.disabled = false;
  if (!res.success) {
    const err = res.error || 'Purchase failed';
    if (String(err).toLowerCase().includes('invalid password')) {
      msg('Purchase failed: delivery API password is invalid in marketapp config.');
    } else {
      msg(err);
    }
    if ((res.error || '').toLowerCase().includes('login')) {
      lockApp(true);
    }
    return;
  }

  msg((res.data.message || 'Purchase done') + ` | Recipient: ${res.data.recipient || res.data.recipient_uuid || '-'}`);
  await loadItems();
});

fetchStatus();
</script>
</body>
</html>
