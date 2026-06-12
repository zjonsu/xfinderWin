// mac 대응: SystemActions의 "다음으로 열기" 앱 나열 (NSWorkspace urlsForApplications) — SHAssocEnumHandlers
using System.Runtime.InteropServices;

namespace XFinder.Services;

/// <summary>
/// "다음으로 열기" — 확장자별 연결 앱 나열(SHAssocEnumHandlers) + "기타…" 대화상자(SHOpenWithDialog).
/// 느린 호출이므로 메뉴 SubmenuOpened에서 lazy 호출할 것 (스펙 03 §5.3).
/// </summary>
public static class OpenWithService
{
    public sealed record AppHandler(string Name, Action<string> Invoke);

    [ComImport, Guid("F04061AC-1659-4A3F-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAssocHandler
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
        [PreserveSig] int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
        [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string ppszPath, out int pIndex);
        [PreserveSig] int IsRecommended();
        [PreserveSig] int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
        [PreserveSig] int Invoke(IntPtr pdo);
        [PreserveSig] int CreateInvoker(IntPtr pdo, out IntPtr ppInvoker);
    }

    [ComImport, Guid("973810AE-9599-4B88-9E4D-6EE98C9552DA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumAssocHandlers
    {
        [PreserveSig]
        int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IAssocHandler[] rgelt,
            out uint pceltFetched);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHAssocEnumHandlers(string pszExtra, int afFilter,
        out IEnumAssocHandlers ppEnumHandler);

    private const int ASSOC_FILTER_RECOMMENDED = 1;

    /// <summary>확장자(".pdf")의 연결 앱 목록 — 실행 클로저는 exe 직접 실행. 실패 시 빈 목록.</summary>
    public static List<AppHandler> EnumHandlers(string extension)
    {
        var result = new List<AppHandler>();
        if (string.IsNullOrEmpty(extension)) return result;
        try
        {
            if (SHAssocEnumHandlers(extension, ASSOC_FILTER_RECOMMENDED, out var enumerator) != 0)
                return result;
            var buf = new IAssocHandler[1];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (enumerator.Next(1, buf, out var fetched) == 0 && fetched == 1)
            {
                var handler = buf[0];
                try
                {
                    if (handler.GetUIName(out var uiName) != 0 || string.IsNullOrWhiteSpace(uiName)) continue;
                    handler.GetName(out var exePath);
                    if (!seen.Add(uiName)) continue;
                    var exe = exePath;
                    result.Add(new AppHandler(uiName, path =>
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"\"{path}\"")
                            { UseShellExecute = true });
                        }
                        catch { }
                    }));
                    if (result.Count >= 12) break;   // 메뉴 과밀 방지
                }
                catch { }
                finally { Marshal.ReleaseComObject(handler); }
            }
        }
        catch { }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pcszFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pcszClass;
        public int oaifInFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OPENASINFO info);

    private const int OAIF_EXEC = 0x04;
    private const int OAIF_HIDE_REGISTRATION = 0x20;

    /// <summary>"기타…" — Windows 연결 프로그램 대화상자.</summary>
    public static void ShowOpenWithDialog(string path)
    {
        try
        {
            var info = new OPENASINFO
            {
                pcszFile = path,
                pcszClass = null,
                oaifInFlags = OAIF_EXEC | OAIF_HIDE_REGISTRATION,
            };
            SHOpenWithDialog(IntPtr.Zero, ref info);
        }
        catch { }
    }
}
