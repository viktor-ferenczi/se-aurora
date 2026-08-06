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

        // When Pulsar builds the plugin from source it calls LoadAssets with the folder the
        // shader was copied into; that wins. Otherwise (msbuild/IDE build) fall back to the
        // copy embedded into the assembly.
        if (AuroraRenderer.ShaderFilePath == null)
            ExtractEmbeddedShader();

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

    // Called by Pulsar with the folder the plugin's asset files were copied into.
    // The game's shader compiler loads shaders from disk, so the .hlsl file has to be there.
    // ReSharper disable once UnusedMember.Global
    public void LoadAssets(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "AuroraBorealis.hlsl");
            if (File.Exists(path))
                AuroraRenderer.ShaderFilePath = path;
            else
                MyLog.Default.Warning($"{Name}: Shader not found in the asset folder: {path}");
        }
        catch (Exception e)
        {
            MyLog.Default.Error($"{Name}: Failed to load assets from {folder}: {e}");
        }
    }

    // Fallback for msbuild/IDE builds, which embed the shader into the assembly:
    // extract it into the plugin's storage folder and use that absolute path.
    private static void ExtractEmbeddedShader()
    {
        try
        {
            using (var resource = Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream("ClientPlugin.Shaders.AuroraBorealis.hlsl"))
            {
                if (resource == null)
                    return;

                var directory = Path.Combine(MyFileSystem.UserDataPath, "Storage", Name);
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "AuroraBorealis.hlsl");

                using (var file = File.Create(path))
                    resource.CopyTo(file);

                AuroraRenderer.ShaderFilePath = path;
            }
        }
        catch (Exception e)
        {
            MyLog.Default.Error($"{Name}: Failed to extract the embedded shader: {e}");
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
