"""Audio differential test: EtG's real Wwise vs. the browser engine (etg-audio.js).

    python port/audio/audiodiff.py                 # everything: cases, Wwise, browser, report
    python port/audio/audiodiff.py --filter Gun    # only cases whose name matches a regex
    python port/audio/audiodiff.py --report-only   # recompute the report from existing features
    options: --skip-ref, --skip-ours, --resume (render only cases without features), --repeats N
             (random cases), --jobs N (browser tabs), --listen N (flagged cases re-rendered with
             audio for the report), --out DIR

1. audiodiff_cases.py turns audio.json into cases (event x switch values, music states and
   transitions, volume RTPCs, mix states, 3D positions); a case is a timed script of API calls.
2. port/WwiseRef runs each script on the game's own AkSoundEngine.dll, offline, frame by frame.
3. audiodiff.html runs the same scripts through EtgAudio.render() (OfflineAudioContext) in
   headless Chrome.
4. Both mixes go through audiodiff_features.py (features/ref, features/ours; the audio itself is
   not kept), audiodiff_report.py compares them -> report.html / report.md. The worst cases are
   rendered again with audio (report/audio/) so they can be listened to side by side.

Output: port/build/audiodiff/. Needs the audio built by build_audio.py.
"""
import argparse
import concurrent.futures
import http.server
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
PORT_DIR = HERE.parent
ROOT = PORT_DIR.parent
WEBAUDIO = ROOT / "ripped" / "ExportedProject" / "Assets" / "StreamingAssets" / "webaudio"
WWISEREF = PORT_DIR / "build" / "WwiseRef" / "WwiseRef.exe"
CHROME_CANDIDATES = [
    Path(os.environ.get("PROGRAMFILES", "C:/Program Files")) / "Google/Chrome/Application/chrome.exe",
    Path(os.environ.get("PROGRAMFILES(X86)", "C:/Program Files (x86)")) / "Microsoft/Edge/Application/msedge.exe",
]
REF_BATCH = 100

sys.path.insert(0, str(HERE))
import audiodiff_cases  # noqa: E402
import audiodiff_features  # noqa: E402


def write_cases(out: Path, pattern, repeats):
    doc = json.loads((WEBAUDIO / "audio.json").read_text(encoding="utf-8"))
    cases, reset = audiodiff_cases.make_cases(doc, repeats)
    if pattern:
        rx = re.compile(pattern)
        cases = [c for c in cases if rx.search(c["name"])]
    (out / "cases.json").write_text(json.dumps({"reset": reset, "cases": cases}), encoding="utf-8")
    print(f"   {len(cases)} cases, {sum(c['duration'] for c in cases) / 60:.0f} min of audio")
    return cases


def build_ref():
    subprocess.run(["dotnet", "build", str(PORT_DIR / "WwiseRef"), "-c", "Release", "-o", str(WWISEREF.parent),
                    "-nologo", "-v", "q"], check=True, stdout=subprocess.DEVNULL)


def render_ref(out: Path, ids, wav_dir: Path, keep_audio=False, feat_dir: Path = None):
    """Wwise renders of `ids`: batches in parallel WwiseRef processes (each one Wwise instance, single
    threaded); features extracted as batches finish (the WAVs deleted unless keep_audio)."""
    wav_dir.mkdir(parents=True, exist_ok=True)
    start = time.time()
    batches = [ids[i:i + REF_BATCH] for i in range(0, len(ids), REF_BATCH)]
    done = 0

    def render(batch):
        subprocess.run([str(WWISEREF), str(ROOT), str(out / "cases.json"), str(wav_dir), "--only", ",".join(batch)],
                       check=True, stdout=subprocess.DEVNULL)
        if feat_dir:
            for cid in batch:
                wav = wav_dir / f"{cid}.wav"
                audiodiff_features.save(feat_dir / f"{cid}.npz", wav.read_bytes())
                if not keep_audio:
                    wav.unlink()
        return len(batch)

    workers = max(1, (os.cpu_count() or 2) - 1)
    with concurrent.futures.ThreadPoolExecutor(workers) as pool:
        for n in pool.map(render, batches):
            done += n
            print(f"   wwise: {done}/{len(ids)} cases, {time.time() - start:.0f} s", flush=True)


class Handler(http.server.SimpleHTTPRequestHandler):
    """Serves webaudio/ + the test page; the page POSTs each render back as a WAV."""
    out_dir: Path = None
    feat_dir: Path = None
    wav_dir: Path = None
    parts = {}
    state = None

    def translate_path(self, path):
        name = path.split("?")[0].lstrip("/")
        if name in ("audiodiff.html", "etg-audio.js"):  # the engine under test: the working copy
            return str(HERE / name)
        if name == "cases.json":
            return str(self.out_dir / name)
        return str(WEBAUDIO / name)

    def do_GET(self):
        if self.path.split("?")[0] == "/parts.json":
            body = json.dumps(self.parts).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        super().do_GET()

    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        if self.path.startswith("/result/"):
            cid = self.path.split("/")[-1]
            try:
                if self.feat_dir:
                    audiodiff_features.save(self.feat_dir / f"{cid}.npz", body)
                if self.wav_dir:
                    (self.wav_dir / f"{cid}.wav").write_bytes(body)
                self.state["rendered"].add(cid)
            except Exception as e:  # noqa: BLE001 - reported with the others
                self.state["errors"].append(f"{cid}: {e}")
            self.state["progress"] = time.time()
        elif self.path == "/error":
            self.state["errors"].append(body.decode("utf-8", "replace"))
            self.state["progress"] = time.time()
        elif self.path == "/finished":
            self.state["finished"] += 1
        self.send_response(204)
        self.end_headers()

    def log_message(self, *args):
        pass


STALL_SECONDS = 90


class Server(http.server.ThreadingHTTPServer):
    request_queue_size = 256  # the default 5 refuses connections when every tab loads the packs at once
    daemon_threads = True


def render_ours(out: Path, ids, jobs, feat_dir: Path = None, wav_dir: Path = None, query=""):
    """Renders `ids` in `jobs` headless Chrome tabs. A tab that makes no progress for STALL_SECONDS (a
    headless Chrome sometimes never loads its page) is given up on and whatever is left is rendered
    again, up to three rounds."""
    chrome = next((p for p in CHROME_CANDIDATES if p.exists()), None)
    if not chrome:
        raise SystemExit("Chrome/Edge not found")
    for d in (feat_dir, wav_dir):
        if d:
            d.mkdir(parents=True, exist_ok=True)
    Handler.out_dir, Handler.feat_dir, Handler.wav_dir = out, feat_dir, wav_dir
    Handler.state = {"rendered": set(), "finished": 0, "errors": [], "progress": time.time()}
    server = Server(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    port = server.server_address[1]
    start = time.time()
    todo = list(ids)
    try:
        for attempt in range(3):
            if not todo:
                break
            n = max(1, min(jobs, len(todo) // 20 or 1))
            Handler.parts = {str(j): todo[j::n] for j in range(n)}
            Handler.state["finished"] = 0
            Handler.state["progress"] = time.time()
            profile = Path(tempfile.mkdtemp(prefix="audiodiff-chrome-"))
            procs = [subprocess.Popen([str(chrome), "--headless=new", "--disable-gpu", "--no-first-run", "--mute-audio",
                                       "--disable-background-timer-throttling", "--disable-renderer-backgrounding",
                                       f"--user-data-dir={profile / str(j)}", f"http://127.0.0.1:{port}/audiodiff.html?part={j}{query}"],
                                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) for j in range(n)]
            last = 0
            try:
                while Handler.state["finished"] < n:
                    time.sleep(0.5)
                    done = len(Handler.state["rendered"])
                    if done - last >= 250:
                        last = done
                        print(f"   browser: {done}/{len(ids)} cases, {time.time() - start:.0f} s", flush=True)
                    if time.time() - Handler.state["progress"] > STALL_SECONDS:
                        print(f"   browser: no progress for {STALL_SECONDS} s, restarting what is left", flush=True)
                        break
            finally:
                for p in procs:
                    p.kill()
                time.sleep(0.5)
                shutil.rmtree(profile, ignore_errors=True)
            todo = [i for i in todo if i not in Handler.state["rendered"]]
        print(f"   browser: {len(Handler.state['rendered'])} cases in {time.time() - start:.0f} s"
              + (f", {len(todo)} not rendered" if todo else ""))
        for e in Handler.state["errors"][:20]:
            print("   browser error:", e[:400])
        if len(Handler.state["errors"]) > 20:
            print(f"   ... {len(Handler.state['errors']) - 20} more errors")
    finally:
        server.shutdown()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--filter")
    ap.add_argument("--repeats", type=int, default=3)
    ap.add_argument("--jobs", type=int, default=max(1, min(8, (os.cpu_count() or 2) // 2)))
    ap.add_argument("--listen", type=int, default=60)
    ap.add_argument("--skip-ref", action="store_true")
    ap.add_argument("--skip-ours", action="store_true")
    ap.add_argument("--report-only", action="store_true")
    ap.add_argument("--resume", action="store_true", help="keep existing features, render only what is missing")
    ap.add_argument("--out", default=str(PORT_DIR / "build" / "audiodiff"))
    args = ap.parse_args()
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    feats = {"ref": out / "features" / "ref", "ours": out / "features" / "ours"}

    if not args.report_only:
        print("== Cases")
        cases = write_cases(out, args.filter, args.repeats)
        ids = [c["id"] for c in cases]
        for side in feats:
            if not args.resume:
                shutil.rmtree(feats[side], ignore_errors=True)
            feats[side].mkdir(parents=True, exist_ok=True)
        missing = {side: [i for i in ids if not (feats[side] / f"{i}.npz").exists()] for side in feats}
        if not args.skip_ref and missing["ref"]:
            print("== Wwise (EtG_Data/Plugins/AkSoundEngine.dll, offline)")
            build_ref()
            render_ref(out, missing["ref"], out / "tmp_ref", feat_dir=feats["ref"])
        if not args.skip_ours and missing["ours"]:
            print("== Browser engine (headless Chrome, OfflineAudioContext)")
            render_ours(out, missing["ours"], args.jobs, feat_dir=feats["ours"])

    print("== Report")
    import audiodiff_report
    results = audiodiff_report.compare(out)
    listen = audiodiff_report.worst_ids(results, args.listen)
    audio = out / "report_audio"
    if listen and not args.report_only:
        print(f"== Audio of the {len(listen)} worst cases")
        shutil.rmtree(audio, ignore_errors=True)
        build_ref()
        render_ref(out, listen, audio / "ref", keep_audio=True)
        render_ours(out, listen, 1, wav_dir=audio / "ours")
    audiodiff_report.write(out, results, audio)


if __name__ == "__main__":
    main()
