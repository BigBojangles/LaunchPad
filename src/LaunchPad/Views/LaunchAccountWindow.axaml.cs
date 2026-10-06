using LaunchPad.Services.Fence;

namespace LaunchPad.Views;

public partial class LaunchAccountWindow : Window
{
    private readonly Func<bool> _create;

    public LaunchAccountWindow(Func<bool> create)
    {
        _create = create;
        InitializeComponent();
        WhyText.Text = LaunchAccountSetup.NotSignedIn;
        AccountText.Text = LaunchAccountSetup.NotBuilder;
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (_create())
        {
            Close(true);
            return;
        }

        ErrorText.Text = SealText.TestAccountMissing;
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
