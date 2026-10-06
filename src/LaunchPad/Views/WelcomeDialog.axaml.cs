
namespace LaunchPad.Views;

public partial class WelcomeDialog : Window
{
    public WelcomeDialog()
    {
        InitializeComponent();
    }

    public bool DontShowAgain => DontShowCheck.IsChecked == true;

    private void GotIt_Click(object sender, RoutedEventArgs e)
    {
        Close(true);

    }
}
