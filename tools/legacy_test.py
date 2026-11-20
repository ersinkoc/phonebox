import importlib.util
import json
import threading
import unittest
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

spec = importlib.util.spec_from_file_location('legacy', Path(__file__).with_name('legacy-server.py'))
legacy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(legacy)


class FakeAgent(BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def do_GET(self):
        value = {'version': 'test', 'config': {'pages': [{'id': 'home', 'widgets': []}], 'shortcuts': ['private'], 'cameras': ['private'], 'display': {}}} if self.path.endswith('/config') else {'pc': {'cpu': 42}, 'privacy': {'micApps': ['private']}, 'events': ['private']}
        data = json.dumps(value).encode()
        self.send_response(200); self.send_header('Content-Length', str(len(data))); self.end_headers(); self.wfile.write(data)


class BridgeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.agent = ThreadingHTTPServer(('127.0.0.1', 0), FakeAgent)
        cls.server = legacy.LegacyServer(('127.0.0.1', 0), '123456', 'http://127.0.0.1:' + str(cls.agent.server_port))
        for s in (cls.agent, cls.server): threading.Thread(target=s.serve_forever, daemon=True).start()
        cls.base = 'http://127.0.0.1:' + str(cls.server.server_port)
        cls.client = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    @classmethod
    def tearDownClass(cls):
        for s in (cls.server, cls.agent): s.shutdown(); s.server_close()

    def fetch(self, path, data=None, cookie=None, origin=None):
        headers = {}
        if cookie: headers['Cookie'] = cookie
        if origin: headers['Origin'] = origin
        req = urllib.request.Request(self.base + path, data=data, headers=headers)
        try: return self.client.open(req, timeout=3)
        except urllib.error.HTTPError as e: return e

    def pair(self):
        r = self.fetch('/pair', b'pin=123456')
        self.assertEqual(r.status, 200)
        return r.headers['Set-Cookie'].split(';')[0]

    def test_requires_pairing(self):
        self.assertEqual(self.fetch('/api/state').status, 401)

    def test_agent_data_is_filtered(self):
        cookie = self.pair()
        cfg = json.load(self.fetch('/api/config', cookie=cookie))
        self.assertEqual(cfg['config']['pages'][0]['id'], 'home')
        self.assertNotIn('shortcuts', cfg['config'])
        self.assertNotIn('cameras', cfg['config'])
        state = json.load(self.fetch('/api/state', cookie=cookie))
        self.assertEqual(state['pc']['cpu'], 42)
        self.assertNotIn('privacy', state)
        self.assertNotIn('events', state)

    def test_no_commands_or_settings(self):
        cookie = self.pair()
        for path in ('/api/run/a', '/api/config', '/api/volume', '/api/hooks/x'):
            self.assertEqual(self.fetch(path, b'{}', cookie).status, 405)
        for path in ('/api/pairing', '/api/quotas', '/api/work', '/../../LICENSE'):
            self.assertEqual(self.fetch(path, cookie=cookie).status, 404)

    def test_cross_origin_pairing_refused(self):
        self.assertEqual(self.fetch('/pair', b'pin=123456', origin='http://other.invalid').status, 403)

    def test_wrong_code_is_rate_limited(self):
        with self.server.guard: self.server.attempts.clear()
        for i in range(5): self.assertEqual(self.fetch('/pair', b'pin=000000').status, 403)
        self.assertEqual(self.fetch('/pair', b'pin=123456').status, 429)
        with self.server.guard: self.server.attempts.clear()

    def test_static_assets_without_pairing(self):
        for path in ('/', '/legacy.js', '/legacy.css'):
            self.assertEqual(self.fetch(path).status, 200)


if __name__ == '__main__': unittest.main()
