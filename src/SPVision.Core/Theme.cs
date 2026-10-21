namespace SPVision.Core;

/// <summary>Theme the user picked in Settings. <see cref="System"/> follows the Windows app theme live.</summary>
public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>The theme actually shown (System resolved against Windows).</summary>
public enum EffectiveTheme
{
    Light,
    Dark,
}

public static class ThemeResolver
{
    /// <summary>Registry value under HKCU that tells whether Windows apps use the light theme (1) or dark (0).</summary>
    public const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public const string AppsUseLightThemeValue = "AppsUseLightTheme";

    /// <summary>
    /// setting: the user's choice; systemUsesLight: Windows' AppsUseLightTheme, null when it could not be read.
    /// Windows without the value (older builds) uses the light theme, so null resolves to light.
    /// </summary>
    public static EffectiveTheme Resolve(AppTheme setting, bool? systemUsesLight) => setting switch
    {
        AppTheme.Light => EffectiveTheme.Light,
        AppTheme.Dark => EffectiveTheme.Dark,
        _ => systemUsesLight == false ? EffectiveTheme.Dark : EffectiveTheme.Light,
    };

    /// <summary>Interprets the raw AppsUseLightTheme registry value: 1 = light, 0 = dark, anything else = unknown.</summary>
    public static bool? SystemUsesLight(object? registryValue) => registryValue switch
    {
        int i when i == 1 => true,
        int i when i == 0 => false,
        long l when l == 1 => true,
        long l when l == 0 => false,
        _ => null,
    };
}
