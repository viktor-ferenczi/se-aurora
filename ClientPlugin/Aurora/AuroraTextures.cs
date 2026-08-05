using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using VRage.Render11.Resources;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Aurora;

/// <summary>
/// Minimal <see cref="ISrvBindable"/> adapter around a plugin-created immutable texture,
/// so it can be bound with rc.PixelShader.SetSrv like any game texture.
/// </summary>
public sealed class AuroraTexture : ISrvBindable, IDisposable
{
    private readonly Texture2D texture;
    private readonly ShaderResourceView srv;
    private readonly Vector2I size;

    public string Name { get; }
    public SharpDX.Direct3D11.Resource Resource => texture;
    public ShaderResourceView Srv => srv;
    public Vector2I Size => size;
    public Vector3I Size3 => new Vector3I(size.X, size.Y, 1);

    public AuroraTexture(string name, int width, int height, byte[] rgbaPixels)
    {
        Name = name;
        size = new Vector2I(width, height);

        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        };

        var handle = GCHandle.Alloc(rgbaPixels, GCHandleType.Pinned);
        try
        {
            var data = new DataRectangle(handle.AddrOfPinnedObject(), width * 4);
            texture = new Texture2D(MyRender11.DeviceInstance, desc, new[] { data });
        }
        finally
        {
            handle.Free();
        }

        texture.DebugName = name;
        srv = new ShaderResourceView(MyRender11.DeviceInstance, texture);
    }

    public void Dispose()
    {
        srv.Dispose();
        texture.Dispose();
    }
}

/// <summary>
/// Owns the two procedurally generated textures of the effect: the tileable RGBA noise
/// and the vertical color/alpha gradient LUT. Recreated lazily after a device reset,
/// and the gradient is re-baked when the color settings change.
/// </summary>
public static class AuroraTextures
{
    private const int NoiseSize = 512;
    private const int RampSize = 256;

    private static readonly object CreateLock = new object();

    private static AuroraTexture noise;
    private static AuroraTexture ramp;
    private static int rampVersion;
    private static int bakedRampVersion = -1;

    public static AuroraTexture Noise => noise;
    public static AuroraTexture Ramp => ramp;

    /// <summary>Called (from any thread) when a color setting changes; the LUT is re-baked on next use.</summary>
    public static void MarkRampDirty()
    {
        System.Threading.Interlocked.Increment(ref rampVersion);
    }

    /// <summary>Called on device reset and session-independent teardown; textures are recreated lazily.</summary>
    public static void Invalidate()
    {
        lock (CreateLock)
        {
            noise?.Dispose();
            noise = null;
            ramp?.Dispose();
            ramp = null;
            bakedRampVersion = -1;
        }
    }

    /// <summary>Creates any missing texture. D3D11 devices are free-threaded, so this is safe off the immediate context.</summary>
    public static void EnsureCreated(Config config)
    {
        int wantedVersion = rampVersion;
        if (noise != null && ramp != null && bakedRampVersion == wantedVersion)
            return;

        lock (CreateLock)
        {
            if (noise == null)
                noise = CreateNoise();

            if (ramp == null || bakedRampVersion != wantedVersion)
            {
                ramp?.Dispose();
                ramp = CreateRamp(config);
                bakedRampVersion = wantedVersion;
            }
        }
    }

    private static AuroraTexture CreateNoise()
    {
        var pixels = new byte[NoiseSize * NoiseSize * 4];
        // R/G: the two difference-cloud layers; B/A: lower-frequency curtain height and vertical offset.
        NoiseGenerator.FillChannel(pixels, NoiseSize, 0, 8, 4, 12345);
        NoiseGenerator.FillChannel(pixels, NoiseSize, 1, 8, 4, 54321);
        NoiseGenerator.FillChannel(pixels, NoiseSize, 2, 4, 3, 98765);
        NoiseGenerator.FillChannel(pixels, NoiseSize, 3, 4, 3, 56789);
        return new AuroraTexture("AuroraPerlin", NoiseSize, NoiseSize, pixels);
    }

    private static AuroraTexture CreateRamp(Config config)
    {
        config.GetGradientColors(out Vector3 bottom, out Vector3 top);

        var pixels = new byte[RampSize * 4];

        // Height falloff: sharp bright lower edge, long fading upper tail (normalized to peak 1).
        var alpha = new float[RampSize];
        float peak = 0f;
        for (int i = 0; i < RampSize; i++)
        {
            float h = (float)i / (RampSize - 1);
            float rise = MathHelper.SmoothStep(0f, 1f, MathHelper.Clamp((h - 0.02f) / 0.08f, 0f, 1f));
            alpha[i] = rise * (float)Math.Exp(-3.0 * h);
            if (alpha[i] > peak)
                peak = alpha[i];
        }

        for (int i = 0; i < RampSize; i++)
        {
            float h = (float)i / (RampSize - 1);
            float blend = MathHelper.SmoothStep(0f, 1f, MathHelper.Clamp((h - 0.05f) / 0.80f, 0f, 1f));
            Vector3 color = Vector3.Lerp(bottom, top, blend);
            pixels[i * 4 + 0] = (byte)MathHelper.Clamp(color.X * 255f, 0f, 255f);
            pixels[i * 4 + 1] = (byte)MathHelper.Clamp(color.Y * 255f, 0f, 255f);
            pixels[i * 4 + 2] = (byte)MathHelper.Clamp(color.Z * 255f, 0f, 255f);
            pixels[i * 4 + 3] = (byte)MathHelper.Clamp(alpha[i] / peak * 255f, 0f, 255f);
        }

        return new AuroraTexture("AuroraColorRamp", RampSize, 1, pixels);
    }
}
