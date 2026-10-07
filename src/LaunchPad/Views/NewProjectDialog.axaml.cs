using LaunchPad.Services;
using Avalonia.Platform.Storage;

namespace LaunchPad.Views;

public partial class NewProjectDialog : Window
{
    public NewProjectDialog(string projectsRoot)
    {
        InitializeComponent();
        FolderBox.Text = projectsRoot;
        Loaded += (_, _) =>
        {
            UpdatePreview();
            NameBox.Focus();
        };
    }

    public string ProjectName { get; private set; } = "";
    public string ProjectsRoot { get; private set; } = "";
    public string ProjectPath => Path.Combine(ProjectsRoot, ProjectName);
    public bool RememberLocation => RememberFolder.IsChecked == true;

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();
    private void FolderBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close(false);

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "Choose where to create the project", AllowMultiple = false });
        var folder = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(folder)) FolderBox.Text = folder;
    }

    private bool TryLocation(out string folder)
    {
        folder = "";
        try
        {
            var text = FolderBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text) || !Path.IsPathFullyQualified(text)) return false;
            folder = Path.GetFullPath(text);
            return !File.Exists(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        { return false; }
    }

    private void UpdatePreview()
    {
        if (NameBox is null || FolderBox is null || PathPreview is null || ErrorText is null)
            return;
        if (!TryLocation(out var folder))
        {
            PathPreview.Text = "Choose a full folder path for the project location.";
            ErrorText.Text = "";
            return;
        }
        if (ProjectNames.TrySanitize(NameBox.Text, out var name, out _))
        {
            PathPreview.Text = "It will be created here: " + Path.Combine(folder, name);
            ErrorText.Text = "";
        }
        else if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            PathPreview.Text = "It will be created in: " + folder;
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

        if (!TryLocation(out var folder))
        {
            ErrorText.Text = "Choose a full folder path for the project location.";
            FolderBox.Focus();
            return;
        }
        ProjectName = name;
        ProjectsRoot = folder;
        Close(true);

    }
}
