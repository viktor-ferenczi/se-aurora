# Aurora — Volumetric Aurora Borealis for Space Engineers

Implementation plan for a **client plugin** (Pulsar) that renders a volumetric Aurora
Borealis over planet poles using a custom HLSL pixel shader injected into the game's
DirectX 11 render pipeline (`VRage.Render11`).

All game internals referenced below were verified against the decompiled sources of game
version **1.210.011 b1** (matches the current `se-dev-game-book` / `se-dev-game-code` data).

## 1. Reference technique

Based on Roy Theunissen's Unity implementation:

- Blog: https://blog.roytheunissen.com/2022/09/17/aurora-borealis-a-breakdown/
- Repo: https://github.com/RoyTheunissen/Aurora-Borealis-Unity
  (key file: `Assets/_Aurora-Borealis-Unity/Shaders/Nature/AuroraBorealis.shader`)

Summary of the technique (all of it is portable, plain SM 5.0-level HLSL):

- **Raymarching through a volume.** One proxy volume; the fragment shader intersects the
  view ray with the volume bounds, then marches between entry and exit with a fixed step,
  additively accumulating emission. No 3D textures, no compute, no geometry shaders.
- **"Difference clouds" shape function.** Two independently scrolling Perlin noise samples
  (different channels of one RGBA noise texture); `abs(a - b)` produces thin veins where
  they match, a contrast push + invert turns the veins into bright curtain bands:

  ```hlsl
  float noise = abs(perlin1.b - perlin2.g);
  noise = (noise - threshold) * push + threshold;
  return 1 - saturate(noise);
  ```

- **2.5D volume.** Noise is sampled on the horizontal plane only (local XZ) and extruded
  vertically; two extra noise channels drive per-column curtain height and vertical offset.
- **Vertical color/alpha ramp.** A 256×1 gradient LUT (green at the bottom with a sharp
  bright lower edge, fading through teal/purple with a long upper tail) sampled by the
  normalized height within the volume.
- **Animation.** No flow maps or curl noise — just two UV scroll offsets at different time
  scales; the differential drift makes the difference-veins writhe organically.
- **Additive HDR blending** (`Blend One One`, ZWrite off); the emission exceeds 1.0 and the
  engine's bloom does the glow. Output alpha is irrelevant.
- The Unity repo also contains scene-depth reconstruction plumbing that is **dead code**
  (never used) — we do not port it; we do our own depth occlusion (see §4.4).

Adaptation needed for SE: the Unity version marches an axis-aligned **box** hanging in a
flat sky. SE has spherical planets, so we march a **spherical shell segment** (between an
inner and outer radius around the planet center), masked to a configurable latitude band
around the poles. Vertical coordinate = radial altitude within the shell; noise UV =
longitude/latitude-derived coordinates.

## 2. Integration points into the game renderer (verified)

### 2.1 Where in the frame to draw

The transparent stage driver `MyTransparentRendering.Render(MyRenderContext rc, ...)`
(`VRage.Render11\VRageRender\MyTransparentRendering.cs:183`) runs after deferred lighting
and executes, in order: **atmosphere → clouds → flares/billboards → GPU particles →
transparent models → OIT resolve → additive-top billboards**.

**Patch point: Harmony postfix on `MyAtmosphereRenderer.RenderGBuffer(MyRenderContext rc)`**
(`VRage.Render11\VRageRender\MyAtmosphereRenderer.cs`).

- It is called unconditionally as the first step of the transparent stage (unlike
  `MyCloudRenderer.Render`, which is gated by `MyRender11.DebugOverrides.Clouds`).
- The `rc` parameter is the correct render context (the stage may be recorded on a
  *deferred* context on a worker thread — see risks, §8).
- Drawing right after the atmosphere and before clouds/billboards means clouds and
  weather particles composite naturally over the aurora, and the aurora composites over
  the atmosphere haze.
- Not called for environment probes (those use `RenderEnvProbe`) — the aurora will simply
  not appear in reflection probes, which is acceptable.

### 2.2 Render target and state (mirroring `MyCloudRenderer.Render`)

`MyCloudRenderer.Render` (`MyCloudRenderer.cs:130`) is the exact pattern to mirror:

```csharp
rc.SetScreenViewport();
rc.SetRasterizerState(MyRasterizerStateManager.NocullRasterizerState);
rc.SetBlendState(MyBlendStateManager.BlendAdditive);          // aurora is pure emission
rc.SetDepthStencilState(MyDepthStencilStateManager.IgnoreDepthStencil);
rc.SetRtv(MyGBuffer.Main.DepthStencil.DsvRo, MyGBuffer.Main.LBuffer);  // HDR light buffer
IConstantBuffer cb = rc.GetObjectCB(sizeof(AuroraConstants));
rc.AllShaderStages.SetConstantBuffer(1, cb);                  // slot b1, like CloudsConstants
// write constants via MyMapping.MapDiscard(rc, cb) → WriteAndPosition → Unmap
// restore all state to null afterwards, exactly as MyCloudRenderer does
```

`MyBlendStateManager.BlendAdditive` exists
(`VRage.Render11\VRage\Render11\Resources\MyBlendStateManager.cs`). Rendering into
`MyGBuffer.Main.LBuffer` before tonemapping means the game's bloom/tonemap gives us the
HDR glow for free — same as the Unity original relies on Unity's bloom.

Frame constants (camera, matrices) are already bound at slot **b0** for the whole
transparent stage (`rc.PixelShader.SetConstantBuffer(0, MyCommon.FrameConstants)` in
`MyTransparentRendering.Render`); our shader includes `<Frame.hlsli>` to access them.

### 2.3 Geometry: fullscreen pass (v1)

`MyScreenPass.DrawFullscreenQuad(MyRenderContext rc, ...)`
(`VRage.Render11\VRageRender\MyScreenPass.cs`) binds its own fullscreen vertex buffer,
input layout and vertex shader (`Postprocess/PostprocessCopy.hlsl`) and issues the draw.
We only bind our **pixel shader** and call it — no custom vertex shader, vertex buffers or
input layouts needed. The PS reconstructs the world-space view ray per pixel from the
frame constants (inverse view-projection is available in `Frame.hlsli`; the game's own
postprocess shaders do the same reconstruction).

A later optimization (§7, phase 6) can switch to a proxy mesh to shrink the rasterized
area, but the fullscreen pass early-outs on the ray/shell intersection test in a few ALU
ops, so it is fine for v1.

### 2.4 Compiling the custom HLSL shader

`MyPixelShaders.Create(string file, ShaderMacro[] macros = null)`
(`VRage.Render11\VRageRender\MyPixelShaders.cs:63`) compiles at profile `ps_5_0`, entry
point **`__pixel_shader`** (the game's convention — see `Transparent/Clouds/Clouds.hlsl`),
caches by (file, macros), and returns an `Id` that is implicitly convertible to
`PixelShader` and **survives device resets** (the game recompiles all registered shaders
in `MyShaders.Recompile`). This is the single biggest win of going through the game's
shader registry instead of raw D3DCompiler: live shader reload and device-reset handling
come for free.

File resolution (`MyShaderCompiler.Compile`, `MyShaderCompiler.cs`):

```csharp
string text = PathUtils.Normalize(Path.Combine(ShadersPath, info.File.ToString()));
```

`Path.Combine` returns the second argument unchanged when it is rooted, so **passing an
absolute path works**. The plugin ships `AuroraBorealis.hlsl` as an embedded resource,
extracts it at init to its own storage folder, and passes the absolute path to
`MyPixelShaders.Create`. `#include <Frame.hlsli>` etc. still resolve, because the include
processor falls back to the global include directory = `<game>/Content/Shaders`.

The runtime compile logs a harmless "Shader was not precompiled" warning (cache miss) on
first run; afterwards the game's shader cache holds the bytecode.

### 2.5 Noise and gradient textures

Two small textures, both **generated procedurally in C# at init** (no asset pipeline):

- `RGBA8 512×512` tileable multi-octave Perlin: R/G = two independent noise layers for
  the difference-clouds function, B = curtain height variation, A = vertical offset
  variation. (Same channel layout idea as the Unity `_PerlinTex`.)
- `RGBA8 256×1` gradient LUT baked from the configured color ramp (RGB = color,
  A = height falloff with sharp start / long tail).

Upload as immutable `SharpDX.Direct3D11.Texture2D` + `ShaderResourceView` on
`MyRender11.DeviceInstance`, wrapped in a trivial adapter implementing
`VRage.Render11.Resources.ISrvBindable` (interface is just `Name`, `Resource`, `Size`,
`Size3`, `Srv` — verified in `ISrvBindable.cs` / `IResource.cs`) so it can be bound with
`rc.PixelShader.SetSrv(slot, texture)`.

Device-reset handling: recreate the textures when the device generation changes (Harmony
postfix on `MyRender11.OnDeviceReset` or lazy recreation guarded by a disposed flag).

Fallback option if texture plumbing misbehaves: compute value noise directly in the
shader (pure ALU, no textures) — costlier per step but zero resource management.

### 2.6 Game-side data (game thread → render thread)

The render patch must not touch game-world objects. Data flow:

- `Plugin.Update()` (called every simulation frame) reads, when a session is running:
  - `MyPlanets.Static.GetClosestPlanet(cameraPos)` / `GetPlanets()`
    (`Sandbox.Game\Sandbox\Game\Entities\Planet\MyPlanets.cs`)
  - Per planet: center, `AverageRadius`, `AtmosphereRadius`, has-atmosphere flag, and the
    planet's local up/north axis (`planet.WorldMatrix.Up`) for the magnetic pole axis.
- It computes the aurora shell parameters (inner/outer radius, pole axis, latitude band,
  colors, intensity) and publishes an immutable snapshot into a `static volatile`
  reference (or under a simple lock). The render-thread patch reads the latest snapshot.
- Camera-relative positioning is done render-side (like `MyCloudRenderer`):
  `planetCenter - MyRender11.Environment.Matrices.CameraPosition` goes into the constant
  buffer as `float3`, so double-precision world coordinates never reach the GPU.
- Night-side modulation reads the sun direction render-side from
  `MyRender11.Environment` data (auroras are only visible against a dark sky; intensity
  is scaled by how deep the camera-side of the shell is in shadow).

## 3. Shader design (`AuroraBorealis.hlsl`)

Single file, pixel shader only (`__pixel_shader` entry), input signature matching the
`Postprocess/PostprocessCopy.hlsl` vertex output (`SV_Position` + `TEXCOORD0` uv).

```
cbuffer AuroraConstants : register(b1)
    float3 PlanetCenterRel;  float InnerRadius;      // camera-relative center, meters
    float  OuterRadius;      float3 PoleAxis;
    float4 BandParams;       // cos(latMin), cos(latMax), band feather, unused
    float4 NoiseParams;      // scale1, scale2, threshold, push
    float4 ScrollSpeeds;     // layer1.xy, layer2.xy
    float4 AnimTime;         // time at two rates (game supplies; avoids frame-counter drift)
    float4 ColorIntensity;   // HDR tint rgb, master intensity
    float4 StepParams;       // step count, dither strength, night factor, StartYVariation
Texture2D PerlinTex   : register(t20);   // high slots to avoid stage collisions
Texture2D ColorRamp   : register(t21);
Texture2D<float> Depth : register(t22);  // resolved scene depth for occlusion
```

Pixel shader outline:

1. Reconstruct the world-space (camera-relative) ray direction from the pixel position
   and the inverse view-projection in `Frame.hlsli`.
2. Ray–sphere intersect against outer and inner shell radii → march segment
   `[tEnter, tExit]`; `discard` on miss. Clamp `tEnter` to 0 (camera inside shell works).
3. Sample scene depth, reconstruct linear view distance, clamp `tExit` to it
   (terrain/ships occlude the aurora when looking up from the ground).
4. Jitter the march start by a screen-space hash (blue-noise style dither) to hide
   banding at low step counts.
5. Fixed-count loop (quality setting: 24/48/96 steps), step = segment length / N —
   bounded cost, unlike the Unity `while` loop:
   - `r = length(p - center)`; altitude `h = saturate((r - Ri) / (Ro - Ri))`
   - `dir = (p - center)/r`; latitude/longitude from `dot(dir, PoleAxis)` and a tangent
     basis; latitude band mask via `smoothstep` (both poles: use `abs`)
   - noise UV from (longitude, latitude) scaled; two scrolled samples → difference-clouds
     curtain function (verbatim math from the reference, §1)
   - per-column curtain height & vertical offset from the extra noise channels;
     remap `h` accordingly and sample the 1D `ColorRamp`
   - accumulate `ColorIntensity.rgb * ramp.rgb * ramp.a * curtain * bandMask * stepSize`
6. Multiply by night factor, output `float4(color, 1)` — blend state is additive so
   alpha is ignored.

Known v1 simplifications (documented, revisit later): only the first shell segment is
marched when the ray would cross the shell twice past the planet's limb; longitude seam
handled by matching the noise tiling period to 2π; MSAA uses resolved depth (no
per-sample pass, minor edge artifacts at worst); no VR/stereo support.

## 4. Plugin architecture (new/changed files)

```
ClientPlugin/
  Plugin.cs                     — init order: config → AuroraRenderer.Init hookup; Update() publishes snapshot
  Config.cs                     — settings (see §6)
  Aurora/
    AuroraSnapshot.cs           — immutable game→render data record
    AuroraSampler.cs            — game-thread logic: pick planet, build snapshot
    AuroraRenderer.cs           — render-thread: lazy init, per-frame draw (called from patch)
    AuroraTextures.cs           — CPU noise + gradient generation, D3D upload, ISrvBindable adapter
    NoiseGenerator.cs           — tileable multi-octave Perlin (CPU)
  Patches/
    AtmosphereRendererPatch.cs  — Harmony postfix on MyAtmosphereRenderer.RenderGBuffer
    (optional) Render11DeviceResetPatch.cs — texture recreation on device reset
  Shaders/
    AuroraBorealis.hlsl         — embedded resource, extracted at runtime
Docs/
  Plan.md                       — this document
```

The template's example patches (`ExamplePrefixPostfixPatch.cs`, `ExampleTranspilerPatch.cs`,
`Preloader.cs` remains unused) are removed; no preloader/transpiler is needed — a plain
postfix suffices.

Robustness rule: every entry point from the patch wraps its body in `try/catch`; on the
first exception the renderer disables itself for the session and logs once (a broken
visual plugin must never crash or spam the render loop).

## 5. Build changes

- **Publicizer**: uncomment the `Krafs.Publicizer` blocks in `ClientPlugin.csproj` and
  `Tools/GameAssembliesToPublicize.cs`; publicize `VRage.Render11` (everything we call is
  `internal`) plus `Sandbox.Game` (for `MyPlanets`). Keep the template's existing
  `DoNotPublicize` entries.
- **References to add** (all present in `Bin64`): `SharpDX`, `SharpDX.Direct3D11`,
  `SharpDX.DXGI` (needed for `ShaderMacro` — in the `SharpDX.Direct3D` namespace inside
  `SharpDX.dll` — texture upload and SRVs). Note the game does **not** ship
  `SharpDX.Mathematics.dll`; the D3D11 API surface uses the
  `SharpDX.Mathematics.Interop.Raw*` structs that live inside `SharpDX.dll`, and all
  CPU-side math uses `VRageMath`. `VRage.Render11` and `VRage.Render` are already
  referenced.
- Embed `Shaders/AuroraBorealis.hlsl` as `EmbeddedResource`.
- `unsafe` not required if the constant-buffer struct size is obtained via
  `Marshal.SizeOf`; otherwise enable `AllowUnsafeBlocks` (the game code uses
  `sizeof(struct)` — either is fine).

## 6. Configuration (template settings UI)

Using the template's `Settings` framework (checkbox/slider/color/dropdown, persisted by
`ConfigStorage`):

| Setting | Default | Notes |
|---|---|---|
| Enabled | on | master switch |
| Intensity | 1.0 | HDR multiplier 0–4 |
| Quality | Medium | Low/Medium/High → 24/48/96 steps |
| Color preset / custom colors | green→purple | bakes the gradient LUT (re-uploaded on change) |
| Latitude band center / width | 70° / 12° | degrees, both hemispheres |
| Altitude min / max | fitted between planet atmosphere shell bounds | relative to atmosphere thickness |
| Animation speed | 1.0 | scales scroll speeds |
| Night only | on | scale by sun direction |

## 7. Implementation phases

1. **Pipeline proof.** Csproj/publicizer changes; Harmony postfix in place; compile a
   trivial pixel shader from an absolute path; fullscreen draw that additively tints the
   sky faintly. Proves: patching, shader compilation, state setup, LBuffer output.
2. **Shell raymarch.** Constant buffer, ray reconstruction, ray–shell intersection,
   fixed-step march producing a uniform green band over the pole of the nearest planet;
   depth occlusion; dither.
3. **Aurora look.** Noise/gradient texture generation and binding; difference-clouds
   curtains; height variation; animation; HDR tuning against the game's bloom.
4. **Gameplay integration.** Game-thread sampler (nearest planet with atmosphere,
   in-range gating, hysteresis), night-side modulation, graceful enable/disable on
   session load/unload.
5. **Settings & persistence.** Config UI wiring, gradient re-bake on change, quality
   presets.
6. **Polish.** Device-reset resilience, performance measurement (GPU frame time with the
   effect on/off at each quality), optional proxy-mesh optimization, limb double-segment
   handling if visually needed, README + screenshots.

Each phase ends with an in-game test on the Earthlike planet's pole (the repo's
`.run/Vanilla.run.xml` config; the template's `Deploy.bat` copies the DLL into Pulsar's
local plugins folder on build).

## 8. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Transparent stage records on a **deferred context** on a worker thread | Only touch the passed `rc`; never `MyRender11.RC`. Resource *creation* (device calls) is done lazily under a lock — D3D11 devices are free-threaded; contexts are not. |
| Shader compile failure would throw `MyRenderException` inside the render loop | Compile once at init inside try/catch; on failure disable the feature and log. Never compile per-frame. |
| Absolute-path trick for shader files stops working after a game update | Fallback A: reflect `MyShaderCompiler.m_includes` and register our folder; fallback B: in-shader procedural noise + copying the file under `Content/Shaders` is explicitly avoided (dirty). Version pin noted below. |
| `__pixel_shader` entry / include conventions | Verified against `Clouds.hlsl` and `MyShaderCompiler.ProfileEntryPoint` for 1.210.011; re-verify on game updates. |
| Device reset loses our textures | Shader registry restores shaders automatically; textures recreated via reset hook / lazy check. |
| Performance on large screens | Fixed step count, early discard outside the shell, quality presets; measure in phase 6. |
| Game update changes internals | The plan pins to 1.210.011 b1; the `se-dev-game-code` git history makes diffs against future versions cheap. |

## 9. Out of scope (for now)

- Server/multiplayer sync (purely client-side visual).
- Aurora visible from environment probes / reflections.
- VR/stereo rendering support.
- Per-planet-definition aurora parameters via modded SBC data (could come later; v1 uses
  one global config applied to the nearest atmospheric planet).
