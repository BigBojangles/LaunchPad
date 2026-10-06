using LaunchPad.Services;

namespace LaunchPad.Views;

public partial class OnboardingWizard : Window
{
    public const string StarterPrompt =
        "i want to make a very small personal daily checklist app. one simple window. i can type a new item and add it. i can check items off when im done. i can clear the ones that are finished. nothing else for the first version. no saving between restarts yet. keep it really small so it can be built in one go.";

    private readonly string _projectsRoot;
    private readonly Func<string, bool, OnboardingProjectStatus> _ensureProject;
    private int _step;

    public OnboardingWizard(string projectsRoot, Func<string, bool, OnboardingProjectStatus> ensureProject)
    {
        _projectsRoot = projectsRoot;
        _ensureProject = ensureProject;
        InitializeComponent();
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

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 2)
        {
            var status = _ensureProject(ProjectNameBox.Text ?? "", false);
            if (status.Result == OnboardingProjectStatus.Kind.Exists)
            {
                ProjectError.Text = status.Message;
                UseExistingButton.IsVisible = true;
                return;
            }

            if (status.Result != OnboardingProjectStatus.Kind.Ready)
            {
                ProjectError.Text = status.Message;
                UseExistingButton.IsVisible = false;
                return;
            }
        }

        if (_step >= 3)
        {
            Completed = true;
            Close(true);

            return;
        }

        ShowStep(_step + 1);
    }

    private void UseExisting_Click(object sender, RoutedEventArgs e)
    {
        var status = _ensureProject(ProjectNameBox.Text ?? "", true);
        if (status.Result != OnboardingProjectStatus.Kind.Ready)
        {
            ProjectError.Text = status.Message;
            return;
        }

        ShowStep(3);
    }

    private async void CopyInstructions_Click(object sender, RoutedEventArgs e)
    {
        var text = AgentBobInstructions.Load();
        if (string.IsNullOrWhiteSpace(text))
        {
            await UiDialogs.ShowAsync(this, "Couldn’t load the Agent Bob instructions. Try again.");
            return;
        }

        await UiDialogs.CopyAsync(this, text);
        CopyLabel.Text = "Copied";
    }

    private async void CopyStarter_Click(object sender, RoutedEventArgs e)
    {
        await UiDialogs.CopyAsync(this, StarterPrompt);
        CopyStarterButton.Content = "Copied";
    }

    private void OpenGrok_Click(object sender, RoutedEventArgs e) => ExternalLinks.Open(ExternalLinks.GrokChat);

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
