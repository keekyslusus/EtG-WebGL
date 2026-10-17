### Differential test (`bash port/shaderdiff.sh`)

Finds decompiled shaders that compute something else than the original. For every variant (shader × pass × keywords × vertex/fragment): the original DXBC (dumped by `decompile_shaders.sh`) and the decompiled CG of that variant, compiled back with `d3dcompiler_47` (`vs_4_0`/`ps_4_0`, Unity's auto-includes and defines, the variant's keywords), both run on D3D11 WARP with the same random constants, textures (random mip chains), vertex attributes / interpolants; outputs are compared with a tolerance, NaN/Inf, sign flips and discards reported separately. For each mismatch it zeroes one input at a time to tell which constant/texture/input the difference depends on.

- Report: `port/build/shaderdiff-report/report.md`, grouped by likely cause; per mismatch
  `failures/<shader>/<variant>.md` with the inputs and outputs of example samples, the disassembly of both
  programs (`.original.asm`, `.recompiled.asm`) and the pass's CGPROGRAM.
- `--shader <name>` (repeatable, `Brave/*` = prefix), `--variant <index>`, `--trials N`, `--seed N`.
  Everything is a function of the seed, the report prints the command that reproduces a failure.
- It tests the text after `fix_shaders.py`, the same files Unity gets: every GLES rewrite there is under `#if` on the
  platform (or follows `UNITY_ATTEN_CHANNEL`), so the test also checks that they are no-ops on D3D11.
- A program that loops forever would hang WARP for good: each shader runs in a worker process, one that stays silent
  for 30 s is killed and the variant is reported as a timeout.
- Not covered: GLES-vs-DX11 differences, which runtime keyword combination picks which `#if` branch, derivatives
  (pixels are drawn as points, ddx/ddy are 0), render state (blend, stencil, ZTest).
- Sanity check: with the `log2` fix reverted it reports 708 mismatching variants in the 66 shaders that use `log2`.

### D3D11-vs-GLES A/B test (`bash port/abtest.sh`)

The differential test proves the decompiled code computes what the DX11 bytecode does; it can't see the engine feeding
a shader different data on GLES. This one renders the same images in the editor in batchmode twice, `-force-d3d11` and
`-force-gles30` (Unity must be closed), and compares them. One run: `apply.sh`, D3D11, GLES, D3D11 again (noise
floor), report — about 2 minutes. Report: `port/build/abtest/report.html` (+ `report.md`), images per run beside it.

- `EtgAbTest.cs` draws every material in the project, and every shader without one, on a quad rendered by a camera
  into a 192² RenderTexture (as the game's cameras do: D3D's flipped projection only shows there), under the lighting
  setups `dark` (ambient only), `dir`, `point`, `spot` (one Unity light each) and `cookie` — the lit ones only for
  shaders with ForwardBase/ForwardAdd passes. Uniforms the shader uses but doesn't declare (set by the game's code) get
  deterministic values, empty texture slots a noise texture, the game's `Shader.SetGlobal*` globals in-game values.
  `_Time` & co. are pinned by a camera command buffer: the engine overwrites `Shader.SetGlobal*` on every camera render
  (to 0 in batchmode Edit Mode). Quad UVs are shifted by a quarter texel of a 4096 texture: at 192 px every third
  pixel centre would sit exactly on a texel boundary of a power-of-two atlas, and D3D/GL pick different neighbours.
- `abtest_report.py`: per image the share of pixels differing by more than 4/255; `differs`, `speckle` (small
  scattered blobs or 1-pixel lines — precision: the `frac(sin(dot(p, (12.9898, 78.233))) * 43758.5)` hash used by
  15 shaders, point sampling on a texel boundary), `minor` (< 0.2 % of pixels), `failed` (shader error/unsupported on
  an API), `empty` (identical to the background on both APIs — not verified). Entries name the shader and keywords.
- Probes (`AbTestProbe.shader`, `AbTestLightProbe.shader`): built-in uniforms and forward light data (`_LightColor0`,
  SH, vertex lights, attenuation) per API. Only the expected cells differ (`_ProjectionParams.x`, `_ZBufferParams`,
  clip space, `UNITY_NEAR_CLIP_VALUE`, platform defines); the light data is identical.
- Determinism: two D3D11 runs are identical to the bit (0 of 3587 images differ). Sensitivity: with
  `fix_clip_space_flip` reverted, TitleWaterShader (32 % of pixels), TitleSky and TitleInk show up as `differs`.
- Graphics emulation: both runs use the WebGL 2.0 emulation Play From Boot sets (`--emulation none` to compare);
  under it D3D11 reports shader level 35 like GLES, tier 3 vs GLES's tier 2.
- Found: decompiler bug 17 (below). What is left is precision: PitSpirits/HologramShader (the sin hash feeding
  `round`/`ceil` dithering) plus speckle. Not covered: real game frames (cameras, Pixelator, post effects), render
  targets other than ARGB32, shadows, 75 materials that draw nothing in isolation.

### Variant selection (`bash port/variant-selection.sh`)

Checks which decompiled **vertex and pixel branches Unity actually selects**, against the original keyword inventories
in `tools/uscs/dump/<shader>/variants.json`. Run with Unity closed, after installing the desired shaders into the project.
The command reads the installed `.shader`/`.mat` assets; it does not run `apply.sh` or edit their originals.

```
bash port/variant-selection.sh
python port/variant_selection.py  # same command from PowerShell; bash above means Git Bash, not WSL
bash port/variant-selection.sh --static-only
bash port/variant-selection.sh --filter 'Brave/LitCutoutUber$' --out port/build/variant-subset
bash port/variant-selection.sh --report-only
python -m unittest discover -s port -p test_variant_selection.py
```

- Output: `port/build/variant-selection/report.md`, `summary.json`, `issues.json` grouped by shader and reason,
  `static.jsonl` (material/pass/stage/engine-setup rows), `combinations.jsonl` (every pragma combination),
  `empirical.jsonl` (original IDs, actual IDs and both stages' macros), `engine-coverage.json`, `stripping.json`,
  `unchecked.json`; Unity's log is `editor.log`. IDs are **one-based indices into variants.json**.
- Static checks enumerate every combination of `multi_compile`/`shader_feature`, including shaders without materials.
  Single-keyword `shader_feature X` includes the disabled option. The parser validates labelled conditions against the
  dump and recognises unlabelled final `#else` through the variant's CB marker, tracking nested platform directives.
  It checks per-stage keyword inventories, so a pixel program need not contain vertex-only keywords.
- Original mutually exclusive choices are reconstructed independently of the installed pragmas. Required choices
  default to their first dumped option; installed pragma choices default to their first option. These are explicit
  assumptions. Conflicting requests, missing exact originals and duplicate original keyword sets are reported separately;
  the tool never calls a nearest-variant guess a match. Unknown material keywords are reported even when drawing matches.
- `EtgVariantSelection.cs` renders diagnostic copies in batchmode on D3D11, with graphics emulation disabled and a linear
  float RenderTexture. Each branch defines an ID; a constant vertex varying and the pixel program encode the two IDs
  and keyword bitsets separately. Program bodies and render state are replaced so clipping, blending, vertex deformation
  or another pass cannot hide the result. ForwardAdd gets a color-masked base pass and one additional light. The camera's
  aspect is explicitly 1, so all 64 diagnostic columns cover the quad in the 64×16 target.
- Setups: dark, directional, point, spot, each cookie light separately, ForceVertex lights, SH probe usage, and an
  attempted directional shadow setup. An independent reference shader records the engine keywords, rather than inferring
  them from possibly broken pragmas. `engine-coverage.json` explicitly marks requested macros that did not activate.
  Temporary shaders and material clones have their own GUIDs in `Assets/Editor/EtgWebBuild/_variants`; the runner deletes
  its own folder in `finally` and restores ProjectSettings changed by batchmode. A pre-existing temporary folder is an
  error, never silently removed. The editor harness is copied into the project as a normal port tool.
  `inputs.json` records hashes of original shaders, materials, metadata, Graphics Settings and variant inventories;
  the runner verifies they stayed unchanged, and `--report-only` rejects changed inputs or a different material filter.
- `stripping.json` checks material feature combinations and Always Included shader GUIDs. Empirical rows also record
  retention risk for the selected feature combination. This is a conservative inventory, **not a player-build trace**:
  editor-only material usage can overestimate retention; bundle dependencies, variant collections, runtime keywords,
  platform and engine stripping still require build evidence. A shader with no material and no Always Included entry
  has no material-based retention evidence, including for its disabled feature variant.
- Coverage limits: only first-subshader Always/ForwardBase/ForwardAdd color passes are rendered. Unsupported paths and
  unresolved builtin shaders are named in `unchecked.json`. ShadowCaster/Meta, deferred/legacy paths, UsePass, fallbacks,
  lower subshaders, fog, lightmaps, VR/instancing, other shadow permutations and real gameplay keyword changes remain
  outside the empirical run. Static enumeration still checks parsed CGPROGRAMs for those passes.

On the current project (899 materials, 22,282 measured VS/PS selections), every selection matches an exact original, and the pragma model
agrees with Unity. There is no new shader fix. The static grid has 58 stage combinations without an exact original and
87 material/setup predictions without one: these predominantly combine `VERTEXLIGHT_ON` with disabled `LIGHTPROBE_SH`.
In the render, Unity enables SH also in dark/point/vertex setups, and these predictions do not become mismatches.
Unknown material keywords are ignored by the installed pragmas; they are retained as findings, not removed from
game assets. Nine feature combinations lack retention evidence: three duplicate shader assets' `EDITOR_VISUALIZATION`
Meta options (editor baking), and both `ENABLE_WARP` states of three Hidden/Kvant/Tunnel/Kernels passes with no material.
The directional shadow attempt does not activate `SHADOWS_SCREEN`; that state remains explicitly unverified.

Sensitivity to bug 17 is built in (`sensitivity.json`): reconstruct the exact old column-based groups in memory and
check point/spot requests for all five affected shaders. To also render that regression without changing USCSandbox or
original project assets:

```
bash port/variant-selection.sh --legacy \
  --filter 'Brave/(BurnableSprite|DisplacerBeast|LitCutoutUber$|LitBlendUber$|LitCutoutUber_ColorEmissive)' \
  --out port/build/variant-legacy
```

The five shaders additionally get synthetic material keyword states from the original inventory, exercising choices
absent from existing materials. Some malformed requests happen to choose the right program; conflicting pragma sets
remain invalid. The legacy render exposes actual wrong trailing fallback programs (4,106 VS/PS selections).
All five shaders fail for some spot requests. Point also fails for LitCutoutUber, LitBlendUber and
LitCutoutUber_ColorEmissive; tested point requests on BurnableSprite and DisplacerBeast happen to match even with the old
groups. This narrows the initial task's assertion that point and spot must always fail on all five. The static sensitivity
still detects malformed/conflicting sets for both lights on all five. The normal run keeps the corrected groups and
checks the same synthetic states. Ten unit regressions cover nested platform directives, wrong fallbacks,
missing originals, per-stage keywords, pragma defaults/conflicts, and malformed input rejection.

### Decompiler bugs fixed (`port/patches/uscsandbox-fixes.patch`, `port/fix_shaders.py`)

Each one broke many shaders at once. Found in the game: 1–6; found by the differential test: 7–16; found by the A/B
test: 17.

1. Render state referring to material properties lost (`Ref [_StencilVal]` -> no `Ref`, `Blend [_SrcBlend] [_DstBlend]`).
2. The disabled `USILAndOptimizer`: `mask & float` turned into an integer AND with a truncated literal.
3. Float literals printed as `0.0#######`: `1e-10` in RGB->HSV became `0.0`, grey pixels gave NaN.
4. DXBC `log` is `log2`, not the natural `log` (`pow()` got the wrong exponent).
5. A non-identity swizzle of a constant (`cb0[2].xyxy`) dropped: `_MainTex_TexelSize * float4(...)` mixed in
   width/height, bloom flooded the screen.
6. `sign()` on comparison masks (true = −1 in DXBC) came out negated (`fix_shaders.py`, `fix_bool_subtraction`;
   now also per vector: `tmp0.yz = tmp0.yz - tmp1.xy`, SimpleCameraMask's minimap border).
7. A swizzle spanning several constants (`cb0[6].yx` over two floats) emitted in declaration order instead of
   swizzle order: the light renderers swapped `_CameraOrthoSize`/`_CameraOrthoSizeX`, LitBlendUber
   `_ValueMinimum`/`_ValueMaximum`, the palette shaders the hue/saturation tolerances.
8. An input register holding two packed interpolators (`v0.xyzw` = `texcoord.xy` + `texcoord1.xy`) named after
   the first one only (Starnest; `fix_shaders.py` used to patch it to `.xyxy`, which was wrong as well).
9. `sincos` whose sine destination is its source: `tmp1.x = sin(tmp1.x); tmp2.x = cos(tmp1.x);` (HueShift).
10. `mask & l(1.0, 0.75)` became `mask ? 1.0 : 0.0` for every component (BraveTilesetWallColorEmissiveCutout:
    the 0.75 blend with `_ParallaxTex` became 1.0).
11. `USILAndOptimizer` kept registers flagged as comparison results after they were overwritten, and then picked
    the value as the condition: `and r2.w(value), r3.x(mask)` -> `tmp2.w ? tmp3.x : 0` (DarkPortalShader's atan).
12. `ftoi` written as `asint()` (bit reinterpretation) instead of a truncation: `5.0` became `1084227584`
    (HologramShader, SinglePassOutline; FinalGunRoom_BG_02 looped ~10⁹ times). `ftou`/`utof` had no HLSL at all, and
    unhandled instructions used to disappear silently — now they print a warning and a comment.
13. A sample through another texture's sampler (`sample_l ..., t3, s0`) read the sampler's texture: Standard's
    second reflection probe sampled `unity_SpecCube0`; in that macro form the LOD was dropped too.
14. Comparison sampling (`sample_c`, shadow maps) decompiled as a plain `tex2D` — depth instead of the 0/1 test, and
    the reference operand had the wrong component. Now `UNITY_DECLARE_SHADOWMAP` + `UNITY_SAMPLE_SHADOW` (cube too).
15. An element of a matrix array: `unity_WorldToShadow[0 + 1]._m01_m11_m21_m31` (column 1 of the *next* matrix)
    instead of `unity_WorldToShadow[0]._m01_m11_m21_m31`.
16. `unity_Lightmap_HDR`, `unity_DynamicLightmap_HDR`, `_PPLAmbient` assumed to come from the auto-includes (in 2017.4
    they are in UnityCG.cginc) — Legacy VertexLit's lightmap pass did not compile.
17. `multi_compile` sets guessed from the n-th keyword of each shortest variant: in the ForwardAdd passes of
    LitCutoutUber (~660 materials), LitBlendUber, LitCutoutUber_ColorEmissive, BurnableSprite and DisplacerBeast the light
    keywords got mixed with the material's (`multi_compile POINT PALETTE_OFF SPOT POINT_COOKIE PALETTE_ON`). Unity then
    compiled keyword combinations without the light keyword, none of the `#if` branches matched, and the trailing `#else`
    — a DIRECTIONAL_COOKIE program — lit every point/spot light: no distance falloff at all on D3D11 (`.w` of the R8
    falloff texture is 1), a falloff along the light's x axis on GLES (Alpha8). Keywords that never occur in one variant
    and stand in for each other in at least one now form a set. The differential test can't see it: it compiles each
    variant with its exact keywords.

Not decompiler bugs but DX11-vs-GLES differences, handled in `fix_shaders.py` and invisible to the differential test:
`unity_OcclusionMaskSelector` is not set on GLES (the LPPV variants' baked occlusion is 1.0 under
`#if !UNITY_LIGHT_PROBE_PROXY_VOLUME`, as in Unity's own code), upper-case `LightMode` values are not recognised when
compiling from source, light attenuation textures are read from alpha on GLES (the read is swizzled through
`UNITY_ATTEN_CHANNEL`: `.xyzw` on D3D11, `.wwww` on GLES), and D3D renders into
textures with a vertically flipped projection: the game's vertex code derives screen positions straight from the clip
position (title screen water/sky/ink, portals, PartialDesaturationEffect, ComputeGrabScreenPos with D3D's −1 baked in),
so on GLES those are computed in D3D's flipped clip space and only `o.position` is flipped back (`fix_clip_space_flip`;
without it the title screen water was upside down — a white gradient instead of the dithered sea). Reversed Z
(D3D depth goes 1->0): the ShadowCaster passes have D3D's `UnityApplyLinearShadowBias` baked in, so on GLES they get
the GL version under `#if !UNITY_REVERSED_Z` (`fix_shadow_bias`); the soft particles, the only shaders reading
`_CameraDepthTexture`, go through `_ZBufferParams`/`_ProjectionParams` and are platform-independent. All these rewrites
are under `#if`/macros on the platform and no-ops on D3D11: the same files are right in the editor on D3D11 and on GLES,
`shaderdiff.sh` checks that they don't change the DX11 result, and `abtest.sh` compares the two APIs (below).

## Sound

Wwise is a native library and its banks are built for Windows, so the browser doesn't run it: `port/audio/etg-audio.js`
is a small Wwise-like engine on Web Audio that interprets the banks' object graph. `WebShim.WwiseShim` (patched in for
`AkSoundEnginePINVOKE`) forwards the game's calls (PostEvent, SetSwitch, SetState, SetRTPCValue, positions...) through
`EtgWebShim.jslib` to `window.EtgAudio`.

- `wwbank.py` parses `Init.bnk` / `SFX.bnk` (bank version 128); every HIRC object is read to its exact end.
- `build_audio.py` writes `StreamingAssets/webaudio/`: `audio.json` (events, actions, nodes, busses, effects, measured
  reverbs and effect models), sound packs (Opus), music files, the engine, `soundboard.html` (post events, set
  switches/states/volumes: `http://localhost:8080/StreamingAssets/webaudio/soundboard.html` with `serve-webbuild.ps1`).
  Encodes are cached; a re-run takes seconds. It copies everything into `webbuild/` too.
- `port/WwiseRef` (.NET 9) is the reference: it loads the game's `AkSoundEngine.dll`, suspends its output and replaces
  `QueryPerformanceCounter` with a virtual clock, so each `RenderAudio()` renders one 1024-sample frame offline.
  `WwiseRef <root> <cases.json> <out> [--only ids] [--remove-fx bus:index,...] [--remove-node-fx node:index,...]`;
  steps: `postEvent`, `playNode` (a sound or container alone, through a DynamicSequence), `setSwitch`, `setState`,
  `setRtpc`, `stopAll`.
- `listen.py`: a page to listen to both side by side (`port/build/listen/index.html`): every event whose sounds
  pass through an effect on a sound (or `--filter REGEX`), rendered by Wwise, by the engine and by the engine
  without its effect models.
- `audiodiff.py`: every event (per switch/state value), music state and transition, volume curve, mix state and 3D
  position rendered by WwiseRef and by `EtgAudio.render()` in headless Chrome; level, peak, onset, end, spectral
  centroid and balance compared (`port/build/audiodiff/report.html`, worst cases with audio of both).

- Baked mods' banks: `build_audio.py --mods <dir>` (from `apply.sh --mods`) writes `webaudio/mods/<package>/` and
  `mods.json`; the engine merges them into the game's data at start. `audiodiff.py --mods` tests them (Mods,
  Expand the Gungeon).
- Found with Expand the Gungeon's bank (`audiodiff.py --mods`, then checked on the whole game): dB-scaled curves
  above 0 mean `1 / (1 - y)`, not `y + 1` (y = 1 is an infinite boost, which Wwise renders as silence); 3D
  spatialization "None" (`bits3d & 3 == 0`): attenuated by distance, not panned; a mono voice that isn't panned in
  3D is mixed at the centre of the panner, -3 dB a side, not copied to both channels at full level. On the whole
  game: 1319 -> 1293 of 2063 cases flagged, event level flags 787 -> 744, event level p10..p90 -2.1..+3.5 ->
  -2.2..+2.6 dB (median -0.2 -> -0.7); the game's 2D mono events alone: mean |level difference| 2.31 -> 1.62 dB
  (median +1.7 -> -1.4: some other ~1.5 dB is still unaccounted for there).

### Effects

- Parametric EQ and Delay: built from their parameters (biquads, a feedback delay line).
- Bus reverbs (Dungeon, the aux busses Foyer / Dimension / Dimension_Delay): impulse responses measured in Wwise by
  `measure_ir.py` (renders with the bus's effects on and off, Wiener deconvolution) and played by a ConvolverNode.
- Effects on sounds and containers (41 instances): `measure_fx.py`.

  ```
  python port/audio/measure_fx.py render   # every effect node alone (playNode), with and without its effects
  python port/audio/measure_fx.py ir       # Matrix Reverb / RoomVerb -> port/build/audio/ir/fx<id>.wav, fx.json
  python port/audio/measure_fx.py fit      # the other plugins -> port/build/audio/ir/fx_models.json
  python port/audio/build_audio.py         # ships both in audio.json / ir/
  python port/audio/measure_fx.py check    # engine with/without the models vs Wwise with/without the effects
  ```

  The renders have every bus effect removed and VOL_SFX at 10 (without the master's limiter 100 clips the capture);
  rendering a node alone matters: on layered events (EnergyCannon, Melter, the guns' shots) only one layer carries the
  effect. The effect's input is the dry render divided by the gain after it (the ratio of its RMS to the decoded media's).
  Parameter layouts were decoded from the banks' blocks and confirmed by the fits; per plugin:

  - **Compressor** (4; threshold -6 dB, ratio 3): wet - dry is at -60 dB in Wwise, the threshold is never reached
    by these sounds. Left out.
  - **Matrix Reverb** (9) / **RoomVerb** (4): linear, but on sustained sounds (wall_reveal, beholster_death) the tail is
    almost uncorrelated with the input at any lag (modulated), so a Wiener response loses most of it. Fitted instead
    is the energy: per octave band a level and decay time after one predelay, from 10 ms block powers of dry and of
    wet - dry. The decay times and predelays come out as the bank says (RT 4 s / 100 ms -> 3.65 s / 120 ms); the
    modelled tail energy is within 0.7 dB per case. Shipped as band-limited noise under those envelopes (16-bit,
    `ir_gain`), one ConvolverNode per effect and destination shared by all its voices; the direct sound is the voice.
    9 distinct parameter blocks -> 9 responses (0.2-0.6 MB each).
  - **Guitar Distortion** (10, all Overdrive, EQ bands at 0 dB): `dry*x + wet*hp(lp(shape(k*hp(x))/k))`, shape a
    knee curve `v/(1+|v|^p)^(1/p)` with rectification; k, knee, the filters and the mix fitted per instance. Sample
    residual -13 to -38 dB (best linear fit -4 to -12 dB); what remains is Wwise's overshoot on clipped edges. Per voice:
    IIR filters, a WaveShaper (2x oversampled), gains — 8 nodes.
  - **Flanger** (9): Dattorro's universal comb with the bank's dry / feed-forward / feedback levels, mix, LFO waveform
    and rate; delay `delay*(center + swing*lfo)`. Three instances fit to -54…-62 dB with center 1, swing depth/2;
    the others get fitted center/swing/phase (-21…-45 dB). The 19 Hz, 10 ms, 100 % one (BlasterBow, AntGoop) sweeps
    faster than real time and can't be fitted sample by sample; its level and spectrum match. A DelayNode in a cycle
    can't be shorter than a render quantum (2.7 ms) and these delays are 0.25-5 ms, so the feedback is unrolled into
    up to 16 taps `fb^(k-1) x(t - k D(t))` in one network per effect shared by its voices; the LFO restarts at the
    fitted phase with each new voice, as Wwise's starts with the voice.
  - **Tremolo** (1): the gain wet/dry folded over its period (a smoothed saw, 19.84 Hz, phase from the voice's start),
    played by a per-voice OscillatorNode with that PeriodicWave into a GainNode.
  - **Harmonizer** (4): the pitch voices as extra buffer sources at their rate (a pitch shifter keeps time, so each copy
    follows the dry's RMS envelope and ends with it), everything delayed by the 1024-sample window as Wwise's sync-dry
    does (measured: dry at lag 1024 with the bank's -3 dB); the voices' level calibrated to Wwise's output energy.

  `check` compares what each effect changes (Wwise wet vs dry, engine with vs without its model; the engine's own
  level errors cancel out). Mean error of that change over all 41 instances, without models -> with: level
  2.04 -> 0.21 dB, spectral centroid 0.186 -> 0.061 octaves, end of the audible part 0.26 -> 0.04 s.

  In the full audiodiff, the 53 event cases that pass through a node effect: 47 -> 40 flagged, mean |level difference|
  4.4 -> 3.0 dB (p10..p90 -7.4..+3.0 -> -2.4..+2.5 dB), centroid 0.39 -> 0.34 octaves. Overall 1326 -> 1319 of 2063
  cases flagged, sound effects' level median -0.2 dB (p10..p90 -2.1..+3.5).

Known differences left (audiodiff): some sounds are about 10 dB quieter than in Wwise with or without effects
(deathray, omegaBeam, several guns); a few music starts are late (Drone, Secret_Room); the HPF is slightly strong.

## Mods

BepInEx mods (r2modman / Thunderstore packages) can't be loaded by a WebGL player: IL2CPP compiles all C# to wasm
ahead of time, there is no JIT, so neither new DLLs nor Harmony / MonoMod detours work at runtime. Instead a fixed
mod pack is baked into the build: `./apply.sh --mods` installs the packages of `port/mods/modpack.txt` (plain
`./apply.sh` takes them out again), then the usual player build.

- `mods/fetch_mods.sh` copies the packages from the r2modman cache into `etg/mods/` (third-party binaries, not in git).
  A package without a `plugins/` folder (Expand the Gungeon) gets its files moved into one, as r2modman installs them.
- `ModBaker/bake.sh` (`port/ModBaker`, .NET Framework 4.7.2: it runs the mods' own net35 HarmonyX/MonoMod) loads the
  game and the mods by reflection and lets HarmonyX build every patched body itself: patches are registered like
  `PatchAll` does (one patch method at a time, so one bad target doesn't take its class down), the patcher backend is
  replaced by one that only records them, then `HarmonyManipulator.Manipulate` runs on a `DynamicMethodDefinition`
  of each original (prefixes, postfixes, finalizers, transpilers, IL manipulators - through `ILContext.Invoke`, as
  ILHook does) and the result is copied into the game DLL with Cecil, every reference re-imported against the game's
  own assemblies (HarmonyX here references .NET Framework's mscorlib 4.0, the game ships Unity's 2.0/3.5 profile).
  - Patches on methods that can't be rewritten (UnityEngine, mscorlib: `Object.Instantiate` for Alexandria's fake
    prefabs, `Enum.GetValues/...` for MtG API's enum extension) become wrappers in the mod
    (`ModBaker.Redirects`), and every call in the game and mod assemblies is redirected to them. MtG API's MonoMod
    patch that routes `Instantiate<T>(T)` through `Instantiate(Object)` is reproduced the same way.
  - `[HarmonyReversePatch]` stand-ins get a copy of the original body.
  - MonoMod `new Hook(from, to[, target])`: from/to are recovered by evaluating the reflection code in front of the
    constructor (a small stack machine: `typeof(X).GetMethod(...)`, `AccessTools.Method`, locals, any third
    argument); the original body moves to a static `Name_orig_bakedN(self, ...)`. Mods switch hooks on and off while
    the game runs (Expand the Gungeon: console commands, items picked up and dropped, the punch-out arcade), so each
    (from, to) pair gets a slot, `ModBaker.BakedHooks::slotN` (a `MonoMod.RuntimeDetour.BakedHook`: an applied count
    and the Hook's target object) in the hook's assembly, and the target's body becomes "slot applied ? hook([target
    as T,] [orig delegate,] self, args) : original". Every `new Hook(...)` is rewritten to the facade's constructor
    that takes the slot; the facade's ctor / `Apply` turn it on, `Undo` / `Dispose` off - as the detour would be. A
    hook created in an `Init` or a static constructor is on from then on, as before; before that the original runs
    (as in the game). Hooks without the orig parameter replace the method outright (MonoMod allows that). An
    instance hook method is called on the Hook's target object if it is of the hook's class; mods pass `typeof(X)`
    there when `this` isn't used (Mono then calls the hook on the Type object), and the hook gets a stand-in instance
    of its class instead (`BakedHook.TargetAs`, `FormatterServices.GetUninitializedObject`) - not null: IL2CPP
    null-checks `this` even on a non-virtual call (the first build threw in every `PhysicsEngine.LateUpdate`). Hooks on engine methods (Expand the Gungeon's
    FakePrefab hooks on `Object.Instantiate`) go on the redirect wrapper every call is sent to (`Redirector`; the
    Harmony one if the method is patched too). Two pairs on one method chain in bake order (MonoMod: apply order).
    One Hook object per pair is assumed: a pair applied twice runs its hook once.
  - Mono finds `Load(string,string)` where .NET Framework reports `Load<T>(string,string)` as ambiguous: a finalizer
    on `AccessTools.DeclaredMethod` (`MonoLikeLookup`) answers like Mono.
  - What only ran while patching is stripped from the mods (IL manipulators, transpilers, their lambdas and closure
    classes - everything touching Mono.Cecil / MonoMod.Utils / CodeInstruction), as are the `[Harmony*]` attributes;
    mod references are set to the identities that ship (facades, MtG API's unsigned Newtonsoft.Json 11).
- `port/ModFacades`: `BepInEx` (BaseUnityPlugin, config files, logging to the Unity log, Paths, Chainloader,
  `UnityInput`), `0Harmony` (PatchAll does nothing, AccessTools, CollectionExtensions) and `MonoMod.RuntimeDetour`
  (Hook switches its baked slot, `BakedHook`) - the API the mods still call at runtime.
- The patcher treats the mods like the game (threads, files, AssetBundles...). Mod-specific: `BindingList<T>` (MtG
  API's SGUI; not in the WebGL profile) is implemented in `WebShim/SystemStubs.cs`; Alexandria's PrefabAPI writes
  AssetBundles at runtime for StandaloneWindows64 and gets WebGL instead (`LiteralShims`).
- Files the mods read (sprites, data) go into `StreamingAssets/mods/files.pack` (`mods/build_pack.py`); the Boot scene
  unpacks it to `/mods/BepInEx/plugins/<package>/...` (r2modman's layout, DLLs as empty placeholders: mods find their
  folder through their DLL's location) and starts the Chainloader (`WebShim/ModFiles.cs`) before the game's first
  scene. `Paths.GameRootPath` (Resources/ for logs and mod data, BepInEx/config) is `<persistentDataPath>/modded`, in
  IndexedDB like the saves. The editor reads the same tree from `etg/mods-runtime/`.

Found on the first runs in the browser:

- Embedded resources (`Assembly.GetManifestResourceStream`: MtG API's item ID maps, the mods' sprites) are null on
  WebGL: IL2CPP reads them from `<data dir>/Resources/<Assembly>.dll-resources.dat` and Unity 2017's WebGL packager
  leaves those files out (the game's own dfScriptLite has one too). `ModBaker resources` writes them in il2cpp's
  format and the mod pack unpacks them to `/Il2CppData/Resources` (absolute entries) before the plugins start.
- The Chainloader starts on the `sceneLoaded` of the game's first scene (LoadingDungeon), not in Boot: MtG API's SGUI
  takes its font from `dfControl.ActiveInstances[0]` on its first OnGUI, fails without one and stays broken.
- Alexandria's `PlayerController.Update` manipulator passes `this` to a method taking a SpeculativeRigidbody (meant
  another method; on Mono it works because only `.gameObject` is used). IL2CPP's C++ doesn't compile that:
  `ArgumentFixer` turns such a component argument into `GetComponent<Expected>()` (same GameObject, same result).
- Unity 2017's mcs (IKVM reader) rejects any assembly with custom attributes on generic constraints (C# 8
  nullability, Newtonsoft.Json) with CS0009, which stops every script compile: the patcher removes them. It also stubs
  Newtonsoft's `JsonTypeReflector.DynamicCodeGeneration` to false (no Reflection.Emit on IL2CPP).
- `LogSwallowedExceptions` (patcher): `catch { }` blocks of chosen mod methods log what they discard.
- Silver Jacket writes `<game root>/SilverJacketData/ItemStats.json` on every pickup of its items, but only its
  `slvjckt read_stats` command creates the folder: the pickup threw out of `PlayerController.HandlePlayerInput` and the
  player stopped responding (on a fresh Steam install too, as far as the code goes). The Chainloader facade creates
  the folder at start.

- Engine generic overloads that call a patched method (`Instantiate<T>(T, Vector3, Quaternion)` is
  `(T)Instantiate((Object)original, ...)`) are patched along with it in the game; here they get copies in the mod
  (`Object_Instantiate_GN`, body from the engine, inner call -> wrapper) and their callers go there. Without it the
  game's projectile spawns skipped Alexandria's FakePrefab postfix: mod guns fired, their projectiles stayed inactive.

Play Mode (`EtG Web/Play From Boot`) runs the mods without a WebGL build: Boot unpacks nothing in the editor, the
Chainloader reads `etg/mods-runtime/`. Mono there checks member access (MethodAccessException; IL2CPP doesn't), and
baked code calls private members of other assemblies: the game calls the mods' private patch methods, reverse patch
copies call the game's internals, and the mods read the game's private fields directly (built against a publicized
Assembly-CSharp). `AccessFixer` routes each such use through a public trampoline `ModBaker.Access.*` in the member's
own assembly (the members keep their visibility, so reflection with NonPublic still finds them).

### Expand the Gungeon (mods with their own AssetBundles, sound banks and shaders)

ApacheThunder's Expand the Gungeon 3.0.8 (new floors, items, enemies; `modpack.txt`) brings three Windows AssetBundles
(`expandsharedauto` 17 MB: rooms, serialized data, textures, prefabs; `expandshaders`: 8 shaders; `expandaudio` 53 MB:
its Wwise bank as a TextAsset) and 57 MonoMod hooks. What it needed beyond the first mods:

- **Bundles from source** (`mods/mod_bundles.py`, run by `apply.sh --mods`). WebGL can't load Windows bundles, and
  decompiling the shaders is what we don't want; the mod's repository has the Unity projects the bundles are built
  from (`ExpandAssets`, `ExpandShaders`). Clone it into `tools/src/ExpandTheGungeon` (the commit is in
  `mod_bundles.py`: `git clone -b BepInEx https://github.com/ApacheThunder/ExpandTheGungeon tools/src/ExpandTheGungeon`,
  `git checkout a7ff72a6`; needs `pip install UnityPy`). The repository has no `.meta` files, so no GUIDs, import
  settings or bundle names; `mod_bundles.py` recovers them from the shipped bundle of the same version (UnityPy):
  - every entry of the shipped bundle's container is copied from the sources to the same path in our project
    (`Assets/ExpandRooms/...`: the mod loads many assets by full path) with `assetBundleName`, so EtG Web -> 3 builds
    `expandsharedauto`/`expandshaders`/`expandaudio` for WebGL with the game's bundles. Sources and bundle agree (all
    1522 entries); text assets are written with the bundle's bytes (git normalised line endings, 122 files had CRLF,
    and the mod splits texts on `'\n'`);
  - textures get the bundle's format (RGB24/RGBA32), point filtering, wrapping, no mips, readable, and
    `alphaIsTransparency` where the bundle's transparent pixels differ from the PNG's;
  - the 21 GUIDs the prefabs reference: each reference is looked up in the shipped bundle's copy of the prefab (same
    hierarchy path, class, index) - a script becomes a reference into `Plugins/ExpandTheGungeon.dll` (fileID = first 4
    bytes of MD4("s\0\0\0" + namespace + class), checked against the game's prefabs), an asset of the bundle gets that
    GUID in its `.meta` (two GUIDs for one asset: the second is rewritten), a missing material stays missing (it is
    in the shipped bundle too). The mod DLLs' `.meta` GUIDs are fixed (md5 of `etgmod:<file>`, `apply.sh`);
  - `expandaudio`'s bank becomes an empty stand-in (sound comes from the audio pack, below): the bundle is tiny.
  - In the build, `BootLoader` preloads every bundle the bundle manifest lists besides the game's (the mods'),
    `BundleShim.LoadFromFile` of `.../BepInEx/plugins/<package>/<file>` returns the bundle named like the file, and
    `build_pack.py` packs Windows bundle files as empty placeholders (the mod only checks they exist). Play Mode
    simulates them from the project like the game's, except that a mod bundle's prefab comes as a runtime copy (one
    per asset, under a hidden inactive root): AssetDatabase hands out the prefab asset itself, and the editor refuses
    `Renderer.material` on it (PortableShip: "Not allowed to access Renderer.material on prefab object"), and the
    mods' AddComponent & co. would change the project's assets.
- **Shaders**: compiled by Unity from the mod's sources for GLES3; `abtest.sh --filter Expand` (D3D11 vs GLES): 4 of 8
  identical, the rest only where `frac(sin(dot(...)) * 43758.5)` noise hashes (VHS, Cinematic) or texel-boundary
  samples (GlitchScreen) differ, as for the game's own shaders; no flipped or broken effect.
- **Video**: `ExpandScreenFXController` plays VideoClips (VHS noise, an old-film overlay); the WebGL VideoPlayer only
  plays URLs. The patcher turns `VideoPlayer.clip = c` into `MediaShim.SetVideoClip`, which on WebGL plays
  `StreamingAssets/mods/video/<clip name>.mp4` (`mod_bundles.py` copies the sources there; H.264/AAC). The clips
  stay in the bundle (6 MB; the VideoPlayer components and the controller reference them, and the name comes from
  them); their `.meta` needs `useLegacyImporter: 0`, otherwise 2017.4 imports them as MovieTextures, which WebGL
  drops (only the audio track was left, as an AudioClip). The old-film
  pass is Windows-only in the mod (`Application.platform == WindowsPlayer`); those reads see Windows
  (`WindowsPlatformChecks`, as on Steam).
- **Sound**: the bank (version 128 like the game's) is converted like the game's into `webaudio/mods/<package>/`
  (`build_audio.py --mods`, listed in `webaudio/mods.json`; sounds over 5 s - the floors' music - are separate
  files fetched when played, the rest one 0.5 MB pack). The engine merges it into the game's data before anything
  plays; its sounds route into the game's Master Audio Bus. `LoadAndDecodeBankFromMemory` -> `WwiseShim`: loaded.
  `audiodiff.py --mods` compares the mod's events (WwiseRef loads the bank from memory as the mod does), at the
  game's default volume options (80). Found and fixed in the engine (Sound section): the dB curve scale above 0
  (the mod's VOL_SFX curves end at +1 = muted at volume 100 - also in Steam), 3D spatialization "None", the -3 dB
  centre of unpanned mono voices. Result: 20 of 116 cases flagged, level median +0.6 dB (p10..p90 +0.4..+1.5).
- **Libraries**: Ionic.Zip (from Mod the Gungeon API's folder; the mod reads its bundles from a zip if it is installed
  zipped): ThreadPool work items run at once (ThreadShim), self-extractor stubs and console-colour tracing stubbed;
  `AssetBundle.LoadFromStream` -> `BundleShim` (Windows bundles from a zip can't load here). BepInEx facade:
  `UnityInput.Current`.
- `Patcher verify` also fails on a branch whose target isn't an instruction: code inserted in front of a call
  without first switching to long branch forms (`SimplifyMacros`) silently overflowed a short branch, and il2cpp died
  with a NullReferenceException in its control-flow graph after 20 minutes of building.
- `Patcher verify` also fails on a parameter with the HasDefault flag but no Constant row: the editor's Mono 2.x
  aborts in `MethodBase.GetParameters()` (`mono_class_from_mono_type: implement me 0x00`), which
  `Exception.StackTrace` calls for every frame - so the first exception whose stack passed such a method killed the
  editor (smoke test: Cascading Bullets after Bone Parade, through `Projectile.HandleDamage`). HookBaker's
  `*_orig_bakedN` clones copied the parameter flags without the default value (4 hooked methods); clones and
  AccessFixer forwarders now copy the constant. `bash port/monosigcheck.sh` checks the same with the real runtime:
  `port/MonoSigCheck` (built by the editor's own gmcs) reflects over every method of `Assets/Plugins` under
  `Mono/bin/mono.exe` the way a stack trace does, a crash is reported and skipped (~7 s; `modsmoke.sh` runs it first).
- **Old West floor fix** (mod bug, also in Steam; `Patcher` `FlowNodeFixes`): the floor behind the mod's secret Old
  West warp has a MANDATORY special room from the game's shrine table (`basic_special_rooms_noBlackMarket`). Of its
  rooms only two are SPECIAL and both have conditions: Shrine_Cleanse needs curse >= 1, Shrine_CursedMirror 3 h played
  and the Gungeon reached more than twice. On a save without either (a fresh browser save) the node got no room, the
  generator threw in `GetExitPairsSimple` and the load never ended. The game only injects that table where a room
  fits; the node is OPTIONAL now and the floor is built without the shrine room when none qualifies.
- **Glitch chest floor fix** (mod bug, also in Steam; `Patcher` `FlowNodeFixes`): Expand the Gungeon's
  `Custom_GlitchChest_Flow` gives its glitch boss node the double beholster room table but leaves the node a CONNECTOR.
  Alexandria replaces that node of the game's `Secret_DoubleBeholster_Flow` with a table (`alexandriaGlitchTable`, boss
  rooms only), so Expand's `doublebeholstertable` is set, the generator searches it for a connector, then a normal room,
  backtracks without end and the floor loads forever (the glitch elevator picks this flow for about 1 in 6 glitch
  floors). The patcher makes that node BOSS like the same node of `Custom_Glitch_Flow`; Alexandria's
  `UsesGlobalBossData` patch then takes the room from its table, i.e. the glitch boss.
- **The game's assets by their original names** (`bundle_names.py` -> `bundle_renames.tsv`, applied by `apply.sh`):
  the mod loads game assets by name (`LoadAsset<Shader>("BraveLitTk2dCustomFalloffCutout")`, the room table
  "basic special rooms (shrines, etc)"), and a bundle finds them by path or file name. AssetRipper had exported 38 of
  them under other names: "," as "_", and 37 shaders of `shared_auto_001` that were also in `Resources/` as
  `<name>_0.shader`. They are renamed back (the latter into `Assets/BundleNames/<bundle>/`, the original file name in
  another folder - a full-path lookup still misses them). Only bundles change: EtG Web -> Rebuild Bundles Only.
- **Memory**: the wasm heap is fixed at build time; 1536 MB ran out while the mod built its room prefabs at start, the
  build now uses 2032 MB (2017.4's maximum; `TOTAL_MEMORY` in `webbuild.json` can't raise it, the module declares its
  maximum).
- **GetCallingAssembly** (all mods, `Patcher/CallerAssembly.cs`): IL2CPP answers it from the stack, and on WebGL
  only development builds have one (stack trace sentries); release builds get mscorlib, and Alexandria (92
  methods: sprites from the calling mod's resources) and Expand the Gungeon found nothing. Every call to a method
  that uses it now first names the calling type (`WebShim.CallerAssembly.Set(typeof(Caller).TypeHandle)`), the
  method takes it on entry; reached through a delegate or reflection it falls back to the real call.

### Smoke test (EtG Web -> Mod Smoke Test)

An automatic Play Mode run over everything the mods add, so items don't have to be tried by hand. **EtG Web -> Mod
Smoke Test -> Everything** (or one part: Items + Shop, Enemies, Floors + Flows) does Play From Boot and then, with
nobody at the keyboard (don't touch the Game view while it runs):

1. waits for the mods to load (Expand the Gungeon's `loadStatus` reaching `LoadFinished`) and the title screen,
   then starts a run as the Pilot the way the title's quick start does (`FinalIntroSequenceManager`), with MtG
   API's godmode on;
2. every mod item - Mod the Gungeon API's `Game.Items` without the game's `gungeon:` IDs, plus
   `ETGMod.Databases.Items.ModItems` (items added without an ID); the owner is the mod whose component sits on the
   item, else the ID's namespace: given like the `give` command (`LootEngine.TryGivePrefabToPlayer`), guns fired
   twice (`Attack` / `ContinueAttack` / `CeaseAttack`), reloaded and dropped, active items used
   (`PlayerController.UseItem`, again if still active) and dropped, passive items dropped;
3. four guns, actives and passives each bought from the first floor's shop: a `ShopItemController` set up as the
   shops do (`Initialize`, room interactable) and `Interact` with enough money;
4. every mod enemy - Expand the Gungeon's `ExpandEnemyDatabase.enemyPrefabDictionary` and enemy database entries
   without a bundle path - spawned next to the player (`AIActor.Spawn`), killed after 3 s, leftovers erased;
5. every mod floor (Expand the Gungeon's dungeon prefabs with the game's level definition, `LoadCustomLevel` as the
   mod does) and flow (`ExpandDungeonFlow.KnownFlows` without the `_Copy` duplicates of the game's flows, through
   `LoadCustomFlowForDebug` the way the game / the mod loads it: boss rush flows in `GameMode.BOSSRUSH` like
   `GameManager.InstantLoadBossRushFloor` - outside it the generator won't link boss rooms to their neighbours and
   skips no injections, so the mod's `Bossrush_01_Castle` / `MiniBossRush_01` never generated; the glitch chest flows
   of `ExpandDungeonFlow.GlitchChestFlows` with `isGlitchFlow` on Phobos and again on Space like
   `ExpandUtility.DelayedGlitchLevelLoad`; the rest with the tileset the flow name belongs to, as `load_flow` would);
   after generation the player warps into up to 12 rooms (not boss rooms), clearing what woke up.

After each step everything the step left is removed (items, guns, debris, spawned objects; the player is healed,
unpaused, time scale and input overrides cleared); if the run is lost (player gone, back in the Breach) the next
step starts a new one. Every error, exception and assert in the log (`Application.logMessageReceived`) and every
exception a step throws is counted for the step that runs, grouped by message + first frame, with the number of
frames it appeared in (an error every frame shows as "every frame" / "Recurring"). A step has a timeout (items 30 s,
floors 180 s) and an exception aborts only that step. Categories, by the first frames of the stack:

- **port**: `MethodAccessException`, `FieldAccessException`, `TypeLoadException`, `Missing*Exception` & co.,
  an error starting in the baker's code (`ModBaker.*` trampolines, hook slots; further down a stack they only mean
  a patched method was on the way), or the top frame in WebShim or a facade (BepInEx, 0Harmony, MonoMod.RuntimeDetour);
- **mod**: the top frames in a mod assembly (one that references BepInEx);
- **playmode**: what only the editor forbids (`... is not allowed to be called from a MonoBehaviour constructor`,
  `... on prefab object`, `Destroying assets is not permitted`, `UnloadAsset can only be used on assets` - BundleShim's
  runtime copies of mod-bundle prefabs);
- **other**: the game's own code or unclear; **smoke**: the test itself.

The game's known noise (`DataPrivacy`, `NONE save flag`, `INSTANTIATING AMMONOMICON`, `dfInputManager.processMouseInput`
NRE, `select_idle`, `_StencilVal`...) is only counted. The report - `port/build/modsmoke/report.md` (summary, error
kinds by category with their steps and stacks, every step with notes) and `report.json` - is rewritten after each
step, so a hang still shows how far it got; at the end Play Mode stops. `port/build/modsmoke/only.txt` (IDs or name
parts, one per line) limits a run to those steps - for checking a fix.

Unattended: `bash port/modsmoke.sh [phases]` (Unity closed) opens the project through OpenGL ES 3.0 like
`open-gles30.ps1`, asks for the run through `port/build/modsmoke/request` (picked up after the editor's first idle script
load, so also after the recompile it may do first - an `-executeMethod` would run the old build of the script) and
returns when the editor has exited by itself. A hang of the game on the main thread (seen once: a run
restart after a dead level load spun forever) or a crash of the editor can't be handled inside the test; the
script's watchdog kills the editor when Editor.log stays silent for `HANG_MINUTES` (6), closes the bug reporter
and starts the next attempt with `port/build/modsmoke/resume`: steps already done are taken over from
`report.json` (the report is written at the start of each step too), the one that was running is reported as
**hang** and the run goes on after it (`MAX_ATTEMPTS` 8; `RESUME=1` continues an interrupted run by hand). The first
editor start after `apply.sh` can come up with a broken script domain (Unity's own `UnityEngine.Timeline` / `UI` not
found, FullInspector throwing in every deserialization - Editor.log grew to 1.3 GB in 30 min and never went quiet): the
script kills that editor and starts it again with the same request. Before each action the test writes `[ModSmoke]   . <action>` to
Editor.log: when Mono itself crashes (the report can't record that) the last such line says what was going on
(exceptions the test catches itself get a stack of type + method names only, `SmokeLog.SafeStackTrace`, so formatting
them can't take Mono down; Unity formats the ones out of game callbacks itself); the
script closes the Unity Bug Reporter without sending (it attaches the whole project). With floors or flows a `diag`
step lists the room tables of Expand the Gungeon with entries the generator can't use (no room, failing
prerequisites). An error whose first frames are in a MonoBehaviour the editor refused to construct ("Called from
MonoBehaviour 'X'") is counted as **playmode** too: the fields its constructor sets stay null in the editor only
(Portable Ship, Iceberg Shavings; Ferryman Oar fails in `FerrymanOar.Add` because Alexandria's
`ProjectileSlashingBehaviour` creates its `SlashData` in the constructor).

Code: `UnityAssets/EtgModSmoke/` (runtime assembly; mods only through reflection, so it compiles and does nothing
without them) and `Editor/EtgWebBuild/EtgModSmokeMenu.cs` (passes the phases through `Temp/modsmoke.pending`, because
entering Play Mode reloads the script domain). Not covered: whether things look or sound right, mod NPCs / shrines /
room objects outside the visited rooms, synergies, bosses fought properly (spawned bosses are killed outright), co-op,
and what only WebGL / IL2CPP can show (null `this`, memory, missing stacks, Reflection.Emit) - for that the same runner
can be started in the player later.
