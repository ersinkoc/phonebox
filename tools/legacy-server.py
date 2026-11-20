#!/usr/bin/env python3
"""Opt-in, read-only bridge for old browsers; the normal agent stays loopback-only."""
import argparse
import hmac
import json
import secrets
import threading
import time
import urllib.request
from http.cookies import SimpleCookie
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

ASSETS = Path(__file__).resolve().parents[1] / 'android/app/src/main/assets/www/legacy'


def public_payload(path, value):
    # Start with the agent's ToPublic response, never its configuration file.
    if path == '/api/config':
        cfg = value.get('config', {})
        return {'version': value.get('version'), 'config': {
            'pages': cfg.get('pages', []), 'display': {'showSeconds': cfg.get('display', {}).get('showSeconds', True)}}}
    if path == '/api/state':
        return {k: value.get(k) for k in ('ts', 'pc', 'media', 'audio', 'ping')}
    raise ValueError('Unsupported endpoint')


class LegacyServer(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, address, pin, agent='http://127.0.0.1:8765'):
        super().__init__(address, Handler)
        self.pin, self.agent = pin, agent
        self.sessions, self.attempts = {}, {}
        self.guard = threading.Lock()
        # No environment HTTP proxy can receive the local agent's response.
        self.client = urllib.request.build_opener(urllib.request.ProxyHandler({}))


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass  # Pairing codes, paths and local data do not go into logs.

    def send(self, status, data, kind='application/json', cookie=None):
        if not isinstance(data, bytes):
            data = json.dumps(data, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header('Content-Type', kind)
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Content-Type-Options', 'nosniff')
        self.send_header('X-Frame-Options', 'DENY')
        self.send_header('Referrer-Policy', 'no-referrer')
        if cookie:
            self.send_header('Set-Cookie', cookie)
        self.end_headers()
        self.wfile.write(data)

    def authenticated(self):
        try:
            cookies = SimpleCookie(self.headers.get('Cookie', ''))
            item = cookies.get('phonebox_legacy')
            token = item.value if item else ''
            with self.server.guard:
                now = time.time()
                self.server.sessions = {k: v for k, v in self.server.sessions.items() if v > now}
                return self.server.sessions.get(token, 0) > now
        except Exception:
            return False

    def do_POST(self):
        if self.path != '/pair':
            self.send(405, {'error': 'Read-only bridge'})
            return
        origin = self.headers.get('Origin')
        if origin and origin != 'http://' + self.headers.get('Host', ''):
            self.send(403, {'error': 'Origin refused'})
            return
        try:
            length = int(self.headers.get('Content-Length', '0'))
        except ValueError:
            length = 0
        if not 0 < length <= 128:
            self.send(400, {'error': 'Invalid code'})
            return
        code = parse_qs(self.rfile.read(length).decode('ascii', errors='ignore')).get('pin', [''])[0]
        now, ip = time.time(), self.client_address[0]
        with self.server.guard:
            self.server.attempts = {k: v for k, v in self.server.attempts.items() if now - v[0] < 60}
            start, count = self.server.attempts.get(ip, (now, 0))
            if count >= 5:
                self.send(429, {'error': 'Wait one minute'})
                return
            self.server.attempts[ip] = (start, count + 1)
            if not hmac.compare_digest(code, self.server.pin):
                self.send(403, {'error': 'Incorrect code'})
                return
            token = secrets.token_hex(32)
            self.server.sessions[token] = now + 86400
        self.send(200, {'ok': True}, cookie='phonebox_legacy=' + token + '; Path=/; HttpOnly; SameSite=Strict; Max-Age=86400')

    def do_GET(self):
        path = urlsplit(self.path).path
        assets = {'/': ('index.html', 'text/html; charset=utf-8'),
                  '/legacy.js': ('legacy.js', 'text/javascript; charset=utf-8'),
                  '/legacy.css': ('legacy.css', 'text/css; charset=utf-8')}
        if path.startswith('/original/'):
            name = path[len('/original/'):] or 'index.html'
            allowed = {'index.html': 'text/html; charset=utf-8', 'main.js': 'text/javascript',
                       'style.css': 'text/css', 'polyfill.js': 'text/javascript',
                       'compat.js': 'text/javascript', 'i18n.js': 'text/javascript', 'appearance.js': 'text/javascript'}
            built = Path(__file__).resolve().parents[1] / 'agent/PhoneBox.Agent/kiosk-lite'
            if name not in allowed or not (built / name).is_file():
                self.send(404, {'error': 'Build tools/kiosk-lite first'})
                return
            self.send(200, (built / name).read_bytes(), allowed[name])
            return
        if path.startswith('/agent/api/'):
            path = path[len('/agent'):]
        if path in assets:
            name, kind = assets[path]
            self.send(200, (ASSETS / name).read_bytes(), kind)
            return
        if path not in ('/api/config', '/api/state'):
            self.send(404, {'error': 'Not found'})
            return
        if not self.authenticated():
            self.send(401, {'error': 'Pair this browser first'})
            return
        try:
            req = urllib.request.Request(self.server.agent + '/agent' + path,
                                         headers={'X-PhoneBox-Panel': '1'})
            with self.server.client.open(req, timeout=5) as response:
                value = json.loads(response.read(2 * 1024 * 1024))
            self.send(200, public_payload(path, value))
        except Exception:
            self.send(502, {'error': 'PhoneBox agent unavailable'})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', default='127.0.0.1', help='Use 0.0.0.0 only on a trusted LAN')
    parser.add_argument('--port', type=int, default=8789)
    args = parser.parse_args()
    pin = str(secrets.randbelow(1000000)).zfill(6)
    with LegacyServer((args.host, args.port), pin) as server:
        print('PhoneBox Legacy on port %s; pairing code: %s' % (args.port, pin), flush=True)
        print('Read-only HTTP: trusted home LAN only. No settings, commands, hooks or pairing API are forwarded.', flush=True)
        server.serve_forever()


if __name__ == '__main__':
    main()
