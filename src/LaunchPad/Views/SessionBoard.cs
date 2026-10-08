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
    private IReadOnlyList<LiveTile> _tiles = Array.Empty<LiveTile>();
    public Func<string, string?>? GroupForSession { get; set; }
    public Action<string, string>? SaveGroup { get; set; }
    public Func<string, string>? DisplayGroupName { get; set; }
    public Func<string, string?>? AgentProgram { get; set; }

    public ObservableCollection<FolderGroup> Groups { get; } = new();
    public ObservableCollection<SessionItem> AllSessions { get; } = new();
    public Action<SessionItem, string, bool>? RenameSession { get; set; }
    public Func<string, ContextMenu>? ProjectMenu { get; set; }
    public Action<SessionItem>? ActivateSession { get; set; }

    public void Show(IReadOnlyList<LiveTile> tiles)
    {
        if (tiles.Select(TileId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tiles.Count)
            throw new ArgumentException("Session IDs must be unique.", nameof(tiles));
        _tiles = tiles.ToArray();
        var signature = string.Join("|", tiles.Select(t => t.ProjectPath + ":" + GroupPath(t) + ":" + t.IsVm + ":" + t.ColorIndex + ":" + t.ProjectName + ":" + t.DisplayName + ":" + t.Session));
        if (signature == _signature)
            return;
        var existing = AllSessions.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var desiredGroups = new List<FolderGroup>();
        var desiredSessions = new List<SessionItem>();

        // A project with more than one live session gets a framed group with its name.
        // Single sessions sit loose. Both use the same label slot and padding, so they share one baseline.
        var loose = Groups.FirstOrDefault(group => !group.ShowFrame) ?? new FolderGroup("", showFrame: false);
        var looseSessions = new List<SessionItem>();
        foreach (var project in tiles.GroupBy(GroupPath, StringComparer.OrdinalIgnoreCase))
        {
            var list = project.ToList();
            var grouped = list.Count > 1 || list.Any(t => !string.Equals(t.ProjectPath, project.Key, StringComparison.OrdinalIgnoreCase));
            var target = loose;
            var groupSessions = looseSessions;
            if (grouped)
            {
                target = Groups.FirstOrDefault(group => group.ShowFrame &&
                    string.Equals(group.ProjectPath, project.Key, StringComparison.OrdinalIgnoreCase))
                    ?? new FolderGroup("", showFrame: true, project.Key);
                target.Name = DisplayGroupName?.Invoke(project.Key) ?? tiles.FirstOrDefault(t => string.Equals(t.ProjectPath, project.Key, StringComparison.OrdinalIgnoreCase))?.ProjectName ?? Path.GetFileName(project.Key);
                groupSessions = new List<SessionItem>();
                desiredGroups.Add(target);
            }

            foreach (var tile in list)
            {
                var kind = tile.IsVm ? "fenced VM" : "native (no sandbox)";
                var id = TileId(tile);
                existing.TryGetValue(id, out var previous);
                var identity = IdentityPalette.At(tile.ColorIndex);
                var name = tile.DisplayName ?? tile.ProjectName;
                var tooltip = name + "  ·  " + kind;
                var item = previous is not null && previous.IsVm == tile.IsVm && previous.IdentityBrush.Color == identity &&
                    string.Equals(previous.ProjectPath, tile.ProjectPath, StringComparison.OrdinalIgnoreCase)
                    ? previous : new SessionItem(name, tooltip, tile.ProjectPath, identity, tile.IsVm, status: null, session: tile.Session)
                    { IsSelected = previous?.IsSelected == true };
                if (ReferenceEquals(item, previous)) item.UpdateDisplay(name, tooltip, tile.Session);
                item.UpdateIcon(AgentProgram?.Invoke(tile.ProjectPath));
                groupSessions.Add(item);
            }
            if (grouped) Reconcile(target.Sessions, groupSessions);
        }

        Reconcile(loose.Sessions, looseSessions);
        if (looseSessions.Count > 0) desiredGroups.Add(loose);

        foreach (var group in desiredGroups) desiredSessions.AddRange(group.Sessions);
        Reconcile(Groups, desiredGroups);
        Reconcile(AllSessions, desiredSessions);
        _signature = signature;
    }

    private string GroupPath(LiveTile tile) => GroupForSession?.Invoke(TileId(tile)) ?? tile.ProjectPath;

    public bool MoveToGroup(string sessionId, string projectPath)
    {
        if (SaveGroup is null || !AllSessions.Any(item => string.Equals(item.Id, sessionId, StringComparison.OrdinalIgnoreCase))) return false;
        SaveGroup(sessionId, projectPath);
        _signature = "\u0000";
        Show(_tiles);
        return true;
    }

    public void FitTiles(double availableWidth)
    {
        var count = Math.Max(1, AllSessions.Count);
        var width = Math.Clamp((availableWidth - 64) / Math.Min(count, 6) - 12, 76, 108);
        foreach (var item in AllSessions) item.SetSize(width);
    }

    private static string TileId(LiveTile tile) => tile.Session?.Id ??
        (tile.IsVm ? "vm:" : "host:") + LaunchPad.Services.Fence.QemuLayout.ProjectKey(tile.ProjectPath);

    // Preserve unaffected item containers, keyboard focus and inline edits.
    // A session that changes grouping may still need a new visual parent.
    private static void Reconcile<T>(ObservableCollection<T> current, IReadOnlyList<T> desired) where T : class
    {
        for (var index = current.Count - 1; index >= 0; index--)
            if (!desired.Contains(current[index])) current.RemoveAt(index);
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < current.Count && ReferenceEquals(current[index], desired[index])) continue;
            var previousIndex = current.IndexOf(desired[index]);
            if (previousIndex >= 0) current.Move(previousIndex, index);
            else current.Insert(index, desired[index]);
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
    public IBrush FrameBrush => ShowFrame ? new SolidColorBrush(IdentityPalette.At(0), 0.5) : Brushes.Transparent;
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
        Icon = ProductIcons.ForAgent(session?.AgentId);
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
        SessionLifecycle.Running => "Open",
        SessionLifecycle.Idle => Session?.Activity?.LastEvent?.Kind switch
        {
            AgentEventKind.RunFailed => "Run failed",
            AgentEventKind.Interrupted => "Interrupted",
            _ => "Idle"
        },
        SessionLifecycle.Busy => "Working",
        SessionLifecycle.NeedsAnswer => "Needs answer",
        SessionLifecycle.Stopping => "Saving",
        SessionLifecycle.Stopped => "Stopped",
        SessionLifecycle.Failed => "Failed",
        _ => "Activity unavailable"
    };
    private static SolidColorBrush? Paint(SessionLifecycle? state) => state switch
    {
        SessionLifecycle.Busy => Freeze(StatusColors.Green),
        SessionLifecycle.NeedsAnswer => Freeze(StatusColors.Yellow),
        SessionLifecycle.Idle or SessionLifecycle.Stopped or SessionLifecycle.Failed => Freeze(StatusColors.Red),
        // Startup, saving and missing activity remain text diagnostics; no fourth lamp.
        _ => null
    };
    public bool IsVm { get; }
    public IImage? Icon { get; private set; }
    public string AgentLabel => Session?.AgentId switch { "grok" => "G", "codex" => "C", "claude" => "A", _ => ">_" };
    public void UpdateIcon(string? program)
    {
        Icon = ProductIcons.ForAgent(Session?.AgentId, program);
        PropertyChanged?.Invoke(this, new(nameof(Icon)));
        PropertyChanged?.Invoke(this, new(nameof(HasIcon)));
        PropertyChanged?.Invoke(this, new(nameof(AgentLabel)));
    }
    public double TileWidth { get; private set; } = 88;
    public double IconSize => Math.Clamp(TileWidth - 48, 32, 52);
    public void SetSize(double width)
    {
        if (Math.Abs(TileWidth - width) < 0.5) return;
        TileWidth = width;
        PropertyChanged?.Invoke(this, new(nameof(TileWidth)));
        PropertyChanged?.Invoke(this, new(nameof(IconSize)));
    }
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
    private static readonly Dictionary<string, IImage?> AgentIcons = new(StringComparer.OrdinalIgnoreCase);
    public static Func<string, IImage?>? ReadProgramIcon { get; set; }
    public static IImage? ForAgent(string? agent, string? program = null)
    {
        if (string.IsNullOrWhiteSpace(agent)) return null;
        var key = agent + "|" + program;
        if (AgentIcons.TryGetValue(key, out var icon)) return icon;
        icon = FromPack("avares://LaunchPad/Assets/" + (agent switch { "grok" => "grok", "codex" => "codex", "claude" => "claude", _ => "custom" }) + "-icon.png");
        if (icon is null && agent == "custom" && !string.IsNullOrWhiteSpace(program)) icon = ReadProgramIcon?.Invoke(program);
        if (icon is null && agent == "grok") icon = Grok;
        AgentIcons[key] = icon;
        return icon;
    }
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
                _launchPad = FromPack("avares://LaunchPad/Assets/launchpad-icon-64.png");
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
