using XFinder.Services;

namespace XFinder.Models;

/// <summary>파인더식 사이드바의 행 하나. 폴더 행은 선택·트리 펼침 가능, 특수 행(내 PC/휴지통/태그)은 전용 동작.</summary>
public sealed class SidebarItem : ObservableObject
{
    public enum ItemKind
    {
        Folder,      // 실제 디렉터리: 선택 + 펼침
        Computer,    // 내 PC: 드라이브 목록 표시
        Trash,       // 휴지통
        Recents,     // 최근 항목
        Tag,         // 색 태그 — 클릭하면 해당 태그 파일 필터
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; }
    public string Icon { get; }            // 글리프 키 (뷰에서 Segoe Fluent Icons로 매핑)
    public string? Path { get; }           // 대상 디렉터리 (가상 항목은 null 또는 가상 경로)
    public ItemKind Kind { get; }
    public int Depth { get; }              // 트리 들여쓰기 레벨

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    private List<SidebarItem>? _children;
    /// <summary>null = 아직 로드 안 됨.</summary>
    public List<SidebarItem>? Children { get => _children; set => Set(ref _children, value); }

    private bool _mayHaveChildren;
    /// <summary>디스클로저 삼각형 표시 여부.</summary>
    public bool MayHaveChildren { get => _mayHaveChildren; set => Set(ref _mayHaveChildren, value); }

    public SidebarItem(string title, string icon, string? path, ItemKind kind, int depth = 0, bool hasChildren = true)
    {
        Title = title;
        Icon = icon;
        Path = path;
        Kind = kind;
        Depth = depth;
        _mayHaveChildren = hasChildren;
    }

    public bool IsSelectable => Path is not null;
    public bool CanExpand => (Kind == ItemKind.Folder || Kind == ItemKind.Computer) && MayHaveChildren;

    /// <summary>하위 폴더를 한 단계 지연 로드.</summary>
    public void LoadChildren(bool showHidden)
    {
        if (Path is null || Children is not null) return;
        List<string> subs = Kind == ItemKind.Computer
            ? FileSystemService.DriveRoots()
            : FileSystemService.Subfolders(Path, showHidden);
        Children = subs.Select(sub =>
        {
            // 하위 폴더가 실제로 있을 때만 삼각형을 보여주고, 채워진 폴더 아이콘으로 구분.
            var hasKids = FileSystemService.HasSubfolders(sub, showHidden);
            var name = System.IO.Path.GetFileName(sub.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name)) name = sub.TrimEnd('\\');   // 드라이브 루트
            return new SidebarItem(name, hasKids ? "folder.fill" : "folder",
                                   sub, ItemKind.Folder, Depth + 1, hasKids);
        }).ToList();
    }
}

/// <summary>제목 있는 사이드바 그룹 ("즐겨찾기", "위치", …).</summary>
public sealed class SidebarSection
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; }
    public List<SidebarItem> Items { get; set; }

    public SidebarSection(string title, List<SidebarItem> items)
    {
        Title = title;
        Items = items;
    }
}
