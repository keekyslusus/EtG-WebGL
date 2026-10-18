"""Plugin DLL settings for the Unity 6 project (run by apply.sh after the DLLs are installed).

    python unity_plugins.py <project dir>

- Every Assets/Plugins/*.dll gets Auto Reference off (`isExplicitlyReferenced: 1` in its .meta). Unity 6 compiles UGUI
  from source against every auto-referenced plugin, and EtgGame.dll's own global `RaycastResult` hides
  `UnityEngine.EventSystems.RaycastResult` there (CS0115/CS0534 in GraphicRaycaster). A DLL without a .meta gets one
  with a fixed GUID (md5 of "etgmod:<file>", as apply.sh and mods/mod_bundles.py compute it for the mods).
- Our assembly definitions then list the plugins they compile against (`precompiledReferences`): all of them, since
  which DLLs exist depends on the mod pack.
"""
import hashlib
import json
import re
import sys
from pathlib import Path

ASMDEFS = ["Editor/EtgWebBuild/EtgWebBuild.asmdef", "EtgModSmoke/EtgModSmoke.asmdef"]

# The importer block Unity 6 writes for a plugin, Auto Reference off. A shorter one (serializedVersion 1) makes Unity
# warn "below the supported minimum" and re-import the plugins on later script reloads, which can leave the editor's
# script domain broken (EditorApplication bindings, engine modules failing to load).
PLUGIN_IMPORTER = """PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 1
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 1
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def plugin_meta(meta: Path, dll_name: str) -> bool:
    if meta.exists():
        text = meta.read_text(encoding="utf-8")
    else:
        guid = hashlib.md5(("etgmod:" + dll_name).encode()).hexdigest()
        text = f"fileFormatVersion: 2\nguid: {guid}\n"
    if not re.search(r"^  serializedVersion: 2$", text, re.M):
        # No importer block, or a stub: write Unity 6's whole block.
        header = re.match(r"fileFormatVersion: \d+\nguid: [0-9a-f]+\n", text)
        text = header.group(0) + PLUGIN_IMPORTER
    elif re.search(r"^  isExplicitlyReferenced: 1$", text, re.M):
        return False
    elif re.search(r"^  isExplicitlyReferenced: \d$", text, re.M):
        text = re.sub(r"^  isExplicitlyReferenced: \d$", "  isExplicitlyReferenced: 1", text, flags=re.M)
    else:
        text = re.sub(r"^PluginImporter:\n", "PluginImporter:\n  isExplicitlyReferenced: 1\n", text, count=1, flags=re.M)
    meta.write_text(text, encoding="utf-8", newline="\n")
    return True


def main() -> None:
    assets = Path(sys.argv[1]) / "Assets"
    plugins = sorted(p.name for p in (assets / "Plugins").glob("*.dll"))
    changed = sum(plugin_meta(assets / "Plugins" / (name + ".meta"), name) for name in plugins)
    print(f"   plugins: {len(plugins)}, Auto Reference turned off in {changed}")
    for rel in ASMDEFS:
        path = assets / rel
        asmdef = json.loads(path.read_text(encoding="utf-8"))
        asmdef["overrideReferences"] = True
        asmdef["precompiledReferences"] = plugins
        path.write_text(json.dumps(asmdef, indent=4) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
