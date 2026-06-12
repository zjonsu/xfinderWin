// mac 대응: Sources/XFinder/Services/AppUninstaller.swift — Windows 재설계: 레지스트리 Uninstall 키 열거 + AppData/시작 메뉴 잔재 스캔 (스펙 04 §11.6)
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace XFinder.Services;

/// <summary>레지스트리 Uninstall 키 하나 = 설치된 프로그램 항목.</summary>
public sealed record InstalledApp
{
    public required string DisplayName { get; init; }
    public string Publisher { get; init; } = "";
    public string DisplayVersion { get; init; } = "";
    public string? DisplayIcon { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? InstallLocation { get; init; }
    /// <summary>레지스트리 EstimatedSize(KB) → bytes. 0 = 미기재.</summary>
    public long EstimatedSizeBytes { get; init; }
}

/// <summary>잔재 후보 — 경로 + 크기 + 출처 라벨 (mac AppRelatedFile 대응).</summary>
public sealed record ResidueCandidate(string Path, long Size, bool IsDirectory, string Kind);

/// <summary>
/// 설치 프로그램 열거(HKLM/HKCU, WOW6432Node 포함)와 잔재 스캔.
/// 매칭은 휴리스틱(이름 정확 일치 또는 단어 경계, 회사명\제품명 구조) — 오탐 방지를 위해
/// 사용자 검토(체크박스) 단계를 절대 생략하지 말 것. 삭제는 휴지통 경유만.
/// </summary>
public static class AppUninstaller
{
    // ── 설치된 프로그램 열거 ─────────────────────────────────────────────

    public static List<InstalledApp> ListInstalledApps()
    {
        var byKey = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        var roots = new (RegistryKey Root, string Path)[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (root, path) in roots)
        {
            RegistryKey? key = null;
            try { key = root.OpenSubKey(path); } catch { }
            if (key is null) continue;
            using (key)
            {
                foreach (var sub in key.GetSubKeyNames())
                {
                    try
                    {
                        using var k = key.OpenSubKey(sub);
                        if (k is null) continue;
                        var name = (k.GetValue("DisplayName") as string)?.Trim();
                        if (string.IsNullOrEmpty(name)) continue;
                        if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                        if (k.GetValue("ParentKeyName") is string parent && parent.Length > 0) continue;   // 패치/업데이트
                        if (k.GetValue("ReleaseType") is string rel && rel.Contains("Update", StringComparison.OrdinalIgnoreCase)) continue;

                        var uninstall = (k.GetValue("UninstallString") as string)?.Trim();
                        // MSI 설치인데 UninstallString이 없으면 제품 코드로 구성
                        if (string.IsNullOrWhiteSpace(uninstall)
                            && k.GetValue("WindowsInstaller") is int wi && wi == 1 && sub.StartsWith('{'))
                            uninstall = "MsiExec.exe /X" + sub;

                        long sizeBytes = 0;
                        if (k.GetValue("EstimatedSize") is int kb && kb > 0) sizeBytes = (long)kb * 1024;

                        var app = new InstalledApp
                        {
                            DisplayName = name,
                            Publisher = (k.GetValue("Publisher") as string)?.Trim() ?? "",
                            DisplayVersion = (k.GetValue("DisplayVersion") as string)?.Trim() ?? "",
                            DisplayIcon = k.GetValue("DisplayIcon") as string,
                            UninstallString = uninstall,
                            QuietUninstallString = k.GetValue("QuietUninstallString") as string,
                            InstallLocation = (k.GetValue("InstallLocation") as string)?.Trim().Trim('"'),
                            EstimatedSizeBytes = sizeBytes,
                        };

                        var dedup = name + "|" + app.DisplayVersion;
                        if (!byKey.TryGetValue(dedup, out var existing)
                            || (string.IsNullOrWhiteSpace(existing.UninstallString) && !string.IsNullOrWhiteSpace(uninstall)))
                            byKey[dedup] = app;
                    }
                    catch { }
                }
            }
        }
        return byKey.Values.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ── 제거 프로그램 실행 ───────────────────────────────────────────────

    /// <summary>UninstallString을 셸 실행(UAC 승격 가능). 실패 시 false + error.</summary>
    public static bool RunUninstaller(InstalledApp app, out string? error)
    {
        var cmd = app.UninstallString;
        if (string.IsNullOrWhiteSpace(cmd))
        {
            error = "제거 명령이 등록되어 있지 않습니다.";
            return false;
        }
        try
        {
            var (exe, args) = SplitCommand(cmd);
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>"명령줄" → (실행 파일, 인자). 따옴표 없는 공백 경로는 실존 파일 기준으로 추정.</summary>
    public static (string Exe, string Args) SplitCommand(string command)
    {
        var cmd = command.Trim();
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            if (end > 0) return (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        var parts = cmd.Split(' ');
        for (int i = parts.Length; i >= 1; i--)
        {
            var candidate = string.Join(' ', parts[..i]);
            try { if (File.Exists(candidate)) return (candidate, string.Join(' ', parts[i..]).Trim()); }
            catch { }
        }
        var space = cmd.IndexOf(' ');
        return space < 0 ? (cmd, "") : (cmd[..space], cmd[(space + 1)..].Trim());
    }

    /// <summary>아이콘 추출용 경로 — DisplayIcon("path,index") → 제거 명령 exe → InstallLocation 폴더 순.</summary>
    public static (string Path, bool IsDirectory)? ResolveIconPath(InstalledApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.DisplayIcon))
        {
            var p = app.DisplayIcon.Trim().Trim('"');
            var comma = p.LastIndexOf(',');
            if (comma > 1 && int.TryParse(p[(comma + 1)..].Trim(), out _)) p = p[..comma];
            p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
            try { if (File.Exists(p)) return (p, false); } catch { }
        }
        if (!string.IsNullOrWhiteSpace(app.UninstallString))
        {
            var (exe, _) = SplitCommand(app.UninstallString);
            try { if (File.Exists(exe)) return (exe, false); } catch { }
        }
        if (!string.IsNullOrWhiteSpace(app.InstallLocation))
        {
            try { if (Directory.Exists(app.InstallLocation)) return (app.InstallLocation, true); } catch { }
        }
        return null;
    }

    // ── 잔재 스캔 (mac relatedFiles 대응) ────────────────────────────────

    /// <summary>
    /// %APPDATA%/%LOCALAPPDATA%/ProgramData/시작 메뉴/바탕 화면에서 제품명·회사명 매칭(1단계만).
    /// 느린 작업(폴더 크기 재귀 합산) — 반드시 백그라운드 스레드에서 호출.
    /// </summary>
    public static List<ResidueCandidate> ScanResidue(InstalledApp app, CancellationToken token)
    {
        var result = new List<ResidueCandidate>();
        var products = ProductIdentifiers(app);
        if (products.Count == 0) return result;   // 식별자 없음 — 오탐 방지 차원에서 스캔 포기
        var companies = CompanyIdentifiers(app.Publisher);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? installLoc = null;
        if (!string.IsNullOrWhiteSpace(app.InstallLocation))
        {
            try { installLoc = Path.GetFullPath(app.InstallLocation).TrimEnd('\\'); } catch { }
        }

        void Add(string path, bool isDir, string kind)
        {
            token.ThrowIfCancellationRequested();
            var norm = path.TrimEnd('\\');
            if (!seen.Add(norm)) return;
            // 설치 폴더 본체(및 하위)는 언인스톨러 담당 — 잔재 목록에서 제외
            if (installLoc is not null
                && (norm.Equals(installLoc, StringComparison.OrdinalIgnoreCase)
                    || norm.StartsWith(installLoc + "\\", StringComparison.OrdinalIgnoreCase))) return;
            long size = isDir ? DirectorySize(norm, token) : SafeFileSize(norm);
            result.Add(new ResidueCandidate(norm, size, isDir, kind));
        }

        // 1) 데이터 폴더 (1단계 항목만; 회사 폴더면 그 안 1단계 추가 탐색)
        foreach (var (root, kind) in DataRoots())
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            foreach (var dir in SafeDirs(root))
            {
                token.ThrowIfCancellationRequested();
                var dn = Path.GetFileName(dir);
                if (MatchesAny(dn, products)) Add(dir, true, kind);
                else if (companies.Any(c => dn.Equals(c, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var child in SafeDirs(dir))
                        if (MatchesAny(Path.GetFileName(child), products)) Add(child, true, kind);
                }
            }
        }

        // 2) 시작 메뉴 (사용자 + 공용): 제품명 폴더, 제품명 바로가기
        foreach (var sm in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        })
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(sm) || !Directory.Exists(sm)) continue;
            foreach (var dir in SafeDirs(sm))
                if (MatchesAny(Path.GetFileName(dir), products)) Add(dir, true, "시작 메뉴");
            foreach (var lnk in SafeFiles(sm, "*.lnk"))
                if (MatchesAny(Path.GetFileNameWithoutExtension(lnk), products)) Add(lnk, false, "시작 메뉴");
        }

        // 3) 바탕 화면 바로가기 (사용자 + 공용)
        foreach (var desk in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        })
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(desk) || !Directory.Exists(desk)) continue;
            foreach (var lnk in SafeFiles(desk, "*.lnk"))
                if (MatchesAny(Path.GetFileNameWithoutExtension(lnk), products)) Add(lnk, false, "바탕 화면");
        }

        return result;
    }

    // ── 식별자 휴리스틱 ──────────────────────────────────────────────────

    /// <summary>너무 일반적이라 매칭 식별자로 쓰면 안 되는 단어들.</summary>
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup", "install", "installer", "uninstall", "uninstaller", "update", "updater",
        "app", "apps", "application", "program", "programs", "windows", "microsoft",
        "system", "common", "data", "temp", "cache", "runtime", "redistributable",
    };

    /// <summary>제품 식별자: DisplayName 원문 + 버전/괄호 정리본 + 회사 접두 제거본 + 주 실행 파일명.</summary>
    private static List<string> ProductIdentifiers(InstalledApp app)
    {
        var ids = new List<string>();
        void AddId(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            var t = s.Trim();
            if (t.Length < 3 || GenericNames.Contains(t)) return;
            if (!ids.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase))) ids.Add(t);
        }

        AddId(app.DisplayName);
        var cleaned = CleanProductName(app.DisplayName);
        AddId(cleaned);

        // "회사 제품" 형태면 회사 접두를 뗀 제품명도 후보 ("Microsoft Edge" → "Edge")
        foreach (var company in CompanyIdentifiers(app.Publisher))
            if (cleaned.Length > company.Length + 1
                && cleaned.StartsWith(company + " ", StringComparison.OrdinalIgnoreCase))
                AddId(cleaned[(company.Length + 1)..]);

        // DisplayIcon이 가리키는 주 실행 파일 이름 (언인스톨러 unins*는 제외)
        if (!string.IsNullOrWhiteSpace(app.DisplayIcon))
        {
            var p = app.DisplayIcon.Trim().Trim('"');
            var comma = p.LastIndexOf(',');
            if (comma > 1 && int.TryParse(p[(comma + 1)..].Trim(), out _)) p = p[..comma];
            p = p.Trim().Trim('"');
            if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var baseName = Path.GetFileNameWithoutExtension(p);
                if (!baseName.StartsWith("unins", StringComparison.OrdinalIgnoreCase)) AddId(baseName);
            }
        }
        return ids;
    }

    private static readonly HashSet<string> ArchTokens = new(StringComparer.OrdinalIgnoreCase)
        { "x64", "x86", "arm64", "64-bit", "32-bit", "64비트", "32비트" };

    /// <summary>"7-Zip 23.01 (x64)" → "7-Zip": 괄호 묶음 제거 + 끝의 버전/아키텍처 토큰 제거.</summary>
    private static string CleanProductName(string name)
    {
        var s = Regex.Replace(name, @"\([^)]*\)", " ");
        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1 && (IsVersionToken(tokens[^1]) || ArchTokens.Contains(tokens[^1])))
            tokens.RemoveAt(tokens.Count - 1);
        return string.Join(' ', tokens).Trim();
    }

    private static bool IsVersionToken(string token)
    {
        var t = token.TrimStart('v', 'V');
        return t.Length > 0 && t.All(c => char.IsDigit(c) || c is '.' or ',');
    }

    private static readonly HashSet<string> LegalSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "inc", "inc.", "llc", "llc.", "ltd", "ltd.", "ltda", "co", "co.", "corp", "corp.",
        "corporation", "company", "gmbh", "ag", "s.a.", "sa", "srl", "limited", "주식회사", "(주)",
    };

    /// <summary>회사 식별자: 원문 + 법인 접미사("Inc." 등) 제거본. 폴더명과 '정확 일치'로만 사용.</summary>
    private static List<string> CompanyIdentifiers(string publisher)
    {
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(publisher)) return ids;
        var tokens = publisher.Replace(",", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1 && LegalSuffixes.Contains(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);
        foreach (var c in new[] { publisher.Trim(), string.Join(' ', tokens).Trim() })
            if (c.Length >= 2 && !ids.Any(x => x.Equals(c, StringComparison.OrdinalIgnoreCase)))
                ids.Add(c);
        return ids;
    }

    private static bool MatchesAny(string name, List<string> ids)
        => ids.Any(id => WordBoundaryMatch(name, id));

    /// <summary>
    /// mac belongs() 대응 단어 경계 매칭: 정확 일치이거나, 식별자(4자 이상)가
    /// 이름 안에 양쪽 비문자·숫자 경계로 등장 ("xcom.x.app"·"app2"류 오탐 차단).
    /// </summary>
    private static bool WordBoundaryMatch(string name, string id)
    {
        if (name.Equals(id, StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Length < 4) return false;   // 짧은 식별자는 정확 일치만
        var idx = name.IndexOf(id, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            var end = idx + id.Length;
            var startOk = idx == 0 || !char.IsLetterOrDigit(name[idx - 1]);
            var endOk = end == name.Length || !char.IsLetterOrDigit(name[end]);
            if (startOk && endOk) return true;
            idx = end >= name.Length ? -1 : name.IndexOf(id, idx + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    // ── 스캔 대상/크기 헬퍼 ──────────────────────────────────────────────

    private static IEnumerable<(string Root, string Kind)> DataRoots()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(roaming)) yield return (roaming, "앱 데이터 (Roaming)");
        if (!string.IsNullOrEmpty(local))
        {
            yield return (local, "앱 데이터 (Local)");
            yield return (Path.Combine(local, "Programs"), "설치 폴더 잔재");
        }
        if (!string.IsNullOrEmpty(programData)) yield return (programData, "프로그램 데이터");
    }

    private static string[] SafeDirs(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch { return Array.Empty<string>(); }
    }

    private static string[] SafeFiles(string root, string pattern)
    {
        try { return Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly); }
        catch { return Array.Empty<string>(); }
    }

    private static long SafeFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>재귀 합산 — 접근 불가/리파스 포인트는 건너뜀.</summary>
    private static long DirectorySize(string dir, CancellationToken token)
    {
        long total = 0;
        try
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var f in Directory.EnumerateFiles(dir, "*", opts))
            {
                if (token.IsCancellationRequested) break;
                total += SafeFileSize(f);
            }
        }
        catch { }
        return total;
    }
}
