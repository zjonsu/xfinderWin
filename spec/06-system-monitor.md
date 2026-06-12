# 06 — 시스템 모니터 (CPU / 메모리 / 디스크) 포팅 스펙

macOS 원본 소스:
- `Sources/XFinder/Services/SystemMonitor.swift` — 샘플링 싱글턴, 디스크/파일계산 캐시, 프로세스 목록
- `Sources/XFinder/Services/SMC.swift` — AppleSMC IOKit 온도 읽기
- `Sources/XFinder/Views/CPUDetailView.swift` — CPU 팝업
- `Sources/XFinder/Views/MemoryDetailView.swift` — 메모리 팝업
- `Sources/XFinder/Views/DiskDetailView.swift` — 디스크 팝업
- 연관: `Services/FileSystemService.swift`(sizeByFileType/folderSize), `Views/RootView.swift`(SystemStatsView, PathBar), `Model/AppModel.swift`(typeMode 페이징), `Model/PaneTab.swift`(typeMode 상태), `doc/system-monitor.md`

---

## 1. 전체 구조 개요

- **`SystemMonitor`는 앱 전역 싱글턴** (`SystemMonitor.shared`). 모든 창/뷰가 같은 인스턴스를 관찰한다. 창마다 인스턴스를 만들지 말 것 (doc 명시).
  - 앱 시작 시(첫 창 루트 뷰 onAppear) `SystemMonitor.shared.start()` 호출.
  - C# 대응: `public sealed class SystemMonitor : INotifyPropertyChanged { public static SystemMonitor Shared { get; } = new(); }` + `DispatcherTimer`.
- 툴바 우측 `SystemStatsView`: CPU/메모리/디스크 3개 컴팩트 표시(아이콘+퍼센트). 각각 클릭 → 아래 화살표 popover로 상세 팝업.
- 상세 팝업 3종: CPUDetailView, MemoryDetailView, DiskDetailView.
- 디스크 팝업의 "파일 계산" 카테고리 행 클릭 → 팝업 닫고 부모창 오른쪽 패널을 `typeMode`(종류별 파일 내역, 크기순, 무한 스크롤 페이징)로 전환.

---

## 2. 데이터 구조 (C# 변환용)

### 2.1 샘플 값 타입

```csharp
// CPUStat — 모든 값은 0..100 퍼센트
public struct CpuStat {
    public double Total;   // 기본 0   (= User + System)
    public double System;  // 기본 0
    public double User;    // 기본 0   (user + nice)
    public double Idle;    // 기본 100
}

public struct MemStat {
    public double UsedPercent;            // 0..100
    public double App, Active, Wired, Compressed, FreeGB, TotalGB; // 전부 GiB 단위 (1024^3)
    public double SwapUsed, SwapTotal;    // GiB
    // 메모리 압력(근사): (Wired + Compressed) / TotalGB * 100, TotalGB <= 0 이면 0
    public double Pressure => TotalGB > 0 ? (Wired + Compressed) / TotalGB * 100 : 0;
}

public record MemProc(int Pid, string Name, double RssGB);      // Identifiable: id = pid
public record CpuProc(int Pid, string Name, double CpuPercent); // Identifiable: id = pid
public record DiskCategory(int Id, string Name, long Bytes);    // Id = 정의 순서 인덱스
```

### 2.2 SystemMonitor 관찰 프로퍼티

| 프로퍼티 | 타입 | 의미 / 초기값 |
|---|---|---|
| `cpu` | `CPUStat` | 최신 CPU 샘플 |
| `memory` | `MemStat` | 최신 메모리 샘플 |
| `cpuHistory` | `[CPUStat]` | 그래프용 최근 N개(오래된 것이 앞). `historyLimit = 60` 초과분은 앞에서 제거 |
| `cpuTemperature` | `Double?` | CPU 온도 ℃. 못 읽으면 nil 유지(마지막 정상값 유지 — 깜빡임 방지) |
| `cpuUsage` | `Double` | 0..1 분수 = clamp(cpu.total/100) (툴바용) |
| `memoryUsage` | `Double` | 0..1 분수 = clamp(memory.usedPercent/100) |
| `diskUsage` | `Double` | 0..1 분수 = used/total |
| `diskUsedText` | `String` | 포맷된 사용량 텍스트, 초기값 `"--"` |
| `diskTotalText` | `String` | 포맷된 전체 용량 텍스트, 초기값 `"--"` |
| `diskVolumeName` | `String` | 초기값 `"디스크"`; 읽기 성공 시 볼륨 이름(실패 폴백 `"Macintosh HD"` → Windows에선 예: `"로컬 디스크 (C:)"` 또는 `DriveInfo.VolumeLabel`) |
| `diskTotalBytes` | `Int64` | 부팅 볼륨 전체 바이트, 초기 0 |
| `diskFreeBytes` | `Int64` | 사용 가능 바이트, 초기 0 |
| `diskCategories` | `[DiskCategory]` | 도넛용 카테고리(아래 §5.3), 초기 `[]` |
| `diskTrashBytes` | `Int64?` | 휴지통 크기(emptyTrash 후에만 채워짐 — 현재 UI 미표시) |
| `diskTemperature` | `Double?` | 드라이브 온도 ℃ |
| `diskHealthValue` | `String?` | S.M.A.R.T. 상태 요약("정상"/"주의"/원문/"지원 안 함") |
| `diskHealthDesc` | `String?` | 상태 설명 문장 |
| `diskComputedAt` | `Date?` | 마지막 디스크 스캔 완료 시각(캐시 유무 판단 기준) |
| `diskComputing` | `Bool` | 디스크 스캔 진행 중 플래그 |
| `fileTypeStats` | `[TypeBreakdown]?` | 홈 폴더 종류별 파일 계산 캐시. nil = 아직 안 함 |
| `fileTypeComputing` | `Bool` | 파일 계산 진행 중 플래그 |
| `diskUsedBytes` | 계산 | `max(0, diskTotalBytes - diskFreeBytes)` |
| `cpuName` | `String` (상수) | 칩 이름. mac: `sysctl machdep.cpu.brand_string`, 실패 시 `"프로세서"`. Windows: WMI `Win32_Processor.Name` 또는 레지스트리 `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\ProcessorNameString`, 실패 시 `"프로세서"` |
| `uptime` | `TimeInterval` 계산 | 시스템 가동 시간(초). Windows: `Environment.TickCount64 / 1000.0` (또는 `GetTickCount64`) |

비관찰(내부) 상태: `historyLimit = 60`, `timer`, `prevCPU`(이전 CPU 틱), `prevProcCPU: [pid: UInt64]`(프로세스별 누적 CPU ns), `prevProcSampleTime: Date?`.

### 2.3 TypeBreakdown / TypeFileEntry (FileSystemService)

```csharp
public record TypeBreakdown(string Name, long Bytes, int Count, IReadOnlyList<TypeFileEntry> Files);
// Files: 크기 내림차순 정렬, 최대 TypeIndexLimit(200_000)개. UI는 여기서 페이지 단위로 잘라 씀.

public record TypeFileEntry(string Path, long Size); // id = Path
```

- 카테고리 순서 상수: `fileTypeOrder = ["문서", "이미지", "동영상", "음악", "압축", "기타"]` (마지막은 항상 "기타").
- 확장자 → 카테고리 맵 (`fileTypeMap`, 소문자 비교, 미등록은 "기타"):
  - **문서**: pdf, doc, docx, xls, xlsx, ppt, pptx, txt, hwp, hwpx, pages, numbers, key, md, csv, rtf, odt, ods, odp, epub
  - **이미지**: jpg, jpeg, png, gif, heic, heif, webp, tiff, tif, bmp, svg, raw, cr2, nef, dng, psd, ai
  - **동영상**: mp4, mov, avi, mkv, m4v, wmv, flv, webm, mpg, mpeg, 3gp
  - **음악**: mp3, wav, aac, flac, m4a, aiff, aif, ogg, wma, opus
  - **압축**: zip, rar, 7z, tar, gz, bz2, xz, dmg, pkg, iso
- `typeIndexLimit = 200_000` — 카테고리당 내역 인덱스 최대 개수(메모리 안전판).

### 2.4 PaneTab 의 typeMode 상태 (오른쪽 패널 가상 목록)

| 필드 | 타입 | 의미 |
|---|---|---|
| `typeMode` | `Bool` | items가 "파일 계산" 카테고리 내역인 동안 true |
| `typeName` | `String?` | 활성 카테고리 이름("문서"/"이미지"/…) |
| `typeTotal` | `Int` | 카테고리의 **전체** 파일 수(표시 중 개수보다 클 수 있음 — 상위 N개만 보여주므로) |

AppModel 측 내부 상태: `typePageSize = 500`(상수), `typeEntries: [TypeFileEntry]`(크기순 인덱스), `typeLoaded: Int`(지금까지 목록으로 옮긴 개수 — `items.count`를 쓰지 않는 이유: 목록에서 항목 삭제 시 count가 줄어 같은 항목이 중복 로드되기 때문).

탭 제목: `typeMode`면 `"파일 계산 — \(typeName)"`. 그룹화(`activeGroups`)는 typeMode에서 비활성. `rebuild()`는 typeMode 동안 items를 건드리지 않음(AppModel이 직접 관리).

---

## 3. 샘플링 동작 명세

### 3.1 타이머

- `start()`: 이미 timer가 있으면 무시. 즉시 1회 `sample()` 후 **2.0초 간격** 반복 타이머(RunLoop.common 모드 — 메뉴/스크롤 중에도 동작). C#: `DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }` (WPF Dispatcher 타이머는 모달 중에도 동작하므로 적합).
- `stop()`: 타이머 해제. (앱에서는 호출하는 곳 없음 — start만 함.)

### 3.2 sample() — 매 틱

1. CPU·메모리는 가벼운 커널 호출이므로 **UI 스레드에서 즉시** 읽음 (prevCPU 델타 연속성 유지):
   - `cpu = readCPU()`, `memory = readMemory()`
   - `cpuUsage = clamp(cpu.total/100, 0, 1)`, `memoryUsage = clamp(memory.usedPercent/100, 0, 1)`
   - `cpuHistory.append(cpu)`; 60개 초과분은 앞에서 제거.
2. 디스크 용량과 온도는 **느릴 수 있어 백그라운드 Task로** 읽고 결과만 UI 스레드에 반영 (원본 주석: 메인에서 2초마다 돌리면 클릭과 겹칠 때 간헐적 끊김 발생):
   - `diskUsageSnapshot()` → 성공 시 `diskUsage/diskUsedText/diskTotalText` 갱신.
   - `SMC.cpuTemperature()` → **정상값을 얻었을 때만** `cpuTemperature` 갱신 (실패 시 기존값 유지 = 깜빡임 방지).
   - C#: `Task.Run(...)` 후 `Dispatcher.InvokeAsync` 또는 `await`로 복귀.

### 3.3 CPU 사용률 계산식 (readCPU)

- mac: `host_statistics(HOST_CPU_LOAD_INFO)` 누적 틱(user, system, idle, nice)의 **이전 샘플과의 델타**로 계산:
  - `total = ΔUser + ΔSys + ΔIdle + ΔNice` (0이면 직전 값 유지)
  - `user% = (ΔUser + ΔNice)/total*100`, `sys% = ΔSys/total*100`, `idle% = ΔIdle/total*100`, `total% = user% + sys%`
  - 첫 샘플(prev 없음)은 기본값(`CPUStat()` = total 0, idle 100) 반환.
- **Windows 대응**: `GetSystemTimes(out idleTime, out kernelTime, out userTime)` (P/Invoke) 델타 권장.
  - 주의: Windows의 kernelTime은 idle을 **포함**하므로 `ΔSys = ΔKernel - ΔIdle`.
  - `total = ΔKernel + ΔUser`; `user% = ΔUser/total*100`; `sys% = (ΔKernel-ΔIdle)/total*100`; `idle% = ΔIdle/total*100`.
  - 대안: `PerformanceCounter("Processor", "% Processor Time"/"% User Time"/"% Privileged Time"/"% Idle Time", "_Total")` — 단 첫 NextValue()는 0이고 인스턴스 생성이 느림. GetSystemTimes 쪽이 가볍고 의존성 없음.
  - mac의 nice 구분은 Windows에 없음 — user에 합산된 것과 동일하게 취급(이미 원본도 user+nice 합산).

### 3.4 메모리 분류 계산식 (readMemory)

- mac: `host_statistics64(HOST_VM_INFO64)` 페이지 카운트 × 페이지 크기:
  - `free = free_count*ps`, `active = active_count*ps`, `wired = wire_count*ps`, `compressed = compressor_page_count*ps`
  - `app = max(0, internal_page_count - purgeable_count)*ps` (UI 미사용이지만 필드 존재)
  - `used = total - free`, `usedPercent = used/total*100` (total = 물리 메모리)
  - 스왑: `sysctl vm.swapusage` → `swapUsed/swapTotal` GiB.
- **Windows 대응 (의미 매핑)** — 1:1 대응이 없으므로 다음 근사를 제안:
  - `GlobalMemoryStatusEx` → `TotalGB = ullTotalPhys`, `FreeGB = ullAvailPhys`(사용 가능), `UsedPercent = dwMemoryLoad` 또는 `(total-avail)/total*100`.
  - `Active`(활성 앱 메모리) ≈ `total - avail - (커널 사용분)`. 베스트에포트: `PerformanceCounter("Memory", "Pool Nonpaged Bytes")` + `"Pool Paged Resident Bytes"` 를 Wired로, 나머지(used - wired - compressed)를 Active로.
  - `Wired`(커널 고정) ≈ NonPaged Pool (`GetPerformanceInfo()` P/Invoke의 `KernelNonpaged * PageSize` 권장 — psapi.dll, 카운터보다 가벼움).
  - `Compressed` ≈ "Memory Compression" 프로세스의 WorkingSet (`Process.GetProcessesByName("Memory Compression")` — 권한에 따라 0일 수 있음, 실패 시 0).
  - `App` ≈ used - wired - compressed (음수 방지 clamp).
  - 스왑: `GlobalMemoryStatusEx`의 `ullTotalPageFile - ullTotalPhys`(페이지파일 전체)와 커밋 기반 근사, 또는 `PerformanceCounter("Paging File", "% Usage", "_Total")` × 페이지파일 크기. 베스트에포트로 명시하고, 0이어도 UI는 "0.00GB"로 표시하면 됨.
  - `Pressure`는 원본 공식 그대로 `(Wired+Compressed)/TotalGB*100` 재사용(근사라고 원본도 명시).

### 3.5 디스크 사용량 스냅샷 (diskUsageSnapshot)

- mac: 루트 볼륨 `volumeTotalCapacity` + `volumeAvailableCapacityForImportantUsage`(퍼지 가능 공간 포함한 "중요 용도" 가용 — 느린 계산이라 백그라운드).
- 반환: `(usage = clamp(used/total, 0, 1), usedText, totalText)` — 텍스트는 ByteCountFormatter(.file = 1000진법, 예: "245.1 GB").
- **Windows**: `new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory))` → `TotalSize`, `AvailableFreeSpace`. 매우 빠르므로 백그라운드 필수 아님(그래도 구조 유지 권장). 포맷은 자체 휴먼 포맷(§7.3)이나 Windows 관례(1024진법, "GB") 중 택1 — **권장: 디스크 팝업과 동일한 §7.3 휴먼 포맷 함수로 통일**.

---

## 4. 온도 (SMC) — Windows 대응

### 4.1 원본 동작

- `SMC.shared` 싱글턴이 IOKit으로 AppleSMC 서비스 오픈. 열기 실패 시 모든 호출이 nil.
- `cpuTemperature()`: 후보 키 16개(TC0P, TC0D, TC0E, TC0F, Tp01…Tp0n — Intel 패키지 + Apple Silicon P/E 코어)를 읽어 **20℃ ≤ v ≤ 110℃ 범위인 값들만 평균**. 유효 값 없으면 nil.
- `driveTemperature()`: 후보 키 12개(TaLP, TaRP, TH0a…TH1x, Ts0P, Ts1P)를 **10℃ ≤ v ≤ 100℃ 평균**. 없으면 nil.
- 데이터 타입 디코딩: `flt `(LE float32), `sp78`(BE 부호있는 8.8 고정소수점), `ioft`(LE 8바이트 /65536).
- 실패는 조용히 무시(온도 표시 생략) — doc 명시.

### 4.2 Windows 포팅 노트

- **AppleSMC 자체는 포팅 불가/무의미.** 구조체 레이아웃(SMCParamStruct 등)은 옮기지 말 것.
- CPU 온도 베스트에포트 (순서대로 시도, 전부 실패 시 null):
  1. WMI `root\WMI` → `MSAcpi_ThermalZoneTemperature.CurrentTemperature` (단위: 0.1K → `℃ = v/10 - 273.15`). 관리자 권한 필요하거나 미지원 기기 많음.
  2. WMI `root\CIMV2` → `Win32_PerfFormattedData_Counters_ThermalZoneInformation.Temperature`(K) — Win10+ 일부 기기.
  3. 실패 시 **CPU 팝업은 원본의 폴백 경로(발열 상태 카드)를 사용** — 단 Windows에는 `ProcessInfo.thermalState` 대응이 없으므로, 온도 자체를 영영 못 읽으면 카드 내용을 "발열 상태 / —" 또는 카드 생략 중 택1. **권장: 온도 카드를 `값 "—" / "이 PC에서는 CPU 온도를 읽을 수 없습니다."`로 표시** (디스크 팝업의 온도 폴백과 동일 패턴).
- 드라이브 온도 베스트에포트: WMI `root\WMI` → `MSStorageDriver_ATAPISmartData`(SMART 속성 194 Temperature) 또는 Win10+ `StorageReliabilityCounter.Temperature` (`Get-PhysicalDisk | Get-StorageReliabilityCounter`의 WMI 클래스 `MSFT_StorageReliabilityCounter`, namespace `root\Microsoft\Windows\Storage`). NVMe + 권한 문제로 자주 실패 — 실패 시 null로 두고 §6.3의 폴백 문자열 표시.
- 유효 범위 필터(CPU 20–110, 드라이브 10–100)와 "정상값일 때만 갱신" 정책은 그대로 유지.

### 4.3 S.M.A.R.T. 건강 상태 (scanHealth)

- mac: `/usr/sbin/diskutil info -plist /` 실행 → plist의 `SMARTDeviceStatus` 또는 `SMARTStatus` 문자열.
- 매핑 (UI 문자열 원문 그대로):
  - `"Verified"` → 값 `"정상"`, 설명 `"드라이브 상태가 정상(Verified)으로 확인되었습니다."`
  - `"Failing"` → 값 `"주의"`, 설명 `"드라이브에 문제가 감지되었습니다. 백업을 권장합니다."`
  - 그 외 비어있지 않은 문자열 s → 값 `s`, 설명 `"드라이브 S.M.A.R.T. 상태입니다."`
  - nil/빈 값 → 값 `"지원 안 함"`, 설명 `"이 드라이브는 S.M.A.R.T. 상태를 제공하지 않습니다."`
- **Windows**: WMI `root\CIMV2` → `Win32_DiskDrive.Status`("OK"/"Pred Fail"/…) 또는 `root\WMI` → `MSStorageDriver_FailurePredictStatus.PredictFailure`(bool). 매핑 제안: `OK/PredictFailure==false` → "정상"(설명은 `"드라이브 상태가 정상(Verified)으로 확인되었습니다."` 대신 `"드라이브 상태가 정상으로 확인되었습니다."`로 다듬어도 무방하나, 가능하면 원문 유지), `Pred Fail/true` → "주의" + 동일 설명, 조회 실패 → "지원 안 함" + 동일 설명.

---

## 5. 디스크 분류·파일 계산 (캐시 동작)

### 5.1 refreshDisk(force:)

1. 항상(저렴): `readDiskCapacity()`(볼륨 이름/total/free 갱신) + `diskTemperature = SMC.driveTemperature()`.
2. `diskComputing`이면 리턴(중복 실행 방지).
3. `!force && diskComputedAt != nil`이면 리턴 — **캐시 재사용** (팝업 재오픈 시 재스캔 없음).
4. `diskComputing = true` → `scanCategories(usedBytes)` + `scanHealth()` 비동기 수행 → `diskCategories/diskHealthValue/diskHealthDesc/diskComputedAt = now` → `diskComputing = false`.

### 5.2 refreshFileTypes(force:)

- `fileTypeComputing`이면 리턴; `!force && fileTypeStats != nil`이면 리턴(캐시).
- 홈 폴더(`Environment.GetFolderPath(UserProfile)`) 대상으로 `FileSystemService.sizeByFileType` 백그라운드 실행, 소요 시간·개수 로그(`NSLog "[XFinder] 종류별 파일 계산: %.2fs (%d개)"` — C#은 Debug.WriteLine 등).
- 결과를 `fileTypeStats`에 캐시.

### 5.3 scanCategories — 도넛 카테고리

- 4개 폴더를 **병렬**로 `folderSize`(재귀 합계, 숨김 포함) 계산:
  1. `"응용 프로그램"` = `/Applications` → Windows: `C:\Program Files` (+ 가능하면 `C:\Program Files (x86)` 합산 제안; 권한 없는 하위 폴더는 건너뛰기)
  2. `"다운로드"` = `~/Downloads` → `Environment.GetFolderPath`로 Downloads(`SHGetKnownFolderPath(FOLDERID_Downloads)`)
  3. `"문서"` = `~/Documents` → MyDocuments
  4. `"데스크탑"` = `~/Desktop` → Desktop
- 5번째 `"기타"` = `max(0, diskUsedBytes - 위 4개 합)`.
- `DiskCategory.id`는 0..4 (팔레트 인덱스 겸용).
- C#: `Task.WhenAll` 또는 `Parallel.ForEach`. folderSize는 `Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })` 합계 — **심볼릭 링크/리파스 포인트는 따라가지 않음**(원본 fts FTS_PHYSICAL과 동일; 무한 루프 방지).

### 5.4 sizeByFileType — 홈 폴더 종류별 스캔 (성능 핵심)

- 원본: 저수준 `fts` C API + **루트의 1~2단계 하위 폴더를 작업 단위로 쪼개 코어 수만큼 병렬 스캔**. FileManager.enumerator 대비 수 배 빠름 (doc 경고: 느린 API로 되돌리지 말 것).
- 규칙:
  - **숨김 트리 스킵**: 이름이 `.`으로 시작하거나 숨김 플래그(UF_HIDDEN) → Windows: `FileAttributes.Hidden` 검사 + 이름 `.` 시작. (mac의 `~/Library` 스킵에 대응해 Windows에선 `AppData`가 Hidden 속성으로 자연 스킵됨.)
  - 심볼릭 링크는 따라가지 않음 (ReparsePoint 스킵).
  - 파일마다: 소문자 확장자 → fileTypeMap 카테고리(미등록 "기타") → 카테고리별 `(bytes, count)` 누적 + `TypeFileEntry(path, size)` 인덱스 추가.
  - **prune 규칙**: 카테고리 인덱스가 `2 × typeIndexLimit`(= 40만)개에 도달하면 크기 내림차순 정렬 후 상위 `typeIndexLimit`(20만)개만 유지. (버려지는 항목은 이미 자기보다 큰 파일이 N개 있으므로 최종 top-N에 못 듦 — 정확성 유지.)
  - 병렬 단위 내부는 락 없이 로컬 누적, 단위 종료 시에만 락 잡고 전역 병합(병합 후에도 prune).
  - 직속 파일(1~2단계 분할 과정에서 만나는 폴더 아닌 항목)은 즉시 전역 누적.
- 최종 결과: `fileTypeOrder` 순서대로 6개 `TypeBreakdown`, 각 `files`는 크기 내림차순 상위 20만 개.
- **C# 구현 제안**: 작업 단위 분할(루트 1~2단계 비숨김 폴더) 동일 + `Parallel.ForEach(units)` 내부에서 수동 스택 기반 `Directory.EnumerateFileSystemEntries` 순회(또는 `FileSystemEnumerable<T>` — 파일당 할당 최소화) + `lock`으로 병합. .NET의 enumerator는 충분히 빠르므로 fts 같은 저수준 P/Invoke는 불필요. `IgnoreInaccessible = true` 필수.

### 5.5 emptyTrash (현재 UI에서 직접 노출 안 됨)

- mac: `~/.Trash` 내용물 개별 삭제 후 `scanTrash()`로 `diskTrashBytes` 갱신.
- Windows: `SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND)`. 크기 조회는 `SHQueryRecycleBin`. (현 팝업 UI에 휴지통 표시/버튼이 없으므로 후순위.)

---

## 6. 프로세스 목록

### 6.1 topMemoryProcesses(limit: 6)

- 모든 PID 나열(`proc_listallpids`, 최대 8192) → 각 PID의 RSS(`proc_pidinfo PROC_PIDTASKINFO → pti_resident_size`).
- **RSS ≤ 1,000,000 바이트(≈1MB)는 제외.** 이름은 `proc_name`(빈 문자열이면 `"PID \(pid)"`).
- RSS GiB 내림차순 정렬 → 상위 6개.
- **Windows**: `Process.GetProcesses()` → `proc.WorkingSet64`(RSS 대응). 접근 불가 프로세스는 try/catch로 스킵. 이름은 `proc.ProcessName`(없으면 `"PID {pid}"`).

### 6.2 topCPUProcesses(limit: 5)

- 각 PID의 누적 CPU 시간(ns) = `proc_pid_rusage(RUSAGE_INFO_V2).ri_user_time + ri_system_time`.
- **이전 호출과의 델타**로 `% = Δns / 1e9 / Δt초 × 100` — 따라서 주기적으로 호출해야 의미 있는 값. 첫 호출은 빈 결과(팝업이 "측정 중…" 표시).
- 새로 등장한 PID(이전 샘플에 없음)나 시간 역전(`nanos < prev`)은 스킵. 호출 후 `prevProcCPU/prevProcSampleTime` 갱신.
- 내림차순 상위 5개.
- **Windows**: `proc.TotalProcessorTime`(접근 불가 시 스킵) 델타 / 경과초 × 100. 참고: 멀티코어에서 100%를 넘을 수 있음(원본도 동일 — 코어 수로 나누지 않음, 그대로 유지).
- 주의: 모니터 싱글턴에 prev 맵이 1개뿐이므로 CPU 팝업이 2초마다 호출하는 것이 전제. (두 군데서 부르면 델타가 깨짐 — 포팅 시에도 호출처를 CPU 팝업 타이머 하나로 유지.)

### 6.3 quit(pid:) — "종료" 버튼

- mac: `kill(pid, SIGTERM)` — 정상 종료 요청.
- **Windows**: 대응 신호 없음. 제안: ① 해당 프로세스 메인 윈도우가 있으면 `proc.CloseMainWindow()`(정상 종료 요청), ② 없으면 `proc.Kill()`. 권한 부족(시스템 프로세스)은 조용히 무시(원본도 실패 무시).
- 버튼 클릭 후 **0.8초 뒤** 목록 reload (`reloadSoon`) — 종료가 반영될 시간을 줌.

### 6.4 프로세스 표시 이름/아이콘

- 아이콘: `NSRunningApplication(pid)?.icon`, 없으면 SF Symbol `app.dashed`(점선 앱 모양) 회색.
  - Windows: `proc.MainModule.FileName` → `Icon.ExtractAssociatedIcon` (접근 불가 시 폴백 글리프 — Segoe Fluent Icons ``(AppIconDefault) 또는 `` 제안). 캐시 권장(경로→ImageSource 딕셔너리).
- 표시 이름: CPU 팝업은 GUI 앱이면 localizedName, 아니면 프로세스 실행 파일명. Windows: `FileVersionInfo.FileDescription`(있으면) → 없으면 `ProcessName`. 메모리 팝업은 `p.name` 그대로.

---

## 7. UI 외형

### 7.1 툴바 컴팩트 표시 (SystemStatsView)

- 가로 HStack, 항목 간격 `11 × scale`(scale = 앱 목록 배율 `listScale`).
- 항목 = 버튼(플레인): `[아이콘][4×scale 간격][퍼센트 텍스트]`
  - 아이콘: SF Symbol, 크기 12×scale.
    - CPU: `cpu` 심볼, 파란색 → Segoe Fluent Icons 제안: `` (CPU/칩 글리프 — Segoe MDL2 "E950"이 칩 모양; 없으면 이모지 🖥 대신 글리프 우선)
    - 메모리: `memorychip` 심볼, 보라색 → ``(Memory) 또는 `` 대안
    - 디스크: `internaldrive` 심볼, 주황색 → ``(HardDrive)
  - 텍스트: `String(format: "%.0f%%", value*100)` — 11×scale 세미볼드, monospacedDigit, 한 줄 고정("100%"도 줄바꿈 없음).
- 툴팁(help):
  - CPU: `"CPU 사용량 — 클릭하면 추이·온도·부하 프로세스 보기"`
  - 메모리: `"메모리 사용량 — 클릭하면 앱·캐시·스왑 상세 보기"`
  - 디스크: `"디스크 사용량 — 클릭하면 용량 분류·S.M.A.R.T. 상태 보기"`
- 클릭 → 해당 팝업을 **아래쪽 화살표 popover**로. WPF: `Popup`(StaysOpen=false, Placement=Bottom) — 바깥 클릭으로 닫힘. 화살표 모양은 생략 가능.

### 7.2 CPU 팝업 (CPUDetailView)

- 크기: **너비 360 고정, 높이는 내용 자동(스크롤 없음)**, 패딩 16, 섹션 간 VStack spacing 16.
- 구조 (위→아래):
  1. **칩 이름** — `monitor.cpuName`, 18pt bold.
  2. **그래프 섹션** (내부 spacing 14):
     - `CPUChart` — 높이 120, 가로 꽉 채움. 라인 차트:
       - 배경: cornerRadius 8 라운드 사각형, `Color.primary.opacity(0.05)`.
       - 격자: 8열×4행, 선색 `secondary.opacity(0.18)`, 두께 0.5.
       - user 라인(파랑)·system 라인(빨강), 두께 1.5. x = i/(n-1)·w (히스토리 인덱스 등분), y = h·(1 − clamp(v,0,100)/100). 점 1개 이하면 그리지 않음.
       - 데이터: `cpuHistory`(최근 60개, 2초 간격 = 2분 추이).
     - **사용률 수치 3개** 가로 등분(HStack spacing 0, 각 readout maxWidth ∞):
       - readout = `[세로 막대 4×34, cornerRadius 2, 해당 색][8 간격][값 17pt bold "%.1f%%" / 라벨 11pt secondary]`
       - 순서·라벨·색: `"사용 가능"`(idle, secondary 회색) / `"사용자"`(user, 파랑) / `"시스템"`(system, 빨강)
  3. Divider
  4. **카드 2개** (HStack spacing 10, infoCard 규격은 §7.5):
     - 카드1 `"가동시간"`: 값 = `d`일(d>0) → `"\(d)일"`, 아니면 h>0 → `"\(h)시간"`, 아니면 `"\(m)분"`. 설명: d==0 → `"시스템을 아주 최근에 시작했습니다."`, d<7 → `"재시작 없이 잘 작동하고 있습니다."`, 그 외 → `"한동안 재시작하지 않았습니다."`
     - 카드2: 온도 읽히면 `"온도"` / `String(format:"%.0f°C")` / 설명: t<70 → `"정상 작동 범위 내에 있습니다."`, t<90 → `"온도가 다소 높습니다."`, 그 외 → `"온도가 매우 높습니다."`
       온도 없으면(mac은 thermalState 폴백): 제목 `"발열 상태"`, 값 `"정상"/"보통"/"높음"/"심각"/"—"`, 설명 `"발열이 정상 범위 내에 있습니다." / "발열이 약간 있는 정상 상태입니다." / "발열이 높습니다. 부하를 줄여 보세요." / "발열이 매우 심각합니다." / "발열 상태를 확인할 수 없습니다."` — **Windows에는 thermalState가 없으므로** §4.2 권장 폴백("온도" / "—" / "이 PC에서는 CPU 온도를 읽을 수 없습니다.") 사용.
  5. Divider
  6. **상위 프로세스 섹션** (§7.6) — `topCPUProcesses(limit: 5)`, 값 표시 `"%.1f%%"`(12pt medium), **50% 이상이면 주황색** 아니면 기본색. 비어 있으면 `"측정 중…"`(12pt secondary, 상하 패딩 4).
- 라이프사이클: 열릴 때 즉시 reload + **2초 타이머**로 reload 반복; 닫힐 때 타이머 해제.

### 7.3 메모리 팝업 (MemoryDetailView)

- 크기: **360 × 540 고정, 내용은 ScrollView**, 패딩 16, 섹션 spacing 16.
- 구조:
  1. 제목 `"메모리"` 18pt bold.
  2. **도넛 + 범례** (HStack spacing 16):
     - 도넛 130×130, 스트로크 두께 16, lineCap butt, 시작 12시 방향(-90° 회전):
       - 세그먼트(used 안에서 누적): 활성화 파랑 → 와이어드 보라 → 압축됨 인디고 → 나머지(used−세 값 합, 음수 clamp) `gray.opacity(0.6)`. 분모 = totalGB.
       - 미사용 잔여 트랙(used/total → 1 구간): `secondary.opacity(0.18)`.
       - 중앙 텍스트: `"%.2fGB"`(used) 19pt bold / `"/ %.0fGB"`(total) 10pt secondary.
     - 범례(VStack spacing 10): `[색 원 9×9][8 간격][이름 12pt secondary / 값 "%.2fGB" 13pt semibold]` — `"활성화"`(파랑), `"와이어드"`(보라), `"압축됨"`(인디고).
  3. Divider
  4. **카드 2개**: `"압력"` 값 `"%.0f%%"`(pressure), 설명 pressure<70 → `"여유로운 상태입니다."` 아니면 `"메모리 사용량이 높습니다."` / `"스왑 파일"` 값 `"%.2fGB"`(swapUsed), 설명 `"메모리 성능을 돕는 디스크 영역입니다."`
  5. Divider
  6. **상위 프로세스** — `topMemoryProcesses(limit: 6)`, 값 `"%.2fGB"`(12pt medium), **2GB 이상 주황색**. 빈 상태 문구 없음(곧바로 채워짐).
- 라이프사이클: 열릴 때 reload + **3초 타이머**; 닫힐 때 해제. used = `max(0, totalGB - freeGB)`.

### 7.4 디스크 팝업 (DiskDetailView)

- 크기: **너비 380 고정, 높이 자동(스크롤 없음)**, 패딩 16, 섹션 spacing 14.
- 열릴 때: `refreshDisk(force: false)` + `refreshFileTypes()` — 캐시 있으면 재스캔 없이 즉시 표시.
- 구조:
  1. **헤더**: 좌측 볼륨 이름 18pt bold. 우측 세로 정렬:
     - `"다시 계산"` 버튼(↻ 아이콘 `arrow.clockwise` → Segoe `` Refresh, 12pt medium, 파란색, 플레인, 포커스 테두리 없음). 계산 중엔 라벨 `"계산 중…"` + disabled. 클릭 → `refreshDisk(force: true)` + `refreshFileTypes(force: true)` 동시 실행.
     - 캐시 시각: 계산 중이 아니고 `diskComputedAt` 있으면 `"계산: HH:mm"` 9pt secondary.
  2. **도넛 + 범례**: 도넛 140×140, 두께 16:
     - 배경 트랙 전체 원: `secondary.opacity(0.18)`.
     - 세그먼트: `diskCategories` 순서대로 누적, 분모 = `totalBytes`. 팔레트(id 순): `[.red, .pink, .blue, .teal, .gray]` (응용 프로그램=빨강, 다운로드=핑크, 문서=파랑, 데스크탑=틸, 기타=회색; id가 팔레트보다 크면 마지막 색).
     - 중앙: 사용 가능 용량 `human(freeBytes)` 19pt bold **주황색** / `"/ \(human(totalBytes))"` 10pt secondary / `"사용 가능"` 10pt secondary.
     - 범례: 카테고리별 `[색 원 9×9][이름 12pt secondary / human(bytes) 13pt semibold]`, spacing 9. 비어 있으면 계산 중 → `"계산 중…"` 아니면 `"—"` (12pt secondary).
  3. Divider
  4. **카드 2개**: `"건강 상태"` 값 = `diskHealthValue` ?? (계산 중 `"확인 중…"` : `"—"`), 설명 = `diskHealthDesc` ?? `"드라이브 상태를 확인하고 있습니다."` / 온도: 있으면 `"온도"` `"%.0f°C"` + 설명 t<50 → `"드라이브 온도가 정상 작동 범위 내에 있습니다."`, t<70 → `"드라이브 온도가 다소 높습니다."`, 그 외 `"드라이브 온도가 높습니다."`; 없으면 값 `"—"`, 설명 `"이 Mac에서는 드라이브 온도를 읽을 수 없습니다."` → Windows 문구 제안: `"이 PC에서는 드라이브 온도를 읽을 수 없습니다."`
  5. Divider
  6. **파일 계산 섹션**:
     - 헤더 행: `"파일 계산"` 14pt semibold + 우측 홈 폴더 경로 10pt secondary(한 줄, 중간 생략).
     - 리스트 컨테이너: cornerRadius 12 라운드, `primary.opacity(0.06)` 배경, 수평 패딩 12·수직 4.
     - `fileTypeStats`가 nil이면: `[작은 ProgressView][8 간격]"종류별로 계산 중…"`(12pt secondary), 패딩 10.
     - 있으면 6행(행 사이 Divider). **typeRow** (버튼, 플레인, 수직 패딩 7):
       `[카테고리 아이콘 14pt, 카테고리 색, 폭 20][10 간격][이름 13pt medium][Spacer][개수 "\(count.formatted())개" 11pt secondary][human(bytes) 13pt semibold monospacedDigit, 폭 78 우측정렬][chevron.right 9pt semibold tertiary — count==0이면 투명]`
       - count==0이면 disabled. 툴팁: `"\(row.name) 파일 내역을 오른쪽 패널에 보기"` (count>0일 때만).
       - 클릭: `app.showTypeBreakdown(row)` 호출 + 팝업 dismiss.
     - 카테고리 아이콘/색 (`typeMeta`, fileTypeOrder 순):
       | 카테고리 | SF Symbol | 색 | Segoe Fluent 제안 |
       |---|---|---|---|
       | 문서 | `doc.text` | 파랑 | `` (Document) |
       | 이미지 | `photo` | 초록 | `` (Photo2) 또는 `` |
       | 동영상 | `film` | 핑크 | `` (Video) |
       | 음악 | `music.note` | 보라 | `` (MusicNote) |
       | 압축 | `archivebox` | 주황 | ``/`` (Zip 폴더 — 없으면 ``) |
       | 기타 | `ellipsis.circle` | 회색 | `` (More) |
       - `meta(forTypeName:)` 정적 함수: 이름으로 fileTypeOrder 인덱스를 찾고(없으면 마지막="기타") 메타 반환 — 경로 표시줄 등 다른 뷰에서 재사용.
- **human(bytes) 포맷(1024진법)**: ≥1TB → `"%.2fTB"`, ≥1GB → `"%.2fGB"`, ≥1MB → `"%.1fMB"`, ≥1KB → `"%.0fKB"`, 그 외 `"\(b)B"`. 캐시 시각 포맷 `"HH:mm"`.

### 7.5 infoCard 공통 규격 (3개 팝업 동일)

- VStack(leading, spacing 6): 첫 줄 `[제목 13pt semibold][Spacer][값 14pt bold]`, 둘째 줄 설명 11pt secondary(여러 줄 허용).
- 패딩 10, maxWidth ∞, 배경 cornerRadius 10 라운드 `primary.opacity(0.06)`.
- WPF 색 대응: `primary.opacity(0.06)` ≈ 다크 `#0FFFFFFF`/라이트 `#0F000000` (전경색 6% — 테마별 리소스 권장). `secondary` ≈ 60% 전경.

### 7.6 상위 프로세스 섹션 공통 (CPU/메모리 팝업)

- 제목 `"부하가 가장 큰 프로세스"` 13pt semibold.
- 컬럼 헤더: `"프로세스 이름"` / `"사용량"` — 11pt secondary, 양끝 정렬.
- 행 (HStack spacing 8): `[앱 아이콘 18×18 또는 폴백 글리프 폭 18][이름 13pt 한 줄][Spacer ≥6][값][종료 버튼]`
- `"종료"` 버튼: 11pt, 플레인, 파란색 → `quit(pid)` + 0.8초 후 reload.

### 7.7 종류별 파일 내역 — 오른쪽 패널 (typeMode)

- `showTypeBreakdown(stat)` 동작:
  1. 진행 중인 검색/최근 항목/태그/리스팅 Task 전부 취소.
  2. `searchMode/recentsMode/tagMode = false`, `filter=""`, `typeMode=true`, `typeName=stat.name`, `typeTotal=stat.count`, `selection=[]`, `items=[]`.
  3. `typeEntries = stat.files`(크기순 인덱스), `typeLoaded = 0` → 첫 페이지 즉시 추가(`appendNextTypePage`) → 커서를 첫 항목으로, 사이드바 선택 해제.
- **무한 스크롤 페이징**: 목록/아이콘 셀이 화면에 나타날 때 `loadMoreTypeItemsIfNeeded(currentIndex)` 호출 — `typeMode`이고 `currentIndex >= items.count - 100`이고 인덱스에 남은 항목이 있으면 다음 페이지 추가. **페이지 크기 500.** (전체를 한 번에 FileItem으로 만들면 배열 구성·diffing·메타데이터 조회가 무거움 — 원본 주석.)
  - WPF 대응: `ScrollViewer.ScrollChanged`에서 `VerticalOffset + ViewportHeight >= ExtentHeight - (100행 높이)` 시 다음 페이지 append, 또는 가상화 ItemsControl의 컨테이너 생성 이벤트 활용.
- `appendNextTypePage()`: 인덱스에서 500개를 FileItem으로 변환(경로·크기·확장자만 채움, modified=distantPast) 후 items에 append → **그 페이지의 수정일·생성일·종류 메타데이터만 백그라운드에서 조회**해 경로 매칭으로 합침(`enrichTypeItems`). 합치기 전 `typeMode && typeName == category` 재확인(모드가 바뀌었으면 폐기).
- 경로 표시줄(PathBar): typeMode면 specialBar — `[카테고리 아이콘+색][제목]`, 제목:
  - 일부만 로드됨(total > shown): `"파일 계산 — \(name) (크기순 · \(shown.formatted())개 로드됨 / 전체 \(total.formatted())개)"`
  - 전부 로드됨: `"파일 계산 — \(name) (\(total.formatted())개)"`
  - specialBar 규격: 아이콘 11pt + 제목 12pt medium, 수평 패딩 10·수직 4, 창 배경색.
- 탭 제목: `"파일 계산 — \(typeName)"`.
- typeMode에서 삭제: 디렉터리 리스팅이 아니므로 삭제 성공 항목을 **목록에서 직접 제거**(reload 안 함). `typeLoaded`는 items.count와 별도라 중복 로드가 안 생김.
- typeMode 해제: 폴더 이동/검색/최근 항목/태그 진입 시 `typeMode=false, typeName=nil, typeTotal=0, typeEntries=[]` 리셋.

---

## 8. 영속화 (UserDefaults)

**이 모듈에는 UserDefaults 키가 없다.** 모든 캐시(디스크 카테고리, 파일 계산, 온도)는 싱글턴의 메모리에만 있고 앱 재시작 시 초기화된다. Windows 포팅에서도 동일하게 메모리 캐시만 유지(파일로 저장하지 말 것 — 재시작 후 첫 팝업 오픈 시 1회 스캔이 원본 동작).

(간접 연관: 툴바 배율 `listScale`은 다른 모듈[설정] 소유.)

---

## 9. Windows 포팅 매핑 요약표

| mac API | 용도 | Windows 대응 |
|---|---|---|
| `host_statistics(HOST_CPU_LOAD_INFO)` | CPU 틱 | `GetSystemTimes` P/Invoke 델타 (또는 PerformanceCounter "_Total") |
| `host_statistics64(HOST_VM_INFO64)` | 메모리 분류 | `GlobalMemoryStatusEx` + `GetPerformanceInfo`(NonPaged≈Wired) + "Memory Compression" 프로세스 WS(≈Compressed) — 베스트에포트 |
| `sysctl vm.swapusage` | 스왑 | `GlobalMemoryStatusEx`(PageFile−Phys) 또는 PerformanceCounter("Paging File","% Usage") — 베스트에포트 |
| `sysctl machdep.cpu.brand_string` | 칩 이름 | 레지스트리 `ProcessorNameString` 또는 WMI `Win32_Processor.Name` |
| `ProcessInfo.systemUptime` | 가동시간 | `Environment.TickCount64 / 1000.0` |
| `ProcessInfo.thermalState` | 발열 폴백 | 대응 없음 → 온도 미지원 카드("—")로 대체 |
| `volumeAvailableCapacityForImportantUsage` | 디스크 가용 | `DriveInfo.AvailableFreeSpace` (시스템 드라이브) |
| AppleSMC IOKit (`SMC.swift` 전체) | CPU/SSD 온도 | 포팅 불가 — WMI `MSAcpi_ThermalZoneTemperature` / `MSFT_StorageReliabilityCounter` 베스트에포트, 실패 시 조용히 생략 |
| `diskutil info -plist /` SMARTStatus | 디스크 건강 | WMI `Win32_DiskDrive.Status` / `MSStorageDriver_FailurePredictStatus` |
| `proc_listallpids` + `proc_pidinfo` | 프로세스 RSS | `Process.GetProcesses()` + `WorkingSet64` |
| `proc_pid_rusage` CPU ns | 프로세스 CPU | `Process.TotalProcessorTime` 델타 |
| `kill(pid, SIGTERM)` | 종료 | `CloseMainWindow()` → 실패/창 없음 시 `Kill()` |
| `NSRunningApplication.icon / localizedName` | 앱 아이콘/이름 | `Icon.ExtractAssociatedIcon(MainModule.FileName)` + `FileVersionInfo.FileDescription` (접근 불가 시 폴백) |
| fts 병렬 스캔 | 종류별/폴더 크기 | 1~2단계 폴더 단위 분할 + `Parallel.ForEach` + `Directory.EnumerateFiles(IgnoreInaccessible, ReparsePoint 스킵)` |
| `~/.Trash` 비우기 | 휴지통 | `SHEmptyRecycleBin` / `SHQueryRecycleBin` |
| SwiftUI popover | 팝업 | WPF `Popup` (StaysOpen=false, Placement=Bottom) |
| `Timer` (RunLoop.common) | 샘플링 | `DispatcherTimer` |
| SF Symbols | 아이콘 | Segoe Fluent Icons 글리프 (§7 표 참조) |

### 포팅 불가/생략 항목
- `ProcessInfo.thermalState`(발열 상태 카드) — Windows 공개 API 없음. 온도 읽기 실패 시 "—" 카드로 대체.
- SMC 키 후보 평균 로직 — Windows에선 열 영역(thermal zone) 1~n개 평균으로 동일 패턴 적용 가능(유효 범위 필터 유지).
- `volumeAvailableCapacityForImportantUsage`의 "퍼지 가능 공간 포함" 의미 — Windows에는 없음. `AvailableFreeSpace` 그대로 사용.
- macOS UF_HIDDEN 기반 `~/Library` 스킵 — Windows에선 `FileAttributes.Hidden`(AppData 등) + `.` 시작 이름으로 동일 의도 구현.

---

## 10. UI 문자열 전체 목록 (원문 유지)

- 툴팁: `CPU 사용량 — 클릭하면 추이·온도·부하 프로세스 보기` / `메모리 사용량 — 클릭하면 앱·캐시·스왑 상세 보기` / `디스크 사용량 — 클릭하면 용량 분류·S.M.A.R.T. 상태 보기`
- 공통: `부하가 가장 큰 프로세스`, `프로세스 이름`, `사용량`, `종료`, `측정 중…`
- CPU: `프로세서`(이름 폴백), `사용 가능`, `사용자`, `시스템`, `가동시간`, `온도`, `발열 상태`, `\(d)일`/`\(h)시간`/`\(m)분`, `시스템을 아주 최근에 시작했습니다.`, `재시작 없이 잘 작동하고 있습니다.`, `한동안 재시작하지 않았습니다.`, `정상 작동 범위 내에 있습니다.`, `온도가 다소 높습니다.`, `온도가 매우 높습니다.`, `정상`, `보통`, `높음`, `심각`, `—`, `발열이 정상 범위 내에 있습니다.`, `발열이 약간 있는 정상 상태입니다.`, `발열이 높습니다. 부하를 줄여 보세요.`, `발열이 매우 심각합니다.`, `발열 상태를 확인할 수 없습니다.`
- 메모리: `메모리`, `활성화`, `와이어드`, `압축됨`, `압력`, `여유로운 상태입니다.`, `메모리 사용량이 높습니다.`, `스왑 파일`, `메모리 성능을 돕는 디스크 영역입니다.`
- 디스크: `디스크`(이름 초기값), `Macintosh HD`(이름 폴백), `다시 계산`, `계산 중…`, `계산: \(HH:mm)`, `사용 가능`, `건강 상태`, `확인 중…`, `드라이브 상태를 확인하고 있습니다.`, `정상`, `드라이브 상태가 정상(Verified)으로 확인되었습니다.`, `주의`, `드라이브에 문제가 감지되었습니다. 백업을 권장합니다.`, `드라이브 S.M.A.R.T. 상태입니다.`, `지원 안 함`, `이 드라이브는 S.M.A.R.T. 상태를 제공하지 않습니다.`, `온도`, `드라이브 온도가 정상 작동 범위 내에 있습니다.`, `드라이브 온도가 다소 높습니다.`, `드라이브 온도가 높습니다.`, `이 Mac에서는 드라이브 온도를 읽을 수 없습니다.`(→ Windows: `이 PC에서는 드라이브 온도를 읽을 수 없습니다.`), `파일 계산`, `종류별로 계산 중…`, `\(count)개`, `\(row.name) 파일 내역을 오른쪽 패널에 보기`
- 디스크 카테고리: `응용 프로그램`, `다운로드`, `문서`, `데스크탑`, `기타`
- 파일 종류: `문서`, `이미지`, `동영상`, `음악`, `압축`, `기타`
- 경로 표시줄(typeMode): `파일 계산 — \(name) (크기순 · \(shown)개 로드됨 / 전체 \(total)개)`, `파일 계산 — \(name) (\(total)개)`
- 탭 제목: `파일 계산 — \(typeName)`
- 로그: `[XFinder] 종류별 파일 계산: %.2fs (%d개)`

---

## 11. 검증 체크리스트 (포팅 후)

1. 툴바 3개 수치가 2초마다 갱신되고 클릭 시 팝업이 아래로 열리는가.
2. CPU 그래프가 2분(60샘플) 추이를 그리고 user/system 두 라인이 분리돼 있는가.
3. CPU 프로세스 목록 첫 표시가 "측정 중…" → 2초 후 채워지는가 (델타 기반).
4. 종료 버튼 후 0.8초 뒤 목록이 갱신되는가; 권한 없는 프로세스에서 크래시하지 않는가.
5. 디스크 팝업 재오픈 시 재스캔 없이 즉시 표시; "다시 계산"으로만 갱신; 계산 중 버튼 disabled + "계산 중…" 라벨.
6. 파일 계산 카테고리 클릭 → 우측 패널 크기순 목록, 스크롤 끝 100행 전에 500개씩 추가 로드, 경로 표시줄 "로드됨/전체" 갱신.
7. typeMode에서 파일 삭제 시 중복 로드 없이 목록에서만 제거되는가.
8. 온도/SMART 미지원 기기에서 카드가 "—"/"지원 안 함" 폴백으로 조용히 표시되는가.
