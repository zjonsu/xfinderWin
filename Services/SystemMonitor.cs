// mac 소스 대응: Sources/XFinder/Services/SystemMonitor.swift + SMC.swift (AppleSMC 온도는 WMI 베스트에포트로 대체 — Smc.cs 별도 파일 없음)
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using XFinder.Models;

namespace XFinder.Services;

// ── 샘플 값 타입 (스펙 §2.1) ─────────────────────────────────────────────

/// <summary>CPU 사용률 샘플 — 모든 값은 0..100 퍼센트.</summary>
public readonly record struct CpuStat(double Total, double System, double User, double Idle)
{
    /// <summary>첫 샘플 기본값 — total 0, idle 100.</summary>
    public static CpuStat Default => new(0, 0, 0, 100);
}

/// <summary>메모리 샘플 — GB 단위 값은 전부 GiB(1024^3).</summary>
public readonly record struct MemStat
{
    public double UsedPercent { get; init; }   // 0..100
    public double App { get; init; }           // GiB — used - wired - compressed (근사, 음수 clamp)
    public double Active { get; init; }        // GiB — 활성 앱 메모리 근사 (App과 동일 산식)
    public double Wired { get; init; }         // GiB — 커널 고정 ≈ NonPaged Pool
    public double Compressed { get; init; }    // GiB — "Memory Compression" 프로세스 WorkingSet (권한에 따라 0)
    public double FreeGB { get; init; }        // GiB — 사용 가능 물리 메모리
    public double TotalGB { get; init; }       // GiB — 물리 메모리 전체
    public double SwapUsed { get; init; }      // GiB — 페이지파일 사용 근사 (커밋 - 물리 사용, 베스트에포트)
    public double SwapTotal { get; init; }     // GiB — 페이지파일 전체 근사 (TotalPageFile - TotalPhys)
    public double CacheGB { get; init; }       // GiB — 시스템 캐시(스탠바이) — Windows 추가 분류
    public double CommitUsedGB { get; init; }  // GiB — 커밋 사용량 — Windows 추가 분류
    public double CommitTotalGB { get; init; } // GiB — 커밋 한계 — Windows 추가 분류

    /// <summary>사용 중 메모리 GiB = max(0, TotalGB - FreeGB).</summary>
    public double UsedGB => Math.Max(0, TotalGB - FreeGB);

    /// <summary>메모리 압력(근사): (Wired + Compressed) / TotalGB × 100. TotalGB ≤ 0이면 0.</summary>
    public double Pressure => TotalGB > 0 ? (Wired + Compressed) / TotalGB * 100 : 0;
}

/// <summary>메모리 상위 프로세스 한 행. Id = Pid.</summary>
public sealed record MemProc(int Pid, string Name, double RssGB)
{
    public int Id => Pid;
}

/// <summary>CPU 상위 프로세스 한 행. Id = Pid. 멀티코어에서 100%를 넘을 수 있음(원본 동일).</summary>
public sealed record CpuProc(int Pid, string Name, double CpuPercent)
{
    public int Id => Pid;
}

/// <summary>디스크 도넛 카테고리. Id = 정의 순서 인덱스(팔레트 인덱스 겸용, 0..4).</summary>
public sealed record DiskCategory(int Id, string Name, long Bytes);

// ── 시스템 모니터 싱글턴 ─────────────────────────────────────────────────

/// <summary>
/// 앱 전역 시스템 모니터 싱글턴 — CPU/메모리/디스크 주기 샘플링(2초), 디스크 분류·파일 계산 캐시,
/// 상위 프로세스 목록. 모든 창/뷰가 같은 인스턴스를 관찰한다(창마다 만들지 말 것).
/// 앱 시작 시(첫 창 루트 onLoaded) UI 스레드에서 Start() 호출.
/// </summary>
public sealed class SystemMonitor : ObservableObject
{
    public static SystemMonitor Instance { get; } = new();
    /// <summary>mac 원본 이름(SystemMonitor.shared) 호환 별칭.</summary>
    public static SystemMonitor Shared => Instance;

    private SystemMonitor() { }

    /// <summary>cpuHistory 최대 길이 — 2초 간격 60개 = 2분 추이.</summary>
    public const int HistoryLimit = 60;

    // ── 관찰 프로퍼티 (스펙 §2.2) ──────────────────────────────────────

    private CpuStat _cpu = CpuStat.Default;
    /// <summary>최신 CPU 샘플.</summary>
    public CpuStat Cpu { get => _cpu; private set => Set(ref _cpu, value); }

    private MemStat _memory;
    /// <summary>최신 메모리 샘플.</summary>
    public MemStat Memory { get => _memory; private set => Set(ref _memory, value); }

    private readonly List<CpuStat> _cpuHistory = new();
    /// <summary>그래프용 최근 샘플(오래된 것이 앞, 최대 HistoryLimit개). 매 샘플마다 변경 알림.</summary>
    public IReadOnlyList<CpuStat> CpuHistory => _cpuHistory;

    private double? _cpuTemperature;
    /// <summary>CPU 온도 ℃. 못 읽으면 null 유지(마지막 정상값 유지 — 깜빡임 방지).</summary>
    public double? CpuTemperature { get => _cpuTemperature; private set => Set(ref _cpuTemperature, value); }

    private double _cpuUsage;
    /// <summary>0..1 분수 = clamp(Cpu.Total/100) — 툴바용.</summary>
    public double CpuUsage { get => _cpuUsage; private set => Set(ref _cpuUsage, value); }

    private double _memoryUsage;
    /// <summary>0..1 분수 = clamp(Memory.UsedPercent/100).</summary>
    public double MemoryUsage { get => _memoryUsage; private set => Set(ref _memoryUsage, value); }

    private double _diskUsage;
    /// <summary>0..1 분수 = used/total.</summary>
    public double DiskUsage { get => _diskUsage; private set => Set(ref _diskUsage, value); }

    private string _diskUsedText = "--";
    /// <summary>포맷된 디스크 사용량 텍스트(§7.3 휴먼 포맷). 초기값 "--".</summary>
    public string DiskUsedText { get => _diskUsedText; private set => Set(ref _diskUsedText, value); }

    private string _diskTotalText = "--";
    /// <summary>포맷된 디스크 전체 용량 텍스트. 초기값 "--".</summary>
    public string DiskTotalText { get => _diskTotalText; private set => Set(ref _diskTotalText, value); }

    private string _diskVolumeName = "디스크";
    /// <summary>볼륨 이름. 읽기 성공 시 볼륨 라벨, 라벨 없으면 "로컬 디스크 (C:)" 형식.</summary>
    public string DiskVolumeName { get => _diskVolumeName; private set => Set(ref _diskVolumeName, value); }

    private long _diskTotalBytes;
    /// <summary>부팅(시스템) 볼륨 전체 바이트.</summary>
    public long DiskTotalBytes { get => _diskTotalBytes; private set => Set(ref _diskTotalBytes, value); }

    private long _diskFreeBytes;
    /// <summary>사용 가능 바이트.</summary>
    public long DiskFreeBytes { get => _diskFreeBytes; private set => Set(ref _diskFreeBytes, value); }

    /// <summary>사용 중 바이트 = max(0, 전체 - 사용 가능).</summary>
    public long DiskUsedBytes => Math.Max(0, DiskTotalBytes - DiskFreeBytes);

    private IReadOnlyList<DiskCategory> _diskCategories = Array.Empty<DiskCategory>();
    /// <summary>도넛용 카테고리(응용 프로그램/다운로드/문서/데스크탑/기타). 초기 빈 목록.</summary>
    public IReadOnlyList<DiskCategory> DiskCategories { get => _diskCategories; private set => Set(ref _diskCategories, value); }

    private long? _diskTrashBytes;
    /// <summary>휴지통 크기(EmptyTrashAsync/ScanTrash 후에만 채워짐 — 현재 UI 미표시).</summary>
    public long? DiskTrashBytes { get => _diskTrashBytes; private set => Set(ref _diskTrashBytes, value); }

    private double? _diskTemperature;
    /// <summary>드라이브 온도 ℃. 못 읽으면 null(UI는 "—" 폴백).</summary>
    public double? DiskTemperature { get => _diskTemperature; private set => Set(ref _diskTemperature, value); }

    private string? _diskHealthValue;
    /// <summary>S.M.A.R.T. 상태 요약("정상"/"주의"/원문/"지원 안 함"). null = 아직 미확인.</summary>
    public string? DiskHealthValue { get => _diskHealthValue; private set => Set(ref _diskHealthValue, value); }

    private string? _diskHealthDesc;
    /// <summary>S.M.A.R.T. 상태 설명 문장.</summary>
    public string? DiskHealthDesc { get => _diskHealthDesc; private set => Set(ref _diskHealthDesc, value); }

    private DateTime? _diskComputedAt;
    /// <summary>마지막 디스크 스캔 완료 시각(캐시 유무 판단 기준). 표시 포맷 "HH:mm".</summary>
    public DateTime? DiskComputedAt { get => _diskComputedAt; private set => Set(ref _diskComputedAt, value); }

    private bool _diskComputing;
    /// <summary>디스크 분류 스캔 진행 중 플래그("다시 계산" 버튼 disabled 근거).</summary>
    public bool DiskComputing { get => _diskComputing; private set => Set(ref _diskComputing, value); }

    private IReadOnlyList<TypeBreakdown>? _fileTypeStats;
    /// <summary>홈 폴더 종류별 파일 계산 캐시. null = 아직 안 함. 메모리 캐시만 유지(파일 저장 금지 — 스펙 §8).</summary>
    public IReadOnlyList<TypeBreakdown>? FileTypeStats { get => _fileTypeStats; private set => Set(ref _fileTypeStats, value); }

    private bool _fileTypeComputing;
    /// <summary>종류별 파일 계산 진행 중 플래그.</summary>
    public bool FileTypeComputing { get => _fileTypeComputing; private set => Set(ref _fileTypeComputing, value); }

    /// <summary>칩 이름 — 레지스트리 ProcessorNameString → WMI Win32_Processor.Name → "프로세서" 폴백.</summary>
    public string CpuName { get; } = ReadCpuName();

    /// <summary>논리 코어 수.</summary>
    public int CoreCount { get; } = Environment.ProcessorCount;

    /// <summary>시스템 가동 시간 (Environment.TickCount64 기반). 갱신 알림 없음 — 팝업 타이머가 다시 읽음.</summary>
    public TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    // ── 비관찰(내부) 상태 ─────────────────────────────────────────────

    private DispatcherTimer? _timer;
    private (ulong Idle, ulong Kernel, ulong User)? _prevCpu;        // 이전 CPU 누적 틱
    private Dictionary<int, long> _prevProcCpu = new();              // 프로세스별 누적 CPU 틱(100ns)
    private DateTime? _prevProcSampleTime;
    private bool _slowSampling;                                      // 백그라운드 샘플 중복 방지
    private bool _cpuTempUnsupported;                                // 온도 전부 실패 → 이후 즉시 null (원본 SMC open 실패 대응)

    // ── 타이머 (스펙 §3.1) ────────────────────────────────────────────

    /// <summary>샘플링 시작 — 이미 동작 중이면 무시. 즉시 1회 샘플 후 2초 간격. UI 스레드에서 호출할 것.</summary>
    public void Start()
    {
        if (_timer is not null) return;
        Sample();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.0) };
        _timer.Tick += (_, _) => Sample();
        _timer.Start();
    }

    /// <summary>샘플링 중지. (앱에서는 호출하는 곳 없음 — 원본 동일.)</summary>
    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    // ── sample() — 매 틱 (스펙 §3.2) ──────────────────────────────────

    private void Sample()
    {
        // 1) CPU·메모리는 가벼운 커널 호출 — UI 스레드에서 즉시 (prevCPU 델타 연속성 유지).
        Cpu = ReadCpu();
        Memory = ReadMemory();
        CpuUsage = Math.Clamp(Cpu.Total / 100.0, 0, 1);
        MemoryUsage = Math.Clamp(Memory.UsedPercent / 100.0, 0, 1);
        _cpuHistory.Add(Cpu);
        if (_cpuHistory.Count > HistoryLimit)
            _cpuHistory.RemoveRange(0, _cpuHistory.Count - HistoryLimit);
        OnPropertyChanged(nameof(CpuHistory));

        // 2) 디스크 용량·온도는 느릴 수 있어 백그라운드로 읽고 결과만 UI 스레드에 반영
        //    (메인에서 2초마다 돌리면 클릭과 겹칠 때 간헐적 끊김 — 원본 주석).
        SampleSlow();
    }

    private async void SampleSlow()
    {
        if (_slowSampling) return;
        _slowSampling = true;
        try
        {
            var (snap, temp) = await Task.Run(() => (DiskUsageSnapshot(), ReadCpuTemperature()));
            if (snap is { } s)
            {
                DiskUsage = s.Usage;
                DiskUsedText = s.UsedText;
                DiskTotalText = s.TotalText;
            }
            if (temp is { } t)
                CpuTemperature = t;   // 정상값을 얻었을 때만 갱신 (실패 시 기존값 유지 = 깜빡임 방지)
        }
        catch { /* 베스트에포트 — 조용히 무시 */ }
        finally { _slowSampling = false; }
    }

    // ── CPU 사용률 (스펙 §3.3 — GetSystemTimes 델타) ──────────────────

    private CpuStat ReadCpu()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            return _cpu;   // 호출 실패 시 직전 값 유지
        ulong idle = idleFt.Value, kernel = kernelFt.Value, user = userFt.Value;

        if (_prevCpu is not { } prev)
        {
            _prevCpu = (idle, kernel, user);
            return CpuStat.Default;   // 첫 샘플은 기본값 (total 0, idle 100)
        }

        double dIdle = idle >= prev.Idle ? idle - prev.Idle : 0;
        double dKernel = kernel >= prev.Kernel ? kernel - prev.Kernel : 0;
        double dUser = user >= prev.User ? user - prev.User : 0;
        _prevCpu = (idle, kernel, user);

        // 주의: Windows의 kernel 시간은 idle을 포함 → ΔSys = ΔKernel - ΔIdle.
        double total = dKernel + dUser;
        if (total <= 0) return _cpu;   // 델타 0이면 직전 값 유지

        double userPct = dUser / total * 100;
        double sysPct = Math.Max(0, dKernel - dIdle) / total * 100;
        double idlePct = Math.Clamp(dIdle / total * 100, 0, 100);
        return new CpuStat(userPct + sysPct, sysPct, userPct, idlePct);
    }

    // ── 메모리 분류 (스펙 §3.4 — GlobalMemoryStatusEx + GetPerformanceInfo) ──

    private MemStat ReadMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
            return _memory;   // 실패 시 직전 값 유지

        const double GiB = 1024.0 * 1024.0 * 1024.0;
        double totalGB = status.ullTotalPhys / GiB;
        double freeGB = status.ullAvailPhys / GiB;
        double usedGB = Math.Max(0, totalGB - freeGB);
        double usedPercent = totalGB > 0 ? usedGB / totalGB * 100 : 0;

        // Wired ≈ 커널 NonPaged Pool, Cache ≈ 시스템 캐시(스탠바이) — psapi GetPerformanceInfo.
        double wired = 0, cache = 0;
        if (GetPerformanceInfo(out var perf, (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>()))
        {
            double pageSize = perf.PageSize.ToUInt64();
            wired = perf.KernelNonpaged.ToUInt64() * pageSize / GiB;
            cache = perf.SystemCache.ToUInt64() * pageSize / GiB;
        }

        // Compressed ≈ "Memory Compression" 프로세스 WorkingSet (권한에 따라 0).
        double compressed = MemoryCompressionWorkingSetGiB();

        // App/Active ≈ used - wired - compressed (음수 방지 clamp) — 베스트에포트 근사.
        double active = Math.Max(0, usedGB - wired - compressed);

        // 스왑(페이지파일) — 커밋 기반 근사 (베스트에포트, 0이어도 UI는 "0.00GB" 표시).
        double commitTotal = status.ullTotalPageFile / GiB;
        double commitUsed = Math.Max(0, (status.ullTotalPageFile >= status.ullAvailPageFile
            ? status.ullTotalPageFile - status.ullAvailPageFile : 0) / GiB);
        double swapTotal = Math.Max(0, commitTotal - totalGB);
        double swapUsed = Math.Clamp(commitUsed - usedGB, 0, swapTotal);

        return new MemStat
        {
            UsedPercent = usedPercent,
            App = active,
            Active = active,
            Wired = wired,
            Compressed = compressed,
            FreeGB = freeGB,
            TotalGB = totalGB,
            SwapUsed = swapUsed,
            SwapTotal = swapTotal,
            CacheGB = cache,
            CommitUsedGB = commitUsed,
            CommitTotalGB = commitTotal,
        };
    }

    private static double MemoryCompressionWorkingSetGiB()
    {
        try
        {
            double bytes = 0;
            foreach (var p in Process.GetProcessesByName("Memory Compression"))
            {
                try { bytes += p.WorkingSet64; }
                catch { /* 보호된 프로세스 — 0 취급 */ }
                finally { p.Dispose(); }
            }
            return bytes / (1024.0 * 1024.0 * 1024.0);
        }
        catch { return 0; }
    }

    // ── 디스크 용량 (스펙 §3.5) ────────────────────────────────────────

    private static string SystemDriveRoot => Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    /// <summary>(usage 0..1, usedText, totalText) — §7.3 휴먼 포맷. 실패 시 null.</summary>
    private static (double Usage, string UsedText, string TotalText)? DiskUsageSnapshot()
    {
        try
        {
            var drive = new DriveInfo(SystemDriveRoot);
            long total = drive.TotalSize;
            if (total <= 0) return null;
            long free = drive.AvailableFreeSpace;
            long used = Math.Max(0, total - free);
            return (Math.Clamp(used / (double)total, 0, 1), Human(used), Human(total));
        }
        catch { return null; }
    }

    /// <summary>볼륨 이름/전체/가용 바이트 갱신 — DriveInfo라 빠름.</summary>
    private void ReadDiskCapacity()
    {
        try
        {
            var drive = new DriveInfo(SystemDriveRoot);
            DiskTotalBytes = drive.TotalSize;
            DiskFreeBytes = drive.AvailableFreeSpace;
            OnPropertyChanged(nameof(DiskUsedBytes));
            var label = drive.VolumeLabel;
            DiskVolumeName = string.IsNullOrWhiteSpace(label)
                ? $"로컬 디스크 ({drive.Name.TrimEnd('\\')})"
                : label;
        }
        catch { /* 볼륨 정보 읽기 실패 — 기존값 유지 */ }
    }

    /// <summary>디스크 팝업용 휴먼 포맷(1024진법): ≥1TB "%.2fTB", ≥1GB "%.2fGB", ≥1MB "%.1fMB", ≥1KB "%.0fKB", 그 외 "{b}B".</summary>
    public static string Human(long bytes)
    {
        const double K = 1024.0;
        if (bytes >= K * K * K * K) return $"{bytes / (K * K * K * K):F2}TB";
        if (bytes >= K * K * K) return $"{bytes / (K * K * K):F2}GB";
        if (bytes >= K * K) return $"{bytes / (K * K):F1}MB";
        if (bytes >= K) return $"{bytes / K:F0}KB";
        return $"{bytes}B";
    }

    // ── refreshDisk(force:) — 디스크 팝업 캐시 동작 (스펙 §5.1) ─────────

    /// <summary>
    /// 디스크 팝업이 열릴 때 호출. 용량/온도는 항상 갱신하고, 분류·건강 상태 스캔은
    /// 캐시(DiskComputedAt)가 있으면 force=true일 때만 다시 수행. UI 스레드에서 호출할 것.
    /// </summary>
    public async Task RefreshDiskAsync(bool force = false, CancellationToken ct = default)
    {
        // 1) 항상(저렴): 볼륨 이름/total/free + 드라이브 온도(WMI는 백그라운드) + 휴지통 크기.
        ReadDiskCapacity();
        try
        {
            var (temp, trash) = await Task.Run(() => (ReadDriveTemperature(), ShellInterop.QueryRecycleBin().Bytes), ct);
            DiskTemperature = temp;   // null이어도 대입 (원본 동일 — UI는 "—" 폴백)
            DiskTrashBytes = trash;
        }
        catch { /* 베스트에포트 */ }

        // 2) 중복 실행 방지 / 3) 캐시 재사용 (팝업 재오픈 시 재스캔 없음).
        if (DiskComputing) return;
        if (!force && DiskComputedAt is not null) return;

        // 4) 분류 + 건강 상태 비동기 스캔.
        DiskComputing = true;
        try
        {
            long usedBytes = DiskUsedBytes;
            var categoriesTask = Task.Run(() => ScanCategories(usedBytes, ct), ct);
            var healthTask = Task.Run(ScanHealth, ct);
            await Task.WhenAll(categoriesTask, healthTask);
            DiskCategories = categoriesTask.Result;
            (DiskHealthValue, DiskHealthDesc) = healthTask.Result;
            DiskComputedAt = DateTime.Now;
        }
        catch { /* 취소/실패 — 캐시 시각 미기록(다음 오픈 때 재시도) */ }
        finally { DiskComputing = false; }
    }

    // ── refreshFileTypes(force:) — 종류별 파일 계산 캐시 (스펙 §5.2) ────

    /// <summary>
    /// 홈 폴더 종류별 파일 계산 — 실제 스캔은 FileSystemService.SizeByFileType에 위임,
    /// 여기서는 캐시(FileTypeStats)와 진행 플래그만 관리. 캐시가 있으면 force=true일 때만 재계산.
    /// </summary>
    public async Task RefreshFileTypesAsync(bool force = false, CancellationToken ct = default)
    {
        if (FileTypeComputing) return;
        if (!force && FileTypeStats is not null) return;

        FileTypeComputing = true;
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var sw = Stopwatch.StartNew();
            IReadOnlyList<TypeBreakdown> stats = await Task.Run(() => FileSystemService.SizeByFileType(home), ct);
            sw.Stop();
            Debug.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[XFinder] 종류별 파일 계산: {0:F2}s ({1}개)", sw.Elapsed.TotalSeconds, stats.Sum(s => s.Count)));
            FileTypeStats = stats;
        }
        catch { /* 취소/실패 — 캐시 미기록 */ }
        finally { FileTypeComputing = false; }
    }

    // ── scanCategories — 도넛 카테고리 (스펙 §5.3) ─────────────────────

    private static IReadOnlyList<DiskCategory> ScanCategories(long usedBytes, CancellationToken ct)
    {
        var defs = new (string Name, string?[] Paths)[]
        {
            ("응용 프로그램", new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            }),
            ("다운로드", new[] { DownloadsPath() }),
            ("문서", new[] { Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) }),
            ("데스크탑", new[] { Environment.GetFolderPath(Environment.SpecialFolder.Desktop) }),
        };

        var sizes = new long[defs.Length];
        Parallel.For(0, defs.Length, new ParallelOptions { CancellationToken = ct }, i =>
        {
            long sum = 0;
            // 같은 실제 폴더가 중복 지정된 경우(32비트 OS 등) 한 번만 계산.
            var paths = defs[i].Paths
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (Directory.Exists(path))
                    sum += FileSystemService.FolderSize(path);   // 재귀 합계, 숨김 포함, 리파스 포인트 스킵
            }
            sizes[i] = sum;
        });

        var list = new List<DiskCategory>(defs.Length + 1);
        for (int i = 0; i < defs.Length; i++)
            list.Add(new DiskCategory(i, defs[i].Name, sizes[i]));
        list.Add(new DiskCategory(defs.Length, "기타", Math.Max(0, usedBytes - sizes.Sum())));
        return list;
    }

    /// <summary>다운로드 폴더 — SHGetKnownFolderPath(FOLDERID_Downloads), 실패 시 %USERPROFILE%\Downloads.</summary>
    private static string DownloadsPath()
    {
        IntPtr ptr = IntPtr.Zero;
        try
        {
            var rfid = FolderDownloads;
            if (SHGetKnownFolderPath(in rfid, 0, IntPtr.Zero, out ptr) == 0)
            {
                var path = Marshal.PtrToStringUni(ptr);
                if (!string.IsNullOrEmpty(path)) return path;
            }
        }
        catch { /* 폴백 사용 */ }
        finally { if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr); }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    // ── S.M.A.R.T. 건강 상태 (스펙 §4.3) ───────────────────────────────

    private static (string Value, string Desc) ScanHealth()
    {
        // 1) root\WMI MSStorageDriver_FailurePredictStatus.PredictFailure
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT PredictFailure FROM MSStorageDriver_FailurePredictStatus");
            bool any = false, failing = false;
            foreach (var mo in searcher.Get())
            {
                using (mo)
                {
                    any = true;
                    if (mo["PredictFailure"] is bool b && b) failing = true;
                }
            }
            if (any)
            {
                return failing
                    ? ("주의", "드라이브에 문제가 감지되었습니다. 백업을 권장합니다.")
                    : ("정상", "드라이브 상태가 정상(Verified)으로 확인되었습니다.");
            }
        }
        catch { /* 다음 방법 시도 */ }

        // 2) root\CIMV2 Win32_DiskDrive.Status ("OK"/"Pred Fail"/…)
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Status FROM Win32_DiskDrive");
            string? other = null;
            bool anyOk = false, anyFail = false;
            foreach (var mo in searcher.Get())
            {
                using (mo)
                {
                    var s = (mo["Status"] as string)?.Trim();
                    if (string.IsNullOrEmpty(s)) continue;
                    if (s.Equals("OK", StringComparison.OrdinalIgnoreCase)) anyOk = true;
                    else if (s.Equals("Pred Fail", StringComparison.OrdinalIgnoreCase)) anyFail = true;
                    else other ??= s;
                }
            }
            if (anyFail) return ("주의", "드라이브에 문제가 감지되었습니다. 백업을 권장합니다.");
            if (anyOk) return ("정상", "드라이브 상태가 정상(Verified)으로 확인되었습니다.");
            if (other is not null) return (other, "드라이브 S.M.A.R.T. 상태입니다.");
        }
        catch { /* 폴백으로 */ }

        return ("지원 안 함", "이 드라이브는 S.M.A.R.T. 상태를 제공하지 않습니다.");
    }

    // ── 휴지통 (스펙 §5.5 — 현재 UI 미노출, 후순위) ─────────────────────

    /// <summary>휴지통 크기를 다시 조회해 DiskTrashBytes 갱신.</summary>
    public void ScanTrash() => DiskTrashBytes = ShellInterop.QueryRecycleBin().Bytes;

    /// <summary>휴지통 비우기 후 크기 재조회.</summary>
    public async Task EmptyTrashAsync(CancellationToken ct = default)
    {
        await Task.Run(ShellInterop.EmptyRecycleBin, ct);
        ScanTrash();
    }

    // ── 온도 — WMI 베스트에포트 (스펙 §4, SMC.swift 대체) ───────────────

    /// <summary>
    /// CPU 온도 ℃ — ① MSAcpi_ThermalZoneTemperature(0.1K) ② ThermalZoneInformation(K).
    /// 열 영역 여러 개면 유효 범위(20–110℃) 값들의 평균. 전부 실패하면 null(이후 호출은 즉시 null — 원본 SMC open 실패 대응).
    /// </summary>
    public double? ReadCpuTemperature()
    {
        if (_cpuTempUnsupported) return null;
        var t = QueryWmiAverage(@"root\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature",
                "CurrentTemperature", v => v / 10.0 - 273.15, 20, 110)
            ?? QueryWmiAverage(@"root\CIMV2",
                "SELECT Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation",
                "Temperature", v => v - 273.15, 20, 110);
        if (t is null) _cpuTempUnsupported = true;
        return t;
    }

    /// <summary>
    /// 드라이브 온도 ℃ — ① MSFT_StorageReliabilityCounter.Temperature(℃) ② ATAPI SMART 속성 194/190.
    /// 유효 범위 10–100℃ 평균. 실패 시 null(UI는 "—" 폴백).
    /// </summary>
    public double? ReadDriveTemperature()
    {
        return QueryWmiAverage(@"root\Microsoft\Windows\Storage",
                "SELECT Temperature FROM MSFT_StorageReliabilityCounter",
                "Temperature", v => v, 10, 100)
            ?? ReadAtapiSmartTemperature();
    }

    private static double? QueryWmiAverage(string scope, string query, string property,
        Func<double, double> convert, double min, double max)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            double sum = 0;
            int count = 0;
            foreach (var mo in searcher.Get())
            {
                using (mo)
                {
                    var raw = mo[property];
                    if (raw is null) continue;
                    double celsius = convert(Convert.ToDouble(raw, CultureInfo.InvariantCulture));
                    if (celsius < min || celsius > max) continue;   // 유효 범위 필터 (스펙 §4.2)
                    sum += celsius;
                    count++;
                }
            }
            return count > 0 ? sum / count : null;
        }
        catch { return null; }
    }

    private static double? ReadAtapiSmartTemperature()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT VendorSpecific FROM MSStorageDriver_ATAPISmartData");
            double sum = 0;
            int count = 0;
            foreach (var mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo["VendorSpecific"] is not byte[] data) continue;
                    // SMART 속성 테이블: 오프셋 2부터 12바이트 단위, [0]=속성 ID, [5]=원시값(현재 온도 ℃).
                    for (int off = 2; off + 12 <= data.Length; off += 12)
                    {
                        byte id = data[off];
                        if (id != 194 && id != 190) continue;   // 194 Temperature, 190 Airflow Temperature
                        double celsius = data[off + 5];
                        if (celsius is >= 10 and <= 100) { sum += celsius; count++; }
                        break;
                    }
                }
            }
            return count > 0 ? sum / count : null;
        }
        catch { return null; }
    }

    // ── 프로세스 목록 (스펙 §6) ────────────────────────────────────────

    /// <summary>
    /// 메모리 상위 프로세스 — WorkingSet 1MB(1,000,000B) 초과만, RSS GiB 내림차순 상위 limit개.
    /// 접근 불가 프로세스는 스킵. 동기 호출(필요하면 호출부에서 Task.Run).
    /// </summary>
    public List<MemProc> TopMemoryProcesses(int limit = 6)
    {
        var result = new List<MemProc>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                long rss = p.WorkingSet64;
                if (rss <= 1_000_000) continue;   // ≈1MB 이하 제외 (원본 동일)
                var name = p.ProcessName;
                if (string.IsNullOrEmpty(name)) name = $"PID {p.Id}";
                result.Add(new MemProc(p.Id, name, rss / (1024.0 * 1024.0 * 1024.0)));
            }
            catch { /* 접근 불가 프로세스 스킵 */ }
            finally { p.Dispose(); }
        }
        return result.OrderByDescending(x => x.RssGB).Take(limit).ToList();
    }

    /// <summary>
    /// CPU 상위 프로세스 — TotalProcessorTime의 이전 호출과의 델타 / 경과초 × 100.
    /// 첫 호출은 빈 결과(팝업이 "측정 중…" 표시). 새 PID·시간 역전은 스킵.
    /// 주의: prev 맵이 인스턴스에 1개뿐 — 호출처를 CPU 팝업 타이머(2초) 하나로 유지할 것.
    /// 멀티코어에서 100%를 넘을 수 있음(원본 동일 — 코어 수로 나누지 않음).
    /// </summary>
    private readonly object _procCpuLock = new();   // 다중 창 CPU 팝업의 델타 상태 경합 방지

    public List<CpuProc> TopCpuProcesses(int limit = 5)
    {
        lock (_procCpuLock)
            return TopCpuProcessesCore(limit);
    }

    private List<CpuProc> TopCpuProcessesCore(int limit)
    {
        var now = DateTime.UtcNow;
        var current = new Dictionary<int, long>();
        var names = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                current[p.Id] = p.TotalProcessorTime.Ticks;   // 접근 불가(Idle/System 등)는 throw → 스킵
                names[p.Id] = p.ProcessName;
            }
            catch { /* 스킵 */ }
            finally { p.Dispose(); }
        }

        var result = new List<CpuProc>();
        if (_prevProcSampleTime is { } prevTime)
        {
            double elapsed = (now - prevTime).TotalSeconds;
            if (elapsed > 0)
            {
                foreach (var (pid, ticks) in current)
                {
                    if (!_prevProcCpu.TryGetValue(pid, out var prev)) continue;   // 새로 등장한 PID 스킵
                    if (ticks < prev) continue;                                   // 시간 역전 스킵
                    double percent = (ticks - prev) / (double)TimeSpan.TicksPerSecond / elapsed * 100.0;
                    var name = names.TryGetValue(pid, out var n) && n.Length > 0 ? n : $"PID {pid}";
                    result.Add(new CpuProc(pid, name, percent));
                }
            }
        }

        _prevProcCpu = current;
        _prevProcSampleTime = now;
        return result.OrderByDescending(x => x.CpuPercent).Take(limit).ToList();
    }

    /// <summary>
    /// "종료" 버튼 — 메인 윈도우가 있으면 정상 종료 요청(CloseMainWindow), 없거나 거부되면 Kill.
    /// 권한 부족(시스템 프로세스)은 조용히 무시(원본 SIGTERM 실패 무시와 동일).
    /// 호출부는 0.8초 뒤 목록 reload 권장(스펙 §6.3).
    /// </summary>
    public void Quit(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.MainWindowHandle == IntPtr.Zero || !p.CloseMainWindow())
                p.Kill();
        }
        catch { /* 조용히 무시 */ }
    }

    /// <summary>프로세스 실행 파일 경로 — 아이콘 추출용(IconCache/ExtractAssociatedIcon). 접근 불가 시 null.</summary>
    public static string? TryGetProcessPath(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainModule?.FileName;
        }
        catch { return null; }
    }

    /// <summary>표시 이름 — FileVersionInfo.FileDescription(있으면) → 폴백(프로세스 이름). 스펙 §6.4.</summary>
    public static string ProcessDisplayName(int pid, string fallback)
    {
        try
        {
            var path = TryGetProcessPath(pid);
            if (path is not null)
            {
                var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) return desc;
            }
        }
        catch { /* 폴백 사용 */ }
        return fallback;
    }

    // ── 칩 이름 ────────────────────────────────────────────────────────

    private static string ReadCpuName()
    {
        try
        {
            if (Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString", null) is string fromRegistry
                && !string.IsNullOrWhiteSpace(fromRegistry))
                return fromRegistry.Trim();
        }
        catch { /* WMI로 폴백 */ }
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo["Name"] is string name && !string.IsNullOrWhiteSpace(name))
                        return name.Trim();
                }
            }
        }
        catch { /* 폴백 사용 */ }
        return "프로세서";
    }

    // ── P/Invoke ──────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FILETIME64
    {
        public readonly uint Low;
        public readonly uint High;
        public ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME64 lpIdleTime, out FILETIME64 lpKernelTime, out FILETIME64 lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal;
        public UIntPtr CommitLimit;
        public UIntPtr CommitPeak;
        public UIntPtr PhysicalTotal;
        public UIntPtr PhysicalAvailable;
        public UIntPtr SystemCache;
        public UIntPtr KernelTotal;
        public UIntPtr KernelPaged;
        public UIntPtr KernelNonpaged;
        public UIntPtr PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

    private static readonly Guid FolderDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
