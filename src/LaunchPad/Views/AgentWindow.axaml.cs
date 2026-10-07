using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Avalonia.Platform.Storage;

namespace LaunchPad.Views;

public partial class AgentWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly string _projectPath;
    private string? _source;
    private readonly List<AgentOption> _choices = new();

    public AgentWindow(SettingsStore settings, string projectPath, string? projectName, bool nativeOnly = false)
    {
        _settings = settings;
        _projectPath = projectPath;
        InitializeComponent();
        LaunchModeBox.ItemsSource = new[] { "Fenced VM", "Native (no sandbox)" };
        LaunchModeBox.SelectedIndex = nativeOnly || settings.LaunchModeFor(projectPath) == "native" ? 1 : 0;
        LaunchModeBox.IsEnabled = !nativeOnly;
        if (!string.IsNullOrWhiteSpace(projectName))
            Title = projectName;

        foreach (var option in AgentChoice.Options)
        {
            if (!option.Enabled)
                continue;
            _choices.Add(option);
            AgentBox.Items.Add(option.Label);
        }

        var current = settings.AgentFor(projectPath);
        var pick = AgentChoice.IsEnabled(current.Id) ? current.Id : AgentChoice.Grok;
        var index = _choices.FindIndex(o => string.Equals(o.Id, pick, StringComparison.Ordinal));
        AgentBox.SelectedIndex = index < 0 ? 0 : index;
        _source = current.SourceFile;
        if (!string.IsNullOrWhiteSpace(current.Program))
            FileText.Text = current.Program;

        SyncCustomPanel();
    }

    private void AgentBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncCustomPanel();

    private void SyncCustomPanel()
    {
        if (CustomPanel is null || AgentBox.SelectedIndex < 0 || AgentBox.SelectedIndex >= _choices.Count)
            return;
        CustomPanel.IsVisible = _choices[AgentBox.SelectedIndex].Id == AgentChoice.Custom;
    }

    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "Program file", AllowMultiple = false });
        var path = selected.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        var name = System.IO.Path.GetFileName(path);
        if (LaunchModeBox.SelectedIndex == 1 ? !NativeAgentLocator.Supported(path) : !AgentChoice.SafeProgram(name))
        {
            await UiDialogs.ShowAsync(this, LaunchModeBox.SelectedIndex == 1
                ? "Choose a Windows executable (.exe) or command file (.cmd or .bat)."
                : "Use a program name made of letters, numbers, dots, dashes, or underscores.");
            return;
        }

        _source = path;
        FileText.Text = name;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (AgentBox.SelectedIndex < 0 || AgentBox.SelectedIndex >= _choices.Count)
            return;

        var chosen = _choices[AgentBox.SelectedIndex];
        if (!chosen.Enabled)
        {
            await UiDialogs.ShowAsync(this, chosen.DisabledReason ?? "That agent is not available yet.");
            return;
        }

        var id = chosen.Id;
        if (id == AgentChoice.Custom)
        {
            var program = System.IO.Path.GetFileName(_source ?? FileText.Text);
            if (string.IsNullOrWhiteSpace(_source) || (LaunchModeBox.SelectedIndex == 1 ? !NativeAgentLocator.Supported(_source) : !AgentChoice.SafeProgram(program)))
            {
                await UiDialogs.ShowAsync(this, "Choose one program file. No installer and no shell.");
                return;
            }

            if (!await SaveSelectionAsync(id, program, _source)) return;
        }
        else
        {
            if (!await SaveSelectionAsync(id, null, null)) return;
        }

        Close(true);
    }
    private async Task<bool> SaveSelectionAsync(string id, string? program, string? source)
    {
        try
        {
            _settings.SaveAgent(_projectPath, id, program, source, LaunchModeBox.SelectedIndex == 1 ? "native" : "fenced");
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await UiDialogs.ShowAsync(this, error.Message);
            return false;
        }
    }
}
