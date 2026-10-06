using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;

namespace LaunchPad.Views;

public static class UiDialogs
{
    public static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static Task ShowAsync(string message) => ShowAsync(MainWindow, message);

    public static async Task ShowAsync(Window? owner, string message, string title = "LaunchPad")
    {
        var accept = new Button { Content = "OK", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        accept.Classes.Add("PrimaryButton");
        var body = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        body.Classes.Add("BodyText");
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 20 };
        panel.Children.Add(body);
        panel.Children.Add(accept);
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Content = panel
        };
        accept.Click += (_, _) => dialog.Close();
        if (owner is not null && owner.IsVisible)
            await dialog.ShowDialog(owner);
        else
        {
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Closed += (_, _) => finished.TrySetResult();
            dialog.Show();
            await finished.Task;
        }
    }

    public static async Task CopyAsync(Control control, string text)
    {
        var clipboard = TopLevel.GetTopLevel(control)?.Clipboard
            ?? throw new InvalidOperationException("The clipboard is unavailable.");
        await clipboard.SetTextAsync(text);
    }

    public static Task<T> OnUIAsync<T>(Func<Task<T>> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return action();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { completion.SetResult(await action()); }
            catch (Exception error) { completion.SetException(error); }
        });
        return completion.Task;
    }

    public static Task OnUIAsync(Func<Task> action) => OnUIAsync(async () =>
    {
        await action();
        return true;
    });
}
