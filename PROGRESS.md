# xFinder 윈도우 포팅 — 진행 상황 핸드오프 (2026-06-11)

macOS 앱 XFinder(D:\project\xFinder-mac-src, Swift/SwiftUI ~8,200줄)를
**C# .NET 8 WPF**로 D:\project\xFinder 에 동일하게 포팅하는 작업.

## 완료된 것

1. **포팅 스펙 완성** — `spec/01~08*.md` (멀티 에이전트가 mac 소스 전체 분석).
   - `spec/08-gaps.md`가 **정본(normative)** — 스펙 간 모순 5건 정정 포함. 구현 시 우선 적용.
2. **프로젝트 골격** — XFinder.csproj (net8.0-windows, WPF, System.Management 참조), app.manifest,
   App.xaml(.cs), Themes/Dark.xaml + Light.xaml(스펙 색상), Themes/Styles.xaml(미완 — 공용 스타일 확장 필요).
3. **Models/ 완성 (직접 구현, 스펙 01 기반)**:
   - ObservableObject.cs, Enums.cs(ViewMode/GroupKey/ListColumn/SortKey + StrCmpLogicalW 자연정렬),
     FileItem.cs(+Format), PaneTab.cs(가상 모드 4종/그룹화/rebuild), SidebarItem.cs,
     AppSheet.cs(시트 라우팅/OperationProgress/ConfirmRequest/터미널·검색창 enum),
     **AppModel.cs(~1400줄, 심장부)** — 탐색/히스토리/탭/즐겨찾기/가상 목록 4종/클립보드/파일 작업/
     type-select(한글 자모 분해)/AI 정리 게이트/한글 NFD 복원 진입점/영속 설정 17키 전부.
4. **Services/ 일부 (직접 구현)**: SettingsStore.cs(%APPDATA%\XFinder\settings.json),
   ShellInterop.cs(휴지통/아이콘/썸네일/속성), IconCache.cs, ThemeService.cs(다크/라이트/시스템 + DWM).
5. **Views/ 공용 자산**: IconMap.cs(SF Symbol→Segoe Fluent 글리프, 탭 8색 팔레트, 태그 7색), Converters.cs.
6. **백엔드 서비스 워크플로우 실행 중(또는 완료)** — 에이전트 3개가 작성 중:
   FileSystemService/FileOperations/SystemActions/RecentsService/TagService/HangulNormalize/WindowsName,
   SystemMonitor(WMI), AIService(Ollama/Gemini).
   → 완료 후 **AppModel이 기대하는 시그니처와 대조·수정 필요** (아래 '계약' 참고).

## AppModel이 기대하는 서비스 시그니처 (불일치 시 서비스 쪽 수정 또는 AppModel 호출부 수정)

- FileSystemService.List(string dir) → List<FileItem> (실패 시 throw)
- FileSystemService.Subfolders(string, bool showHidden) → List<string>
- FileSystemService.HasSubfolders(string, bool) → bool / DriveRoots() → List<string>
- FileSystemService.SearchRecursive(root, needle, showHidden, limit, CancellationToken) → List<FileItem>
- FileSystemService.FolderSize(path, CancellationToken) → long
- FileOperations.Transfer(IReadOnlyList<string>, string dest, bool move, OperationProgress) → Task<List<string>> (실패 메시지 목록)
- FileOperations.Compress(List<string>, string zipPath, OperationProgress) → Task<string?> (오류 or null)
- FileOperations.Extract(string zip, string destDir, OperationProgress) → Task<string?>
- SystemActions.Open(path) / OpenTerminal(dir, TerminalAppChoice) / ShowProperties(path)
- SystemActions.WriteFilesToClipboard(IEnumerable<string>, bool cut) / TryReadClipboardFiles(out List<string>, out bool isCut) → bool / ClipboardSequenceNumber() → uint
- RecentsService.Load(int limit, List<string> categories, CancellationToken) → List<FileItem>
- TagService.FilesWithTag(string tagName, CancellationToken) → List<FileItem>
- HangulNormalize.Scan(dir, recursive) → IReadOnlyList<HangulNormalize.Target> (Target에 Path/FixedName)
- HangulNormalize.Fix(targets) → (int fixedCount, List<string> failures)
- WindowsName.Sanitize(string name) → string
- ※ 서비스 쪽에 OperationProgress가 중복 정의됐으면 Models.OperationProgress로 통일.

## 진행 업데이트 (2026-06-12)

- **전체 구현 완료 + 통합 빌드 통과 + 실행 확인**: UI 7개 영역(사이드바/상세 목록/시트 8종/설정/
  AI 정리/프로그램 제거/시스템 모니터) 전부 구현. 빌드 오류 수정 2건(SortKey 모호성,
  MiddleEllipsisTextBlock sealed override) + 크래시 수정 1건(Styles.xaml ScrollBar — ControlTemplate
  내부 트리거에서 Template 설정 금지 패턴).
- 앱 실행 성공 — 창 제목 '최근 항목'(기본 탭 없음 → 최근 항목 시작, 원본과 동일 동작).
  ※ 머신이 잠금 상태라 화면 캡처 불가 — 잠금 해제 후 시각 검증 필요. Release 빌드로 실행 중.
- Debug 출력 exe는 크래시 좀비 프로세스(3872/4256/26300)가 잠금 — 재부팅/로그오프 시 해소.
  그동안 빌드는 -c Release 또는 -o bin\check 사용.
- 앱 아이콘(.icns→.ico) 적용, README.md 작성.
- 리뷰 워크플로우는 **토큰 한도(11:40am 리셋)로 5개 리뷰어 전부 실패** — 아직 수행 안 됨.

## 남은 일 (다음 세션 — 14:30 자동 재개 또는 수동)

1. **멀티 에이전트 코드 리뷰 재실행**: 세션 워크플로우 스크립트
   `xfinder-review-wf_eb6a3a7a-815.js` 와 동일 구성(5 영역 리뷰 → critical/major 반박 검증).
   새 세션이면 그 스크립트 내용 참고해 동일 Workflow 재작성 → 확정 버그 수정 → 재빌드.
2. **시각/기능 검증** (사용자가 잠금 해제한 뒤): 앱 실행해 사이드바/목록/탭/검색/시스템 모니터/
   설정/AI 정리 화면 확인. 실행: `dotnet run --project D:\project\xFinder` 또는
   `D:\project\xFinder\bin\Release\net8.0-windows\XFinder.exe`.
3. Debug exe 잠금(좀비 PID 3872/4256/26300)은 재부팅·로그오프로 해소.
4. 끝나면 작업 스케줄러 정리: `Unregister-ScheduledTask -TaskName "XFinder-Claude-Resume" -Confirm:$false`

## 다음 할 일 (순서대로)

1. 백엔드 워크플로우 결과 확인 (Services/*.cs 생성됐는지) → 시그니처 대조 → `dotnet build` 통과시키기.
2. **UI 구현** — 멀티 에이전트 워크플로우로 병렬 작성 (스펙 02/03/04/06/07 + 08 정본 기준):
   - (내가 직접) MainWindow.xaml(.cs): WindowChrome 커스텀 타이틀바+툴바, 경로 막대(브레드크럼/편집),
     탭 바(8색 알약), PreviewKeyDown 전역 키 라우팅(02 §7 표), 확인 오버레이/알림 오버레이/type-select HUD,
     시트 라우팅 → Views.SheetPresenter.Present(owner, model) 호출.
   - 에이전트: SidebarView.xaml (스펙 02 §6), DetailView.xaml (스펙 03 — 목록/아이콘/그룹 헤더/선택/DnD/상태바),
     Sheets(다이얼로그들 + SheetPresenter, 스펙 04), SettingsWindow(04 §10, SettingsWindowPresenter.Show(model)),
     UninstallWindow(04 §11 Windows 재설계), AIOrganizeWindow(07), SystemStatsView+CPU/Memory/Disk 팝업(06).
   - 계약: AppModel.SettingsRequested 이벤트 → SettingsWindowPresenter.Show(model).
     MainWindow가 model.Sheet 변경 감지 → SheetPresenter.Present.
3. 통합 빌드 → 오류 수정 → 실행 검증 (`dotnet run`) → 스크린샷 확인.
4. 멀티 에이전트 코드 리뷰 (버그/스펙 불일치) → 수정.
5. README.md 작성 (한국어, mac 버전 README 구조 따라).

## 참고

- 워크플로우 스크립트/리줌: 세션 디렉토리에 저장됨. 새 세션에서는 그냥 spec/ + 이 문서 기준으로 진행.
- 빌드: `dotnet build D:\project\xFinder\XFinder.csproj` (bootstrap MainWindow는 임시 — 풀 UI로 교체 예정).
- 컨벤션: UI 문자열·주석 한국어(스펙 원문 그대로), 경로 비교 OrdinalIgnoreCase, NuGet 추가 금지(System.Management만).
