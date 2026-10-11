"""Copies the web page (port/site) into a WebGL build folder, next to its Build/.

Unity's default template writes its own index.html on every player build; EtgWebBuild.BuildPlayer
does the same copy afterwards, this script is for doing it by hand.

Usage: python port/site/install.py [webbuild dir]
"""
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
FILES = ["index.html", "etg-site.js", "etg-site.css"]


def main():
    out = os.path.normpath(sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "../../webbuild"))
    if not os.path.isdir(os.path.join(out, "Build")):
        raise SystemExit("No Build/ in " + out)
    for name in FILES:
        shutil.copy(os.path.join(HERE, name), os.path.join(out, name))
    shutil.copytree(os.path.join(HERE, "assets"), os.path.join(out, "assets"), dirs_exist_ok=True)
    print("site -> " + out)


if __name__ == "__main__":
    main()
