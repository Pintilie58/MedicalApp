import http.server, json, threading, socketserver, sys, time

MODE = {"v": "up"}   # up | 503 | slow

class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        m = MODE["v"]
        if self.path == "/mode/up":   MODE["v"] = "up";   self._ok({"mode": "up"}); return
        if self.path == "/mode/503":  MODE["v"] = "503";  self._ok({"mode": "503"}); return
        if self.path == "/mode/slow": MODE["v"] = "slow"; self._ok({"mode": "slow"}); return
        if self.path == "/ready":
            if m == "503":
                self.send_response(503); self.send_header("Content-Type", "application/json")
                self.end_headers(); self.wfile.write(b'{"status":"not_ready"}'); return
            if m == "slow":
                time.sleep(6)
            self._ok({"status": "ready", "entries": 97531}); return
        self.send_response(404); self.end_headers()

    def _ok(self, obj):
        body = json.dumps(obj).encode()
        self.send_response(200); self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body))); self.end_headers()
        self.wfile.write(body)

    def log_message(self, *a): pass

class S(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True

S(("127.0.0.1", 8000), H).serve_forever()
