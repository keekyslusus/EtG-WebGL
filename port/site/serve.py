"""Local web server for a WebGL build folder (serve-webbuild.ps1 runs it).

Unity 6 compresses the player's files on disk (Build/*.data.gz, *.framework.js.gz, *.wasm.gz; .br for Brotli) and
leaves decompression to the browser, which needs "Content-Encoding: gzip" plus the type of the uncompressed file
(application/wasm for streaming compilation). Python's http.server sends neither, so this adds them; everything else
is served as http.server does. Caching is left to the loader (it revalidates the data file in its IndexedDB cache).

Usage: python port/site/serve.py <dir> [--port 8080] [--bind 127.0.0.1]
"""
import argparse
import functools
import http.server
import os

ENCODINGS = {".gz": "gzip", ".br": "br"}
TYPES = {".wasm": "application/wasm", ".js": "application/javascript", ".data": "application/octet-stream",
         ".json": "application/json"}


class Handler(http.server.SimpleHTTPRequestHandler):
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
    args = ap.parse_args()
    handler = functools.partial(Handler, directory=os.path.abspath(args.dir))
    with http.server.ThreadingHTTPServer((args.bind, args.port), handler) as server:
        print("Serving %s on http://localhost:%d/ (Ctrl+C to stop)" % (os.path.abspath(args.dir), args.port))
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
