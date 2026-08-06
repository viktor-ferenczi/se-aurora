using System;

namespace ClientPlugin.Aurora;

/// <summary>
/// Tileable multi-octave gradient (Perlin) noise generated on the CPU at init.
/// The lattice gradients repeat with the given period, so the result tiles seamlessly.
/// </summary>
public static class NoiseGenerator
{
    /// <summary>
    /// Fills one channel of an RGBA8 texture with tileable fractal noise normalized to 0..255.
    /// </summary>
    /// <param name="pixels">RGBA8 pixel buffer of size*size*4 bytes.</param>
    /// <param name="size">Texture edge length in pixels.</param>
    /// <param name="channel">Byte offset within each pixel (0=R, 1=G, 2=B, 3=A).</param>
    /// <param name="basePeriod">Lattice period of the first octave (must divide size).</param>
    /// <param name="octaves">Number of octaves, each doubling the frequency.</param>
    /// <param name="seed">Seed for the gradient lattice.</param>
    public static void FillChannel(byte[] pixels, int size, int channel, int basePeriod, int octaves, int seed)
    {
        var values = new float[size * size];
        float min = float.MaxValue;
        float max = float.MinValue;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float sum = 0f;
                float amplitude = 1f;
                int period = basePeriod;
                for (int octave = 0; octave < octaves; octave++)
                {
                    float fx = (float)x / size * period;
                    float fy = (float)y / size * period;
                    sum += amplitude * Gradient2D(fx, fy, period, seed + octave * 7919);
                    amplitude *= 0.5f;
                    period *= 2;
                }

                values[y * size + x] = sum;
                if (sum < min) min = sum;
                if (sum > max) max = sum;
            }
        }

        float scale = max > min ? 255f / (max - min) : 0f;
        for (int i = 0; i < values.Length; i++)
        {
            pixels[i * 4 + channel] = (byte)Math.Round((values[i] - min) * scale);
        }
    }

    private static float Gradient2D(float x, float y, int period, int seed)
    {
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        float tx = x - x0;
        float ty = y - y0;

        float d00 = GradDot(x0, y0, tx, ty, period, seed);
        float d10 = GradDot(x0 + 1, y0, tx - 1f, ty, period, seed);
        float d01 = GradDot(x0, y0 + 1, tx, ty - 1f, period, seed);
        float d11 = GradDot(x0 + 1, y0 + 1, tx - 1f, ty - 1f, period, seed);

        float sx = Fade(tx);
        float sy = Fade(ty);
        float a = d00 + sx * (d10 - d00);
        float b = d01 + sx * (d11 - d01);
        return a + sy * (b - a);
    }

    private static float GradDot(int ix, int iy, float dx, float dy, int period, int seed)
    {
        // Wrap the lattice so the noise tiles with the period.
        ix = ((ix % period) + period) % period;
        iy = ((iy % period) + period) % period;

        uint h = (uint)(ix * 374761393 + iy * 668265263 + seed * 1274126177);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;

        double angle = h * (2.0 * Math.PI / uint.MaxValue);
        return dx * (float)Math.Cos(angle) + dy * (float)Math.Sin(angle);
    }

    private static float Fade(float t)
    {
        return t * t * t * (t * (t * 6f - 15f) + 10f);
    }
}
