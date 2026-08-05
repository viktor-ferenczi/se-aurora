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

    public AuroraSnapshot(Vector3D planetCenter, float innerRadius, float outerRadius, Vector3 poleAxis)
    {
        PlanetCenter = planetCenter;
        InnerRadius = innerRadius;
        OuterRadius = outerRadius;
        PoleAxis = poleAxis;
    }
}
