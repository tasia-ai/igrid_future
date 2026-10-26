#!/usr/bin/env python3
"""
ToS Acceptance Server
Serves the ToS page from a markdown file and records acceptances in SQLite.

Usage:
  python3 tos_server.py [--port 8099] [--db Data/accesscontrol.db] [--file bin/tos.md]

The ToS URL in your config should point to this server, e.g.:
  ToSUrl = http://your-server:8099/?user=<UUID>
"""

import argparse
import json
import sqlite3
import os
import re
import hashlib
from http.server import HTTPServer, BaseHTTPRequestHandler
from urllib.parse import parse_qs, urlparse

DB_PATH = "Data/accesscontrol.db"
TOS_VERSION = 1
TOS_FILE = "tos.md"  # markdown file


def md_to_html(text):
    """Very simple markdown to HTML converter — no dependencies."""
    lines = text.split("\n")
    html_lines = []
    in_list = False

    for line in lines:
        stripped = line.strip()

        # Headers
        if stripped.startswith("### "):
            if in_list:
                html_lines.append("</ul>")
                in_list = False
            html_lines.append(f"<h3>{stripped[4:]}</h3>")
        elif stripped.startswith("## "):
            if in_list:
                html_lines.append("</ul>")
                in_list = False
            html_lines.append(f"<h2>{stripped[3:]}</h2>")
        elif stripped.startswith("# "):
            if in_list:
                html_lines.append("</ul>")
                in_list = False
            html_lines.append(f"<h1>{stripped[2:]}</h1>")
        # Horizontal rule
        elif stripped in ("---", "***", "___"):
            if in_list:
                html_lines.append("</ul>")
                in_list = False
            html_lines.append("<hr>")
        # List items
        elif stripped.startswith("- ") or stripped.startswith("* "):
            if not in_list:
                html_lines.append("<ul>")
                in_list = True
            item = stripped[2:]
            # Bold
            item = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", item)
            # Italic
            item = re.sub(r"\*(.+?)\*", r"<em>\1</em>", item)
            # Inline code
            item = re.sub(r"`(.+?)`", r"<code>\1</code>", item)
            html_lines.append(f"  <li>{item}</li>")
        # Ordered list
        elif re.match(r"^\d+\.\s", stripped):
            content = re.sub(r"^\d+\.\s", "", stripped)
            if not in_list:
                html_lines.append("<ol>")
                in_list = True
            content = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", content)
            content = re.sub(r"\*(.+?)\*", r"<em>\1</em>", content)
            content = re.sub(r"`(.+?)`", r"<code>\1</code>", content)
            html_lines.append(f"  <li>{content}</li>")
        # Empty line
        elif stripped == "":
            if in_list:
                html_lines.append("</ul>")
                in_list = False
            html_lines.append("")
        # Paragraph text
        else:
            if in_list:
                html_lines.append("</ul>")
                in_list = False
            # Bold
            stripped = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", stripped)
            # Italic
            stripped = re.sub(r"\*(.+?)\*", r"<em>\1</em>", stripped)
            # Inline code
            stripped = re.sub(r"`(.+?)`", r"<code>\1</code>", stripped)
            html_lines.append(f"<p>{stripped}</p>")

    if in_list:
        html_lines.append("</ul>")

    return "\n".join(html_lines)


def render_page(user_id, tos_version, tos_html):
    """Wrap ToS HTML content in a full page."""
    return f"""<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Terms of Service</title>
    <style>
        * {{ box-sizing: border-box; }}
        html, body {{ width: 100%; height: 100%; margin: 0; overflow: hidden; }}
        body {{ display: flex; flex-direction: column; font: 15px/1.5 Arial, sans-serif; color: #111; background: #fff; }}
        header {{ flex: 0 0 auto; padding: 8px 14px; border-bottom: 1px solid #bbb; }}
        h1 {{ display: inline; margin: 0; font-size: 18px; font-weight: 600; }}
        .version-tag {{ margin-left: 8px; color: #555; font-size: 12px; }}
        .tos-content {{ flex: 1 1 auto; min-height: 0; overflow-y: scroll; overscroll-behavior: contain; padding: 12px 16px; line-height: 1.45; background: #fff; color: #111; }}
        .tos-content h2, .tos-content h3 {{ margin: 1em 0 .4em; color: #111; font-size: 1em; }}
        .tos-content p {{ margin: .45em 0; }}
        .tos-content ul, .tos-content ol {{ margin: .4em 0 .6em 1.5em; padding: 0; }}
        .tos-content li {{ margin: .2em 0; }}
        .tos-content hr {{ border: 0; border-top: 1px solid #aaa; }}
        .accept-bar {{ flex: 0 0 auto; display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 10px 14px; border-top: 1px solid #aaa; background: #f3f3f3; }}
        .accept-label {{ display: flex; align-items: center; gap: 8px; }}
        .accept-label input {{ width: 18px; height: 18px; margin: 0; }}
        .btn {{ flex: 0 0 auto; min-width: 100px; padding: 9px 18px; border: 1px solid #555; border-radius: 2px; color: #fff; background: #333; cursor: pointer; font-size: 15px; }}
        .btn:hover {{ background: #111; }}
        .btn:disabled {{ color: #555; background: #ddd; cursor: not-allowed; }}
        #result {{ flex: 0 0 auto; padding: 0 14px; }}
        .success {{ color: #176b2c; }}
        .error {{ color: #a00000; }}
        @media (max-width: 480px) {{ .accept-bar {{ align-items: flex-start; }} .accept-label {{ font-size: 13px; }} .btn {{ min-width: 82px; padding: 8px 12px; }} }}
    </style>
</head>
<body>
    <header><h1>Terms of Service</h1><span class="version-tag">Version {tos_version}</span></header>
    <main class="tos-content" tabindex="0" aria-label="Terms of Service">
        {tos_html}
    </main>
    <div id="result" role="status" aria-live="polite"></div>
    <footer class="accept-bar">
        <label class="accept-label">
            <input type="checkbox" id="agreeCheck" onchange="document.getElementById('acceptBtn').disabled = !this.checked">
            <span>I have read and agree to the Terms of Service (v{tos_version})</span>
        </label>
        <button class="btn" id="acceptBtn" disabled onclick="acceptToS()">Accept</button>
    </footer>

    <script>
        const userId = "{user_id}";
        const tosVersion = {tos_version};

        if (!userId) {{
            document.getElementById('result').innerHTML = '<p class="error">Missing user ID. Access this page from your viewer login.</p>';
            document.getElementById('acceptBtn').disabled = true;
        }}

        async function acceptToS() {{
            const btn = document.getElementById('acceptBtn');
            btn.disabled = true;
            btn.textContent = 'Processing...';

            try {{
                const response = await fetch('/tos/accept', {{
                    method: 'POST',
                    headers: {{ 'Content-Type': 'application/json' }},
                    body: JSON.stringify({{ user_id: userId, tos_version: tosVersion }})
                }});

                const data = await response.json();

                if (data.success) {{
                    document.getElementById('result').innerHTML =
                        '<p class="success">Thank you! You have accepted the Terms of Service (v' + tosVersion + ').</p>' +
                        '<p>You can now close this page and log in to the grid.</p>';
                }} else {{
                    document.getElementById('result').innerHTML = '<p class="error">Error: ' + (data.error || 'Unknown error') + '</p>';
                    btn.disabled = false;
                    btn.textContent = 'I Agree';
                }}
            }} catch (err) {{
                document.getElementById('result').innerHTML = '<p class="error">Connection error: ' + err.message + '</p>';
                btn.disabled = false;
                btn.textContent = 'I Agree';
            }}
        }}
    </script>
</body>
</html>"""


class ToSHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        parsed = urlparse(self.path)
        params = parse_qs(parsed.query)

        if parsed.path == "/" or parsed.path == "/tos":
            user_id = params.get("user", [""])[0]
            version = params.get("version", [str(TOS_VERSION)])[0]

            # Load markdown file fresh on each request (allows live editing)
            tos_md = load_tos_markdown()
            tos_html = md_to_html(tos_md)
            page = render_page(user_id, TOS_VERSION, tos_html)

            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.end_headers()
            self.wfile.write(page.encode("utf-8"))

        elif parsed.path == "/tos/raw":
            # Serve raw markdown
            tos_md = load_tos_markdown()
            self.send_response(200)
            self.send_header("Content-Type", "text/markdown; charset=utf-8")
            self.end_headers()
            self.wfile.write(tos_md.encode("utf-8"))

        elif parsed.path == "/tos/check":
            user_id = params.get("user", [""])[0]
            if not user_id:
                self.send_json(400, {"error": "Missing user parameter"})
                return

            db = get_db()
            row = db.execute(
                "SELECT tos_version FROM tos_acceptances WHERE user_id = ?",
                (user_id,)
            ).fetchone()
            db.close()

            accepted = row is not None and row[0] >= TOS_VERSION
            self.send_json(200, {
                "accepted": accepted,
                "user_version": row[0] if row else 0,
                "current_version": TOS_VERSION
            })

        else:
            self.send_response(404)
            self.end_headers()

    def do_POST(self):
        parsed = urlparse(self.path)

        if parsed.path == "/tos/accept":
            content_length = int(self.headers.get("Content-Length", 0))
            body = self.rfile.read(content_length)
            try:
                data = json.loads(body)
            except json.JSONDecodeError:
                self.send_json(400, {"error": "Invalid JSON"})
                return

            user_id = data.get("user_id", "")
            tos_version = data.get("tos_version", TOS_VERSION)

            if not user_id:
                self.send_json(400, {"error": "Missing user_id"})
                return

            try:
                db = get_db()
                db.execute("""
                    INSERT INTO tos_acceptances (user_id, tos_version, accepted_at)
                    VALUES (?, ?, datetime('now'))
                    ON CONFLICT(user_id) DO UPDATE SET tos_version = ?, accepted_at = datetime('now')
                """, (user_id, tos_version, tos_version))
                db.commit()
                db.close()
                self.send_json(200, {"success": True})
            except Exception as e:
                self.send_json(500, {"error": str(e)})
        else:
            self.send_response(404)
            self.end_headers()

    def send_json(self, code, data):
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(json.dumps(data).encode())

    def log_message(self, format, *args):
        print(f"[ToS] {args[0]}")


def load_tos_markdown():
    """Load ToS markdown file, reload on each request for live editing."""
    try:
        with open(TOS_FILE, "r", encoding="utf-8") as f:
            return f.read()
    except FileNotFoundError:
        return f"# Terms of Service\n\nToS file not found: `{TOS_FILE}`\n\nPlease create this file with your Terms of Service content in markdown format."


def get_db():
    db = sqlite3.connect(DB_PATH)
    db.execute("""
        CREATE TABLE IF NOT EXISTS tos_acceptances (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            user_id TEXT NOT NULL,
            tos_version INTEGER NOT NULL,
            accepted_at TEXT DEFAULT (datetime('now')),
            UNIQUE(user_id)
        )
    """)
    return db


def main():
    global DB_PATH, TOS_VERSION, TOS_FILE

    parser = argparse.ArgumentParser(description="ToS Acceptance Server")
    parser.add_argument("--port", type=int, default=8099, help="Port to listen on")
    parser.add_argument("--db", default=DB_PATH, help="SQLite database path")
    parser.add_argument("--version", type=int, default=TOS_VERSION, help="Current ToS version")
    parser.add_argument("--file", default=TOS_FILE, help="Path to ToS markdown file (default: tos.md)")
    args = parser.parse_args()

    DB_PATH = args.db
    TOS_VERSION = args.version
    TOS_FILE = args.file

    # Ensure DB table exists
    db = get_db()
    db.close()

    # Check if markdown file exists
    if not os.path.exists(TOS_FILE):
        print(f"[ToS] WARNING: ToS file not found: {TOS_FILE}")
        print(f"[ToS] Create the file with your Terms of Service in markdown format.")

    server = HTTPServer(("0.0.0.0", args.port), ToSHandler)
    print(f"[ToS] Server running on port {args.port}")
    print(f"[ToS] Database: {DB_PATH}")
    print(f"[ToS] ToS Version: {TOS_VERSION}")
    print(f"[ToS] ToS File: {TOS_FILE}")
    print(f"[ToS] ToS URL: http://your-server:{args.port}/?user=<UUID>")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\n[ToS] Server stopped")


if __name__ == "__main__":
    main()
