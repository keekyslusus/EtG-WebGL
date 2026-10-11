"""Test cases for the audio differential test (audiodiff.py): what to play, in which context.

A case is a script both renderers run: steps [{t, op, args}] with op one of postEvent(event, obj, x, y, z),
setSwitch(group, value, obj), setState(group, state), setRtpc(id, value, obj, fadeMs), stopAll().
Every case starts from the same context: listener at the origin facing +z, all state groups at
None, VOL_MUS/VOL_SFX/VOL_UI at 100, a fresh game object (obj 2) at the origin.

Kinds:
  event     every sound effect event, once per value of the switches/states its sounds depend on
  music     every music event, once per value of its music switch's state group (20 s)
  trans     music: one state for 8 s, then the next one (transition rules, sync points, fades)
  volume    VOL_SFX / VOL_MUS / VOL_UI curves (RTPC -> bus volume)
  state     global states that change the mix (Pause -> music low-pass, Music_Dip, Secret_Revealed)
  pos       3D-positioned sounds left/right/near/far of the listener (panning, distance attenuation),
            a few events per attenuation
"""
import itertools
import math

OBJ = 2
MAX_SECONDS = 6.0
LOOP_SECONDS = 4.0
MUSIC_SECONDS = 20.0
MUSIC_SWITCH_AT = 8.0
VOLUMES = ("VOL_MUS", "VOL_SFX", "VOL_UI")
POS_EVENTS_PER_ATTENUATION = 3  # 3D cases for a few events per attenuation curve set


def fnv(name):
    h = 2166136261
    for c in name.lower().encode():
        h = (h * 16777619) & 0xFFFFFFFF
        h ^= c
    return h


class Bank:
    def __init__(self, doc):
        self.doc = doc
        self.nodes = doc["nodes"]
        self.events = doc["events"]
        self.media = doc["media"]
        self.names = {int(k): v for k, v in doc["names"].items()}

    def node(self, nid):
        return self.nodes.get(str(nid))

    def name(self, nid):
        return self.names.get(nid, str(nid))

    def plays(self, eid):
        return [a for a in self.events[str(eid)] if a["type"] == 4]

    def is_music(self, nid):
        n = self.node(nid)
        return bool(n) and n["type"] in ("mseg", "mranseq", "mswitch")

    # --- what a play depends on -----------------------------------------------------------------

    def dependencies(self, nid, out, seen):
        """Switch/state groups the sounds under nid depend on: {(kind, group): [values]}."""
        if nid in seen:
            return
        seen.add(nid)
        n = self.node(nid)
        if not n:
            return
        if n["type"] == "switch":
            kind = "state" if n["group_type"] == 1 else "switch"
            values = [int(v) for v, kids in n["switches"].items() if kids]
            if values:
                out.setdefault((kind, n["group"]), set()).update(values)
            for kids in n["switches"].values():
                for c in kids:
                    self.dependencies(c, out, seen)
        elif n["type"] in ("ranseq", "layer"):
            for c in n.get("children", []):
                self.dependencies(c, out, seen)

    def random_inside(self, nid, seen=None):
        """True if playing nid involves a random choice or randomized property."""
        seen = seen or set()
        if nid in seen:
            return False
        seen.add(nid)
        n = self.node(nid)
        if not n:
            return False
        if n.get("ranged") or (n["type"] == "ranseq" and n["mode"] == 0 and len(n["playlist"]) > 1):
            return True
        kids = [c for ks in n.get("switches", {}).values() for c in ks] if n["type"] == "switch" else n.get("children", [])
        return any(self.random_inside(c, seen) for c in kids)

    def sequence_inside(self, nid, seen=None):
        """True if nid contains a sequence container (its position persists between plays)."""
        seen = seen or set()
        if nid in seen:
            return False
        seen.add(nid)
        n = self.node(nid)
        if not n:
            return False
        if n["type"] == "ranseq" and n["mode"] == 1:
            return True
        kids = [c for ks in n.get("switches", {}).values() for c in ks] if n["type"] == "switch" else n.get("children", [])
        return any(self.sequence_inside(c, seen) for c in kids)

    def attenuation_3d(self, nid, seen=None):
        """Attenuation id of the first 3D-positioned sound under nid (positioning is inherited), else None."""
        seen = seen or set()
        if nid in seen:
            return None
        seen.add(nid)
        n = self.node(nid)
        if not n:
            return None
        if n["type"] == "sound":
            cur = n
            while cur:
                if "pos" in cur:
                    return cur["pos"]["att"] if cur["pos"]["3d"] else None
                cur = self.node(cur.get("parent", 0))
            return None
        kids = [c for ks in n.get("switches", {}).values() for c in ks] if n["type"] == "switch" else n.get("children", [])
        for c in kids:
            att = self.attenuation_3d(c, seen)
            if att is not None:
                return att
        return None

    def length(self, nid, seen=None):
        """Seconds a play of nid lasts (math.inf for loops)."""
        seen = seen or set()
        if nid in seen:
            return 0.0
        seen = seen | {nid}
        n = self.node(nid)
        if not n:
            return 0.0
        props = n.get("props", {})
        delay = props.get("idelay", 0.0)
        if n["type"] == "sound":
            m = self.media.get(str(n["media"]))
            if not m:
                return 0.0
            if props.get("loop", 1) != 1:
                return math.inf
            return delay + m["samples"] / m["rate"]
        if n["type"] == "switch":
            kids = [c for ks in n["switches"].values() for c in ks]
        else:
            kids = n.get("children", [])
        lengths = [self.length(c, seen) for c in kids] or [0.0]
        if n["type"] == "ranseq" and n["bits"] & 8:  # continuous
            if n["loop"] == 0:
                return math.inf
            return delay + sum(lengths) * max(n["loop"], 1)
        return delay + max(lengths)


def event_duration(bank, eid):
    longest = 0.0
    for a in bank.plays(eid):
        delay = a.get("props", {}).get("delay", 0) / 1000
        longest = max(longest, delay + bank.length(a["target"]))
    if math.isinf(longest):
        return LOOP_SECONDS
    return round(min(longest + 0.5, MAX_SECONDS), 3)


def post(eid, t=0.0, pos=(0.0, 0.0, 0.0)):
    return {"t": t, "op": "postEvent", "args": [eid, OBJ, *pos]}


def make_cases(doc, repeats=3):
    bank = Bank(doc)
    state_groups = sorted({g for n in bank.nodes.values() if n["type"] == "switch" and n["group_type"] == 1 for g in [n["group"]]}
                          | {g for n in bank.nodes.values() if n["type"] == "mswitch" for g, t in n["tree_groups"] if t == 1}
                          | {a["group"] for acts in bank.events.values() for a in acts if a["type"] == 0x12})
    cases = []

    def add(kind, name, steps, duration, randomized, fresh=False):
        for r in range(repeats if randomized else 1):
            cases.append({"id": f"{len(cases):05d}", "kind": kind, "name": name, "rep": r, "seed": 1000 + r,
                          "duration": duration, "fresh": fresh, "steps": steps})

    pos_per_att = {}
    by_name = {}
    for eid in bank.events:
        by_name[bank.name(int(eid))] = int(eid)

    for eid_s in sorted(bank.events, key=lambda e: bank.name(int(e))):
        eid = int(eid_s)
        plays = bank.plays(eid)
        if not plays:
            continue
        ename = bank.name(eid)
        if any(bank.is_music(a["target"]) for a in plays):
            continue  # music cases below
        deps = {}
        for a in plays:
            bank.dependencies(a["target"], deps, set())
        randomized = any(bank.random_inside(a["target"]) for a in plays)
        fresh = any(bank.sequence_inside(a["target"]) for a in plays)
        duration = event_duration(bank, eid)
        groups = sorted(deps.items(), key=lambda kv: (kv[0][0], bank.name(kv[0][1])))
        if not groups:
            combos = [[]]
        else:
            lists = [[(kind, g, v) for v in sorted(vals, key=bank.name)] for (kind, g), vals in groups]
            if math.prod(len(lst) for lst in lists) <= 32:
                combos = [list(c) for c in itertools.product(*lists)]
            else:  # vary one group at a time, the others at their first value
                combos = []
                for i, lst in enumerate(lists):
                    for item in lst:
                        combos.append([item if j == i else other[0] for j, other in enumerate(lists)])
        for combo in combos:
            steps, label = [], [ename]
            for kind, g, v in combo:
                if kind == "switch":
                    steps.append({"t": 0, "op": "setSwitch", "args": [g, v, OBJ]})
                else:
                    steps.append({"t": 0, "op": "setState", "args": [g, v]})
                label.append(f"{bank.name(g)}={bank.name(v)}")
            steps.append(post(eid))
            add("event", " ".join(label), steps, duration, randomized, fresh)
        att = next((x for x in (bank.attenuation_3d(a["target"]) for a in plays) if x is not None), None)
        if att is not None and not combos[0] and pos_per_att.get(att, 0) < POS_EVENTS_PER_ATTENUATION:
            pos_per_att[att] = pos_per_att.get(att, 0) + 1
            for x in (6.0, -6.0, 20.0, 40.0):
                add("pos", f"{ename} x={x:g}", [post(eid, pos=(x, 0.0, 0.0))], duration, randomized, fresh)

    # music: every state of the top switch's group, then transitions between neighbouring states
    for eid_s in sorted(bank.events, key=lambda e: bank.name(int(e))):
        eid = int(eid_s)
        plays = [a for a in bank.plays(eid) if bank.is_music(a["target"])]
        if not plays:
            continue
        ename = bank.name(eid)
        top = bank.node(plays[0]["target"])
        groups = [g for g, t in top.get("tree_groups", []) if t == 1] if top["type"] == "mswitch" else []
        if not groups:
            add("music", ename, [post(eid)], MUSIC_SECONDS, True)
            continue
        g = groups[0]
        tree = top["tree"]
        first, count = tree[0][1] & 0xFFFF, tree[0][1] >> 16
        values = [k for k, v, *_ in tree[first:first + count] if k != 0 and bank.node(v)]
        for v in values:
            add("music", f"{ename} {bank.name(g)}={bank.name(v)}",
                [{"t": 0, "op": "setState", "args": [g, v]}, post(eid)], MUSIC_SECONDS, True)
        for a, b in zip(values, values[1:]):
            add("trans", f"{ename} {bank.name(g)}={bank.name(a)}->{bank.name(b)}",
                [{"t": 0, "op": "setState", "args": [g, a]}, post(eid),
                 {"t": MUSIC_SWITCH_AT, "op": "setState", "args": [g, b]}], MUSIC_SECONDS, True)

    # volume curves and global mix states
    gun = [{"t": 0, "op": "setSwitch", "args": [fnv("WPN_Guns"), fnv("AK47"), OBJ]}, post(by_name["Play_WPN_Gun_Shot_01"])]
    for v in (0, 1, 10, 25, 50, 75):
        add("volume", f"VOL_SFX={v} Play_WPN_Gun_Shot_01 WPN_Guns=AK47",
            [{"t": 0, "op": "setRtpc", "args": [fnv("VOL_SFX"), v, -1, 0]}] + gun, 1.5, True)
    title = by_name.get("Play_MUS_Title_Theme_01")
    for v in (0, 10, 50):
        add("volume", f"VOL_MUS={v} Play_MUS_Title_Theme_01",
            [{"t": 0, "op": "setRtpc", "args": [fnv("VOL_MUS"), v, -1, 0]}, post(title)], 8.0, True)
    ui = next((by_name[n] for n in sorted(by_name) if n.startswith("Play_UI_")), None)
    if ui:
        for v in (10, 50):
            add("volume", f"VOL_UI={v} {bank.name(ui)}",
                [{"t": 0, "op": "setRtpc", "args": [fnv("VOL_UI"), v, -1, 0]}, post(ui)], event_duration(bank, ui), True)
    for group, state in (("Pause", "Paused"), ("Music_Dip", "On"), ("Secret_Revealed", "On")):
        add("state", f"{group}={state} Play_MUS_Title_Theme_01",
            [{"t": 0, "op": "setState", "args": [fnv(group), fnv(state)]}, post(title)], 8.0, True)

    return cases, {"states": state_groups, "rtpcs": [[fnv(n), 100.0] for n in VOLUMES]}
