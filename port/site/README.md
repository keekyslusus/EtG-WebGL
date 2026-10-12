# Web page around the player

Replaces Unity's default WebGL template page.

- `assets/`: `04B_03.ttf`, the game's pixel font (copied from the ripped project by
  `extract_assets.py`), and `ico.ico`, the tab icon.
- `index.html`: title card with download progress (MB) and Play / Fullscreen / Export / Import;
  after Play the game fills the window and a small icon toolbar shows up in the top right corner
  while the mouse moves.
- `etg-site.js`: pixel icons (string grids -> SVG), save export/import, fullscreen, UnityLoader
  glue, frame monitor (F3 or `?fps`: FPS, frame / CPU / GPU time; F4 or `?fps=profile`: the CPU
  time split into WebGL calls, the audio engine, console output and the game's own code).
- `install.py` copies the page into `webbuild/`; `EtgWebBuild.BuildPlayer` does the same after
  every player build (menu: EtG Web -> Copy Site Page).

## Fullscreen

Fullscreen is always the whole page, so the toolbar keeps working, and Esc is kept for the game
with `navigator.keyboard.lock` (Chrome/Edge: hold Esc to leave). The game can ask for fullscreen
itself: the RECOMMENDED visual preset (the default) forces PreferredFullscreenMode.FULLSCREEN
(`GameOptions.CurrentVisualPreset` setter, `BraveOptionsMenuItem.Update`) ->
`DoSetResolution(.., true)` -> `Screen.SetResolution(.., fullscreen)`, which Unity defers to the
next click or key press (e.g. Play) and runs as `Module.canvas.requestFullscreen()`. `Etg.startGame` overrides that method on the canvas to
go through the page instead. The game's Options -> Windowed still works (Unity calls
`document.exitFullscreen`), and Pixelator keeps the saved mode in sync with the real state.

## Saves

Unity 2017 keeps `persistentDataPath` in IndexedDB: database `/idbfs`, store `FILE_DATA`, key =
full path, value `{timestamp, mode, contents}`. The folder is `/idbfs/<md5 of the page's folder
URL without the trailing "/">` (e.g. md5("http://localhost:8080")), so every page in one folder
shares saves, but another host/port is another origin with its own saves. The same folder holds
Unity's asset-bundle cache (~70 MB), so export takes only `SlotX.save/.options/.backup.*` and
`PlayerPrefs`, as a store-only zip. Import takes those files or zips of them (deflate via
`DecompressionStream`; a zipped Steam save folder works, `steam_autocloud.vdf` is skipped),
writes them straight into IndexedDB and reloads the page: the running game keeps its own copy in
memory and its next `FS.syncfs` would overwrite or delete the imported files.

## Gotcha

The player rewrites the canvas' inline style once it runs (drops UnityLoader's `width:100%`), so
`etg-site.css` forces `.game canvas` to 100% with `!important`; otherwise after a resize or
fullscreen the canvas no longer follows the container and the game renders into a stale corner.
