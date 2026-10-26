using System.Collections;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;

namespace TasiaAddons.ScriptErrors;

/// <summary>Central, authenticated script-error page. Feed writers call Publish; errors are shared through its configured JSONL file.</summary>
public sealed class ScriptErrorsConnector : ServiceConnector
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ScriptErrorsConnector));
    private const string Cookie = "GridScriptErrors";
    private const int MaxMessage = 2048, MaxPage = 100;
    private const int MaxRequestBytes = 8192;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, long> nonces = new();
    private readonly string signingKey;
    private readonly int ratePerMinute;
    private readonly ConcurrentDictionary<string, (long Window, int Count)> rates = new();
    private readonly ConcurrentDictionary<string, (UUID User, string Token, string Csrf, DateTime Expires)> sessions = new();
    private readonly ConcurrentDictionary<string, (long Window, int Count)> loginRates = new();
    private readonly object fileLock = new();
    private readonly string feedFile;
    private readonly IUserAccountService users;
    private readonly IAuthenticationService auth;

    public ScriptErrorsConnector(IConfigSource config, IHttpServer server, string configName) : base(config, server, configName)
    {
        IConfig cfg = config?.Configs["ScriptErrors"];
        if (cfg?.GetBoolean("Enabled", false) != true) return;
        signingKey = cfg.GetString("SigningKey", "");
        if (!string.IsNullOrEmpty(signingKey) && Encoding.UTF8.GetByteCount(signingKey) < 32)
            throw new InvalidOperationException("ScriptErrors.SigningKey must contain at least 32 UTF-8 bytes, or be empty to disable ingestion.");
        ratePerMinute = Math.Clamp(cfg.GetInt("MaxIngestPerMinute", 120), 1, 10000);
        feedFile = Path.GetFullPath(cfg.GetString("FeedFile", "ScriptErrors/errors.jsonl"));
        Directory.CreateDirectory(Path.GetDirectoryName(feedFile));
        users = Load<IUserAccountService>(config, "UserAccountService");
        auth = Load<IAuthenticationService>(config, "AuthenticationService");
        if (users == null || auth == null) throw new InvalidOperationException("ScriptErrors requires grid user and authentication services.");
        server.AddHTTPHandler("/scripterrors", Handle);
        if (!string.IsNullOrEmpty(signingKey)) server.AddHTTPHandler("/scripterrors/ingest", Ingest);
    }

    // Service-to-service endpoint. The HMAC covers the exact UTF-8 request body plus timestamp and nonce.
    // No diagnostic details from rejected requests are returned or logged.
    private Hashtable Ingest(Hashtable request)
    {
        try
        {
            if (!string.Equals(request?["http-method"]?.ToString(), "POST", StringComparison.OrdinalIgnoreCase))
                return Respond(405, "", "Request rejected.");
            string body = Body(request);
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            if (bytes.Length == 0 || bytes.Length > MaxRequestBytes) return Respond(413, "", "Request rejected.");
            Hashtable headers = request?["headers"] as Hashtable ?? new();
            string timestamp = Header(headers, "x-script-errors-timestamp");
            string nonce = Header(headers, "x-script-errors-nonce");
            string signature = Header(headers, "x-script-errors-signature");
            if (!long.TryParse(timestamp, out long unix) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix) > (long)ClockSkew.TotalSeconds
                || nonce.Length is < 16 or > 128 || signature.Length != 64)
                return Respond(401, "", "Request rejected.");
            byte[] expected;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey)))
                expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp + "\n" + nonce + "\n" + body));
            byte[] supplied;
            try { supplied = Convert.FromHexString(signature); } catch { return Respond(401, "", "Request rejected."); }
            if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) return Respond(401, "", "Request rejected.");
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var n in nonces) if (n.Value < now) nonces.TryRemove(n.Key, out _);
            if (nonces.Count > 20000 || !nonces.TryAdd(nonce, now + 180)) return Respond(401, "", "Request rejected.");
            long minute = now / 60;
            var rate = rates.AddOrUpdate("all", _ => (minute, 1), (_, old) => old.Window == minute ? (minute, old.Count + 1) : (minute, 1));
            if (rate.Count > ratePerMinute) return Respond(429, "", "Request rejected.");
            var data = JsonSerializer.Deserialize<IngestRow>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (data == null || string.IsNullOrWhiteSpace(data.Region) || string.IsNullOrWhiteSpace(data.Engine)) return Respond(400, "", "Request rejected.");
            Publish(data.Region, data.Engine, data.Script ?? "", data.Message ?? "");
            return new Hashtable { ["int_response_code"] = 202, ["content_type"] = "text/plain", ["str_response_string"] = "accepted" };
        }
        catch { return Respond(400, "", "Request rejected."); }
    }

    private sealed record IngestRow(string Region, string Engine, string Script, string Message);

    private static T Load<T>(IConfigSource c, string section) where T : class
    {
        string module = c?.Configs[section]?.GetString("LocalServiceModule", "");
        return string.IsNullOrWhiteSpace(module) ? null : ServerUtils.LoadPlugin<T>(module, new object[] { c });
    }

    /// <summary>Collector API: persist one sanitized error. Message is bounded; source text is never accepted or stored.</summary>
    public void Publish(string region, string engine, string script, string message)
    {
        var row = new ErrorRow(DateTimeOffset.UtcNow, Clean(region, 128), Clean(engine, 32), Clean(script, 128), Clean(message, MaxMessage));
        lock (fileLock)
        {
            File.AppendAllText(feedFile, System.Text.Json.JsonSerializer.Serialize(row) + Environment.NewLine, Encoding.UTF8);
            TrimFeed();
        }
    }

    private Hashtable Handle(Hashtable request)
    {
        Hashtable headers = request?["headers"] as Hashtable ?? new Hashtable();
        DateTime utcNow = DateTime.UtcNow;
        foreach (var entry in sessions)
        {
            if (entry.Value.Expires <= utcNow && sessions.TryRemove(entry.Key, out var expired))
            {
                try { auth.Release(expired.User, expired.Token); } catch { }
            }
        }
        string method = request?["http-method"]?.ToString().ToUpperInvariant() ?? string.Empty;
        string cookie = Header(headers, "cookie");
        string sid = cookie.Split(';').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith(Cookie + "=", StringComparison.Ordinal))?.Substring(Cookie.Length + 1);
        sessions.TryGetValue(sid ?? "", out var session);
        bool valid = session.Expires > utcNow && auth.Verify(session.User, session.Token, 1800);
        if (method == "POST")
        {
            var form = ParseForm(Body(request));
            if (form.TryGetValue("logout", out _)) { if (valid) auth.Release(session.User, session.Token); sessions.TryRemove(sid ?? "", out _); return Redirect("/scripterrors"); }
            if (valid)
            {
                if (!form.TryGetValue("csrf", out string csrf) || !Fixed(csrf, session.Csrf)) return Respond(403, "forbidden", "Request rejected.");
                return Html(Feed(form.TryGetValue("q", out string query) ? query : "", ParsePage(form)));
            }
            string remote = Header(headers, "remote_addr");
            if (remote.Length > 64) remote = "unknown";
            long minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            var rate = loginRates.AddOrUpdate(remote, _ => (minute, 1), (_, old) => old.Window == minute ? (minute, old.Count + 1) : (minute, 1));
            if (rate.Count > 10) return Html(Login("Too many login attempts. Try again shortly."), 429);
            string identity = form.GetValueOrDefault("username", "").Trim();
            string first = form.GetValueOrDefault("first", "").Trim();
            string last = form.GetValueOrDefault("last", "").Trim();
            string password = form.GetValueOrDefault("password", "");
            UserAccount account = FindLoginAccount(identity, first, last);
            if (account == null)
            {
                Log.Warn("[ScriptErrors]: login rejected: account lookup failed (identity and password omitted).");
                return Html(Login("Login failed."), 401);
            }
            if (string.IsNullOrEmpty(password))
            {
                Log.Warn("[ScriptErrors]: login rejected: empty password (identity and password omitted).");
                return Html(Login("Login failed."), 401);
            }
            // OpenSim stores MD5(MD5(password) + ":" + salt), while
            // PasswordAuthenticationService.Authenticate expects MD5(password).
            string token = auth.Authenticate(account.PrincipalID, Util.Md5Hash(password), 1800);
            if (string.IsNullOrEmpty(token))
            {
                Log.Warn("[ScriptErrors]: login rejected: grid authentication denied credentials (identity and password omitted).");
                return Html(Login("Login failed."), 401);
            }
            string newId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            string csrfValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            sessions[newId] = (account.PrincipalID, token, csrfValue, DateTime.UtcNow.AddMinutes(30));
            var response = Html(Feed("", 0, csrfValue));
            var responseHeaders = (Hashtable)response["headers"];
            responseHeaders["Set-Cookie"] = Cookie + "=" + newId + "; Path=/scripterrors; HttpOnly; SameSite=Strict; Max-Age=1800";
            return response;
        }
        return valid ? Html(Feed(Query(request), Page(request), session.Csrf)) : Html(Login(""));
    }

    private string Feed(string q, int page, string csrf = "")
    {
        List<ErrorRow> rows;
        lock (fileLock) rows = File.Exists(feedFile) ? File.ReadLines(feedFile).TakeLast(5000).Select(line => { try { return System.Text.Json.JsonSerializer.Deserialize<ErrorRow>(line); } catch { return null; } }).Where(x => x != null).ToList() : new();
        if (!string.IsNullOrWhiteSpace(q)) rows = rows.Where(x => (x.Region + " " + x.Engine + " " + x.Script + " " + x.Message).Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        rows.Reverse(); int start = Math.Clamp(page, 0, 100000) * 50;
        var b = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Grid script errors</title>")
            .Append(PageStyle)
            .Append("</head><body><main class=\"shell\"><header class=\"topbar\"><div><p class=\"eyebrow\">GRID OPERATIONS</p><h1>Script errors</h1><p class=\"subtle\">Recent runtime errors from across the grid</p></div><form method=post action=\"/scripterrors\"><input type=hidden name=csrf value=\"").Append(E(csrf)).Append("\"><button class=\"quiet\" name=logout value=1>Log out</button></form></header><section class=\"panel\"><form class=\"search\" method=get action=\"/scripterrors\"><label for=\"q\">Search errors</label><div class=\"searchrow\"><input id=\"q\" name=q value=\"").Append(E(q)).Append("\" placeholder=\"Region, engine, script, or message\"><button>Search</button></div></form><p class=\"subtle\">Showing up to 50 of the latest 5,000 retained events.</p><div class=\"tablewrap\"><table><thead><tr><th>Time (UTC)</th><th>Region</th><th>Engine</th><th>Script</th><th>Error</th></tr></thead><tbody>");
        var pageRows = rows.Skip(start).Take(50).ToList();
        if (pageRows.Count == 0) b.Append("<tr><td colspan=\"5\" class=\"empty\">No matching script errors.</td></tr>");
        foreach (var x in pageRows) b.Append("<tr><td class=\"time\">").Append(E(x.Time.ToString("u"))).Append("</td><td>").Append(E(x.Region)).Append("</td><td><span class=\"badge\">").Append(E(x.Engine)).Append("</span></td><td class=\"script\">").Append(E(x.Script)).Append("</td><td class=\"message\">").Append(E(x.Message)).Append("</td></tr>");
        b.Append("</tbody></table></div><nav class=\"pagination\"><a href=\"?q=").Append(Uri.EscapeDataString(q)).Append("&page=").Append(Math.Max(0, page - 1)).Append("\">← Previous</a><a href=\"?q=").Append(Uri.EscapeDataString(q)).Append("&page=").Append(page + 1).Append("\">Next →</a></nav></section><footer>Visible to authenticated grid accounts · Error details only; no script source or stack traces</footer></main></body></html>"); return b.ToString();
    }
    private UserAccount FindLoginAccount(string identity, string first, string last)
    {
        if (!string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(last))
            return FindAccountByName(first, last);
        if (string.IsNullOrWhiteSpace(identity)) return null;
        if (identity.Contains('@')) return users.GetUserAccount(UUID.Zero, identity);
        string[] name = identity.Contains('.')
            ? identity.Split('.', 2, StringSplitOptions.TrimEntries)
            : identity.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (name.Length == 2) return FindAccountByName(name[0], name[1]);
        return FindAccountByName(identity, "Resident");
    }

    private UserAccount FindAccountByName(string first, string last)
    {
        UserAccount account = users.GetUserAccount(UUID.Zero, first, last);
        if (account != null) return account;
        // PostgreSQL's account lookup is case-sensitive; viewer names are commonly normalized to lowercase.
        return users.GetUserAccounts(UUID.Zero, first + " " + last)?.FirstOrDefault(user =>
            string.Equals(user.FirstName, first, StringComparison.OrdinalIgnoreCase)
            && string.Equals(user.LastName, last, StringComparison.OrdinalIgnoreCase));
    }

    private static string Login(string error) => "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Script errors · Sign in</title>" + PageStyle + "</head><body><main class=\"login-shell\"><section class=\"login-card\"><p class=\"eyebrow\">GRID OPERATIONS</p><h1>Script errors</h1><p class=\"subtle\">Sign in with your grid account to view runtime errors across all regions.</p>" + (string.IsNullOrEmpty(error) ? "" : "<p class=\"alert\" role=\"alert\">" + E(error) + "</p>") + "<form method=post action=\"/scripterrors\"><label for=\"username\">Grid login</label><input id=\"username\" name=username autocomplete=username placeholder=\"First Last, First.Last, or email\" required><label for=\"password\">Password</label><input id=\"password\" name=password type=password autocomplete=current-password required><button class=\"login-button\">Sign in</button></form><p class=\"hint\">Your grid password is checked by the grid authentication service and is not stored.</p></section></main></body></html>";
    private const string PageStyle = "<style> :root{color-scheme:dark;font-family:Inter,Segoe UI,Arial,sans-serif;background:#0b1020;color:#e6edf7}*{box-sizing:border-box}body{margin:0;min-height:100vh;background:radial-gradient(ellipse at top,#18233b 0,#0b1020 58%);color:#e6edf7;font-size:16px;line-height:1.5}.shell{width:min(1440px,calc(100% - 40px));margin:42px auto}.login-shell{min-height:100vh;display:grid;place-items:center;padding:24px}.login-card,.panel{background:#121a2b;border:1px solid #29354a;border-radius:16px;box-shadow:0 18px 54px #0006}.login-card{width:min(460px,100%);padding:36px}.topbar{display:flex;align-items:center;justify-content:space-between;gap:24px;margin-bottom:26px}.topbar h1,.login-card h1{font-size:clamp(28px,4vw,38px);line-height:1.15;margin:0 0 8px;color:#f4f7fc}.eyebrow{font-size:12px;font-weight:700;letter-spacing:.16em;color:#93a9ca;margin:0 0 9px}.subtle,.hint,footer{color:#aab8ce}.subtle{margin:0 0 20px}.login-card .subtle{margin-bottom:26px}.panel{padding:24px}.search{max-width:680px}.search label,.login-card label{display:block;font-weight:650;margin:16px 0 8px;color:#dce6f5}.searchrow{display:flex;gap:10px}.search input,.login-card input{width:100%;min-width:0;background:#0b1220;border:1px solid #3a4963;border-radius:9px;color:#f1f5fb;padding:12px 14px;font:inherit}.login-card input{margin-bottom:10px}.search input:focus,.login-card input:focus{outline:3px solid #5b9dff55;border-color:#69a8ff}button{border:0;border-radius:9px;background:#2d73d5;color:white;font:inherit;font-weight:700;padding:11px 18px;cursor:pointer;white-space:nowrap}button:hover{background:#3b83e8}button:focus-visible,a:focus-visible{outline:3px solid #9cc4ff;outline-offset:3px}.quiet{background:#202c40;border:1px solid #3b4b64}.quiet:hover{background:#2a3a52}.login-button{width:100%;margin-top:14px;padding:13px}.hint{font-size:13px;margin:20px 0 0}.alert{background:#3a1c27;border:1px solid #8d3b50;color:#ffd6de;border-radius:9px;padding:11px 13px}.tablewrap{overflow:auto;border:1px solid #29354a;border-radius:10px}table{width:100%;border-collapse:collapse;min-width:860px}th,td{text-align:left;padding:13px 14px;border-bottom:1px solid #263247;vertical-align:top}th{background:#192439;color:#c6d3e6;font-size:13px;letter-spacing:.04em}td{color:#e1e8f2}tr:last-child td{border-bottom:0}tbody tr:hover{background:#172237}.time,.script{white-space:nowrap}.message{min-width:280px;overflow-wrap:anywhere}.badge{display:inline-block;border:1px solid #384b69;border-radius:999px;padding:2px 9px;color:#b8d2fa;background:#1a2b45;font-size:13px}.empty{padding:28px;color:#9fb0c8;text-align:center}.pagination{display:flex;justify-content:space-between;margin-top:18px}.pagination a{color:#91bdff;text-decoration:none;padding:8px}.pagination a:hover{text-decoration:underline}footer{text-align:center;margin:18px 0;font-size:13px}@media(max-width:650px){.shell{width:calc(100% - 24px);margin:20px auto}.topbar{align-items:flex-start}.panel{padding:16px}.login-card{padding:26px}.searchrow{flex-direction:column}.searchrow button{width:100%}} </style>";
    private static Hashtable Html(string html, int status = 200) => new() { ["int_response_code"] = status, ["content_type"] = "text/html; charset=utf-8", ["str_response_string"] = html, ["headers"] = new Hashtable { ["Cache-Control"] = "no-store", ["Content-Security-Policy"] = "default-src 'none'; form-action 'self'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'", ["X-Content-Type-Options"] = "nosniff" } };
    private static Hashtable Redirect(string target) => new() { ["int_response_code"] = 303, ["content_type"] = "text/plain", ["str_response_string"] = "", ["headers"] = new Hashtable { ["Location"] = target } };
    private static Hashtable Respond(int status, string _, string text) => Html(E(text), status);
    private static string E(string x) => System.Net.WebUtility.HtmlEncode(x ?? "");
    private static string Clean(string x, int max) => new((x ?? "").Where(c => !char.IsControl(c) || c == '\t').Take(max).ToArray());
    private static bool Fixed(string a, string b) { try { return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b)); } catch { return false; } }
    private static string Body(Hashtable r) => r?["body"] as string ?? r?["requestbody"] as string ?? "";
    private static string Header(Hashtable h, string key) { foreach (DictionaryEntry e in h) if (string.Equals(e.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase)) return e.Value?.ToString() ?? ""; return ""; }
    private static string Query(Hashtable r) => ParseForm(r?["querystring"]?.ToString() ?? "").GetValueOrDefault("q", "");
    private static int Page(Hashtable r) => int.TryParse(ParseForm(r?["querystring"]?.ToString() ?? "").GetValueOrDefault("page"), out int n) ? Math.Clamp(n, 0, 100000) : 0;
    private static int ParsePage(Dictionary<string,string> x) => int.TryParse(x.GetValueOrDefault("page"), out int n) ? Math.Clamp(n, 0, 100000) : 0;
    private static Dictionary<string,string> ParseForm(string s) => s.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).Where(x => x.Length == 2).GroupBy(x => Uri.UnescapeDataString(x[0].Replace('+',' '))).ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.Last()[1].Replace('+',' ')));
    private void TrimFeed() { var lines = File.ReadLines(feedFile).TakeLast(5000).ToArray(); File.WriteAllLines(feedFile, lines, Encoding.UTF8); }
    private sealed record ErrorRow(DateTimeOffset Time, string Region, string Engine, string Script, string Message);
}
