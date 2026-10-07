
namespace LaunchPad.Views;

public enum OnboardingPath
{
    AgentBob,
    CodingAgent
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

    private void CodingAgent_Click(object sender, RoutedEventArgs e)
    {
        SelectedPath = OnboardingPath.CodingAgent;
        Close(true);

    }
}
