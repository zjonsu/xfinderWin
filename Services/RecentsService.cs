// mac 소스 대응: Sources/XFinder/Services/RecentsService.swift (RecentsLoader, NSMetadataQuery) — Windows Recent 폴더 .lnk 해석으로 대체 (스펙 §4.2 1안)
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using XFinder.Models;

namespace XFinder.Services;

/// <summary>
/// 최근 항목 — %APPDATA%\Microsoft\Windows\Recent 의 .lnk를 IShellLink로 해석.
/// .lnk 수정 시각 = 마지막 사용 시각(내림차순), 60일 컷, 카테고리 필터, limit.
/// Spotlight 재진입 가드는 불필요 — CancellationToken으로 대체.
/// </summary>
public static class RecentsService
{
    /// <summary>
    /// 최근 항목 로드 (동기 — 호출부가 Task.Run으로 감싼다).
    /// categories가 비어 있지 않으면 FileSystemService.FileCategory(확장자)가 포함된 것만 (빈 목록/널 = 전체).
    /// FileItem.Modified = 마지막 사용 시각(수정일 아님!), 폴더는 Size -1, IsSymlink/IsHidden = false 고정.
    /// </summary>
    public static List<FileItem> Load(int limit = 100, IReadOnlyCollection<string>? categories = null,
                                      CancellationToken ct = default)
    {
        // IShellLink는 아파트형 COM — 스레드 풀(MTA)에서 불리면 전용 STA 스레드에서 해석.
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return LoadCore(limit, categories, ct);

        List<FileItem>? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = LoadCore(limit, categories, ct); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();   // 취소 포함 — 호출부 catch
        return result ?? new List<FileItem>();
    }

    private static List<FileItem> LoadCore(int limit, IReadOnlyCollection<string>? categories,
                                           CancellationToken ct)
    {
        var result = new List<FileItem>();
        string recentDir;
        try { recentDir = Environment.GetFolderPath(Environment.SpecialFolder.Recent); }
        catch { return result; }
        if (string.IsNullOrEmpty(recentDir) || !Directory.Exists(recentDir)) return result;

        List<FileInfo> links;
        try
        {
            links = new DirectoryInfo(recentDir)
                .EnumerateFiles("*.lnk", new EnumerationOptions { IgnoreInaccessible = true })
                .OrderByDescending(f => f.LastWriteTime)   // 마지막 사용일 내림차순
                .ToList();
        }
        catch { return result; }

        var cutoff = DateTime.Now.AddDays(-60);            // mac: 최근 60일
        var filter = categories is { Count: > 0 } ? new HashSet<string>(categories, StringComparer.Ordinal) : null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var shellLink = (IShellLinkW)new CShellLink();
        try
        {
            var persistFile = (System.Runtime.InteropServices.ComTypes.IPersistFile)shellLink;
            var buffer = new StringBuilder(1024);
            foreach (var lnk in links)
            {
                ct.ThrowIfCancellationRequested();
                if (result.Count >= limit) break;
                if (lnk.LastWriteTime < cutoff) break;     // 정렬돼 있으므로 이후는 전부 컷

                string target;
                try
                {
                    persistFile.Load(lnk.FullName, 0);     // STGM_READ
                    buffer.Clear();
                    if (shellLink.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0) != 0) continue;
                    target = buffer.ToString();
                }
                catch { continue; }
                if (target.Length == 0 || !seen.Add(target)) continue;

                bool isDir;
                try
                {
                    if (File.Exists(target)) isDir = false;
                    else if (Directory.Exists(target)) isDir = true;
                    else continue;                          // 존재하는 항목만
                }
                catch { continue; }

                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(target));
                if (name.Length == 0) continue;             // 드라이브 루트 등 제외
                var ext = isDir ? "" : FileSystemService.ExtensionLower(name);
                if (filter is not null && !filter.Contains(FileSystemService.FileCategory(ext))) continue;

                long size = -1;
                if (!isDir)
                {
                    try { size = new FileInfo(target).Length; } catch { size = 0; }
                }

                result.Add(new FileItem
                {
                    Path = target,
                    Name = name,
                    IsDirectory = isDir,
                    IsSymlink = false,                      // 고정 (원본 동일)
                    IsHidden = false,                       // 고정 (원본 동일)
                    Size = size,
                    Modified = lnk.LastWriteTime,           // 마지막 사용 시각
                    Ext = ext,
                    IsParent = false,
                    TypeName = FileSystemService.TypeNameFor(name, isDir),
                });
            }
        }
        finally
        {
            try { Marshal.ReleaseComObject(shellLink); } catch { }
        }
        return result;
    }

    // ── IShellLink COM 인터롭 ────────────────────────────────────────────

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath,
                    IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath,
                             out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
