"""Local web server for a WebGL build folder (serve-webbuild.ps1 runs it).

Unity 6 compresses the player's files on disk (Build/*.data.gz, *.framework.js.gz, *.wasm.gz; .br for Brotli) and
leaves decompression to the browser, which needs "Content-Encoding: gzip" plus the type of the uncompressed file
(application/wasm for streaming compilation). Python's http.server sends neither, so this adds them; everything else
is served as http.server does. Caching is left to the loader (it revalidates the data file in its IndexedDB cache).

With --reports DIR it also takes the mod smoke test's report from the page (?smoketest posts report.md / report.json,
and console.txt when the runtime aborts, to /__smoke/<name>; etg-site.js) and writes it into DIR, e.g.
port/build/modsmoke/web.

Usage: python port/site/serve.py <dir> [--port 8080] [--bind 127.0.0.1] [--reports DIR]
"""
import argparse
import functools
import http.server
import os

ENCODINGS = {".gz": "gzip", ".br": "br"}
TYPES = {".wasm": "application/wasm", ".js": "application/javascript", ".data": "application/octet-stream",
         ".json": "application/json"}


SMOKE_FILES = ("report.md", "report.json", "console.txt")


class Handler(http.server.SimpleHTTPRequestHandler):
    reports = None

    def do_POST(self):
        name = self.path.split("?")[0]
        if not self.reports or not name.startswith("/__smoke/") or name[len("/__smoke/"):] not in SMOKE_FILES:
            self.send_error(404)
            return
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        os.makedirs(self.reports, exist_ok=True)
        with open(os.path.join(self.reports, name[len("/__smoke/"):]), "wb") as f:
            f.write(body)
        self.send_response(204)
        self.end_headers()

    def guess_type(self, path):
        base, ext = os.path.splitext(path)
        if ext in ENCODINGS:
            inner = os.path.splitext(base)[1]
            return TYPES.get(inner, "application/octet-stream")
        return TYPES.get(ext) or super().guess_type(path)

    def end_headers(self):
        ext = os.path.splitext(self.path.split("?")[0])[1]
        if ext in ENCODINGS:
            self.send_header("Content-Encoding", ENCODINGS[ext])
        self.send_header("Cache-Control", "no-cache")
        super().end_headers()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dir")
    ap.add_argument("--port", type=int, default=8080)
    ap.add_argument("--bind", default="127.0.0.1")
    ap.add_argument("--reports", help="where POST /__smoke/report.md|json from ?smoketest goes")
    args = ap.parse_args()
    Handler.reports = os.path.abspath(args.reports) if args.reports else None
    handler = functools.partial(Handler, directory=os.path.abspath(args.dir))
    with http.server.ThreadingHTTPServer((args.bind, args.port), handler) as server:
        print("Serving %s on http://localhost:%d/ (Ctrl+C to stop)" % (os.path.abspath(args.dir), args.port))
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
