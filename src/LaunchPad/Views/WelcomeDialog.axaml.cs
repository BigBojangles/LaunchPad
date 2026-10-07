
namespace LaunchPad.Views;

public partial class WelcomeDialog : Window
{
    public WelcomeDialog(bool nativeOnly = false)
    {
        InitializeComponent();
        if (nativeOnly)
            WarningText.Text = "New Project creates a folder, then lets you choose an installed Windows agent or custom program. Native agents work directly in your folder using your account permissions. Install and sign in to your agent separately. This package has no VM runtime. Your projects stay listed on the left.";
    }

    public bool DontShowAgain => DontShowCheck.IsChecked == true;

    private void GotIt_Click(object sender, RoutedEventArgs e)
    {
        Close(true);

    }
}
