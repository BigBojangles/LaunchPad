using LaunchPad.Services.Fence;

namespace LaunchPad.Views;

public partial class HypervisorWindow : Window
{
    private readonly HypervisorDecision _decision;
    private readonly Func<bool> _enable;

    public HypervisorWindow(HypervisorDecision decision, Func<bool> enable)
    {
        _decision = decision;
        _enable = enable;
        InitializeComponent();
        BodyText.Text = decision.Message;
        EnableButton.IsVisible = decision.RequestElevation;
    }

    private void Enable_Click(object sender, RoutedEventArgs e)
    {
        if (_decision.RequestElevation && _enable())
            Close(true);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
