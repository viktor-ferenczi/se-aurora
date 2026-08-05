using Sandbox.Game.Entities.Planet;
using Sandbox.Game.World;
using VRageMath;

namespace ClientPlugin.Aurora;

/// <summary>
/// Game-thread side: picks the nearest atmospheric planet, derives the aurora shell
/// parameters and publishes an immutable snapshot for the render thread.
/// Must never be called from the render thread.
/// </summary>
public static class AuroraSampler
{
    // Update every half second of simulation; planets do not move.
    private const int UpdateInterval = 30;

    // Range gating with hysteresis to avoid flickering at the boundary (relative to atmosphere radius).
    private const double EnterRangeFactor = 12.0;
    private const double ExitRangeFactor = 13.0;

    private static int frameCounter;
    private static bool active;

    public static void Update()
    {
        if (frameCounter++ % UpdateInterval != 0)
            return;

        AuroraRenderer.Publish(BuildSnapshot());
    }

    public static void OnSessionUnloading()
    {
        active = false;
        AuroraRenderer.Publish(null);
    }

    private static AuroraSnapshot BuildSnapshot()
    {
        var config = Config.Current;
        if (!config.Enabled)
        {
            active = false;
            return null;
        }

        if (MySession.Static == null || MySector.MainCamera == null || MyPlanets.Static == null)
        {
            active = false;
            return null;
        }

        var cameraPosition = MySector.MainCamera.Position;
        var planet = MyPlanets.Static.GetClosestPlanet(cameraPosition);
        if (planet == null || planet.Closed || !planet.HasAtmosphere || planet.AtmosphereRadius <= 0f)
        {
            active = false;
            return null;
        }

        var center = planet.PositionComp.GetPosition();
        double distance = (cameraPosition - center).Length();
        double range = planet.AtmosphereRadius * (active ? ExitRangeFactor : EnterRangeFactor);
        if (distance > range)
        {
            active = false;
            return null;
        }
        active = true;

        // Fit the shell between the surface and the top of the atmosphere.
        float surface = planet.AverageRadius;
        float top = planet.AtmosphereRadius;
        float inner = MathHelper.Lerp(surface, top, config.AltitudeMin);
        float outer = MathHelper.Lerp(surface, top, config.AltitudeMax);
        if (outer < inner + 100f)
            outer = inner + 100f;

        var pole = (Vector3)planet.WorldMatrix.Up;
        pole.Normalize();

        return new AuroraSnapshot(center, inner, outer, pole);
    }
}
