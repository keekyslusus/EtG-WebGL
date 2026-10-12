"""Copies the game's art the web page (port/site) uses from the ripped project.

Writes port/site/assets/:
  04B_03.ttf         the game's pixel UI font
(assets/ico.ico, the tab icon, is kept in git as it is.)

Usage: python port/site/extract_assets.py
"""
import os
import shutil

HERE = os.path.dirname(os.path.abspath(__file__))
UI = os.path.normpath(os.path.join(HERE, "../../ripped/ExportedProject/Assets/art/ui"))
OUT = os.path.join(HERE, "assets")


def main():
    os.makedirs(OUT, exist_ok=True)
    shutil.copy(os.path.join(UI, "04B_03__.ttf"), os.path.join(OUT, "04B_03.ttf"))
    print("04B_03.ttf ->", OUT)


if __name__ == "__main__":
    main()
