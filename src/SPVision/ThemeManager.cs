using System.Windows;
using Microsoft.Win32;
using SPVision.Core;

namespace SPVision;

/// <summary>
/// Applies the light or dark theme dictionary (Themes/Light.xaml, Themes/Dark.xaml) at run time.
/// The theme dictionary is the first merged dictionary of the application; views use DynamicResource,
/// so swapping it restyles every open window. In System mode it follows Windows' "app mode" live
/// (HKCU\...\Themes\Personalize\AppsUseLightTheme, re-read on SystemEvents.UserPreferenceChanged).
/// </summary>
static class ThemeManager
{
    static bool _subscribed;
    static ResourceDictionary? _current;

    public static EffectiveTheme Current { get; private set; } = EffectiveTheme.Dark;

    /// <summary>Raised on the UI thread after the theme dictionary changed (code-drawn controls repaint).</summary>
    public static event Action? ThemeChanged;

    /// <summary>Call once at startup, before the first window is created.</summary>
    public static void Initialize()
    {
        if (!_subscribed)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _subscribed = true;
        }
        Apply(force: true);
    }

    /// <summary>Call on exit: SystemEvents holds static references to its handlers.</summary>
    public static void Shutdown()
    {
        if (_subscribed)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _subscribed = false;
        }
    }

    /// <summary>Re-evaluates <see cref="AppSettings.Theme"/> (and Windows in System mode) and applies the result.</summary>
    public static void Apply(bool force = false)
    {
        var effective = ThemeResolver.Resolve(App.Settings.Theme, ReadSystemUsesLight());
        if (!force && effective == Current && _current is not null)
        {
            return;
        }
        Current = effective;

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var next = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/SPVision;component/Themes/{(effective == EffectiveTheme.Light ? "Light" : "Dark")}.xaml",
                UriKind.Absolute),
        };
        var index = _current is null ? IndexOfTheme(dictionaries) : dictionaries.IndexOf(_current);
        if (index >= 0)
        {
            dictionaries[index] = next;
        }
        else
        {
            dictionaries.Insert(0, next);
        }
        _current = next;

        DarkTitleBar.SetDark(effective == EffectiveTheme.Dark);
        ThemeChanged?.Invoke();
    }

    /// <summary>Windows' AppsUseLightTheme; null when the value is missing or unreadable.</summary>
    static bool? ReadSystemUsesLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ThemeResolver.PersonalizeKey);
            return ThemeResolver.SystemUsesLight(key?.GetValue(ThemeResolver.AppsUseLightThemeValue));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }

    static int IndexOfTheme(IList<ResourceDictionary> dictionaries)
    {
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString ?? "";
            if (source.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Windows broadcasts the light/dark switch as "ImmersiveColorSet" (category General); other
        // categories are cheap to re-check. The event may arrive on a SystemEvents thread.
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
        {
            return;
        }
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (App.Settings.Theme == AppTheme.System)
            {
                Apply();
            }
        });
    }
}

static class ThemeResourceExtensions
{
    /// <summary>Sets a theme brush as a resource reference (follows theme switches) and returns the element.</summary>
    public static T WithBrush<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }
}
