using System;
using System.IO;
using System.Runtime.InteropServices;
using VRage.FileSystem;
using VRage.Render11.RenderContext;
using VRage.Render11.Resources;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Aurora;

/// <summary>
/// Render-thread side of the effect: compiles the pixel shader through the game's shader
/// registry, keeps the constant buffer filled and issues the fullscreen draw. Called from
/// the Harmony postfix on MyAtmosphereRenderer.RenderGBuffer; must only touch the passed
/// render context (the transparent stage may be recorded on a deferred context).
/// </summary>
public static class AuroraRenderer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AuroraConstants
    {
        public Vector4 CenterInner;     // xyz = camera-relative planet center, w = inner radius
        public Vector4 PoleOuter;       // xyz = pole axis, w = outer radius
        public Vector4 Tangent1;
        public Vector4 Tangent2;
        public Vector4 BandParams;      // sin(latLo), sin(latHi), feather, unused
        public Vector4 NoiseParams;     // tiling1, tiling2, threshold, push
        public Vector4 ScrollOffsets;   // layer1.xy, layer2.xy
        public Vector4 ColumnScroll;    // column layer offset.xy, tiling.z, unused.w
        public Vector4 ColorIntensity;  // rgb tint, w intensity
        public Vector4 StepParams;      // steps, dither, fade factor (night x distance), height variation
    }

    private static readonly int ConstantsSize = Marshal.SizeOf(typeof(AuroraConstants));
    private static readonly object InitLock = new object();

    // Set once at plugin init (game thread), read on the render thread.
    public static volatile string ShaderFilePath;

    private static volatile AuroraSnapshot snapshot;
    private static bool failed;
    private static bool shaderInitialized;
    private static MyPixelShaders.Id pixelShader;

    /// <summary>Publishes the latest game-thread snapshot (null hides the effect).</summary>
    public static void Publish(AuroraSnapshot value)
    {
        snapshot = value;
    }

    public static void OnDeviceReset()
    {
        // The shader registry recompiles our shader automatically; only the textures are ours.
        AuroraTextures.Invalidate();
    }

    /// <summary>Entry point from the render patch. Never throws: on the first error the effect disables itself.</summary>
    public static void Draw(MyRenderContext rc)
    {
        if (failed)
            return;
        try
        {
            DrawInternal(rc);
        }
        catch (Exception e)
        {
            failed = true;
            MyLog.Default.Error($"{Plugin.Name}: Aurora renderer failed, disabling for this session: {e}");
        }
    }

    private static void DrawInternal(MyRenderContext rc)
    {
        var snap = snapshot;
        if (snap == null)
            return;

        var config = Config.Current;
        if (!config.Enabled)
            return;

        float fadeFactor = ComputeNightFactor(snap, config) * ComputeDistanceFade(snap);
        if (fadeFactor <= 0f)
            return;

        if (!EnsureShader())
            return;
        AuroraTextures.EnsureCreated(config);

        var constants = FillConstants(snap, config, fadeFactor);

        rc.SetScreenViewport();
        rc.SetRasterizerState(MyRasterizerStateManager.NocullRasterizerState);
        rc.SetBlendState(MyBlendStateManager.BlendAdditive);
        rc.SetDepthStencilState(MyDepthStencilStateManager.IgnoreDepthStencil);
        rc.SetRtv(MyGBuffer.Main.DepthStencil.DsvRo, MyGBuffer.Main.LBuffer);

        IConstantBuffer cb = rc.GetObjectCB(ConstantsSize);
        rc.AllShaderStages.SetConstantBuffer(1, cb);
        var mapping = MyMapping.MapDiscard(rc, cb);
        mapping.WriteAndPosition(ref constants);
        mapping.Unmap();

        rc.PixelShader.Set(pixelShader);
        rc.PixelShader.SetSrv(20, AuroraTextures.Noise);
        rc.PixelShader.SetSrv(21, AuroraTextures.Ramp);
        rc.PixelShader.SetSrv(22, MyGBuffer.Main.ResolvedDepthStencil.SrvDepth);
        rc.PixelShader.SetSampler(6, MySamplerStateManager.CloudSampler);
        rc.PixelShader.SetSampler(7, MySamplerStateManager.Default);

        MyScreenPass.DrawFullscreenQuad(rc);

        rc.PixelShader.SetSrv(20, null);
        rc.PixelShader.SetSrv(21, null);
        rc.PixelShader.SetSrv(22, null);
        rc.SetDepthStencilState(null);
        rc.SetBlendState(null);
        rc.SetRasterizerState(null);
        rc.SetRtvNull();
    }

    private static bool EnsureShader()
    {
        if (shaderInitialized)
            return pixelShader != MyPixelShaders.Id.NULL;

        lock (InitLock)
        {
            if (shaderInitialized)
                return pixelShader != MyPixelShaders.Id.NULL;

            var path = ShaderFilePath;
            if (path == null)
            {
                shaderInitialized = true;
                MyLog.Default.Error($"{Plugin.Name}: No shader file available, the aurora will not render");
                return false;
            }

            try
            {
                // Compile a flattened copy with all game includes inlined: the include
                // handler callback is broken in the Linux build of the D3D compiler.
                var directory = Path.Combine(MyFileSystem.UserDataPath, "Storage", Plugin.Name);
                Directory.CreateDirectory(directory);
                var flattenedPath = Path.Combine(directory, "AuroraBorealis.flat.hlsl");
                ShaderFlattener.Flatten(path, flattenedPath);

                // The game's registry compiles at ps_5_0 with entry point __pixel_shader,
                // caches the bytecode and restores the shader after device resets.
                pixelShader = MyPixelShaders.Create(flattenedPath);
            }
            catch (Exception e)
            {
                pixelShader = MyPixelShaders.Id.NULL;
                MyLog.Default.Error($"{Plugin.Name}: Failed to compile {path}: {e.Message}");
            }

            shaderInitialized = true;
            return pixelShader != MyPixelShaders.Id.NULL;
        }
    }

    private static float ComputeNightFactor(AuroraSnapshot snap, Config config)
    {
        if (!config.NightOnly)
            return 1f;

        var up = (Vector3)Vector3D.Normalize(
            MyRender11.Environment.Matrices.CameraPosition - snap.PlanetCenter);
        // SunLightDirection points FROM the sun (the direction the light travels).
        var dirToSun = -MyRender11.Environment.Data.EnvironmentLight.SunLightDirection;
        float elevation = up.Dot(dirToSun);

        // Fully visible once the sun is 0.15 below the local horizon, gone at 0.05 above it.
        return MathHelper.Clamp((0.05f - elevation) / 0.20f, 0f, 1f);
    }

    // Computed per frame rather than in the game-thread snapshot so the fade tracks the
    // camera smoothly instead of stepping on each snapshot update.
    private static float ComputeDistanceFade(AuroraSnapshot snap)
    {
        float distance = (float)(MyRender11.Environment.Matrices.CameraPosition - snap.PlanetCenter).Length();
        if (distance <= snap.FadeStartDistance)
            return 1f;
        return MathHelper.Clamp(
            (snap.FadeEndDistance - distance) / Math.Max(snap.FadeEndDistance - snap.FadeStartDistance, 1f),
            0f, 1f);
    }

    private static AuroraConstants FillConstants(AuroraSnapshot snap, Config config, float fadeFactor)
    {
        var centerRel = (Vector3)(snap.PlanetCenter - MyRender11.Environment.Matrices.CameraPosition);

        var pole = snap.PoleAxis;
        var reference = Math.Abs(pole.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var tangent1 = Vector3.Normalize(Vector3.Cross(pole, reference));
        var tangent2 = Vector3.Cross(pole, tangent1);

        float halfWidth = config.LatitudeWidth * 0.5f;
        float latLo = MathHelper.ToRadians(MathHelper.Clamp(config.LatitudeCenter - halfWidth, 0f, 89.5f));
        float latHi = MathHelper.ToRadians(MathHelper.Clamp(config.LatitudeCenter + halfWidth, 1f, 89.9f));
        float sinLo = (float)Math.Sin(latLo);
        float sinHi = (float)Math.Sin(latHi);
        float feather = Math.Max((sinHi - sinLo) * 0.25f, 1e-4f);

        // Noise tiling over the azimuthal projection of the polar cap: how many curtains
        // fit across it. The two layers are close in scale so their difference forms thin
        // veins rather than blobs, and their ratio sets the vein shape, so the density
        // setting scales both together.
        float density = Math.Max(config.PatternDensity, 0.01f);
        float tiling1 = 9f * density;
        float tiling2 = 10.5f * density;

        // The per-column height and offset layer is the same noise at a lower frequency,
        // moving with layer 1. It gets its own tiling and offset rather than the shader
        // rescaling layer 1's: an offset wrapped into [0, 1) is only invisible to a wrapped
        // sampler while it is added at the scale it was wrapped for, and rescaling it makes
        // every wrap teleport the whole curtain structure at once.
        const float columnScale = 0.37f;

        // Scroll rates are in texture units per second, so the speed the pattern moves over
        // the ground is rate / tiling. Scaling them by the same density keeps that ratio
        // fixed, so the density setting changes the feature size without also changing how
        // fast the curtains drift.
        double rate1X = 0.005 * density;
        double rate1Y = 0.002 * density;
        double rate2X = -0.0035 * density;
        double rate2Y = 0.0025 * density;

        double t = MyCommon.FrameTime.Seconds * config.AnimationSpeed;
        float Frac(double v) => (float)(v - Math.Floor(v));
        var scroll = new Vector4(
            Frac(t * rate1X), Frac(t * rate1Y),
            Frac(t * rate2X), Frac(t * rate2Y));
        var columnScroll = new Vector4(
            Frac(t * rate1X * columnScale), Frac(t * rate1Y * columnScale),
            tiling1 * columnScale, 0f);

        return new AuroraConstants
        {
            CenterInner = new Vector4(centerRel, snap.InnerRadius),
            PoleOuter = new Vector4(pole, snap.OuterRadius),
            Tangent1 = new Vector4(tangent1, 0f),
            Tangent2 = new Vector4(tangent2, 0f),
            BandParams = new Vector4(sinLo, sinHi, feather, 0f),
            NoiseParams = new Vector4(tiling1, tiling2, 0.25f, 4f),
            ScrollOffsets = scroll,
            ColumnScroll = columnScroll,
            // The shader saturates its emission to this value, so it sets how far the
            // brightest curtains reach into the game's bloom. Scaled by the planet's
            // ground level air density, which is 1.0 on an Earthlike.
            ColorIntensity = new Vector4(1f, 1f, 1f, config.Intensity * snap.DensityFactor),
            StepParams = new Vector4(config.StepCount, 1f, fadeFactor, 0.6f),
        };
    }
}
