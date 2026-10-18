# Web page around the player

Replaces Unity's default WebGL template page.

- `assets/`: `04B_03.ttf`, the game's pixel font (copied from the ripped project by
  `extract_assets.py`), and `ico.ico`, the tab icon.
- `index.html`: title card with download progress (MB) and Play / Fullscreen / Export / Import;
  after Play the game fills the window and a small icon toolbar shows up in the top right corner
  while the mouse moves.
- `etg-site.js`: pixel icons (string grids -> SVG), save export/import, fullscreen, the Unity 6
  loader glue (`createUnityInstance` on a canvas it adds to `#game`; the canvas needs an id, Unity
  binds its input to `"#" + id`), frame monitor (F3 or `?fps`: FPS, frame / CPU / GPU time; F4 or `?fps=profile`: the CPU
  time split into WebGL calls, the audio engine, console output and the game's own code).
- `install.py` copies the page into `webbuild6/` (or the folder given) and writes `etg-build.js`:
  the player's file names in `Build/` (Unity 6 names them after the output folder,
  `webbuild6.loader.js`, `.data.gz`, `.framework.js.gz`, `.wasm.gz`) and company / product / version for
  the loader. `EtgWebBuild.BuildPlayer` does the same after every player build (menu: EtG Web -> Copy
  Site Page).
- `serve.py` (run by `serve-webbuild.ps1 [-Dir webbuild6]`): http.server plus `Content-Encoding: gzip`
  / `br` and the real type (`application/wasm`) for Unity 6's pre-compressed `Build/*.gz`; without the
  header the loader fails ("still gzip-compressed").

## Fullscreen

Fullscreen is always the whole page, so the toolbar keeps working, and Esc is kept for the game
with `navigator.keyboard.lock` (Chrome/Edge: hold Esc to leave). W, T and N are locked as well, so
Ctrl+W / Ctrl+T / Ctrl+N don't close the tab or open another mid-game; outside fullscreen browsers
don't let a page cancel those, so after Play the page asks "Leave site?" on close or reload
(`Etg.guardUnload`). The game can ask for fullscreen
itself: the RECOMMENDED visual preset (the default) forces PreferredFullscreenMode.FULLSCREEN
(`GameOptions.CurrentVisualPreset` setter, `BraveOptionsMenuItem.Update`) ->
`DoSetResolution(.., true)` -> `Screen.SetResolution(.., fullscreen)`, which Unity defers to the
next click or key press (e.g. Play) and runs as `Module.canvas.requestFullscreen()`. `Etg.startGame` overrides that method on the canvas to
go through the page instead. The game's Options -> Windowed still works (Unity calls
`document.exitFullscreen`), and Pixelator keeps the saved mode in sync with the real state.

## Saves

Unity (2017.4 and 6.6, checked on a 6.6 probe build) keeps `persistentDataPath` in IndexedDB: database `/idbfs`, store `FILE_DATA`, key =
full path, value `{timestamp, mode, contents}`. The folder is `/idbfs/<md5 of the page's folder
URL without the trailing "/">` (e.g. md5("http://localhost:8080"); Unity 6's loader has no MD5, so
`etg-site.js` brings its own), so every page in one folder shares saves (old 2017.4 saves stay found), but another host/port is another origin with its own saves. The same folder held 2017.4's
asset-bundle cache (~70 MB), so export takes only `SlotX.save/.options/.backup.*` and
`PlayerPrefs`, as a store-only zip. Import takes those files or zips of them (deflate via
`DecompressionStream`; a zipped Steam save folder works, `steam_autocloud.vdf` is skipped),
writes them straight into IndexedDB and reloads the page: the running game keeps its own copy in
memory and its next `FS.syncfs` would overwrite or delete the imported files.

## Gotcha

The player rewrites the canvas' inline style once it runs (2017.4 dropped UnityLoader's `width:100%`), so
`etg-site.css` forces `.game canvas` to 100% with `!important`; otherwise after a resize or
fullscreen the canvas no longer follows the container and the game renders into a stale corner.
