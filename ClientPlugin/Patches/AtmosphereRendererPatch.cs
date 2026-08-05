using ClientPlugin.Aurora;
using HarmonyLib;
using VRage.Render11.RenderContext;
using VRageRender;

namespace ClientPlugin.Patches;

/// <summary>
/// Draws the aurora right after the atmosphere (and its clouds) in the transparent stage.
/// RenderGBuffer is called unconditionally for the main view only (environment probes use
/// RenderEnvProbe), and rc is the context the stage is being recorded on, which may be a
/// deferred context on a worker thread.
/// </summary>
[HarmonyPatch(typeof(MyAtmosphereRenderer), nameof(MyAtmosphereRenderer.RenderGBuffer))]
public static class AtmosphereRendererPatch
{
    public static void Postfix(MyRenderContext rc)
    {
        // AuroraRenderer.Draw never throws; it disables itself on the first error.
        AuroraRenderer.Draw(rc);
    }
}
