"""Converts EtG's Wwise soundbanks into what the browser engine (etg-audio.js) plays.

    python port/audio/build_audio.py [--jobs N] [--force]
    python port/audio/build_audio.py --mods port/build/audio/mods   (the baked mods' banks; apply.sh --mods)

Output (OUT = ripped/Unity6Project/Assets/StreamingAssets/webaudio, mirrored to
webbuild6/StreamingAssets/webaudio when that exists):
  audio.json    events, actions, the node tree (sounds, containers, music), busses, states,
                attenuations and an index of where each media file lives
  sfx<N>.pack   sound effect media, Ogg Opus files concatenated (loaded in the background at boot)
  music/<id>.ogg  music media, one file each (fetched when a music event needs it)
  etg-audio.js  the engine (copied from port/audio)
  soundboard.html  a test page: post events, set switches/states/volumes without the game
  ir/<bus>.wav  impulse responses of bus effect chains (reverbs), measured by measure_ir.py
  version.txt   build id the loader appends to every URL, so browsers don't keep stale files
  mods.json, mods/<package>/   the baked mods' banks in the same format (--mods), merged in by the engine

Media goes through vgmstream (Wwise ADPCM / Wwise Vorbis -> WAV) and ffmpeg (-> Opus); the
encoded files are cached in port/build/audio/enc, so a re-run only rebuilds the JSON.
"""
import argparse
import hashlib
import struct
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
OUT = os.path.join(ROOT, "ripped", "Unity6Project", "Assets", "StreamingAssets", "webaudio")
WEB_OUT = os.path.join(ROOT, "webbuild6", "StreamingAssets", "webaudio")

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
        if pos["bits"] & 0x10 and not pos["bits3d"] & 3:  # 3D spatialization "None": attenuated, not panned
            n["pos"]["pan"] = False
    adv = o.get("adv")
    if adv and adv["max_inst"]:
        n["max"] = {"n": adv["max_inst"], "bits": adv["bits"]}
    if adv and adv["bits2"] & 9:  # HDR envelope tracking: bit 0 overrides the parent's, bit 3 enables
        n["hdr_env"] = [bool(adv["bits2"] & 1), bool(adv["bits2"] & 8)]
    if o.get("children"):
        n["children"] = o["children"]
    fx = [[f["index"], f["id"]] for f in o.get("fx", []) if f["id"]]
    if fx:
        n["fx"] = fx
    aux = o.get("aux")
    if aux and aux["bits"] & 8 and any(aux["aux"]):  # user-defined aux sends (send volume 0 dB in EtG)
        n["aux"] = [a for a in aux["aux"] if a]
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
        if o["hdr_bits"] & 1:  # HDR bus (bit 1: exponential release)
            p = o["props"]
            n["hdr"] = {"threshold": rnd(p.get("hdr_threshold", 0.0)), "ratio": rnd(p.get("hdr_ratio", 1.0)),
                        "release": rnd(p.get("hdr_release", 0.0)), "exp_release": bool(o["hdr_bits"] & 2)}
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


def write_version():
    """version.txt: a hash of everything the browser loads, appended to its URLs (cache busting)."""
    h = hashlib.sha1()
    for dp, _, fns in sorted(os.walk(OUT)):
        for fn in sorted(fns):
            if fn.endswith(".meta") or fn == "version.txt":
                continue
            path = os.path.join(dp, fn)
            st = os.stat(path)
            h.update(f"{os.path.relpath(path, OUT)}:{st.st_size}:{st.st_mtime_ns}".encode())
    with open(os.path.join(OUT, "version.txt"), "w") as f:
        f.write(h.hexdigest()[:12])


def float_wav_to_pcm16(src, dst):
    """measure_ir.py's float WAV -> 16-bit PCM scaled to full scale; returns the scale (the original peak)."""
    data = open(src, "rb").read()
    channels, rate = struct.unpack_from("<HI", data, 22)
    n = struct.unpack_from("<I", data, 40)[0] // 4
    samples = struct.unpack_from(f"<{n}f", data, 44)
    peak = max(abs(v) for v in samples) or 1.0
    pcm = struct.pack(f"<{n}h", *(int(round(v / peak * 32767)) for v in samples))
    header = b"RIFF" + struct.pack("<I", 36 + len(pcm)) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 1, channels, rate, rate * 2 * channels, 2 * channels, 16)
    with open(dst, "wb") as f:
        f.write(header + b"data" + struct.pack("<I", len(pcm)) + pcm)
    return peak


def hdr_envelope(wem_path, rate):
    """The loudness envelope Wwise's HDR tracks, from the WEM's 'akd ' chunk: [[seconds, dB relative to
    the loudest point]...]. Chunk: f32, f32, u32 count, f32 peak dB, count x (u32 sample, u16 attenuation dB)."""
    data = open(wem_path, "rb").read()
    p = 12
    while p + 8 <= len(data):
        tag, n = data[p:p + 4], struct.unpack_from("<I", data, p + 4)[0]
        if tag == b"akd ":
            count = struct.unpack_from("<I", data, p + 16)[0]
            peak = struct.unpack_from("<f", data, p + 20)[0]
            if 16 + 6 * count > n:
                return None
            points = []
            for i in range(count):
                pos, att = struct.unpack_from("<IH", data, p + 24 + 6 * i)
                points.append([round(pos / rate, 3), round(-att - peak, 1)])
            return points
        p += 8 + n + (n & 1)
    return None


def read_names(files=None):
    """id -> name from Wwise's SoundBank .txt tables (the game's Init.txt and SFX.txt by default)."""
    names = {}
    for path in files or [os.path.join(BANKS, f) for f in ("Init.txt", "SFX.txt")]:
        section = None
        for line in open(path, encoding="latin1"):
            parts = line.rstrip("\n").split("\t")
            if parts and parts[0]:
                section = parts[0]
                continue
            if len(parts) > 2 and parts[1].isdigit() and section in ("Event", "Switch Group", "State Group", "Switch", "State", "Game Parameter", "Audio Bus", "Auxiliary Bus", "Trigger"):
                names.setdefault(int(parts[1]), parts[2])
    return names


def write_media(metas, kinds, out, stream_over=None):
    """Encoded media -> sfx<N>.pack (sound effects, concatenated; the engine loads every pack at start) and
    music/<id>.ogg (fetched when first played) in out; the media index. stream_over: seconds above which a sound
    effect goes to its own file too (mods play their music as plain sounds)."""
    os.makedirs(os.path.join(out, "music"), exist_ok=True)
    media = {}
    packs, pack, pack_size = [], None, 0
    for mid in sorted(metas):
        meta = metas[mid]
        src = meta["file"]
        entry = {"rate": meta["rate"], "channels": meta["channels"], "samples": meta["samples"]}
        if "loop_start" in meta:
            entry["loop"] = [meta["loop_start"], meta["loop_end"]]
        env = hdr_envelope(os.path.join(CACHE, "wem", f"{mid}.wem"), meta["rate"])
        if env:
            entry["env"] = env
        if kinds.get(mid) == "music" or stream_over and meta["samples"] / meta["rate"] > stream_over:
            dst = os.path.join(out, "music", f"{mid}.ogg")
            if not os.path.exists(dst) or os.path.getsize(dst) != os.path.getsize(src):
                shutil.copyfile(src, dst)
            entry["file"] = f"music/{mid}.ogg"
        else:
            data = open(src, "rb").read()
            if pack is None or pack_size + len(data) > PACK_SIZE:
                pack = open(os.path.join(out, f"sfx{len(packs)}.pack"), "wb")
                packs.append(f"sfx{len(packs)}.pack")
                pack_size = 0
            pack.write(data)
            entry["pack"] = [len(packs) - 1, pack_size, len(data)]
            pack_size += len(data)
        media[str(mid)] = entry
    if pack:
        pack.close()
    return media, packs


def export_objects(objects):
    """events, nodes and effects of audio.json from parsed bank objects."""
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
            elif o["plugin"] == "delay":
                e.update(wwbank.delay(o["params"]))
            effects[str(oid)] = e
    return events, nodes, effects


def build_mods(banks_dir, args):
    """The baked mods' banks (mods/mod_bundles.py extracts them to <banks_dir>/<package>/*.bnk) -> OUT/mods/<package>/
    (audio.json, packs, music: same format, ids are the banks' own) and OUT/mods.json, the list the engine loads
    and merges into the game's data at start. Mod banks route into the game's busses, which keep their ids.
    No banks (a vanilla build): both go."""
    mods_out = os.path.join(OUT, "mods")
    if os.path.isdir(mods_out):
        shutil.rmtree(mods_out)
    listed = []
    for pkg in sorted(os.listdir(banks_dir)) if os.path.isdir(banks_dir) else []:
        files = sorted(f for f in os.listdir(os.path.join(banks_dir, pkg)) if f.endswith(".bnk"))
        banks = [wwbank.parse_bank(os.path.join(banks_dir, pkg, f)) for f in files]
        objects = {k: v for b in banks for k, v in b["objects"].items()}
        kinds = {s["id"]: "music" for o in objects.values() if o["type"] == "mtrack" for s in o["sources"]}
        print(f"== {pkg}: {', '.join(files)}")
        metas = {}
        for b in banks:
            metas.update(convert_media(b, kinds, args.jobs, args.force))
        out = os.path.join(mods_out, pkg)
        media, packs = write_media(metas, kinds, out, stream_over=5)
        events, nodes, effects = export_objects(objects)
        tables = [os.path.join(banks_dir, pkg, f[:-4] + ".txt") for f in files]
        names = read_names([t for t in tables if os.path.exists(t)]) if any(os.path.exists(t) for t in tables) else {}
        doc = {"version": 1, "effects": effects, "packs": packs, "media": media, "events": events, "nodes": nodes,
               "names": {str(k): v for k, v in names.items()}}
        with open(os.path.join(out, "audio.json"), "w", encoding="utf-8") as f:
            json.dump(doc, f, separators=(",", ":"))
        size = sum(os.path.getsize(os.path.join(dp, fn)) for dp, _, fns in os.walk(out) for fn in fns)
        streamed = sum(1 for m in media.values() if "file" in m)
        print(f"   {len(events)} events, {len(nodes)} nodes, {len(packs)} packs, {streamed} files fetched when played, {size / 1e6:.1f} MB")
        listed.append(f"mods/{pkg}/")
    shutil.copyfile(os.path.join(PORT, "audio", "etg-audio.js"), os.path.join(OUT, "etg-audio.js"))  # loads mods.json
    mods_json = os.path.join(OUT, "mods.json")
    if listed:
        with open(mods_json, "w", encoding="utf-8") as f:
            json.dump(listed, f)
    elif os.path.exists(mods_json):
        os.remove(mods_json)
    write_version()
    if os.path.isdir(os.path.dirname(WEB_OUT)):
        print("== Mirroring to", WEB_OUT)
        shutil.rmtree(os.path.join(WEB_OUT, "mods"), ignore_errors=True)
        if os.path.isdir(mods_out):
            shutil.copytree(mods_out, os.path.join(WEB_OUT, "mods"), ignore=shutil.ignore_patterns("*.meta"))
        for name in ("mods.json", "version.txt", "etg-audio.js"):
            if os.path.exists(os.path.join(WEB_OUT, name)):
                os.remove(os.path.join(WEB_OUT, name))
            if os.path.exists(os.path.join(OUT, name)):
                shutil.copyfile(os.path.join(OUT, name), os.path.join(WEB_OUT, name))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--jobs", type=int, default=os.cpu_count())
    ap.add_argument("--force", action="store_true", help="re-encode all media")
    ap.add_argument("--mods", metavar="BANKS_DIR", help="only (re)build the mods' audio from <BANKS_DIR>/<package>/*.bnk")
    args = ap.parse_args()
    if args.mods is not None:
        build_mods(args.mods, args)
        return

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
    media, packs = write_media(metas, kinds, OUT)
    events, nodes, effects = export_objects(objects)

    # Bus reverbs measured in the real Wwise by measure_ir.py (port/build/audio/ir/<bus>.wav).
    reverbs = {}
    ir_dir = os.path.join(CACHE, "ir")
    if os.path.isdir(ir_dir):
        os.makedirs(os.path.join(OUT, "ir"), exist_ok=True)
        for fn in sorted(os.listdir(ir_dir)):
            if fn.endswith(".wav") and not fn.startswith("fx"):
                shutil.copyfile(os.path.join(ir_dir, fn), os.path.join(OUT, "ir", fn))
                reverbs[fn[:-4]] = f"ir/{fn}"
    if not reverbs:
        print("   no measured bus reverbs (port/build/audio/ir) - run measure_ir.py; sound effects will be dry")

    # Effects on sounds, measured by measure_fx.py: reverb tails (ir/fx<id>.wav, shipped as 16-bit, peak
    # normalized: ir_gain restores the level) and the fitted models of the other plugins (fx_models.json).
    fx_irs = os.path.join(ir_dir, "fx.json")
    if os.path.exists(fx_irs):
        for fid, fn in json.load(open(fx_irs)).items():
            if fid in effects:
                effects[fid].update(ir=f"ir/{fn}", ir_gain=round(float_wav_to_pcm16(os.path.join(ir_dir, fn), os.path.join(OUT, "ir", fn)), 6))
    fx_models = os.path.join(ir_dir, "fx_models.json")
    if os.path.exists(fx_models):
        for fid, m in json.load(open(fx_models)).items():
            e = effects.get(fid)
            if not e or "none" in m:
                continue
            if e["plugin"] == "guitar_distortion":
                e["model"] = m
            else:
                e.update(m)
    if not (os.path.exists(fx_irs) and os.path.exists(fx_models)):
        print("   no measured effects on sounds (port/build/audio/ir/fx*.json) - run measure_fx.py render, ir, fit")

    doc = {"version": 1, "settings": init["settings"], "effects": effects, "reverbs": reverbs, "packs": packs, "media": media, "events": events, "nodes": nodes,
           "names": {str(k): v for k, v in read_names().items()}}
    with open(os.path.join(OUT, "audio.json"), "w", encoding="utf-8") as f:
        json.dump(doc, f, separators=(",", ":"))
    for name in ("etg-audio.js", "soundboard.html"):
        shutil.copyfile(os.path.join(PORT, "audio", name), os.path.join(OUT, name))
    for name in ("Init.txt", "SFX.txt"):  # the soundboard lists switch/state names from these
        shutil.copyfile(os.path.join(BANKS, name), os.path.join(OUT, name))
    write_version()

    total = sum(os.path.getsize(os.path.join(dp, fn)) for dp, _, fns in os.walk(OUT) for fn in fns if not fn.endswith(".meta"))
    print(f"   {len(events)} events, {len(nodes)} nodes, {len(packs)} packs, {total / 1e6:.1f} MB total")

    if os.path.isdir(os.path.dirname(WEB_OUT)):
        print("== Mirroring to", WEB_OUT)
        shutil.copytree(OUT, WEB_OUT, dirs_exist_ok=True, ignore=shutil.ignore_patterns("*.meta"))


if __name__ == "__main__":
    main()
