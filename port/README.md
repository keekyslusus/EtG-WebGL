## Shaders

Three checks of the decompiled shaders: against the game's DX11 bytecode (differential test), D3D11 vs GLES in the
editor (A/B test), and which variant Unity actually selects (variant selection). The bugs they found are listed after.

### Differential test (`bash port/shaderdiff.sh`)

Every variant (shader × pass × keywords × stage) runs twice on D3D11 WARP with the same random constants, textures and
inputs: the original DXBC (dumped by `decompile_shaders.sh`) and the decompiled CG compiled back with `d3dcompiler_47`.
Outputs are compared with a tolerance (NaN/Inf, sign flips and discards separately); for each mismatch inputs are zeroed
one at a time to find the one it depends on.

- Report: `port/build/shaderdiff-report/report.md`, grouped by likely cause; per mismatch
  `failures/<shader>/<variant>.md` with sample inputs/outputs, both disassemblies and the CGPROGRAM.
- `--shader <name>` (repeatable, `Brave/*` = prefix), `--variant <index>`, `--trials N`, `--seed N`. The report prints
  the command that reproduces a failure.
- It tests the files after `fix_shaders.py`, so it also checks that the GLES rewrites are no-ops on D3D11.
- A shader silent for 30 s (an endless loop hangs WARP) is killed and reported as a timeout.
- Not covered: GLES-vs-DX11 differences, which `#if` branch a runtime keyword set picks, derivatives (ddx/ddy are 0),
  render state.

### D3D11-vs-GLES A/B test (`bash port/abtest.sh`)

Catches the engine feeding a shader different data on GLES, which the differential test can't see: the same images are
rendered in batchmode with `-force-d3d11` and `-force-gles30` (Unity closed) and compared. One run (`apply.sh`, D3D11,
GLES, D3D11 again as the noise floor) takes ~2 minutes. Report: `port/build/abtest/report.html` (+ `report.md`).

- `EtgAbTest.cs` draws every material, and every shader without one, on a quad into a 192² RenderTexture (D3D's flipped
  projection only shows in render textures) under `dark`, `dir`, `point`, `spot` and `cookie` lighting (the lit ones
  only for ForwardBase/ForwardAdd shaders). Uniforms set by the game's code get deterministic values, empty texture slots
  a noise texture, `Shader.SetGlobal*` globals in-game values; `_Time` & co. are pinned by a camera command buffer (the
  engine overwrites them on every camera render). UVs are offset by a quarter texel of a 4096 atlas so pixel centres
  don't land on texel boundaries, where D3D and GL pick different neighbours.
- `abtest_report.py` (threshold 4/255): `differs`, `speckle` (scattered blobs or lines: precision of the
  `frac(sin(dot(p, (12.9898, 78.233))) * 43758.5)` hash, point sampling on a texel boundary), `minor` (< 0.2 % of
  pixels), `failed`, `empty` (background on both APIs, not verified).
- Probes (`AbTestProbe.shader`, `AbTestLightProbe.shader`) dump built-in uniforms and forward light data per API: only
  the expected cells differ (`_ProjectionParams.x`, `_ZBufferParams`, clip space, platform defines).
- Unity 6 has no graphics emulation (2017.4 ran both under "WebGL 2.0"): GLES uses the desktop GPU's caps, shader
  level 46 against WebGL 2's 35, so a SubShader or `#pragma target` above 3.5 can be picked here and not in the
  browser. `environment.json` records the WebGL player's built-in defines (`webglDefines`; in 6.6 they include
  `UNITY_LIGHT_PROBE_PROXY_VOLUME`, so fix_shaders.py's LPPV / baked-occlusion rewrites are inactive in the player).
- `apply.sh` runs without `--mods`; `--no-apply` keeps a modded project (mod shaders are compiled from source, the
  report reads their `.shader` files too).
- What is left is precision: PitSpirits/HologramShader (the sin hash feeding `round`/`ceil` dithering) and speckle.
  Not covered: real game frames (Pixelator, post effects), targets other than ARGB32, shadows, 75 materials that draw
  nothing in isolation.

### Variant selection (`bash port/variant-selection.sh`)

Checks which vertex and pixel branches Unity actually selects against the original keyword inventories
(`tools/uscs/dump/<shader>/variants.json`). Unity closed; it reads the installed `.shader`/`.mat` assets and changes
nothing.

```
bash port/variant-selection.sh                 # from PowerShell: python port/variant_selection.py
bash port/variant-selection.sh --static-only
bash port/variant-selection.sh --filter 'Brave/LitCutoutUber$' --out port/build/variant-subset
bash port/variant-selection.sh --report-only
python -m unittest discover -s port -p test_variant_selection.py
```

- Output in `port/build/variant-selection/`: `report.md`, `summary.json`, `issues.json` (by shader and reason), the rows
  (`static.jsonl`, `combinations.jsonl`, `empirical.jsonl`), `engine-coverage.json`, `stripping.json`, `unchecked.json`,
  `editor.log`. IDs are one-based indices into variants.json.
- Material keywords: 2017.4's `m_ShaderKeywords` or Unity 6's `m_ValidKeywords` + `m_InvalidKeywords` (Unity 6
  rewrites a material in that form when it saves it).
- Static: every `multi_compile`/`shader_feature` combination, shaders without materials included, must map to an exact
  original per stage. Default choices (the first option) are explicit assumptions; conflicts, missing originals and
  duplicate keyword sets are reported separately, never replaced by a nearest-variant guess.
- Empirical: `EtgVariantSelection.cs` renders diagnostic copies on D3D11 (float target) whose branches
  output their IDs and keyword bitsets; bodies and render state are replaced so nothing can hide the result. Setups:
  dark, directional, point, spot, each cookie, ForceVertex, SH, an attempted directional shadow. Engine keywords are
  recorded by an independent reference shader. Temporary assets go to `Assets/Editor/EtgWebBuild/_variants` (removed
  in `finally`; a leftover folder is an error); `inputs.json` hashes reject changed inputs.
- `stripping.json` is a conservative retention estimate from materials and Always Included shaders, not a player-build
  trace.
- Not rendered (listed in `unchecked.json`, still checked statically): passes other than first-subshader
  Always/ForwardBase/ForwardAdd, ShadowCaster/Meta, deferred, UsePass, fallbacks, fog, lightmaps, instancing, gameplay
  keyword changes.

Result (899 materials, 22,282 VS/PS selections): every selection matches an exact original. Statically, 58 stage
combinations and 87 material/setup predictions have no original, mostly `VERTEXLIGHT_ON` with `LIGHTPROBE_SH` off,
which Unity never renders (it enables SH there). Nine feature combinations have no retention evidence (the
`EDITOR_VISUALIZATION` Meta options, both `ENABLE_WARP` states of Hidden/Kvant/Tunnel/Kernels with no material).
`SHADOWS_SCREEN` could not be activated and is unverified.

`--legacy` renders bug 17 (the old keyword groups, rebuilt in memory) with synthetic keyword states on the five shaders
it affected (`sensitivity.json` checks the same statically):

```
bash port/variant-selection.sh --legacy \
  --filter 'Brave/(BurnableSprite|DisplacerBeast|LitCutoutUber$|LitBlendUber$|LitCutoutUber_ColorEmissive)' \
  --out port/build/variant-legacy
```

It shows 4,106 wrong fallback selections: spot fails on all five, point on LitCutoutUber, LitBlendUber and
LitCutoutUber_ColorEmissive.

### Decompiler bugs fixed (`port/patches/uscsandbox-fixes.patch`, `port/fix_shaders.py`)

1. Render state referring to material properties lost (`Ref [_StencilVal]`, `Blend [_SrcBlend] [_DstBlend]`).
2. `USILAndOptimizer`: `mask & float` turned into an integer AND with a truncated literal.
3. Float literals printed as `0.0#######`: `1e-10` in RGB->HSV became `0.0`, grey pixels gave NaN.
4. DXBC `log` is `log2`, not the natural `log` (`pow()` got the wrong exponent).
5. A non-identity swizzle of a constant (`cb0[2].xyxy`) dropped: bloom flooded the screen.
6. `sign()` on comparison masks (true = −1 in DXBC) negated, also per vector (`fix_shaders.py`, `fix_bool_subtraction`).
7. A swizzle spanning several constants (`cb0[6].yx`) emitted in declaration order: e.g. `_CameraOrthoSize` /
   `_CameraOrthoSizeX` swapped in the light renderers.
8. An input register holding two packed interpolators (`texcoord.xy` + `texcoord1.xy`) named after the first (Starnest).
9. `sincos` whose sine destination is its source (HueShift).
10. `mask & l(1.0, 0.75)` became `mask ? 1.0 : 0.0` for every component (BraveTilesetWallColorEmissiveCutout).
11. `USILAndOptimizer` kept comparison flags on overwritten registers and picked the value as the condition
    (DarkPortalShader's atan).
12. `ftoi` written as `asint()`: `5.0` became `1084227584` (HologramShader, SinglePassOutline; FinalGunRoom_BG_02 looped
    ~10⁹ times). `ftou`/`utof` added; unhandled instructions now print a warning and a comment.
13. A sample through another texture's sampler (`sample_l ..., t3, s0`) read the sampler's texture (Standard's second
    reflection probe), LOD dropped too.
14. `sample_c` (shadow maps) decompiled as a plain `tex2D`; now `UNITY_DECLARE_SHADOWMAP` + `UNITY_SAMPLE_SHADOW`.
15. Matrix array element: `unity_WorldToShadow[0 + 1]._m01_m11_m21_m31` instead of `unity_WorldToShadow[0]._m01_...`.
16. `unity_Lightmap_HDR`, `unity_DynamicLightmap_HDR`, `_PPLAmbient` assumed to come from the auto-includes (in 2017.4
    they are in UnityCG.cginc): Legacy VertexLit's lightmap pass did not compile.
17. `multi_compile` sets guessed from the n-th keyword of each shortest variant mixed light and material keywords in the
    ForwardAdd passes of LitCutoutUber (~660 materials), LitBlendUber, LitCutoutUber_ColorEmissive, BurnableSprite and
    DisplacerBeast. No `#if` branch matched and the trailing `#else`, a DIRECTIONAL_COOKIE program, lit every point/spot
    light: no distance falloff. Keywords that never occur together and stand in for each other now form a set.

DX11-vs-GLES differences (not decompiler bugs), handled in `fix_shaders.py` under `#if`/macros on the platform, so the
same files are right on D3D11 and GLES:

- `unity_OcclusionMaskSelector` is not set on GLES: the LPPV variants' baked occlusion is 1.0 without
  `UNITY_LIGHT_PROBE_PROXY_VOLUME`, as in Unity's own code.
- Upper-case `LightMode` values are not recognised when compiling from source.
- Light attenuation textures are read from alpha on GLES (`UNITY_ATTEN_CHANNEL`: `.xyzw` on D3D11, `.wwww` on GLES).
- D3D renders into textures with a flipped projection and the game's vertex code derives screen positions from the clip
  position (title screen water/sky/ink, portals, PartialDesaturationEffect, ComputeGrabScreenPos): on GLES those are
  computed in D3D's clip space and only `o.position` is flipped back (`fix_clip_space_flip`).
- Reversed Z: the ShadowCaster passes have D3D's `UnityApplyLinearShadowBias` baked in, GLES gets the GL version
  (`fix_shadow_bias`). The soft particles read depth through `_ZBufferParams` and need nothing.

## Bundles (EtG Web -> 3)

`EtgWebBuild.BuildBundles` builds the 33 WebGL bundles into `Assets/StreamingAssets/Assets` (Unity 6.6 batchmode:
`-executeMethod EtgWebBuild.BuildBundles`, ~4.5 min). `python -I port/bundle_ficheck.py <old dir> <new dir>` compares
the FullInspector state of every MonoBehaviour bundle by bundle (keys, values, object references set), e.g. 2017.4's
`webbuild/StreamingAssets/Assets` against a new build: Unity 6's prefab import once wiped it silently (BossStatues lost
`levelData`; fixed in `EtgFullInspector.cs`). Unity 6.6 vs 2017.4: 280 objects, one differs -
`MetalCubeGuy_TrapVersion`'s `primeAnim` is `null` as in the Steam data (2017.4's import had re-saved the field
default `"attack_tell"`). Expected noise of the build: Foyer scenes lose a `GUILayer` (removed in Unity 6) on some
lights, and `AkInitializer.Awake` (ExecuteInEditMode) throws on `DontDestroyOnLoad`.

## Sound

Wwise is a native Windows library, so the browser gets `port/audio/etg-audio.js`, a small Wwise-like engine on Web Audio
that interprets the banks' object graph. `WebShim.WwiseShim` (patched in for `AkSoundEnginePINVOKE`) forwards the game's
calls (PostEvent, SetSwitch, SetState, SetRTPCValue, positions...) through `EtgWebShim.jslib` to `window.EtgAudio`.

- `wwbank.py` parses `Init.bnk` / `SFX.bnk` (bank version 128).
- `build_audio.py` writes `StreamingAssets/webaudio/` (and copies it into `webbuild/`): `audio.json` (events, nodes,
  busses, effects, measured reverbs and effect models), Opus sound packs, music, the engine and `soundboard.html`
  (`http://localhost:8080/StreamingAssets/webaudio/soundboard.html` with `serve-webbuild.ps1`). Encodes are cached.
- `port/WwiseRef` (.NET 9) is the reference: it loads the game's `AkSoundEngine.dll` with a virtual clock and renders
  offline, one 1024-sample frame per `RenderAudio()`.
  `WwiseRef <root> <cases.json> <out> [--only ids] [--remove-fx bus:index,...] [--remove-node-fx node:index,...]`;
  steps: `postEvent`, `playNode`, `setSwitch`, `setState`, `setRtpc`, `stopAll`.
- `audiodiff.py`: every event (per switch/state), music state and transition, volume curve, mix state and 3D position
  rendered by WwiseRef and by `EtgAudio.render()` in headless Chrome; level, peak, onset, end, spectral centroid and
  balance compared (`port/build/audiodiff/report.html`, worst cases with audio of both). Currently 1293 of 2063 cases
  flagged, event level p10..p90 -2.2..+2.6 dB.
- `listen.py`: Wwise vs engine side by side (`port/build/listen/index.html`) for events through a node effect or
  `--filter REGEX`.
- Mods' banks: `build_audio.py --mods <dir>` (from `apply.sh --mods`) writes `webaudio/mods/<package>/` and `mods.json`;
  the engine merges them into the game's data at start. `audiodiff.py --mods` tests them.

Wwise behaviour found on the way: dB-scaled curves above 0 mean `1 / (1 - y)`, not `y + 1` (y = 1 renders as silence);
3D spatialization "None" (`bits3d & 3 == 0`) is attenuated by distance but not panned; an unpanned mono voice is mixed at
the centre of the panner (-3 dB a side).

### Effects

- Parametric EQ and Delay: built from their parameters (biquads, a feedback delay line).
- Bus reverbs (Dungeon, Foyer / Dimension / Dimension_Delay): impulse responses measured in Wwise by `measure_ir.py`
  (with and without the bus's effects, Wiener deconvolution), played by a ConvolverNode.
- Effects on sounds and containers (41 instances): `measure_fx.py`.

  ```
  python port/audio/measure_fx.py render   # every effect node alone (playNode), with and without its effects
  python port/audio/measure_fx.py ir       # Matrix Reverb / RoomVerb -> port/build/audio/ir/fx<id>.wav, fx.json
  python port/audio/measure_fx.py fit      # the other plugins -> port/build/audio/ir/fx_models.json
  python port/audio/build_audio.py         # ships both in audio.json / ir/
  python port/audio/measure_fx.py check    # engine with/without the models vs Wwise with/without the effects
  ```

  Renders have the bus effects removed and VOL_SFX at 10 (100 clips without the master's limiter); nodes are rendered
  alone because on layered events only one layer carries the effect. Per plugin:

  - **Compressor** (4): the threshold is never reached by these sounds. Left out.
  - **Matrix Reverb** (9) / **RoomVerb** (4): the modulated tail doesn't deconvolve, so per octave band a level and
    decay time are fitted to block energies; shipped as band-limited noise under those envelopes, one ConvolverNode per
    effect shared by its voices.
  - **Guitar Distortion** (10, Overdrive): `dry*x + wet*hp(lp(shape(k*hp(x))/k))` with a fitted knee curve; residual
    -13 to -38 dB. Per voice: IIR filters and a 2x oversampled WaveShaper.
  - **Flanger** (9): Dattorro's comb with the bank's levels and LFO, delays fitted per instance (-21…-62 dB). The
    0.25–5 ms delays are below a render quantum, so the feedback is unrolled into up to 16 taps.
  - **Tremolo** (1): the measured gain curve as a PeriodicWave into a GainNode.
  - **Harmonizer** (4): pitch voices as extra buffer sources following the dry envelope, all delayed by the 1024-sample
    window like Wwise's sync-dry.

  `check`, mean error of what each effect changes, without models -> with: level 2.04 -> 0.21 dB, spectral centroid
  0.186 -> 0.061 octaves, end of the audible part 0.26 -> 0.04 s.

Known differences: some sounds are ~10 dB quieter than in Wwise (deathray, omegaBeam, several guns); a few music starts
are late (Drone, Secret_Room); the HPF is slightly strong; 2D mono events are still ~1.5 dB off in level.

## Mods

A WebGL player can't load BepInEx mods: IL2CPP compiles everything to wasm ahead of time, so there are no new DLLs and
no Harmony / MonoMod detours at runtime. Instead a fixed mod pack is baked into the build: `./apply.sh --mods` installs
the packages of `port/mods/modpack.txt` (plain `./apply.sh` takes them out again), then the usual player build.

- `mods/fetch_mods.sh` copies the packages from the r2modman cache into `etg/mods/` (not in git). A package without a
  `plugins/` folder (Expand the Gungeon) gets its files moved into one, as r2modman installs them.
- `ModBaker/bake.sh` (`port/ModBaker`, .NET Framework 4.7.2, to run the mods' own HarmonyX/MonoMod) loads the game and
  the mods, registers every patch like `PatchAll` with a backend that only records them, lets
  `HarmonyManipulator.Manipulate` build each patched body and copies it into the game DLL with Cecil.
  - Patches on methods that can't be rewritten (UnityEngine, mscorlib: `Object.Instantiate`, `Enum.GetValues`) become
    wrappers in the mod (`ModBaker.Redirects`) and every call is redirected to them. Engine generic overloads that call
    a patched method (`Instantiate<T>`) get copies there too (`Type_Method_GN`), and so do plain overloads of the same
    name that forward to it using only public members: MtG API's finalizer on `Enum.Parse(Type, string, bool)` reads
    a mod character's `PlayableCharacters` name back from the save, and FullSerializer calls `Enum.Parse(Type,
    string)` - without the copy, returning to the Breach after a run as a mod character threw ArgumentException.
  - `[HarmonyReversePatch]` stand-ins get a copy of the original body.
  - MonoMod `new Hook(from, to[, target])`: from/to are recovered by evaluating the reflection code before the
    constructor; the original body moves to `Name_orig_bakedN`. Mods switch hooks on and off at runtime, so each pair
    gets a slot (`ModBaker.BakedHooks::slotN`) and the target becomes "slot applied ? hook(...) : original"; the facade's
    `Hook` ctor / `Apply` / `Undo` / `Dispose` switch it. An instance hook called with a `typeof(X)` target gets a
    stand-in instance (`BakedHook.TargetAs`): IL2CPP null-checks `this`. Hooks on engine methods go on the redirect
    wrapper. Two pairs on one method chain in bake order; a pair applied twice runs once.
  - `MonoLikeLookup`: `AccessTools.DeclaredMethod` answers like Mono where .NET Framework reports an ambiguity.
  - What only runs while patching (IL manipulators, transpilers, Cecil / MonoMod.Utils code) and the `[Harmony*]`
    attributes are stripped; references are set to the identities that ship.
  - `AccessFixer`: Mono in Play Mode checks member access (IL2CPP doesn't) and baked code calls private members across
    assemblies, so each such use goes through a public trampoline `ModBaker.Access.*`.
  - `ArgumentFixer`: Alexandria passes `this` where a SpeculativeRigidbody is expected (works on Mono); IL2CPP's C++
    won't compile it, so the argument becomes `GetComponent<Expected>()`.
  - `ModBaker resources`: IL2CPP reads embedded resources from `Resources/<Assembly>.dll-resources.dat`, which Unity
    2017's WebGL packager leaves out; they are written in il2cpp's format and unpacked to `/Il2CppData/Resources`.
- `port/ModFacades`: `BepInEx` (BaseUnityPlugin, config, logging, Paths, Chainloader, `UnityInput`), `0Harmony`
  (PatchAll does nothing, AccessTools) and `MonoMod.RuntimeDetour` (Hook switches its baked slot).
- The patcher treats the mods like the game. Mod-specific: `BindingList<T>` in `WebShim/SystemStubs.cs`; Alexandria's
  runtime AssetBundles target WebGL (`LiteralShims`); attributes on generic constraints are removed (Unity 2017's mcs
  rejects them with CS0009); Newtonsoft's `DynamicCodeGeneration` is false; `LogSwallowedExceptions` logs what chosen
  `catch { }` blocks discard; `GetCallingAssembly` callers pass their type (`Patcher/CallerAssembly.cs`), since release
  WebGL builds have no stack to answer it from.
- Files the mods read go into `StreamingAssets/mods/files.pack` (`mods/build_pack.py`). Boot unpacks it to
  `/mods/BepInEx/plugins/<package>/...` (DLLs as empty placeholders: mods find their folder through them) and the
  Chainloader (`WebShim/ModFiles.cs`) starts on the `sceneLoaded` of the first game scene (MtG API's SGUI needs a
  `dfControl` by its first OnGUI). `Paths.GameRootPath` is `<persistentDataPath>/modded` in IndexedDB; the Chainloader
  facade creates `SilverJacketData/` there (Silver Jacket writes to it on every pickup but never creates it).
- Play Mode (`EtG Web/Play From Boot`) runs the mods from `etg/mods-runtime/` without a WebGL build.
- **Our own mods**: a `modpack.txt` line with version `local` is a mod whose source lives in `port/mods/<name>`;
  `fetch_mods.sh` skips it and `ModBaker/bake.sh` builds it (`dotnet build`, net35 against the game's assemblies and
  the fetched packages) into `etg/mods/<name>/plugins`, the layout of a Thunderstore package. The DLL also runs on
  Steam (copy it into r2modman's `BepInEx/plugins`).

### Sukeban (our character mod, `port/mods/Sukeban`)

A playable character built on Alexandria's CharacterAPI (`Loader.BuildCharacter`, data and sprites as embedded
resources), on Convict's frames; the past and the unlock come later.

- **Sprites**: `Character/playersheet.png` is Convict's atlas (512 x 1024) with every frame redrawn in place, so
  Alexandria's sheet replacement keeps his UVs, offsets and animation library. (Its per-sprite replacement can't be
  partial: it points the shared materials at the new page, breaking every frame not replaced.)
  `tools/build_sheet.py` makes it from the ripped collection: all frames recolored to her palette; idle and run in
  four directions (with the hand variants) and the dodges' upright / face-on frames get one of four drawn heads
  (dark bob, design v4) placed by his eyes or hair, a cropped sailor top with scarf, a belt raised three rows and a
  long skirt over the legs, geta. The rolling dodge frames and the rest (Breach idles, death, pitfall...) are only
  recolored so far. `tools/extract_frames.py` cuts a collection into PNGs for reference.

- **Ninth Inning** (`skb:ninth_inning`): a copy of gun 616, the nailed Casey that the #NAILBAT synergy ("Careful
  Iteration": Casey + one of guns 26, 143 / items 457, 118) switches to, without Casey's +2 curse. Charged swings reflect bullets (Casey's
  `MegaReflection`). 616's sprites; the Ammonomicon icon falls back to Casey's.
- **Koshien Dirt** (`skb:koshien_dirt`, active, 450 damage cooldown): pours a home base for 15 s, a flat sprite of
  3 x 1.5 tiles drawn like a landed supply crate (FG_Critical layer, Background sorting layer; on BG_Critical the floor
  sometimes covered it). Standing inside its ellipse multiplies `ChargeAmountMultiplier` by 2 (what `Gun` uses for
  charge time); the base is dimmed otherwise and a sound plays on stepping in. The smoke test's `characters` phase
  uses it in five rooms and saves screenshots (`port/build/modsmoke/sukeban-room*.png`).
- **Armored Elbow Guard** (`skb:armored_elbow_guard`): +1 armor on the first pickup; a Harmony postfix on
  `PassiveReflectItem.ReflectBulletsInRange` (all reflections, the bat's included) gives +1.5 speed for 3 s.
- Names and descriptions in English and Russian. Placeholder icons: `tools/placeholder_sprites.py`.

### Expand the Gungeon (mods with their own AssetBundles, sound banks and shaders)

ApacheThunder's Expand the Gungeon 3.0.8 brings three Windows AssetBundles (`expandsharedauto` 17 MB, `expandshaders`:
8 shaders, `expandaudio` 53 MB: its Wwise bank) and 57 MonoMod hooks.

- **Bundles from source** (`mods/mod_bundles.py`, run by `apply.sh --mods`; needs `pip install UnityPy` and
  `git clone -b BepInEx https://github.com/ApacheThunder/ExpandTheGungeon tools/src/ExpandTheGungeon` at the commit
  named in `mod_bundles.py`). The repository has the bundles' Unity projects but no `.meta` files; `mod_bundles.py`
  recovers paths, bundle names, import settings and the 21 referenced GUIDs from the shipped bundle (UnityPy):
  - each container entry is copied to the same path in our project (the mod loads many assets by full path) with its
    `assetBundleName`, so EtG Web -> 3 builds the three bundles for WebGL. Text assets get the bundle's bytes (git
    normalised CRLF, the mod splits on `'\n'`);
  - textures get the bundle's format, point filtering, no mips, readable, `alphaIsTransparency` where needed;
  - prefab references are resolved against the shipped copy: scripts point into `Plugins/ExpandTheGungeon.dll`
    (fileID = first 4 bytes of MD4("s\0\0\0" + namespace + class)), assets get that GUID in their `.meta`. The mod DLLs'
    GUIDs are fixed (md5 of `etgmod:<file>`, `apply.sh`);
  - `expandaudio`'s bank becomes an empty stand-in (sound comes from the audio pack).
  - In the build, `BootLoader` preloads the mods' bundles from the manifest, `BundleShim.LoadFromFile` of a plugin path
    returns the bundle named like the file, `build_pack.py` packs the Windows bundles as empty placeholders. Play Mode
    hands out runtime copies of mod-bundle prefabs (the editor refuses `Renderer.material` on prefab assets, and
    AddComponent would change the project).
- **Shaders**: compiled by Unity from the mod's sources. `abtest.sh --filter Expand`: 4 of 8 identical, the rest differ
  only by noise-hash precision (VHS, Cinematic) or texel-boundary samples (GlitchScreen).
- **Video**: the WebGL VideoPlayer only plays URLs, so `VideoPlayer.clip = c` becomes `MediaShim.SetVideoClip`, which
  plays `StreamingAssets/mods/video/<clip name>.mp4` (copied by `mod_bundles.py`, H.264/AAC). The clips' `.meta` need
  `useLegacyImporter: 0` (otherwise 2017.4 imports MovieTextures, which WebGL drops). The Windows-only old-film pass sees
  Windows (`WindowsPlatformChecks`, as on Steam).
- **Sound**: converted like the game's bank (`build_audio.py --mods`); sounds over 5 s are fetched when played, the rest
  is one 0.5 MB pack, routed into the game's Master Audio Bus. `audiodiff.py --mods` (at volume 80): 20 of 116 cases
  flagged, level median +0.6 dB.
- **Libraries**: Ionic.Zip (ThreadPool work runs at once, `AssetBundle.LoadFromStream` -> `BundleShim`).
- **Game assets by their original names** (`bundle_names.py` -> `bundle_renames.tsv`, applied by `apply.sh`): the mod
  loads game assets by name, and AssetRipper had exported 38 under other names ("," as "_", 37 shaders of
  `shared_auto_001` as `<name>_0.shader`). They are renamed back (only bundles change: EtG Web -> Rebuild Bundles Only).
- **Memory**: the build uses 2032 MB of wasm heap (2017.4's maximum); 1536 MB ran out while the mod built its rooms.
- **Old West floor** (mod bug, also on Steam; `Patcher` `FlowNodeFixes`): its MANDATORY shrine room has only
  conditional candidates, so on a fresh save the generator threw and the load never ended. The node is OPTIONAL now.
- **Glitch chest floor** (mod bug, also on Steam; `FlowNodeFixes`): `Custom_GlitchChest_Flow`'s boss node is a CONNECTOR
  with a boss room table, so the generator backtracked forever (~1 in 6 glitch floors). The node is BOSS now, like in
  `Custom_Glitch_Flow`.
- **Portable Ship** (mod bug, also on Steam; `Patcher` `NullSafeCalls` -> `WebShim.ModFixes`): `ResetConfig` (drop,
  destroy) clears `LaserShootPoints`, which only exists once the ship was used, so an unused Portable Ship threw
  NullReferenceException.
- **Corrupted enemies on glitch floors** (mod bug, also on Steam; `Patcher` `SpriteIdClamps`): `ApplyCustomTexture` gives a
  BulletMan copy an NPC's sprite collection (15-153 sprites) but keeps sprite ID 363; tk2d only clamps an ID that
  changes, so unless the sprite counted as off-screen `AIShooter.Initialize` threw IndexOutOfRange in
  `GetUntrimmedBounds`. The ID is clamped to the new collection.
- **Mods catch NullReferenceException**: e.g. Expand's `ExpandCompanionManager.LateUpdate` calls
  `m_AIShooter.ToggleGunAndHandRenderers` in try/catch on companions without an AIShooter (Sonic). So release builds keep
  null and bounds checks (`FullWithoutStacktrace`, `EtgWebBuild.ApplyPlayerSettings`): with "explicitly thrown only"
  that call wrote through null and halted the player ("memory access out of bounds"). Builds write
  `Build/<name>.symbols.json` to name the `wasm-function[N]` frames of such a crash.
- **What `Object.Destroy` disables** (Unity 6; `RewriteCalls` -> `WebShim.DestroyShim`, every call in the game and the
  mods): Unity 6 clears the enabled flag of every component under a destroyed GameObject inside the call, so what is
  moved out of it before the destruction stays disabled for good. EtG relies on survivors working:
  `GameUIRoot.RemoveControlFromMotionGroups` destroys a motion-group panel and then moves its control to a new one, so
  in Russian & co. the pickup notification showed only the item's sprite (box and texts disabled); an IL scan finds ~50
  more methods in the game and mods that move, re-enable or reactivate objects after `Destroy`. `DestroyShim` notes the
  enabled behaviours and renderers below the GameObject (not for childless ones), lets Unity destroy it as usual and
  next frame enables again those that survived. Tried and dropped (full Play Mode smoke test): deferring the destroy
  (destroyed enemies kept running LateUpdate: `AIBulletBank.LateUpdate` NRE, failed / hung flows) and deactivating at
  once (stopped the notification's coroutine).
- **`IntVector2` without boxing** (`Patcher` `EquatableStructs`): it has `Equals(IntVector2)` but doesn't declare
  `IEquatable<IntVector2>`, so `EqualityComparer<IntVector2>.Default` boxed on every `List.Contains/Remove` and
  `Dictionary` lookup. The dungeon generator does millions of those in one frame (`DungeonData.ExciseElbows`), and WebGL's
  GC only runs between frames: big floors (The Backrooms) ran the 4 GB heap out of memory in the release build
  (`abort("OOM")`). With the interface declared, Backrooms loads in 16 s instead of 24 s in Play Mode as well.
- `Patcher verify` also fails on a branch whose target isn't an instruction (a short branch overflowed by inserted code
  crashes il2cpp after 20 minutes) and on a parameter with HasDefault but no Constant (the editor's Mono aborts in
  `GetParameters()` on any stack trace through it; seen with 2017.4's Mono 2.x). `bash port/monosigcheck.sh` checks the
  latter with the editor's own Mono (6.6's MonoBleedingEdge, `port/MonoSigCheck`, ~5 s).

### Smoke test (EtG Web -> Mod Smoke Test)

An unattended Play Mode run over everything the mods add. **EtG Web -> Mod Smoke Test -> Everything** (or Items + Shop,
Enemies, Floors + Flows) does Play From Boot (don't touch the Game view while it runs), waits for the mods and the title
screen, starts a run as the Pilot with MtG API's godmode, then:

1. every mod item (MtG API's `Game.Items` without `gungeon:` IDs, plus `ModItems`) is given like the `give` command;
   guns are fired, reloaded and dropped, actives used and dropped, passives dropped;
2. four guns, actives and passives each are bought from a `ShopItemController` on the first floor;
3. every mod enemy (`ExpandEnemyDatabase`, database entries without a bundle path) is spawned next to the player and
   killed after 3 s;
4. every mod floor (`LoadCustomLevel`) and flow (`ExpandDungeonFlow.KnownFlows` without `_Copy`, loaded the way the
   game / mod does: boss rush flows in `GameMode.BOSSRUSH`, glitch chest flows with `isGlitchFlow` on Phobos and Space,
   the rest with their tileset); the player then warps into up to 12 non-boss rooms. A `diag` step lists Expand's room
   tables with entries the generator can't use.
5. every mod character (MtG API's `ETGModConsole.Characters` beyond the game's twelve, where Alexandria's CharacterAPI
   registers them) is switched to like the `character` command; its starting guns / items, health and armor are
   noted, its actives used and its gun swung charged at an enemy bullet put in front of it, with the player stats each
   changed. Last a `breach` step does "Quit to Breach" and checks each one's select flag (`NPC_FoyerCharacter_<name>`).

Phase `batching` (not in `all`, `SmokeBatching.cs`): in the first floor's start room, empty and with 12 shooting
enemies, dynamic batching is switched on and off through the player settings (restored after) and `UnityStats` draw
calls are averaged per state (`port/tasks/unity6-migration.md`).

Each step is cleaned up after (items, spawns, health, time scale, input); a lost run is restarted. Errors, exceptions
and asserts from the log are counted per step, grouped by message + first frame. Steps time out (items 30 s, floors
180 s). Categories by the top frames:

- **port**: access/type-load/missing-member exceptions, the baker's code (`ModBaker.*`, hook slots), WebShim or a facade;
- **mod**: a mod assembly;
- **playmode**: what only the editor forbids (MonoBehaviour constructor calls, prefab objects, destroying assets),
  including fields a refused constructor leaves null (Portable Ship, Iceberg Shavings, Ferryman Oar);
- **other**: the game or unclear; **smoke**: the test itself.

The game's known noise (`DataPrivacy`, `NONE save flag`, `select_idle`, `_StencilVal`...) is only counted. Report:
`port/build/modsmoke/report.md` + `report.json`, rewritten after each step. `port/build/modsmoke/only.txt` (IDs or name
parts, one per line) limits a run to those steps.

Unattended: `bash port/modsmoke.sh [phases]` (Unity closed) runs `monosigcheck.sh`, opens the project on GLES 3.0,
requests the run through `port/build/modsmoke/request` and waits for the editor to exit. A watchdog kills the editor when
Editor.log is silent for `HANG_MINUTES` (6) or it comes up with a broken script domain, closes the Bug Reporter without
sending, and resumes from `report.json` with the running step reported as **hang** (`MAX_ATTEMPTS` 8; `RESUME=1` to
continue by hand). Before each action the test logs `[ModSmoke]   . <action>`, so after a Mono crash the last such line
in Editor.log says what was running.

Code: `UnityAssets/EtgModSmoke/` (mods only through reflection, so it compiles without them) and
`Editor/EtgWebBuild/EtgModSmokeMenu.cs` (phases go through `Temp/modsmoke.pending`, since Play Mode reloads the domain).
Not covered: whether things look or sound right, mod NPCs / shrines outside the visited rooms, synergies, real boss
fights, co-op.

**In the browser** (WebGL development build, IL2CPP: what Play Mode can't show): open the page with
`?smoketest[=phases][&only=a,b][&resume]` (`SmokeWeb.cs` reads `Application.absoluteURL`; without the parameter no test
exists). The page presses Play by itself; the report is written to `persistentDataPath/modsmoke` (IndexedDB) after every
step and handed to the page (`EtgModSmoke.jslib` -> `window.EtgSmoke`), which downloads it at the end. Served by
`port/site/serve.py --reports port/build/modsmoke/web` (launch.json `webbuild6`), the page also posts `report.md` /
`report.json` there after every step. "restart before X" reloads the page with `&resume`, and so does a runtime abort
(`Module.onAbort`; the last 400 console lines go to `console.txt` first, the running step becomes **hang**). A frozen
page needs a manual reload with `&resume`. Keep the browser pane visible: a hidden one barely gets frames.
