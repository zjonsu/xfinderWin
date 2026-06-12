using System.Runtime.InteropServices;

namespace XFinder.Models;

public enum ViewMode
{
    Full,      // 이름/크기/수정일/종류 열 (목록)
    Icon,      // 큰 아이콘 그리드 + 썸네일
}

public static class ViewModeExtensions
{
    public static string Label(this ViewMode m) => m == ViewMode.Full ? "목록" : "아이콘";
}

/// <summary>"다음으로 그룹화" 기준 — 파인더 그룹 보기처럼 목록을 구간(섹션)으로 나눈다.</summary>
public enum GroupKey
{
    None, Name, Kind, Size, Modified, Created,
}

public static class GroupKeyExtensions
{
    public static string Label(this GroupKey k) => k switch
    {
        GroupKey.None => "없음",
        GroupKey.Name => "이름",
        GroupKey.Kind => "종류",
        GroupKey.Size => "크기",
        GroupKey.Modified => "수정일",
        GroupKey.Created => "생성일",
        _ => "",
    };
}

/// <summary>목록 보기의 고정 너비 열 — 이름 열은 나머지 공간 전부라 여기 없음.
/// 문자열 키는 사용자 지정 너비 저장(settings.json) 키.</summary>
public enum ListColumn
{
    Size, Modified, Created, Kind,
}

public static class ListColumnExtensions
{
    public static string Key(this ListColumn c) => c switch
    {
        ListColumn.Size => "size",
        ListColumn.Modified => "modified",
        ListColumn.Created => "created",
        ListColumn.Kind => "kind",
        _ => "",
    };

    /// <summary>기본 너비 (목록 크기 배율 적용 전).</summary>
    public static double DefaultWidth(this ListColumn c) => c switch
    {
        ListColumn.Size => 70,
        ListColumn.Modified => 120,
        ListColumn.Created => 120,
        ListColumn.Kind => 96,
        _ => 100,
    };

    public const double MinWidth = 44;
    public const double MaxWidth = 360;
}

public enum SortKey
{
    Name, Ext, Size, Modified, Created, Kind,
}

public static class SortKeyExtensions
{
    public static string Label(this SortKey k) => k switch
    {
        SortKey.Name => "Name",
        SortKey.Ext => "Ext",
        SortKey.Size => "Size",
        SortKey.Modified => "Date Modified",
        SortKey.Created => "Date Created",
        SortKey.Kind => "Kind",
        _ => "",
    };
}

/// <summary>날짜 열 표시 방식 — 실제 날짜 또는 상대 시간("3시간 전").</summary>
public enum DateDisplayStyle
{
    Absolute, Relative,
}

/// <summary>탐색기와 동일한 자연 정렬 비교 (숫자 묶음 인식, 한글 OK).</summary>
public static class NaturalSort
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    public static int Compare(string a, string b) => StrCmpLogicalW(a, b);

    public static readonly IComparer<string> Comparer = Comparer<string>.Create(Compare);
}

public static class FileItemSorting
{
    /// <summary>정렬 규칙: ".." 먼저 → 폴더 먼저 → 키 기준 (동률이면 이름 자연 정렬).</summary>
    public static List<FileItem> SortedBy(this IEnumerable<FileItem> source, SortKey key, bool ascending)
    {
        var list = source.ToList();
        list.Sort((a, b) =>
        {
            if (a.IsParent != b.IsParent) return a.IsParent ? -1 : 1;
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;

            int result = key switch
            {
                SortKey.Name => NaturalSort.Compare(a.Name, b.Name),
                SortKey.Ext => CompareThenName(NaturalSort.Compare(a.Ext, b.Ext), a, b),
                SortKey.Size => CompareThenName(a.Size.CompareTo(b.Size), a, b),
                SortKey.Modified => CompareThenName(a.Modified.CompareTo(b.Modified), a, b),
                SortKey.Created => CompareThenName(a.Created.CompareTo(b.Created), a, b),
                SortKey.Kind => CompareThenName(NaturalSort.Compare(Format.KindLabel(a), Format.KindLabel(b)), a, b),
                _ => 0,
            };
            return ascending ? result : -result;
        });
        return list;
    }

    private static int CompareThenName(int primary, FileItem a, FileItem b)
        => primary != 0 ? primary : NaturalSort.Compare(a.Name, b.Name);
}
