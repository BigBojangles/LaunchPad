using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace LaunchPad.Views;

public partial class ExistingProjectsView : UserControl
{
    private readonly AppServices _services;
    private readonly Action<string> _openProject;
    private readonly Func<string, Task> _openUnfenced;
    private readonly Func<Task> _newProject;
    private readonly DispatcherTimer _tipTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private TopLevel? _sizingHost;

    public ExistingProjectsView(AppServices services, Action<string> openProject, Func<string, Task> openUnfenced, Func<Task> newProject)
    {
        _services = services;
        _openProject = openProject;
        _openUnfenced = openUnfenced;
        _newProject = newProject;
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (_sizingHost is { } previous) previous.SizeChanged -= Top_SizeChanged;
            if (TopLevel.GetTopLevel(this) is { } top)
            {
                _sizingHost = top;
                top.SizeChanged += Top_SizeChanged;
                UpdateListHeight(top);
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_sizingHost is { } top) top.SizeChanged -= Top_SizeChanged;
            _sizingHost = null;
        };
        _tipTimer.Tick += (_, _) => { TipText.IsVisible = false; _tipTimer.Stop(); };
        Loaded += (_, _) => { _services.Settings.PreferencesChanged += RefreshPreferences; ShowTip(); };
        Unloaded += (_, _) => { _services.Settings.PreferencesChanged -= RefreshPreferences; _tipTimer.Stop(); };
        Reload();
    }

    private void Top_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is TopLevel top) UpdateListHeight(top);
    }
    private void UpdateListHeight(TopLevel top)
        => MaxHeight = Math.Max(120, top.Bounds.Height - 150);

    private void ShowTip()
    {
        if (!_services.Settings.Current.ShowTips) { TipText.IsVisible = false; _tipTimer.Stop(); return; }
        try { if (_services.Settings.TakeTip("project-menu")) { TipText.IsVisible = true; _tipTimer.Start(); } }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { TipText.IsVisible = false; _tipTimer.Stop(); _services.Log.Write("Tip could not be saved: " + error.Message); }
    }
    private void RefreshPreferences()
    {
        ShowTip();
        foreach (var row in this.GetVisualDescendants().OfType<Button>().Where(button => button.DataContext is ProjectEntry))
            if (row.ContextMenu is not null && row.DataContext is ProjectEntry project) row.ContextMenu = CreateProjectMenu(project.Path);
    }

    private void ProjectName_Saved(object? sender, DisplayNameSavedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ProjectEntry project) return;
        if (e.Reset) _services.Settings.ResetDisplayName(project.Path);
        else _services.Settings.SaveDisplayName(project.Path, e.Name);
        Reload();
    }

    private void ProjectRow_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProjectEntry entry } row) row.ContextMenu = CreateProjectMenu(entry.Path);
    }

    public ContextMenu CreateProjectMenu(string path)
    {
        var entry = new ProjectEntry { Path = path, Name = _services.Settings.DisplayNameFor(path) };
        var menu = new ContextMenu { DataContext = entry };
        menu.Classes.Add("BubbleMenu");
        MenuItem Action(string caption, Action<object, RoutedEventArgs> run)
        {
            var item = new MenuItem { Header = caption, Tag = path, DataContext = entry };
            item.Click += (sender, e) => run(sender!, e);
            return item;
        }
        menu.Items.Add(Action("Open project", Project_Click));
        menu.Items.Add(Action("Open folder", OpenFolder_Click));
        var recovery = Action("Saved VM work", Recovery_Click);
        recovery.IsEnabled = !_services.Runtime.NativeOnly;
        menu.Items.Add(recovery);
        var files = Action("Send files to VM", Files_Click);
        files.IsEnabled = !_services.Runtime.NativeOnly && _services.Settings.LaunchModeFor(path) != "native";
        menu.Items.Add(files);
        menu.Items.Add(Action("Agent", Agent_Click));
        var more = new MenuItem { Header = "More…" };
        var memory = Action("VM memory", Memory_Click);
        memory.IsEnabled = !_services.Runtime.NativeOnly && _services.Settings.LaunchModeFor(path) != "native";
        more.Items.Add(memory);
        more.Items.Add(Action("Project permissions", Permissions_Click));
        var windowsTest = Action("Windows tests", WindowsTest_Click);
        windowsTest.IsEnabled = OperatingSystem.IsWindows();
        more.Items.Add(windowsTest);
        if (_services.Settings.Current.NotificationsEnabled && Guid.TryParseExact(_services.Settings.Current.NotificationDestination, "N", out _))
            more.Items.Add(Action(_services.Settings.NotificationConsentFor(path).ProjectEnabled ? "Notifications: On" : "Notifications: Off", async (_, _) =>
            {
                try { _services.Settings.SaveProjectNotifications(path, !_services.Settings.NotificationConsentFor(path).ProjectEnabled); }
                catch { await UiDialogs.ShowAsync((Window?)TopLevel.GetTopLevel(this), "Project alert preference could not be saved. The previous choice was preserved."); }
            }));
        more.Items.Add(Action("Rename project", Rename_Click));
        more.Items.Add(Action("Reset to folder name", async (_, e) =>
        {
            e.Handled = true;
            try { _services.Settings.ResetDisplayName(path); }
            catch (Exception error)
            {
                await UiDialogs.ShowAsync((Window?)TopLevel.GetTopLevel(this),
                    "The display name could not be reset. Saved project records were preserved.\n\n" + error.Message);
                return;
            }
            Reload();
        }));
        var unfenced = Action("Open native (no sandbox)", OpenUnfenced_Click);
        more.Items.Add(unfenced);
        menu.Items.Add(more);
        return menu;
    }

    public void Reload()
    {
        var projects = _services.Catalog.ListProjects();
        ProjectList.ItemsSource = projects;
        EmptyText.IsVisible = projects.Count == 0;
        ProjectList.IsVisible = !(projects.Count == 0);
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        await _newProject();
        Reload();
    }

    private async void Project_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (PathOf(sender) is not string path || !Directory.Exists(path))
        {
            await UiDialogs.ShowAsync((Window?)TopLevel.GetTopLevel(this), "That project folder is no longer there.");
            Reload();
            return;
        }

        _openProject(path);
    }

    // Bubbles and their menu items carry the project path in Tag. A menu item falls back to its row.
    private static string? PathOf(object sender) =>
        sender is Control { Tag: string tag } ? tag
        : (sender as Control)?.DataContext is ProjectEntry entry ? entry.Path
        : null;

    private void RowMenu_Opening(object sender, ContextRequestedEventArgs e)
    {
        // The row actions live in this menu now, so it always opens.
        // Open unfenced still only shows when FenceReady.Installed() is true, same as before.
        if (sender is not Control { ContextMenu: { } menu })
            return;

        menu.DataContext = ((Control)sender).DataContext;
        var fenced = _services.Runtime.HasVirtualMachine;
        foreach (var item in menu.Items.OfType<Control>())
        {
            if (item.Name is "UnfencedItem" or "UnfencedSeparator")
                item.IsVisible = fenced;
        }
    }

    private async void OpenUnfenced_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var entry = (sender as Control)?.DataContext as ProjectEntry;
        if (entry is null && sender is MenuItem { Parent: ContextMenu menu })
            entry = menu.DataContext as ProjectEntry;

        if (entry is null || !Directory.Exists(entry.Path))
        {
            await UiDialogs.ShowAsync((Window?)TopLevel.GetTopLevel(this), "That project folder is no longer there.");
            Reload();
            return;
        }

        _ = _openUnfenced(entry.Path);
    }

    private async void Agent_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (PathOf(sender) is not string path || !Directory.Exists(path))
        {
            await UiDialogs.ShowAsync((Window?)TopLevel.GetTopLevel(this), "That project folder is no longer there.");
            Reload();
            return;
        }

        var name = _services.Settings.DisplayNameFor(path);
        var dialog = new AgentWindow(_services.Settings, path, name, _services.Runtime.NativeOnly)
        {
            Title = name
        };
        await dialog.ShowDialog((Window)TopLevel.GetTopLevel(this)!);
    }

    private async void WindowsTest_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (PathOf(sender) is not string path || !Directory.Exists(path)) return;
        await new ManagedWindowsTestsWindow(_services, path).ShowDialog((Window)TopLevel.GetTopLevel(this)!);
    }

    private async void Files_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (PathOf(sender) is not string path)
            return;

        string? message;
        try
        {
            message = await _services.Runtime.SendProjectAsync(path, CancellationToken.None);
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }

        if (!string.IsNullOrWhiteSpace(message))
            await UiDialogs.ShowAsync((Window?)TopLevel.GetTopLevel(this), message);
    }

    private async void Memory_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (PathOf(sender) is not string path || !Directory.Exists(path))
        {
            await UiDialogs.ShowAsync((Window?)TopLevel.GetTopLevel(this), "That project folder is no longer there.");
            Reload();
            return;
        }

        var name = _services.Settings.DisplayNameFor(path);
        var dialog = new MachineWindow(_services.Settings, path, name, _services.Resources)
        {
            Title = name
        };
        await dialog.ShowDialog((Window)TopLevel.GetTopLevel(this)!);
    }

    private async void Permissions_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (PathOf(sender) is not string path) return;
        await new ProjectPermissionsWindow(_services, path).ShowDialog((Window)TopLevel.GetTopLevel(this)!);
    }

    private void Rename_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Control control || PathOf(control) is not string path) return;
        this.GetVisualDescendants().OfType<EditableDisplayName>()
            .FirstOrDefault(editor => editor.DataContext is ProjectEntry entry && entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase))?.BeginEdit();
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (PathOf(sender) is not string path || !Directory.Exists(path))
        {
            await UiDialogs.ShowAsync((Window?)TopLevel.GetTopLevel(this), "That project folder is no longer there.");
            Reload();
            return;
        }

        try
        {
            _services.Desktop.OpenProjectFolder(path);
        }
        catch
        {
            await UiDialogs.ShowAsync((Window?)TopLevel.GetTopLevel(this), "Couldn’t open that folder.");
        }
    }

    private async void Recovery_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (PathOf(sender) is not string path || TopLevel.GetTopLevel(this) is not Window owner) return;
        try { await new FenceDialog(_services, path).ShowDialog(owner); }
        catch (Exception error) { await UiDialogs.ShowAsync(owner, error.Message); }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var selected = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "Add a project folder", AllowMultiple = false });
        var path = selected.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!Directory.Exists(path))
            return;

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
            name = path;

        try { ProjectRow.AddFolder(_services.Settings, name, path); }
        catch (Exception error)
        {
            await UiDialogs.ShowAsync((Window?)top,
                "The folder could not be added. Saved project records were preserved.\n\n" + error.Message);
            return;
        }
        Reload();
    }

    private void BubbleMenu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Control control)
            return;
        var bubble = control.GetVisualAncestors().OfType<Button>().FirstOrDefault(b => b.ContextMenu is not null);
        if (bubble is null) return;
        if (bubble.ContextMenu is null)
            return;
        bubble.ContextMenu.DataContext = bubble.DataContext;
        foreach (var item in bubble.ContextMenu.Items.OfType<Control>())
            if (item.Name is "UnfencedItem" or "UnfencedSeparator") item.IsVisible = _services.Runtime.HasVirtualMachine;
        bubble.ContextMenu.Open(bubble);
    }
}
