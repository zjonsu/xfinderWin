namespace XFinder.Models;

/// <summary>복사/이동/압축 진행 상태 (진행률 시트와 바인딩).</summary>
public sealed class OperationProgress : ObservableObject
{
    public OperationProgress(string title) { _title = title; }

    private string _title;
    public string Title { get => _title; set => Set(ref _title, value); }

    private string _currentFile = "";
    public string CurrentFile { get => _currentFile; set => Set(ref _currentFile, value); }

    private long _completedUnits;
    public long CompletedUnits
    {
        get => _completedUnits;
        set { if (Set(ref _completedUnits, value)) OnPropertyChanged(nameof(Fraction)); }
    }

    private long _totalUnits;
    public long TotalUnits
    {
        get => _totalUnits;
        set { if (Set(ref _totalUnits, value)) OnPropertyChanged(nameof(Fraction)); }
    }

    private bool _isCancelled;
    /// <summary>취소 버튼 → true; 작업 루프가 폴링해 중단.</summary>
    public bool IsCancelled { get => _isCancelled; set => Set(ref _isCancelled, value); }

    public double Fraction => TotalUnits <= 0 ? 0 : Math.Min(1.0, (double)CompletedUnits / TotalUnits);
}

/// <summary>시트(모달) 라우팅 — 어떤 다이얼로그를 띄울지 단일 상태로 표현.</summary>
public abstract record AppSheet
{
    public sealed record Viewer(FileItem Item) : AppSheet;
    public sealed record GoToFolder : AppSheet;
    public sealed record NewFolder : AppSheet;
    public sealed record Rename(FileItem Item) : AppSheet;
    public sealed record Progress(OperationProgress Op) : AppSheet;
    public sealed record About : AppSheet;
    public sealed record Manual : AppSheet;
    public sealed record Uninstall : AppSheet;          // Windows: 설치된 프로그램 제거 + 잔여 파일 정리
    public sealed record AiOrganize : AppSheet;
}

/// <summary>확인 대화상자 요청.</summary>
public sealed class ConfirmRequest
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Title { get; init; }
    public required string Message { get; init; }
    public required string ConfirmTitle { get; init; }
    public bool IsDestructive { get; init; }
    public required Action Action { get; init; }
}

/// <summary>앱 내부 잘라내기/복사 상태.</summary>
public sealed record ClipboardState(List<string> Paths, bool IsCut);

public enum FocusPane { Sidebar, Detail }

/// <summary>터미널 앱 선택 — Windows 재정의: auto/wt/powershell (저장 키는 mac 그대로).</summary>
public enum TerminalAppChoice { Auto, WindowsTerminal, PowerShell }

public static class TerminalAppChoiceExtensions
{
    public static string Label(this TerminalAppChoice t) => t switch
    {
        TerminalAppChoice.Auto => "자동",
        TerminalAppChoice.WindowsTerminal => "Windows Terminal",
        TerminalAppChoice.PowerShell => "PowerShell",
        _ => "",
    };

    public static string RawValue(this TerminalAppChoice t) => t switch
    {
        TerminalAppChoice.Auto => "auto",
        TerminalAppChoice.WindowsTerminal => "terminal",
        TerminalAppChoice.PowerShell => "iterm",   // mac 저장 키 호환
        _ => "auto",
    };

    public static TerminalAppChoice FromRaw(string? raw) => raw switch
    {
        "terminal" => TerminalAppChoice.WindowsTerminal,
        "iterm" => TerminalAppChoice.PowerShell,
        _ => TerminalAppChoice.Auto,
    };
}

/// <summary>검색창 위치.</summary>
public enum SearchBarPosition { Toolbar, Below }

public static class SearchBarPositionExtensions
{
    public static string Label(this SearchBarPosition p) => p == SearchBarPosition.Toolbar ? "툴바" : "툴바 아래";
    public static string RawValue(this SearchBarPosition p) => p == SearchBarPosition.Toolbar ? "toolbar" : "below";
    public static SearchBarPosition FromRaw(string? raw) => raw == "below" ? SearchBarPosition.Below : SearchBarPosition.Toolbar;
}
