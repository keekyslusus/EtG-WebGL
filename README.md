# EtG-WebGL

Unofficial browser port of Enter the Gungeon (Unity 2017.4, Mono)
This repository contains only the porting tools - the game files must come from your own purchased copy.

## Requirements

- Enter the Gungeon 2.1.9 (Steam) - the game folder
- [Unity 2017.4.27f1](https://unity.com/releases/editor/archive) + the **WebGL Build Support** module
- .NET SDK 9
- [AssetRipper](https://github.com/AssetRipper/AssetRipper/releases) 2.x
- Git Bash (for `apply.sh`), Python 3 (for `setstack.py`)

## Layout

```
Enter the Gungeon/      game files (not in git)
ripped/ExportedProject/ Unity project from AssetRipper (not in git)
webbuild/               finished WebGL build (not in git)
port/
  WebShim/      runtime shims: threads, bundle preloading, saves in IndexedDB, gamepads (XInputShim),
                stubs for Unity types stripped from WebGL, loading screen
  site/         the web page around the player (index.html, save export/import; port/site/README.md)
  Patcher/      Mono.Cecil patcher for the game DLLs + `verify` (checks references against the WebGL libraries)
  UnityAssets/  what gets copied into the Unity project: "EtG Web" menu, link.xml, jslib
  ShaderDiff/   differential test of the decompiled shaders against the game's DX11 bytecode (see below)
  apply.sh      builds WebShim/Patcher, patches the DLLs, installs everything into the project
  decompile_shaders.sh / fix_shaders.py / shaderdiff.sh   shader decompilation, gles3 fix-ups, testing
  abtest.sh / abtest_report.py   the same frames in the editor on D3D11 and GLES, compared (port/README.md)
  variant-selection.sh / variant_selection.py   original variant inventory vs actual Unity branch selection
  setstack.py   increases the stack size of an exe (needed for asm2wasm)
```

## Building

1. Copy the game into `Enter the Gungeon/`.
2. **AssetRipper** (settings: *Script Export Format* = `Dll Export Without Renaming`,
   *Shader Export Format* = `Decompilation`) -> open the game folder -> *Export Unity Project*
   to `ripped/`.
3. Shaders: AssetRipper writes stubs in their place (`DummyShaderTextExporter`), so
   clone [USCSandbox](https://github.com/nesrak1/USCSandbox) into `tools/src/USCSandbox`, apply
   `git apply --ignore-whitespace port/patches/uscsandbox-fixes.patch` (fixes for the decompiler bugs listed under
   *Shaders* below, plus the `--dump` mode), build it into `tools/uscs`
   (`dotnet build -c Release -o tools/uscs` + `Files/classdata.tpk` next to the exe) and run
   `bash port/decompile_shaders.sh` — it decompiles the DX11 bytecode of all game shaders into `tools/uscs/out`
   (and dumps the original bytecode of every variant into `tools/uscs/dump` for `shaderdiff.sh`).
4. **With Unity closed** -> `bash port/apply.sh`
   Patches the DLLs, renames `Assembly-CSharp*` → `EtgGame*` (keeping GUIDs),
   replaces the stub shaders with the decompiled ones, removes Windows bundles and Wwise banks
   from StreamingAssets, verifies references.
5. **Once**: `asm2wasm` from Unity 2017 crashes with a stack overflow on a large assembly.
   Increase its stack size (requires admin rights, the original is kept as `.orig`):
   ```
   python port/setstack.py <copy of asm2wasm.exe> 1073741824
   ```
   and put the copy in place of
   `<Unity>/Editor/Data/PlaybackEngines/WebGLSupport/BuildTools/Emscripten_FastComp_Win/binaryen/bin/asm2wasm.exe`.
6. Open `ripped/ExportedProject` in Unity 2017.4.27f1 -> menu **EtG Web -> Do Everything (1-4)**
   (boot scene, WebGL 2 / wasm / .NET 2.0 settings, bundles, dev build into `webbuild/`).
   After that, when the port changes: `apply.sh` -> Ctrl+R in Unity -> **4. Build WebGL Player**.
   If only assets inside bundles changed, **Rebuild Bundles Only -> webbuild** is enough. But shaders from
   *Always Included Shaders* (Graphics Settings: `Brave/PlayerShader`, `LitCutoutUber`, `LitBlendUber`…)
   live in the player data, and `Shader.Find` in the browser uses exactly those: after editing shaders step 4 is required.
7. Run it from `webbuild/` through any local HTTP server (it doesn't work from `file://`).

Unity build log: `%LOCALAPPDATA%\Unity\Editor\Editor.log`.

## Quick testing in Play Mode

Most errors are runtime errors - they're faster to catch in the editor.
**EtG Web -> Play From Boot**: opens `Boot.unity` and starts Play Mode with bundle simulation.
Bundles from StreamingAssets (built for WebGL) aren't loaded: `BundleShim` serves assets straight from the project
via `AssetDatabase` by their `assetBundleName`. Scenes from bundles (`tt_foyer`, `Foyer`, `Foyer_Coop`, `tt6`) are
added to Build Settings for the duration of Play Mode and removed after exiting.

- The **EtG Web -> Simulate Bundles In Play Mode** flag (EditorPrefs, on by default) also applies to regular Play.
- The patcher redirects all instance `AssetBundle` calls in the game (`LoadAsset*`, `GetAllScenePaths`) to `BundleShim`;
  for a real bundle the original method is called, so the WebGL build works as before.
- A stub bundle is an `AssetBundle` object with no native part, and `== null` returns `true` for it. The only effect
  is that the game requests the bundle again from `ResourceManager`, where it is already cached.
- Play Mode saves go to `etg/editor-saves`, not `LocalLow/Dodge Roll/Enter the Gungeon`. The project has the same
  company/product name as the game, so without the redirect the editor would write to the real Steam save.
- **Open the project via `open-gles30.ps1`** (Unity with `-force-gles30`, `<OpenGL ES 3.0>` in the window title):
  then shaders are compiled and rendered for GLES3, like in the browser, and the image matches the WebGL build.
  In normal mode the editor renders through D3D11, and graphics may differ noticeably: the shaders are decompiled
  from DX11 and adjusted for gles3 (`fix_shaders.py`). Play From Boot additionally enables
  **Edit -> Graphics Emulation -> WebGL 2.0** (the default is WebGL 1.0, while the player is WebGL 2 only).
- What Play Mode still won't show: shaders from *Always Included Shaders* in the browser come from the player data
  (after editing them step 4 is required, not just Rebuild Bundles); the editor compiles shader variants on demand,
  while the build strips `shader_feature`s not enabled in materials; Mono in the editor vs IL2CPP in the player.

## Shaders

The game ships its shaders only as DX11 bytecode (DXBC). USCSandbox decompiles them into ShaderLab/CG,
`fix_shaders.py` adapts the result to gles3, `apply.sh` puts it into the project.

## Limitations

- **No sound**: Wwise is native, banks are for Windows. Plan — a shim `AkSoundEngine.PostEvent` → `AudioSource`.
- WebGL 2 only (the decompiled shaders don't compile for WebGL 1).
- Floor generation is single-threaded — slower than on PC.
- Game ownership verification (Steam sign-in) isn't implemented yet.
