# 08 — 보충 스펙 (스펙 01~07 누락·모순 검수 결과)

> 검수일: 2026-06-11. 대조 대상: `D:\project\xFinder-mac-src\Sources` Swift 32개 파일 전체 +
> `D:\project\xFinder-mac-src\CHANGELOG.md`. 모든 모순 항목은 **원본 소스를 직접 읽어 확인**했으며,
> 아래 "정본(normative)" 규정이 01~07의 해당 문구보다 우선한다.

---

## 1. 소스 파일/기능 커버리지 — 누락 없음

Sources 하위 Swift 32개 파일 전부가 스펙 01~07 중 최소 한 곳에서 다뤄짐을 확인.

| 영역 | 파일 | 담당 스펙 |
|---|---|---|
| App | App.swift | 02 §1, §7 (메뉴 커맨드 전문 일치 확인) |
| Model | AppModel / PaneTab / FileItem / SidebarItem / Enums | 01 (+02, 03 참조) |
| Services | FileSystemService, FileOperations, SystemActions, RecentsService, TagService, ThumbnailCache, HangulNormalize, WindowsName | 05 (SystemActions 함수 18개 전수 대조 — 누락 없음) |
| Services | KeyboardMonitor | 03 §6 (키 코드 전수 대조 — 누락 없음) |
| Services | SystemMonitor, SMC | 06 |
| Services | AIService, AppUninstaller | 07, 04 |
| Views | RootView, SidebarView, DetailView, FileDrag, FolderDrop | 02, 03 |
| Views | Sheets, SettingsWindow, UninstallSheet | 04 |
| Views | AIOrganizeSheet | 07 |
| Views | CPUDetailView, MemoryDetailView, DiskDetailView | 06 |

사소 보충(스펙 미기재이나 포팅 영향 없음): `App.swift` init의
`NSApplication.setActivationPolicy(.regular)` + 비동기 `activate` — 시작 시 앱 전면 활성화.
WPF에서는 기본 동작이므로 별도 구현 불필요.

## 2. 스펙 간 모순 — 정본 규정 (소스 확인 완료)

### 2.1 `XFinder.folderViewModes.v1` 저장값 ★

- 모순: 04 §12 표는 값을 `"list"/"icons"`로 기재. 01 §7(#3)은 `"full"/"icon"`.
- 소스 확인: `Model/Enums.swift` — `enum ViewMode: String { case full; case icon }`,
  `Model/AppModel.swift` `saveFolderViewModes()`가 `ViewMode.rawValue`를 그대로 저장.
- **정본: 값은 `"full"` / `"icon"`** (01이 맞음). 04 §12의 해당 행은 무시할 것.
  설정 JSON 호환을 위해 C# 직렬화도 `"full"/"icon"` 소문자 문자열 사용.

### 2.2 `XFinder.recentsCategories.v1` 기본값 ★

- 모순: 04 §12는 기본값을 "(미설정=전체)"로 기재. 01 §6.1·§7(#10)은 "키 없음 → `["문서","이미지"]`".
- 소스 확인: `AppModel.loadRecentsCategories()` — 키가 없으면 `["문서","이미지"]` 반환,
  사용자가 저장한 **빈 배열은 그대로 빈 집합(=전체 표시)** 으로 유지.
- **정본: 키 없음 → `{"문서","이미지"}`, 빈 배열(저장됨) → 전체 표시.** "키 없음"과 "빈 배열"을
  반드시 구분해 구현할 것 (01이 맞음, 04 표의 기본값 기재는 오류).

### 2.3 폴더 드롭 시 "복사" 보조키 (mac 원본 동작 기술 불일치)

- 모순: 01 §6.13은 "⌥/⌘ 누르면 복사"(AppModel.swift 1913행의 **낡은 주석**을 옮긴 것),
  02 §6.4 / 03 §3.6은 "⌃(Control) 누르면 복사".
- 소스 확인: `Views/FolderDrop.swift` — `copyHeld = NSEvent.modifierFlags.contains(.control)`.
  실제 동작은 **⌃ Control = 복사**가 맞음 (02/03이 정확, 05 §2.9의 "코드 기준" 판단도 코드 주석을
  근거로 ⌥/⌘라 했으나 실 구현은 ⌃).
- **정본(Windows): 무보조 = 이동, Ctrl = 복사** (탐색기 관례 — 모든 스펙의 Windows 결론과 일치하므로
  포팅 영향 없음. mac 원본 동작 기록만 ⌃로 정정).

### 2.4 `TerminalApp`의 Windows 재해석 ★ (스펙 4곳이 서로 다름)

- 모순: 01 §1.6 = terminal→wt / iterm→PowerShell, 02 §2.3 = 라벨 "자동/PowerShell/Windows Terminal",
  04 §10.3 = `{auto, wt("Windows Terminal"), cmd("명령 프롬프트")}`, 05 §3.1·§9 = `{auto, cmd, wt}`
  (auto = wt 있으면 wt, 없으면 cmd).
- **정본(이 문서가 단일 기준):**
  - 저장 키·값은 원본 그대로 유지: `XFinder.terminalApp.v1` ∈ `"auto" | "terminal" | "iterm"`.
  - 의미 재해석: `auto` = wt.exe 있으면 Windows Terminal, 없으면 PowerShell /
    `terminal` = Windows Terminal(wt.exe, 없으면 PowerShell 폴백) / `iterm` = PowerShell.
  - 라벨: "자동" / "Windows Terminal" / "PowerShell".
  - 실행: `wt -d "<dir>"` / `powershell -NoExit -Command Set-Location -LiteralPath '<dir>'`.
  - 설정 화면 힌트 문구(04 §10.3의 iTerm 분기)는 위 3케이스에 맞춰 재작성
    (예: auto+wt 있음 → "자동: Windows Terminal이 설치되어 wt로 엽니다.").

### 2.5 새 폴더 단축키 "F7" 기재 오류

- 모순: 04 §4 제목에 "⇧⌘N / F7". 03 §6 키보드 표에는 F7 없음.
- 소스 확인: F7은 `Sheets.swift` 111행의 `// MARK: - New folder (F7)` **주석에만** 존재.
  `KeyboardMonitor`(keyCode 98 미처리)와 메뉴 어디에도 F7 바인딩 없음.
- **정본: 새 폴더 단축키는 ⇧⌘N(→ Ctrl+Shift+N)뿐. F7 구현하지 말 것.**

### 2.6 설정 창 크기 (명확화 — 실모순 아님)

- 02 §8.1 "minWidth 500 / ideal 520, minHeight 560 / ideal 620"은 SwiftUI 뷰 제약이고,
  04 §10.1 "크기 조절 없음"이 창 동작. 소스 확인: `SettingsWindow.swift` styleMask =
  `[.titled, .closable, .miniaturizable]`(resizable 없음), 콘텐츠 520×620 고정.
- **정본: WPF `ResizeMode=CanMinimize`, 고정 520×620.** 뷰 min 치수는 구현 불필요.

### 2.7 복제 단축키 제안 통일 (경미)

- 03 §6.4는 "Ctrl+D (충돌 검토 후 Ctrl+Shift+D 대안)", 05 §2.10은 "Ctrl+D".
- **정본: Ctrl+D 채택** (탐색기에 Ctrl+D 삭제 관례가 있으나 본 앱은 Delete를 삭제로 쓰므로 충돌 없음).

### 2.8 모순 아님 확인(기록)

- About/Manual 헤더 그라데이션(파랑→보라, 04 §8)과 AI 정리 시트 헤더(보라→파랑, 07 §7.2)는
  소스에서도 서로 반대 방향이 맞음 — 각 스펙이 자기 뷰를 정확히 기술.
- 더블클릭 간격: 파일 목록 0.3초 고정(03 §1.8, DetailView 264행), 사이드바는 시스템 값
  (02 §6.4, SidebarView 163행 `NSEvent.doubleClickInterval`) — 뷰별로 다른 것이 원본 동작.
- Ollama 기본 모델 `"gemma4:latest"` — 오타가 아니라 소스 그대로(`AIService.swift` 81행). 유지.
- UninstallSheet 로드 완료 시 전 항목 기본 체크 — 소스 확인(`UninstallSheet.swift` 142행), 04 §11.4 맞음.

## 3. CHANGELOG(v1.5~v1.7, 2026-06-08) 대비 — 누락 없음

| CHANGELOG 항목 | 스펙 반영 위치 |
|---|---|
| v1.7 다중 탭 / 탭 색 / 포워딩 계약 | 01 §6.7, 02 §5 |
| v1.7 기본 탭 (`XFinder.defaultTabs.v1`) | 01 §6.8·§7, 02 §8.2, 04 §10.3 |
| v1.7 다음으로 그룹화 (sticky 헤더, 경계 검사) | 01 §4.4, 02 §3.4, 03 §2.5 |
| v1.6 열 너비 조절 (`XFinder.columnWidths.v1`) | 01 §2.4, 03 §2.2·§9 |
| v1.6 Gemini 안내 팝업 | 07 §7.9 |
| v1.6 날짜 표시 옵션 (실제/상대) | 01 §3.2, 03 §2.3, 04 §10.4 |
| v1.6 정보 가져오기(⌘I) | 01 §6.14, 03 §5.1·§6.4 |
| v1.6 검색창 위치 옵션 | 01 §1.6, 02 §3.5~3.6, 04 §10.4 |
| v1.6 검색창 포커스 강탈 버그 수정(onDrop 정체성 유지 + textInputActive) | 03 §3.6 중요 구현 노트, 02 §1.4 |
| v1.5 파일 계산 typeMode + 무한 스크롤 | 01 §6.9, 06 §7.7 |
| v1.5 fts 병렬 스캔 + 캐시 | 05 §1.7·§1.10, 06 §5 |
| v1.5 type-select(한글 자모) + HUD | 01 §6.10, 03 §7 |
| 2026-06-08 리브랜딩/SF Rounded | 02 §1.1 (폰트 대체 명시) |
| 2026-06-08 배경 컨텍스트 메뉴 | 03 §5.2 |
