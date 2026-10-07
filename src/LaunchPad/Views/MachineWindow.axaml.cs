using LaunchPad.Services;
using LaunchPad.Services.Fence;

namespace LaunchPad.Views;

public partial class MachineWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly string? _projectPath;
    private readonly int[] _gigabytes;

    public MachineWindow(SettingsStore settings, string? projectPath = null, string? projectName = null, IHostResources? resources = null)
    {
        _settings = settings;
        _projectPath = string.IsNullOrWhiteSpace(projectPath) ? null : projectPath;
        InitializeComponent();

        var hostResources = resources ?? HostResources.Current;
        var installed = hostResources.InstalledMemoryMegabytes ?? GuestMemory.DefaultMegabytes + 2048;
        var capMb = Math.Max(2048, installed - 2048);
        var maxGb = Math.Max(2, capMb / 1024);
        _gigabytes = new int[maxGb - 1];
        for (var gb = 2; gb <= maxGb; gb++)
        {
            _gigabytes[gb - 2] = gb;
            MemoryBox.Items.Add(gb + " GB");
        }

        var savedMb = _projectPath is null
            ? settings.Current.MachineMemoryMb
            : settings.ProjectMemoryMbFor(_projectPath);
        if (_projectPath is not null && savedMb < 2048)
            savedMb = settings.Current.MachineMemoryMb;
        var shownMb = GuestMemory.ChooseMegabytes(savedMb, installed);
        var shownGb = Math.Clamp(shownMb / 1024, 2, maxGb);
        MemoryBox.SelectedIndex = shownGb - 2;

        if (_projectPath is null)
        {
            var logical = Math.Max(1, hostResources.LogicalProcessors);
            for (var core = 1; core <= logical; core++)
                CoresBox.Items.Add(core == 1 ? "1 core" : core + " cores");

            var shownCores = GuestMemory.ChooseCores(settings.Current.MachineCores, logical);
            CoresBox.SelectedIndex = shownCores - 1;
            return;
        }

        Title = string.IsNullOrWhiteSpace(projectName) ? "Memory" : projectName;
        HeadingText.Text = "Memory";
        BodyText.Text = "Used the next time this project opens. One that is already running keeps its memory.";
        CoresPanel.IsVisible = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "";
        if (MemoryBox.SelectedIndex < 0)
            return;

        var megabytes = _gigabytes[MemoryBox.SelectedIndex] * 1024;
        if (_projectPath is not null)
        {
            try { _settings.SaveProjectMemory(_projectPath, megabytes); }
            catch (Exception error)
            {
                StatusText.Text = "Memory could not be saved.\n" + error.Message;
                return;
            }
            Close(true);
            return;
        }

        if (CoresBox.SelectedIndex < 0)
            return;

        try { _settings.SavePreferences(_settings.Current.ShowTips, _settings.Current.DefaultAgent, megabytes, CoresBox.SelectedIndex + 1); }
        catch (Exception error)
        {
            StatusText.Text = "VM defaults could not be saved.\n" + error.Message;
            return;
        }
        Close(true);
    }
}
