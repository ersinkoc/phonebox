#!/usr/bin/env python3
"""
PhoneBox demo server: serves the web demo (tools/pages) straight from the sources, so the real panel and settings page
run in a desktop browser with made-up data; no phone, agent or Windows needed. Handy for working on the UI and for
screenshots. The made-up data lives in tools/pages/demo.js (the same as on the live demo); this server adds the
webhook API, so curl can push values, notifications and page switches into the open browser tabs.

    python tools/demo-server.py [port]     # http://127.0.0.1:8790/          the landing page with the panel in a phone
                                           # http://127.0.0.1:8790/panel/    the panel (size the window to 780 x 360)
                                           # http://127.0.0.1:8790/settings/ the settings page and layout editor
    curl -X POST http://127.0.0.1:8790/api/notify -d hello
    curl -X POST http://127.0.0.1:8790/api/push/temperature -d 21.5
    curl -X POST http://127.0.0.1:8790/api/goto/music
    curl -X POST http://127.0.0.1:8790/api/ask                     # a made-up Claude Code permission prompt

Only the Python standard library is used. Layout changes are kept in the browser (as on the live demo).
"""
import importlib.util, json, sys, threading, time
sys.dont_write_bytecode = True  # importing tools/pages/build.py must not leave a __pycache__ in the repo
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import unquote, urlparse

ROOT = Path(__file__).resolve().parent.parent
PAGES = ROOT / 'tools' / 'pages'
_spec = importlib.util.spec_from_file_location('pages_build', PAGES / 'build.py')
site = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(site)
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8790

# webhooks received by this server, relayed to the open tabs over /demo/events (server-sent events)
EVENTS, LOCK = [], threading.Condition()


def relay(kind, data):
    with LOCK:
        seq = (EVENTS[-1]['seq'] if EVENTS else 0) + 1
        EVENTS.append({'seq': seq, 'kind': kind, 'data': data})
        del EVENTS[:-100]
        LOCK.notify_all()
    return seq


def parse(body):
    raw = (body or b'').decode('utf-8').strip()
    try: return json.loads(raw) if raw else None
    except ValueError: return raw


def webhook(method, path, body):
    """The agent's webhook endpoints. Returns a JSON-able reply, or None when the path is not one of them."""
    if method not in ('POST', 'PUT'): return None
    if path.startswith('/api/push/'): return {'ok': True, 'seq': relay('push', {'key': unquote(path[10:]), 'value': parse(body)})}
    if path == '/api/notify':
        d = parse(body)
        return {'ok': True, 'seq': relay('notify', d if isinstance(d, dict) else {'text': '' if d is None else str(d)})}
    if path.startswith('/api/goto/'): return {'ok': True, 'seq': relay('goto', {'page': unquote(path[10:])})}
    if path == '/api/refresh': return {'ok': True, 'seq': relay('refresh', None)}
    if path == '/api/ask': return {'ok': True, 'seq': relay('ask', parse(body) if isinstance(parse(body), dict) else None)}
    return None


TYPES = {'.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml',
         '.png': 'image/png', '.ico': 'image/x-icon', '.woff2': 'font/woff2'}


def static(path):
    """Maps a URL path to (content type, bytes) like tools/pages/build.py lays out the site, or None."""
    if path in ('/', '/index.html'): return 'text/html', site.landing_html().encode()
    if path == '/demo.js': return 'text/javascript', (PAGES / 'demo.js').read_bytes()
    if path in ('/widgets.json', '/templates.json'): return 'application/json', (site.AGENT / path[1:]).read_bytes()
    if path == '/favicon.ico': return 'image/x-icon', (site.AGENT / 'phonebox.ico').read_bytes()
    if path in ('/panel/', '/panel/index.html'): return 'text/html', site.panel_html(relay=True).encode()
    if path in ('/settings/', '/settings/index.html'): return 'text/html', site.settings_html(relay=True).encode()
    if path.startswith('/panel/'):
        f = (site.WWW / path[7:]).resolve()
        if site.WWW in f.parents and f.is_file(): return TYPES.get(f.suffix, 'application/octet-stream'), f.read_bytes()
    return None


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def send(self, code, ctype, data, extra=None):
        self.send_response(code)
        self.send_header('Content-Type', ctype + ('; charset=utf-8' if ctype.startswith('text') or ctype.endswith('json') else ''))
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        for k, v in (extra or {}).items(): self.send_header(k, v)
        self.end_headers()
        self.wfile.write(data)

    def events(self):
        last = int(self.headers.get('Last-Event-ID') or 0)
        with LOCK: last = last or (EVENTS[-1]['seq'] if EVENTS else 0)  # a new tab starts from now
        self.send_response(200)
        self.send_header('Content-Type', 'text/event-stream')
        self.send_header('Cache-Control', 'no-store')
        self.end_headers()
        try:
            while True:
                with LOCK:
                    LOCK.wait_for(lambda: EVENTS and EVENTS[-1]['seq'] > last, timeout=15)
                    new = [e for e in EVENTS if e['seq'] > last]
                for e in new:
                    self.wfile.write(f"id: {e['seq']}\nevent: {e['kind']}\ndata: {json.dumps(e['data'], ensure_ascii=False)}\n\n".encode())
                    last = e['seq']
                if not new: self.wfile.write(b': keep-alive\n\n')
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass

    def handle_any(self):
        u = urlparse(self.path)
        path = u.path
        # A body this server does not read stays on the connection and would be taken for the next request: refuse it
        # and close the connection (chunked bodies are not decoded; send the body with a Content-Length).
        if self.headers.get('Transfer-Encoding') is not None:
            return self.send(411, 'application/json', b'{"error":"send the body with a Content-Length"}', {'Connection': 'close'})
        body = self.rfile.read(int(self.headers.get('Content-Length') or 0)) or None
        if path == '/demo/events': return self.events()
        if path in ('/panel', '/settings'): return self.send(301, 'text/plain', b'', {'Location': path + '/'})
        reply = webhook(self.command, path[len('/agent'):] if path.startswith('/agent/api/') else path, body)
        if reply is not None: return self.send(200, 'application/json', json.dumps(reply).encode())
        if path.startswith('/api/'):
            return self.send(404, 'application/json', b'{"error":"the demo answers this in the browser (tools/pages/demo.js)"}')
        hit = static(path)
        if hit: return self.send(200, *hit)
        self.send(404, 'text/plain', b'not found')

    do_GET = do_POST = do_PUT = do_DELETE = handle_any

    def log_message(self, *a):
        pass


if __name__ == '__main__':
    ThreadingHTTPServer.daemon_threads = True
    srv = ThreadingHTTPServer(('127.0.0.1', PORT), Handler)
    print(f'PhoneBox demo: http://127.0.0.1:{PORT}/  ·  panel /panel/  ·  settings /settings/  ·  webhooks: curl -X POST http://127.0.0.1:{PORT}/api/notify -d hello')
    try: srv.serve_forever()
    except KeyboardInterrupt: pass
