using LaunchPad.Services;
using LaunchPad.Services.Fence;

namespace LaunchPad.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly AgentOption[] _agents;
    private bool _resetTips;
    public SettingsWindow(SettingsStore settings, IHostResources? resources = null)
    {
        _settings = settings;
        InitializeComponent();
        var host = resources ?? HostResources.Current;
        _agents = AgentChoice.Options.Where(agent => agent.Id != AgentChoice.Custom && agent.Enabled).ToArray();
        DefaultAgentBox.ItemsSource = _agents.Select(agent => agent.Label).ToArray();
        DefaultAgentBox.SelectedIndex = Math.Max(0, Array.FindIndex(_agents, agent => agent.Id == settings.Current.DefaultAgent));
        TipsSwitch.IsChecked = settings.Current.ShowTips;
        var installed = host.InstalledMemoryMegabytes ?? GuestMemory.DefaultMegabytes + 2048;
        var maxGb = Math.Max(2, (installed - 2048) / 1024);
        MemoryBox.ItemsSource = Enumerable.Range(2, maxGb - 1).Select(gb => gb + " GB").ToArray();
        MemoryBox.SelectedIndex = Math.Clamp(GuestMemory.ChooseMegabytes(settings.Current.MachineMemoryMb, installed) / 1024, 2, maxGb) - 2;
        var logical = Math.Max(1, host.LogicalProcessors);
        CoresBox.ItemsSource = Enumerable.Range(1, logical).Select(count => count == 1 ? "1 core" : count + " cores").ToArray();
        CoresBox.SelectedIndex = GuestMemory.ChooseCores(settings.Current.MachineCores, logical) - 1;
    }
    private void ResetTips_Click(object? sender, RoutedEventArgs e) { _resetTips = true; StatusText.Text = "Tips will appear again after saving."; }
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DefaultAgentBox.SelectedIndex < 0 || MemoryBox.SelectedIndex < 0 || CoresBox.SelectedIndex < 0) throw new ArgumentException("Choose the default agent, memory and CPU count.");
            _settings.SavePreferences(TipsSwitch.IsChecked == true, _agents[DefaultAgentBox.SelectedIndex].Id,
                (MemoryBox.SelectedIndex + 2) * 1024, CoresBox.SelectedIndex + 1, resetTips: _resetTips);
            Close(true);
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }
}
