using LaunchPad.Services;

namespace LaunchPad.Views;

/// <summary>Startup recovery never replaces unreadable saved records with defaults.</summary>
public sealed class SettingsRecoveryWindow : Window
{
    public SettingsRecoveryWindow(Exception error, Func<bool> retry, Action openFolder)
    {
        Title = "LaunchPad — saved settings need attention";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var message = new TextBlock { Name = "RecoveryMessage", Text = "Your saved settings could not be opened. The files were preserved. Repair or restore them, then retry.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var detail = new TextBlock { Text = error.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 };
        var retryButton = new Button { Name = "RetryButton", Content = "Retry", IsDefault = true };
        retryButton.Classes.Add("PrimaryButton");
        retryButton.Click += (_, _) => { if (retry()) Close(); };
        var folder = new Button { Name = "OpenSettingsButton", Content = "Open settings folder" };
        folder.Click += async (_, _) =>
        {
            try { openFolder(); }
            catch (Exception failure) { await UiDialogs.ShowAsync(this, failure.Message); }
        };
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(folder); actions.Children.Add(retryButton); actions.Children.Add(close);
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 18 };
        panel.Children.Add(message); panel.Children.Add(detail); panel.Children.Add(actions);
        Content = panel;
    }
}
