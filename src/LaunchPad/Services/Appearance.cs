using Avalonia.Styling;

namespace LaunchPad.Services;

public static class Appearance
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";
    public static bool IsValid(string value) => value is System or Light or Dark;
    public static void Apply(string? value)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = value switch
            {
                Light => ThemeVariant.Light,
                Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
    }
}
