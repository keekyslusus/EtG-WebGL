"""Features of a rendered case, the same code for both sides of audiodiff.

Per 20 ms block: RMS of each channel (dB) and the spectral centroid of the mono mix (Hz). Per render:
overall RMS and peak (dB), onset / end of the audible part (s), energy-weighted centroid (Hz), and the
stereo balance (dB, + = right louder).
"""
import io
import wave

import numpy as np

RATE = 48000
BLOCK = 960            # 20 ms
FLOOR_DB = -120.0
SILENT_PEAK_DB = -80.0  # quieter than this counts as silence
ACTIVE_RANGE_DB = 50.0  # audible = blocks within this range of the loudest block


def read_wav(src):
    with wave.open(io.BytesIO(src) if isinstance(src, (bytes, bytearray)) else str(src)) as w:
        if w.getsampwidth() != 2 or w.getnchannels() != 2:
            raise ValueError(f"expected 16-bit stereo, got {w.getsampwidth() * 8}-bit x{w.getnchannels()}")
        x = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").reshape(-1, 2)
    return x.astype(np.float32) / 32768.0


def db(power):
    return np.maximum(10 * np.log10(np.maximum(power, 1e-30)), FLOOR_DB)


def features(x):
    n = len(x) // BLOCK
    blocks = x[:n * BLOCK].reshape(n, BLOCK, 2)
    pow_lr = (blocks ** 2).mean(axis=1)                     # (n, 2)
    env_l, env_r = db(pow_lr[:, 0]), db(pow_lr[:, 1])
    mono = blocks.mean(axis=2) * np.hanning(BLOCK)
    spec = np.abs(np.fft.rfft(mono, axis=1)) ** 2
    freqs = np.fft.rfftfreq(BLOCK, 1 / RATE)
    total = spec.sum(axis=1)
    centroid = np.where(total > 1e-12, (spec * freqs).sum(axis=1) / np.maximum(total, 1e-30), np.nan)

    power = pow_lr.mean(axis=1)
    peak = float(db(np.max(np.abs(x)) ** 2)) if len(x) else FLOOR_DB
    env = db(power)
    loud = env.max() if n else FLOOR_DB
    active = np.nonzero((env > loud - ACTIVE_RANGE_DB) & (env > SILENT_PEAK_DB))[0]
    f = {
        "rms": float(db(power.mean())) if n else FLOOR_DB,
        "peak": peak,
        "silent": peak < SILENT_PEAK_DB,
        "onset": float(active[0] * BLOCK / RATE) if len(active) else 0.0,
        "end": float((active[-1] + 1) * BLOCK / RATE) if len(active) else 0.0,
        "centroid": float(np.exp(np.nansum(np.log(np.maximum(centroid[active], 1)) * power[active]) / max(power[active].sum(), 1e-30)))
        if len(active) else 0.0,
        "balance": float(db(pow_lr[:, 1].sum()) - db(pow_lr[:, 0].sum())) if n else 0.0,
    }
    arrays = {"env_l": env_l.astype(np.float16), "env_r": env_r.astype(np.float16),
              "centroid_blocks": np.nan_to_num(centroid, nan=0).astype(np.float32)}
    return f, arrays


def save(path, src):
    """Reads a WAV (path or bytes) and writes its features to path (.npz)."""
    f, arrays = features(read_wav(src))
    np.savez_compressed(path, **arrays, **{k: np.array(v) for k, v in f.items()})


def load(path):
    with np.load(path) as z:
        out = {k: z[k] for k in z.files}
    for k in ("rms", "peak", "onset", "end", "centroid", "balance"):
        out[k] = float(out[k])
    out["silent"] = bool(out["silent"])
    return out
