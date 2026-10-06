using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LaunchPad.Models;

namespace LaunchPad.Views;

/// <summary>
/// Fixed identity colors for project tiles. Assign in array order (most distinct first).
/// The closest pairs sit at the end (13-16). Never orange. Never status green, red, or yellow.
/// </summary>
public static class IdentityPalette
{
    public static readonly Color[] Colors =
    {
        Color.FromRgb(0x3A, 0x45, 0x58),  // 1 Ink navy
        Color.FromRgb(0x4E, 0x5E, 0x42),  // 2 Moss
        Color.FromRgb(0x6E, 0x45, 0x60),  // 3 Berry mute
        Color.FromRgb(0x45, 0x70, 0x79),  // 4 Sea slate
        Color.FromRgb(0x4F, 0x5A, 0x8A),  // 5 Soft indigo
        Color.FromRgb(0x6E, 0x62, 0x7A),  // 6 Dust lilac
        Color.FromRgb(0x4A, 0x5F, 0x7A),  // 7 Slate blue
        Color.FromRgb(0x45, 0x60, 0x55),  // 8 Forest mute
        Color.FromRgb(0x5C, 0x65, 0x70),  // 9 Cool ash
        Color.FromRgb(0x7A, 0x5E, 0x6A),  // 10 Mauve taupe
        Color.FromRgb(0x5E, 0x4A, 0x62),  // 11 Heather
        Color.FromRgb(0x6A, 0x45, 0x52),  // 12 Wine mute
        Color.FromRgb(0x2F, 0x55, 0x58),  // 13 Deep teal
        Color.FromRgb(0x5A, 0x48, 0x6E),  // 14 Grape mute
        Color.FromRgb(0x5A, 0x6B, 0x58),  // 15 Sage
        Color.FromRgb(0x63, 0x5A, 0x82)   // 16 Soft violet
    };

    public static Color At(int index) => Colors[((index % Colors.Length) + Colors.Length) % Colors.Length];
}

/// <summary>Status paints only. Never used as a project color.</summary>
public static class StatusColors
{
    public static readonly Color Green = Color.FromRgb(0x22, 0xC5, 0x5E);
    public static readonly Color Red = Color.FromRgb(0xE2, 0x3B, 0x3B);
    public static readonly Color Yellow = Color.FromRgb(0xF5, 0xC5, 0x18);
}

/// <summary>One live session the app already tracks.</summary>
public sealed record LiveTile(string ProjectPath, string ProjectName, int ColorIndex, bool IsVm, SessionRecord? Session = null, string? DisplayName = null);

/// <summary>
/// What the home tiles and the edge bar show. Fed from the sessions the app already tracks
/// (FenceSession.IsProjectOpen for the VM, ProjectLauncher.WasLaunched for an unfenced window).
/// </summary>
public sealed class SessionBoard
{
    private string _signature = "\u0000";
    private string _layout = "\u0000";

    public ObservableCollection<FolderGroup> Groups { get; } = new();
    public ObservableCollection<SessionItem> AllSessions { get; } = new();
    public Action<SessionItem, string, bool>? RenameSession { get; set; }
    public Func<string, ContextMenu>? ProjectMenu { get; set; }
    public Action<SessionItem>? ActivateSession { get; set; }

    public void Show(IReadOnlyList<LiveTile> tiles)
    {
        var signature = string.Join("|", tiles.Select(t => t.ProjectPath + ":" + t.IsVm + ":" + t.ColorIndex + ":" + t.ProjectName + ":" + t.DisplayName + ":" + t.Session));
        if (signature == _signature)
            return;
        _signature = signature;
        var layout = string.Join("|", tiles.Select(tile => tile.ProjectPath + ":" + tile.IsVm + ":" + tile.ColorIndex + ":" +
            (tile.Session?.Id ?? (tile.IsVm ? "vm:" : "host:") + LaunchPad.Services.Fence.QemuLayout.ProjectKey(tile.ProjectPath))));
        if (layout == _layout)
        {
            foreach (var tile in tiles)
            {
                var id = tile.Session?.Id ?? (tile.IsVm ? "vm:" : "host:") + LaunchPad.Services.Fence.QemuLayout.ProjectKey(tile.ProjectPath);
                var kind = tile.IsVm ? "fenced VM" : "unfenced window";
                AllSessions.First(item => item.Id == id).UpdateDisplay(tile.DisplayName ?? tile.ProjectName,
                    (tile.DisplayName ?? tile.ProjectName) + "  ·  " + kind, tile.Session);
            }
            foreach (var group in Groups.Where(group => group.ShowFrame))
                group.Name = tiles.First(tile => tile.ProjectPath.Equals(group.ProjectPath, StringComparison.OrdinalIgnoreCase)).ProjectName;
            return;
        }
        _layout = layout;

        var selected = AllSessions.FirstOrDefault(s => s.IsSelected);
        Groups.Clear();
        AllSessions.Clear();

        // A project with more than one live session gets a framed group with its name.
        // Single sessions sit loose. Both use the same label slot and padding, so they share one baseline.
        var loose = new FolderGroup("", showFrame: false);
        foreach (var project in tiles.GroupBy(t => t.ProjectPath, StringComparer.OrdinalIgnoreCase))
        {
            var list = project.ToList();
            var grouped = list.Count > 1;
            var target = loose;
            if (grouped)
            {
                target = new FolderGroup(list[0].ProjectName, showFrame: true, list[0].ProjectPath);
                Groups.Add(target);
            }

            foreach (var tile in list)
            {
                var kind = tile.IsVm ? "fenced VM" : "unfenced window";
                var item = new SessionItem(
                    tile.DisplayName ?? tile.ProjectName,
                    (tile.DisplayName ?? tile.ProjectName) + "  ·  " + kind,
                    tile.ProjectPath,
                    IdentityPalette.At(tile.ColorIndex),
                    tile.IsVm,
                    status: null,
                    session: tile.Session);
                if (selected is not null
                    && string.Equals(selected.Id, item.Id, StringComparison.OrdinalIgnoreCase))
                    item.IsSelected = true;
                target.Sessions.Add(item);
            }
        }

        if (loose.Sessions.Count > 0)
            Groups.Add(loose);

        foreach (var group in Groups)
        {
            foreach (var session in group.Sessions)
                AllSessions.Add(session);
        }
    }

    public void Select(SessionItem item)
    {
        foreach (var session in AllSessions)
            session.IsSelected = ReferenceEquals(session, item);
    }

    public void Activate(SessionItem item)
    {
        if (!AllSessions.Contains(item)) return;
        Select(item);
        ActivateSession?.Invoke(item);
    }
}

public sealed class FolderGroup : INotifyPropertyChanged
{
    private string _name;
    public FolderGroup(string name, bool showFrame, string? projectPath = null)
    {
        _name = name;
        ShowFrame = showFrame;
        ProjectPath = projectPath;
    }

    public string Name { get => _name; set { if (_name == value) return; _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); } }
    public string? ProjectPath { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool ShowFrame { get; }
    public double CaptionOpacity => ShowFrame ? 1 : 0;
    public IBrush FrameBrush => ShowFrame ? new SolidColorBrush(Color.Parse("#C4B89A")) : Brushes.Transparent;
    public ObservableCollection<SessionItem> Sessions { get; } = new();
}

public sealed class SessionItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public SessionItem(string name, string toolTip, string projectPath, Color identity, bool isVm, Color? status, SessionRecord? session = null)
    {
        Name = name;
        ToolTip = toolTip;
        ProjectPath = projectPath;
        IdentityBrush = Freeze(identity);
        IsVm = isVm;
        Session = session;
        Id = session?.Id ?? (isVm ? "vm:" : "host:") + LaunchPad.Services.Fence.QemuLayout.ProjectKey(projectPath);
        StatusBrush = status is Color s ? Freeze(s) : Paint(session?.State);
        Icon = isVm ? ProductIcons.LaunchPad : ProductIcons.Grok;
    }

    public string Name { get; private set; }
    private string _toolTip = "";
    public string ToolTip { get => _toolTip + "  ·  " + StatusText + (Session?.Error is { } error ? "\n" + error : ""); private set => _toolTip = value; }
    public string ProjectPath { get; }
    public string Id { get; }
    public SessionRecord? Session { get; private set; }
    public void UpdateDisplay(string name, string toolTip, SessionRecord? session)
    {
        Name = name; ToolTip = toolTip; Session = session;
        StatusBrush = Paint(session?.State);
        foreach (var property in new[] { nameof(Name), nameof(ToolTip), nameof(Session), nameof(StatusBrush), nameof(HasStatus), nameof(StatusText), nameof(IsBusy) }) PropertyChanged?.Invoke(this, new(property));
    }

    /// <summary>Project color. Never green, red, or yellow.</summary>
    public SolidColorBrush IdentityBrush { get; }

    /// <summary>Status color only. Null when there is no live status for this session.</summary>
    public SolidColorBrush? StatusBrush { get; private set; }

    public bool HasStatus => StatusBrush is not null;
    public bool IsBusy => Session?.State == SessionLifecycle.Busy;
    public string StatusText => Session?.State switch
    {
        SessionLifecycle.Starting => "Starting",
        SessionLifecycle.Running => IsVm ? "Ready" : "Open",
        SessionLifecycle.Busy => "Working",
        SessionLifecycle.NeedsAnswer => "Needs answer",
        SessionLifecycle.Stopping => "Saving",
        SessionLifecycle.Stopped => "Closed",
        SessionLifecycle.Failed => "Failed",
        _ => "Unavailable"
    };
    private static SolidColorBrush? Paint(SessionLifecycle? state) => state switch
    {
        SessionLifecycle.Running or SessionLifecycle.Busy => Freeze(StatusColors.Green),
        SessionLifecycle.NeedsAnswer => Freeze(StatusColors.Yellow),
        SessionLifecycle.Failed => Freeze(StatusColors.Red),
        null => null,
        _ => Freeze(Color.Parse("#827E75"))
    };
    public bool IsVm { get; }
    public IImage? Icon { get; }
    public bool HasIcon => Icon is not null;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        return brush;
    }
}

/// <summary>Tile pictures: the LaunchPad icon for a fenced VM, the Grok program icon for an unfenced window.</summary>
public static class ProductIcons
{
    private static IImage? _launchPad;
    private static bool _launchPadTried;
    private static IImage? _grok;
    private static bool _grokTried;

    private static Func<IImage?>? _readHostAgentIcon;

    public static void SetHostAgentIcon(Func<IImage?> readIcon)
    {
        _readHostAgentIcon = readIcon;
        _grok = null;
        _grokTried = false;
    }

    public static IImage? LaunchPad
    {
        get
        {
            if (!_launchPadTried)
            {
                _launchPadTried = true;
                _launchPad = FromPack("avares://LaunchPad/Assets/launchpad-rocket.png");
            }

            return _launchPad;
        }
    }

    public static IImage? Grok
    {
        get
        {
            if (!_grokTried)
            {
                _grokTried = true;
                _grok = _readHostAgentIcon?.Invoke();
            }

            return _grok;
        }
    }

    private static IImage? FromPack(string uri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            return new Bitmap(stream);
        }
        catch { return null; }
    }
}
