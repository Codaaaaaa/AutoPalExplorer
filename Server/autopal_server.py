#!/usr/bin/env python3
import json
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import urlparse, parse_qs

API_KEY = "woshinidie"

# 存储结构：{ territory (int): set(long) }
ignored = {}


class Handler(BaseHTTPRequestHandler):
    def _send_json(self, status_code, obj):
        data = json.dumps(obj).encode("utf-8")
        self.send_response(status_code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _check_api_key_query(self, query):
        key = query.get("api_key", [""])[0]
        return key == API_KEY

    def do_GET(self):
        parsed = urlparse(self.path)
        qs = parse_qs(parsed.query)

        if parsed.path == "/health":
            if not self._check_api_key_query(qs):
                self._send_json(401, {"error": "invalid api_key"})
                return
            self._send_json(200, {"status": "ok"})
            return

        if parsed.path == "/ignored":
            if not self._check_api_key_query(qs):
                self._send_json(401, {"error": "invalid api_key"})
                return

            try:
                territory = int(qs.get("territory", [0])[0])
            except ValueError:
                self._send_json(400, {"error": "invalid territory"})
                return

            keys = list(ignored.get(territory, set()))
            self._send_json(200, {"territory": territory, "keys": keys})
            return

        self.send_error(404)

    def do_POST(self):
        parsed = urlparse(self.path)
        length = int(self.headers.get("Content-Length", "0") or "0")
        body = self.rfile.read(length) if length > 0 else b"{}"

        try:
            data = json.loads(body.decode("utf-8"))
        except Exception:
            self._send_json(400, {"error": "invalid json"})
            return

        if data.get("api_key") != API_KEY:
            self._send_json(401, {"error": "invalid api_key"})
            return

        # ====== 新增：/ignored/reserve ======
        if parsed.path == "/ignored/reserve":
            try:
                territory = int(data.get("territory", 0))
                keys = data.get("keys", [])
            except Exception:
                self._send_json(400, {"error": "invalid payload"})
                return

            if not isinstance(keys, list) or not keys:
                self._send_json(400, {"error": "keys must be non-empty list"})
                return

            s = ignored.setdefault(territory, set())
            taken = []
            added = []

            for k in keys:
                if not isinstance(k, int):
                    continue
                if k in s:
                    taken.append(k)
                else:
                    s.add(k)
                    added.append(k)

            # ok=True 表示这些 key 之前都不存在，这次成功抢到
            ok = len(added) > 0 and len(taken) == 0

            self._send_json(200, {
                "territory": territory,
                "ok": ok,
                "added": added,
                "taken": taken,
                "count": len(s),
            })
            return
        # ====== 原本的 /ignored/add 和 /ignored/clear 保持不变 ======

        if parsed.path == "/ignored/add":
            try:
                territory = int(data.get("territory", 0))
                keys = data.get("keys", [])
                print("----")
                print(data)
                print(keys)
                print("---")
            except Exception:
                self._send_json(400, {"error": "invalid payload"})
                return

            s = ignored.setdefault(territory, set())
            for k in keys:
                if isinstance(k, int):
                    s.add(k)

            self._send_json(200, {"territory": territory, "count": len(s)})
            return

        if parsed.path == "/ignored/clear":
            try:
                territory = int(data.get("territory", 0))
            except Exception:
                self._send_json(400, {"error": "invalid payload"})
                return

            ignored.pop(territory, None)
            self._send_json(200, {"territory": territory, "cleared": True})
            return

        self.send_error(404)


def main():
    port = 6666
    server = HTTPServer(("0.0.0.0", port), Handler)
    print(f"AutoPalExplorer server listening on 0.0.0.0:{port}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
