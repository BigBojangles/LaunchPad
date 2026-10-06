
namespace LaunchPad.Views;

public enum OnboardingPath
{
    AgentBob,
    GrokBuild
}

public partial class OnboardingChoiceDialog : Window
{
    public OnboardingChoiceDialog()
    {
        InitializeComponent();
    }

    public OnboardingPath? SelectedPath { get; private set; }

    private void AgentBob_Click(object sender, RoutedEventArgs e)
    {
        SelectedPath = OnboardingPath.AgentBob;
        Close(true);

    }

    private void GrokBuild_Click(object sender, RoutedEventArgs e)
    {
        SelectedPath = OnboardingPath.GrokBuild;
        Close(true);

    }
}
