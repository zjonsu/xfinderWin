# 02 — 메인 UI 포팅 스펙 (창 레이아웃 · 툴바 · 경로 막대 · 탭 바 · 사이드바 · 메뉴 · 설정 · 다이얼로그)

> 소스: `Sources/XFinder/Views/RootView.swift`(886줄, 전체 분석), `Views/SidebarView.swift`, `App.swift`,
> `doc/navigation.md`, `doc/ui-appearance.md`. 참조 보강: `Model/AppModel.swift`, `Model/SidebarItem.swift`,
> `Model/PaneTab.swift`, `Model/Enums.swift`, `Services/TagService.swift`, `Services/AIService.swift`.
>
> 모든 수치는 macOS 포인트(pt) 단위. WPF에서는 1pt = 1 DIU(96dpi 기준)로 그대로 옮기면 된다.
> `scale` = `AppModel.listScale`(0.8~1.8, 기본 1.0) — "× scale" 표기가 붙은 수치는 목록 크기 설정에 비례.

---

## 1. 창 구조와 다중 창

### 1.1 창 기본값
- 기본 크기: **1080 × 680**. 최소 크기: **760 × 460** (루트 뷰에도 `minWidth 760, minHeight 460` 중복 지정).
- macOS는 `windowToolbarStyle(.unified)` — 툴바가 네이티브 타이틀 바에 합쳐진 형태(Liquid Glass).
  보더리스가 아니라 **타이틀 바 + 툴바 일체형**이다.
- 창 제목(`windowTitle`): 활성 탭의 `tabTitle`. 단 `"/"`이면 `"Macintosh HD"`로 표시.
- 앱 전체 폰트: `fontDesign(.rounded)` (SF Rounded). 시스템 산세리프의 둥근 변형.

**Windows 대응**: WPF `WindowChrome`(CaptionHeight ≈ 48, GlassFrameThickness=0 아님 — 시스템 버튼 유지)로
타이틀 바 영역에 툴바를 직접 그린다. Win11이면 둥근 모서리는 DWM이 자동 적용
(`DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND)` 명시 가능).
배경 Mica: `DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_MAINWINDOW` 베스트에포트, 실패 시 단색.
폰트는 "Segoe UI Variable" (둥근 변형 없음 — SF Rounded 느낌은 포기, 명시).
창 제목의 `"Macintosh HD"` 대응 → 드라이브 루트면 볼륨 레이블(예: `"로컬 디스크 (C:)"`) 표시.

### 1.2 분할 레이아웃 (NavigationSplitView)
```
┌────────────────────────────────────────────────────────┐
│ [타이틀바/툴바 (unified)]  좌: 시스템상태│탐색버튼들  우: 보기·작업·검색 │
├──────────────┬─────────────────────────────────────────┤
│  사이드바      │ (탭 2개 이상일 때) TabBarView + Divider     │
│  min 180     │ (검색창 위치=below일 때) searchBarRow+Divider│
│  ideal 215   │ PathBar + Divider                        │
│  max 340     │ DetailView (파일 목록 — 03 스펙)            │
└──────────────┴─────────────────────────────────────────┘
```
- 사이드바 칼럼 폭: **min 180 / ideal 215 / max 340**, 사용자가 경계 드래그로 조절(네이티브).
- 상세(detail) 영역 최소 폭: **480**.
- 상세 영역은 `VStack(spacing: 0)`으로 위에서부터: 탭 바(조건부) → 검색 줄(조건부) → PathBar → Divider → DetailView.
- 사이드바는 반투명(behind-window blur) — 스크롤 배경 숨김(`scrollContentBackground(.hidden)`).

**Windows 대응**: `Grid` 2열 + `GridSplitter`(폭 1~4px). 사이드바 반투명은 WPF에서 부분 블러가 불가 —
테마 배경색(라이트 `#F2F2F7` 근사 / 다크 `#262626` 근사)의 단색 패널로 대체하고 명시해 둔다.
네이티브 사이드바 접기 토글(NavigationSplitView 제공)은 툴바에 햄버거/사이드바 토글 버튼을 직접 추가해 대체.

### 1.3 다중 창
- `⌘N` "새 창" → 같은 WindowGroup의 새 인스턴스. **창마다 독립된 `AppModel`** (탐색·선택·히스토리·탭 전부 독립).
- `SystemMonitor.shared`(CPU/메모리/디스크)는 **모든 창이 공유하는 싱글턴**.
- 메뉴 커맨드는 `@FocusedValue(\.appModel)`로 **포커스된 창**의 모델에 라우팅된다.
- 창 시작 시: `app.bootstrap()`(중복 가드 있음, init은 부수효과 없음), `SystemMonitor.shared.start()`, `app.applyAppearance()`.
- `WindowAccessor`: 호스팅 NSWindow를 찾아 `app.window`에 저장 — 키 이벤트가 자기 창의 것인지 판별용.

**Windows 대응**: `new MainWindow { DataContext = new AppModel() }.Show()`. WPF는 창마다 메뉴가 따로 있으므로
FocusedValue 라우팅이 필요 없음(각 창의 메뉴/단축키가 자연히 그 창의 DataContext를 사용).
`app.window` 대응 → 각 창이 자기 `Window` 참조 보유, 전역 키 후킹 대신 `PreviewKeyDown` 사용.

### 1.4 루트 오버레이/시트/알림
- `app.sheet`(enum `Sheet`, Identifiable)로 모달 시트 9종 표시:
  - `viewer(FileItem)` → ViewerSheet (미리 보기)
  - `goToFolder` → GoToFolderSheet
  - `newFolder` → NewFolderSheet
  - `rename(FileItem)` → RenameSheet
  - `progress(OperationProgress)` → ProgressSheet
  - `about` → AboutSheet ("XFinder 정보")
  - `manual` → ManualSheet (사용설명서)
  - `uninstall(FileItem)` → UninstallSheet (앱 삭제)
  - `aiOrganize` → AIOrganizeSheet
  - `id` 문자열: `"viewer:{path}"`, `"goto"`, `"newfolder"`, `"rename:{path}"` 등.
- `app.confirm`(ConfirmRequest?)이 있으면 **전체 화면 오버레이**로 ConfirmDialog 표시 (§9).
- 오류 알림: 제목 **"오류"**, 본문 `app.errorMessage`, 버튼 **"확인"** (cancel role).
- 안내 알림: 제목 **"XFinder"**, 본문 `app.infoMessage`, 버튼 **"확인"**.
- 검색창 포커스 변화 → `app.textInputActive` 갱신 (텍스트 입력 중에는 KeyboardMonitor의 type-select 등 정지).

**Windows 대응**: 시트 → 소유자 지정 모달 `Window.ShowDialog()` 또는 루트 Grid 위 오버레이 컨트롤.
alert → 자체 스타일 다이얼로그(MessageBox는 외형 불일치).

---

## 2. 데이터 구조 (C# 대응 설계)

### 2.1 SidebarItem (`Model/SidebarItem.swift`)
```csharp
public enum SidebarItemKind { Folder, Computer, AirDrop, Trash, Recents, Tag }

public sealed class SidebarItem : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; init; }          // 표시 이름
    public string Icon { get; init; }           // 원본은 SF Symbol 이름 → 글리프 키로 치환
    public string? Url { get; init; }           // 대상 경로 (AirDrop·태그·최근항목은 null)
    public SidebarItemKind Kind { get; init; }
    public int Depth { get; init; }             // 트리 들여쓰기 레벨 (0 = 최상위)
    public bool IsExpanded { get; set; }                    // [Observable]
    public List<SidebarItem>? Children { get; set; }        // null = 아직 미로드
    public bool HasCheckedChildren { get; set; }
    public bool MayHaveChildren { get; set; } = true;       // 디스클로저(▸) 표시 여부

    public bool IsSelectable => Url != null;
    public bool CanExpand => (Kind is Folder or Computer) && MayHaveChildren;
    // LoadChildren(showHidden): 한 단계 하위 폴더 지연 로드.
    //  - Computer 노드는 /Volumes 대신 → Windows: DriveInfo.GetDrives() 목록
    //  - 자식에 하위 폴더가 있으면 icon "folder.fill"(채움) / 없으면 "folder", hasChildren도 그에 따름
}

public sealed class SidebarSection // struct → record/class
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; init; }          // "즐겨찾기" | "위치" | "태그"
    public List<SidebarItem> Items { get; set; }
}
```

### 2.2 ConfirmRequest / Clipboard / FocusPane (`Model/AppModel.swift` 47–65행)
```csharp
public sealed class ConfirmRequest
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; set; }
    public string Message { get; set; }
    public string ConfirmTitle { get; set; }    // 확인 버튼 라벨 (예: "삭제")
    public bool IsDestructive { get; set; }     // true → 빨간 버튼
    public Action Action { get; set; }
}

public sealed class ClipboardState { public List<string> Urls; public bool IsCut; } // 내부 클립보드

public enum FocusPane { Sidebar, Detail }       // Tab 키로 전환, 사이드바 강조 농도 결정
```

### 2.3 설정 관련 enum (rawValue = 소문자 케이스명, UserDefaults 저장값)
```csharp
public enum AppearanceMode { System, Light, Dark }            // 라벨: 시스템/라이트/다크
public enum TerminalApp    { Auto, Terminal, Iterm }          // 라벨: 자동/터미널/iTerm
public enum DateDisplayStyle { Absolute, Relative }           // 라벨: "실제 날짜"/"상대 시간"
public enum SearchBarPosition { Toolbar, Below }              // 라벨: "툴바"/"툴바 아래"
public enum AIProvider { Ollama, Gemini }                     // 라벨: "로컬 (Ollama)"/"Gemini"
public enum GroupKey { None, Name, Kind, Size, Modified, Created }
// GroupKey 라벨: 없음/이름/종류/크기/수정일/생성일
```
- `TerminalApp` Windows 대응: Auto/`Windows Terminal(wt.exe)`/`cmd` 또는 `PowerShell`로 재해석
  (Auto = wt.exe 있으면 wt, 없으면 PowerShell). 라벨도 그에 맞게 교체 권장: "자동"/"PowerShell"/"Windows Terminal".

### 2.4 PaneTab 중 본 스펙 관련 필드 (`Model/PaneTab.swift`)
```csharp
public sealed class PaneTab : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Directory { get; set; }          // 이 탭의 현재 폴더
    public List<string> History = new();           // 탭별 뒤로/앞으로
    public int HistoryIndex = -1;
    public int ColorIndex = 0;                     // ★ 탭 색 — 생성 순서 카운터값, 탭에 영구 저장
    // ... rawItems/items/selection/sort/filter/viewMode/groupKey 등은 03 스펙
    public string TabTitle =>
        RecentsMode ? "최근 항목"
      : TagMode && TagName != null ? TagName
      : TypeMode && TypeName != null ? $"파일 계산 — {TypeName}"
      : SearchMode ? $"검색: {Filter}"
      : Title;                                     // 일반 모드: 폴더 이름
}
```

### 2.5 AppModel 탭 포워딩 계약 (★ 포팅 시 그대로 유지)
- `tabs: [PaneTab]` — **항상 1개 이상**. `activeTabIndex: Int`.
- `detail`(활성 탭), `selectedFolder`, `history`, `historyIndex`는 활성 탭으로 포워딩되는 **계산 프로퍼티**.
  C#에서도 `public PaneTab Detail => Tabs[Math.Clamp(ActiveTabIndex, 0, Tabs.Count - 1)];` 형태 유지.
- **비동기 작업은 시작 시점에 `var pane = Detail`로 캡처** — 완료 시 사용자가 탭을 바꿨으면 `Detail`이 다른 탭.
- `tabColorCounter`: 새 탭마다 1씩 증가하는 카운터. `newTab()`에서 `pane.colorIndex = tabColorCounter++`.
- 탭 전환(`selectTab`)은 디렉터리를 **다시 읽지 않는다** — 목록·커서·선택 보존이 의도된 동작.
- `draggingFavorite: URL?` — 사이드바 즐겨찾기 드래그 중 그 URL(순서 변경 판별), 드롭/종료 시 null. 관찰 제외(@ObservationIgnored).

---

## 3. 툴바 (네이티브 unified — 타이틀 바 안에 렌더)

### 3.1 왼쪽(leading) 그룹 — 순서대로
1. **SystemStatsView** (§10) — `padding(.leading, 8)`
2. **섹션 구분선** `sectionDivider`: 1 × 16 사각형(코너 0.5), 색 `secondary @ 0.25`, 좌우 패딩 5
3. **탐색 버튼 묶음** `HStack(spacing: 2)` — `NavButton` 3개:
   | 아이콘 (SF) | 툴팁 | 동작 | 비활성 조건 |
   |---|---|---|---|
   | `chevron.backward` | `뒤로 — 이전 폴더로 이동 (⌘[)` | `goBack()` | `!canGoBack` |
   | `chevron.forward` | `앞으로 — 다음 폴더로 이동 (⌘])` | `goForward()` | `!canGoForward` |
   | `chevron.up` | `상위 폴더로 이동 (⌘↑)` | `goUp()` | 없음 |
4. **섹션 간격** `sectionGap`: 투명 폭 10
5. **위치 동작 묶음** `HStack(spacing: 2)` — `NavButton` 4개:
   | 아이콘 | 툴팁 | 동작 |
   |---|---|---|
   | `terminal` | `현재 폴더를 터미널에서 열기` | `openTerminal()` |
   | `calendar` | `Dayflow 캘린더 열기` | `openDayflow()` |
   | 텍스트 `"가나"` | `한글 자소 분리(NFD) 파일명 복원 — 클릭: 이 폴더 / 우클릭: 하위 폴더까지` | `fixDecomposedNames(recursive: false)` |
   | `sparkles` | (아래 3분기) | `requestAIOrganize()` |
   - `"가나"` 버튼 우클릭 컨텍스트 메뉴: **"이 폴더의 한글 파일명 복원"** / **"하위 폴더까지 복원"** (recursive true).
   - `sparkles` 툴팁 분기: 응용 프로그램 위치면 `응용 프로그램 폴더는 AI 파일 정리에서 제외됩니다`,
     보호 위치면 `시스템 폴더는 AI 파일 정리에서 제외됩니다`,
     그 외 `AI 파일 정리 — 프롬프트로 현재 폴더 파일 정리 (로컬 LLM)`.
     비활성 조건: `aiOrganizeBlocked(selectedFolder)`.
   - 묶음 끝에 `padding(.trailing, 14)` (타이틀과의 여백).

#### NavButton 외형 (둥근 호버 하이라이트 버튼)
- 크기: 아이콘형 **30 × 26 × scale**, 텍스트형(`"가나"`) **34 × 26 × scale**.
- 배경: `RoundedRectangle(cornerRadius: 7, continuous)` — 기본 `primary @ 0.05`, 호버(비활성 아님) `primary @ 0.10`.
- 테두리: 같은 모양 stroke `primary @ 0.06`, 두께 0.5.
- 글리프: 아이콘 폰트 13 × scale semibold / 텍스트 폰트 12 × scale semibold.
- 비활성 시 전경색 `secondary @ 0.4`, 평소 `primary`. `.help` 툴팁.

### 3.2 오른쪽(trailing, primaryAction) 그룹 — 순서대로
`HStack(spacing: 2)`에 `iconButton`/메뉴 5개:
| 항목 | 아이콘 | 툴팁 | 동작 |
|---|---|---|---|
| 보기 전환 | viewMode == .full → `square.grid.2x2`, 아니면 `list.bullet` | `.full`: `아이콘 보기로 전환 (⌃M)` / 그 외: `목록 보기로 전환 (⌃M)` | `toggleViewMode()` |
| 그룹화 메뉴 | `square.grid.3x1.below.line.grid.1x2` | `다음으로 그룹화 — 이름·종류·크기·수정일·생성일 구간으로 묶어 보기` | 메뉴 (아래) |
| 새 폴더 | `folder.badge.plus` | `현재 위치에 새 폴더 만들기 (⇧⌘N)` | `requestNewFolder()` |
| 작업 메뉴 | `ellipsis.circle` (14 × scale) | `작업 — 열기·복사·이동·압축·이름 변경 등` | 메뉴 (아래) |
| 숨김 토글 | showHidden → `eye.fill`, 아니면 `eye.slash` | showHidden: `숨김 파일 숨기기 (⇧⌘.)` / 아니면 `숨김 파일 표시 (⇧⌘.)` | `toggleHidden()` |

그 뒤 `searchPosition == .toolbar`이면: **sectionDivider** + **검색 필드**(§3.5).

#### iconButton 외형
- 글리프 13 × scale, 프레임 **26 × 24 × scale**, plain 스타일(배경 없음, 시스템 호버 없음).
- 비활성 시 전경 `secondary @ 0.5`, 평소 `primary`.

### 3.3 작업(⋯) 메뉴 — 항목 전문 (구분선 포함 순서 그대로)
```
열기                    → openSelected()
미리 보기               → viewSelected()
──────────
복사                    → copySelection()
잘라내기                → cutSelection()
붙여넣기                → paste()          (clipboard == null이면 비활성)
복제                    → duplicate()
이름 변경…              → requestRename()
──────────
압축                    → compressSelected()
압축 풀기               → extractSelected()
──────────
Finder에서 보기         → revealInFinder()
터미널 열기             → openTerminal()
──────────
한글 파일명 복원 ▸       (서브메뉴)
   이 폴더              → fixDecomposedNames(recursive: false)
   하위 폴더까지         → fixDecomposedNames(recursive: true)
AI 파일 정리…           → requestAIOrganize()
──────────
설정…  (⌘,)            → openSettings()
휴지통으로 이동          → requestDelete()
```
- 메뉴 인디케이터(▾) 숨김(`menuIndicator(.hidden)`).
- Windows: "Finder에서 보기" → **"탐색기에서 보기"**로 문구 교체, `explorer.exe /select,"path"`.

### 3.4 그룹화 메뉴
- `GroupKey.allCases` 전체를 나열: **없음 / 이름 / 종류 / 크기 / 수정일 / 생성일**.
  현재 선택 항목에는 `checkmark` 아이콘(라벨의 systemImage 자리).
- 클릭 → `setGroupKey(key)`.
- **가상 목록 모드(검색/최근 항목/태그/파일 계산)에서는 메뉴 전체 비활성**:
  `detail.searchMode || recentsMode || tagMode || typeMode`.
- 아이콘 색: `groupKey == .none`이면 `primary`, 그룹화 켜져 있으면 **accentColor** (켜짐 표시).

### 3.5 검색 필드 (툴바 위치)
- placeholder: **"하위 폴더까지 검색"**. 바인딩: get `detail.filter` / set `updateSearch($0)` (탭별 필터).
- plain 스타일 + 커스텀 배경(포커스 링 완전 제거가 의도 — Windows에선 기본 포커스 시각 제거).
- 폰트 13 × scale, 폭 **170 × scale**, 패딩 가로 8 / 세로 4.
- 배경: `RoundedRectangle(6, continuous)` fill `textBackgroundColor`(라이트 흰색/다크 짙은 회색),
  stroke `secondary @ 0.25` 두께 1. 끝에 `padding(.trailing, 18)`.
- 툴팁: **"이름으로 검색 — 현재 폴더(하위 폴더 포함)에서 걸러냅니다"**.
- 포커스 시 `app.textInputActive = true` → 전역 키 처리(type-select 등) 정지.

### 3.6 검색 줄 (searchBarRow — 설정에서 "툴바 아래" 선택 시)
- 탭 바 아래(있다면), PathBar 위에 **전체 폭 한 줄** + Divider.
- 내용 `HStack(spacing: 6)`: `magnifyingglass`(12 × scale, secondary) + TextField(13 × scale, 같은 바인딩/placeholder)
  + 필터 비어있지 않으면 지우기 버튼 `xmark.circle.fill`(12 × scale, secondary, 툴팁 **"검색어 지우기"**, 클릭 → `updateSearch("")`).
- 내부 패딩 가로 9 / 세로 5. 배경 `RoundedRectangle(7, continuous)` fill `textBackgroundColor`,
  stroke `secondary @ 0.25` 두께 1.
- 바깥 패딩 가로 10 / 세로 6, 줄 배경 `windowBackgroundColor`.
- 툴팁(줄 전체): "이름으로 검색 — 현재 폴더(하위 폴더 포함)에서 걸러냅니다".

---

## 4. 경로 막대 (PathBar) — 툴바 아래 한 줄

### 4.1 모드 분기 (탭의 가상 모드에 따라)
1. `recentsMode` → specialBar(icon `clock.fill`, 제목 **"최근 항목"**, 색 secondary)
2. `typeMode` → specialBar(icon/색은 `DiskDetailView.meta(forTypeName:)`에서, 제목은 §4.2)
3. `tagMode` → specialBar(icon `circle.fill`, 제목 = 태그 이름, 색 = 표준 태그 색 (없으면 secondary))
4. 그 외 → 편집 가능한 브레드크럼 바

### 4.2 typeMode 제목 문자열 (스크롤 페이징 안내 포함)
- 더 불러올 항목이 남았으면(`typeTotal > items.count`):
  `파일 계산 — {name} (크기순 · {shown}개 로드됨 / 전체 {total}개)` (숫자는 천 단위 구분 표기)
- 전부 로드됨: `파일 계산 — {name} ({total}개)`

### 4.3 specialBar 외형
- `HStack(spacing: 6)`: 아이콘 폰트 11(지정 색) + 제목 폰트 12 medium + Spacer.
- 패딩 가로 10 / 세로 4. 배경 `windowBackgroundColor`.

### 4.4 브레드크럼 (editableBar 비편집 상태)
- 맨 앞 `folder` 아이콘(11, secondary).
- 세그먼트 계산: `selectedFolder`에서 루트까지 거슬러 올라가며 `(이름, URL)` 수집 후 뒤집기.
  루트 `"/"`의 이름은 **"Macintosh HD"** (Windows: 드라이브 레이블).
- 가로 스크롤(`ScrollView(.horizontal)`, 인디케이터 숨김), `HStack(spacing: 2)`.
- 세그먼트 사이에 `chevron.right`(8, secondary). 각 세그먼트는 plain 버튼, 폰트 12.
  마지막(현재 폴더) = `primary`, 나머지 = `secondary`. 클릭 → `app.select(url)` (그 폴더로 이동).
- **빈 영역 더블클릭** → 편집 모드 진입.
- 연필 버튼 `pencil`(11, secondary), 툴팁 **"경로 직접 입력"** → 편집 모드 진입.

### 4.5 편집 모드
- TextField placeholder: **"경로 입력 (예: ~/Downloads)"**, plain, 폰트 12, 진입 시 현재 경로로 채우고 포커스.
- 포커스 동안 `textInputActive = true`.
- **Enter** → 트림 후 비어있지 않으면 `app.goToFolder(text)` 호출, 편집 종료. **Esc** → 취소(편집 종료만).
- 이동 버튼 `arrow.right.circle.fill`(13, secondary), 툴팁 **"이동"** — Enter와 동일.
- `selectedFolder`가 (다른 경로로의 탐색 등으로) 바뀌면 편집 모드 자동 해제.
- 패딩/배경은 브레드크럼과 동일(가로 10/세로 4, `windowBackgroundColor`).
- Windows: `~` 확장 → `%USERPROFILE%`, 환경변수 확장(`Environment.ExpandEnvironmentVariables`)도 지원 권장.

---

## 5. 탭 바 (TabBarView) — 파인더식 다중 탭

### 5.1 표시 조건과 동작
- **탭이 2개 이상일 때만** 탭 바 표시(+아래 Divider). 1개면 완전히 숨김.
- `⌘T` 새 탭: 현재 폴더를 복제한 탭 생성 — `newTab()`은
  새 PaneTab(directory = 현재 폴더), history=[그 폴더], historyIndex=0,
  viewMode = `folderViewModes[경로] ?? .full`, **groupKey는 현재 탭 것을 상속**,
  `colorIndex = tabColorCounter++`, tabs에 append, 활성화, 사이드바 선택 동기화, focusedPane = .detail, 목록 로드.
- `⌘W` 탭 닫기 — **마지막 탭이면 창 닫기**. `⌃Tab`/`⌃⇧Tab` 탭 순환. (이 셋은 KeyboardMonitor 처리.)
- 탭 클릭 = 전환(내용 재로딩 없음). 호버 시 왼쪽에 ✕(닫기). 우클릭 = 컨텍스트 메뉴.
- 폴더 우클릭 메뉴(DetailView, 03 스펙)에 **"새 탭에서 열기"** 존재 — `newTab(folder:)`.
- 탭마다 폴더·목록·선택·정렬·히스토리·그룹화가 **전부 독립**.
- **기본 탭**: 설정 → 일반에서 저장 시 다음 실행부터 그 폴더들이 탭으로 복원(§8.2, 키 `XFinder.defaultTabs.v1`).
  저장이 없으면 "최근 항목" 탭 하나로 시작. 복원 시 존재하지 않는 폴더는 걸러냄.

### 5.2 탭 색 자동 배정 규칙 (★)
- 팔레트(8색, 순서 고정): **blue, green, orange, purple, pink, teal, indigo, brown** (시스템 파스텔 계열).
- 적용 색 = `palette[pane.colorIndex % 8]`. `colorIndex`는 **생성 순서 카운터**가 탭 자신에 저장된 값 —
  배열 위치가 아니므로 **탭을 닫아도 남은 탭들의 색이 바뀌지 않는다**.
- 같은 색에서 **농도(불투명도)로 상태 구분**: 활성 **0.38** / 호버 **0.25** / 비활성 **0.16**.
  ⚠ 비활성을 0.14 미만으로 내리지 말 것(디스플레이에 따라 안 보임 — 원문 코드 주석의 명시적 경고).
- 활성 탭 제목은 semibold + `primary`, 비활성은 regular + `secondary`.
- WPF 색 제안(라이트 기준 시스템 색): blue `#007AFF`, green `#28CD41`, orange `#FF9500`, purple `#AF52DE`,
  pink `#FF2D55`, teal `#59ADC4`, indigo `#5856D6`, brown `#A2845E`. 불투명도는 Alpha로 적용.

### 5.3 탭 바 레이아웃 수치
- 줄: `HStack(spacing: 4)`, 패딩 가로 6 / 세로 3, 배경 `windowBackgroundColor`. **구분선 없음**(알약 색으로만 구분).
- 탭 셀: 높이 **22**, `maxWidth: .infinity`(균등 폭), 모양 `RoundedRectangle(6, continuous)`, 내부 가로 패딩 4.
- 셀 내부 `HStack(spacing: 0)`:
  - 왼쪽 고정 폭 **20**: 호버 중에만 ✕ 버튼(`xmark` 8 bold, secondary, 16×16 히트영역, 툴팁 **"탭 닫기 (⌘W)"**).
    호버 아닐 땐 빈 자리 유지 → 제목이 흔들리지 않음.
  - Spacer + 제목(폰트 **11.5**, 1줄, tail 말줄임) + Spacer
  - 오른쪽 투명 고정 폭 **20** (✕와 대칭 — 제목 정중앙 유지)
- ＋ 버튼(맨 오른쪽): `plus` 11 semibold secondary, 26 × 22, 코너 6, 툴팁 **"새 탭 (⌘T)"** → `newTab()`.

### 5.4 탭 컨텍스트 메뉴
```
새 탭            → newTab()
──────────
탭 닫기          → closeTab(index)
다른 탭 닫기      → closeOtherTabs(index)   (탭 1개면 비활성)
```

---

## 6. 사이드바 (SidebarView)

### 6.1 구조
- `ScrollView` > `LazyVStack(alignment: .leading, spacing: 1)`, 바깥 패딩 6.
- 섹션 3개 — `rebuildSections()`이 만든다: **즐겨찾기**, **위치**, **태그**.
- 섹션 제목: 폰트 **11 × scale semibold**, `secondary`, 패딩 가로 10 / 위 12 / 아래 2, 좌측 정렬.

### 6.2 섹션 내용
1. **즐겨찾기**:
   - 첫 항목 고정: **"최근 항목"** (icon `clock`, kind `.recents`, url 없음, 자식 없음).
   - 이어서 사용자 즐겨찾기(`favoritePaths`) 중 **실존하는 폴더만**. kind `.folder`,
     hasChildren = 하위 폴더 존재 여부.
   - 기본 즐겨찾기(최초 실행): `/Applications`, `~/Desktop`, `~/Documents`, `~/Downloads`,
     `~/Pictures`, `~/Movies`, `~/Music` 중 존재하는 것.
   - 표시 이름 한글화(`favoriteTitle`): `/Applications`→**응용 프로그램**, Desktop→**데스크탑**, Documents→**문서**,
     Downloads→**다운로드**, Pictures→**사진**, Movies→**동영상**, Music→**음악**, Public→**공용**, Library→**라이브러리**,
     그 외 폴더명 그대로.
   - 아이콘(`favoriteIcon`): `/Applications`→`square.grid.2x2`, Desktop→`menubar.dock.rectangle`, Documents→`doc`,
     Downloads→`arrow.down.circle`, Pictures→`photo`, Movies→`film`, Music→`music.note`, 그 외→`folder`.
2. **위치**:
   - 홈 폴더(제목 = 홈 폴더명, icon `house`).
   - 마운트된 **탐색 가능한 볼륨**(숨김 볼륨 제외, 심볼릭 링크 해석 경로로 중복 제거 — `/Volumes` 링크로 인한
     "Macintosh HD" 중복 방지). 루트 볼륨 icon `internaldrive`, 그 외 `externaldrive`.
   - (컴퓨터 노드는 루트 볼륨과 중복이라 **표시하지 않음** — 코드 주석에 명시. enum에 `.computer`/`.trash`/`.airDrop`
     kind는 남아 있고 activateSidebar가 처리하지만 현재 섹션엔 안 나타남.)
3. **태그**: 파인더 표준 7색 — 순서·이름·색번호·표시색:
   | 이름 | colorIndex | 색 |
   |---|---|---|
   | 빨간색 | 6 | red |
   | 주황색 | 7 | orange |
   | 노란색 | 5 | yellow |
   | 초록색 | 2 | green |
   | 파란색 | 4 | blue |
   | 보라색 | 3 | purple |
   | 회색 | 1 | systemGray |
   - kind `.tag`, url 없음, 자식 없음. 클릭 → 그 태그가 붙은 파일만 표시(tagMode).

### 6.3 행(SidebarRowView) 외형 — 수치 전부
- 행 컨테이너: `HStack(spacing: 5)`, 패딩 세로 **5 × scale** / 가로 6, 전체 폭, 좌측 정렬,
  클립 `RoundedRectangle(cornerRadius: 6)`.
- 들여쓰기: 투명 박스 폭 `depth × 15 × scale`.
- 디스클로저: `canExpand`이면 `chevron.down`(펼침)/`chevron.right`(접힘), 10 × scale semibold,
  13 × 13 × scale 프레임. **화살표 자체 탭** → `focusedPane = .sidebar; toggleExpand(item)`.
  canExpand 아니면 같은 폭(13 × scale)의 투명 자리.
- 아이콘: 태그면 색 점(`Circle` 11 × scale, 컨테이너 폭 22 × scale),
  그 외엔 SF Symbol — **`.fill` 접미사를 제거한 외곽선 심볼**로 통일, 14.5 × scale, 폭 22 × scale,
  색은 포커스 선택 시 white, 평소 primary(모노크롬).
- 제목: 폰트 **13.5 × scale**, 1줄, **middle 말줄임**.
- 선택 강조(행 배경):
  - 선택 + 사이드바 포커스(`focusedPane == .sidebar`): **accentColor** 채움 + 전경 white
    (디스클로저는 `white @ 0.9`).
  - 선택 + 포커스 아님: `secondary @ 0.25` (흐린 강조 — 어느 패널을 방향키가 움직이는지 표시).
  - 비선택: 투명.
- 선택 판정: `app.selectedSidebarID == item.id` (항목 단위 — `/`를 가리키는 행이 둘이어도 동시 강조 금지).

### 6.4 행 상호작용
- **단일 클릭**(행 전체가 히트 영역, 여백 포함): `focusedPane = .sidebar; activateSidebar(item)` — 즉시 이동.
- **빠른 더블클릭**(시스템 더블클릭 간격 내 재클릭): `canExpand`이면 `toggleExpand(item)`.
  구현: `lastTapAt` 기억, 간격 비교(Windows: `SystemInformation.DoubleClickTime`). 더블클릭 처리 후 리셋.
- 폴더 선택만으로는 **자동으로 펼치지 않는다** — 디스클로저 직접 클릭/더블클릭만 펼침.
- 펼침 시 자식 행들을 재귀 렌더(같은 SidebarRowView, depth+1). 자식은 지연 로드(`loadChildren`):
  하위 폴더가 있으면 icon `folder.fill` + 디스클로저, 없으면 `folder` + 디스클로저 없음.
- **드래그 소스**: `kind == .folder && url != nil`인 행 — URL 페이로드로 드래그 시작.
  즐겨찾기 최상위(depth 0 + isFavorite) 행이면 `app.draggingFavorite = url`로 표시 →
  다른 즐겨찾기 위에 놓으면 **순서 변경**(FolderDrop이 실제 페이로드 검증 후 처리).
- **드롭 타깃**: 폴더 행 위로 파일 드롭 → 그 폴더로 **이동** (⌃ 누르면 **복사**) — `FolderDropModifier`.
  Windows: `DragDrop.DoDragDrop` + `DataFormats.FileDrop`; Ctrl=복사는 Windows 관례와 일치.
- **우클릭 컨텍스트 메뉴** (폴더 행만):
  ```
  즐겨찾기에 추가      (isFavorite 아닐 때)  → addFavorite(url)
  즐겨찾기에서 제거    (isFavorite일 때)    → removeFavorite(url)
  ──────────
  Finder에서 보기                          → SystemActions.reveal(url)
  터미널에서 열기                          → SystemActions.openTerminal(at: url)
  ```
- `addFavorite`: 디렉터리만 허용, 중복 방지, 저장 후 **즐겨찾기 섹션만 재구축**(위치 트리 펼침 상태 보존 —
  `rebuildFavoritesSection()`).

---

## 7. 메뉴 막대 커맨드 전체 (App.swift `AppCommands`)

macOS 전역 메뉴 막대 → Windows에서는 **창 상단 Menu 컨트롤**로 이식. ⌘→Ctrl, ⌥→Alt, ⇧→Shift, ⌃→Ctrl(충돌 시 조정).
KeyboardMonitor(목록 탐색 키)가 처리한 키는 메뉴로 전달되지 않아 **이중 실행이 없다** — WPF에선
`PreviewKeyDown`에서 `e.Handled = true`로 같은 계약 유지.

### 앱 메뉴 대체 (XFinder ▸)
| 항목 | 단축키 | 동작 | Windows 제안 키 |
|---|---|---|---|
| XFinder 정보 | — | `sheet = .about` | (도움말 메뉴로 이동) |
| 설정… | ⌘, | `openSettings()` | Ctrl+, |

### 파일 (newItem 대체)
| 항목 | 단축키 | 동작 | Windows 제안 키 |
|---|---|---|---|
| 새 창 | ⌘N | 새 WindowGroup 인스턴스 | Ctrl+N |
| 새 탭 | ⌘T | `newTab()` | Ctrl+T |
| 새 폴더 | ⇧⌘N | `requestNewFolder()` | Ctrl+Shift+N |

### 편집 (pasteboard 대체 — 시스템 항목을 교체해 파일 목록 위에서도 단축키가 동작)
| 항목 | 단축키 | 동작 | Windows 제안 키 |
|---|---|---|---|
| 복사 | ⌘C | `copyShortcut()` (텍스트 필드 편집 중이면 그쪽으로 전달) | Ctrl+C |
| 경로 복사 | ⌥⌘C | `copySelectedPath()` | Ctrl+Shift+C |
| 잘라내기 | ⌘X | `cutShortcut()` | Ctrl+X |
| 붙여넣기 | ⌘V | `pasteShortcut()` | Ctrl+V |
| 복제 | ⌘D | `duplicate()` | Ctrl+D |
| 이름 변경 (F2) | (F2는 KeyboardMonitor) | `requestRename()` | F2 |
| 휴지통으로 이동 | ⌘⌫ | `requestDelete()` | Delete |

### 이동
| 항목 | 단축키 | 동작 | Windows 제안 키 |
|---|---|---|---|
| 뒤로 | ⌘[ | `goBack()` | Alt+Left |
| 앞으로 | ⌘] | `goForward()` | Alt+Right |
| 상위 폴더 | ⌘↑ | `goUp()` | Alt+Up |
| 선택 항목 열기 | ⌘↓ | `openSelected()` | Enter |
| ────── | | | |
| 폴더로 이동 | ⇧⌘G | `requestGoToFolder()` | Ctrl+Shift+G |
| Finder에서 보기 | — | `revealInFinder()` | ("탐색기에서 보기") |
| 터미널 열기 | — | `openTerminal()` | |

### 작업
| 항목 | 단축키 | 동작 | Windows 제안 키 |
|---|---|---|---|
| 기본 앱으로 열기 (F4) | (F4는 KeyboardMonitor) | `editSelected()` | F4 |
| 미리 보기 (Space) | (Space는 KeyboardMonitor) | `viewSelected()` | Space |
| ────── | | | |
| 압축 | — | `compressSelected()` | |
| 압축 풀기 | — | `extractSelected()` | |
| ────── | | | |
| 새로고침 | ⌘R | `refresh()` | F5 (+Ctrl+R) |
| 숨김 파일 표시/숨기기 | ⇧⌘. | `toggleHidden()` | Ctrl+H |
| 보기 전환 목록/아이콘 | ⌃M | `toggleViewMode()` | Ctrl+M |
| ────── | | | |
| 화면 모드 ▸ 시스템/라이트/다크 | — | `appearance = .system/.light/.dark` | |
| ────── | | | |
| 키보드 단축키 | — | `showHelp()` | |

### 도움말 (help 대체)
| 항목 | 단축키 | 동작 | Windows 제안 키 |
|---|---|---|---|
| XFinder 사용설명서 | ⌘/ | `sheet = .manual` | F1 |
| 키보드 단축키 | — | `showHelp()` | |

기타(메뉴 밖, doc/navigation.md): **마우스 측면 버튼 3/4 → 뒤로/앞으로** — Windows: `MouseDown`의
`XButton1`/`XButton2` 처리(탐색기 관례와 동일).

---

## 8. 설정 화면 (SettingsView — RootView.swift 내 정의)

### 8.1 창 형태
- **단독 창**(팝오버 아님): `SettingsWindowPresenter.show(app:)` — 한 번에 하나만, 이미 열려 있으면 앞으로.
  호출한 창의 `AppModel`을 캡처해 **그 모델을 계속 편집**(다중 창 주의).
- 크기: **minWidth 500 / ideal 520, minHeight 560 / ideal 620**.
- 구조: 상단 카테고리 탭 바 + Divider + 스크롤 콘텐츠(`VStack(spacing: 22)`, 패딩 가로 28 / 세로 24).
- 카테고리 탭 3개: **일반**(`gearshape`) / **보기**(`rectangle.grid.1x2`) / **AI**(`sparkles`).
  탭 버튼: 폭 74, 세로 패딩 9, 아이콘 19 + 라벨 11 medium(VStack spacing 4), 코너 9,
  선택 시 전경 accent + 배경 `accent @ 0.14`, 비선택 secondary.
  탭 바 패딩: 가로 16 / 위 16 / 아래 10, 버튼 간격 8, 중앙 정렬.
- 공통 컴포넌트: `sectionLabel`(아이콘+제목, 13 semibold, secondary), `hint`(11, secondary).

### 8.2 "일반" 탭
1. **화면 모드** (icon `circle.lefthalf.filled`): 세그먼트 — 시스템/라이트/다크.
   힌트: "시스템: macOS 설정을 따릅니다." (Windows: "시스템: Windows 설정을 따릅니다."로 교체)
2. **터미널 앱** (icon `terminal`): 세그먼트 — 자동/터미널/iTerm. 힌트(동적):
   - auto+iTerm 있음: "자동: iTerm이 설치되어 iTerm으로 엽니다." / auto+없음: "자동: iTerm이 없어 기본 터미널로 엽니다."
   - terminal: "기본 터미널로 엽니다."
   - iterm+있음: "iTerm으로 엽니다." / iterm+없음: "iTerm이 설치되어 있지 않아 기본 터미널로 엽니다."
   - Windows 재해석: 자동/PowerShell/Windows Terminal — wt.exe 존재 검사로 동일 패턴.
3. **기본 탭** (icon `rectangle.split.3x1`):
   - 저장 없음: 힌트 "저장된 기본 탭이 없습니다 — 시작할 때 '최근 항목' 하나로 시작합니다."
   - 저장 있음: 폴더명 나열(루트는 "Macintosh HD", `  ·  ` 구분, 폰트 12, 2줄 middle 말줄임) +
     힌트 "시작할 때 위 {n}개 폴더가 탭으로 자동으로 열립니다."
   - 버튼 "현재 열린 탭을 기본으로 저장" → `saveCurrentTabsAsDefault()`,
     툴팁 "지금 이 창에 열려 있는 탭들의 폴더를 저장합니다 ({tabs.count}개)"
   - 저장 있을 때만 버튼 "지우기"(destructive) → `clearDefaultTabs()`,
     툴팁 "저장된 기본 탭을 삭제하고 기본 동작(최근 항목)으로 되돌립니다"

### 8.3 "보기" 탭
1. **파일 목록 크기** (icon `textformat.size`): 오른쪽에 "{n}%" 표시(11 medium secondary).
   −버튼(`textformat.size.smaller`, 22×22, 툴팁 "5% 작게") + 슬라이더(**0.8~1.8, step 0.05**) +
   ＋버튼(`textformat.size.larger`, 툴팁 "5% 크게"). 버튼은 0.05 단위 반올림 후 경계로 클램프.
2. **날짜 표시** (icon `calendar.badge.clock`): 세그먼트 — 실제 날짜/상대 시간. 힌트(동적):
   - relative: "수정일·생성일을 '1분 전', '1시간 전', '1일 전' 형식으로 표시합니다. 마우스를 올리면 실제 날짜가 보입니다."
   - absolute: "수정일·생성일을 '2026-06-09 17:16' 형식으로 표시합니다."
3. **검색창 위치** (icon `magnifyingglass`): 세그먼트 — 툴바/툴바 아래. 힌트(동적):
   - toolbar: "검색창을 툴바 오른쪽에 표시합니다." / below: "검색창을 툴바 아래 전체 폭의 별도 줄로 표시합니다."
4. **폴더 용량 계산** (icon `sum`): 체크박스 "폴더 용량 계산 및 표시" (12pt).
   힌트: "끄면 파인더처럼 폴더 용량을 계산하지 않아 탐색이 빠릅니다(폴더는 -- 로 표시)."
5. **최근 항목 표시 종류** (icon `clock`): `FileSystemService.fileTypeOrder`의 카테고리별 체크박스(12pt).
   힌트: "선택한 종류만 최근 항목에 표시 (아무것도 선택 안 하면 전체)."

### 8.4 "AI" 탭
1. **AI 모델** (icon `sparkles`): 세그먼트 — 로컬 (Ollama)/Gemini.
   - Gemini 선택: SecureField "Gemini API 키" + TextField "모델 (예: gemini-2.5-flash)" (roundedBorder, 11pt).
     힌트: "키는 aistudio.google.com 에서 발급합니다. 정리 시 파일 이름이 구글로 전송됩니다."
   - Ollama 선택: 힌트 "서버 주소" + TextField placeholder "http://localhost:11434"(11pt monospace),
     힌트 "모델 이름" + TextField(placeholder = 기본 모델명, monospace), 버튼 "기본값"(작게 — 기본 URL/모델 복원).
     힌트: "로컬 Ollama 사용 — 입력한 모델이 없으면 자동 감지. 데이터가 기기 밖으로 나가지 않습니다."
2. **AI 정리 예외 폴더** (icon `hand.raised`): 오른쪽 "{n}개" 카운트.
   - 없음: 힌트 "등록된 예외 폴더가 없습니다. 폴더를 우클릭해 'AI 정리 예외 폴더로 등록'을 선택하세요."
   - 있음: 스크롤 목록(최대 높이 132, 행 간격 4) + 힌트 "등록된 폴더와 그 하위 폴더 전체에서 AI 정리가 동작하지 않습니다."
   - 행(excludedRow): `folder.fill`(11, blue) + 폴더명(11 medium)/경로(9, secondary, middle 말줄임) +
     `magnifyingglass`(10) 버튼 툴팁 "Finder에서 보기" + `xmark.circle.fill`(12) 버튼 툴팁 "예외 해제".
     행 패딩 가로 8/세로 5, 배경 코너 6 `primary @ 0.05`.

---

## 9. 확인 다이얼로그 (ConfirmDialog — 자체 구현 오버레이)

- 전체 화면 스크림: `black @ 0.28`, **스크림 클릭 = 취소**(`cancelConfirm()`). 페이드 전환.
- 패널: 폭 **320**, 패딩 20, `RoundedRectangle(16, continuous)` + `.regularMaterial`(반투명 —
  WPF: 테마 배경색 + 약간의 투명도), 테두리 `primary @ 0.1`, 그림자 `black @ 0.3` radius 24 / y 8.
- 내용 `VStack(spacing: 14)`: 제목(15 bold) + 메시지(12, secondary, 중앙 정렬, 여러 줄) +
  버튼 줄 `HStack(spacing: 10)`(위 패딩 2).
- 버튼 2개: index 0 = `request.confirmTitle`(파괴적이면 빨강), index 1 = **"취소"**.
  - 버튼 외형: 13 semibold, 균등 폭, 세로 패딩 8, 코너 9.
    파괴적: 빨강 채움(포커스 1.0 / 비포커스 0.75) + 흰 글자. 일반: `secondary @ 0.28` 채움 + primary 글자.
  - **키보드 포커스 표시**: `app.confirmFocus`(0/1) — 포커스된 버튼에 accent 스트로크 **3pt**.
  - ←→/↑↓/Tab으로 포커스 이동, Enter 실행(`executeConfirm(index:)`), Esc 취소 — KeyboardMonitor가 구동.
    Windows: 오버레이가 떠 있는 동안 창 `PreviewKeyDown`에서 같은 키 처리.

---

## 10. 시스템 상태 표시 (SystemStatsView — 툴바 왼쪽 끝)

- `HStack(spacing: 11 × scale)`에 지표 3개 — 각각 plain 버튼, 클릭 시 아래 화살표 팝오버:
  | 아이콘 | 색 | 값 | 툴팁 | 팝오버 |
  |---|---|---|---|---|
  | `cpu` | blue | `monitor.cpuUsage` | `CPU 사용량 — 클릭하면 추이·온도·부하 프로세스 보기` | CPUDetailView |
  | `memorychip` | purple | `monitor.memoryUsage` | `메모리 사용량 — 클릭하면 앱·캐시·스왑 상세 보기` | MemoryDetailView |
  | `internaldrive` | orange | `monitor.diskUsage` | `디스크 사용량 — 클릭하면 용량 분류·S.M.A.R.T. 상태 보기` | DiskDetailView |
- 지표 셀: 아이콘 12 × scale(지정 색) + 퍼센트 텍스트 11 × scale semibold **monospacedDigit**,
  간격 4 × scale, "100%"도 줄바꿈 없이 1줄 고정(레이아웃 흔들림 방지). 형식 `String(format: "%.0f%%", v*100)`.
- `SystemMonitor.shared` 싱글턴(모든 창 공유). 상세 팝오버 내용은 04 스펙(시스템 모니터) 담당.
- Windows: 팝오버 → `Popup`(StaysOpen=false, Placement=Bottom). 수치는 WMI/PerformanceCounter 베스트에포트.

---

## 11. 영속화 — UserDefaults 키 전체 (본 영역 관련)

모두 `UserDefaults.standard`. Windows 대응: `%APPDATA%\XFinder\settings.json` 단일 JSON 파일 권장(키 이름 유지).

| 키 | 형식 | 내용 / 기본값 |
|---|---|---|
| `XFinder.favorites.v1` | `[String]` (경로 배열) | 즐겨찾기. 미설정 시 기본 즐겨찾기(§6.2) |
| `XFinder.defaultTabs.v1` | `[String]` | 기본 탭 폴더들. 없으면 "최근 항목" 1탭으로 시작 |
| `XFinder.appearance.v1` | String (`system/light/dark`) | 화면 모드, 기본 system |
| `XFinder.terminalApp.v1` | String (`auto/terminal/iterm`) | 터미널 앱, 기본 auto (SystemActions.terminalPrefKey) |
| `XFinder.dateStyle.v1` | String (`absolute/relative`) | 날짜 표시, 기본 absolute |
| `XFinder.searchPosition.v1` | String (`toolbar/below`) | 검색창 위치, 기본 toolbar |
| `XFinder.listScale.v1` | Double | 목록 크기 배율, 기본 1.0 (범위 0.8~1.8) |
| `XFinder.columnWidths.v1` | `[String: Double]` (키: size/modified/created/kind) | 열 너비(배율 적용 **전** 포인트) |
| `XFinder.recentsCategories.v1` | `[String]` | 최근 항목 표시 카테고리(비면 전체) |
| `XFinder.calculateFolderSizes.v1` | Bool | 폴더 용량 계산, 기본 **false** |
| `XFinder.folderViewModes.v1` | `[String: String]` (경로→viewMode) | 폴더별 보기 모드 기억 |
| `XFinder.aiProvider.v1` | String (`ollama/gemini`) | 기본 **gemini** |
| `XFinder.geminiAPIKey.v1` | String | Gemini API 키 (⚠ 평문 — Windows에선 DPAPI 암호화 권장) |
| `XFinder.geminiModel.v1` | String | Gemini 모델명 |
| `XFinder.ollamaBaseURL.v1` | String | Ollama 서버 주소, 기본 `http://localhost:11434` |
| `XFinder.ollamaModel.v1` | String | Ollama 모델명 |
| `XFinder.aiExcludedFolders.v1` | `[String]` | AI 정리 예외 폴더 경로들 |

저장 패턴(★ 유지): `AppModel`에 `var x = loadX() { didSet { saveX() } }` — C#에서는 프로퍼티 setter에서
즉시 저장(또는 디바운스 저장). **별도 설정 저장소를 만들지 말 것**(원문 doc의 명시적 지침).

---

## 12. SF Symbol → Windows 글리프 매핑 제안 (Segoe Fluent Icons, 베스트에포트 — 빌드 시 확인 필요)

| SF Symbol | 용도 | Segoe Fluent Icons 제안 | 대안(이모지/직접 그리기) |
|---|---|---|---|
| chevron.backward / forward / up / right / down | 탐색·디스클로저 | E76B / E76C / E70E / E76C / E70D | ◀▶▲▸▾ |
| terminal | 터미널 | E756 (CommandPrompt) | |
| calendar | Dayflow | E787 (Calendar) | |
| sparkles | AI | E945 또는 ✨ 이모지 | ✨ |
| magnifyingglass | 검색 | E721 (Search) | |
| xmark / xmark.circle.fill | 닫기/지우기 | E711 (Cancel) / E711+원 배경 | ✕ |
| plus | 새 탭 | E710 (Add) | ＋ |
| square.grid.2x2 | 아이콘 보기/응용 프로그램 | E80A 또는 F0E2 (GridView) | ▦ |
| list.bullet | 목록 보기 | E8FD (BulletedList) | |
| square.grid.3x1.below.line.grid.1x2 | 그룹화 | F168 (GroupList) 확인, 없으면 E8FD | |
| folder.badge.plus | 새 폴더 | E8F4 (NewFolder) | |
| ellipsis.circle | 작업 메뉴 | E712 (More) | ⋯ |
| eye.fill / eye.slash | 숨김 토글 | E7B3 (RedEye) / ED1A(Hide, 확인) | 👁 |
| cpu / memorychip / internaldrive | 시스템 상태 | 적절 글리프 부재 — E950 계열 확인 | 🧠/💾 또는 Path 직접 그리기 |
| externaldrive | 외장 볼륨 | E88E (MapDrive) 확인 | |
| clock / clock.fill | 최근 항목 | E823 (Recent) | |
| house | 홈 | E80F (Home) | |
| doc | 문서 | E8A5 (Document) | |
| arrow.down.circle | 다운로드 | E896 (Download) | |
| photo | 사진 | E8B9 (Picture) | |
| film | 동영상 | E8B2 (Movies) | |
| music.note | 음악 | E8D6 (Audio) | |
| menubar.dock.rectangle | 데스크탑 | E7F4 (TVMonitor) | |
| folder / folder.fill | 폴더 | E8B7 (Folder) / E8D5 (FolderFill) | |
| circle.fill | 태그 점 | WPF `Ellipse`로 직접 그리기 | ● |
| pencil | 경로 편집 | E70F (Edit) | |
| arrow.right.circle.fill | 경로 이동 | E72A (Forward) | → |
| checkmark | 메뉴 체크 | E73E (CheckMark) | ✓ |
| gearshape | 설정-일반 | E713 (Settings) | |
| rectangle.grid.1x2 | 설정-보기 | E8A9 또는 E8FD 확인 | |
| circle.lefthalf.filled | 화면 모드 | — (Path 직접) | ◐ |
| rectangle.split.3x1 | 기본 탭 | F246 확인 | |
| textformat.size(.smaller/.larger) | 글자 크기 | E8D2 (FontSize) | 가⁻/가⁺ |
| calendar.badge.clock | 날짜 표시 | E787 | |
| sum | 폴더 용량 | E8EF (Calculator) | Σ |
| hand.raised | 예외 폴더 | E8F8 확인 | 🚫 |

색상 상수(macOS 시스템 색 → WPF 라이트/다크 제안):
`accentColor`→시스템 강조색(`SystemParameters.WindowGlassColor` 또는 `UISettings.GetColorValue(Accent)`),
`primary`→라이트 `#000000`/다크 `#FFFFFF`, `secondary`→라이트 `#00000099`(60%)/다크 `#FFFFFF99`,
`windowBackgroundColor`→라이트 `#ECECEC`/다크 `#323232`, `textBackgroundColor`→라이트 `#FFFFFF`/다크 `#1E1E1E`.

---

## 13. Windows 포팅 노트 (mac 전용 API별)

| mac API/개념 | Windows 대응 |
|---|---|
| NavigationSplitView + Liquid Glass 사이드바 | Grid+GridSplitter, 단색 패널(부분 블러 불가). 사이드바 토글 버튼 직접 추가 |
| unified 툴바(타이틀 바 통합) | WindowChrome 커스텀 타이틀 바. 드래그 영역 = 툴바 빈 공간(`WindowChrome.IsHitTestVisibleInChrome`) |
| NSWindow / WindowAccessor | 각 창의 `Window` 참조. 전역 NSEvent 모니터 → 창 `PreviewKeyDown`/`PreviewMouseDown` |
| KeyboardMonitor(전역 키 모니터) | 창 수준 PreviewKeyDown. `textInputActive`일 땐 가로채지 않는 계약 동일 유지 |
| `.help` 툴팁 | `ToolTip` (단축키 표기는 Ctrl식으로 교체) |
| `.popover(arrowEdge: .bottom)` | `Popup` Placement=Bottom (화살표 없음 — 생략 가능) |
| `.sheet` | 소유자 모달 Window 또는 루트 오버레이 |
| `.contextMenu` | `ContextMenu` |
| `NSEvent.doubleClickInterval` | `System.Windows.Forms.SystemInformation.DoubleClickTime` (ms) |
| `Finder에서 보기` (NSWorkspace reveal) | `explorer.exe /select,"경로"` — 문구 "탐색기에서 보기"로 교체 |
| 터미널/iTerm 열기 | `wt.exe -d "경로"` / `powershell -NoExit -Command Set-Location` — 설정 enum 재해석 |
| Dayflow 캘린더 열기 | 대응 앱 없음 — **제거 권장** 또는 `outlookcal:` URI/사용자 지정 명령으로 대체(스펙상 옵션) |
| AirDrop (SidebarItem.Kind.airDrop) | 직접 API 없음 — **포팅 제외** (현재 사이드바에 노출되지도 않음). 굳이 하려면 Windows 근거리 공유 안내만 |
| 휴지통(trashItem / Finder AppleScript 폴백) | `SHFileOperation`/`IFileOperation` + `FOF_ALLOWUNDO`. TCC 권한 폴백 로직은 불필요 — 삭제 |
| Finder 태그 (`com.apple.metadata:_kMDItemUserTags` xattr) | NTFS ADS(`파일:XFinder.Tags`) 또는 로컬 JSON DB. 표준 7색·이름·색번호 체계는 그대로 유지 |
| 최근 항목 (Spotlight) | `%APPDATA%\Microsoft\Windows\Recent` .lnk 해석 또는 자체 최근 기록. 카테고리 필터 동일 적용 |
| 마운트 볼륨 열거(mountedVolumeURLs) | `DriveInfo.GetDrives()` (IsReady만), 중복 제거 불필요. 루트 볼륨 = 시스템 드라이브 |
| "Macintosh HD" 루트 표기 | 드라이브 레이블 + 문자 (예: "로컬 디스크 (C:)") |
| `~` 경로 확장 | `%USERPROFILE%` 치환 + `Environment.ExpandEnvironmentVariables` |
| 한글 NFD 복원 ("가나") | Windows에선 보통 NFC라 무의미하지만 **mac에서 복사된 파일** 정리용으로 유지 가치 있음 — `string.Normalize(NormalizationForm.FormC)` + `File.Move` |
| SF Rounded 폰트 | 없음 — Segoe UI Variable로 대체(명시적 포기) |
| `.regularMaterial`(반투명 패널) | 단색 + 60~80% 불투명 근사 |
| 메뉴 막대(전역) | 창 내 Menu. FocusedValue 라우팅 불필요(창별 메뉴) |
| 마우스 버튼 3/4 | XButton1/XButton2 = 뒤로/앞으로 |
| 설정 단독 NSWindow(싱글턴) | static 참조로 1개 유지, `Activate()`로 앞으로. Owner 설정하지 않음(독립) — 단 AppModel은 연 창 것 캡처 |
| SMC 온도(CPU 팝오버) | WMI `MSAcpi_ThermalZoneTemperature` 베스트에포트(권한/하드웨어 따라 실패 허용) — 04 스펙 |

---

## 14. 동작 엣지 케이스 체크리스트 (구현·테스트용)

1. 탭 1개 → 탭 바 미표시. `⌘W`로 마지막 탭 닫기 = **창 닫기**.
2. 탭 닫은 뒤 남은 탭들의 색 불변(`colorIndex`는 탭에 저장).
3. 탭 전환은 재로딩 없음 — 스크롤/커서/선택 보존.
4. 비동기 목록 로드 완료 시점에 활성 탭이 바뀌었으면 **시작 시점에 캡처한 pane**에 반영 (detail 직접 참조 금지).
5. 검색창/경로 입력 포커스 중에는 type-select·단축키 가로채기 금지(`textInputActive`).
6. 그룹화 메뉴는 가상 모드(검색/최근/태그/파일 계산)에서 비활성. PathBar도 모드별 전용 바 표시.
7. 사이드바: 폴더 클릭은 이동만, 펼침은 디스클로저 클릭/더블클릭만. 더블클릭 간격은 시스템 값.
8. 즐겨찾기에 없는(삭제된) 폴더는 섹션 구축 시 걸러냄. 즐겨찾기 변경 시 위치 트리 펼침 상태 보존.
9. `/` 가리키는 행이 여러 개여도 선택 강조는 **항목(ID) 단위** 1개만.
10. 사이드바 선택 강조: 포커스 패널일 때만 accent 풀컬러, 아니면 회색 — Tab 키로 패널 전환 시 즉시 반영.
11. AI 버튼: 보호 위치(시스템/응용 프로그램/예외 폴더 하위 포함)에서 비활성 + 전용 안내 툴팁/오류문.
12. 확인 다이얼로그가 떠 있는 동안 스크림 클릭=취소, 방향키/Tab=포커스 이동, Enter=실행, Esc=취소.
13. 기본 탭 복원 시 존재하지 않는 폴더 제외, 첫 탭 활성화. 복원할 것이 없으면 "최근 항목" 1탭.
14. listScale 변경 → 툴바 아이콘·검색창·사이드바·목록 글꼴이 **즉시 동기 확대/축소**.
15. 검색창 위치 below↔toolbar 전환 시 즉시 레이아웃 재배치(별도 줄 ↔ 툴바 우측).

---

## 15. UI 문자열 총람 (한국어 원문 — 포팅 시 그대로 사용)

창/공통: `Macintosh HD`(→드라이브 레이블), `오류`, `XFinder`, `확인`, `취소`
툴바 툴팁: `뒤로 — 이전 폴더로 이동 (⌘[)`, `앞으로 — 다음 폴더로 이동 (⌘])`, `상위 폴더로 이동 (⌘↑)`,
`현재 폴더를 터미널에서 열기`, `Dayflow 캘린더 열기`,
`한글 자소 분리(NFD) 파일명 복원 — 클릭: 이 폴더 / 우클릭: 하위 폴더까지`,
`응용 프로그램 폴더는 AI 파일 정리에서 제외됩니다`, `시스템 폴더는 AI 파일 정리에서 제외됩니다`,
`AI 파일 정리 — 프롬프트로 현재 폴더 파일 정리 (로컬 LLM)`,
`아이콘 보기로 전환 (⌃M)`, `목록 보기로 전환 (⌃M)`,
`다음으로 그룹화 — 이름·종류·크기·수정일·생성일 구간으로 묶어 보기`,
`현재 위치에 새 폴더 만들기 (⇧⌘N)`, `작업 — 열기·복사·이동·압축·이름 변경 등`,
`숨김 파일 숨기기 (⇧⌘.)`, `숨김 파일 표시 (⇧⌘.)`,
`이름으로 검색 — 현재 폴더(하위 폴더 포함)에서 걸러냅니다`, `검색어 지우기`, `하위 폴더까지 검색`
가나 버튼 메뉴: `이 폴더의 한글 파일명 복원`, `하위 폴더까지 복원`
작업 메뉴: `열기`, `미리 보기`, `복사`, `잘라내기`, `붙여넣기`, `복제`, `이름 변경…`, `압축`, `압축 풀기`,
`Finder에서 보기`, `터미널 열기`, `한글 파일명 복원`, `이 폴더`, `하위 폴더까지`, `AI 파일 정리…`, `설정…`, `휴지통으로 이동`
그룹화: `없음`, `이름`, `종류`, `크기`, `수정일`, `생성일`
시스템 상태 툴팁: `CPU 사용량 — 클릭하면 추이·온도·부하 프로세스 보기`,
`메모리 사용량 — 클릭하면 앱·캐시·스왑 상세 보기`, `디스크 사용량 — 클릭하면 용량 분류·S.M.A.R.T. 상태 보기`
경로 막대: `최근 항목`, `경로 입력 (예: ~/Downloads)`, `이동`, `경로 직접 입력`,
`파일 계산 — {name} (크기순 · {n}개 로드됨 / 전체 {m}개)`, `파일 계산 — {name} ({m}개)`
탭: `새 탭 (⌘T)`, `탭 닫기 (⌘W)`, `새 탭`, `탭 닫기`, `다른 탭 닫기`
사이드바: `즐겨찾기`, `위치`, `태그`, `최근 항목`, `응용 프로그램`, `데스크탑`, `문서`, `다운로드`, `사진`,
`동영상`, `음악`, `공용`, `라이브러리`, `즐겨찾기에 추가`, `즐겨찾기에서 제거`, `Finder에서 보기`, `터미널에서 열기`
태그 이름: `빨간색`, `주황색`, `노란색`, `초록색`, `파란색`, `보라색`, `회색`
메뉴 막대: `XFinder 정보`, `설정…`, `새 창`, `새 탭`, `새 폴더`, `복사`, `경로 복사`, `잘라내기`, `붙여넣기`,
`복제`, `이름 변경 (F2)`, `휴지통으로 이동`, `이동`, `뒤로`, `앞으로`, `상위 폴더`, `선택 항목 열기`, `폴더로 이동`,
`작업`, `기본 앱으로 열기 (F4)`, `미리 보기 (Space)`, `새로고침`, `숨김 파일 표시/숨기기`, `보기 전환 목록/아이콘`,
`화면 모드`, `시스템`, `라이트`, `다크`, `키보드 단축키`, `XFinder 사용설명서`
설정: `일반`, `보기`, `AI`, `화면 모드`, `시스템: macOS 설정을 따릅니다.`, `터미널 앱`, `자동`, `터미널`, `iTerm`,
`자동: iTerm이 설치되어 iTerm으로 엽니다.`, `자동: iTerm이 없어 기본 터미널로 엽니다.`, `기본 터미널로 엽니다.`,
`iTerm으로 엽니다.`, `iTerm이 설치되어 있지 않아 기본 터미널로 엽니다.`, `기본 탭`,
`저장된 기본 탭이 없습니다 — 시작할 때 '최근 항목' 하나로 시작합니다.`,
`시작할 때 위 {n}개 폴더가 탭으로 자동으로 열립니다.`, `현재 열린 탭을 기본으로 저장`,
`지금 이 창에 열려 있는 탭들의 폴더를 저장합니다 ({n}개)`, `지우기`,
`저장된 기본 탭을 삭제하고 기본 동작(최근 항목)으로 되돌립니다`, `파일 목록 크기`, `5% 작게`, `5% 크게`,
`날짜 표시`, `실제 날짜`, `상대 시간`,
`수정일·생성일을 '1분 전', '1시간 전', '1일 전' 형식으로 표시합니다. 마우스를 올리면 실제 날짜가 보입니다.`,
`수정일·생성일을 '2026-06-09 17:16' 형식으로 표시합니다.`, `검색창 위치`, `툴바`, `툴바 아래`,
`검색창을 툴바 오른쪽에 표시합니다.`, `검색창을 툴바 아래 전체 폭의 별도 줄로 표시합니다.`,
`폴더 용량 계산`, `폴더 용량 계산 및 표시`,
`끄면 파인더처럼 폴더 용량을 계산하지 않아 탐색이 빠릅니다(폴더는 -- 로 표시).`,
`최근 항목 표시 종류`, `선택한 종류만 최근 항목에 표시 (아무것도 선택 안 하면 전체).`,
`AI 모델`, `로컬 (Ollama)`, `Gemini`, `Gemini API 키`, `모델 (예: gemini-2.5-flash)`,
`키는 aistudio.google.com 에서 발급합니다. 정리 시 파일 이름이 구글로 전송됩니다.`, `서버 주소`, `모델 이름`,
`기본값`, `로컬 Ollama 사용 — 입력한 모델이 없으면 자동 감지. 데이터가 기기 밖으로 나가지 않습니다.`,
`AI 정리 예외 폴더`, `{n}개`, `등록된 예외 폴더가 없습니다. 폴더를 우클릭해 'AI 정리 예외 폴더로 등록'을 선택하세요.`,
`등록된 폴더와 그 하위 폴더 전체에서 AI 정리가 동작하지 않습니다.`, `예외 해제`
(단축키 표기 `⌘[` 등은 Windows 포팅 시 `Ctrl`식 표기로 일괄 치환할 것.)
