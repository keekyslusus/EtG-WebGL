"""Reader for EtG's Wwise soundbanks (bank version 128, Wwise 2018.1).

Parses the HIRC section into plain dicts - only what a small playback engine needs, but every
object is read field by field to its end so a layout mistake shows up as a size mismatch
instead of silently wrong data (`parse_bank` raises on any object that doesn't end exactly
where its header says).
"""
import struct

HIRC_TYPES = {
    1: "state", 2: "sound", 3: "action", 4: "event", 5: "ranseq", 6: "switch", 7: "mixer",
    8: "bus", 9: "layer", 10: "mseg", 11: "mtrack", 12: "mswitch", 13: "mranseq",
    14: "attenuation", 15: "dialogue", 16: "fxshareset", 17: "fxcustom", 18: "auxbus",
    19: "lfo", 20: "envelope", 21: "audiodevice",
}

# AkPropID (v128). Only the ones the engine uses get names; the rest stay numeric.
PROPS = {
    0x00: "vol", 0x02: "pitch", 0x03: "lpf", 0x04: "hpf", 0x05: "busvol", 0x06: "makeup", 0x07: "priority",
    0x0F: "delay", 0x10: "transition", 0x11: "prob", 0x18: "outbusvol", 0x3A: "loop",
    0x3B: "idelay", 0x1B: "hdr_threshold", 0x1C: "hdr_ratio", 0x1D: "hdr_release",
}
INT_PROPS = {"transition", "delay", "loop"}


class Reader:
    def __init__(self, data, pos=0, end=None):
        self.d = data
        self.p = pos
        self.end = len(data) if end is None else end

    def _unpack(self, fmt, n):
        if self.p + n > self.end:
            raise ValueError(f"read past object end at {self.p:#x}")
        v = struct.unpack_from(fmt, self.d, self.p)[0]
        self.p += n
        return v

    def u8(self): return self._unpack("<B", 1)
    def u16(self): return self._unpack("<H", 2)
    def s16(self): return self._unpack("<h", 2)
    def u32(self): return self._unpack("<I", 4)
    def s32(self): return self._unpack("<i", 4)
    def f32(self): return self._unpack("<f", 4)
    def f64(self): return self._unpack("<d", 8)

    def var(self):
        """Wwise's 7-bit variable-length integer (big-endian groups)."""
        v = 0
        while True:
            b = self.u8()
            v = (v << 7) | (b & 0x7F)
            if not b & 0x80:
                return v

    def skip(self, n):
        if self.p + n > self.end:
            raise ValueError(f"skip past object end at {self.p:#x}")
        self.p += n


def read_chunks(data):
    chunks, p = {}, 0
    while p < len(data):
        tag = data[p:p + 4].decode("latin1")
        n = struct.unpack_from("<I", data, p + 4)[0]
        chunks[tag] = (p + 8, n)
        p += 8 + n
    return chunks


# --- shared blocks -------------------------------------------------------------------------

def prop_bundle(r):
    n = r.u8()
    ids = [r.u8() for _ in range(n)]
    out = {}
    for i in ids:
        name = PROPS.get(i, i)
        out[name] = r.s32() if name in INT_PROPS else r.f32()
    return out


def ranged_bundle(r):
    n = r.u8()
    ids = [r.u8() for _ in range(n)]
    out = {}
    for i in ids:
        out[PROPS.get(i, i)] = (r.f32(), r.f32())
    return out


def bank_source(r):
    plugin = r.u32()
    stream_type = r.u8()
    src = {"plugin": plugin, "stream": stream_type, "id": r.u32(), "size": r.u32(), "bits": r.u8()}
    if (plugin & 0x0F) == 2:  # source plugin (tone generator, silence...): its parameter block
        size = r.u32()
        r.skip(size)
        src["plugin_params"] = size
    return src


def fx_params(r):
    r.u8()  # bIsOverrideParentFX
    n = r.u8()
    fx = []
    if n:
        r.u8()  # bitsFXBypass
        for _ in range(n):
            fx.append({"index": r.u8(), "id": r.u32(), "shareset": r.u8(), "rendered": r.u8()})
    return fx


def positioning(r):
    bits = r.u8()
    pos = {"bits": bits}
    if bits & 0x10:  # 3D positioning
        bits3d = r.u8()
        pos["bits3d"] = bits3d
        pos["attenuation"] = r.u32()
    return pos


def aux_params(r):
    bits = r.u8()
    aux = []
    if bits & 8:  # bHasAux
        aux = [r.u32() for _ in range(4)]
    return {"bits": bits, "aux": aux}


def adv_settings(r):
    return {"bits": r.u8(), "virtual": r.u8(), "max_inst": r.u16(), "below": r.u8(), "bits2": r.u8()}


def state_chunk(r):
    for _ in range(r.var()):  # state-driven properties: AkPropID, accumulation type, in dB
        r.var()
        r.u8()
        r.u8()
    groups = []
    for _ in range(r.var()):
        gid = r.u32()
        sync = r.u8()
        states = [(r.u32(), r.u32()) for _ in range(r.var())]
        groups.append({"group": gid, "sync": sync, "states": states})
    return groups


def rtpc_curve_points(r, n):
    return [(r.f32(), r.f32(), r.u32()) for _ in range(n)]


def initial_rtpc(r):
    out = []
    for _ in range(r.u16()):
        rid = r.u32()
        rtype = r.u8()
        accum = r.u8()
        param = r.var()
        curve_id = r.u32()
        scaling = r.u8()
        n = r.u16()
        out.append({"rtpc": rid, "type": rtype, "accum": accum, "param": param, "curve": curve_id,
                    "scaling": scaling, "points": rtpc_curve_points(r, n)})
    return out


def node_base(r):
    n = {}
    n["fx"] = fx_params(r)
    r.u8()  # bOverrideAttachmentParams
    n["bus"] = r.u32()
    n["parent"] = r.u32()
    n["bits"] = r.u8()
    n["props"] = prop_bundle(r)
    n["ranged"] = ranged_bundle(r)
    n["pos"] = positioning(r)
    n["aux"] = aux_params(r)
    n["adv"] = adv_settings(r)
    n["states"] = state_chunk(r)
    n["rtpc"] = initial_rtpc(r)
    return n


def children(r):
    return [r.u32() for _ in range(r.u32())]


# --- object types --------------------------------------------------------------------------

def parse_event(r, o):
    o["actions"] = [r.u32() for _ in range(r.var())]


def except_list(r):
    return [(r.u32(), r.u8()) for _ in range(r.var())]


def parse_action(r, o):
    atype = r.u16()
    o["atype"] = atype
    o["target"] = r.u32()
    o["is_bus"] = r.u8()
    o["props"] = prop_bundle(r)
    o["ranged"] = ranged_bundle(r)
    kind = atype >> 8
    if kind == 0x04:  # Play
        o["curve"] = r.u8()
        o["bank"] = r.u32()
    elif kind in (0x01, 0x02, 0x03):  # Stop / Pause / Resume
        o["curve"] = r.u8()
        o["flags"] = r.u8()
        o["except"] = except_list(r)
    elif kind in (0x06, 0x07):  # Mute / UnMute
        o["curve"] = r.u8()
        o["except"] = except_list(r)
    elif kind == 0x12:  # SetState
        o["group"] = r.u32()
        o["state"] = r.u32()
    elif kind == 0x19:  # SetSwitch
        o["group"] = r.u32()
        o["switch"] = r.u32()
    elif kind in (0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x20, 0x30):  # Set/Reset Pitch, Volume, BusVolume, LPF, HPF
        o["curve"] = r.u8()
        if kind in (0x08, 0x0A, 0x0C, 0x0E, 0x20):
            o["meaning"] = r.u8()
            o["value"] = (r.f32(), r.f32(), r.f32())
        o["except"] = except_list(r)
    elif kind in (0x13, 0x14):  # Set/Reset GameParameter
        o["curve"] = r.u8()
        if kind == 0x13:
            o["bypass_transition"] = r.u8()
            o["meaning"] = r.u8()
            o["value"] = (r.f32(), r.f32(), r.f32())
        o["except"] = except_list(r)
    else:
        raise ValueError(f"action type {atype:#x} not supported")


def parse_sound(r, o):
    o["source"] = bank_source(r)
    o.update(node_base(r))


def parse_ranseq(r, o):
    o.update(node_base(r))
    o["loop"] = r.u16()
    o["loop_mod"] = (r.u16(), r.u16())
    o["trans_time"] = (r.f32(), r.f32(), r.f32())
    o["avoid_repeat"] = r.u16()
    o["trans_mode"] = r.u8()
    o["random_mode"] = r.u8()
    o["mode"] = r.u8()
    o["rs_bits"] = r.u8()
    o["children"] = children(r)
    o["playlist"] = [(r.u32(), r.s32()) for _ in range(r.u16())]


def parse_switch(r, o):
    o.update(node_base(r))
    o["group_type"] = r.u8()
    o["group"] = r.u32()
    o["default"] = r.u32()
    o["continuous"] = r.u8()
    o["children"] = children(r)
    o["switches"] = {}
    for _ in range(r.u32()):
        sid = r.u32()
        o["switches"][sid] = [r.u32() for _ in range(r.u32())]
    o["switch_params"] = {}
    for _ in range(r.u32()):
        nid = r.u32()
        o["switch_params"][nid] = {"bits": r.u8(), "mode": r.u8(), "fade_out": r.s32(), "fade_in": r.s32()}


def parse_mixer(r, o):
    o.update(node_base(r))
    o["children"] = children(r)


def parse_layer(r, o):
    o.update(node_base(r))
    o["children"] = children(r)
    layers = []
    for _ in range(r.u32()):
        layer = {"id": r.u32(), "rtpc": initial_rtpc(r), "rtpc_id": r.u32(), "rtpc_type": r.u8(), "assoc": []}
        for _ in range(r.u32()):
            child = r.u32()
            layer["assoc"].append((child, rtpc_curve_points(r, r.u32())))
        layers.append(layer)
    o["layers"] = layers
    o["continuous"] = r.u8()


def music_node(r, o):
    o["mflags"] = r.u8()
    o.update(node_base(r))
    o["children"] = children(r)
    o["grid_period"] = r.f64()
    o["grid_offset"] = r.f64()
    o["tempo"] = r.f32()
    o["time_sig"] = (r.u8(), r.u8())
    o["meter_flag"] = r.u8()
    o["stingers"] = [dict(trigger=r.u32(), segment=r.u32(), sync=r.u32(), cue=r.u32(), dont_repeat=r.s32(),
                          lookahead=r.u32()) for _ in range(r.u32())]


def fade(r):
    return {"time": r.s32(), "curve": r.u32(), "offset": r.s32()}


def music_trans_node(r, o):
    music_node(r, o)
    rules = []
    for _ in range(r.u32()):
        src = [r.s32() for _ in range(r.u32())]
        dst = [r.s32() for _ in range(r.u32())]
        rule = {"src": src, "dst": dst}
        rule["src_rule"] = {"fade": fade(r), "sync": r.u32(), "cue": r.u32(), "post_exit": r.u8()}
        rule["dst_rule"] = {"fade": fade(r), "cue": r.u32(), "jump_to": r.u32(),
                            "entry": r.u16(), "pre_entry": r.u8(), "match_cue": r.u8()}
        if r.u8():
            rule["trans"] = {"segment": r.u32(), "fade_in": fade(r), "fade_out": fade(r),
                             "pre_entry": r.u8(), "post_exit": r.u8()}
        rules.append(rule)
    o["rules"] = rules


def parse_mseg(r, o):
    music_node(r, o)
    o["duration"] = r.f64()
    markers = []
    for _ in range(r.u32()):
        mid = r.u32()
        pos = r.f64()
        name_len = r.u32()
        name = bytes(r.d[r.p:r.p + name_len]).decode("latin1")
        r.skip(name_len)
        markers.append({"id": mid, "pos": pos, "name": name})
    o["markers"] = markers


def parse_mtrack(r, o):
    o["mflags"] = r.u8()
    o["sources"] = [bank_source(r) for _ in range(r.u32())]
    clips = []
    n = r.u32()
    for _ in range(n):
        clips.append({"track": r.u32(), "source": r.u32(), "play_at": r.f64(),
                      "begin_trim": r.f64(), "end_trim": r.f64(), "src_duration": r.f64()})
    o["clips"] = clips
    if n:
        o["num_subtracks"] = r.u32()
    autos = []
    for _ in range(r.u32()):
        autos.append({"clip": r.u32(), "type": r.u32(), "points": rtpc_curve_points(r, r.u32())})
    o["clip_autos"] = autos
    o.update(node_base(r))
    o["track_type"] = r.u8()
    if o["track_type"] == 3:
        o["sw_group_type"] = r.u8()
        o["sw_group"] = r.u32()
        o["sw_default"] = r.u32()
        o["sw_assoc"] = [r.u32() for _ in range(r.u32())]
        o["sw_trans"] = {"src_fade": fade(r), "sync": r.u32(), "cue": r.u32(), "dst_fade": fade(r)}
    o["lookahead"] = r.s32()


def parse_mswitch(r, o):
    music_trans_node(r, o)
    o["continue_playback"] = r.u8()
    depth = r.u32()
    groups = [r.u32() for _ in range(depth)]
    gtypes = [r.u8() for _ in range(depth)]
    o["tree_groups"] = list(zip(groups, gtypes))
    size = r.u32()
    o["tree_mode"] = r.u8()
    nodes = []
    for _ in range(size // 12):
        key = r.u32()
        val = r.u32()
        weight = r.u16()
        prob = r.u16()
        nodes.append((key, val, weight, prob))
    o["tree"] = nodes


def parse_mranseq(r, o):
    music_trans_node(r, o)
    items = []
    for _ in range(r.u32()):
        items.append({"segment": r.u32(), "item": r.u32(), "nchildren": r.u32(), "rs_type": r.u32(),
                      "loop": r.s16(), "loop_min": r.s16(), "loop_max": r.s16(), "weight": r.u32(),
                      "avoid_repeat": r.u16(), "use_weight": r.u8(), "shuffle": r.u8()})
    o["playlist"] = items


def parse_state(r, o):
    props = {}
    n = r.u16()
    ids = [r.u16() for _ in range(n)]
    for i in ids:
        props[PROPS.get(i, i)] = r.f32()
    o["props"] = props


def parse_bus(r, o):
    o["parent"] = r.u32()
    if o["parent"] == 0:
        o["device"] = r.u32()  # master bus: audio device shareset
    o["props"] = prop_bundle(r)
    o["pos"] = positioning(r)
    o["aux"] = aux_params(r)
    o["bus_bits"] = r.u8()
    o["max_inst"] = r.u16()
    o["channel_config"] = r.u32()
    o["hdr_bits"] = r.u8()
    o["recovery_time"] = r.s32()
    o["max_duck_volume"] = r.f32()
    ducks = []
    for _ in range(r.u32()):
        ducks.append({"bus": r.u32(), "volume": r.f32(), "fade_out": r.s32(), "fade_in": r.s32(),
                      "curve": r.u8(), "target": r.u8()})
    o["ducks"] = ducks
    nfx = r.u8()
    if nfx:
        r.u8()  # bitsFXBypass
    o["fx"] = [{"index": r.u8(), "id": r.u32(), "shareset": r.u8(), "rendered": r.u8()} for _ in range(nfx)]
    o["mixer_plugin"] = r.u32()
    r.u8()  # bOverrideAttachmentParams
    r.u8()  # unknown (always 0 in EtG's Init.bnk)
    o["rtpc"] = initial_rtpc(r)
    o["states"] = state_chunk(r)


FX_PLUGINS = {
    6881283: "parametric_eq", 6946819: "delay", 7077891: "compressor", 7208963: "peak_limiter",
    7536643: "matrix_reverb", 7733251: "roomverb", 8192003: "flanger", 8257539: "guitar_distortion",
    8585219: "tremolo", 8847363: "stereo_delay", 9043971: "harmonizer",
}


def parse_fx(r, o):
    """FxShareSet / FxCustom: an effect plugin instance with its parameter block."""
    plugin = r.u32()
    o["plugin"] = FX_PLUGINS.get(plugin, plugin)
    size = r.u32()
    o["params"] = bytes(r.d[r.p:r.p + size])
    r.skip(size)
    o["media"] = [(r.u8(), r.u32()) for _ in range(r.u8())]
    o["rtpc"] = initial_rtpc(r)
    o["states"] = state_chunk(r)
    r.u16()  # property RTPC/modulator list (always empty in EtG's banks)


def parametric_eq(params):
    """Wwise Parametric EQ: 3 x (filter type, gain dB, frequency Hz, Q, on) + output level dB."""
    bands = []
    for i in range(3):
        ftype, gain, freq, q, on = struct.unpack_from("<IfffB", params, 17 * i)
        bands.append({"type": ftype, "gain": gain, "freq": freq, "q": q, "on": bool(on)})
    return {"bands": bands, "output": struct.unpack_from("<f", params, 51)[0]}


def delay(params):
    """Wwise Delay: delay time s, feedback %, wet/dry mix %, output level dB, feedback on, LFE."""
    t, feedback, mix, output, fb_on, _ = struct.unpack_from("<ffffBB", params, 0)
    return {"time": t, "feedback": feedback if fb_on else 0.0, "mix": mix, "output": output}


def parse_attenuation(r, o):
    if r.u8():  # cone
        o["cone"] = {"inside": r.f32(), "outside": r.f32(), "volume": r.f32(), "lpf": r.f32(), "hpf": r.f32()}
    # curve index per use: dry volume, game-defined aux, user-defined aux, LPF, HPF, spread, focus (0xFF = none)
    o["uses"] = [r.u8() for _ in range(7)]
    curves = []
    for _ in range(r.u8()):
        scaling = r.u8()
        curves.append({"scaling": scaling, "points": rtpc_curve_points(r, r.u16())})
    o["curves"] = curves
    o["rtpc"] = initial_rtpc(r)


PARSERS = {
    "event": parse_event, "action": parse_action, "sound": parse_sound, "ranseq": parse_ranseq,
    "switch": parse_switch, "mixer": parse_mixer, "layer": parse_layer, "mseg": parse_mseg,
    "mtrack": parse_mtrack, "mswitch": parse_mswitch, "mranseq": parse_mranseq, "state": parse_state,
    "bus": parse_bus, "auxbus": parse_bus, "attenuation": parse_attenuation,
    "fxshareset": parse_fx, "fxcustom": parse_fx,
}


def parse_hirc(data, chunks, strict=True):
    """Returns ({id: obj}, errors). Objects of types the engine doesn't need keep only id/type."""
    o, _ = chunks["HIRC"]
    count = struct.unpack_from("<I", data, o)[0]
    q = o + 4
    objs, errors = {}, []
    for _ in range(count):
        t = data[q]
        size = struct.unpack_from("<I", data, q + 1)[0]
        start, end = q + 5, q + 5 + size
        q = end
        kind = HIRC_TYPES.get(t, t)
        oid = struct.unpack_from("<I", data, start)[0]
        obj = {"id": oid, "type": kind}
        objs[oid] = obj
        parser = PARSERS.get(kind)
        if parser is None:
            continue
        r = Reader(data, start + 4, end)
        try:
            parser(r, obj)
            if r.p != end:
                raise ValueError(f"{end - r.p} bytes left over")
        except Exception as e:  # noqa: BLE001 - collected and reported per object
            errors.append((kind, oid, start, size, str(e)))
            if strict:
                raise ValueError(f"{kind} {oid} at {start:#x} (size {size}): {e}") from e
    return objs, errors


def media_index(data, chunks):
    """{media id: (absolute offset, size)} of the WEM files embedded in DATA."""
    if "DIDX" not in chunks:
        return {}
    o, n = chunks["DIDX"]
    base = chunks["DATA"][0]
    out = {}
    for k in range(n // 12):
        mid, off, size = struct.unpack_from("<III", data, o + 12 * k)
        out[mid] = (base + off, size)
    return out


def parse_bank(path, strict=True):
    data = open(path, "rb").read()
    chunks = read_chunks(data)
    version = struct.unpack_from("<I", data, chunks["BKHD"][0])[0]
    objs, errors = parse_hirc(data, chunks, strict)
    settings = {}
    if "STMG" in chunks:  # Init.bnk: project-wide voice settings
        o = chunks["STMG"][0]
        settings = {"volume_threshold": struct.unpack_from("<f", data, o)[0],
                    "max_voices": struct.unpack_from("<H", data, o + 4)[0]}
    return {"version": version, "settings": settings, "objects": objs, "errors": errors, "media": media_index(data, chunks), "data": data}
