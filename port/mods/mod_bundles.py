"""Puts the sources of the mods' own AssetBundles into the Unity project, so the web build makes WebGL bundles of
them along with the game's (EtG Web -> 3 / Rebuild Bundles; WebShim.BundleShim hands them out when a mod loads its
bundle file, port/README.md "Mods").

The mods ship Windows bundles, which a WebGL player can't load. Expand the Gungeon keeps the Unity projects its
bundles are built from in its repository (ExpandAssets, ExpandShaders; mods/README of the port names the commit),
but without .meta files: no GUIDs (prefabs reference scripts, video clips and textures by GUID), no import
settings, no bundle names. All three are recovered from the shipped bundle of the same version:
  - the assets are the shipped bundle's container: every entry is copied from the sources to the same path in the
    project (Assets/ExpandRooms/..., as the mod loads them by full path), text assets with the bundle's exact
    bytes (the repository normalised line endings; the mod splits some texts on '\\n');
  - textures get the bundle's format (RGB24/RGBA32), filtering, wrapping, mip maps and readability, and
    alphaIsTransparency where the bundle's transparent pixels differ from the PNG's (Unity's dilation);
  - every GUID a prefab references is resolved by finding the same component in the shipped bundle's copy of the
    prefab (hierarchy path, class, index): an asset of the bundle gets that GUID in its .meta, a script becomes a
    reference into the mod's DLL in Assets/Plugins (fileID from namespace + class name, GUID from apply.sh), a
    built-in material a built-in reference;
  - expandaudio holds the sound bank, which the web build plays from its own audio pack (port/audio): the bundle
    gets an empty stand-in of the same name, 53 MB less to download.

Writes <project>/Assets/.mod-assets (the top-level folders it created; apply.sh removes them on a vanilla run).

Also extracts the mods' Wwise banks for the web build's audio (port/audio/build_audio.py --mods <banks dir>).

Usage: mod_bundles.py <modpack.txt> <mods dir> <sources dir (etg/tools/src)> <project Assets dir> <banks dir>
Needs UnityPy (pip install UnityPy) and PyYAML.
"""
import hashlib
import os
import re
import shutil
import struct
import subprocess
import sys

import UnityPy
import yaml

# Mods whose bundles are rebuilt from source. Per bundle: the Unity project in the repository it is built from.
MODS = {
    "ApacheThunder-ExpandTheGungeon": {
        "repo": "ExpandTheGungeon",
        "url": "https://github.com/ApacheThunder/ExpandTheGungeon (branch BepInEx)",
        "commit": "a7ff72a6",  # "v3.0.8 Release Commit."
        "dll": "ExpandTheGungeon.dll",
        "bundles": {
            "expandsharedauto": "ExpandAssets",
            "expandshaders": "ExpandShaders",
            "expandaudio": "ExpandAssets",
        },
        # Assets replaced by an empty stand-in (sound banks: the web build has its own audio).
        "stubs": {"assets/expandaudio/ex_sfx.bytes"},
        # Wwise banks in the bundles -> <banks dir>/<package>/<name>.bnk for port/audio/build_audio.py --mods.
        "banks": {"assets/expandaudio/ex_sfx.bytes": "EX_SFX"},
        # Their name tables (Wwise's SoundBank .txt; names for debugging and port/audio's tests), in the repository.
        "bank_names": {"EX_SFX": "ExpandAssets/Audio/GeneratedSoundBanks/Windows/EX_SFX.txt"},
    },
}

# Unity's built-in materials a bundle copies in (by name) -> built-in reference.
BUILTIN = {
    "Sprites-Default": "{fileID: 10754, guid: 0000000000000000f000000000000000, type: 0}",
    "Default-Material": "{fileID: 10303, guid: 0000000000000000f000000000000000, type: 0}",
}


def mod_dll_guid(dll_name):
    """Same as apply.sh: md5 of "etgmod:<file name>" (the DLL's .meta GUID must not change between runs)."""
    return hashlib.md5(("etgmod:" + dll_name).encode()).hexdigest()


def script_file_id(namespace, name):
    """fileID of a MonoScript of a class inside a DLL: first 4 bytes of MD4("s\\0\\0\\0" + namespace + name)."""
    return struct.unpack("<i", md4(b"s\0\0\0" + (namespace + name).encode())[:4])[0]


def md4(data):
    def f(x, y, z): return x & y | ~x & z
    def g(x, y, z): return x & y | x & z | y & z
    def h(x, y, z): return x ^ y ^ z
    def rol(v, s): v &= 0xFFFFFFFF; return (v << s | v >> (32 - s)) & 0xFFFFFFFF
    msg = data + b"\x80" + b"\0" * ((55 - len(data)) % 64) + struct.pack("<Q", len(data) * 8)
    a, b, c, d = 0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476
    for off in range(0, len(msg), 64):
        x = struct.unpack("<16I", msg[off:off + 64])
        aa, bb, cc, dd = a, b, c, d
        for i in (0, 4, 8, 12):
            a = rol(a + f(b, c, d) + x[i], 3); d = rol(d + f(a, b, c) + x[i + 1], 7)
            c = rol(c + f(d, a, b) + x[i + 2], 11); b = rol(b + f(c, d, a) + x[i + 3], 19)
        for i in (0, 1, 2, 3):
            a = rol(a + g(b, c, d) + x[i] + 0x5A827999, 3); d = rol(d + g(a, b, c) + x[i + 4] + 0x5A827999, 5)
            c = rol(c + g(d, a, b) + x[i + 8] + 0x5A827999, 9); b = rol(b + g(c, d, a) + x[i + 12] + 0x5A827999, 13)
        for i in (0, 2, 1, 3):
            a = rol(a + h(b, c, d) + x[i] + 0x6ED9EBA1, 3); d = rol(d + h(a, b, c) + x[i + 8] + 0x6ED9EBA1, 9)
            c = rol(c + h(d, a, b) + x[i + 4] + 0x6ED9EBA1, 11); b = rol(b + h(c, d, a) + x[i + 12] + 0x6ED9EBA1, 15)
        a, b, c, d = (a + aa) & 0xFFFFFFFF, (b + bb) & 0xFFFFFFFF, (c + cc) & 0xFFFFFFFF, (d + dd) & 0xFFFFFFFF
    return struct.pack("<4I", a, b, c, d)


def packages(modpack):
    with open(modpack, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#"):
                yield line.split()[0]


# ---- Unity YAML (prefabs) ----

DOC = re.compile(r"^--- !u!(\d+) &(-?\d+)( stripped)?$", re.M)
REF = re.compile(r"\{fileID: (-?\d+), guid: ([0-9a-f]{32}), type: (\d+)\}")


def yaml_docs(text):
    """{fileID: (classID, parsed dict)} of a Unity YAML file."""
    docs = {}
    heads = list(DOC.finditer(text))
    for i, m in enumerate(heads):
        body = text[m.end():heads[i + 1].start() if i + 1 < len(heads) else len(text)]
        parsed = yaml.safe_load(body) or {}
        docs[int(m.group(2))] = (int(m.group(1)), next(iter(parsed.values())) if parsed else {})
    return docs


def yaml_hierarchy(docs):
    """fileID -> (GameObject path, classID, index among the GameObject's components of that class)."""
    result = {}
    gos = {fid: d for fid, (cls, d) in docs.items() if cls == 1}
    transforms = {fid: d for fid, (cls, d) in docs.items() if cls in (4, 224)}
    go_of_transform = {fid: d["m_GameObject"]["fileID"] for fid, d in transforms.items()}

    def path(go_id):
        go = gos[go_id]
        tr = next(c["component"]["fileID"] for c in go["m_Component"] if c["component"]["fileID"] in transforms)
        father = transforms[tr]["m_Father"]["fileID"]
        own = go["m_Name"]
        return own if not father else path(go_of_transform[father]) + "/" + own

    for go_id, go in gos.items():
        p = path(go_id)
        result[go_id] = (p, 1, 0)
        seen = {}
        for c in go["m_Component"]:
            cid = c["component"]["fileID"]
            cls = docs[cid][0]
            result[cid] = (p, cls, seen.get(cls, 0))
            seen[cls] = seen.get(cls, 0) + 1
    return result


def bundle_hierarchy(root_go):
    """Same keys as yaml_hierarchy -> ObjectReader, for a prefab in a bundle (root GameObject reader)."""
    result = {}

    def walk(go_reader, prefix):
        go = go_reader.read()
        p = go.m_Name if not prefix else prefix + "/" + go.m_Name
        result[(p, 1, 0)] = go_reader
        seen = {}
        children = []
        for c in go.m_Component:
            ptr = c.component if hasattr(c, "component") else c
            reader = ptr.deref()
            cls = reader.type.value
            result[(p, cls, seen.get(cls, 0))] = reader
            seen[cls] = seen.get(cls, 0) + 1
            if cls in (4, 224):
                children = reader.read().m_Children
        for child in children:
            walk(child.deref().read().m_GameObject.deref(), p)

    walk(root_go, "")
    return result


def field_of(tree, dotted):
    """A field of a read_typetree() dict by YAML key path ("m_Materials.0")."""
    v = tree
    for part in dotted.split("."):
        v = v[int(part)] if isinstance(v, list) else v[part]
    return v


# ---- meta files ----

def texture_meta(guid, bundle, tex, alpha_is_transparency):
    ts = tex.m_TextureSettings
    fmt = int(tex.m_TextureFormat)
    return f"""fileFormatVersion: 2
guid: {guid}
TextureImporter:
  fileIDToRecycleName: {{}}
  externalObjects: {{}}
  serializedVersion: 4
  mipmaps:
    mipMapMode: 0
    enableMipMap: {1 if tex.m_MipCount > 1 else 0}
    sRGBTexture: {1 if getattr(tex, 'm_ColorSpace', 1) == 1 else 0}
    linearTexture: 0
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
  isReadable: {1 if tex.m_IsReadable else 0}
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: {fmt}
  maxTextureSize: 2048
  textureSettings:
    serializedVersion: 2
    filterMode: {ts.m_FilterMode}
    aniso: {ts.m_Aniso}
    mipBias: {ts.m_MipBias}
    wrapU: {ts.m_WrapU}
    wrapV: {ts.m_WrapV}
    wrapW: {getattr(ts, 'm_WrapW', ts.m_WrapU)}
  nPOTScale: 0
  lightmap: 0
  compressionQuality: 50
  spriteMode: 0
  spriteExtrude: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 1
  alphaUsage: 1
  alphaIsTransparency: {1 if alpha_is_transparency else 0}
  spriteTessellationDetail: -1
  textureType: 0
  textureShape: 1
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  platformSettings:
  - buildTarget: DefaultTexturePlatform
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: {fmt}
    textureCompression: 0
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    androidETC2FallbackOverride: 0
  - buildTarget: WebGL
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: {fmt}
    textureCompression: 0
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 1
    androidETC2FallbackOverride: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    physicsShape: []
  spritePackingTag:
  userData:
  assetBundleName: {bundle}
  assetBundleVariant:
"""


def default_meta(guid, bundle, importer):
    return f"""fileFormatVersion: 2
guid: {guid}
{importer}:
  externalObjects: {{}}
  userData:
  assetBundleName: {bundle}
  assetBundleVariant:
"""


def video_meta(guid, bundle):
    # useLegacyImporter: 0, or 2017.4 imports the clip as a MovieTexture (not supported on WebGL: only its audio
    # track is left, as an AudioClip).
    return f"""fileFormatVersion: 2
guid: {guid}
VideoClipImporter:
  externalObjects: {{}}
  serializedVersion: 2
  useLegacyImporter: 0
  quality: 0.5
  isColorLinear: 0
  frameRange: 0
  startFrame: -1
  endFrame: -1
  colorSpace: 0
  deinterlace: 0
  encodeAlpha: 0
  flipVertical: 0
  flipHorizontal: 0
  importAudio: 1
  targetSettings: {{}}
  userData: 
  assetBundleName: {bundle}
  assetBundleVariant: 
"""


def folder_meta(guid):
    return f"""fileFormatVersion: 2
guid: {guid}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""


IMPORTERS = {".prefab": "NativeFormatImporter", ".bytes": "TextScriptImporter", ".txt": "TextScriptImporter",
             ".shader": "ShaderImporter"}


def stable_guid(*parts):
    return hashlib.md5(("etgmodasset:" + "|".join(parts)).encode()).hexdigest()


def alpha_dilated(bundle_tex, source_png):
    """True if the bundle's fully transparent pixels differ from the PNG's (Unity's alphaIsTransparency)."""
    from PIL import Image
    a = bundle_tex.image.convert("RGBA")
    s = Image.open(source_png).convert("RGBA")
    if a.size != s.size:
        raise SystemExit(f"texture size differs from the bundle: {source_png}")
    pa, ps = a.tobytes(), s.tobytes()
    for i in range(0, len(pa), 4):
        if pa[i:i + 4] != ps[i:i + 4]:
            if pa[i + 3] or ps[i + 3]:
                raise SystemExit(f"visible pixels differ from the bundle: {source_png}")
            return True
    return False


def install_mod(pkg, cfg, mods_dir, sources_dir, assets_dir, created_roots, written, banks_dir):
    repo = os.path.join(sources_dir, cfg["repo"])
    if not os.path.isdir(repo):
        raise SystemExit(f"{pkg}: sources missing - git clone {cfg['url']} into {repo} and check out {cfg['commit']}")
    head = subprocess.run(["git", "-C", repo, "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    if not head.startswith(cfg["commit"]):
        raise SystemExit(f"{pkg}: {repo} is at {head[:8]}, the shipped bundles were built from {cfg['commit']}")
    plugin_dir = os.path.join(mods_dir, pkg, "plugins")
    dll_guid = mod_dll_guid(cfg["dll"])

    # Pass 1: what goes where, per bundle.
    entries = []  # (bundle, container path, source file, project path, ObjectReader)
    bundles = {}
    for bundle, project in cfg["bundles"].items():
        env = UnityPy.load(os.path.join(plugin_dir, bundle))
        bundles[bundle] = env
        src_root = os.path.join(repo, project)
        by_lower = {}
        for dirpath, _, files in os.walk(os.path.join(src_root, "Assets")):
            for name in files:
                full = os.path.join(dirpath, name)
                by_lower[os.path.relpath(full, src_root).replace(os.sep, "/").lower()] = full
        for path, reader in env.container.items():
            source = by_lower.get(path)
            if source is None:
                raise SystemExit(f"{pkg}: {bundle} has {path}, the sources don't")
            rel = os.path.relpath(source, src_root).replace(os.sep, "/")  # Assets/ExpandRooms/... (source casing)
            entries.append((bundle, path, source, rel, reader))

    # Pass 2: GUIDs prefabs reference -> what they are in the shipped bundle.
    path_ids = {}  # (bundle, path_id) -> container path of the main object
    for bundle, path, _, _, reader in entries:
        path_ids[(bundle, reader.path_id)] = path
    asset_guids = {}   # container path -> GUID for its .meta
    replacements = {}  # (guid, fileID) -> replacement reference text
    aliases = {}       # GUID -> the GUID of the same asset under another name (two GUIDs, one asset)

    def claim(asset, guid):
        if asset_guids.setdefault(asset, guid) != guid:
            aliases[guid] = asset_guids[asset]
    for bundle, path, source, rel, reader in entries:
        if not path.endswith(".prefab"):
            continue
        with open(source, encoding="utf-8") as f:
            text = f.read()
        if not REF.search(text):
            continue
        docs = yaml_docs(text)
        ykeys = yaml_hierarchy(docs)
        bobjs = bundle_hierarchy(reader)
        for fid, (cls, doc) in docs.items():
            if fid not in ykeys:
                continue
            for key, value in flatten(doc):
                m = REF.fullmatch(value) if isinstance(value, str) else None
                if not m or m.group(2).startswith("0000000000000000"):
                    continue
                guid = m.group(2)
                if (guid, m.group(1)) in replacements or guid in aliases or guid in asset_guids.values():
                    continue
                bobj = bobjs.get(ykeys[fid])
                if bobj is None:
                    raise SystemExit(f"{pkg}: {path}: no {ykeys[fid]} in the shipped bundle")
                tree = bobj.read_typetree()
                ptr = field_of(tree, key)
                if not ptr or not ptr.get("m_PathID"):
                    # Missing in the bundle too (an asset that wasn't in the project): keep it missing.
                    replacements[(guid, m.group(1))] = "{fileID: 0}"
                    continue
                target = bundles[bundle].files  # only references inside the same bundle are expected
                if ptr["m_FileID"] != 0:
                    raise SystemExit(f"{pkg}: {path} {key}: reference into another file")
                tobj = find_object(bundles[bundle], ptr["m_PathID"])
                if tobj.type.name == "MonoScript":
                    s = tobj.read()
                    if s.m_AssemblyName != cfg["dll"]:
                        raise SystemExit(f"{pkg}: {path}: script from {s.m_AssemblyName}")
                    replacements[(guid, m.group(1))] = f"{{fileID: {script_file_id(s.m_Namespace, s.m_ClassName)}, guid: {dll_guid}, type: 3}}"
                elif (bundle, ptr["m_PathID"]) in path_ids:
                    claim(path_ids[(bundle, ptr["m_PathID"])], guid)
                elif tobj.type.name == "GameObject" or tobj.type.name in ("Transform",):
                    # An object inside another prefab: its prefab's GUID; fileIDs inside stay as in the YAML.
                    claim(owning_prefab(bundles[bundle], tobj, path_ids, bundle), guid)
                else:
                    name = tobj.read().m_Name if hasattr(tobj.read(), "m_Name") else ""
                    if tobj.type.name == "Material" and name in BUILTIN:
                        replacements[(guid, m.group(1))] = BUILTIN[name]
                    else:
                        raise SystemExit(f"{pkg}: {path} {key}: unexpected {tobj.type.name} {name}")

    # Sound banks, exactly as the shipped bundle has them.
    for bundle, path, source, rel, reader in entries:
        if path in cfg.get("banks", {}):
            out = os.path.join(banks_dir, pkg, cfg["banks"][path] + ".bnk")
            os.makedirs(os.path.dirname(out), exist_ok=True)
            s = reader.read().m_Script
            write_if_changed(out, s.encode("utf-8", "surrogateescape") if isinstance(s, str) else bytes(s))
            names = cfg.get("bank_names", {}).get(cfg["banks"][path])
            if names:
                shutil.copyfile(os.path.join(repo, names), out[:-4] + ".txt")

    # Video clips: the WebGL VideoPlayer plays URLs only (WebShim.MediaShim.SetVideoClip): the source files go to
    # StreamingAssets/mods/video/<clip name><ext> as well.
    for bundle, path, source, rel, reader in entries:
        if reader.type.name == "VideoClip":
            out = os.path.join(assets_dir, "StreamingAssets", "mods", "video", reader.read().m_Name + os.path.splitext(source)[1])
            os.makedirs(os.path.dirname(out), exist_ok=True)
            with open(source, "rb") as f:
                write_if_changed(out, f.read())

    # Pass 3: write.
    for bundle, path, source, rel, reader in entries:
        dest = os.path.join(assets_dir, os.path.relpath(rel, "Assets"))
        created_roots.add(rel.split("/")[1])
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        ext = os.path.splitext(path)[1]
        guid = asset_guids.get(path) or stable_guid(pkg, path)
        if path in cfg["stubs"]:
            data = b""
        elif reader.type.name == "TextAsset":
            s = reader.read().m_Script
            data = s.encode("utf-8", "surrogateescape") if isinstance(s, str) else bytes(s)
        elif ext == ".prefab":
            with open(source, encoding="utf-8") as f:
                text = f.read()
            text = REF.sub(lambda m: replacements.get((m.group(2), m.group(1)))
                           or m.group(0).replace(m.group(2), aliases.get(m.group(2), m.group(2))), text)
            data = text.encode("utf-8")
        else:
            with open(source, "rb") as f:
                data = f.read()
        write_if_changed(dest, data)
        written.add(os.path.normcase(os.path.abspath(dest)))
        if reader.type.name == "Texture2D":
            tex = reader.read()
            meta = texture_meta(guid, bundle, tex, alpha_dilated(tex, source))
        elif reader.type.name == "VideoClip":
            meta = video_meta(guid, bundle)
        else:
            importer = IMPORTERS.get(ext)
            if importer is None:
                raise SystemExit(f"{pkg}: no importer for {path}")
            meta = default_meta(guid, bundle, importer)
        write_if_changed(dest + ".meta", meta.encode("utf-8"))

    # Folders get stable GUIDs too (Unity would make random ones on each fresh install).
    for root in created_roots:
        for dirpath, dirs, _ in os.walk(os.path.join(assets_dir, root)):
            for d in [dirpath] + [os.path.join(dirpath, x) for x in dirs]:
                rel = os.path.relpath(d, assets_dir).replace(os.sep, "/")
                write_if_changed(d + ".meta", folder_meta(stable_guid("folder", rel)).encode("utf-8"))
    resolved = sorted({g for g, _ in replacements} | set(asset_guids.values()) | set(aliases))
    print(f"   {pkg}: {len(entries)} assets in {len(cfg['bundles'])} bundles, {len(resolved)} prefab GUIDs resolved")


def flatten(node, prefix=""):
    """(dotted key, value) for every leaf of a parsed YAML document; {fileID...} mappings come back as text."""
    if isinstance(node, dict):
        if "fileID" in node:
            if "guid" in node:
                yield prefix, f"{{fileID: {node['fileID']}, guid: {node['guid']}, type: {node.get('type', 0)}}}"
            return
        for k, v in node.items():
            yield from flatten(v, f"{prefix}.{k}" if prefix else str(k))
    elif isinstance(node, list):
        for i, v in enumerate(node):
            yield from flatten(v, f"{prefix}.{i}")


def find_object(env, path_id):
    for obj in env.objects:
        if obj.path_id == path_id:
            return obj
    raise SystemExit(f"object {path_id} not in bundle")


def owning_prefab(env, obj, path_ids, bundle):
    reader = obj
    while True:
        go = reader if reader.type.name == "GameObject" else reader.read().m_GameObject.deref()
        tr = next(c.component.deref() for c in go.read().m_Component if c.component.deref().type.value in (4, 224))
        father = tr.read().m_Father
        if not father.m_PathID:
            return path_ids[(bundle, go.path_id)]
        reader = father.deref()


def write_if_changed(path, data):
    if os.path.exists(path):
        with open(path, "rb") as f:
            if f.read() == data:
                return
    with open(path, "wb") as f:
        f.write(data)


def main(modpack, mods_dir, sources_dir, assets_dir, banks_dir):
    roots_file = os.path.join(assets_dir, ".mod-assets")
    created = set()
    written = set()
    if os.path.isdir(banks_dir):
        shutil.rmtree(banks_dir)
    for pkg in packages(modpack):
        if pkg in MODS:
            install_mod(pkg, MODS[pkg], mods_dir, sources_dir, assets_dir, created, written, banks_dir)
    # Folders of a previous run that this one didn't recreate (a mod left the modpack).
    if os.path.exists(roots_file):
        with open(roots_file, encoding="utf-8") as f:
            for old in f.read().split():
                if old not in created:
                    shutil.rmtree(os.path.join(assets_dir, old), ignore_errors=True)
                    if os.path.exists(os.path.join(assets_dir, old + ".meta")):
                        os.remove(os.path.join(assets_dir, old + ".meta"))
    # Assets of an older mod version that are gone now.
    for root in created:
        for dirpath, _, files in os.walk(os.path.join(assets_dir, root)):
            for name in files:
                full = os.path.normcase(os.path.abspath(os.path.join(dirpath, name)))
                asset = full[:-5] if full.endswith(".meta") else full
                if asset not in written and not os.path.isdir(asset):
                    os.remove(full)
    with open(roots_file, "w", encoding="utf-8") as f:
        f.write("\n".join(sorted(created)) + "\n")


if __name__ == "__main__":
    if len(sys.argv) != 6:
        print(__doc__)
        sys.exit(2)
    main(*sys.argv[1:])
