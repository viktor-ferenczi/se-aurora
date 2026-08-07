using VRageMath;

namespace ClientPlugin.Aurora;

/// <summary>
/// Immutable game-thread to render-thread data record. Published by <see cref="AuroraSampler"/>
/// on the simulation thread, consumed by <see cref="AuroraRenderer"/> on the render thread.
/// </summary>
public sealed class AuroraSnapshot
{
    /// <summary>Planet center in world coordinates (double precision; made camera-relative render-side).</summary>
    public readonly Vector3D PlanetCenter;

    /// <summary>Shell inner radius in meters.</summary>
    public readonly float InnerRadius;

    /// <summary>Shell outer radius in meters.</summary>
    public readonly float OuterRadius;

    /// <summary>Magnetic pole axis (unit vector, planet's local up).</summary>
    public readonly Vector3 PoleAxis;

    /// <summary>
    /// Brightness scale from the planet's ground level air density, relative to an
    /// Earthlike atmosphere (which is exactly 1.0). Always above zero: a planet without
    /// an atmosphere produces no snapshot at all.
    /// </summary>
    public readonly float DensityFactor;

    /// <summary>Camera distance from the planet center (meters) where the fade-out begins.</summary>
    public readonly float FadeStartDistance;

    /// <summary>Camera distance from the planet center (meters) where the aurora is fully faded out.</summary>
    public readonly float FadeEndDistance;

    public AuroraSnapshot(Vector3D planetCenter, float innerRadius, float outerRadius, Vector3 poleAxis,
        float densityFactor, float fadeStartDistance, float fadeEndDistance)
    {
        PlanetCenter = planetCenter;
        InnerRadius = innerRadius;
        OuterRadius = outerRadius;
        PoleAxis = poleAxis;
        DensityFactor = densityFactor;
        FadeStartDistance = fadeStartDistance;
        FadeEndDistance = fadeEndDistance;
    }
}
