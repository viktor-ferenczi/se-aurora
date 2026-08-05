using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using ClientPlugin.Aurora;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Layouts;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using VRage.FileSystem;
using VRage.Plugins;
using VRage.Utils;

// Define assembly version when compiled by Pulsar
#if !DEV_BUILD
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#endif

namespace ClientPlugin;

// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    public const string Name = "Aurora";
    public static Plugin Instance { get; private set; }
    private SettingsGenerator settingsGenerator;
    private bool updateFailed;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
        Instance = this;
        Instance.settingsGenerator = new SettingsGenerator();

        ExtractShader();
        Config.Current.PropertyChanged += OnConfigPropertyChanged;

        var harmony = new Harmony(Name);
        harmony.PatchAll(Assembly.GetExecutingAssembly());
    }

    public void Dispose()
    {
        // IMPORTANT: Do NOT call harmony.UnpatchAll() here! It may break other plugins.
        AuroraRenderer.Publish(null);
        Instance = null;
    }

    public void Update()
    {
        if (updateFailed)
            return;
        try
        {
            AuroraSampler.Update();
        }
        catch (Exception e)
        {
            updateFailed = true;
            AuroraRenderer.Publish(null);
            MyLog.Default.Error($"{Name}: Update failed, disabling for this session: {e}");
        }
    }

    // The shader must exist as a file, because the game's shader compiler loads shaders
    // from disk (and reloads them after device resets). Extract the embedded resource
    // into the plugin's storage folder and hand the absolute path to the renderer.
    private static void ExtractShader()
    {
        try
        {
            var directory = Path.Combine(MyFileSystem.UserDataPath, "Storage", Name);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "AuroraBorealis.hlsl");

            using (var resource = Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream("ClientPlugin.Shaders.AuroraBorealis.hlsl"))
            using (var file = File.Create(path))
            {
                if (resource == null)
                    throw new InvalidOperationException("Embedded shader resource not found");
                resource.CopyTo(file);
            }

            AuroraRenderer.ShaderFilePath = path;
        }
        catch (Exception e)
        {
            MyLog.Default.Error($"{Name}: Failed to extract the shader, the aurora will not render: {e}");
        }
    }

    private static void OnConfigPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Config.ColorPreset):
            case nameof(Config.BottomColor):
            case nameof(Config.TopColor):
                AuroraTextures.MarkRampDirty();
                break;
        }
    }

    // ReSharper disable once UnusedMember.Global
    public void OpenConfigDialog()
    {
        Instance.settingsGenerator.SetLayout<Simple>();
        MyGuiSandbox.AddScreen(Instance.settingsGenerator.Dialog);
    }
}
