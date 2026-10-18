"""Copies the web page (port/site) into a WebGL build folder, next to its Build/, and writes etg-build.js there.

Unity's default template writes its own index.html on every player build; EtgWebBuild.BuildPlayer
does the same copy afterwards (CopySite), this script is for doing it by hand.
etg-build.js tells the page the player's file names (Unity 6 names them after the output folder:
Build/webbuild6.loader.js, .data.gz, .framework.js.gz, .wasm.gz) and the product names the loader wants.

Usage: python port/site/install.py [webbuild dir, default webbuild6]
"""
import json
import os
import re
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
FILES = ["index.html", "etg-site.js", "etg-site.css"]
PROJECT_SETTINGS = os.path.join(HERE, "../../ripped/Unity6Project/ProjectSettings/ProjectSettings.asset")
# Build/ file kinds by name; compressed builds add .gz / .br (or .unityweb with the decompression fallback)
KINDS = {"loader": r"\.loader\.js$", "framework": r"\.framework\.js(\.\w+)?$", "data": r"\.data(\.\w+)?$",
         "code": r"\.wasm(\.\w+)?$"}


def build_info(out):
    names = sorted(os.listdir(os.path.join(out, "Build")))
    info = {}
    for kind, pattern in KINDS.items():
        found = [n for n in names if re.search(pattern, n)]
        if len(found) != 1:
            raise SystemExit("Build/ in %s: expected one %s file, found %s" % (out, kind, found or "none"))
        info[kind] = "Build/" + found[0]
    settings = open(PROJECT_SETTINGS, encoding="utf-8").read()
    for key, field in (("companyName", "companyName"), ("productName", "productName"),
                       ("productVersion", "bundleVersion")):
        m = re.search(r"^  %s: (.*)$" % field, settings, re.M)
        info[key] = m.group(1).strip() if m else ""
    return info


def write_build_js(out, info):
    with open(os.path.join(out, "etg-build.js"), "w", encoding="utf-8", newline="\n") as f:
        f.write("// Written by port/site/install.py / EtgWebBuild.CopySite: the player's files in Build/.\n")
        f.write("window.EtgBuild = " + json.dumps(info, indent=2) + ";\n")


def main():
    out = os.path.normpath(sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "../../webbuild6"))
    if not os.path.isdir(os.path.join(out, "Build")):
        raise SystemExit("No Build/ in " + out)
    info = build_info(out)
    for name in FILES:
        shutil.copy(os.path.join(HERE, name), os.path.join(out, name))
    shutil.copytree(os.path.join(HERE, "assets"), os.path.join(out, "assets"), dirs_exist_ok=True)
    write_build_js(out, info)
    print("site -> " + out + " (" + info["loader"] + ")")


if __name__ == "__main__":
    main()
