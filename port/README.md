### Differential test (`bash port/shaderdiff.sh`)

Finds decompiled shaders that compute something else than the original, without running the game. For every
variant (shader × pass × keywords × vertex/fragment): the original DXBC (dumped by `decompile_shaders.sh`) and the
decompiled CG of that variant, compiled back with `d3dcompiler_47` (`vs_4_0`/`ps_4_0`, Unity's auto-includes and
defines, the variant's keywords), both run on D3D11 WARP with the same random constants, textures (random mip
chains), vertex attributes / interpolants; outputs are compared with a tolerance, NaN/Inf, sign flips and
discards reported separately. For each mismatch it zeroes one input at a time to tell which constant/texture/input
the difference depends on. One run (3068 variants, 8 trials × 256 samples each) takes about 2 minutes.

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