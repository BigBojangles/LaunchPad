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

    public AgentWindow(SettingsStore settings, string projectPath, string? projectName)
    {
        _settings = settings;
        _projectPath = projectPath;
        InitializeComponent();
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
        if (!AgentChoice.SafeProgram(name))
        {
            await UiDialogs.ShowAsync(this, "Use a program name made of letters, numbers, dots, dashes, or underscores.");
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
            if (!AgentChoice.SafeProgram(program) || string.IsNullOrWhiteSpace(_source))
            {
                await UiDialogs.ShowAsync(this, "Choose one program file. No installer and no shell.");
                return;
            }

            _settings.SaveAgent(_projectPath, id, program, _source);
        }
        else
        {
            _settings.SaveAgent(_projectPath, id, null, null);
        }

        Close(true);
    }
}
