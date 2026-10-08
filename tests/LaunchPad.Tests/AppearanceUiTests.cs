using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using LaunchPad;
using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Views;
using Xunit;

namespace LaunchPad.Tests;

public sealed class AppearanceUiTests
{
    [Fact]
    public async Task NewThemeResourcesAndTileBindingsLoadWithoutLaunchingAnAppOrVm()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
                var app = Application.Current!;
                Assert.True(app.Resources.TryGetResource("Panel", ThemeVariant.Light, out var light));
                Assert.Equal(Color.Parse("#ECEAE5"), Assert.IsType<SolidColorBrush>(light).Color);
                Assert.True(app.Resources.TryGetResource("Panel", ThemeVariant.Dark, out var dark));
                Assert.Equal(Color.Parse("#1E1E1E"), Assert.IsType<SolidColorBrush>(dark).Color);
                Appearance.Apply(Appearance.Dark);
                Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
                var mark = new SessionMark { TileWidth = 100, IconSize = 48, DataContext = new SessionItem("Kept", "Kept", "owned", IdentityPalette.At(0), true, StatusColors.Green) };
                var window = new Window { Content = mark, Width = 200, Height = 200 };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(100, mark.Width);
                Assert.Equal(56, mark.IconFrameSize);
                var lamp = mark.FindControl<Border>("StatusLamp")!;
                Assert.Equal(StatusColors.Green, Assert.IsType<SolidColorBrush>(lamp.Background).Color);
                Appearance.Apply(Appearance.Light);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(StatusColors.Green, Assert.IsType<SolidColorBrush>(lamp.Background).Color);
                window.Close();
                Appearance.Apply(Appearance.System);
                Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
                finished.SetResult();
            }
            catch (Exception error) { finished.SetException(error); }
        }) { IsBackground = true };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
