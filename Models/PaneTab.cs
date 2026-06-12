namespace XFinder.Models;

/// <summary>그룹화된 목록의 한 구간: 제목 + items 안에서의 범위 (항목 복사 없이 범위로 가리킴).</summary>
public readonly record struct FileGroup(string Title, int Start, int Count)
{
    public int End => Start + Count;   // exclusive
    public string Id => Title;
}

/// <summary>패널의 탭 하나의 상태: 현재 폴더, 표시 항목, 선택.</summary>
public sealed class PaneTab : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>"내 PC"(드라이브 목록) 가상 경로.</summary>
    public const string ComputerPath = "::COMPUTER";

    private string _directory;
    public string Directory { get => _directory; set => Set(ref _directory, value); }

    /// <summary>이 탭의 뒤로/앞으로 히스토리 — 탭마다 독립.</summary>
    public List<string> History { get; } = new();
    public int HistoryIndex { get; set; } = -1;

    /// <summary>탭 바 색상 팔레트 인덱스 — 생성 순서대로 자동 배정, 탭이 닫혀도 남은 탭 색 유지.</summary>
    public int ColorIndex { get; set; }

    /// <summary>필터 전 원본 디렉터리 목록 (".." 행 제외).</summary>
    public List<FileItem> RawItems { get; set; } = new();

    private IReadOnlyList<FileItem> _items = Array.Empty<FileItem>();
    /// <summary>화면에 실제 표시되는 목록 (필터+정렬 적용).</summary>
    public IReadOnlyList<FileItem> Items { get => _items; set => Set(ref _items, value); }

    /// <summary>다중 선택 (경로 집합 — NTFS 대소문자 무시).</summary>
    public HashSet<string> Selection { get; } = new(StringComparer.OrdinalIgnoreCase);

    private string? _cursor;
    /// <summary>키보드 커서 행 (경로).</summary>
    public string? Cursor { get => _cursor; set => Set(ref _cursor, value); }

    /// <summary>범위 선택(Shift) 기준점.</summary>
    public string? SelectionAnchor { get; set; }

    public SortKey SortKey { get; set; } = SortKey.Name;
    public bool SortAscending { get; set; } = true;

    private string _filter = "";
    public string Filter { get => _filter; set => Set(ref _filter, value); }

    private ViewMode _viewMode = ViewMode.Full;
    public ViewMode ViewMode { get => _viewMode; set => Set(ref _viewMode, value); }

    /// <summary>"다음으로 그룹화" 기준 (None = 평평한 목록). 일반 폴더 목록에서만 적용.</summary>
    public GroupKey GroupKey { get; set; } = GroupKey.None;
    /// <summary>GroupKey != None일 때 Items의 그룹 구간들 (Items는 그룹 순서로 재배열됨).</summary>
    public List<FileGroup> Groups { get; set; } = new();

    private string? _loadError;
    public string? LoadError { get => _loadError; set => Set(ref _loadError, value); }

    /// <summary>Items가 재귀 검색 결과일 때 true (일반 목록 아님).</summary>
    public bool SearchMode { get; set; }
    /// <summary>Items가 "최근 항목" 목록일 때 true.</summary>
    public bool RecentsMode { get; set; }
    /// <summary>Items가 태그 필터 결과일 때 true. TagName이 활성 태그.</summary>
    public bool TagMode { get; set; }
    public string? TagName { get; set; }
    /// <summary>Items가 디스크 팝업 "파일 계산" 종류별 내역일 때 true (크기순 상위 N개).
    /// TypeName은 분류명(문서/이미지/…), TypeTotal은 분류 전체 파일 수.</summary>
    public bool TypeMode { get; set; }
    public string? TypeName { get; set; }
    public int TypeTotal { get; set; }

    public PaneTab(string directory)
    {
        _directory = directory;
    }

    public string Title
    {
        get
        {
            if (Directory == ComputerPath) return "내 PC";
            var name = System.IO.Path.GetFileName(Directory.TrimEnd('\\'));
            return string.IsNullOrEmpty(name) ? Directory.TrimEnd('\\') : name;   // 드라이브 루트 → "C:"
        }
    }

    /// <summary>탭 바·창 제목용 이름 — 가상 목록 모드면 모드 이름, 아니면 폴더 이름.</summary>
    public string TabTitle
    {
        get
        {
            if (RecentsMode) return "최근 항목";
            if (TagMode && TagName is not null) return TagName;
            if (TypeMode && TypeName is not null) return $"파일 계산 — {TypeName}";
            if (SearchMode) return $"검색: {Filter}";
            return Title;
        }
    }

    public bool IsAtRoot =>
        Directory == ComputerPath;

    public bool IsVirtualMode => SearchMode || RecentsMode || TagMode || TypeMode;

    /// <summary>화면에 그릴 그룹 구간 — 그룹화 켜짐 + 가상 모드 아님 + 구간이 현재 Items와 정확히
    /// 일치할 때만 (모드 전환 직후 낡은 구간으로 인한 범위 오류 방지).</summary>
    public List<FileGroup>? ActiveGroups
    {
        get
        {
            if (GroupKey == GroupKey.None || Groups.Count == 0) return null;
            if (IsVirtualMode) return null;
            if (Groups[^1].End != Items.Count) return null;
            return Groups;
        }
    }

    /// <summary>RawItems에서 숨김 필터·텍스트 필터·정렬을 적용해 Items를 다시 만든다.</summary>
    public void Rebuild(bool showHidden)
    {
        // 검색/최근/태그/종류 모드 중에는 Items를 AppModel이 관리한다.
        if (IsVirtualMode) return;
        IEnumerable<FileItem> visible = RawItems;
        if (!showHidden)
            visible = visible.Where(i => !i.IsHidden);
        var needle = Filter.Trim().ToLowerInvariant();
        if (needle.Length > 0)
            visible = visible.Where(i => i.Name.ToLowerInvariant().Contains(needle));
        var sorted = visible.SortedBy(SortKey, SortAscending);

        // 파인더식 상세 목록: ".." 행 없음 (탐색은 사이드바/툴바로).
        Items = sorted;
        RebuildGroups();   // 그룹화가 켜져 있으면 Items를 그룹 순서로 재배열 + 구간 계산

        // 커서 유효성 유지.
        if (Cursor is not null && !Items.Any(i => i.Path == Cursor))
            Cursor = Items.FirstOrDefault()?.Path;
        else if (Cursor is null)
            Cursor = Items.FirstOrDefault()?.Path;

        // 사라진 항목은 선택에서 제거.
        var present = new HashSet<string>(Items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        Selection.RemoveWhere(p => !present.Contains(p));
    }

    /// <summary>Items(사용자 정렬 적용 상태)를 그룹 기준으로 묶어 재배열하고 구간을 만든다.
    /// 그룹 안에서는 기존 정렬 순서 유지.</summary>
    private void RebuildGroups()
    {
        if (GroupKey == GroupKey.None || Items.Count == 0) { Groups = new(); return; }
        var buckets = new Dictionary<string, List<FileItem>>();
        var orderOf = new Dictionary<string, int>();
        foreach (var item in Items)
        {
            var (order, title) = Bucket(GroupKey, item);
            if (!buckets.TryGetValue(title, out var list))
            {
                list = new List<FileItem>();
                buckets[title] = list;
                orderOf[title] = order;
            }
            list.Add(item);
        }
        var titles = buckets.Keys.ToList();
        titles.Sort((x, y) =>
        {
            int a = orderOf[x], b = orderOf[y];
            if (a != b) return a.CompareTo(b);
            return NaturalSort.Compare(x, y);
        });
        var flat = new List<FileItem>(Items.Count);
        var result = new List<FileGroup>();
        foreach (var title in titles)
        {
            int start = flat.Count;
            flat.AddRange(buckets[title]);
            result.Add(new FileGroup(title, start, flat.Count - start));
        }
        Items = flat;
        Groups = result;
    }

    /// <summary>항목이 속할 그룹의 (정렬 순서, 제목). order가 작을수록 위에 표시.</summary>
    private static (int Order, string Title) Bucket(GroupKey key, FileItem item)
    {
        switch (key)
        {
            case GroupKey.None:
                return (0, "");
            case GroupKey.Name:
                // 첫 글자 기준 (한글은 음절 그대로, 영문은 대문자 통일).
                if (item.Name.Length == 0) return (1, "#");
                return (0, item.Name[..1].ToUpperInvariant());
            case GroupKey.Kind:
                if (item.IsDirectory && !item.IsBundle) return (0, "폴더");
                return (1, Format.KindLabel(item));
            case GroupKey.Size:
            {
                if (item.IsDirectory && !item.IsBundle) return (0, "폴더");
                var b = item.Size;
                if (b >= 1L << 30) return (1, "1 GB 이상");
                if (b >= 100L << 20) return (2, "100 MB ~ 1 GB");
                if (b >= 1L << 20) return (3, "1 MB ~ 100 MB");
                return (4, "1 MB 미만");
            }
            case GroupKey.Modified:
                return DateBucket(item.Modified);
            case GroupKey.Created:
                return DateBucket(item.Created);
            default:
                return (0, "");
        }
    }

    /// <summary>날짜를 파인더식 구간으로: 오늘/어제/지난 7일/지난 30일/그 이전은 연도별.</summary>
    private static (int Order, string Title) DateBucket(DateTime date)
    {
        if (date == DateTime.MinValue) return (100_000, "날짜 없음");
        var today = DateTime.Today;
        if (date.Date == today) return (0, "오늘");
        if (date.Date == today.AddDays(-1)) return (1, "어제");
        var age = DateTime.Now - date;
        if (age.TotalDays < 7) return (2, "지난 7일");
        if (age.TotalDays < 30) return (3, "지난 30일");
        return (10_000 - date.Year, $"{date.Year}년");   // 최근 연도가 위로
    }

    /// <summary>작업 대상: 선택된 집합, 없으면 커서 행.</summary>
    public List<FileItem> ActionTargets()
    {
        var marked = Items.Where(i => Selection.Contains(i.Path) && !i.IsParent).ToList();
        if (marked.Count > 0) return marked;
        if (Cursor is not null)
        {
            var item = Items.FirstOrDefault(i => i.Path == Cursor);
            if (item is not null && !item.IsParent) return new List<FileItem> { item };
        }
        return new List<FileItem>();
    }

    public void ToggleMark(string path)
    {
        if (!Selection.Add(path)) Selection.Remove(path);
        OnPropertyChanged(nameof(Selection));
    }

    /// <summary>Selection 집합을 직접 변경한 뒤 UI에 알림.</summary>
    public void NotifySelectionChanged() => OnPropertyChanged(nameof(Selection));
}
