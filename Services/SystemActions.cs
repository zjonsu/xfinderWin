// mac 소스 대응: Sources/XFinder/Services/SystemActions.swift — 열기/탐색기 보기/속성/휴지통 열기/터미널 + 클립보드(CF_HDROP, Preferred DropEffect)
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using XFinder.Models;

namespace XFinder.Services;

/// <summary>
/// 시스템 연동 동작 모음. mac 전용 멤버(전체 디스크 접근/자동화 설정, DayFlow)는 포팅하지 않음(스펙 §3.1).
/// 클립보드 메서드는 STA(UI) 스레드에서 호출할 것.
/// </summary>
public static class SystemActions
{
    // ── 열기 / 탐색기 / 속성 / 휴지통 ───────────────────────────────────

    /// <summary>기본 앱으로 열기 (NSWorkspace.open 대응). 연결 프로그램이 없으면 '연결 프로그램' 대화상자.</summary>
    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // 연결된 앱 없음 → "연결 프로그램 선택" 대화상자
            try { Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}")); }
            catch { }
        }
    }

    /// <summary>탐색기에서 항목 선택 표시 (Finder에서 보기 대응).</summary>
    public static void RevealInExplorer(string path)
    {
        try { ShellInterop.RevealInExplorer(path); } catch { }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType,
        string pszObjectName, string? pszPropertyPage);

    private const uint SHOP_FILEPATH = 0x2;

    /// <summary>셸 속성 대화상자 (mac Finder '정보 가져오기' 대응).</summary>
    public static void ShowProperties(string path)
    {
        try { SHObjectProperties(IntPtr.Zero, SHOP_FILEPATH, path, null); } catch { }
    }

    /// <summary>휴지통 폴더 열기 (Finder 휴지통 열기 대응).</summary>
    public static void OpenRecycleBin()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder")
            {
                UseShellExecute = true,
            });
        }
        catch { }
    }

    // ── 터미널 열기 (설정 키 XFinder.terminalApp.v1) ─────────────────────

    /// <summary>
    /// 해당 디렉터리에서 터미널 열기 — Auto: Windows Terminal 있으면 wt, 없으면 PowerShell.
    /// WindowsTerminal: wt(없으면 PowerShell 폴백). PowerShell: 항상 PowerShell.
    /// (mac auto/iterm/terminal 선택 로직의 Windows 대응.)
    /// </summary>
    public static void OpenTerminal(string directory, TerminalAppChoice app = TerminalAppChoice.Auto)
    {
        try
        {
            var wt = app == TerminalAppChoice.PowerShell ? null : FindWindowsTerminal();
            if (wt is not null)
            {
                // 끝의 '\'가 닫는 따옴표를 이스케이프하지 않게 한 번 더 추가 (예: C:\ → C:\\)
                var dir = directory.EndsWith('\\') ? directory + "\\" : directory;
                Process.Start(new ProcessStartInfo(wt, $"-d \"{dir}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = true,
                    WorkingDirectory = directory,
                });
            }
        }
        catch { }
    }

    /// <summary>wt.exe 위치 — %LOCALAPPDATA%\Microsoft\WindowsApps 우선, 그다음 PATH 검색. 없으면 null.</summary>
    public static string? FindWindowsTerminal()
    {
        try
        {
            var alias = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "wt.exe");
            if (File.Exists(alias)) return alias;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "wt.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // ── 클립보드 (CF_HDROP + Preferred DropEffect — 탐색기 상호 운용) ────

    private const string PreferredDropEffectFormat = "Preferred DropEffect";

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    /// <summary>시스템 클립보드 시퀀스 번호 — mac changeCount의 정확한 대응. 호출부가 기록·비교한다.</summary>
    public static uint ClipboardSequenceNumber()
    {
        try { return GetClipboardSequenceNumber(); } catch { return 0; }
    }

    /// <summary>
    /// 파일 목록을 시스템 클립보드에 기록 — CF_HDROP + "Preferred DropEffect"
    /// (잘라내기 = Move(2), 복사 = Copy|Link(5) — 탐색기 관례). 탐색기에 그대로 붙여넣기 가능.
    /// </summary>
    public static void WriteFilesToClipboard(IReadOnlyList<string> paths, bool cut)
    {
        try
        {
            var data = new DataObject();
            var files = new StringCollection();
            files.AddRange(paths.ToArray());
            data.SetFileDropList(files);
            int effect = cut
                ? (int)DragDropEffects.Move
                : (int)(DragDropEffects.Copy | DragDropEffects.Link);
            data.SetData(PreferredDropEffectFormat, new MemoryStream(BitConverter.GetBytes(effect)), false);
            Clipboard.SetDataObject(data, copy: true);
        }
        catch { /* CLIPBRD_E_CANT_OPEN 등 — 일시적 점유 실패는 무시 */ }
    }

    /// <summary>
    /// 시스템 클립보드의 파일 목록 읽기 — 파일이 있으면 true.
    /// cut: "Preferred DropEffect"가 Move(복사 비트 없음)이면 true — 탐색기에서 Ctrl+X 한 파일을 이동으로 존중.
    /// </summary>
    public static bool TryReadClipboardFiles(out List<string> files, out bool cut)
    {
        files = new List<string>();
        cut = false;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null || !data.GetDataPresent(DataFormats.FileDrop)) return false;
            if (data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0) return false;
            files = dropped.ToList();

            if (data.GetDataPresent(PreferredDropEffectFormat)
                && data.GetData(PreferredDropEffectFormat) is MemoryStream ms)
            {
                var buf = new byte[4];
                ms.Position = 0;
                if (ms.Read(buf, 0, 4) == 4)
                {
                    int effect = BitConverter.ToInt32(buf, 0);
                    cut = (effect & (int)DragDropEffects.Move) != 0
                          && (effect & (int)DragDropEffects.Copy) == 0;
                }
            }
            return true;
        }
        catch
        {
            files = new List<string>();
            cut = false;
            return false;
        }
    }

    /// <summary>선택 항목들의 절대 경로를 텍스트로 복사 — 여러 개면 \n 구분 (⌥⌘C 대응).</summary>
    public static void CopyPathsAsText(IReadOnlyList<string> paths)
    {
        try { Clipboard.SetText(string.Join("\n", paths)); } catch { }
    }
}
