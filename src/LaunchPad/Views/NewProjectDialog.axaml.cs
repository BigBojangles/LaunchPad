using LaunchPad.Services;

namespace LaunchPad.Views;

public partial class NewProjectDialog : Window
{
    private readonly string _projectsRoot;

    public NewProjectDialog(string projectsRoot)
    {
        _projectsRoot = projectsRoot;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdatePreview();
            NameBox.Focus();
        };
    }

    public string ProjectName { get; private set; } = "";

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (NameBox is null || PathPreview is null || ErrorText is null)
            return;
        if (ProjectNames.TrySanitize(NameBox.Text, out var name, out _))
        {
            PathPreview.Text = "It will be created here: " + Path.Combine(_projectsRoot, name);
            ErrorText.Text = "";
        }
        else if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            PathPreview.Text = "It will be created in: " + _projectsRoot;
            ErrorText.Text = "";
        }
        else
        {
            PathPreview.Text = "";
            ErrorText.Text = "";
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!ProjectNames.TrySanitize(NameBox.Text, out var name, out var error))
        {
            ErrorText.Text = error;
            NameBox.Focus();
            return;
        }

        ProjectName = name;
        Close(true);

    }
}
