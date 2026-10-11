"""Converts EtG's Wwise soundbanks into what the browser engine (etg-audio.js) plays.

    python port/audio/build_audio.py [--jobs N] [--force]

Output (OUT = ripped/ExportedProject/Assets/StreamingAssets/webaudio, mirrored to
webbuild/StreamingAssets/webaudio when that exists):
  audio.json    events, actions, the node tree (sounds, containers, music), busses, states,
                attenuations and an index of where each media file lives
  sfx<N>.pack   sound effect media, Ogg Opus files concatenated (loaded in the background at boot)
  music/<id>.ogg  music media, one file each (fetched when a music event needs it)
  etg-audio.js  the engine (copied from port/audio)
  soundboard.html  a test page: post events, set switches/states/volumes without the game
  ir/<bus>.wav  impulse responses of bus effect chains (reverbs), measured by measure_ir.py

Media goes through vgmstream (Wwise ADPCM / Wwise Vorbis -> WAV) and ffmpeg (-> Opus); the
encoded files are cached in port/build/audio/enc, so a re-run only rebuilds the JSON.
"""
import argparse
import concurrent.futures
import json
import os
import re
import shutil
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import wwbank  # noqa: E402

PORT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ROOT = os.path.dirname(PORT)
BANKS = os.path.join(ROOT, "Enter the Gungeon", "EtG_Data", "StreamingAssets", "Audio", "GeneratedSoundBanks", "Windows")
VGMSTREAM = os.path.join(ROOT, "tools", "vgmstream", "vgmstream-cli.exe")
CACHE = os.path.join(PORT, "build", "audio")
OUT = os.path.join(ROOT, "ripped", "ExportedProject", "Assets", "StreamingAssets", "webaudio")
WEB_OUT = os.path.join(ROOT, "webbuild", "StreamingAssets", "webaudio")

# Opus bitrates (kbit/s). Wwise ADPCM is ~144 kbit/s per channel at 36 kHz.
BITRATE = {("sfx", 1): 48, ("sfx", 2): 80, ("music", 1): 64, ("music", 2): 96}
ENCODE_VERSION = 1  # bump to invalidate the encode cache
PACK_SIZE = 4 << 20


# --- media -----------------------------------------------------------------------------------

def vgm_meta(wem):
    out = subprocess.run([VGMSTREAM, "-m", wem], capture_output=True, text=True, check=True).stdout
    meta = {}
    for key, pat in (("rate", r"sample rate: (\d+)"), ("channels", r"channels: (\d+)"),
                     ("samples", r"stream total samples: (\d+)"), ("loop_start", r"loop start: (\d+)"),
                     ("loop_end", r"loop end: (\d+)")):
        m = re.search(pat, out)
        if m:
            meta[key] = int(m.group(1))
    return meta


def encode(job):
    mid, wem, enc_dir, kind, force = job
    meta = vgm_meta(wem)
    kbps = BITRATE[(kind, min(meta["channels"], 2))]
    out = os.path.join(enc_dir, f"{mid}_{kbps}.ogg")
    meta["file"] = out
    if force or not os.path.exists(out):
        tmp = out + ".tmp"
        # -i: decode the file once, ignoring loop points (the engine loops by itself)
        dec = subprocess.Popen([VGMSTREAM, "-i", "-p", wem], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        enc = subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "wav", "-i", "pipe:0", "-c:a", "libopus",
                              "-b:a", f"{kbps}k", "-vbr", "on", "-application", "audio", "-f", "ogg", tmp],
                             stdin=dec.stdout, capture_output=True)
        dec.stdout.close()
        dec.wait()
        if dec.returncode or enc.returncode:
            raise RuntimeError(f"media {mid}: vgmstream {dec.returncode}, ffmpeg {enc.returncode}: {enc.stderr.decode()}")
        os.replace(tmp, out)
    return mid, meta


def convert_media(bank, kinds, jobs, force):
    wem_dir = os.path.join(CACHE, "wem")
    enc_dir = os.path.join(CACHE, f"enc{ENCODE_VERSION}")
    os.makedirs(wem_dir, exist_ok=True)
    os.makedirs(enc_dir, exist_ok=True)
    work = []
    for mid, (off, size) in bank["media"].items():
        wem = os.path.join(wem_dir, f"{mid}.wem")
        if not os.path.exists(wem) or os.path.getsize(wem) != size:
            with open(wem, "wb") as f:
                f.write(bank["data"][off:off + size])
        work.append((mid, wem, enc_dir, kinds.get(mid, "sfx"), force))
    metas = {}
    print(f"   {len(work)} media files")
    with concurrent.futures.ThreadPoolExecutor(jobs) as pool:
        for i, (mid, meta) in enumerate(pool.map(encode, work)):
            metas[mid] = meta
            if (i + 1) % 300 == 0:
                print(f"   {i + 1}/{len(work)}")
    return metas


# --- json ------------------------------------------------------------------------------------

def rnd(v):
    return round(v, 4) if isinstance(v, float) else v


def props(p):
    return {str(k): rnd(v) for k, v in p.items()}


def ranged(p):
    return {str(k): [rnd(a), rnd(b)] for k, (a, b) in p.items()}


def rtpcs(lst):
    return [{"id": r["rtpc"], "type": r["type"], "param": r["param"], "scaling": r["scaling"],
             "points": [[rnd(x), rnd(y), i] for x, y, i in r["points"]]} for r in lst]


def states(groups):
    return [[g["group"], [[s, inst] for s, inst in g["states"]]] for g in groups]


def common(o):
    n = {"type": o["type"], "parent": o.get("parent", 0)}
    if o.get("bus"):
        n["bus"] = o["bus"]
    for key, conv in (("props", props), ("ranged", ranged), ("rtpc", rtpcs), ("states", states)):
        if o.get(key):
            n[key] = conv(o[key])
    pos = o.get("pos")
    if pos and pos["bits"] & 1:  # positioning overrides the parent's
        n["pos"] = {"3d": bool(pos["bits"] & 0x10), "att": pos.get("attenuation", 0)}
    adv = o.get("adv")
    if adv and adv["max_inst"]:
        n["max"] = {"n": adv["max_inst"], "bits": adv["bits"]}
    if o.get("children"):
        n["children"] = o["children"]
    fx = [[f["index"], f["id"]] for f in o.get("fx", []) if f["id"]]
    if fx:
        n["fx"] = fx
    return n


def export_node(o):
    t = o["type"]
    n = common(o)
    if t == "sound":
        n["media"] = o["source"]["id"]
    elif t == "ranseq":
        n.update(loop=o["loop"], avoid=o["avoid_repeat"], mode=o["mode"], random=o["random_mode"],
                 bits=o["rs_bits"], trans=[rnd(v) for v in o["trans_time"]], trans_mode=o["trans_mode"],
                 playlist=[[c, w] for c, w in o["playlist"]])
    elif t == "switch":
        n.update(group_type=o["group_type"], group=o["group"], default=o["default"],
                 switches={str(k): v for k, v in o["switches"].items()},
                 modes={str(k): [v["bits"], v["mode"], v["fade_out"], v["fade_in"]] for k, v in o["switch_params"].items()})
    elif t == "layer":
        pass
    elif t in ("mseg", "mswitch", "mranseq"):
        n.update(grid=[rnd(o["grid_period"]), rnd(o["grid_offset"])], tempo=rnd(o["tempo"]), sig=list(o["time_sig"]),
                 meter=o["meter_flag"], mflags=o["mflags"])
        if o["stingers"]:
            n["stingers"] = o["stingers"]
        if t == "mseg":
            n.update(duration=rnd(o["duration"]), markers=[[m["id"], rnd(m["pos"]), m["name"]] for m in o["markers"]])
        else:
            n["rules"] = o["rules"]
        if t == "mswitch":
            n.update(continue_playback=o["continue_playback"], tree_groups=o["tree_groups"], tree_mode=o["tree_mode"],
                     tree=o["tree"])
        if t == "mranseq":
            n["playlist"] = o["playlist"]
    elif t == "mtrack":
        n.update(mflags=o["mflags"], sources=[s["id"] for s in o["sources"]], track_type=o["track_type"],
                 clips=[[c["track"], c["source"], rnd(c["play_at"]), rnd(c["begin_trim"]), rnd(c["end_trim"]),
                         rnd(c["src_duration"])] for c in o["clips"]],
                 subtracks=o.get("num_subtracks", 0), lookahead=o["lookahead"])
        if o["clip_autos"]:
            n["clip_autos"] = [[a["clip"], a["type"], [[rnd(x), rnd(y), i] for x, y, i in a["points"]]] for a in o["clip_autos"]]
        if o["track_type"] == 3:
            n["switch"] = {"group_type": o["sw_group_type"], "group": o["sw_group"], "default": o["sw_default"],
                           "assoc": o["sw_assoc"], "trans": o["sw_trans"]}
    elif t in ("bus", "auxbus"):
        if o.get("ducks"):
            n["ducks"] = o["ducks"]
    return n


def export_action(a):
    out = {"type": a["atype"] >> 8, "scope": a["atype"] & 0xFF, "target": a["target"]}
    if a["is_bus"]:
        out["is_bus"] = 1
    if a["props"]:
        out["props"] = props(a["props"])
    if a["ranged"]:
        out["ranged"] = ranged(a["ranged"])
    for key in ("curve", "group", "state", "switch", "meaning", "flags"):
        if key in a:
            out[key] = a[key]
    if "value" in a:
        out["value"] = [rnd(v) for v in a["value"]]
    if a.get("except"):
        out["except"] = a["except"]
    return out


def read_names():
    names = {}
    for f in ("Init.txt", "SFX.txt"):
        section = None
        for line in open(os.path.join(BANKS, f), encoding="latin1"):
            parts = line.rstrip("\n").split("\t")
            if parts and parts[0]:
                section = parts[0]
                continue
            if len(parts) > 2 and parts[1].isdigit() and section in ("Event", "Switch Group", "State Group", "Switch", "State", "Game Parameter", "Audio Bus", "Auxiliary Bus", "Trigger"):
                names.setdefault(int(parts[1]), parts[2])
    return names


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--jobs", type=int, default=os.cpu_count())
    ap.add_argument("--force", action="store_true", help="re-encode all media")
    args = ap.parse_args()

    print("== Parsing soundbanks")
    init = wwbank.parse_bank(os.path.join(BANKS, "Init.bnk"))
    sfx = wwbank.parse_bank(os.path.join(BANKS, "SFX.bnk"))
    objects = {**init["objects"], **sfx["objects"]}

    kinds = {}
    for o in objects.values():
        if o["type"] == "mtrack":
            for s in o["sources"]:
                kinds[s["id"]] = "music"

    print("== Converting media (vgmstream + ffmpeg)")
    metas = convert_media(sfx, kinds, args.jobs, args.force)

    print("== Writing", OUT)
    os.makedirs(os.path.join(OUT, "music"), exist_ok=True)
    media = {}
    packs, pack, pack_size = [], None, 0
    for mid in sorted(metas):
        meta = metas[mid]
        src = meta["file"]
        entry = {"rate": meta["rate"], "channels": meta["channels"], "samples": meta["samples"]}
        if "loop_start" in meta:
            entry["loop"] = [meta["loop_start"], meta["loop_end"]]
        if kinds.get(mid) == "music":
            dst = os.path.join(OUT, "music", f"{mid}.ogg")
            if not os.path.exists(dst) or os.path.getsize(dst) != os.path.getsize(src):
                shutil.copyfile(src, dst)
            entry["file"] = f"music/{mid}.ogg"
        else:
            data = open(src, "rb").read()
            if pack is None or pack_size + len(data) > PACK_SIZE:
                pack = open(os.path.join(OUT, f"sfx{len(packs)}.pack"), "wb")
                packs.append(f"sfx{len(packs)}.pack")
                pack_size = 0
            pack.write(data)
            entry["pack"] = [len(packs) - 1, pack_size, len(data)]
            pack_size += len(data)
        media[str(mid)] = entry
    if pack:
        pack.close()

    events, nodes = {}, {}
    for oid, o in objects.items():
        t = o["type"]
        if t == "event":
            events[str(oid)] = [export_action(objects[a]) for a in o["actions"]]
        elif t == "state":
            nodes[str(oid)] = {"type": "state", "props": props(o["props"])}
        elif t == "attenuation":
            nodes[str(oid)] = {"type": "attenuation", "uses": o["uses"], "rtpc": rtpcs(o["rtpc"]),
                               "curves": [{"scaling": c["scaling"], "points": [[rnd(x), rnd(y), i] for x, y, i in c["points"]]}
                                          for c in o["curves"]]}
        elif t in ("sound", "ranseq", "switch", "layer", "mixer", "bus", "auxbus", "mseg", "mtrack", "mswitch", "mranseq"):
            nodes[str(oid)] = export_node(o)

    effects = {}
    for oid, o in objects.items():
        if o["type"] in ("fxshareset", "fxcustom"):
            e = {"plugin": o["plugin"]}
            if o["plugin"] == "parametric_eq":
                e.update(wwbank.parametric_eq(o["params"]))
            effects[str(oid)] = e

    # Bus reverbs measured in the real Wwise by measure_ir.py (port/build/audio/ir/<bus>.wav).
    reverbs = {}
    ir_dir = os.path.join(CACHE, "ir")
    if os.path.isdir(ir_dir):
        os.makedirs(os.path.join(OUT, "ir"), exist_ok=True)
        for fn in sorted(os.listdir(ir_dir)):
            if fn.endswith(".wav"):
                shutil.copyfile(os.path.join(ir_dir, fn), os.path.join(OUT, "ir", fn))
                reverbs[fn[:-4]] = f"ir/{fn}"
    if not reverbs:
        print("   no measured bus reverbs (port/build/audio/ir) - run measure_ir.py; sound effects will be dry")

    doc = {"version": 1, "settings": init["settings"], "effects": effects, "reverbs": reverbs, "packs": packs, "media": media, "events": events, "nodes": nodes,
           "names": {str(k): v for k, v in read_names().items()}}
    with open(os.path.join(OUT, "audio.json"), "w", encoding="utf-8") as f:
        json.dump(doc, f, separators=(",", ":"))
    for name in ("etg-audio.js", "soundboard.html"):
        shutil.copyfile(os.path.join(PORT, "audio", name), os.path.join(OUT, name))
    for name in ("Init.txt", "SFX.txt"):  # the soundboard lists switch/state names from these
        shutil.copyfile(os.path.join(BANKS, name), os.path.join(OUT, name))

    total = sum(os.path.getsize(os.path.join(dp, fn)) for dp, _, fns in os.walk(OUT) for fn in fns if not fn.endswith(".meta"))
    print(f"   {len(events)} events, {len(nodes)} nodes, {len(packs)} packs, {total / 1e6:.1f} MB total")

    if os.path.isdir(os.path.dirname(WEB_OUT)):
        print("== Mirroring to", WEB_OUT)
        shutil.copytree(OUT, WEB_OUT, dirs_exist_ok=True, ignore=shutil.ignore_patterns("*.meta"))


if __name__ == "__main__":
    main()
