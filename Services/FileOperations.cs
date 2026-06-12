// mac 소스 대응: Sources/XFinder/Services/FileOperations.swift — 복사/이동/복제(이름 충돌 규칙)·ZIP 압축/해제(/usr/bin/zip·ditto → System.IO.Compression)
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using XFinder.Models;

namespace XFinder.Services;

/// <summary>
/// 파일 작업 — 진행률은 XFinder.Models.OperationProgress(UI 바인딩)로 보고,
/// 취소는 progress.IsCancelled 폴링(항목 경계에서만 — 원본과 동등) + CancellationToken.
/// 실패 메시지 형식은 원본 그대로: "{이름}: {시스템 오류 메시지}", 여러 건은 \n join.
/// </summary>
public static class FileOperations
{
    // ── 2.2 이름 충돌 회피 규칙 (그대로 이식) ───────────────────────────

    /// <summary>
    /// n=1이면 "base"(+확장자), n≥2이면 "base n"(+확장자) — 보고서.pdf → 보고서 2.pdf → 보고서 3.pdf …
    /// ext는 점 없는 확장자(빈 문자열이면 점 없이 stem만). 존재하지 않는 첫 경로를 반환.
    /// </summary>
    public static string UniqueUrl(string directory, string baseName, string ext)
    {
        for (int n = 1; ; n++)
        {
            var stem = n == 1 ? baseName : $"{baseName} {n}";
            var name = ext.Length == 0 ? stem : $"{stem}.{ext}";
            var candidate = Path.Combine(directory, name);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    // ── 2.3 복사/이동 ────────────────────────────────────────────────────

    /// <summary>
    /// 복사/이동 — 항목당 1단위 진행률(폴더는 통째 1단위). 모든 항목 이름에 WindowsName.Sanitize 적용,
    /// 같은 폴더로의 이동은 no-op(이름 정리가 필요한 경우는 진행), 충돌 시 UniqueUrl로 자동 리네임.
    /// 반환: 실패 메시지 목록 (비어 있으면 성공/취소).
    /// </summary>
    public static Task<List<string>> Transfer(IReadOnlyList<string> items, string destDir, bool move,
                                              OperationProgress progress)
    {
        return Task.Run(() =>
        {
            OnUi(() => { progress.TotalUnits = items.Count; progress.CompletedUnits = 0; });
            var failures = new List<string>();
            var destNorm = NormalizeDir(destDir);

            foreach (var src in items)
            {
                if (progress.IsCancelled) break;   // 항목 경계에서만 취소 확인 (원본 동등)

                var srcTrimmed = Path.TrimEndingDirectorySeparator(src);
                var original = Path.GetFileName(srcTrimmed);
                var name = WindowsName.Sanitize(original);
                OnUi(() => progress.CurrentFile = original);   // 정리 전 이름 표시

                // 폴더를 자기 자신/자기 하위로 복사·이동 금지 — CopyDirectory 무한 재귀 방지
                if (Directory.Exists(srcTrimmed))
                {
                    var srcNorm = NormalizeDir(srcTrimmed);
                    if (string.Equals(destNorm, srcNorm, StringComparison.OrdinalIgnoreCase)
                        || destNorm.StartsWith(srcNorm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add($"{name}: 대상 폴더가 원본 폴더 자신이거나 그 안에 있습니다.");
                        OnUi(() => progress.CompletedUnits += 1);
                        continue;
                    }
                }

                // 같은 폴더로의 이동은 no-op (Finder 동작). 이름 정리가 필요하면 같은 폴더라도 진행.
                string parent = "";
                try { parent = Path.GetDirectoryName(Path.GetFullPath(srcTrimmed)) ?? ""; } catch { }
                if (move && name == original
                    && string.Equals(NormalizeDir(parent), destNorm, StringComparison.OrdinalIgnoreCase))
                {
                    OnUi(() => progress.CompletedUnits += 1);
                    continue;
                }

                var dest = Path.Combine(destDir, name);
                if (File.Exists(dest) || Directory.Exists(dest))
                {
                    var stem0 = Path.GetFileNameWithoutExtension(original);
                    var ext0 = Path.GetExtension(original).TrimStart('.');   // 확장자는 sanitize하지 않음
                    // 폴더("v1.2") 또는 도트파일(".gitignore")은 이름 전체를 stem으로
                    if (Directory.Exists(srcTrimmed) || stem0.Length == 0) { stem0 = original; ext0 = ""; }
                    dest = UniqueUrl(destDir, WindowsName.Sanitize(stem0), ext0);
                }

                try
                {
                    if (move) MoveItem(srcTrimmed, dest);
                    else CopyItem(srcTrimmed, dest);
                }
                catch (Exception ex)
                {
                    failures.Add($"{name}: {ex.Message}");     // 실패해도 다음 항목 계속
                }
                OnUi(() => progress.CompletedUnits += 1);
            }
            return failures;
        });
    }

    // ── 2.10 복제 (⌘D — "이름 copy.ext" 규칙) ──────────────────────────

    /// <summary>
    /// 복제 — UniqueUrl(폴더, Sanitize(stem) + " copy", 확장자): 보고서.pdf → 보고서 copy.pdf → 보고서 copy 2.pdf.
    /// 반환: (실패 메시지 목록, 생성된 경로 목록 — 마지막 항목으로 커서 이동용).
    /// </summary>
    public static Task<(List<string> Failures, List<string> Created)> Duplicate(
        IReadOnlyList<string> items, OperationProgress progress)
    {
        return Task.Run(() =>
        {
            OnUi(() => { progress.TotalUnits = items.Count; progress.CompletedUnits = 0; });
            var failures = new List<string>();
            var created = new List<string>();
            foreach (var src in items)
            {
                if (progress.IsCancelled) break;
                var srcTrimmed = Path.TrimEndingDirectorySeparator(src);
                var original = Path.GetFileName(srcTrimmed);
                OnUi(() => progress.CurrentFile = original);
                try
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(srcTrimmed)) ?? "";
                    var stem0 = Path.GetFileNameWithoutExtension(original);
                    var ext0 = Path.GetExtension(original).TrimStart('.');
                    // 폴더("v1.2") 또는 도트파일(".gitignore")은 이름 전체를 stem으로
                    if (Directory.Exists(srcTrimmed) || stem0.Length == 0) { stem0 = original; ext0 = ""; }
                    var dest = UniqueUrl(dir, WindowsName.Sanitize(stem0) + " copy", ext0);
                    CopyItem(srcTrimmed, dest);
                    created.Add(dest);
                }
                catch (Exception ex)
                {
                    failures.Add($"{original}: {ex.Message}");
                }
                OnUi(() => progress.CompletedUnits += 1);
            }
            return (failures, created);
        });
    }

    // ── 2.4 ZIP 압축 ────────────────────────────────────────────────────

    /// <summary>
    /// ZIP 압축 — items는 같은 부모 디렉터리 안에 있어야 함(호출부 책임). 아카이브 내부 경로는 상대 이름.
    /// UTF-8 엔트리 이름 + NFC 정규화(mac zip 동등). 엔트리 단위 진행률·취소 지원(스펙 개선안).
    /// 반환: 오류 메시지(null = 성공/취소). 실패 형식: "zip failed ({코드}):\n{메시지}".
    /// </summary>
    public static Task<string?> Compress(IReadOnlyList<string> items, string destZip,
                                         OperationProgress progress, CancellationToken ct = default)
    {
        return Task.Run<string?>(() =>
        {
            try
            {
                // 엔트리 수집: (소스 경로, '/' 구분 상대 엔트리 이름). 빈 폴더는 "이름/" 디렉터리 엔트리.
                var files = new List<(string Path, string Entry)>();
                var emptyDirs = new List<string>();
                foreach (var item in items)
                {
                    var trimmed = Path.TrimEndingDirectorySeparator(item);
                    var name = Path.GetFileName(trimmed);
                    if (Directory.Exists(trimmed)) CollectZipEntries(trimmed, name, files, emptyDirs);
                    else files.Add((trimmed, name));
                }
                OnUi(() =>
                {
                    progress.TotalUnits = Math.Max(1, files.Count);
                    progress.CompletedUnits = 0;
                    progress.CurrentFile = Path.GetFileName(destZip);
                });

                bool cancelled = false;
                // CreateNew 자체가 실패(동명 파일 존재 등)하면 기존 파일을 건드리지 않고 종료
                FileStream stream;
                try { stream = new FileStream(destZip, FileMode.CreateNew, FileAccess.Write); }
                catch (Exception ex) { return $"zip failed (0):\n{ex.Message}"; }
                using (stream)
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false,
                                                    entryNameEncoding: Encoding.UTF8))
                {
                    foreach (var dir in emptyDirs)
                    {
                        if (ct.IsCancellationRequested || progress.IsCancelled) { cancelled = true; break; }
                        archive.CreateEntry(NfcEntry(dir));
                    }
                    if (!cancelled)
                    {
                        foreach (var (path, entry) in files)
                        {
                            if (ct.IsCancellationRequested || progress.IsCancelled) { cancelled = true; break; }
                            OnUi(() => progress.CurrentFile = Path.GetFileName(path));
                            archive.CreateEntryFromFile(path, NfcEntry(entry));
                            OnUi(() => progress.CompletedUnits += 1);
                        }
                    }
                }
                if (cancelled)
                {
                    try { File.Delete(destZip); } catch { }    // 미완성 아카이브 정리
                    return null;
                }
                OnUi(() => progress.CompletedUnits = progress.TotalUnits);
                return null;
            }
            catch (Exception ex)
            {
                try { File.Delete(destZip); } catch { }
                return $"zip failed (1):\n{ex.Message}";
            }
        });
    }

    // ── 2.5 ZIP 해제 ────────────────────────────────────────────────────

    /// <summary>
    /// ZIP 해제 — 기존 파일은 덮어씀(mac ditto 동등). UTF-8 플래그 없는 한글 zip은 CP949 폴백,
    /// 엔트리 이름은 세그먼트별 WindowsName.Sanitize(NFC 정규화 + 금지 문자) + Zip Slip 방어.
    /// 반환: 오류 메시지(null = 성공/취소). 실패 형식: "extract failed ({코드}):\n{메시지}".
    /// </summary>
    public static Task<string?> Extract(string archive, string destDir,
                                        OperationProgress progress, CancellationToken ct = default)
    {
        return Task.Run<string?>(() =>
        {
            try
            {
                Directory.CreateDirectory(destDir);
                var destRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destDir));
                var destPrefix = destRoot + Path.DirectorySeparatorChar;

                using var zip = ZipFile.Open(archive, ZipArchiveMode.Read, Cp949.Value);
                OnUi(() =>
                {
                    progress.TotalUnits = Math.Max(1, zip.Entries.Count);
                    progress.CompletedUnits = 0;
                });
                foreach (var entry in zip.Entries)
                {
                    if (ct.IsCancellationRequested || progress.IsCancelled) return null;   // 취소

                    var relative = SafeEntryPath(entry.FullName);
                    if (relative.Length == 0) { OnUi(() => progress.CompletedUnits += 1); continue; }
                    var destPath = Path.GetFullPath(Path.Combine(destRoot, relative));
                    if (!destPath.StartsWith(destPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        OnUi(() => progress.CompletedUnits += 1);   // Zip Slip 방어 — 밖으로 나가는 엔트리 무시
                        continue;
                    }

                    bool isDirEntry = entry.FullName.EndsWith("/", StringComparison.Ordinal)
                                   || entry.FullName.EndsWith("\\", StringComparison.Ordinal)
                                   || entry.Name.Length == 0;
                    if (isDirEntry)
                    {
                        Directory.CreateDirectory(destPath);
                    }
                    else
                    {
                        OnUi(() => progress.CurrentFile = Path.GetFileName(destPath));
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                    OnUi(() => progress.CompletedUnits += 1);
                }
                return null;
            }
            catch (Exception ex)
            {
                return $"extract failed (1):\n{ex.Message}";
            }
        });
    }

    // ── 2.7 프로세스 헬퍼 (범용 유틸로 유지) ────────────────────────────

    /// <summary>외부 프로세스 실행 — stdout+stderr 합쳐 캡처. 실행 실패 시 (-1, 예외 메시지).</summary>
    public static (int ExitCode, string Output) RunProcess(string launchPath, IReadOnlyList<string> arguments,
                                                           string? workingDirectory = null)
    {
        try
        {
            var psi = new ProcessStartInfo(launchPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? "",
            };
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null) return (-1, "프로세스를 시작하지 못했습니다.");
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();
            return (proc.ExitCode, outTask.Result + errTask.Result);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    // ── 내부 구현 ────────────────────────────────────────────────────────

    /// <summary>경로 정규화(절대 경로 + 끝 구분자 제거) — 같은 폴더 판정용.</summary>
    private static string NormalizeDir(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path; }
    }

    /// <summary>이동 — 같은 볼륨은 Move, 볼륨 간 디렉터리는 복사+삭제 (파일은 MoveFile이 자동 처리).</summary>
    private static void MoveItem(string src, string dest)
    {
        if (Directory.Exists(src))
        {
            var srcRoot = Path.GetPathRoot(Path.GetFullPath(src));
            var destRoot = Path.GetPathRoot(Path.GetFullPath(dest));
            if (!string.Equals(srcRoot, destRoot, StringComparison.OrdinalIgnoreCase))
            {
                CopyDirectory(src, dest);
                Directory.Delete(src, recursive: true);
                return;
            }
            Directory.Move(src, dest);
        }
        else
        {
            File.Move(src, dest);
        }
    }

    /// <summary>복사 — 파일은 File.Copy(덮어쓰기 없음), 폴더는 재귀 복사.</summary>
    private static void CopyItem(string src, string dest)
    {
        if (Directory.Exists(src)) CopyDirectory(src, dest);
        else File.Copy(src, dest, overwrite: false);
    }

    /// <summary>재귀 폴더 복사 — 내부 항목 이름은 정리하지 않음(원본도 루트 항목만 정리).
    /// 심링크/정션 디렉터리는 빈 폴더로 재현(무한 루프 방지). 오류는 예외로 전파.</summary>
    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.None };
        foreach (var info in new DirectoryInfo(src).EnumerateFileSystemInfos("*", options))
        {
            var target = Path.Combine(dest, info.Name);
            if ((info.Attributes & FileAttributes.Directory) != 0)
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                CopyDirectory(info.FullName, target);
            }
            else
            {
                File.Copy(info.FullName, target, overwrite: false);
            }
        }
    }

    /// <summary>ZIP 엔트리 수집 — entryPrefix는 부모 기준 상대 경로('/' 구분). 빈 폴더는 "경로/" 등록.</summary>
    private static void CollectZipEntries(string dirPath, string entryPrefix,
                                          List<(string Path, string Entry)> files, List<string> emptyDirs)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(dirPath)
                .EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.None,
                })
                .ToList();
        }
        catch { entries = new List<FileSystemInfo>(); }

        if (entries.Count == 0)
        {
            emptyDirs.Add(entryPrefix + "/");
            return;
        }
        foreach (var info in entries)
        {
            FileAttributes attrs;
            try { attrs = info.Attributes; } catch { continue; }
            if ((attrs & FileAttributes.ReparsePoint) != 0) continue;   // 심링크/정션 제외
            var childEntry = entryPrefix + "/" + info.Name;
            if ((attrs & FileAttributes.Directory) != 0)
                CollectZipEntries(info.FullName, childEntry, files, emptyDirs);
            else
                files.Add((info.FullName, childEntry));
        }
    }

    /// <summary>엔트리 이름 NFC 정규화 — mac에서 풀 때 자소 분리 방지.</summary>
    private static string NfcEntry(string entryName)
    {
        try { return entryName.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return entryName; }
    }

    /// <summary>엔트리 경로를 세그먼트별로 정리 — "."/".." 제거(상위 탈출 차단) + WindowsName.Sanitize(NFC 포함).</summary>
    private static string SafeEntryPath(string entryName)
    {
        var parts = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safe = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == "." || part == "..") continue;
            safe.Add(WindowsName.Sanitize(part));
        }
        return string.Join(Path.DirectorySeparatorChar, safe);
    }

    /// <summary>CP949 폴백 인코딩 — UTF-8 플래그 없는 한글 zip(과거 윈도우/알집 제작) 대응. 실패 시 null(기본 동작).</summary>
    private static readonly Lazy<Encoding?> Cp949 = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949);
        }
        catch { return null; }
    });

    /// <summary>UI 스레드에서 진행률 갱신 — Dispatcher 없으면(테스트 등) 그 자리에서 실행.</summary>
    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.InvokeAsync(action);
    }
}
