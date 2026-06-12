using System.IO;
using System.Windows.Threading;
using XFinder.Services;

namespace XFinder.Models;

/// <summary>AI 정리 계획의 실행 단위 (AI 시트 → AppModel.ApplyAIPlan).</summary>
public sealed record AIPlannedOp(string Kind, string File, string? Destination);   // Kind: "move" | "delete"

/// <summary>
/// 창 하나의 루트 상태 — mac AppModel.swift 대응.
/// 사이드바, 탭, 히스토리, 클립보드, 시트 라우팅, 사용자 설정 전부가 여기로 모인다.
/// 생성자는 가볍게(I/O 금지), 실제 초기화는 Bootstrap()에서.
/// </summary>
public sealed class AppModel : ObservableObject
{
    private static readonly StringComparer PathCmp = StringComparer.OrdinalIgnoreCase;

    // ── 상태 ────────────────────────────────────────────────────────────

    private List<SidebarSection> _sections = new();
    public List<SidebarSection> Sections { get => _sections; set => Set(ref _sections, value); }

    public System.Collections.ObjectModel.ObservableCollection<PaneTab> Tabs { get; } = new();

    private int _activeTabIndex;
    public int ActiveTabIndex
    {
        get => _activeTabIndex;
        set
        {
            if (Set(ref _activeTabIndex, value))
            {
                OnPropertyChanged(nameof(Detail));
                OnPropertyChanged(nameof(SelectedFolder));
                OnPropertyChanged(nameof(CanGoBack));
                OnPropertyChanged(nameof(CanGoForward));
            }
        }
    }

    /// <summary>활성 탭 포워딩 — 저장 프로퍼티로 만들지 말 것.</summary>
    public PaneTab Detail => Tabs[Math.Clamp(ActiveTabIndex, 0, Tabs.Count - 1)];

    public string SelectedFolder
    {
        get => Detail.Directory;
        set => Detail.Directory = value;
    }

    private bool _showHidden;
    /// <summary>숨김 파일 표시 (비영속 — 실행마다 false).</summary>
    public bool ShowHidden { get => _showHidden; private set => Set(ref _showHidden, value); }

    private string? _statusFreeSpace;
    public string? StatusFreeSpace { get => _statusFreeSpace; set => Set(ref _statusFreeSpace, value); }

    /// <summary>즐겨찾기 드래그 중 경로 (재정렬 UI용).</summary>
    public string? DraggingFavorite { get; set; }

    private ClipboardState? _internalClipboard;
    public ClipboardState? InternalClipboard
    {
        get => _internalClipboard;
        private set => Set(ref _internalClipboard, value);
    }
    private uint _clipboardSequence = unchecked((uint)~0u);

    /// <summary>텍스트 입력 중 — 키 모니터/type-select 전부 정지.</summary>
    public bool TextInputActive { get; set; }

    private Guid? _selectedSidebarId;
    public Guid? SelectedSidebarId { get => _selectedSidebarId; set => Set(ref _selectedSidebarId, value); }

    private FocusPane _focusedPane = FocusPane.Detail;
    public FocusPane FocusedPane { get => _focusedPane; set => Set(ref _focusedPane, value); }

    private AppSheet? _sheet;
    public AppSheet? Sheet { get => _sheet; set => Set(ref _sheet, value); }

    private ConfirmRequest? _confirm;
    public ConfirmRequest? Confirm
    {
        get => _confirm;
        set { if (Set(ref _confirm, value) && value is not null) ConfirmFocus = 0; }
    }

    private int _confirmFocus;
    /// <summary>0 = 확인/실행 버튼, 1 = 취소 버튼.</summary>
    public int ConfirmFocus { get => _confirmFocus; set => Set(ref _confirmFocus, value); }

    private string? _errorMessage;
    public string? ErrorMessage { get => _errorMessage; set => Set(ref _errorMessage, value); }

    private string? _infoMessage;
    public string? InfoMessage { get => _infoMessage; set => Set(ref _infoMessage, value); }

    private string? _typeSelectDisplay;
    /// <summary>type-select 입력 HUD 텍스트 (null = 숨김).</summary>
    public string? TypeSelectDisplay { get => _typeSelectDisplay; set => Set(ref _typeSelectDisplay, value); }

    // ── 영속 설정 ───────────────────────────────────────────────────────

    private const string KeyFavorites = "XFinder.favorites.v1";
    private const string KeyExcluded = "XFinder.aiExcludedFolders.v1";
    private const string KeyFolderViewModes = "XFinder.folderViewModes.v1";
    private const string KeyAppearance = "XFinder.appearance.v1";
    private const string KeyDateStyle = "XFinder.dateStyle.v1";
    private const string KeySearchPosition = "XFinder.searchPosition.v1";
    private const string KeyTerminalApp = "XFinder.terminalApp.v1";
    private const string KeyListScale = "XFinder.listScale.v1";
    private const string KeyColumnWidths = "XFinder.columnWidths.v1";
    private const string KeyRecentsCategories = "XFinder.recentsCategories.v1";
    private const string KeyCalcFolderSizes = "XFinder.calculateFolderSizes.v1";
    private const string KeyAiProvider = "XFinder.aiProvider.v1";
    private const string KeyGeminiApiKey = "XFinder.geminiAPIKey.v1";
    private const string KeyGeminiModel = "XFinder.geminiModel.v1";
    private const string KeyOllamaBaseUrl = "XFinder.ollamaBaseURL.v1";
    private const string KeyOllamaModel = "XFinder.ollamaModel.v1";
    private const string KeyDefaultTabs = "XFinder.defaultTabs.v1";

    public List<string> FavoritePaths { get; private set; } = new();
    public List<string> ExcludedPaths { get; private set; } = new();
    private Dictionary<string, ViewMode> _folderViewModes = new(PathCmp);

    private AppTheme _appearance = AppTheme.System;
    public AppTheme Appearance
    {
        get => _appearance;
        set
        {
            if (!Set(ref _appearance, value)) return;
            ThemeService.Apply(value);   // 즉시 적용 + 저장
        }
    }

    private TerminalAppChoice _terminalApp = TerminalAppChoice.Auto;
    public TerminalAppChoice TerminalApp
    {
        get => _terminalApp;
        set { if (Set(ref _terminalApp, value)) SettingsStore.Set(KeyTerminalApp, value.RawValue()); }
    }

    private DateDisplayStyle _dateStyle = DateDisplayStyle.Absolute;
    public DateDisplayStyle DateStyle
    {
        get => _dateStyle;
        set { if (Set(ref _dateStyle, value)) SettingsStore.Set(KeyDateStyle, value == DateDisplayStyle.Relative ? "relative" : "absolute"); }
    }

    private SearchBarPosition _searchPosition = SearchBarPosition.Toolbar;
    public SearchBarPosition SearchPosition
    {
        get => _searchPosition;
        set { if (Set(ref _searchPosition, value)) SettingsStore.Set(KeySearchPosition, value.RawValue()); }
    }

    private double _listScale = 1.0;
    /// <summary>목록 글자/아이콘 배율 (0.8 ~ 1.8).</summary>
    public double ListScale
    {
        get => _listScale;
        set
        {
            var clamped = Math.Clamp(value, 0.8, 1.8);
            if (Set(ref _listScale, clamped)) SettingsStore.Set(KeyListScale, clamped);
        }
    }

    private Dictionary<string, double> _columnWidths = new();
    /// <summary>열 너비 (배율 적용 전; 키 = ListColumn rawValue).</summary>
    public IReadOnlyDictionary<string, double> ColumnWidths => _columnWidths;

    public double ColumnWidth(ListColumn col)
        => _columnWidths.TryGetValue(col.Key(), out var w) ? w : col.DefaultWidth();

    public void SetColumnWidth(ListColumn col, double width)
    {
        _columnWidths[col.Key()] = Math.Clamp(width, ListColumnExtensions.MinWidth, ListColumnExtensions.MaxWidth);
        SettingsStore.Set(KeyColumnWidths, _columnWidths);
        OnPropertyChanged(nameof(ColumnWidths));
    }

    public void ResetColumnWidth(ListColumn col)
    {
        _columnWidths.Remove(col.Key());
        SettingsStore.Set(KeyColumnWidths, _columnWidths);
        OnPropertyChanged(nameof(ColumnWidths));
    }

    public void ResetAllColumnWidths()
    {
        _columnWidths.Clear();
        SettingsStore.Set(KeyColumnWidths, _columnWidths);
        OnPropertyChanged(nameof(ColumnWidths));
    }

    private HashSet<string> _recentsCategories = new() { "문서", "이미지" };
    /// <summary>최근 항목 카테고리 필터. 빈 집합 = 전체 표시 (빈 집합도 유효 저장값).</summary>
    public IReadOnlyCollection<string> RecentsCategories => _recentsCategories;

    public void SetRecentsCategories(IEnumerable<string> categories)
    {
        _recentsCategories = new HashSet<string>(categories);
        SettingsStore.Set(KeyRecentsCategories, _recentsCategories.ToList());
        OnPropertyChanged(nameof(RecentsCategories));
        if (Detail.RecentsMode) ShowRecents();
    }

    public void ToggleRecentsCategory(string category)
    {
        var set = new HashSet<string>(_recentsCategories);
        if (!set.Add(category)) set.Remove(category);
        SetRecentsCategories(set);
    }

    private bool _calculateFolderSizes;
    public bool CalculateFolderSizes
    {
        get => _calculateFolderSizes;
        set
        {
            if (!Set(ref _calculateFolderSizes, value)) return;
            SettingsStore.Set(KeyCalcFolderSizes, value);
            if (value) ComputeFolderSizes();
            else _sizeCts?.Cancel();
        }
    }

    private string _aiProviderRaw = "gemini";
    /// <summary>"ollama" | "gemini".</summary>
    public string AiProviderRaw
    {
        get => _aiProviderRaw;
        set { if (Set(ref _aiProviderRaw, value)) SettingsStore.Set(KeyAiProvider, value); }
    }

    private string _geminiApiKey = "";
    public string GeminiApiKey
    {
        get => _geminiApiKey;
        set { if (Set(ref _geminiApiKey, value)) SettingsStore.Set(KeyGeminiApiKey, value); }
    }

    private string _geminiModel = "";
    public string GeminiModel
    {
        get => _geminiModel;
        set { if (Set(ref _geminiModel, value)) SettingsStore.Set(KeyGeminiModel, value); }
    }

    private string _ollamaBaseUrl = "";
    public string OllamaBaseUrl
    {
        get => _ollamaBaseUrl;
        set { if (Set(ref _ollamaBaseUrl, value)) SettingsStore.Set(KeyOllamaBaseUrl, value); }
    }

    private string _ollamaModel = "";
    public string OllamaModel
    {
        get => _ollamaModel;
        set { if (Set(ref _ollamaModel, value)) SettingsStore.Set(KeyOllamaModel, value); }
    }

    public string EffectiveGeminiModel => string.IsNullOrWhiteSpace(GeminiModel) ? "gemini-flash-latest" : GeminiModel;
    public string EffectiveOllamaBaseUrl => string.IsNullOrWhiteSpace(OllamaBaseUrl) ? "http://localhost:11434" : OllamaBaseUrl;
    public string EffectiveOllamaModel => string.IsNullOrWhiteSpace(OllamaModel) ? "gemma4:latest" : OllamaModel;

    public List<string> DefaultTabPaths { get; private set; } = new();

    // ── 내부 작업 상태 ──────────────────────────────────────────────────

    private CancellationTokenSource? _listCts;
    private CancellationTokenSource? _sizeCts;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _recentsCts;
    private CancellationTokenSource? _tagCts;

    private readonly Dictionary<string, long> _folderSizeCache = new(PathCmp);
    private int _tabColorCounter = 1;   // 첫 탭은 0

    private List<(string Path, long Size)> _typeEntries = new();
    private int _typeLoaded;            // items.Count로 세지 말 것 (삭제 시 중복 로드 버그)
    private const int TypePageSize = 500;

    private bool _didBootstrap;

    public static string HomePath => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // ── init / bootstrap ────────────────────────────────────────────────

    public AppModel()
    {
        var home = HomePath;
        FavoritePaths = LoadFavorites();
        ExcludedPaths = SettingsStore.Get<List<string>>(KeyExcluded) ?? new();
        _folderViewModes = LoadFolderViewModes();
        _appearance = ThemeService.Current;
        _terminalApp = TerminalAppChoiceExtensions.FromRaw(SettingsStore.Get<string>(KeyTerminalApp));
        _dateStyle = SettingsStore.Get<string>(KeyDateStyle) == "relative" ? DateDisplayStyle.Relative : DateDisplayStyle.Absolute;
        _searchPosition = SearchBarPositionExtensions.FromRaw(SettingsStore.Get<string>(KeySearchPosition));
        _listScale = Math.Clamp(SettingsStore.Get(KeyListScale, 1.0), 0.8, 1.8);
        _columnWidths = SettingsStore.Get<Dictionary<string, double>>(KeyColumnWidths) ?? new();
        var savedCats = SettingsStore.Get<List<string>>(KeyRecentsCategories);
        _recentsCategories = savedCats is null ? new HashSet<string> { "문서", "이미지" } : new HashSet<string>(savedCats);
        _calculateFolderSizes = SettingsStore.Get(KeyCalcFolderSizes, false);
        _aiProviderRaw = SettingsStore.Get(KeyAiProvider, "gemini") ?? "gemini";
        _geminiApiKey = SettingsStore.Get(KeyGeminiApiKey, "") ?? "";
        _geminiModel = SettingsStore.Get(KeyGeminiModel, "") ?? "";
        _ollamaBaseUrl = SettingsStore.Get(KeyOllamaBaseUrl, "") ?? "";
        _ollamaModel = SettingsStore.Get(KeyOllamaModel, "") ?? "";
        DefaultTabPaths = SettingsStore.Get<List<string>>(KeyDefaultTabs) ?? new();

        var pane = new PaneTab(home)
        {
            HistoryIndex = 0,
            ViewMode = _folderViewModes.TryGetValue(home, out var vm) ? vm : ViewMode.Full,
        };
        pane.History.Add(home);
        Tabs.Add(pane);
        // 디렉터리 읽기·사이드바 구성 등 무거운 작업은 Bootstrap()에서.
    }

    private static List<string> LoadFavorites()
    {
        var saved = SettingsStore.Get<List<string>>(KeyFavorites);
        if (saved is not null) return saved.Where(Directory.Exists).ToList();
        // 기본 즐겨찾기: 존재하는 표준 폴더들
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(HomePath, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        };
        return candidates.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).ToList();
    }

    private static Dictionary<string, ViewMode> LoadFolderViewModes()
    {
        var raw = SettingsStore.Get<Dictionary<string, string>>(KeyFolderViewModes) ?? new();
        var result = new Dictionary<string, ViewMode>(PathCmp);
        foreach (var (k, v) in raw)
            result[k] = v == "icon" ? ViewMode.Icon : ViewMode.Full;
        return result;
    }

    private void SaveFolderViewModes()
    {
        var raw = _folderViewModes.ToDictionary(kv => kv.Key, kv => kv.Value == ViewMode.Icon ? "icon" : "full");
        SettingsStore.Set(KeyFolderViewModes, raw);
    }

    /// <summary>창 Loaded 시 1회 호출 — 실제 초기화.</summary>
    public void Bootstrap()
    {
        if (_didBootstrap) return;
        _didBootstrap = true;

        ReloadDetail();
        Detail.Cursor = Detail.Items.FirstOrDefault()?.Path;
        RebuildSections();
        SelectedSidebarId = SidebarItemMatching(SelectedFolder);

        var saved = DefaultTabPaths.Where(Directory.Exists).ToList();
        if (saved.Count == 0)
            ShowRecents();   // Finder처럼 최근 항목으로 시작
        else
            RestoreDefaultTabs(saved);
    }

    // ── 사이드바 구성 ────────────────────────────────────────────────────

    public void RebuildSections()
    {
        Sections = new List<SidebarSection>
        {
            BuildFavoritesSection(),
            BuildLocationsSection(),
            BuildTagsSection(),
        };
    }

    private SidebarSection BuildFavoritesSection()
    {
        var items = new List<SidebarItem>
        {
            new("최근 항목", "clock", null, SidebarItem.ItemKind.Recents, hasChildren: false),
        };
        foreach (var path in FavoritePaths.Where(Directory.Exists))
        {
            var hasKids = FileSystemService.HasSubfolders(path, ShowHidden);
            items.Add(new SidebarItem(FavoriteTitle(path), FavoriteIcon(path), path,
                SidebarItem.ItemKind.Folder, hasChildren: hasKids));
        }
        return new SidebarSection("즐겨찾기", items);
    }

    private SidebarSection BuildLocationsSection()
    {
        var items = new List<SidebarItem>();
        var home = HomePath;
        items.Add(new SidebarItem(Path.GetFileName(home.TrimEnd('\\')), "house", home,
            SidebarItem.ItemKind.Folder, hasChildren: FileSystemService.HasSubfolders(home, ShowHidden)));
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            var root = drive.RootDirectory.FullName;   // "C:\"
            var letter = root.TrimEnd('\\');           // "C:"
            string label;
            try
            {
                label = string.IsNullOrEmpty(drive.VolumeLabel)
                    ? (drive.DriveType == DriveType.Fixed ? $"로컬 디스크 ({letter})" : $"드라이브 ({letter})")
                    : $"{drive.VolumeLabel} ({letter})";
            }
            catch { label = $"로컬 디스크 ({letter})"; }
            var icon = drive.DriveType == DriveType.Fixed ? "internaldrive" : "externaldrive";
            items.Add(new SidebarItem(label, icon, root, SidebarItem.ItemKind.Folder, hasChildren: true));
        }
        return new SidebarSection("위치", items);
    }

    /// <summary>표준 7색 태그 (이름, 색 인덱스 — mac Finder 체계 유지).</summary>
    public static readonly (string Name, int ColorIndex)[] StandardTags =
    {
        ("빨간색", 6), ("주황색", 7), ("노란색", 5), ("초록색", 2), ("파란색", 4), ("보라색", 3), ("회색", 1),
    };

    private static SidebarSection BuildTagsSection()
    {
        var items = StandardTags
            .Select(t => new SidebarItem(t.Name, "circle.fill", null, SidebarItem.ItemKind.Tag, hasChildren: false))
            .ToList();
        return new SidebarSection("태그", items);
    }

    /// <summary>즐겨찾기 섹션만 교체 (위치 트리 펼침 상태 보존).</summary>
    public void RebuildFavoritesSection()
    {
        var sections = new List<SidebarSection>(Sections);
        var idx = sections.FindIndex(s => s.Title == "즐겨찾기");
        if (idx >= 0) sections[idx] = BuildFavoritesSection();
        else sections.Insert(0, BuildFavoritesSection());
        Sections = sections;
    }

    public static string FavoriteTitle(string path)
    {
        var std = Standardize(path);
        if (PathEquals(std, Environment.GetFolderPath(Environment.SpecialFolder.Desktop))) return "데스크탑";
        if (PathEquals(std, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))) return "문서";
        if (PathEquals(std, Path.Combine(HomePath, "Downloads"))) return "다운로드";
        if (PathEquals(std, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures))) return "사진";
        if (PathEquals(std, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))) return "동영상";
        if (PathEquals(std, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic))) return "음악";
        var name = Path.GetFileName(std.TrimEnd('\\'));
        return name switch
        {
            "Desktop" => "데스크탑", "Documents" => "문서", "Downloads" => "다운로드",
            "Pictures" => "사진", "Videos" => "동영상", "Music" => "음악",
            "Public" => "공용", "Library" => "라이브러리",
            _ => string.IsNullOrEmpty(name) ? std : name,
        };
    }

    public static string FavoriteIcon(string path)
    {
        return FavoriteTitle(path) switch
        {
            "응용 프로그램" => "square.grid.2x2",
            "데스크탑" => "menubar.dock.rectangle",
            "문서" => "doc",
            "다운로드" => "arrow.down.circle",
            "사진" => "photo",
            "동영상" => "film",
            "음악" => "music.note",
            _ => "folder",
        };
    }

    // ── 즐겨찾기 ─────────────────────────────────────────────────────────

    public bool IsFavorite(string path)
        => FavoritePaths.Any(f => PathEquals(f, path));

    public void AddFavorite(string path)
    {
        if (!Directory.Exists(path) || IsFavorite(path)) return;
        FavoritePaths.Add(Standardize(path));
        SettingsStore.Set(KeyFavorites, FavoritePaths);
        RebuildFavoritesSection();
    }

    public void RemoveFavorite(string path)
    {
        FavoritePaths.RemoveAll(f => PathEquals(f, path));
        SettingsStore.Set(KeyFavorites, FavoritePaths);
        RebuildFavoritesSection();
    }

    public void ToggleFavoriteForCursor()
    {
        var item = CurrentItem();
        if (item is null || !item.IsDirectory) return;
        if (IsFavorite(item.Path)) RemoveFavorite(item.Path);
        else AddFavorite(item.Path);
    }

    /// <summary>즐겨찾기 드래그 재정렬 — movedPath를 target 앞으로 (target null = 맨 뒤).</summary>
    public void MoveFavorite(string fromPath, string? toBefore)
    {
        var from = FavoritePaths.FindIndex(f => PathEquals(f, fromPath));
        if (from < 0) return;
        var moved = FavoritePaths[from];
        FavoritePaths.RemoveAt(from);
        var to = toBefore is null ? FavoritePaths.Count : FavoritePaths.FindIndex(f => PathEquals(f, toBefore));
        if (to < 0) to = FavoritePaths.Count;
        if (to == from && PathEquals(FavoritePaths.ElementAtOrDefault(to) ?? "", moved)) { FavoritePaths.Insert(from, moved); return; }
        FavoritePaths.Insert(to, moved);
        SettingsStore.Set(KeyFavorites, FavoritePaths);
        RebuildFavoritesSection();
    }

    // ── AI 정리 예외/보호 폴더 ───────────────────────────────────────────

    public bool IsDirectlyExcluded(string path) => ExcludedPaths.Any(e => PathEquals(e, path));

    public bool IsExcluded(string path)
    {
        var p = Standardize(path);
        return ExcludedPaths.Any(e => PathEquals(e, p) || p.StartsWith(Standardize(e) + "\\", StringComparison.OrdinalIgnoreCase));
    }

    public void AddExcludedFolder(string path)
    {
        if (!Directory.Exists(path) || IsDirectlyExcluded(path)) return;
        ExcludedPaths.Add(Standardize(path));
        SettingsStore.Set(KeyExcluded, ExcludedPaths);
        InfoMessage = $"“{Path.GetFileName(path.TrimEnd('\\'))}” 및 하위 폴더를 AI 정리 예외로 등록했습니다.";
    }

    public void RemoveExcludedFolder(string path)
    {
        ExcludedPaths.RemoveAll(e => PathEquals(e, path));
        SettingsStore.Set(KeyExcluded, ExcludedPaths);
        InfoMessage = $"“{Path.GetFileName(path.TrimEnd('\\'))}”의 AI 정리 예외를 해제했습니다.";
    }

    public static bool IsApplicationsLocation(string path)
    {
        var p = Standardize(path);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };
        return roots.Where(r => !string.IsNullOrEmpty(r))
            .Any(r => PathEquals(p, r) || p.StartsWith(Standardize(r) + "\\", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsProtectedLocation(string path)
    {
        var p = Standardize(path);
        // 재귀 보호
        var recursive = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        foreach (var r in recursive)
        {
            if (string.IsNullOrEmpty(r)) continue;
            if (PathEquals(p, r) || p.StartsWith(Standardize(r) + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        // 자체만 보호: 드라이브 루트, C:\Users
        if (p.Length <= 3 && p.Length >= 2 && p[1] == ':') return true;   // "C:" / "C:\"
        var usersDir = Path.Combine(Path.GetPathRoot(HomePath) ?? "C:\\", "Users");
        if (PathEquals(p, usersDir)) return true;
        return false;
    }

    public bool AiOrganizeBlocked(string path) => IsExcluded(path) || IsProtectedLocation(path);

    // ── 탐색 ─────────────────────────────────────────────────────────────

    /// <summary>모든 폴더 이동의 단일 진입점.</summary>
    public void Select(string url, bool addHistory = true, Guid? sidebarId = null)
    {
        var target = Standardize(url);
        var pane = Detail;
        pane.Directory = target;

        // 상태 리셋
        pane.Selection.Clear();
        pane.Filter = "";
        pane.SearchMode = false; pane.RecentsMode = false; pane.TagMode = false; pane.TypeMode = false;
        pane.TagName = null; pane.TypeName = null; pane.TypeTotal = 0;
        _typeEntries = new(); _typeLoaded = 0;

        _searchCts?.Cancel();
        _recentsCts?.Cancel();
        _tagCts?.Cancel();

        pane.Cursor = null;
        pane.ViewMode = _folderViewModes.TryGetValue(target, out var vm) ? vm : ViewMode.Full;

        if (addHistory) PushHistory(target);
        SelectedSidebarId = sidebarId ?? SidebarItemMatching(target);

        LoadDetail(target);
        OnPropertyChanged(nameof(SelectedFolder));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    private async void LoadDetail(string dir)
    {
        _listCts?.Cancel();
        _sizeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _listCts = cts;
        var pane = Detail;   // 활성 탭 캡처 — 로드 중 탭 전환에도 결과가 요청한 탭으로

        List<FileItem>? items = null;
        string? error = null;
        try
        {
            items = await Task.Run(() => FileSystemService.List(dir), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { error = ex.Message; }

        if (cts.IsCancellationRequested || !PathEquals(pane.Directory, dir)) return;
        if (items is not null) { pane.RawItems = items; pane.LoadError = null; }
        else { pane.RawItems = new(); pane.LoadError = error; }

        pane.Rebuild(ShowHidden);
        // RevealInList가 미리 지정한 커서(새 목록에 존재)는 보존 — 일반 탐색은 Select가 null로 리셋함
        if (pane.Cursor is null || !pane.Items.Any(i => PathEquals(i.Path, pane.Cursor)))
            pane.Cursor = pane.Items.FirstOrDefault()?.Path;
        ComputeFolderSizes();
        UpdateFreeSpace(dir);
    }

    private async void UpdateFreeSpace(string dir)
    {
        var text = await Task.Run(() =>
        {
            try
            {
                var root = Path.GetPathRoot(dir);
                if (string.IsNullOrEmpty(root)) return null;
                var free = new DriveInfo(root).AvailableFreeSpace;
                return Format.Bytes(free) + " 사용 가능";
            }
            catch { return null; }
        });
        if (PathEquals(Detail.Directory, dir)) StatusFreeSpace = text;
    }

    private void PushHistory(string url)
    {
        var pane = Detail;
        if (pane.HistoryIndex >= 0 && pane.HistoryIndex < pane.History.Count
            && PathEquals(pane.History[pane.HistoryIndex], url)) return;
        if (pane.HistoryIndex < pane.History.Count - 1)
            pane.History.RemoveRange(pane.HistoryIndex + 1, pane.History.Count - pane.HistoryIndex - 1);
        pane.History.Add(url);
        pane.HistoryIndex = pane.History.Count - 1;
    }

    public bool CanGoBack => Detail.HistoryIndex > 0;
    public bool CanGoForward => Detail.HistoryIndex < Detail.History.Count - 1;

    public void GoBack()
    {
        if (!CanGoBack) return;
        Detail.HistoryIndex--;
        Select(Detail.History[Detail.HistoryIndex], addHistory: false);
    }

    public void GoForward()
    {
        if (!CanGoForward) return;
        Detail.HistoryIndex++;
        Select(Detail.History[Detail.HistoryIndex], addHistory: false);
    }

    public void GoUp()
    {
        var parent = Path.GetDirectoryName(SelectedFolder.TrimEnd('\\'));
        if (string.IsNullOrEmpty(parent) || PathEquals(parent, SelectedFolder)) return;
        Select(parent);
    }

    public void ActivateSidebar(SidebarItem item)
    {
        switch (item.Kind)
        {
            case SidebarItem.ItemKind.Folder:
            case SidebarItem.ItemKind.Trash:
                if (item.Path is not null) Select(item.Path, sidebarId: item.Id);
                break;
            case SidebarItem.ItemKind.Computer:
                break;   // 현재 UI에 컴퓨터 노드 없음
            case SidebarItem.ItemKind.Recents:
                ShowRecents();
                break;
            case SidebarItem.ItemKind.Tag:
                ShowTag(item.Title, item.Id);
                break;
        }
    }

    /// <summary>동기 재목록 (파일 작업 후 갱신용).</summary>
    public void ReloadDetail()
    {
        var pane = Detail;
        try
        {
            pane.RawItems = FileSystemService.List(pane.Directory);
            pane.LoadError = null;
        }
        catch (Exception ex)
        {
            pane.RawItems = new();
            pane.LoadError = ex.Message;
        }
        pane.Rebuild(ShowHidden);
        ComputeFolderSizes();
    }

    /// <summary>⌘R/F5 — recentsMode면 재로드, 아니면 용량 캐시 비우고 재목록.</summary>
    public void Refresh()
    {
        if (Detail.RecentsMode) { ShowRecents(); return; }
        foreach (var item in Detail.RawItems)
            _folderSizeCache.Remove(item.Path);
        ReloadDetail();
    }

    public void GoToFolderPath(string raw)
    {
        var path = raw.Trim();
        if (path.StartsWith('~'))
            path = HomePath + path[1..];
        path = Environment.ExpandEnvironmentVariables(path);
        if (!Directory.Exists(path))
        {
            ErrorMessage = $"폴더를 찾을 수 없습니다:\n{raw}";
            return;
        }
        Select(Standardize(path));
    }

    public void Open(FileItem item)
    {
        if (item.IsDirectory && !item.IsBundle) Select(item.Path);
        else SystemActions.Open(item.Path);
    }

    /// <summary>검색/가상 목록 우클릭 "위치로 이동": 부모 폴더로 이동 후 커서.</summary>
    public void RevealInList(FileItem item)
    {
        var parent = Path.GetDirectoryName(item.Path.TrimEnd('\\'));
        if (string.IsNullOrEmpty(parent)) return;
        Select(parent);
        Detail.Cursor = item.Path;
    }

    /// <summary>검색 결과 표시명 — 검색 루트 기준 상대 경로.</summary>
    public string RelativeDisplay(FileItem item)
    {
        var root = Standardize(SelectedFolder);
        var p = Standardize(item.Path);
        if (p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            return p[(root.Length + 1)..];
        return item.Name;
    }

    // ── 탭 ──────────────────────────────────────────────────────────────

    public void NewTab(string? folder = null)
    {
        var target = Standardize(folder ?? SelectedFolder);
        if (target == PaneTab.ComputerPath || !Directory.Exists(target)) target = HomePath;
        var pane = new PaneTab(target)
        {
            HistoryIndex = 0,
            ViewMode = _folderViewModes.TryGetValue(target, out var vm) ? vm : ViewMode.Full,
            GroupKey = Detail.GroupKey,   // 현재 탭에서 상속
            ColorIndex = _tabColorCounter++,
        };
        pane.History.Add(target);
        Tabs.Add(pane);
        ActiveTabIndex = Tabs.Count - 1;
        SelectedSidebarId = SidebarItemMatching(target);
        FocusedPane = FocusPane.Detail;
        LoadDetail(target);
        OnPropertyChanged(nameof(SelectedFolder));
    }

    /// <summary>탭이 1개면 무시 (그때 Ctrl+W는 창 닫기 — 키 라우팅 책임).</summary>
    public void CloseTab(int index)
    {
        if (Tabs.Count <= 1 || index < 0 || index >= Tabs.Count) return;
        Tabs.RemoveAt(index);
        if (index < ActiveTabIndex) ActiveTabIndex--;
        else if (ActiveTabIndex >= Tabs.Count) ActiveTabIndex = Tabs.Count - 1;
        else OnPropertyChanged(nameof(Detail));
        AfterTabSwitch();
    }

    public void CloseCurrentTab() => CloseTab(ActiveTabIndex);

    public void CloseOtherTabs(int index)
    {
        if (index < 0 || index >= Tabs.Count) return;
        var keep = Tabs[index];
        for (int i = Tabs.Count - 1; i >= 0; i--)
            if (i != index) Tabs.RemoveAt(i);
        ActiveTabIndex = 0;
        AfterTabSwitch();
    }

    /// <summary>내용은 다시 읽지 않음 — 목록·커서·선택·스크롤 보존 (의도된 동작).</summary>
    public void SelectTab(int index)
    {
        if (index == ActiveTabIndex || index < 0 || index >= Tabs.Count) return;
        ActiveTabIndex = index;
        AfterTabSwitch();
    }

    public void CycleTab(int delta)
    {
        if (Tabs.Count < 2) return;
        SelectTab(((ActiveTabIndex + delta) % Tabs.Count + Tabs.Count) % Tabs.Count);
    }

    private void AfterTabSwitch()
    {
        SelectedSidebarId = SidebarItemMatching(Detail.Directory);
        UpdateFreeSpace(Detail.Directory);
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(SelectedFolder));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    // ── 기본 탭 ──────────────────────────────────────────────────────────

    public void SaveCurrentTabsAsDefault()
    {
        DefaultTabPaths = Tabs.Select(t => t.Directory).Where(d => d != PaneTab.ComputerPath).ToList();
        SettingsStore.Set(KeyDefaultTabs, DefaultTabPaths);
        InfoMessage = $"현재 탭 {DefaultTabPaths.Count}개를 기본 탭으로 저장했습니다.\n다음 실행부터 이 탭들로 시작합니다.";
    }

    public void ClearDefaultTabs()
    {
        DefaultTabPaths = new();
        SettingsStore.Remove(KeyDefaultTabs);
        InfoMessage = "기본 탭을 삭제했습니다. 다음 실행부터 기본 동작으로 시작합니다.";
    }

    private void RestoreDefaultTabs(List<string> paths)
    {
        if (paths.Count == 0) return;
        Select(paths[0]);
        foreach (var p in paths.Skip(1))
            NewTab(p);
        SelectTab(0);
    }

    // ── 가상 목록: 최근 항목 ─────────────────────────────────────────────

    public async void ShowRecents()
    {
        _listCts?.Cancel();
        _searchCts?.Cancel();
        _tagCts?.Cancel();
        _recentsCts?.Cancel();
        var pane = Detail;
        pane.SearchMode = false; pane.TagMode = false; pane.TypeMode = false;
        pane.TagName = null; pane.TypeName = null; pane.TypeTotal = 0;
        pane.RecentsMode = true;
        pane.Selection.Clear();
        pane.Items = Array.Empty<FileItem>();
        pane.Cursor = null;
        SelectedSidebarId = Sections.SelectMany(s => s.Items)
            .FirstOrDefault(i => i.Kind == SidebarItem.ItemKind.Recents)?.Id;
        OnPropertyChanged(nameof(Detail));

        var cts = new CancellationTokenSource();
        _recentsCts = cts;
        var categories = _recentsCategories.ToList();
        List<FileItem> items;
        try
        {
            items = await Task.Run(() => RecentsService.Load(100, categories, cts.Token), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch { items = new(); }

        if (cts.IsCancellationRequested || !pane.RecentsMode) return;
        pane.Items = items;
        pane.Cursor = items.FirstOrDefault()?.Path;
    }

    // ── 가상 목록: 태그 ──────────────────────────────────────────────────

    public async void ShowTag(string tagName, Guid? sidebarId = null)
    {
        _searchCts?.Cancel();
        _recentsCts?.Cancel();
        _tagCts?.Cancel();
        _listCts?.Cancel();
        var pane = Detail;
        pane.SearchMode = false; pane.RecentsMode = false; pane.TypeMode = false;
        pane.TypeName = null; pane.TypeTotal = 0;
        pane.TagMode = true; pane.TagName = tagName;
        pane.Selection.Clear();
        pane.Items = Array.Empty<FileItem>();
        pane.Cursor = null;
        if (sidebarId is not null) SelectedSidebarId = sidebarId;
        OnPropertyChanged(nameof(Detail));

        var cts = new CancellationTokenSource();
        _tagCts = cts;
        List<FileItem> items;
        try
        {
            items = await Task.Run(() => TagService.FilesWithTag(tagName, cts.Token), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch { items = new(); }

        if (cts.IsCancellationRequested || !pane.TagMode || pane.TagName != tagName) return;
        pane.Items = items;
        pane.Cursor = items.FirstOrDefault()?.Path;
    }

    // ── 가상 목록: 종류별 내역 (디스크 팝업 "파일 계산") ──────────────────

    public void ShowTypeBreakdown(string name, int totalCount, IReadOnlyList<(string Path, long Size)> files)
    {
        _searchCts?.Cancel();
        _recentsCts?.Cancel();
        _tagCts?.Cancel();
        _listCts?.Cancel();
        var pane = Detail;
        pane.SearchMode = false; pane.RecentsMode = false; pane.TagMode = false; pane.TagName = null;
        pane.TypeMode = true; pane.TypeName = name; pane.TypeTotal = totalCount;
        pane.Selection.Clear();
        pane.Items = Array.Empty<FileItem>();
        pane.Cursor = null;
        _typeEntries = files.ToList();   // 크기순 정렬 완료 상태
        _typeLoaded = 0;
        AppendNextTypePage(pane);
        pane.Cursor = pane.Items.FirstOrDefault()?.Path;
        SelectedSidebarId = null;
        OnPropertyChanged(nameof(Detail));
    }

    /// <summary>행이 화면에 나타날 때 호출 — 무한 스크롤 페이징.</summary>
    public void LoadMoreTypeItemsIfNeeded(int currentIndex)
    {
        var pane = Detail;
        if (!pane.TypeMode) return;
        if (currentIndex < pane.Items.Count - 100) return;
        if (_typeLoaded >= _typeEntries.Count) return;
        AppendNextTypePage(pane);
    }

    private void AppendNextTypePage(PaneTab pane)
    {
        var page = _typeEntries.Skip(_typeLoaded).Take(TypePageSize).ToList();
        if (page.Count == 0) return;
        var category = pane.TypeName;
        var added = page.Select(e => new FileItem
        {
            Path = e.Path,
            Name = Path.GetFileName(e.Path),
            IsDirectory = false,
            IsSymlink = false,
            IsHidden = false,
            Size = e.Size,
            Modified = DateTime.MinValue,
            Ext = Path.GetExtension(e.Path).TrimStart('.').ToLowerInvariant(),
            IsParent = false,
        }).ToList();
        var list = pane.Items.ToList();
        list.AddRange(added);
        pane.Items = list;
        _typeLoaded += page.Count;
        EnrichTypeItems(added.Select(a => a.Path).ToList(), category);
    }

    /// <summary>페이지 항목의 수정일·생성일·종류를 백그라운드 조회 후 병합.</summary>
    private async void EnrichTypeItems(List<string> paths, string? category)
    {
        var enriched = await Task.Run(() =>
        {
            var result = new Dictionary<string, (DateTime Modified, DateTime Created, string TypeName)>(PathCmp);
            foreach (var p in paths)
            {
                try
                {
                    var fi = new FileInfo(p);
                    if (!fi.Exists) continue;
                    result[p] = (fi.LastWriteTime, fi.CreationTime, ShellInterop.GetTypeName(p, false));
                }
                catch { }
            }
            return result;
        });
        var pane = Detail;
        if (!pane.TypeMode || pane.TypeName != category) return;
        var list = pane.Items.Select(i =>
        {
            if (!enriched.TryGetValue(i.Path, out var meta)) return i;
            return new FileItem
            {
                Path = i.Path, Name = i.Name, IsDirectory = i.IsDirectory, IsSymlink = i.IsSymlink,
                IsHidden = i.IsHidden, Size = i.Size, Modified = meta.Modified, Ext = i.Ext,
                IsParent = i.IsParent, Created = meta.Created, TypeName = meta.TypeName,
            };
        }).ToList();
        pane.Items = list;
    }

    /// <summary>경로 표시줄용 — "파일 계산 — 기타 (크기순 · 500개 로드됨 / 전체 131,170개)".</summary>
    public string TypeStatusText =>
        $"{Detail.TabTitle} (크기순 · {_typeLoaded:N0}개 로드됨 / 전체 {Detail.TypeTotal:N0}개)";

    // ── 가상 목록: 검색 ──────────────────────────────────────────────────

    public async void UpdateSearch(string query)
    {
        var pane = Detail;
        pane.Filter = query;
        var needle = query.Trim().ToLowerInvariant();
        _searchCts?.Cancel();

        if (needle.Length == 0)
        {
            pane.SearchMode = false;
            ReloadDetail();
            pane.Cursor = pane.Items.FirstOrDefault()?.Path;
            OnPropertyChanged(nameof(Detail));
            return;
        }

        _listCts?.Cancel();
        _tagCts?.Cancel();
        _recentsCts?.Cancel();
        pane.TagMode = false; pane.TypeMode = false; pane.RecentsMode = false;
        pane.TagName = null; pane.TypeName = null; pane.TypeTotal = 0;
        pane.SearchMode = true;
        pane.Selection.Clear();
        pane.Items = Array.Empty<FileItem>();
        OnPropertyChanged(nameof(Detail));

        var root = SelectedFolder;
        var showHidden = ShowHidden;
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        List<FileItem> results;
        try
        {
            results = await Task.Run(() => FileSystemService.SearchRecursive(root, needle, showHidden, 1000, cts.Token), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch { results = new(); }

        // 3중 가드: 취소 안 됨 && 같은 폴더 && 같은 질의
        if (cts.IsCancellationRequested) return;
        if (!PathEquals(SelectedFolder, root)) return;
        if (Detail.Filter.Trim().ToLowerInvariant() != needle) return;
        Detail.Items = results;
        Detail.Cursor = results.FirstOrDefault()?.Path;
    }

    // ── 키보드: 커서/선택 ────────────────────────────────────────────────

    private int CursorIndex()
    {
        var pane = Detail;
        if (pane.Cursor is null) return -1;
        for (int i = 0; i < pane.Items.Count; i++)
            if (PathEquals(pane.Items[i].Path, pane.Cursor)) return i;
        return -1;
    }

    /// <summary>일반 화살표는 선택 해제 + 앵커=커서.</summary>
    public void MoveCursor(int delta)
    {
        var pane = Detail;
        if (pane.Items.Count == 0) return;
        var idx = Math.Clamp(CursorIndex() + delta, 0, pane.Items.Count - 1);
        pane.Cursor = pane.Items[idx].Path;
        pane.Selection.Clear();
        pane.SelectionAnchor = pane.Cursor;
        pane.NotifySelectionChanged();
    }

    public void CursorToTop()
    {
        var pane = Detail;
        if (pane.Items.Count == 0) return;
        pane.Cursor = pane.Items[0].Path;
        pane.Selection.Clear();
        pane.SelectionAnchor = pane.Cursor;
        pane.NotifySelectionChanged();
    }

    public void CursorToBottom()
    {
        var pane = Detail;
        if (pane.Items.Count == 0) return;
        pane.Cursor = pane.Items[^1].Path;
        pane.Selection.Clear();
        pane.SelectionAnchor = pane.Cursor;
        pane.NotifySelectionChanged();
    }

    /// <summary>Shift+화살표 — 앵커~커서 구간 전체 선택.</summary>
    public void ExtendCursor(int delta)
    {
        var pane = Detail;
        if (pane.Items.Count == 0) return;
        pane.SelectionAnchor ??= pane.Cursor ?? pane.Items[0].Path;
        var idx = Math.Clamp(CursorIndex() + delta, 0, pane.Items.Count - 1);
        pane.Cursor = pane.Items[idx].Path;
        ApplyAnchorRange();
    }

    public void ExtendCursorToTop()
    {
        var pane = Detail;
        if (pane.Items.Count == 0) return;
        pane.SelectionAnchor ??= pane.Cursor ?? pane.Items[0].Path;
        pane.Cursor = pane.Items[0].Path;
        ApplyAnchorRange();
    }

    public void ExtendCursorToBottom()
    {
        var pane = Detail;
        if (pane.Items.Count == 0) return;
        pane.SelectionAnchor ??= pane.Cursor ?? pane.Items[0].Path;
        pane.Cursor = pane.Items[^1].Path;
        ApplyAnchorRange();
    }

    private void ApplyAnchorRange()
    {
        var pane = Detail;
        var anchorIdx = -1;
        var cursorIdx = CursorIndex();
        for (int i = 0; i < pane.Items.Count; i++)
            if (PathEquals(pane.Items[i].Path, pane.SelectionAnchor ?? "")) { anchorIdx = i; break; }
        if (anchorIdx < 0 || cursorIdx < 0) return;
        var lo = Math.Min(anchorIdx, cursorIdx);
        var hi = Math.Max(anchorIdx, cursorIdx);
        pane.Selection.Clear();
        for (int i = lo; i <= hi; i++)
            if (!pane.Items[i].IsParent) pane.Selection.Add(pane.Items[i].Path);
        pane.NotifySelectionChanged();
    }

    /// <summary>마우스 드래그 선택 — lo..hi 전체 선택, 커서=hi, 앵커=lo.</summary>
    public void SelectRange(int fromIndex, int toIndex)
    {
        var pane = Detail;
        if (pane.Items.Count == 0) return;
        var lo = Math.Clamp(Math.Min(fromIndex, toIndex), 0, pane.Items.Count - 1);
        var hi = Math.Clamp(Math.Max(fromIndex, toIndex), 0, pane.Items.Count - 1);
        pane.Selection.Clear();
        for (int i = lo; i <= hi; i++)
            if (!pane.Items[i].IsParent) pane.Selection.Add(pane.Items[i].Path);
        pane.Cursor = pane.Items[hi].Path;
        pane.SelectionAnchor = pane.Items[lo].Path;
        pane.NotifySelectionChanged();
    }

    public void OpenCursorItem()
    {
        var item = CurrentItem();
        if (item is not null) Open(item);
    }

    /// <summary>Ctrl+A — 전체 선택.</summary>
    public void SelectAll()
    {
        var pane = Detail;
        pane.Selection.Clear();
        foreach (var item in pane.Items)
            if (!item.IsParent) pane.Selection.Add(item.Path);
        pane.NotifySelectionChanged();
    }

    public FileItem? CurrentItem()
    {
        var pane = Detail;
        if (pane.Cursor is not null)
        {
            var item = pane.Items.FirstOrDefault(i => PathEquals(i.Path, pane.Cursor));
            if (item is not null) return item;
        }
        return pane.Items.FirstOrDefault();
    }

    // ── type-select (글자 입력 → 항목 점프) ─────────────────────────────

    private string _typeSelectBuffer = "";
    private string _typeSelectRaw = "";
    private DateTime _typeSelectLast = DateTime.MinValue;
    private DispatcherTimer? _typeSelectHideTimer;
    private static readonly TimeSpan TypeSelectResetInterval = TimeSpan.FromSeconds(1.0);
    private static readonly TimeSpan TypeSelectHudHide = TimeSpan.FromSeconds(1.2);

    /// <summary>true = 이벤트 소비. textInputActive면 호출하지 말 것 (뷰 책임).</summary>
    public bool TypeSelect(string chars)
    {
        if (chars.Length == 0) return false;
        var first = chars[0];
        if (!char.IsLetterOrDigit(first)) return false;
        var pane = Detail;
        if (pane.Items.Count == 0) return true;

        var now = DateTime.Now;
        var expired = now - _typeSelectLast > TypeSelectResetInterval;
        _typeSelectLast = now;

        var key = JamoKey(chars);
        if (expired) { _typeSelectBuffer = ""; _typeSelectRaw = ""; }

        int targetIdx = -1;
        if (!expired && _typeSelectBuffer.Length > 0 && _typeSelectBuffer == key)
        {
            // 같은 키 반복 — 같은 접두어 일치 항목 순환
            var cursorIdx = CursorIndex();
            var matches = new List<int>();
            for (int i = 0; i < pane.Items.Count; i++)
                if (!pane.Items[i].IsParent && JamoKey(pane.Items[i].Name).StartsWith(_typeSelectBuffer, StringComparison.Ordinal))
                    matches.Add(i);
            if (matches.Count > 0)
            {
                targetIdx = matches.FirstOrDefault(i => i > cursorIdx, matches[0]);
            }
        }
        else
        {
            _typeSelectBuffer += key;
            _typeSelectRaw += chars;
            // 접두어 일치 첫 항목
            for (int i = 0; i < pane.Items.Count; i++)
            {
                if (pane.Items[i].IsParent) continue;
                if (JamoKey(pane.Items[i].Name).StartsWith(_typeSelectBuffer, StringComparison.Ordinal)) { targetIdx = i; break; }
            }
            if (targetIdx < 0)
            {
                // 사전순 후속 항목: jamoKey(name) > needle 인 최소, 없으면 마지막
                int best = -1;
                string? bestKey = null;
                for (int i = 0; i < pane.Items.Count; i++)
                {
                    if (pane.Items[i].IsParent) continue;
                    var k = JamoKey(pane.Items[i].Name);
                    if (string.CompareOrdinal(k, _typeSelectBuffer) > 0 && (bestKey is null || string.CompareOrdinal(k, bestKey) < 0))
                    { best = i; bestKey = k; }
                }
                targetIdx = best >= 0 ? best : pane.Items.Count - 1;
            }
        }

        if (targetIdx >= 0)
        {
            pane.Cursor = pane.Items[targetIdx].Path;
            pane.Selection.Clear();
            pane.SelectionAnchor = pane.Cursor;
            pane.NotifySelectionChanged();
        }

        TypeSelectDisplay = _typeSelectRaw.Length > 0 ? _typeSelectRaw : chars;
        if (_typeSelectHideTimer is null)
        {
            _typeSelectHideTimer = new DispatcherTimer { Interval = TypeSelectHudHide };
            _typeSelectHideTimer.Tick += TypeSelectHideTick;   // 구독은 1회만 (키 입력마다 누적 금지)
        }
        _typeSelectHideTimer.Stop();
        _typeSelectHideTimer.Start();
        return true;
    }

    private void TypeSelectHideTick(object? sender, EventArgs e)
    {
        _typeSelectHideTimer?.Stop();
        TypeSelectDisplay = null;
    }

    private static readonly string[] Choseong =
        { "ㄱ","ㄲ","ㄴ","ㄷ","ㄸ","ㄹ","ㅁ","ㅂ","ㅃ","ㅅ","ㅆ","ㅇ","ㅈ","ㅉ","ㅊ","ㅋ","ㅌ","ㅍ","ㅎ" };
    private static readonly string[] Jungseong =
        { "ㅏ","ㅐ","ㅑ","ㅒ","ㅓ","ㅔ","ㅕ","ㅖ","ㅗ","ㅗㅏ","ㅗㅐ","ㅗㅣ","ㅛ","ㅜ","ㅜㅓ","ㅜㅔ","ㅜㅣ","ㅠ","ㅡ","ㅡㅣ","ㅣ" };
    private static readonly string[] Jongseong =
        { "","ㄱ","ㄲ","ㄱㅅ","ㄴ","ㄴㅈ","ㄴㅎ","ㄷ","ㄹ","ㄹㄱ","ㄹㅁ","ㄹㅂ","ㄹㅅ","ㄹㅌ","ㄹㅍ","ㄹㅎ","ㅁ","ㅂ","ㅂㅅ","ㅅ","ㅆ","ㅇ","ㅈ","ㅊ","ㅋ","ㅌ","ㅍ","ㅎ" };
    private static readonly Dictionary<char, string> CompatJamoDecompose = new()
    {
        ['ㅘ'] = "ㅗㅏ", ['ㅙ'] = "ㅗㅐ", ['ㅚ'] = "ㅗㅣ", ['ㅝ'] = "ㅜㅓ", ['ㅞ'] = "ㅜㅔ", ['ㅟ'] = "ㅜㅣ", ['ㅢ'] = "ㅡㅣ",
        ['ㄳ'] = "ㄱㅅ", ['ㄵ'] = "ㄴㅈ", ['ㄶ'] = "ㄴㅎ", ['ㄺ'] = "ㄹㄱ", ['ㄻ'] = "ㄹㅁ", ['ㄼ'] = "ㄹㅂ",
        ['ㄽ'] = "ㄹㅅ", ['ㄾ'] = "ㄹㅌ", ['ㄿ'] = "ㄹㅍ", ['ㅀ'] = "ㄹㅎ",
    };

    /// <summary>비교 키: NFC + 소문자 + 한글 음절을 키 입력 단위 자모로 분해.</summary>
    public static string JamoKey(string s)
    {
        var normalized = s.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
        var sb = new System.Text.StringBuilder(normalized.Length * 2);
        foreach (var ch in normalized)
        {
            if (ch >= '가' && ch <= '힣')
            {
                var idx = ch - 0xAC00;
                sb.Append(Choseong[idx / 588]);
                sb.Append(Jungseong[idx % 588 / 28]);
                sb.Append(Jongseong[idx % 28]);
            }
            else if (CompatJamoDecompose.TryGetValue(ch, out var dec))
            {
                sb.Append(dec);
            }
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    // ── 사이드바 키보드 ──────────────────────────────────────────────────

    public void ToggleFocusedPane()
    {
        FocusedPane = FocusedPane == FocusPane.Sidebar ? FocusPane.Detail : FocusPane.Sidebar;
        if (FocusedPane == FocusPane.Sidebar && SelectedSidebarId is null)
            SelectedSidebarId = SidebarItemMatching(SelectedFolder) ?? VisibleSidebarItems().FirstOrDefault()?.Id;
    }

    /// <summary>펼침 상태를 반영한 위→아래 평탄화 목록.</summary>
    public List<SidebarItem> VisibleSidebarItems()
    {
        var result = new List<SidebarItem>();
        foreach (var section in Sections)
            foreach (var item in section.Items)
                Flatten(item, result);
        return result;

        static void Flatten(SidebarItem item, List<SidebarItem> into)
        {
            into.Add(item);
            if (item.IsExpanded && item.Children is not null)
                foreach (var child in item.Children)
                    Flatten(child, into);
        }
    }

    /// <summary>↑↓ — 강조 이동 + 즉시 그 행으로 탐색 (Finder식).</summary>
    public void MoveSidebarSelection(int delta)
    {
        var visible = VisibleSidebarItems();
        if (visible.Count == 0) return;
        var idx = visible.FindIndex(i => i.Id == SelectedSidebarId);
        idx = Math.Clamp(idx + delta, 0, visible.Count - 1);
        var item = visible[idx];
        SelectedSidebarId = item.Id;
        ActivateSidebar(item);
        SelectedSidebarId = item.Id;   // activate가 강조를 바꿨어도 키보드 위치 유지
    }

    /// <summary>→ — 펼치기 / 이미 펼쳐졌으면 첫 자식으로.</summary>
    public void ExpandSidebarSelection()
    {
        var visible = VisibleSidebarItems();
        var item = visible.FirstOrDefault(i => i.Id == SelectedSidebarId);
        if (item is null) return;
        if (item.CanExpand && !item.IsExpanded) ToggleExpand(item);
        else if (item.IsExpanded && item.Children is { Count: > 0 }) MoveSidebarSelection(+1);
    }

    /// <summary>← — 접기 / 리프면 부모 행으로.</summary>
    public void CollapseSidebarSelection()
    {
        var visible = VisibleSidebarItems();
        var item = visible.FirstOrDefault(i => i.Id == SelectedSidebarId);
        if (item is null) return;
        if (item.IsExpanded) { item.IsExpanded = false; return; }
        // 부모 찾기: 자기보다 위에 있고 depth가 1 작은 행
        var idx = visible.FindIndex(i => i.Id == item.Id);
        for (int i = idx - 1; i >= 0; i--)
        {
            if (visible[i].Depth == item.Depth - 1)
            {
                SelectedSidebarId = visible[i].Id;
                ActivateSidebar(visible[i]);
                SelectedSidebarId = visible[i].Id;
                return;
            }
        }
    }

    public void ActivateSelectedSidebar()
    {
        var item = VisibleSidebarItems().FirstOrDefault(i => i.Id == SelectedSidebarId);
        if (item is null) return;
        ActivateSidebar(item);
        FocusedPane = FocusPane.Detail;
    }

    public void ToggleExpand(SidebarItem item)
    {
        if (!item.CanExpand) return;
        item.IsExpanded = !item.IsExpanded;
        if (item.IsExpanded) item.LoadChildren(ShowHidden);
        SelectedSidebarId ??= SidebarItemMatching(SelectedFolder);
    }

    /// <summary>url 일치 행 (computer 아닌 행 우선)의 id.</summary>
    public Guid? SidebarItemMatching(string url)
    {
        SidebarItem? fallback = null;
        foreach (var item in AllSidebarItems())
        {
            if (item.Path is null || !PathEquals(item.Path, url)) continue;
            if (item.Kind != SidebarItem.ItemKind.Computer) return item.Id;
            fallback ??= item;
        }
        return fallback?.Id;
    }

    private IEnumerable<SidebarItem> AllSidebarItems()
    {
        foreach (var section in Sections)
            foreach (var item in section.Items)
                foreach (var x in Walk(item))
                    yield return x;

        static IEnumerable<SidebarItem> Walk(SidebarItem item)
        {
            yield return item;
            if (item.Children is null) yield break;
            foreach (var child in item.Children)
                foreach (var x in Walk(child))
                    yield return x;
        }
    }

    /// <summary>해당 url 노드의 트리 갱신 (파일 작업 후).</summary>
    private void RefreshSidebar(string url)
    {
        foreach (var item in AllSidebarItems().ToList())
        {
            if (item.Path is null || !PathEquals(item.Path, url)) continue;
            item.Children = null;
            if (item.IsExpanded) item.LoadChildren(ShowHidden);
        }
    }

    private void RefreshAllSidebarChildren()
    {
        foreach (var item in AllSidebarItems().ToList())
        {
            if (item.Children is null) continue;
            item.Children = null;
            if (item.IsExpanded) item.LoadChildren(ShowHidden);
        }
    }

    // ── 확인 대화상자 키보드 ─────────────────────────────────────────────

    public void MoveConfirmFocus(int delta) => ConfirmFocus = ((ConfirmFocus + delta) % 2 + 2) % 2;

    public void ExecuteConfirmFocus() => ExecuteConfirm(ConfirmFocus);

    public void ExecuteConfirm(int index)
    {
        var request = Confirm;
        Confirm = null;
        if (index == 0) request?.Action();
    }

    public void CancelConfirm() => Confirm = null;

    // ── 폴더 용량 계산 ───────────────────────────────────────────────────

    public async void ComputeFolderSizes()
    {
        _sizeCts?.Cancel();
        if (!CalculateFolderSizes) return;
        var pane = Detail;
        var dir = pane.Directory;
        var targets = pane.Items.Where(i => i.IsDirectory && !i.IsSymlink && !i.IsParent).ToList();
        if (targets.Count == 0) return;

        // 캐시 히트는 즉시 반영
        var hits = targets.Where(t => _folderSizeCache.ContainsKey(t.Path)).ToList();
        if (hits.Count > 0)
            ApplyFolderSizes(pane, hits.ToDictionary(t => t.Path, t => _folderSizeCache[t.Path], PathCmp));

        var misses = targets.Where(t => !_folderSizeCache.ContainsKey(t.Path)).Select(t => t.Path).ToList();
        if (misses.Count == 0) return;

        var cts = new CancellationTokenSource();
        _sizeCts = cts;
        var computed = await Task.Run(() =>
        {
            var result = new Dictionary<string, long>(PathCmp);
            try
            {
                Parallel.ForEach(misses, new ParallelOptions { CancellationToken = cts.Token, MaxDegreeOfParallelism = 4 }, path =>
                {
                    var size = FileSystemService.FolderSize(path, cts.Token);
                    lock (result) result[path] = size;
                });
            }
            catch (OperationCanceledException) { }
            return result;
        });

        if (cts.IsCancellationRequested || !PathEquals(Detail.Directory, dir)) return;
        foreach (var (k, v) in computed) _folderSizeCache[k] = v;
        ApplyFolderSizes(pane, computed);
    }

    private static void ApplyFolderSizes(PaneTab pane, Dictionary<string, long> sizes)
    {
        if (sizes.Count == 0) return;
        foreach (var item in pane.RawItems)
            if (sizes.TryGetValue(item.Path, out var s)) item.Size = s;
        var list = pane.Items.ToList();
        foreach (var item in list)
            if (sizes.TryGetValue(item.Path, out var s)) item.Size = s;
        pane.Items = list;   // 재렌더 1회
    }

    // ── 클립보드 ─────────────────────────────────────────────────────────

    public void CopySelection()
    {
        var targets = Detail.ActionTargets();
        if (targets.Count == 0) return;
        var paths = targets.Select(t => t.Path).ToList();
        InternalClipboard = new ClipboardState(paths, IsCut: false);
        SystemActions.WriteFilesToClipboard(paths, cut: false);
        _clipboardSequence = SystemActions.ClipboardSequenceNumber();
    }

    public void CutSelection()
    {
        var targets = Detail.ActionTargets();
        if (targets.Count == 0) return;
        var paths = targets.Select(t => t.Path).ToList();
        InternalClipboard = new ClipboardState(paths, IsCut: true);
        SystemActions.WriteFilesToClipboard(paths, cut: true);
        _clipboardSequence = SystemActions.ClipboardSequenceNumber();
    }

    public void CopySelectedPath()
    {
        var targets = Detail.ActionTargets();
        if (targets.Count == 0) return;
        try { System.Windows.Clipboard.SetText(string.Join("\n", targets.Select(t => t.Path))); }
        catch { }
    }

    public void Paste()
    {
        var hasExternal = SystemActions.TryReadClipboardFiles(out var externalFiles, out var externalCut);
        var weOwnPasteboard = SystemActions.ClipboardSequenceNumber() == _clipboardSequence;

        if (hasExternal && !weOwnPasteboard)
        {
            // 남이 쓴 클립보드 — 잘라내기 의미까지 존중 (탐색기 상호 운용)
            RunTransfer(externalFiles, SelectedFolder, move: externalCut, clearClipboardOnSuccess: false);
            return;
        }
        if (InternalClipboard is { } clip)
        {
            RunTransfer(clip.Paths, SelectedFolder, move: clip.IsCut, clearClipboardOnSuccess: true);
            return;
        }
        if (hasExternal)
            RunTransfer(externalFiles, SelectedFolder, move: false, clearClipboardOnSuccess: false);
    }

    // ── 드래그&드롭 / 전송 실행 ──────────────────────────────────────────

    /// <summary>앱 내 폴더에 드롭. 기본 이동, Ctrl 누르면 복사.</summary>
    public void DropFiles(IReadOnlyList<string> paths, string ontoFolder, bool copy)
    {
        var dest = Standardize(ontoFolder);
        var valid = paths.Where(src =>
        {
            var s = Standardize(src);
            if (PathEquals(s, dest)) return false;                                              // 자기 자신
            if (dest.StartsWith(s + "\\", StringComparison.OrdinalIgnoreCase)) return false;    // 자기 하위 트리
            if (!copy && PathEquals(Path.GetDirectoryName(s.TrimEnd('\\')) ?? "", dest)) return false;  // 이동인데 같은 폴더
            return true;
        }).ToList();
        RunTransfer(valid, dest, move: !copy);
    }

    private async void RunTransfer(IReadOnlyList<string> urls, string to, bool move, bool clearClipboardOnSuccess = false)
    {
        if (urls.Count == 0) return;
        var progress = new OperationProgress(move ? "이동 중…" : "복사 중…");
        Sheet = new AppSheet.Progress(progress);
        var sourceParents = urls.Select(u => Path.GetDirectoryName(u.TrimEnd('\\')) ?? "")
            .Where(p => p.Length > 0).Distinct(PathCmp).ToList();

        var failures = await FileOperations.Transfer(urls, to, move, progress);

        DismissProgress(progress);
        if (clearClipboardOnSuccess && move && failures.Count == 0)
            InternalClipboard = null;
        ReloadDetail();
        RefreshSidebar(to);
        foreach (var p in sourceParents) RefreshSidebar(p);
        if (failures.Count > 0)
            ErrorMessage = string.Join("\n", failures);
    }

    /// <summary>자기 progress일 때만 시트 닫기 (다른 작업의 진행 시트를 덮지 않게).</summary>
    private void DismissProgress(OperationProgress progress)
    {
        if (Sheet is AppSheet.Progress p && ReferenceEquals(p.Op, progress))
            Sheet = null;
    }

    // ── 파일 작업 ────────────────────────────────────────────────────────

    /// <summary>Space/F3 — 미리보기 (Windows: 자체 뷰어 시트).</summary>
    public void ViewSelected()
    {
        var item = CurrentItem();
        if (item is not null) Sheet = new AppSheet.Viewer(item);
    }

    /// <summary>F4 — 기본 앱으로 열기.</summary>
    public void EditSelected()
    {
        var item = CurrentItem();
        if (item is not null) SystemActions.Open(item.Path);
    }

    public void OpenSelected()
    {
        var item = CurrentItem();
        if (item is not null) Open(item);
    }

    public void RequestNewFolder() => Sheet = new AppSheet.NewFolder();
    public void RequestGoToFolder() => Sheet = new AppSheet.GoToFolder();

    public void RequestRename()
    {
        var item = CurrentItem();
        if (item is not null) Sheet = new AppSheet.Rename(item);
    }

    public void CreateFolder(string named)
    {
        var name = named.Trim();
        if (name.Length == 0) return;
        var safe = WindowsName.Sanitize(name);
        try
        {
            Directory.CreateDirectory(Path.Combine(SelectedFolder, safe));
            ReloadDetail();
            RefreshSidebar(SelectedFolder);
            CursorToItem(safe);
            if (safe != name)
                InfoMessage = $"윈도우 호환을 위해 폴더명을 “{safe}”(으)로 저장했습니다.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"폴더를 만들 수 없습니다: {ex.Message}";
        }
    }

    public void RenameItem(FileItem item, string to)
    {
        var name = to.Trim();
        if (name.Length == 0 || name == item.Name) return;
        var safe = WindowsName.Sanitize(name);
        if (safe == item.Name) return;
        try
        {
            var dest = Path.Combine(Path.GetDirectoryName(item.Path.TrimEnd('\\')) ?? "", safe);
            if (item.IsDirectory) Directory.Move(item.Path, dest);
            else File.Move(item.Path, dest);
            ReloadDetail();
            RefreshSidebar(Path.GetDirectoryName(item.Path.TrimEnd('\\')) ?? SelectedFolder);
            CursorToItem(safe);
            if (safe != name)
                InfoMessage = $"윈도우 호환을 위해 이름을 “{safe}”(으)로 저장했습니다.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"이름을 바꿀 수 없습니다: {ex.Message}";
        }
    }

    private void CursorToItem(string named)
    {
        var item = Detail.Items.FirstOrDefault(i => string.Equals(i.Name, named, StringComparison.OrdinalIgnoreCase));
        if (item is not null) Detail.Cursor = item.Path;
    }

    /// <summary>Ctrl+D — "{이름} copy" 복제.</summary>
    public async void Duplicate()
    {
        var targets = Detail.ActionTargets();
        if (targets.Count == 0) return;
        var failures = new List<string>();
        string? lastCopy = null;

        foreach (var item in targets)
        {
            try
            {
                var dir = Path.GetDirectoryName(item.Path.TrimEnd('\\')) ?? SelectedFolder;
                var stem = item.IsDirectory ? item.Name : Path.GetFileNameWithoutExtension(item.Name);
                var ext = item.IsDirectory ? "" : Path.GetExtension(item.Name);
                if (stem.Length == 0) { stem = item.Name; ext = ""; }   // ".gitignore" 같은 도트파일
                var baseName = WindowsName.Sanitize(stem);
                var destPath = UniquePath(dir, $"{baseName} copy{ext}");
                var src = item.Path;
                await Task.Run(() => CopyEntry(src, destPath));
                lastCopy = destPath;
            }
            catch (Exception ex)
            {
                failures.Add($"{item.Name}: {ex.Message}");
            }
        }

        ReloadDetail();
        if (lastCopy is not null) CursorToItem(Path.GetFileName(lastCopy));
        if (failures.Count > 0) ErrorMessage = string.Join("\n", failures);
    }

    public static void CopyEntry(string src, string dest)
    {
        if (Directory.Exists(src))
        {
            Directory.CreateDirectory(dest);
            foreach (var entry in Directory.EnumerateFileSystemEntries(src))
            {
                var name = Path.GetFileName(entry);
                CopyEntry(entry, Path.Combine(dest, name));
            }
        }
        else
        {
            File.Copy(src, dest);
        }
    }

    public static string UniquePath(string dir, string name)
    {
        var candidate = Path.Combine(dir, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        var baseName = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            candidate = Path.Combine(dir, $"{baseName} {i}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Delete — 휴지통 이동 확인.</summary>
    public void RequestDelete()
    {
        var targets = Detail.ActionTargets();
        if (targets.Count == 0) return;
        var names = targets.Count == 1 ? $"“{targets[0].Name}”" : $"{targets.Count}개 항목";
        Confirm = new ConfirmRequest
        {
            Title = "휴지통으로 이동",
            Message = $"{names}을(를) 휴지통으로 옮기시겠습니까?",
            ConfirmTitle = "휴지통으로 이동",
            IsDestructive = true,
            Action = () => PerformDelete(targets),
        };
    }

    private async void PerformDelete(List<FileItem> targets)
    {
        var failures = new List<string>();
        var removed = new List<string>();
        await Task.Run(() =>
        {
            foreach (var item in targets)
            {
                if (ShellInterop.MoveToRecycleBin(new[] { item.Path })) removed.Add(item.Path);
                else failures.Add($"{item.Name}: 휴지통으로 옮길 수 없습니다");
            }
        });

        var pane = Detail;
        if (pane.IsVirtualMode)
            RemoveFromListing(removed);
        else
        {
            ReloadDetail();
            RefreshSidebar(SelectedFolder);
        }
        if (failures.Count > 0) ErrorMessage = string.Join("\n", failures);
    }

    private void RemoveFromListing(IReadOnlyList<string> urls)
    {
        var pane = Detail;
        var removedSet = new HashSet<string>(urls, PathCmp);
        var oldIndex = CursorIndex();
        var cursorRemoved = pane.Cursor is not null && removedSet.Contains(pane.Cursor);
        var list = pane.Items.Where(i => !removedSet.Contains(i.Path)).ToList();
        pane.RawItems = pane.RawItems.Where(i => !removedSet.Contains(i.Path)).ToList();
        pane.Items = list;
        pane.Selection.RemoveWhere(p => removedSet.Contains(p));
        if (cursorRemoved)
        {
            if (list.Count == 0) pane.Cursor = null;
            else pane.Cursor = list[Math.Clamp(oldIndex, 0, list.Count - 1)].Path;
        }
        pane.NotifySelectionChanged();
    }

    public async void CompressSelected()
    {
        var targets = Detail.ActionTargets();
        if (targets.Count == 0) return;
        var baseName = targets.Count == 1 ? Path.GetFileNameWithoutExtension(targets[0].Name) : "Archive";
        var zipPath = UniquePath(SelectedFolder, baseName + ".zip");
        var progress = new OperationProgress("압축 중…");
        Sheet = new AppSheet.Progress(progress);

        var error = await FileOperations.Compress(targets.Select(t => t.Path).ToList(), zipPath, progress);

        DismissProgress(progress);
        ReloadDetail();
        CursorToItem(Path.GetFileName(zipPath));
        if (error is not null) ErrorMessage = error;
    }

    public async void ExtractSelected()
    {
        var item = CurrentItem();
        if (item is null || !string.Equals(item.Ext, "zip", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "압축을 풀 .zip 파일을 선택하세요.";
            return;
        }
        var progress = new OperationProgress("압축 푸는 중…");
        Sheet = new AppSheet.Progress(progress);

        var error = await FileOperations.Extract(item.Path, SelectedFolder, progress);

        DismissProgress(progress);
        ReloadDetail();
        RefreshSidebar(SelectedFolder);
        if (error is not null) ErrorMessage = error;
    }

    /// <summary>탐색기에서 보기.</summary>
    public void RevealInExplorer()
    {
        var item = CurrentItem();
        ShellInterop.RevealInExplorer(item?.Path ?? SelectedFolder);
    }

    /// <summary>Ctrl+I — 셸 속성 대화상자.</summary>
    public void GetInfoSelection()
    {
        var item = CurrentItem();
        SystemActions.ShowProperties(item?.Path ?? SelectedFolder);
    }

    public void OpenTerminal() => SystemActions.OpenTerminal(SelectedFolder, TerminalApp);

    public void ToggleHidden()
    {
        ShowHidden = !ShowHidden;
        ReloadDetail();
        RefreshAllSidebarChildren();
    }

    public void ToggleViewMode()
    {
        var pane = Detail;
        pane.ViewMode = pane.ViewMode == ViewMode.Full ? ViewMode.Icon : ViewMode.Full;
        _folderViewModes[pane.Directory] = pane.ViewMode;
        SaveFolderViewModes();
    }

    public void SetViewMode(ViewMode mode)
    {
        var pane = Detail;
        if (pane.ViewMode == mode) return;
        pane.ViewMode = mode;
        _folderViewModes[pane.Directory] = mode;
        SaveFolderViewModes();
    }

    public void SetGroupKey(GroupKey key)
    {
        Detail.GroupKey = key;
        Detail.Rebuild(ShowHidden);
        OnPropertyChanged(nameof(Detail));
    }

    public void SetSort(SortKey key, bool? ascending = null)
    {
        var pane = Detail;
        if (pane.SortKey == key && ascending is null)
            pane.SortAscending = !pane.SortAscending;
        else
        {
            pane.SortKey = key;
            pane.SortAscending = ascending ?? true;
        }
        pane.Rebuild(ShowHidden);
        OnPropertyChanged(nameof(Detail));
    }

    public void ShowHelp() => InfoMessage = HelpText;

    // ── 한글 자소(NFD) 파일명 복원 ───────────────────────────────────────

    public async void FixDecomposedNames(bool recursive = false)
    {
        var dir = SelectedFolder;
        var targets = await Task.Run(() => HangulNormalize.Scan(dir, recursive));
        if (targets.Count == 0)
        {
            InfoMessage = recursive
                ? "하위 폴더까지 살펴봤지만 자소가 분리된 한글 파일명이 없습니다."
                : "이 폴더에는 자소가 분리된 한글 파일명이 없습니다.";
            return;
        }
        var scope = recursive ? "하위 폴더까지 포함해 " : "";
        var preview = string.Join("\n", targets.Take(6).Select(t => $"• {t.FixedName}"));
        var more = targets.Count > 6 ? $"\n…외 {targets.Count - 6}개" : "";
        Confirm = new ConfirmRequest
        {
            Title = "한글 파일명 복원",
            Message = $"{scope}자소가 분리된 한글 파일명 {targets.Count}개를 찾았습니다. 정상 형태로 바꾸시겠습니까?\n\n{preview}{more}",
            ConfirmTitle = "복원",
            IsDestructive = false,
            Action = () => PerformHangulFix(targets),
        };
    }

    private async void PerformHangulFix(IReadOnlyList<HangulNormalize.Target> targets)
    {
        var (fixedCount, failures) = await Task.Run(() => HangulNormalize.Fix(targets));
        ReloadDetail();
        if (failures.Count == 0)
        {
            InfoMessage = $"한글 파일명 {fixedCount}개를 복원했습니다.";
        }
        else
        {
            var preview = string.Join("\n", failures.Take(5));
            var more = failures.Count > 5 ? $"\n…외 {failures.Count - 5}개" : "";
            ErrorMessage = $"{fixedCount}개 복원, {failures.Count}개 실패:\n{preview}{more}";
        }
    }

    // ── AI 파일 정리 / 프로그램 제거 / 설정 ──────────────────────────────

    /// <summary>설정 창 열기 — 뷰 레이어가 구독 (Ctrl+,).</summary>
    public event Action? SettingsRequested;
    public void OpenSettings() => SettingsRequested?.Invoke();

    public void RequestAIOrganize()
    {
        var dir = SelectedFolder;
        if (IsProtectedLocation(dir))
        {
            ErrorMessage = IsApplicationsLocation(dir)
                ? "응용 프로그램 폴더는 AI 파일 정리에서 제외됩니다."
                : "시스템 폴더는 AI 파일 정리에서 제외됩니다.";
            return;
        }
        if (IsExcluded(dir))
        {
            ErrorMessage = "이 폴더는 AI 정리 예외 폴더로 지정되어 정리할 수 없습니다.";
            return;
        }
        Sheet = new AppSheet.AiOrganize();
    }

    /// <summary>현재 폴더 최상위 항목명(숨김 제외) — LLM 후보 목록.</summary>
    public List<string> CurrentFolderEntries(int limit = 300)
    {
        return Detail.RawItems
            .Where(i => !i.IsHidden && !i.IsParent)
            .Select(i => i.Name)
            .OrderBy(n => n, NaturalSort.Comparer)
            .Take(limit)
            .ToList();
    }

    /// <summary>AI 계획 실행 — move/delete (delete는 휴지통).</summary>
    public async void ApplyAIPlan(IReadOnlyList<AIPlannedOp> ops)
    {
        var dir = SelectedFolder;
        if (AiOrganizeBlocked(dir))
        {
            ErrorMessage = IsApplicationsLocation(dir) ? "응용 프로그램 폴더는 AI 파일 정리에서 제외됩니다."
                : IsProtectedLocation(dir) ? "시스템 폴더는 AI 파일 정리에서 제외됩니다."
                : "이 폴더는 AI 정리 예외 폴더로 지정되어 정리할 수 없습니다.";
            return;
        }

        int moved = 0, deleted = 0;
        var failures = new List<string>();

        await Task.Run(() =>
        {
            foreach (var op in ops)
            {
                // LLM이 반환한 파일명의 경로 탈출 방지 — 현재 폴더 직속 이름만 허용
                if (op.File.IndexOfAny(new[] { '\\', '/' }) >= 0 || op.File is "." or "..")
                {
                    failures.Add($"{op.File}: 잘못된 파일 이름");
                    continue;
                }
                var src = Path.Combine(dir, op.File);
                if (!File.Exists(src) && !Directory.Exists(src))
                {
                    failures.Add($"{op.File}: 항목 없음");
                    continue;
                }
                try
                {
                    if (op.Kind == "delete")
                    {
                        if (ShellInterop.MoveToRecycleBin(new[] { src })) deleted++;
                        else failures.Add($"{op.File}: 휴지통으로 옮길 수 없습니다");
                    }
                    else if (op.Kind == "move" && op.Destination is { } destRel)
                    {
                        if (!IsSafeDestination(destRel)) { failures.Add($"{op.File}: 잘못된 대상 경로"); continue; }
                        var destDir = Path.Combine(dir, destRel);
                        var destPath = Path.Combine(destDir, op.File);
                        if (PathEquals(src, destPath)) continue;
                        Directory.CreateDirectory(destDir);
                        if (File.Exists(destPath) || Directory.Exists(destPath))
                            destPath = UniquePath(destDir, op.File);
                        if (Directory.Exists(src)) Directory.Move(src, destPath);
                        else File.Move(src, destPath);
                        moved++;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{op.File}: {ex.Message}");
                }
            }
        });

        ReloadDetail();
        RefreshSidebar(dir);
        var parts = new List<string>();
        if (moved > 0) parts.Add($"{moved}개 정리");
        if (deleted > 0) parts.Add($"{deleted}개 휴지통으로 이동");
        var doneText = parts.Count > 0 ? "AI가 " + string.Join(", ", parts) + "했습니다." : "처리한 항목이 없습니다";
        if (failures.Count == 0)
        {
            InfoMessage = doneText;
        }
        else
        {
            var preview = string.Join("\n", failures.Take(5));
            var more = failures.Count > 5 ? $"\n…외 {failures.Count - 5}개" : "";
            ErrorMessage = $"{doneText}\n{failures.Count}개 실패:\n{preview}{more}";
        }
    }

    /// <summary>상대 하위 경로만 허용 (절대 경로/상위 탈출 금지).</summary>
    public static bool IsSafeDestination(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return false;
        if (Path.IsPathRooted(rel)) return false;
        var parts = rel.Replace('/', '\\').Split('\\');
        return parts.All(p => p != ".." && p.Trim().Length > 0);
    }

    public void RequestUninstall() => Sheet = new AppSheet.Uninstall();

    // ── 경로 헬퍼 ────────────────────────────────────────────────────────

    public static string Standardize(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            // 루트("C:\")는 백슬래시 유지, 그 외엔 제거
            if (full.Length > 3 && full.EndsWith('\\')) full = full.TrimEnd('\\');
            return full;
        }
        catch { return path; }
    }

    public static bool PathEquals(string a, string b)
        => string.Equals(Standardize(a), Standardize(b), StringComparison.OrdinalIgnoreCase);

    // ── 도움말 전문 ──────────────────────────────────────────────────────

    public const string HelpText = """
        XFinder — 키보드 단축키 (클릭 없이 항상 동작)

        ↑ ↓ / PageUp·Down / Home·End   커서 이동
        Return          파일 열기 / 폴더 진입
        Ctrl+↓           선택 항목 열기
        Alt+↑ / Backspace  상위 폴더로
        Alt+← Alt+→     뒤로 / 앞으로
        Space (F3)      빠른 보기      F4  기본 앱으로 열기

        Ctrl+C 복사   Ctrl+X 잘라내기   Ctrl+V 붙여넣기
        Ctrl+D 복제   Delete 휴지통으로   F2 이름 변경
        Ctrl+Shift+N 새 폴더   Ctrl+R·F5 새로고침
        Ctrl+H  숨김 파일   Ctrl+Shift+G  폴더로 이동   Ctrl+M  목록/아이콘

        Ctrl+T  새 탭   Ctrl+W  탭 닫기(마지막 탭이면 창 닫기)   Ctrl+Tab / Ctrl+Shift+Tab  탭 전환

        Tab             사이드바 ⇄ 파일 목록 포커스 전환
        사이드바 포커스 시  ↑↓ 이동 · → 펼치기 · ← 접기 · Return 열기
        경로 막대 더블클릭(또는 ✎) → 경로 직접 입력 후 Return

        폴더 우클릭 → 즐겨찾기에 추가 / 제거
        """;
}
