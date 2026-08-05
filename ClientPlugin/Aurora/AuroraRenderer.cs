using System;
using System.Runtime.InteropServices;
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
        public Vector4 ColorIntensity;  // rgb tint, w intensity
        public Vector4 StepParams;      // steps, dither, night factor, height variation
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

        float nightFactor = ComputeNightFactor(snap, config);
        if (nightFactor <= 0f)
            return;

        if (!EnsureShader())
            return;
        AuroraTextures.EnsureCreated(config);

        var constants = FillConstants(snap, config, nightFactor);

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
                // The game's registry compiles at ps_5_0 with entry point __pixel_shader,
                // caches the bytecode and restores the shader after device resets.
                pixelShader = MyPixelShaders.Create(path);
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

    private static AuroraConstants FillConstants(AuroraSnapshot snap, Config config, float nightFactor)
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

        // Noise tiling over the azimuthal projection of the polar cap. The two layers are
        // close in scale so their difference forms thin veins rather than blobs.
        const float tiling1 = 18f;
        const float tiling2 = 21f;

        double t = MyCommon.FrameTime.Seconds * config.AnimationSpeed;
        float Frac(double v) => (float)(v - Math.Floor(v));
        var scroll = new Vector4(
            Frac(t * 0.010), Frac(t * 0.004),
            Frac(t * -0.007), Frac(t * 0.005));

        return new AuroraConstants
        {
            CenterInner = new Vector4(centerRel, snap.InnerRadius),
            PoleOuter = new Vector4(pole, snap.OuterRadius),
            Tangent1 = new Vector4(tangent1, 0f),
            Tangent2 = new Vector4(tangent2, 0f),
            BandParams = new Vector4(sinLo, sinHi, feather, 0f),
            NoiseParams = new Vector4(tiling1, tiling2, 0.25f, 4f),
            ScrollOffsets = scroll,
            // The shader saturates its emission to this value, so the boost sets how far the
            // brightest curtains reach into the game's bloom.
            ColorIntensity = new Vector4(1f, 1f, 1f, config.Intensity * 1f),
            StepParams = new Vector4(config.StepCount, 1f, nightFactor, 0.6f),
        };
    }
}
