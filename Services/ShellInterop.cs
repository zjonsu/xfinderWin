using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XFinder.Services;

/// <summary>
/// Windows 셸 연동 P/Invoke 모음 — 휴지통 이동, 파일 아이콘/썸네일, 탐색기에서 보기.
/// macOS의 NSWorkspace / QuickLookThumbnailing 대응.
/// </summary>
public static class ShellInterop
{
    // ── 휴지통 (FOF_ALLOWUNDO) ──────────────────────────────────────────

    // SHFILEOPSTRUCT는 shellapi.h에서 32비트 빌드만 1바이트 패킹 — 64비트는 자연 정렬.
    // Pack=1을 64비트에 쓰면 pFrom 오프셋이 어긋나 휴지통 이동이 실패/크래시한다.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct SHFILEOPSTRUCT32
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation32(ref SHFILEOPSTRUCT32 fileOp);

    /// <summary>파일/폴더들을 휴지통으로 이동. 성공 여부 반환.</summary>
    public static bool MoveToRecycleBin(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0) return true;
        var from = string.Join("\0", list) + "\0\0";
        const ushort flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT;
        if (IntPtr.Size == 8)
        {
            var op = new SHFILEOPSTRUCT { wFunc = FO_DELETE, pFrom = from, fFlags = flags };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
        }
        else
        {
            var op = new SHFILEOPSTRUCT32 { wFunc = FO_DELETE, pFrom = from, fFlags = flags };
            return SHFileOperation32(ref op) == 0 && !op.fAnyOperationsAborted;
        }
    }

    // ── 휴지통 비우기 ────────────────────────────────────────────────────

    [DllImport("shell32.dll")]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x1;
    private const uint SHERB_NOPROGRESSUI = 0x2;
    private const uint SHERB_NOSOUND = 0x4;

    public static void EmptyRecycleBin()
        => SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOSOUND);

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    /// <summary>휴지통 (총 바이트, 항목 수). 실패 시 (0,0).</summary>
    public static (long Bytes, long Items) QueryRecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    // ── 파일 아이콘 (SHGetFileInfo) ─────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint SHGFI_TYPENAME = 0x400;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>실제 경로 기반 아이콘 (실파일 접근). UI 스레드 호출 권장 아님.</summary>
    public static ImageSource? GetIcon(string path, bool isDirectory, bool large)
    {
        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON);
        var result = SHGetFileInfo(path, isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
            ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var img = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            img.Freeze();
            return img;
        }
        finally { DestroyIcon(info.hIcon); }
    }

    /// <summary>확장자 기반 아이콘 (파일 접근 없음 — 캐시 키로 확장자 사용 가능).</summary>
    public static ImageSource? GetIconForExtension(string extension, bool isDirectory, bool large)
    {
        var name = isDirectory ? "folder" : "f" + extension;
        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON);
        var result = SHGetFileInfo(name, isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
            ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var img = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            img.Freeze();
            return img;
        }
        finally { DestroyIcon(info.hIcon); }
    }

    /// <summary>셸이 보고하는 파일 종류 문자열 (예: "PNG 파일").</summary>
    public static string GetTypeName(string path, bool isDirectory)
    {
        var info = new SHFILEINFO();
        SHGetFileInfo(path, isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
            ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
        return info.szTypeName;
    }

    // ── 썸네일 (IShellItemImageFactory) ─────────────────────────────────

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
        ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const int SIIGBF_RESIZETOFIT = 0x00;
    private const int SIIGBF_THUMBNAILONLY = 0x08;

    /// <summary>셸 썸네일 (이미지/동영상/PDF 등). 썸네일이 없으면 null.</summary>
    public static ImageSource? GetThumbnail(string path, int pixelSize, bool thumbnailOnly = true)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            int flags = SIIGBF_RESIZETOFIT | (thumbnailOnly ? SIIGBF_THUMBNAILONLY : 0);
            if (factory.GetImage(new SIZE { cx = pixelSize, cy = pixelSize }, flags, out var hBitmap) != 0
                || hBitmap == IntPtr.Zero)
                return null;
            try
            {
                var img = BitmapFromHBitmapPreservingAlpha(hBitmap);
                if (img is { CanFreeze: true }) img.Freeze();
                return img;
            }
            finally { DeleteObject(hBitmap); }
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hObject, int nCount, ref BITMAP lpObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hBitmap, uint start, uint lines,
        byte[]? bits, ref BITMAPINFOHEADER bmi, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    /// <summary>CreateBitmapSourceFromHBitmap은 알파를 버려 투명 썸네일이 검게 나온다 —
    /// 32bpp면 GetDIBits(음수 높이 = 톱다운 강제)로 프리멀티플라이드 BGRA를 직접 복사.</summary>
    private static ImageSource? BitmapFromHBitmapPreservingAlpha(IntPtr hBitmap)
    {
        var bmp = new BITMAP();
        if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), ref bmp) != 0
            && bmp.bmBitsPixel == 32 && bmp.bmHeight > 0 && bmp.bmWidth > 0)
        {
            var hdc = GetDC(IntPtr.Zero);
            try
            {
                var stride = bmp.bmWidth * 4;
                var buffer = new byte[(long)stride * bmp.bmHeight];
                var bmi = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = bmp.bmWidth,
                    biHeight = -bmp.bmHeight,   // 음수 = 톱다운
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,          // BI_RGB
                };
                if (GetDIBits(hdc, hBitmap, 0, (uint)bmp.bmHeight, buffer, ref bmi, 0) != 0)
                    return BitmapSource.Create(bmp.bmWidth, bmp.bmHeight, 96, 96,
                        PixelFormats.Pbgra32, null, buffer, stride);
            }
            finally { ReleaseDC(IntPtr.Zero, hdc); }
        }
        return Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
    }

    // ── 탐색기에서 보기 (Finder에서 보기 대응) ───────────────────────────

    /// <summary>탐색기를 열어 해당 항목을 선택 상태로 표시.</summary>
    public static void RevealInExplorer(string path)
        => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
}
