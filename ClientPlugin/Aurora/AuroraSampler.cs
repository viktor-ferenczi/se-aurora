using System;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.World;
using VRage.Utils;
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

    // Extra margin past the configured fade end for leaving the active state, so the
    // snapshot gating does not churn at the boundary (relative to atmosphere radius).
    // Since the effect fades to zero brightness at the fade end distance, the snapshot
    // appearing or disappearing there is invisible.
    private const double ExitRangeMargin = 2.0;

    // Ground level air density of the vanilla EarthLike generator, the reference for the
    // brightness scale. MyPlanetAtmosphere.Density also defaults to this when a planet
    // definition omits it.
    private const float EarthAirDensity = 1.0f;

    private static int frameCounter;
    private static bool active;
    private static long loggedPlanetId;

    public static void Update()
    {
        if (frameCounter++ % UpdateInterval != 0)
            return;

        AuroraRenderer.Publish(BuildSnapshot());
    }

    // One line per planet the aurora switches to, so the chosen target and its brightness
    // can be checked in the log instead of being guessed from the screen.
    private static void LogPlanet(MyPlanet planet, string detail)
    {
        if (planet.EntityId == loggedPlanetId)
            return;
        loggedPlanetId = planet.EntityId;
        MyLog.Default.Info($"{Plugin.Name}: nearest planet '{planet.Generator?.Id.SubtypeName}': {detail}");
    }

    public static void OnSessionUnloading()
    {
        active = false;
        loggedPlanetId = 0;
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
        if (planet == null || planet.Closed)
        {
            active = false;
            return null;
        }

        // Only planets with an atmosphere get an aurora; airless planets and moons are out.
        if (!planet.HasAtmosphere || planet.AtmosphereRadius <= 0f)
        {
            LogPlanet(planet, "no atmosphere, aurora disabled");
            active = false;
            return null;
        }

        // Distance fade: full brightness out to the start factor, then a linear fade to
        // zero at the end factor (applied per-frame on the render thread; here they only
        // gate the snapshot). The end factor is clamped so it never sits below the start.
        double fadeStartFactor = config.FadeStartFactor;
        double fadeEndFactor = Math.Max(config.FadeEndFactor, fadeStartFactor);

        var center = planet.PositionComp.GetPosition();
        double distance = (cameraPosition - center).Length();
        double range = planet.AtmosphereRadius * (fadeEndFactor + (active ? ExitRangeMargin : 0.0));
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

        // Brightness follows the air density at ground level. MyPlanet.GetAirDensity()
        // reduces to the generator's Atmosphere.Density at AverageRadius, and returns 0
        // for a planet or moon without an atmosphere, which switches the effect off.
        float groundDensity = planet.GetAirDensity(center + pole * planet.AverageRadius);
        if (groundDensity <= 0f)
        {
            LogPlanet(planet, "no atmosphere, aurora disabled");
            active = false;
            return null;
        }

        float densityFactor = groundDensity / EarthAirDensity;
        LogPlanet(planet, $"air density {groundDensity:0.###} -> brightness x{densityFactor:0.###}, " +
                          $"shell {inner / 1000f:0.#}-{outer / 1000f:0.#} km");

        float fadeStart = (float)(planet.AtmosphereRadius * fadeStartFactor);
        float fadeEnd = (float)(planet.AtmosphereRadius * fadeEndFactor);

        return new AuroraSnapshot(center, inner, outer, pole, densityFactor, fadeStart, fadeEnd);
    }
}
