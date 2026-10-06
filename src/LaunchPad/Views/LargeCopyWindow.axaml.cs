using LaunchPad.Services.Fence;

namespace LaunchPad.Views;

public partial class LargeCopyWindow : Window
{
    private readonly IReadOnlyList<TopRow> _rows;

    public LargeCopyWindow(string root, long bytes, IReadOnlyList<TopRow> rows)
    {
        _rows = rows;
        InitializeComponent();
        var megabytes = bytes / (1024 * 1024);
        var size = megabytes >= 1024
            ? (megabytes / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB"
            : megabytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " MB";
        Body.Text = "About " + size + " would be copied into the fence. Uncheck a folder to leave it on this PC.";
        var hasIgnore = File.Exists(Path.Combine(root, ".gitignore"));
        Hint.Text = hasIgnore
            ? "This folder already has a .gitignore. Ignored files are already left out. Unchecked folders are added to it."
            : "Unchecked folders are added to .gitignore.";
        StartGitBox.IsVisible = !(Directory.Exists(Path.Combine(root, ".git")));
        Folders.ItemsSource = rows;
    }

    public bool SendEverything { get; private set; }
    public bool StartGit { get; private set; }
    public IReadOnlyList<string> LeaveOut { get; private set; } = Array.Empty<string>();

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        SendEverything = false;
        StartGit = StartGitBox.IsChecked == true;
        LeaveOut = _rows.Where(row => !row.Include).Select(row => row.Name).ToArray();
        Close(true);
    }

    private void SendAll_Click(object sender, RoutedEventArgs e)
    {
        SendEverything = true;
        StartGit = false;
        LeaveOut = Array.Empty<string>();
        Close(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
