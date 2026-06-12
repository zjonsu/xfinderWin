// mac 소스 대응: Sources/XFinder/Services/HangulNormalize.swift — NFD 자소 분리 한글 파일명 스캔 + NFC 일괄 복원 (Windows는 File.Move로 충분, rename(2) 트릭 불필요)
using System.IO;
using System.Text;

namespace XFinder.Services;

/// <summary>
/// NFD(분해형, ㅎㅏㄴ) 한글 파일명을 NFC(한)로 복원.
/// C# string ==는 서수 비교라 mac 함정 1(정규 동등 비교) 없음 — Normalize 비교가 그대로 동작.
/// Windows 고유 함정: NFD/NFC 이름이 NTFS에서 공존 가능 → 복원 전 대상 존재 확인, 충돌 시 UniqueUrl 회피.
/// </summary>
public static class HangulNormalize
{
    /// <summary>스캔 결과 한 건 — 원본 경로 + 복원된(NFC) 이름.</summary>
    public sealed record Target(string Path, string FixedName);

    /// <summary>
    /// NFD 분해형 한글 이름인지 — ① NFC 결과와 서수 불일치 그리고 ② 한글 결합 자모 포함.
    /// (조건 ②가 없으면 한글 무관한 NFD 악센트 문자까지 잡힘 — 한글만 대상, 원본 동일.)
    /// </summary>
    public static bool IsDecomposed(string name)
    {
        if (name.Length == 0) return false;
        string nfc;
        try { nfc = name.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return false; }   // 잘못된 서로게이트 등
        if (string.Equals(name, nfc, StringComparison.Ordinal)) return false;
        return ContainsConjoiningJamo(name);
    }

    /// <summary>NFC 재조합 결과.</summary>
    public static string Recomposed(string name)
    {
        try { return name.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return name; }
    }

    /// <summary>
    /// 분해형 이름 항목 스캔 — recursive면 전체 트리 (IgnoreInaccessible, 숨김 포함).
    /// 패키지 개념은 Windows에 없으므로 skipsPackageDescendants 조건은 생략 (스펙 §7.2).
    /// </summary>
    public static List<Target> Scan(string directory, bool recursive, CancellationToken ct = default)
    {
        var result = new List<Target>();
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = recursive,
                AttributesToSkip = FileAttributes.None,
            };
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (IsDecomposed(name)) result.Add(new Target(path, Recomposed(name)));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 나열 실패 — 부분 결과 반환 */ }
        return result;
    }

    /// <summary>
    /// 스캔 결과 일괄 복원 — 깊은 경로부터 처리(부모 이름이 먼저 바뀌어 자식 경로가 무효화되는 것 방지).
    /// NFC 동명 항목이 이미 존재하면(NTFS 공존 함정) UniqueUrl 규칙("이름 2.ext")으로 회피.
    /// 반환: (성공 수, "{이름}: {오류}" 실패 목록).
    /// </summary>
    public static (int FixedCount, List<string> Failures) Fix(IReadOnlyList<Target> targets,
                                                              CancellationToken ct = default)
    {
        var ordered = targets
            .OrderByDescending(t => t.Path.Count(c => c == '\\' || c == '/'))
            .ToList();
        int fixedCount = 0;
        var failures = new List<string>();
        foreach (var t in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(t.Path)) ?? "";
            var dest = Path.Combine(dir, t.FixedName);
            try
            {
                if (File.Exists(dest) || Directory.Exists(dest))
                {
                    var stem = Path.GetFileNameWithoutExtension(t.FixedName);
                    var ext = Path.GetExtension(t.FixedName).TrimStart('.');
                    dest = FileOperations.UniqueUrl(dir, stem, ext);
                }
                var error = Rename(t.Path, dest);
                if (error is null) fixedCount++;
                else failures.Add($"{Path.GetFileName(t.Path)}: {error}");
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(t.Path)}: {ex.Message}");
            }
        }
        return (fixedCount, failures);
    }

    /// <summary>저수준 이름 변경 — 성공 null, 실패 시 오류 메시지 (mac rename(at:to:) 대응).
    /// 대소문자만 다른 충돌 등 OS 거부는 메시지 그대로 표시 (스펙 §7.2).</summary>
    public static string? Rename(string sourcePath, string destPath)
    {
        try
        {
            if (Directory.Exists(sourcePath)) Directory.Move(sourcePath, destPath);
            else File.Move(sourcePath, destPath);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>한글 결합 자모 범위(원본 그대로): U+1100–U+11FF, U+A960–U+A97F, U+D7B0–U+D7FF.</summary>
    private static bool ContainsConjoiningJamo(string s)
    {
        foreach (var ch in s)
        {
            if ((ch >= 'ᄀ' && ch <= 'ᇿ')      // Hangul Jamo (conjoining)
                || (ch >= 'ꥠ' && ch <= '꥿')   // Hangul Jamo Extended-A
                || (ch >= 'ힰ' && ch <= '퟿'))  // Hangul Jamo Extended-B
                return true;
        }
        return false;
    }
}
