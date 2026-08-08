using ClientPlugin.Settings;
using ClientPlugin.Settings.Elements;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VRageMath;


namespace ClientPlugin;

public enum AuroraQuality
{
    Low,
    Medium,
    High,
}

public enum AuroraColorPreset
{
    GreenPurple,
    Green,
    RedPurple,
    BlueTeal,
    Custom,
}

public class Config : INotifyPropertyChanged
{
    #region Options

    private bool enabled = true;
    private float intensity = 0.25f;
    private AuroraQuality quality = AuroraQuality.High;
    private AuroraColorPreset colorPreset = AuroraColorPreset.GreenPurple;
    private Color bottomColor = new Color(30, 238, 221);
    private Color topColor = new Color(140, 50, 210);
    private float latitudeCenter = 64f;
    private float latitudeWidth = 24f;
    private float altitudeMin = 0.1f;
    private float altitudeMax = 0.4f;
    private float patternDensity = 1.0f;
    private float fadeStartFactor = 12f;
    private float fadeEndFactor = 16f;
    private float animationSpeed = 4.0f;
    private bool nightOnly = true;

    #endregion

    #region User interface

    public readonly string Title = "Aurora";

    [Separator("Aurora Borealis")]

    [Checkbox(description: "Master switch for the aurora effect")]
    public bool Enabled
    {
        get => enabled;
        set => SetField(ref enabled, value);
    }

    [Slider(0f, 1f, 0.01f, SliderAttribute.SliderType.Float, description: "HDR brightness multiplier of the aurora")]
    public float Intensity
    {
        get => intensity;
        set => SetField(ref intensity, value);
    }

    [Dropdown(description: "Raymarching quality (number of volume samples per pixel)")]
    public AuroraQuality Quality
    {
        get => quality;
        set => SetField(ref quality, value);
    }

    [Separator("Colors")]

    [Dropdown(description: "Color scheme of the vertical gradient; select Custom to use the colors below")]
    public AuroraColorPreset ColorPreset
    {
        get => colorPreset;
        set => SetField(ref colorPreset, value);
    }

    [Color(description: "Color of the bright lower edge of the curtains (Custom preset)")]
    public Color BottomColor
    {
        get => bottomColor;
        set => SetField(ref bottomColor, value);
    }

    [Color(description: "Color of the fading upper tail of the curtains (Custom preset)")]
    public Color TopColor
    {
        get => topColor;
        set => SetField(ref topColor, value);
    }

    [Separator("Placement")]

    [Slider(45f, 85f, 1f, SliderAttribute.SliderType.Float, description: "Latitude of the center of the aurora band (degrees, both hemispheres)")]
    public float LatitudeCenter
    {
        get => latitudeCenter;
        set => SetField(ref latitudeCenter, value);
    }

    [Slider(4f, 48f, 1f, SliderAttribute.SliderType.Float, description: "Width of the aurora band (degrees of latitude)")]
    public float LatitudeWidth
    {
        get => latitudeWidth;
        set => SetField(ref latitudeWidth, value);
    }

    [Slider(0f, 1.0f, 0.01f, SliderAttribute.SliderType.Float, description: "Bottom of the aurora shell (0 = surface, 1 = top of atmosphere)")]
    public float AltitudeMin
    {
        get => altitudeMin;
        set => SetField(ref altitudeMin, value);
    }

    [Slider(0.05f, 1.0f, 0.01f, SliderAttribute.SliderType.Float, description: "Top of the aurora shell (0 = surface, 1 = top of atmosphere)")]
    public float AltitudeMax
    {
        get => altitudeMax;
        set => SetField(ref altitudeMax, value);
    }

    [Slider(0.25f, 4f, 0.05f, SliderAttribute.SliderType.Float, description: "How many curtains fit across the polar cap; lower is sparser with larger structures")]
    public float PatternDensity
    {
        get => patternDensity;
        set => SetField(ref patternDensity, value);
    }

    [Slider(1f, 100f, 0.1f, SliderAttribute.SliderType.Float, description: "Distance from the planet where the aurora starts to fade out (multiple of the atmosphere radius)")]
    public float FadeStartFactor
    {
        get => fadeStartFactor;
        set => SetField(ref fadeStartFactor, value);
    }

    [Slider(1f, 100f, 0.1f, SliderAttribute.SliderType.Float, description: "Distance from the planet where the aurora becomes fully invisible (multiple of the atmosphere radius)")]
    public float FadeEndFactor
    {
        get => fadeEndFactor;
        set => SetField(ref fadeEndFactor, value);
    }

    [Separator("Animation")]

    [Slider(0f, 12f, 0.1f, SliderAttribute.SliderType.Float, description: "Speed of the curtain movement")]
    public float AnimationSpeed
    {
        get => animationSpeed;
        set => SetField(ref animationSpeed, value);
    }

    [Checkbox(description: "Show the aurora only on the night side of the planet")]
    public bool NightOnly
    {
        get => nightOnly;
        set => SetField(ref nightOnly, value);
    }

    #endregion

    #region Derived values

    public int StepCount
    {
        get
        {
            switch (quality)
            {
                case AuroraQuality.Low:
                    return 24;
                case AuroraQuality.High:
                    return 96;
                default:
                    return 48;
            }
        }
    }

    public void GetGradientColors(out Vector3 bottom, out Vector3 top)
    {
        switch (colorPreset)
        {
            case AuroraColorPreset.Green:
                bottom = new Vector3(0.12f, 1f, 0.3f);
                top = new Vector3(0f, 0.6f, 0.4f);
                break;
            case AuroraColorPreset.RedPurple:
                bottom = new Vector3(1f, 0.25f, 0.3f);
                top = new Vector3(0.6f, 0.1f, 0.8f);
                break;
            case AuroraColorPreset.BlueTeal:
                bottom = new Vector3(0.15f, 0.55f, 1f);
                top = new Vector3(0.1f, 0.9f, 0.8f);
                break;
            case AuroraColorPreset.Custom:
                bottom = bottomColor.ToVector3();
                top = topColor.ToVector3();
                break;
            default:
                bottom = new Vector3(0.12f, 1f, 0.3f);
                top = new Vector3(0.55f, 0.2f, 0.82f);
                break;
        }
    }

    #endregion

    #region Property change notification boilerplate

    public static readonly Config Default = new Config();
    public static readonly Config Current = ConfigStorage.Load();

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion
}
