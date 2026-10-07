using LaunchPad.Services;

namespace LaunchPad.Views;

public partial class OnboardingWizard : Window
{
    public const string StarterPrompt =
        "i want to make a very small personal daily checklist app. one simple window. i can type a new item and add it. i can check items off when im done. i can clear the ones that are finished. nothing else for the first version. no saving between restarts yet. keep it really small so it can be built in one go.";

    private readonly string _projectsRoot;
    private readonly Func<string, bool, OnboardingProjectStatus> _ensureProject;
    private readonly Func<Window, string, bool, Task<bool>>? _openProject;
    private bool _busy;
    private int _step;

    public OnboardingWizard(string projectsRoot, Func<string, bool, OnboardingProjectStatus> ensureProject,
        Func<Window, string, bool, Task<bool>>? openProject = null, bool nativeOnly = false)
    {
        _projectsRoot = projectsRoot;
        _ensureProject = ensureProject;
        _openProject = openProject;
        InitializeComponent();
        InstructionsPreview.Text = AgentBobInstructions.Load();
        InstructionCount.Text = $"Full instructions: {InstructionsPreview.Text.Length:N0} characters. Nothing is shortened.";
        CompanionBox.ItemsSource = CompanionChoice.All;
        CompanionBox.SelectedIndex = 0;
        if (nativeOnly)
            RunModeHint.Text = "This package runs installed Windows agents directly in your project folder using your account permissions. It has no VM runtime.";
        ShowStep(0);
    }

    public bool Completed { get; private set; }

    private void ShowStep(int step)
    {
        _step = step;
        StepWelcome.IsVisible = step == 0;
        StepAgentBob.IsVisible = step == 1;
        StepCreateProject.IsVisible = step == 2;
        StepNext.IsVisible = step == 3;
        BackButton.IsVisible = step > 0;
        StepLabel.Text = $"Step {step + 1} of 4";
        PrimaryButton.Content = step switch
        {
            0 => "Get started",
            2 => "Create project & continue",
            3 => "Done",
            _ => "Continue"
        };

        if (step == 2)
            UpdateProjectPreview();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
            ShowStep(_step - 1);
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_step == 2)
        {
            if (!await PrepareProjectAsync(reuseExisting: false)) return;
        }

        if (_step >= 3)
        {
            Completed = true;
            Close(true);

            return;
        }

        ShowStep(_step + 1);
    }

    private async void UseExisting_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy && await PrepareProjectAsync(reuseExisting: true)) ShowStep(3);
    }

    private async Task<bool> PrepareProjectAsync(bool reuseExisting)
    {
        _busy = true;
        PrimaryButton.IsEnabled = BackButton.IsEnabled = UseExistingButton.IsEnabled = false;
        try
        {
            var status = _ensureProject(ProjectNameBox.Text ?? "", reuseExisting);
            if (status.Result != OnboardingProjectStatus.Kind.Ready)
            {
                ProjectError.Text = status.Message;
                UseExistingButton.IsVisible = status.Result == OnboardingProjectStatus.Kind.Exists;
                return false;
            }
            if (_openProject is not null && !await _openProject(this, status.Path!, reuseExisting))
            {
                ProjectError.Text = "The folder is saved. Use this project to choose an agent and open it when you are ready.";
                UseExistingButton.IsVisible = true;
                return false;
            }
            return true;
        }
        catch (Exception error)
        {
            ProjectError.Text = error.Message;
            UseExistingButton.IsVisible = true;
            return false;
        }
        finally
        {
            _busy = false;
            PrimaryButton.IsEnabled = BackButton.IsEnabled = UseExistingButton.IsEnabled = true;
        }
    }

    private async void CopyInstructions_Click(object sender, RoutedEventArgs e)
    {
        var text = AgentBobInstructions.Load();
        if (string.IsNullOrWhiteSpace(text))
        {
            await UiDialogs.ShowAsync(this, "Couldn’t load the Agent Bob instructions. Try again.");
            return;
        }

        try
        {
            await UiDialogs.CopyAsync(this, text);
            CopyLabel.Text = "Full instructions copied";
        }
        catch (Exception)
        {
            await UiDialogs.ShowAsync(this, "Could not copy the instructions. You can select and copy them from the full instructions preview.");
        }
    }

    private async void CopyStarter_Click(object sender, RoutedEventArgs e)
    {
        await UiDialogs.CopyAsync(this, StarterPrompt);
        CopyStarterButton.Content = "Copied";
    }

    private void Companion_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (OpenCompanionButton is null) return;
        var choice = CompanionBox.SelectedItem as CompanionChoice;
        OpenCompanionButton.IsEnabled = choice?.ChatUrl is not null;
        OpenCompanionLabel.Text = choice?.ChatUrl is not null ? "Open " + choice.Name : "Open your preferred chat yourself";
        CompanionAddress.Text = choice?.ChatUrl ?? "Copy the full instructions and paste them into your preferred chat.";
        CompanionHelpButton.IsVisible = choice?.InstructionsUrl is not null;
    }

    private async void OpenCompanion_Click(object sender, RoutedEventArgs e)
    {
        if (CompanionBox.SelectedItem is CompanionChoice { ChatUrl: not null } choice)
            await OpenLinkAsync(choice.ChatUrl);
    }

    private async void OpenCompanionHelp_Click(object sender, RoutedEventArgs e)
    {
        if (CompanionBox.SelectedItem is CompanionChoice { InstructionsUrl: not null } choice)
            await OpenLinkAsync(choice.InstructionsUrl);
    }

    private async Task OpenLinkAsync(string url)
    {
        try { ExternalLinks.Open(url); }
        catch (Exception) { await UiDialogs.ShowAsync(this, "Could not open your browser. Open this address yourself: " + url); }
    }

    private void OpenGuide_Click(object sender, RoutedEventArgs e) => ExternalLinks.Open(ExternalLinks.Guide);

    private void ProjectName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (UseExistingButton is not null)
            UseExistingButton.IsVisible = false;
        UpdateProjectPreview();
    }

    private void UpdateProjectPreview()
    {
        if (ProjectNameBox is null || ProjectPathPreview is null || ProjectError is null)
            return;

        ProjectError.Text = "";
        if (ProjectNames.TrySanitize(ProjectNameBox.Text, out var name, out _))
        {
            var folder = Path.Combine(_projectsRoot, name);
            ProjectPathPreview.Text = Directory.Exists(folder)
                ? "This folder already exists: " + folder
                : "It will be created here: " + folder;
        }
        else
        {
            ProjectPathPreview.Text = "It will be created in: " + _projectsRoot;
        }
    }
}
