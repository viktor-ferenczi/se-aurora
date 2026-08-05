using ClientPlugin.Aurora;
using HarmonyLib;
using VRageRender;

namespace ClientPlugin.Patches;

/// <summary>
/// The game's shader registry recompiles our pixel shader after a device reset on its own;
/// the plugin-created textures have to be dropped and recreated lazily.
/// </summary>
[HarmonyPatch(typeof(MyRender11), "OnDeviceReset")]
public static class Render11DeviceResetPatch
{
    public static void Postfix()
    {
        AuroraRenderer.OnDeviceReset();
    }
}
