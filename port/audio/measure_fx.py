"""Measures the effects on sounds and containers (not busses) in EtG's real Wwise, for the browser engine.

    python port/audio/measure_fx.py render   # Wwise renders: each effect node alone, with and without its effects
    python port/audio/measure_fx.py ir       # Matrix Reverb / RoomVerb -> port/build/audio/ir/fx<id>.wav + fx.json
    python port/audio/measure_fx.py fit      # distortion, flanger, tremolo, harmonizer -> ir/fx_models.json
    python port/audio/measure_fx.py check    # the engine's models against the renders (after build_audio.py)
    options: --plugin NAME (only effects of that plugin), --repeats N, --jobs N (check: browser tabs),
             --effect ID,... (fit: only these effects)

The harness (port/WwiseRef) plays every node that carries an effect on its own (playNode: a DynamicSequence, so
the other layers of an event don't mix in) twice: as is ("wet") and with the node effects taken off
(SetActorMixerEffect, "dry"). Every bus effect is off in both, so the two renders differ only by the node
effect: same seeds -> same random choices, and the gain after the effect (voice volume, HDR, busses) is the
same in both. The sections below say how each plugin is measured; port/README.md (Sound) has the results.
"""
import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
PORT_DIR = HERE.parent
ROOT = PORT_DIR.parent
sys.path.insert(0, str(HERE))
import audiodiff_cases  # noqa: E402
import audiodiff_features  # noqa: E402
import build_audio  # noqa: E402
import measure_ir  # noqa: E402

RATE = 48000
WORK = PORT_DIR / "build" / "audio" / "fx_work"
LINEAR = ("matrix_reverb", "roomverb")
SKIPPED = ("parametric_eq", "delay")  # modelled from their parameters since the first version
TAIL = {"matrix_reverb": 4.0, "roomverb": 4.0, "flanger": 1.0}
VOL_SFX = 10  # with the master's limiter off, 100 clips the 16-bit capture; this is linear gain after the effects


def kids(n):
    if n["type"] == "switch":
        return [c for ks in n["switches"].values() for c in ks]
    return n.get("children", [])


def reached_sounds(nodes, nid, switches, out, seen):
    """Sounds a play of nid can reach with the given switch/state values (random containers: all children)."""
    if nid in seen:
        return
    seen.add(nid)
    n = nodes.get(str(nid))
    if not n:
        return
    if n["type"] == "sound":
        out.add(nid)
        return
    if n["type"] == "switch":
        v = switches.get(n["group"], 0) or n["default"]
        for c in n["switches"].get(str(v), []):
            reached_sounds(nodes, c, switches, out, seen)
        return
    for c in kids(n):
        reached_sounds(nodes, c, switches, out, seen)


def actor_chain(nodes, nid):
    out = []
    while nid and str(nid) in nodes and nodes[str(nid)]["type"] not in ("bus", "auxbus"):
        out.append(nid)
        nid = nodes[str(nid)].get("parent", 0)
    return out


def node_effects(doc, plugin=None):
    """[(node, index, effect id, plugin)] of the effects on sounds/containers the engine has to model."""
    out = []
    for nid, n in doc["nodes"].items():
        if n["type"] in ("bus", "auxbus"):
            continue
        for index, fid in n.get("fx", []):
            p = doc["effects"].get(str(fid), {}).get("plugin")
            if p and p not in SKIPPED and (plugin is None or p == plugin):
                out.append((int(nid), index, fid, p))
    return sorted(out)


def node_cases(doc, repeats, plugin=None):
    """One case per effect node (repeats with other seeds): the node played on its own, without the event and
    the layers around it (WwiseRef's playNode). Tagged with the effects on the way: {node: [fx ids]}."""
    nodes = doc["nodes"]
    fx_nodes = {}
    for nid, index, fid, p in node_effects(doc):
        fx_nodes.setdefault(nid, []).append(fid)
    bank = audiodiff_cases.Bank(doc)
    cases = []
    for nid, fids in sorted(fx_nodes.items()):
        plugins = {doc["effects"][str(f)]["plugin"] for f in fids}
        if plugin and plugin not in plugins:
            continue
        length = bank.length(nid)
        length = audiodiff_cases.LOOP_SECONDS if length == float("inf") else min(length + 0.2, audiodiff_cases.MAX_SECONDS)
        fx = {str(n): fx_nodes[n] for n in actor_chain(nodes, nid) if n in fx_nodes}
        for r in range(repeats):
            cases.append({"id": f"{len(cases):05d}", "kind": "node", "name": f"{'+'.join(sorted(plugins))} {nid}", "rep": r,
                          "seed": 1000 + r, "fresh": True, "fx": fx,
                          "duration": round(length + max(TAIL.get(p, 0.5) for p in plugins), 3),
                          "steps": [{"t": 0, "op": "playNode", "args": [nid, audiodiff_cases.OBJ, 0.0, 0.0, 0.0]}]})
    _, reset = audiodiff_cases.make_cases(doc, 1)
    return cases, reset


def bus_effects(doc):
    return [(int(b), i) for b, n in doc["nodes"].items() if n["type"] in ("bus", "auxbus") for i, _ in n.get("fx", [])]


def run_wwise(cases_file, out_dir, remove_bus, remove_node):
    shutil.rmtree(out_dir, ignore_errors=True)
    args = [str(PORT_DIR / "build" / "WwiseRef" / "WwiseRef.exe"), str(ROOT), str(cases_file), str(out_dir)]
    if remove_bus:
        args += ["--remove-fx", ",".join(f"{b}:{i}" for b, i in remove_bus)]
    if remove_node:
        args += ["--remove-node-fx", ",".join(f"{n}:{i}" for n, i in remove_node)]
    subprocess.run(args, check=True, stdout=subprocess.DEVNULL)


def load_doc():
    return json.loads((Path(build_audio.OUT) / "audio.json").read_text(encoding="utf-8"))


def cmd_render(args):
    doc = load_doc()
    cases, reset = node_cases(doc, args.repeats, args.plugin)
    WORK.mkdir(parents=True, exist_ok=True)
    reset["rtpcs"] = [[r, VOL_SFX if r == audiodiff_cases.fnv("VOL_SFX") else v] for r, v in reset["rtpcs"]]
    (WORK / "cases.json").write_text(json.dumps({"reset": reset, "cases": cases}), encoding="utf-8")
    print(f"   {len(cases)} cases, {sum(c['duration'] for c in cases) / 60:.1f} min of audio")
    measure_ir.build_audio_ref()
    busfx = bus_effects(doc)
    nodefx = [(n, i) for n, i, _, _ in node_effects(doc)]
    run_wwise(WORK / "cases.json", WORK / "wet", busfx, [])
    run_wwise(WORK / "cases.json", WORK / "dry", busfx, nodefx)


def read(path):
    return audiodiff_features.read_wav(path.read_bytes()).astype(np.float64)


def pairs_for(fid, cases, wet_dir=None, dry_dir=None):
    """(case, wet, dry) of the cases that go through effect fid only, where the effect makes a difference."""
    wet_dir, dry_dir = wet_dir or WORK / "wet", dry_dir or WORK / "dry"
    out = []
    for c in cases:
        fxs = [f for fl in c["fx"].values() for f in fl]
        if fxs != [fid]:
            continue
        wet, dry = read(wet_dir / f"{c['id']}.wav"), read(dry_dir / f"{c['id']}.wav")
        n = min(len(wet), len(dry))
        wet, dry = wet[:n], dry[:n]
        if np.abs(dry).max() < 1e-3:
            continue
        out.append((c, wet, dry))
    return out


# --- reverbs: energy decay per band ----------------------------------------------------------
# A Wiener estimate (measure_ir.py) only captures what is coherent with the input. For short sounds it
# fits well, but on sustained ones (wall_reveal, beholster_death: seconds of noise-like sound) wet - dry is
# almost uncorrelated with dry at any lag: the tails are modulated. Their energy is what is heard, so it
# is fitted instead: per octave band b and 10 ms block t,
#     P_rev(t, b) = sum_k P_dry(t - k, b) * E_b(k),   E_b(k) = A_b * 10^(-6 (k - k0) / T_b)  (k >= k0)
# with P the block powers of dry and of wet - dry (the reverb alone: both have 0 dB dry level), one
# predelay k0 for all bands and a level A_b / decay time T_b per band. The response shipped is that
# envelope over band-limited noise (independent per channel); the direct sound is the voice itself.

BLOCK = 480                                                     # 10 ms
BAND_EDGES = [0, 125, 250, 500, 1000, 2000, 4000, 8000, 16000, 24001]
DECAYS = np.geomspace(0.05, 10.0, 64)                            # T (s, -60 dB)
MAX_PREDELAY = 25                                                # blocks


def band_powers(x):
    """(blocks, bands, channels) mean power per 10 ms block and octave band."""
    n = len(x) // BLOCK
    frames = x[:n * BLOCK].reshape(n, BLOCK, 2) * np.hanning(BLOCK)[None, :, None]
    spec = np.abs(np.fft.rfft(frames, 1024, axis=1)) ** 2 / (BLOCK * np.mean(np.hanning(BLOCK) ** 2)) / 512
    freqs = np.fft.rfftfreq(1024, 1 / RATE)
    return np.stack([spec[:, (freqs >= lo) & (freqs < hi)].sum(axis=1)
                     for lo, hi in zip(BAND_EDGES[:-1], BAND_EDGES[1:])], axis=1)


def decayed(px):
    """y_T(t) = sum_k px(t - k) * 10^(-6 k BLOCK / (T RATE)) for every T in DECAYS: (T, blocks, bands, ch)."""
    d = 10 ** (-6 * BLOCK / RATE / DECAYS)[:, None, None]
    y = np.zeros((len(DECAYS),) + px.shape)
    acc = np.zeros((len(DECAYS),) + px.shape[1:])
    for t in range(len(px)):
        acc = acc * d + px[t]
        y[:, t] = acc
    return y


def fit_decay(pairs):
    """Fits k0, A_b, T_b to [(wet, dry)...]; returns (k0, A, T, per-case log error dB)."""
    data = []
    for wet, dry in pairs:
        px, pr = band_powers(dry), band_powers(wet - dry)
        data.append((px, pr, decayed(px)))
    nb = len(BAND_EDGES) - 1
    best = None
    for k0 in range(MAX_PREDELAY + 1):
        A = np.zeros(nb)
        T = np.zeros(nb)
        total = 0.0
        for b in range(nb):
            num = np.zeros(len(DECAYS))
            den = np.zeros(len(DECAYS))
            for px, pr, y in data:
                m = np.zeros_like(y[:, :, b])
                m[:, k0:] = y[:, :len(y[0]) - k0, b]
                num += (m * pr[None, :, b]).sum(axis=(1, 2))
                den += (m * m).sum(axis=(1, 2))
            amp = num / np.maximum(den, 1e-30)
            err = np.zeros(len(DECAYS))
            for px, pr, y in data:
                m = np.zeros_like(y[:, :, b])
                m[:, k0:] = y[:, :len(y[0]) - k0, b]
                floor = max(pr[:, b].max() * 1e-6, 1e-12)
                err += ((10 * np.log10(pr[None, :, b] + floor) - 10 * np.log10(amp[:, None, None] * m + floor)) ** 2).mean(axis=(1, 2))
            i = int(np.argmin(err))
            A[b], T[b] = amp[i], DECAYS[i]
            total += err[i]
        if best is None or total < best[0]:
            best = (total, k0, A, T)
    _, k0, A, T = best
    # bands the sounds barely reach (< -30 dB of their strongest band) take their nearest measured neighbour's fit
    energy = np.array([sum(px[:, b].sum() for px, _, _ in data) for b in range(nb)])
    known = np.nonzero(energy >= energy.max() * 1e-3)[0]
    for b in range(nb):
        if b not in known:
            j = known[np.argmin(np.abs(known - b))]
            A[b], T[b] = A[j], T[j]
    return k0, A, T, data


def synth_ir(k0, A, T, seconds, seed=1):
    """Band-limited white noise under the fitted envelopes: |H(f)|^2 in band b is sum_k E_b(k)."""
    n = int(seconds * RATE)
    rng = np.random.default_rng(seed)
    t = np.arange(n) / RATE
    start = k0 * BLOCK / RATE
    out = np.zeros((n, 2))
    freqs = np.fft.rfftfreq(n, 1 / RATE)
    for ch in range(2):
        white = np.fft.rfft(rng.standard_normal(n))
        for b, (lo, hi) in enumerate(zip(BAND_EDGES[:-1], BAND_EDGES[1:])):
            band = np.fft.irfft(np.where((freqs >= lo) & (freqs < hi), white, 0), n)
            env = np.where(t >= start, A[b] * 10 ** (-6 * (t - start) / T[b]), 0.0)  # energy per block
            out[:, ch] += band * np.sqrt(env / BLOCK)
    fade = int(0.05 * RATE)
    out[-fade:] *= np.linspace(1, 0, fade)[:, None]
    return out.astype(np.float32)


def model_error(data, k0, A, T):
    """Per case: energy of the predicted reverb vs. the measured one (dB), and the mean log error per block."""
    out = []
    for px, pr, y in data:
        pred = np.zeros_like(pr)
        for b in range(len(A)):
            i = int(np.argmin(np.abs(DECAYS - T[b])))
            pred[k0:, b] = A[b] * y[i, :len(pr) - k0, b]
        floor = max(pr.max() * 1e-6, 1e-12)
        out.append((10 * np.log10(pred.sum() / max(pr.sum(), 1e-30)),
                    float(np.sqrt(((10 * np.log10(pr + floor) - 10 * np.log10(pred + floor)) ** 2).mean()))))
    return out


def cmd_ir(args):
    """The reverbs on sounds: one response per distinct parameter block (several effects share one)."""
    doc = load_doc()
    cases = json.loads((WORK / "cases.json").read_text(encoding="utf-8"))["cases"]
    groups = {}
    for nid, index, fid, p in node_effects(doc):
        if p in LINEAR:
            groups.setdefault(params_key(doc, fid), []).append(fid)
    measure_ir.OUT.mkdir(parents=True, exist_ok=True)
    index = {}
    for key, fids in sorted(groups.items()):
        pairs = [(w, d) for fid in fids for _, w, d in pairs_for(fid, cases)]
        print(f"== {doc['effects'][str(fids[0])]['plugin']} {', '.join(map(str, fids))}: {len(pairs)} cases")
        if not pairs:
            continue
        k0, A, T, data = fit_decay(pairs)
        per_band = ", ".join(f"{lo}: {10 * np.log10(a * (1 - 10 ** (-6 * BLOCK / RATE / t)) ** -1 + 1e-30):.0f} dB/{t:.2f} s"
                             for lo, a, t in zip(BAND_EDGES, A, T))
        print(f"   predelay {k0 * BLOCK / RATE * 1000:.0f} ms; band from Hz: total energy / T60: {per_band}")
        for (lvl, err), (w, d) in zip(model_error(data, k0, A, T), pairs):
            print(f"   reverb energy model/measured {lvl:+.1f} dB, block log error {err:.1f} dB")
        length = min(3.0, k0 * BLOCK / RATE + max(T[A > 0])) if (A > 0).any() else 0.1
        h = synth_ir(k0, A, T, max(length, 0.1))
        name = f"fx{fids[0]}.wav"
        measure_ir.write_float_wav(measure_ir.OUT / name, h)
        for fid in fids:
            index[str(fid)] = name
    (measure_ir.OUT / "fx.json").write_text(json.dumps(index, indent=1), encoding="utf-8")


# --- nonlinear and modulated effects: models fitted to the renders -----------------------------
# Each effect node is rendered on its own (playNode) with and without its effect, all bus effects off. Its
# input is then dry / G, G being the (constant) gain after the effect: voice volume, busses. G is the ratio
# of the dry render's RMS to the source media's (the media decoded by vgmstream, as build_audio does).
# The structure of each model was established on these renders (port/README.md); per effect instance its
# free constants are fitted by Nelder-Mead on the sample-level residual  |wet/G - model(dry/G)|^2.

def nelder_mead(f, x0, step, iters=300):
    pts = [np.array(x0, float)] + [np.array(x0, float) + np.eye(len(x0))[i] * step[i] for i in range(len(x0))]
    vals = [f(p) for p in pts]
    for _ in range(iters):
        order = np.argsort(vals)
        pts, vals = [pts[i] for i in order], [vals[i] for i in order]
        c = np.mean(pts[:-1], axis=0)
        xr = c + (c - pts[-1])
        fr = f(xr)
        if fr < vals[0]:
            xe = c + 2 * (c - pts[-1])
            fe = f(xe)
            pts[-1], vals[-1] = (xe, fe) if fe < fr else (xr, fr)
        elif fr < vals[-2]:
            pts[-1], vals[-1] = xr, fr
        else:
            xc = c + 0.5 * (pts[-1] - c)
            fc = f(xc)
            if fc < vals[-1]:
                pts[-1], vals[-1] = xc, fc
            else:
                pts = [pts[0]] + [pts[0] + 0.5 * (p - pts[0]) for p in pts[1:]]
                vals = [vals[0]] + [f(p) for p in pts[1:]]
    i = int(np.argmin(vals))
    return pts[i], vals[i]


def one_pole(x, fc, kind):
    """First-order bilinear low/high-pass (the engine's IIRFilterNode), applied in the frequency domain."""
    n = len(x)
    nfft = 1 << int(np.ceil(np.log2(n + 8192)))
    w = np.tan(np.pi * min(fc, RATE * 0.49) / RATE)
    z = np.exp(-2j * np.pi * np.fft.rfftfreq(nfft))
    h = w * (1 + z) if kind == "lp" else (1 - z)
    return np.fft.irfft(np.fft.rfft(x, nfft) * h / ((1 + w) + (w - 1) * z), nfft)[:n]


def residual_db(y, parts):
    """Least-squares mix of `parts` against y; returns (residual energy relative to y in dB, coefficients)."""
    a = np.stack(parts, 1)
    if not np.all(np.isfinite(a)):
        return 1e9, None
    coef = np.linalg.lstsq(a, y, rcond=None)[0]
    return 10 * np.log10(((y - a @ coef) ** 2).sum() / (y ** 2).sum()), coef


_media = {}


def source(doc, case):
    """Decoded media of the (single) sound under the case's effect node."""
    nid = int(next(iter(case["fx"])))
    sounds = set()
    reached_sounds(doc["nodes"], nid, {}, sounds, set())
    mid = doc["nodes"][str(min(sounds))]["media"]
    if mid not in _media:
        wav = WORK / "media" / f"{mid}.wav"
        wav.parent.mkdir(exist_ok=True)
        if not wav.exists():
            subprocess.run([build_audio.VGMSTREAM, "-i", "-o", str(wav), str(Path(build_audio.CACHE) / "wem" / f"{mid}.wem")],
                           check=True, capture_output=True)
        import wave
        with wave.open(str(wav)) as w:
            _media[mid] = np.frombuffer(w.readframes(w.getnframes()), "<i2").astype(np.float64) / 32768
    return _media[mid]


def active_rms(x):
    a = np.abs(x)
    return np.sqrt((x[a > a.max() * 1e-2] ** 2).mean())


def effect_input(doc, fid, cases, seconds=1.0, count=2):
    """[(x, y)]: the effect's input and output (left channel) of up to `count` cases, the first `seconds`
    from the onset, both divided by the gain after the effect."""
    out = []
    for c, wet, dry in pairs_for(fid, cases):
        g = active_rms(dry[:, 0]) / active_rms(source(doc, c))
        a = np.abs(dry[:, 0])
        i0 = int(np.argmax(a > a.max() * 0.01))
        cut = slice(i0, i0 + int(seconds * RATE))
        out.append((dry[cut, 0] / g, wet[cut, 0] / g))
        if len(out) == count:
            break
    return out


# Guitar Distortion (Overdrive in all ten EtG instances; their pre/post EQ bands are all at 0 dB):
#   y = dry * x + wet * hp_post(lp_tone(shape(k * hp_pre(x)) / k)),  shape(v) = v / (1 + |v|^knee)^(1/knee),
# rectification blending |shape| into shape. Wwise's own output overshoots on the clipped edges (its
# oversampling filters); the rest is matched closely.
DIST_BOUNDS = np.array([[0, np.log(0.05), np.log(0.5), np.log(500), 0, -1],
                        [np.log(5000), np.log(500), np.log(40), np.log(23000), np.log(2000), 1]])


def distortion_model(x, prm):
    fa, lk, lp, ft, fb, r = np.clip(prm, *DIST_BOUNDS)
    k, p = np.exp(lk), np.exp(lp)
    v = k * one_pole(x, np.exp(fa), "hp")
    s = v / (1 + np.minimum(np.abs(v), 1e6) ** p) ** (1 / p)
    s = s + r * (np.abs(s) - s)
    return one_pole(one_pole(s / k, np.exp(ft), "lp"), np.exp(fb), "hp")


def fit_distortion(data):
    x, y = np.concatenate([a for a, _ in data]), np.concatenate([b for _, b in data])
    err = lambda prm: residual_db(y, [x, distortion_model(x, prm)])[0]  # noqa: E731
    best = None
    for start in ([np.log(20), np.log(30), np.log(4), np.log(8000), np.log(100), 0.0],
                  [np.log(5), np.log(4), np.log(4), np.log(15000), np.log(40), 0.2]):
        prm, v = nelder_mead(err, start, [0.7, 0.7, 0.7, 0.7, 0.7, 0.2])
        if best is None or v < best[1]:
            best = (np.clip(prm, *DIST_BOUNDS), v)
    prm, v = best
    _, (dry, wet) = residual_db(y, [x, distortion_model(x, prm)])
    linear = residual_db(y, [x])[0]
    fa, lk, lp, ft, fb, r = prm
    return {"hp_pre": round(float(np.exp(fa)), 1), "k": round(float(np.exp(lk)), 3), "knee": round(float(np.exp(lp)), 2),
            "tone": round(float(np.exp(ft))), "hp_post": round(float(np.exp(fb)), 1), "rect": round(float(r), 3),
            "dry": round(float(dry), 4), "wet": round(float(wet), 4)}, v, linear


# Flanger: Dattorro's universal comb, as Wwise's parameters name it (dry, feed-forward, feedback levels):
#   xh[n] = x[n] + fb * xh[n - D(n)],  y = (1 - mix) x + mix (dry xh[n] + ff xh[n - D(n)]),
#   D(n) = delay * (center + swing * lfo(freq t + phase)), linear interpolation.
# Delay, levels, rate, mix and the waveform are the bank's; center/swing/phase (and the rate within 5 %) are
# fitted. Three instances follow center = 1, swing = depth / 2 exactly (residual -54 to -62 dB).

def flanger_params(raw):
    import struct
    delay, dry, ff, fb, depth, freq = struct.unpack_from("<6f", raw, 0)
    wave = struct.unpack_from("<I", raw, 24)[0]
    smooth, pwm, phase, phase_mode, spread, out, mix = struct.unpack_from("<7f", raw, 28)
    return {"delay": delay, "dry": dry, "ff": ff, "fb": fb, "depth": depth, "freq": freq, "wave": wave,
            "smooth": smooth, "out": out, "mix": mix}


def lfo(phase, wave):
    """Wwise LFO waveforms: 0 sine, 1 triangle, 2 square, 3 saw up, 4 saw down."""
    ph = phase % 1.0
    return {0: np.sin(2 * np.pi * ph), 1: 1 - 4 * np.abs(ph - 0.5), 2: np.where(ph < 0.5, 1.0, -1.0),
            3: 2 * ph - 1, 4: 1 - 2 * ph}.get(wave, np.sin(2 * np.pi * ph))


def comb(x, d, dry, ff, fb):
    """The universal comb with a delay per sample (in samples), in blocks no longer than the shortest delay."""
    n = len(x)
    xh = np.zeros(n)
    pos = np.arange(n) - d
    i0 = np.floor(pos).astype(int)
    frac = pos - i0
    step = max(1, int(np.floor(d.min())))
    tap = np.zeros(n)
    for t in range(0, n, step):
        e = min(n, t + step)
        a, f = i0[t:e], frac[t:e]
        v0 = np.where(a >= 0, xh[np.clip(a, 0, n - 1)], 0.0)
        v1 = np.where((a + 1 >= 0) & (a + 1 < t), xh[np.clip(a + 1, 0, n - 1)], 0.0)
        tap[t:e] = v0 * (1 - f) + v1 * f
        xh[t:e] = x[t:e] + fb * tap[t:e]
    return dry * xh + ff * tap


def fit_flanger(data, p):
    x, y = data[0]
    x, y = x[:int(0.75 * RATE)], y[:int(0.75 * RATE)]
    t = np.arange(len(x)) / RATE
    mix = p["mix"] / 100

    def model(prm):
        center, swing, phase, freq = prm
        d = np.maximum(p["delay"] * RATE / 1000 * (center + swing * lfo(freq * t + phase, p["wave"])), 1.0)
        return (1 - mix) * x + mix * comb(x, d, p["dry"], p["ff"], p["fb"])

    def err(prm):
        if abs(prm[3] / p["freq"] - 1) > 0.05:
            return 1e9
        return residual_db(y, [model(prm)])[0]

    depth = p["depth"] / 100
    starts = [(c, s * sign, ph) for c, s in ((1, depth / 2), (1 - depth / 2, depth / 2), (1, depth), (0.85, 0.95 * depth),
                                            (0.6, 0.65 * depth))
              for sign in (1, -1) for ph in np.linspace(0, 1, 12, endpoint=False)]
    best = min(((err([c, s, ph, p["freq"]]), [c, s, ph, p["freq"]]) for c, s, ph in starts), key=lambda e: e[0])
    prm, v = nelder_mead(err, best[1], [0.05, 0.05, 0.02, 0.01 * p["freq"]], iters=150)
    if v > best[0]:
        prm, v = best[1], best[0]
    plain = residual_db(y, [x])[0]
    return {"center": round(float(prm[0]), 4), "swing": round(float(prm[1]), 4), "freq": round(float(prm[3]), 4),
            "phase": round(float(prm[2] % 1), 3)}, v, plain


# Tremolo: wet = dry * g(t), g periodic: the bank's LFO (saw up here) through its smoothing, measured
# directly as wet/dry and folded over the period (phase from the voice's start), 64 points.
def fit_tremolo(data, p_freq):
    gains, phases = [], []
    for x, y in data:
        on = int(np.argmax(np.abs(x) > np.abs(x).max() * 1e-3))
        m = np.nonzero(np.abs(x) > np.abs(x).max() * 0.05)[0]
        m = m[m > on + RATE // 20]  # past the first period: the smoother starts from another state
        gains.append(y[m] / x[m])
        phases.append((m - on) / RATE)
    g, t = np.concatenate(gains), np.concatenate(phases)
    best = None
    for freq in p_freq * np.linspace(0.99, 1.01, 81):
        bins = ((t * freq % 1) * 64).astype(int)
        table = np.bincount(bins, g, 64) / np.maximum(np.bincount(bins, minlength=64), 1)
        e = np.sqrt(((g - table[bins]) ** 2).mean())
        if best is None or e < best[0]:
            best = (e, freq, table)
    e, freq, table = best
    return {"freq": round(float(freq), 4), "table": [round(float(v), 4) for v in table]}, e


# Harmonizer: dry and the pitch-shifted voices all come 'window' samples late (sync dry). The engine plays
# the voices as extra copies of the source at another playback rate that follow the dry's envelope; their
# level is calibrated here to Wwise's output energy: a voice at 0 cents adds to the dry coherently, the
# others as uncorrelated signals of the dry's energy.
def fit_harmonizer(data, raw):
    import struct
    voices = [struct.unpack_from("<B2f", raw, 25 * i) for i in range(2)]
    _, dry_db, wet_db, window = struct.unpack_from("<IffI", raw, 50)
    dry = 10 ** (dry_db / 20)
    want = np.mean([(y ** 2).sum() / (x ** 2).sum() for x, y in data])
    wet = 10 ** (wet_db / 20)
    unison = sum(wet * 10 ** (g / 20) for on, c, g in voices if on and c == 0)
    others = sum((wet * 10 ** (g / 20)) ** 2 for on, c, g in voices if on and c != 0)
    # (dry + c unison)^2 + c^2 others = want
    a, b, k = unison ** 2 + others, 2 * dry * unison, dry ** 2 - want
    calib = float((-b + np.sqrt(max(b * b - 4 * a * k, 0))) / (2 * a)) if a else 0.0
    return {"latency": window, "dry": round(dry_db, 2), "wet": round(wet_db, 2),
            "voices": [[c, round(g, 2)] for on, c, g in voices if on], "calib": round(min(max(calib, 0.25), 2.0), 3)}, want


def cmd_fit(args):
    doc = load_doc()
    cases = json.loads((WORK / "cases.json").read_text(encoding="utf-8"))["cases"]
    params_key(doc, next(fid for _, _, fid, _ in node_effects(doc)))
    only = {int(f) for f in args.effect.split(",")} if args.effect else None
    models = {}
    out_file = measure_ir.OUT / "fx_models.json"
    if out_file.exists():
        models = json.loads(out_file.read_text(encoding="utf-8"))
    done = set()
    for nid, index, fid, plugin in node_effects(doc, args.plugin):
        if fid in done or plugin in LINEAR or (only and fid not in only):
            continue
        done.add(fid)
        raw = bytes.fromhex(_params[fid])
        if plugin == "compressor":
            pairs = pairs_for(fid, cases)
            diff = max(10 * np.log10(((w - d) ** 2).sum() / (d ** 2).sum()) for _, w, d in pairs)
            print(f"== compressor {fid}: wet - dry at most {diff:.1f} dB of dry: no audible effect, left out")
            models[str(fid)] = {"none": round(float(diff), 1)}
            continue
        data = effect_input(doc, fid, cases, count=1 if plugin == "flanger" else 2)
        if not data:
            continue
        if plugin == "guitar_distortion":
            m, v, base = fit_distortion(data)
            print(f"== distortion {fid}: residual {v:.1f} dB (best linear {base:.1f} dB) {m}")
        elif plugin == "flanger":
            p = flanger_params(raw)
            m, v, base = fit_flanger(data, p)
            m.update({k: round(p[k], 4) for k in ("delay", "dry", "ff", "fb", "wave", "mix")})
            print(f"== flanger {fid}: residual {v:.1f} dB (dry only {base:.1f} dB) {m}")
        elif plugin == "tremolo":
            import struct
            m, e = fit_tremolo(data, struct.unpack_from("<f", raw, 4)[0])
            print(f"== tremolo {fid}: folded gain rms error {e:.3f}, rate {m['freq']} Hz")
        elif plugin == "harmonizer":
            m, want = fit_harmonizer(data, raw)
            print(f"== harmonizer {fid}: wet/dry energy {10 * np.log10(want):+.1f} dB, voice calibration {m['calib']}")
        else:
            continue
        models[str(fid)] = m
    out_file.write_text(json.dumps(models, indent=1), encoding="utf-8")
    print("  ", out_file)


def features(x):
    """audiodiff's features, with the end of the audible part relative to the loudest block only (no absolute
    floor): the engine's level on these renders is off by up to 10 dB with or without effects."""
    f, arrays = audiodiff_features.features(x)
    env = 10 * np.log10((10 ** (arrays["env_l"].astype(np.float64) / 10) + 10 ** (arrays["env_r"].astype(np.float64) / 10)) / 2)
    f["end"] = (np.nonzero(env > env.max() - audiodiff_features.ACTIVE_RANGE_DB)[0][-1] + 1) * audiodiff_features.BLOCK / RATE
    return f


def cmd_check(args):
    """The browser engine (working copy of etg-audio.js, audio.json as built) renders the same cases with and
    without its models of the effects on sounds; per effect: level, spectral centroid and end of the audible
    part, each against Wwise's render with the effect. The engine's own level errors (some sounds are off by
    10 dB with or without effects) are in both columns; what the effect model changes is the difference."""
    import audiodiff
    doc = load_doc()
    cases = json.loads((WORK / "cases.json").read_text(encoding="utf-8"))["cases"]
    if args.plugin:
        cases = [c for c in cases if args.plugin in c["name"]]
    for side, query in (("ours", "&busfx=off"), ("ours_nofx", "&busfx=off&nodefx=off")):  # Wwise's: bus effects off
        shutil.rmtree(WORK / side, ignore_errors=True)
        audiodiff.render_ours(WORK, [c["id"] for c in cases], args.jobs, wav_dir=WORK / side, query=query)
    rows = {}
    for c in cases:
        sides = ("dry", "wet", "ours_nofx", "ours")
        paths = [WORK / side / f"{c['id']}.wav" for side in sides]
        if not all(p.exists() for p in paths):
            continue
        x = [audiodiff_features.read_wav(p.read_bytes()) for p in paths]
        n = min(len(a) for a in x)  # Wwise's capture of a playNode case can end a few frames early
        rows.setdefault(c["name"], []).append({side: features(a[:n]) for side, a in zip(sides, x)})
    oct_ = lambda a, b: float(np.log2(max(b, 1) / max(a, 1)))  # noqa: E731
    metrics = (("rms", "level dB", lambda a, b: b - a), ("centroid", "centroid oct", oct_), ("end", "end s", lambda a, b: b - a))
    print(f"{'effect: change it makes, Wwise / ours':40s}" + "".join(f"{label:>18s}" for _, label, _ in metrics))
    errors = {"no model": [], "model": []}
    for name, fs in sorted(rows.items()):
        line, ew, eo = f"{name:40s}", [], []
        for key, _, fn in metrics:
            w = float(np.mean([fn(f["dry"][key], f["wet"][key]) for f in fs]))
            o = float(np.mean([fn(f["ours_nofx"][key], f["ours"][key]) for f in fs]))
            line += f"{w:+9.2f}{o:+9.2f}"
            ew.append(abs(w))
            eo.append(abs(o - w))
        errors["no model"].append(ew)
        errors["model"].append(eo)
        print(line)
    for side, v in errors.items():
        a = np.array(v)
        print(f"mean error of the change, {side}: level {a[:, 0].mean():.2f} dB, centroid {a[:, 1].mean():.3f} oct, end {a[:, 2].mean():.2f} s")


_params = {}


def params_key(doc, fid):
    """Plugin + raw parameter block: effects with the same key behave the same."""
    if not _params:
        for bank in ("Init.bnk", "SFX.bnk"):
            for oid, o in build_audio.wwbank.parse_bank(str(Path(build_audio.BANKS) / bank))["objects"].items():
                if o["type"] in ("fxshareset", "fxcustom"):
                    _params[oid] = o["params"].hex()
    return doc["effects"][str(fid)]["plugin"] + ":" + _params[fid]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("command", choices=("render", "ir", "fit", "check"))
    ap.add_argument("--plugin")
    ap.add_argument("--repeats", type=int, default=4)
    ap.add_argument("--jobs", type=int, default=4, help="browser tabs (check)")
    ap.add_argument("--effect", help="fit: only these effect ids (comma separated)")
    args = ap.parse_args()
    {"render": cmd_render, "ir": cmd_ir, "fit": cmd_fit, "check": cmd_check}[args.command](args)


if __name__ == "__main__":
    main()
