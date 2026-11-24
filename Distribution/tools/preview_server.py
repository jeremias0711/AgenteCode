"""Local preview of a WebGL release exactly as Cloudflare Pages serves it (standard library only).

    python -I preview_server.py "C:\\Users\\...\\GameDevAgent_Builds\\WebRelease_<date>" [--port 8800] [--bind 127.0.0.1] [--log report.log]

- Applies the "_headers" file of the build (path patterns with *, like Cloudflare Pages), so the Brotli files are
  sent with Content-Encoding: br and the right MIME types. A wrong header shows up here before publishing.
- Receives the page's test reports (/__gda/report, only sent with ?gdaReport=1) and prints/logs them.
- Binds to 127.0.0.1 by default (this computer only). It is a test tool: never expose it to the Internet.
"""
import argparse
import http.server
import os
import re
import sys
import threading
import time
from urllib.parse import parse_qs, urlparse

MIME = {".wasm": "application/wasm", ".js": "application/javascript", ".json": "application/json", ".html": "text/html; charset=utf-8",
        ".webp": "image/webp", ".png": "image/png", ".ttf": "font/ttf", ".txt": "text/plain; charset=utf-8", ".data": "application/octet-stream"}


def load_headers(root):
    rules, current = [], None
    path = os.path.join(root, "_headers")
    if not os.path.exists(path):
        return rules
    for raw in open(path, encoding="utf-8"):
        line = raw.rstrip("\n")
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if not line.startswith((" ", "\t")):
            pattern = "^" + re.escape(line.strip()).replace(r"\*", ".*") + "$"
            current = (re.compile(pattern), [])
            rules.append(current)
        elif current is not None and ":" in line:
            k, v = line.strip().split(":", 1)
            current[1].append((k.strip(), v.strip()))
    return rules


def make_handler(root, rules, log):
    lock = threading.Lock()

    class Handler(http.server.SimpleHTTPRequestHandler):
        def __init__(self, *a, **kw):
            super().__init__(*a, directory=root, **kw)

        def log_message(self, fmt, *args):
            pass  # quiet: only the reports are printed

        def report(self):
            q = parse_qs(urlparse(self.path).query)
            line = "%s t=%sms %s: %s" % (time.strftime("%H:%M:%S"), q.get("t", ["?"])[0], q.get("k", ["?"])[0], q.get("m", [""])[0])
            with lock:
                print(line, flush=True)
                if log:
                    with open(log, "a", encoding="utf-8") as f:
                        f.write(line + "\n")
            self.send_response(204)
            self.end_headers()

        def do_POST(self):
            if self.path.startswith("/__gda/report"):
                return self.report()
            self.send_error(405)

        def do_GET(self):
            if self.path.startswith("/__gda/report"):
                return self.report()
            return super().do_GET()

        def guess_type(self, path):
            name = path[:-3] if path.endswith(".br") else path[:-3] if path.endswith(".gz") else path
            return MIME.get(os.path.splitext(name)[1].lower(), super().guess_type(name))

        def rule_headers(self):
            url_path = urlparse(self.path).path
            out = []
            for pattern, headers in rules:
                if pattern.match(url_path):
                    out.extend(headers)
            return out

        def send_header(self, keyword, value):
            # Like Cloudflare Pages: a Content-Type given in _headers replaces the guessed one.
            if keyword.lower() == "content-type" and not getattr(self, "_rule_pass", False):
                for k, v in self.rule_headers():
                    if k.lower() == "content-type":
                        value = v
            super().send_header(keyword, value)

        def end_headers(self):
            self._rule_pass = True
            for k, v in self.rule_headers():
                if k.lower() != "content-type":
                    self.send_header(k, v)
            self._rule_pass = False
            super().end_headers()

    return Handler


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("root")
    ap.add_argument("--port", type=int, default=8800)
    ap.add_argument("--bind", default="127.0.0.1")
    ap.add_argument("--log", default=None)
    a = ap.parse_args()
    root = os.path.abspath(a.root)
    if not os.path.exists(os.path.join(root, "index.html")):
        sys.exit("no index.html in " + root)
    rules = load_headers(root)
    server = http.server.ThreadingHTTPServer((a.bind, a.port), make_handler(root, rules, a.log))
    print("serving %s on http://%s:%d/ (%d header rules)" % (root, a.bind, a.port, len(rules)), flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
