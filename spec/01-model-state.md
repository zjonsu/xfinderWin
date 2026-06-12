# 01 — 모델·상태 (AppModel / PaneTab / FileItem / SidebarItem / Enums) 포팅 스펙

> 소스: `Sources/XFinder/Model/AppModel.swift` (~2036줄), `PaneTab.swift`, `FileItem.swift`,
> `SidebarItem.swift`, `Enums.swift` + `doc/navigation.md`, `doc/virtual-lists.md`
>
> 대상: C# .NET 8 WPF. 식별자는 영어 그대로 유지. `@Observable` →
> `CommunityToolkit.Mvvm`의 `ObservableObject`(또는 `INotifyPropertyChanged` 수동 구현),
> Swift `Task` → `async/await` + `CancellationTokenSource`.
>
> **중요한 전역 규칙**
> - macOS의 URL/경로 비교는 대소문자 구분이지만 Windows(NTFS)는 비구분이다.
>   경로를 키로 쓰는 모든 자료구조(`selection`, `folderViewModes`, `folderSizeCache`,
>   즐겨찾기/예외 폴더 비교 등)는 **정규화된 절대 경로 문자열 + `StringComparer.OrdinalIgnoreCase`** 로 통일할 것.
> - Swift의 `url.standardizedFileURL.path` ≒ `Path.GetFullPath(p).TrimEnd('\\')` (루트 제외).
> - 창(Window)마다 `AppModel` 인스턴스가 **하나씩 따로** 있다. 메뉴/단축키는 포커스된 창의 모델로 라우팅.

---

## 1. 보조 타입 (AppModel.swift 상단)

### 1.1 `OperationProgress` — 복사/이동/압축 진행 상태 (Observable class)

| 프로퍼티 | 타입 | 초기값 | 설명 |
|---|---|---|---|
| `title` | `string` | 생성자 인자 | "복사 중…", "이동 중…", "압축 중…", "압축 푸는 중…" |
| `currentFile` | `string` | `""` | 현재 처리 중 파일명 |
| `completedUnits` | `long` | `0` | 완료 단위(바이트 또는 항목 수) |
| `totalUnits` | `long` | `0` | 전체 단위 |
| `isCancelled` | `bool` | `false` | 사용자가 취소 버튼을 누르면 true — 작업 루프가 폴링해 중단 |
| `fraction` (계산) | `double` | — | `totalUnits <= 0 ? 0 : min(1, completed/total)` |

### 1.2 `AppSheet` — 시트(모달) 라우팅 (C#: 추상 record 또는 enum + payload)

케이스 전부 (id 문자열은 SwiftUI 시트 identity용 — WPF에서는 단일 `ActiveSheet` 상태 + 오버레이/다이얼로그로 구현):

| 케이스 | payload | id 문자열 |
|---|---|---|
| `viewer(FileItem)` | 미리보기 대상 | `"viewer:{path}"` |
| `goToFolder` | — | `"goto"` |
| `newFolder` | — | `"newfolder"` |
| `rename(FileItem)` | 대상 | `"rename:{path}"` |
| `progress(OperationProgress)` | 진행 객체 | `"progress"` |
| `about` | — | `"about"` |
| `manual` | — | `"manual"` |
| `uninstall(FileItem)` | 앱 번들 | `"uninstall:{path}"` |
| `aiOrganize` | — | `"aiOrganize"` |

C# 제안:
```csharp
public abstract record AppSheet {
    public sealed record Viewer(FileItem Item) : AppSheet;
    public sealed record GoToFolder() : AppSheet;
    public sealed record NewFolder() : AppSheet;
    public sealed record Rename(FileItem Item) : AppSheet;
    public sealed record Progress(OperationProgress Op) : AppSheet;
    public sealed record About() : AppSheet;
    public sealed record Manual() : AppSheet;
    public sealed record Uninstall(FileItem Item) : AppSheet;
    public sealed record AiOrganize() : AppSheet;
}
```

### 1.3 `ConfirmRequest` — 확인 대화상자

| 필드 | 타입 | 설명 |
|---|---|---|
| `id` | `Guid` | 새 요청마다 새 값 |
| `title` | `string` | 제목 |
| `message` | `string` | 본문 (여러 줄 가능) |
| `confirmTitle` | `string` | 실행 버튼 라벨 (예: "휴지통으로 이동", "복원") |
| `isDestructive` | `bool` | true면 실행 버튼 빨간색 |
| `action` | `Action` | 확인 시 실행할 클로저 |

### 1.4 `Clipboard` (앱 내부 잘라내기/복사 상태)

| 필드 | 타입 | 설명 |
|---|---|---|
| `urls` | `[URL]` → `List<string>` | 대상 경로들 |
| `isCut` | `bool` | true = 잘라내기(붙여넣기 시 이동) |

### 1.5 `FocusPane` — `enum { sidebar, detail }` (Tab 키로 토글)

### 1.6 설정 enum 4종 (rawValue가 UserDefaults 저장값)

| enum | 케이스(rawValue) | label(한국어, 그대로 사용) | 기본값 |
|---|---|---|---|
| `TerminalApp` | `auto`, `terminal`, `iterm` | "자동" / "터미널" / "iTerm" | `auto` |
| `AppearanceMode` | `system`, `light`, `dark` | "시스템" / "라이트" / "다크" | `system` |
| `DateDisplayStyle` | `absolute`, `relative` | "실제 날짜" / "상대 시간" | `absolute` |
| `SearchBarPosition` | `toolbar`, `below` | "툴바" / "툴바 아래" | `toolbar` |

Windows 노트: `TerminalApp`은 의미를 바꿔 `auto`(wt.exe 있으면 Windows Terminal, 없으면 PowerShell) /
`terminal`→`wt`(Windows Terminal) / `iterm`→`powershell`(또는 `cmd`)로 재정의 권장. 라벨도
"자동" / "Windows Terminal" / "PowerShell" 등으로 교체. 저장 키는 그대로 유지 가능.

---

## 2. `Enums.swift`

### 2.1 `PaneSide` — `{ left, right }`, `other` 계산 프로퍼티. (현재 코드에서는 사이드바/상세 구분 외 거의 사용 안 함 — 보존.)

### 2.2 `ViewMode` — `{ full, icon }` (rawValue `"full"`/`"icon"`)
- label: full = "목록", icon = "아이콘". 폴더별로 기억·영속화됨(§7.3).

### 2.3 `GroupKey` — "다음으로 그룹화" 기준
`{ none, name, kind, size, modified, created }` (rawValue = 케이스명)
label: "없음" / "이름" / "종류" / "크기" / "수정일" / "생성일".

### 2.4 `ListColumn` — 목록 보기 고정폭 열 (이름 열은 나머지 폭 전부라 제외)
`{ size, modified, created, kind }`. rawValue가 열 너비 저장 딕셔너리 키.

| 열 | defaultWidth(pt) |
|---|---|
| `size` | 70 |
| `modified` | 120 |
| `created` | 120 |
| `kind` | 96 |

공통: `minWidth = 44`, `maxWidth = 360`. (배율 `listScale`은 표시 시 곱함 — 저장값은 배율 적용 전.)

### 2.5 `SortKey` — `{ name, ext, size, modified, created, kind }`
label (영문 그대로): "Name" / "Ext" / "Size" / "Date Modified" / "Date Created" / "Kind".

### 2.6 정렬 규칙 (`Array<FileItem>.sorted(by:ascending:)`) — **그대로 복제할 것**

비교 함수 `less(a, b)`:
1. `a.isParent != b.isParent` → `".."`가 항상 먼저 (**방향 무관, 조기 반환**).
2. `a.isDirectory != b.isDirectory` → 폴더가 항상 먼저 (**방향 무관, 조기 반환**).
3. 키별 비교 후 `ascending ? result : !result`:
   - `name`: `localizedStandardCompare`(자연어 정렬 — 숫자 인식, 대소문자 무시).
     Windows 대응: `StrCmpLogicalW`(shlwapi P/Invoke — 탐색기와 동일한 자연 정렬).
   - `ext`: 확장자 비교, 같으면 이름 비교.
   - `size`: `a.size < b.size`, 같으면 이름.
   - `modified` / `created`: 날짜 오름차순, 같으면 이름.
   - `kind`: `Format.kindLabel` 문자열 비교, 같으면 이름.
- 주의: Swift 코드는 내림차순 시 **타이브레이커까지 통째로 반전**한다(동률일 때 결과가 뒤집힘).
  C#에서는 `IComparer<FileItem>`로 (1)(2)를 방향과 무관하게 처리하고, (3)의 결과 부호만 반전하면
  실질 동작 동일 (동률 항목의 상대 순서 차이는 무시 가능).

---

## 3. `FileItem.swift`

### 3.1 `FileItem` (struct → C# class 또는 record; 목록 셀의 단위)

| 필드 | 타입 | 기본값 | 설명 |
|---|---|---|---|
| `url` | `URL` → `string`(절대경로) | — | identity (`id == url`) |
| `name` | `string` | — | 표시 이름(마지막 경로 요소) |
| `isDirectory` | `bool` | — | |
| `isSymlink` | `bool` | — | Windows: reparse point(symlink/junction) |
| `isHidden` | `bool` | — | Windows: Hidden 속성 또는 `.`으로 시작 |
| `size` | `long` | — | 바이트. **-1 = 미계산/알 수 없음** (폴더는 측정 전 -1) |
| `modified` | `DateTime` | — | 수정일. 없으면 `distantPast` 센티널(C#: `DateTime.MinValue`) |
| `ext` | `string` | — | 소문자 확장자, 점 없음. 없거나 폴더면 `""` |
| `isParent` | `bool` | — | 합성 ".." 행 (현재 Finder식 목록에서는 표시하지 않지만 로직 전반의 가드로 남아있음 — 유지) |
| `created` | `DateTime` | `distantPast` | 생성일 |
| `typeName` | `string` | `""` | 현지화된 종류 설명(예: "PDF 문서"); 비면 `Format.kind`로 대체 |

계산 프로퍼티 `isBundle`: `isDirectory && ext != "" && ext ∈ {"app","bundle","framework","rtfd","playground"}`.
→ **Windows에는 번들 개념이 없음.** `isBundle`은 항상 `false`를 반환하도록 유지하되 프로퍼티 자체는 남겨
   분기 코드를 그대로 포팅(`.app` 폴더를 복사해 온 경우에도 일반 폴더로 취급해 무방).

정적 팩토리 `FileItem.parent(of: directory)`: 부모 폴더를 가리키는 ".." 행
(`name=".."`, `isDirectory=true`, `size=-1`, `modified=distantPast`, `isParent=true`).

### 3.2 `Format` — 표시 포맷 규칙 (정적 헬퍼)

- `size(bytes, isDirectory)`:
  - `bytes < 0` → `"--"`.
  - 그 외 `ByteCountFormatter(.file)` — **1000 진법**(KB=1000B), 숫자 표기 강제
    (`allowsNonnumericFormatting=false` → "Zero KB" 같은 표기 금지, 0도 숫자로).
  - Windows 구현: 자체 포매터로 1000 진법 유지 권장(예: `1.2 MB`). 탐색기식 1024 진법(KiB 의미의 KB)으로
    바꾸려면 일관되게 전체 적용할 것.
- `date(d)`: `distantPast` → `""`; 그 외 형식 **`yyyy-MM-dd HH:mm`** (고정, 로캘 무관).
- `relativeDate(d)`: `distantPast` → `""`; 미래 시각(`interval < 0`) → 절대 날짜로 폴백.
  초 단위 `s = floor(interval)`:
  - `s < 60` → `"방금 전"`
  - 분 `m < 60` → `"{m}분 전"`
  - 시 `h < 24` → `"{h}시간 전"`
  - 일 `d < 7` → `"{d}일 전"`
  - `d < 30` → `"{d/7}주 전"`
  - `d < 365` → `"{d/30}개월 전"`
  - 그 외 → `"{d/365}년 전"` (모두 정수 나눗셈)
- `date(d, style)`: `style == relative ? relativeDate : date`.
- `kind(item)`: `isParent → ""`; `isSymlink → "Alias"`; `isBundle → ext.uppercased()`;
  `isDirectory → "Folder"`; `ext == "" → "File"`; 그 외 `ext` 대문자 (예: "PDF").
  Windows: "Alias" 대신 "바로 가기"로 바꿔도 되지만 원문 유지 권장(정렬·그룹 키로 쓰임).
- `kindLabel(item)`: `isParent → ""`; `typeName`이 있으면 그것, 없으면 `kind(item)`.
  Windows에서 `typeName` 채우기: `SHGetFileInfo(SHGFI_TYPENAME)`로 탐색기 종류 문자열
  (예: "PDF 파일") 조회 — `FileSystemService.list` 단계에서.

---

## 4. `PaneTab.swift` — 탭 1개의 전체 상태

### 4.1 `FileGroup` (struct)
- `title: string`, `range: Range<Int>` (C#: `(int Start, int End)` exclusive upper), `id == title`.
- 항목을 복사하지 않고 `items` 안의 **구간 인덱스**만 가리킨다 — 수만 개 목록 메모리 절약. 유지할 것.

### 4.2 `PaneTab` (Observable class, `id = Guid`)

| 프로퍼티 | 타입 | 초기값 | 설명 |
|---|---|---|---|
| `directory` | `URL` → `string` | 생성자 | 탭이 보는 폴더 |
| `history` | `List<string>` | `[]` | 탭별 뒤로/앞으로 히스토리 |
| `historyIndex` | `int` | `-1` | |
| `colorIndex` | `int` | `0` | 탭 바 파스텔 팔레트 인덱스 — **탭 자신에 저장**(배열 위치 아님) → 탭을 닫아도 남은 탭 색 불변 |
| `rawItems` | `List<FileItem>` | `[]` | 필터 전 원본 목록(".." 제외) |
| `items` | `List<FileItem>` | `[]` | 실제 표시 목록(필터+정렬+그룹 재배열) |
| `selection` | `HashSet<string>` (OrdinalIgnoreCase) | `∅` | 다중 선택(마크) |
| `cursor` | `string?` | `null` | 키보드 커서 행 |
| `selectionAnchor` | `string?` | `null` | Shift 범위 선택의 앵커 |
| `sortKey` | `SortKey` | `name` | |
| `sortAscending` | `bool` | `true` | |
| `filter` | `string` | `""` | 검색어(툴바 검색창과 바인딩) |
| `viewMode` | `ViewMode` | `full` | |
| `groupKey` | `GroupKey` | `none` | 일반 폴더 목록에서만 적용 |
| `groups` | `List<FileGroup>` | `[]` | `groupKey != none`일 때 구간들 |
| `loadError` | `string?` | `null` | 폴더 읽기 실패 메시지 |
| `searchMode` | `bool` | `false` | 가상 모드: 재귀 검색 결과 |
| `recentsMode` | `bool` | `false` | 가상 모드: 최근 항목 |
| `tagMode` / `tagName` | `bool` / `string?` | `false`/`null` | 가상 모드: 태그 필터 |
| `typeMode` / `typeName` / `typeTotal` | `bool` / `string?` / `int` | `false`/`null`/`0` | 가상 모드: 디스크 팝업 "파일 계산" 카테고리 내역. `typeTotal` = 카테고리 전체 파일 수(표시 중 개수보다 클 수 있음 — 상위 N개만 페이징 표시) |

계산 프로퍼티:
- `title`: `directory == "/"` → `"/"`; 아니면 `lastPathComponent`(비면 전체 경로).
  Windows: 드라이브 루트 `C:\` → `"C:"` 등으로 표시.
- `tabTitle` (탭 바·창 제목): 우선순위대로
  - `recentsMode` → `"최근 항목"`
  - `tagMode && tagName != null` → `tagName`
  - `typeMode && typeName != null` → `"파일 계산 — {typeName}"`
  - `searchMode` → `"검색: {filter}"`
  - 그 외 → `title`
- `isAtRoot`: `directory.path == "/"` (Windows: 드라이브 루트 여부).
- `activeGroups`: 다음 조건을 **모두** 만족할 때만 `groups` 반환, 아니면 `null`:
  `groupKey != none`, `groups`가 비지 않음, 4개 가상 모드 모두 false,
  `groups.last.range.upperBound == items.count` (모드 전환 직후 낡은 구간으로 인한 인덱스 범위 오류 방지 — 반드시 유지).

### 4.3 `rebuild(showHidden)` — 표시 목록 재계산
1. **가상 모드(search/recents/tag/type) 중이면 아무것도 하지 않음** — `items`는 AppModel이 직접 관리.
2. `visible = rawItems` 복사; `!showHidden`이면 `isHidden` 제거.
3. `filter`를 trim + 소문자 → 비어 있지 않으면 `name.lowercased().contains(needle)` 필터
   (가상 검색이 아닌 **현재 목록 내 부분 일치 필터** — 단, 실제 앱 흐름에선 updateSearch가 재귀 검색으로 전환하므로
   이 분기는 모드 전환 타이밍 보호용).
4. `sorted(by: sortKey, ascending:)` 적용.
5. `items = visible` (".."행 없음 — Finder식), `rebuildGroups()` 호출.
6. 커서 유효성: 커서가 목록에 없으면 첫 항목으로; `null`이면 첫 항목.
7. `selection`을 현재 `items`에 존재하는 url로 교집합.

### 4.4 `rebuildGroups()` — 그룹화
- `groupKey == none || items.isEmpty` → `groups = []`.
- 각 항목을 `bucket(groupKey, item)` → `(order, title)`로 분류; 같은 title끼리 버킷에 모음
  (버킷 내부는 기존 정렬 순서 유지).
- title 정렬: `order` 오름차순, 동률이면 title의 자연 정렬(`localizedStandardCompare`).
- `items`를 그룹 순서대로 평탄화해 재배열, `groups`에 `(title, range)` 기록.

`bucket` 규칙 (order 작을수록 위):
| key | 규칙 |
|---|---|
| `name` | 첫 글자(대문자화; 한글 음절 그대로). 빈 이름 → `(1, "#")`, 그 외 `(0, 첫글자.uppercased())` |
| `kind` | 폴더(번들 제외) → `(0, "폴더")`; 그 외 `(1, Format.kindLabel)` |
| `size` | 폴더(번들 제외) → `(0, "폴더")`; `>= 1 GiB(1<<30)` → `(1, "1 GB 이상")`; `>= 100 MiB(100<<20)` → `(2, "100 MB ~ 1 GB")`; `>= 1 MiB(1<<20)` → `(3, "1 MB ~ 100 MB")`; 그 외 `(4, "1 MB 미만")` |
| `modified`/`created` | `dateBucket` |

`dateBucket(date)`:
- `distantPast` → `(100000, "날짜 없음")`
- 오늘 → `(0, "오늘")`, 어제 → `(1, "어제")`
- 경과 `< 7일(7*86400초)` → `(2, "지난 7일")`, `< 30일` → `(3, "지난 30일")`
- 그 외 연도별 → `(10000 - year, "{year}년")` (최근 연도가 위)

### 4.5 `actionTargets()` — 작업 대상 결정 (모든 파일 작업의 공통 진입)
- 마크된 항목(selection ∩ items, `isParent` 제외)이 있으면 그것들.
- 없으면 커서 행 1개(역시 `isParent` 제외).
- 둘 다 없으면 `[]`.

### 4.6 `toggleMark(url)` — selection에 있으면 제거, 없으면 추가.

---

## 5. `SidebarItem.swift`

### 5.1 `SidebarItem.Kind`
`{ folder, computer, airDrop, trash, recents, tag }`
- `folder`: 실제 디렉터리 — 선택+트리 펼침 가능.
- `computer`: "/" 선택 + 볼륨 나열 (현재 빌드에선 표시 안 함 — §6.2 참고).
- `airDrop`: 동작 없음(activate 시 no-op). **Windows: 의미 없음 — 케이스만 유지하거나 "근거리 공유"로 대체 가능(권장: 제거하되 enum 케이스는 유지).**
- `trash`: 휴지통 폴더. Windows: 휴지통 셸 폴더(`shell:RecycleBinFolder`)는 일반 경로가 아니므로 별도 처리(§10).
- `recents`: 최근 항목(가상 목록).
- `tag`: 색상 태그 — 클릭 시 태그 필터 목록.

### 5.2 `SidebarItem` (Observable class)

| 프로퍼티 | 타입 | 설명 |
|---|---|---|
| `id` | `Guid` | identity (같은 URL의 행 2개가 동시에 강조되지 않게 **id 단위 강조**) |
| `title` | `string` | 표시명 |
| `icon` | `string` | SF Symbol 이름 (→ §9.2 매핑) |
| `url` | `string?` | 대상 폴더 (airDrop/recents/tag는 null) |
| `kind` | `Kind` | |
| `depth` | `int` | 트리 들여쓰기 레벨 (기본 0, 자식은 +1) |
| `isExpanded` | `bool` | 기본 false |
| `children` | `List<SidebarItem>?` | **null = 아직 로드 안 함** (lazy) |
| `hasCheckedChildren` | `bool` | (현재 코드에서 미사용 플래그 — 보존 불필요) |
| `mayHaveChildren` | `bool` | 디스클로저 삼각형 표시 여부 (생성자 `hasChildren` 인자, 기본 true) |

계산: `isSelectable = url != null`; `canExpand = (kind ∈ {folder, computer}) && mayHaveChildren`.

`loadChildren(showHidden)`:
- `url == null || children != null` → 반환 (1회 lazy 로드).
- `kind == computer`면 `/Volumes`의 하위 폴더, 아니면 자기 url의 하위 폴더를 나열
  (Windows: computer 노드 = `DriveInfo.GetDrives()`의 Ready 드라이브 목록).
- 자식마다 `hasSubfolders` 검사 → 있으면 icon `"folder.fill"`, 없으면 `"folder"`,
  `hasChildren: hasKids`, `depth: depth+1`.

### 5.3 `SidebarSection` — `{ id: Guid, title: string, items: List<SidebarItem> }`.
섹션 제목(고정 문자열): **"즐겨찾기"**, **"위치"**, **"태그"**.

---

## 6. `AppModel` — 루트 상태

### 6.1 프로퍼티 전체 목록

| 프로퍼티 | 타입 | 초기값 | 비고 |
|---|---|---|---|
| `sections` | `List<SidebarSection>` | `[]` (bootstrap에서 구성) | |
| `tabs` | `List<PaneTab>` | 홈 폴더 탭 1개 | **항상 1개 이상 유지** |
| `activeTabIndex` | `int` | `0` | |
| `detail` (계산) | `PaneTab` | — | `tabs[clamp(activeTabIndex, 0, count-1)]` — **활성 탭 포워딩. 저장 프로퍼티로 만들지 말 것** |
| `selectedFolder` (계산) | `string` | — | `detail.directory` get/set 포워딩 |
| `showHidden` | `bool` | `false` | 숨김 파일 표시 (비영속 — 실행마다 false) |
| `statusFreeSpace` | `string?` | `null` | 상태바 여유공간 캐시 (백그라운드 계산) |
| `draggingFavorite` | `string?` (관찰 제외) | `null` | 즐겨찾기 드래그 중 URL |
| `history` (계산) | `List<string>` | — | `detail.history` 포워딩 |
| `historyIndex` (계산) | `int` | — | `detail.historyIndex` 포워딩 |
| `clipboard` | `Clipboard?` | `null` | |
| `pasteboardChangeCount` (private) | `int` | `-1` | 우리가 시스템 클립보드에 쓴 시점의 changeCount (§6.12) |
| `window` (weak, 관찰 제외) | 창 참조 | — | 키 모니터가 다른 창 이벤트를 무시하도록 |
| `textInputActive` (관찰 제외) | `bool` | `false` | 텍스트 필드 포커스 중 — 키 모니터의 type-select 등 전부 정지 |
| `favoritePaths` | `List<string>` | 영속 로드 | §7.1 |
| `excludedPaths` | `List<string>` | 영속 로드 | AI 정리 예외 폴더 (§7.2) |
| `folderViewModes` | `Dictionary<string, ViewMode>` | 영속 로드 | 폴더별 보기 모드 (§7.3) |
| `appearance` | `AppearanceMode` | 영속 로드 | didSet: 즉시 적용 + 저장 |
| `terminalApp` | `TerminalApp` | 영속 로드 | didSet: 저장 |
| `dateStyle` | `DateDisplayStyle` | 영속 로드 | didSet: 저장 |
| `searchPosition` | `SearchBarPosition` | 영속 로드 | didSet: 저장 |
| `listScale` | `double` | 영속 로드 (0.8~1.8 클램프, 기본 1.0) | didSet: 저장 |
| `columnWidths` | `Dictionary<string,double>` | 영속 로드 | didSet: 저장 |
| `recentsCategories` | `HashSet<string>` | 영속 로드 (기본 `{"문서","이미지"}`) | didSet: 저장 + `recentsMode`면 `showRecents()` 재실행. **빈 집합 = 전체 표시**이며 빈 집합도 저장됨 |
| `calculateFolderSizes` | `bool` | 영속 로드 (기본 false) | didSet: 저장 + 켜면 `computeFolderSizes()`, 끄면 `sizeTask` 취소 |
| `aiProvider` | `AIProvider` (`ollama`/`gemini`, 라벨 "로컬 (Ollama)"/"Gemini") | 영속 (기본 `gemini`) | didSet: 저장 |
| `geminiAPIKey` | `string` | 영속 (기본 `""`) | didSet: 저장. **소스에 키를 두지 않음** |
| `geminiModel` | `string` | 영속 (빈 값이면 `"gemini-flash-latest"`) | didSet: 저장 |
| `ollamaBaseURL` | `string` | 영속 (빈 값이면 `"http://localhost:11434"`) | didSet: 저장 |
| `ollamaModel` | `string` | 영속 (빈 값이면 `"gemma4:latest"`) | didSet: 저장 |
| `aiConfig` (계산) | `AIConfig` | — | 위 5개 묶음 |
| `selectedSidebarID` | `Guid?` | `null` | 사이드바 강조 행 (id 단위) |
| `focusedPane` | `FocusPane` | `.detail` | |
| `sheet` | `AppSheet?` | `null` | |
| `confirm` | `ConfirmRequest?` | `null` | didSet: 새 값이 오면 `confirmFocus = 0` |
| `confirmFocus` | `int` | `0` | 0 = 확인/실행 버튼, 1 = 취소 버튼 |
| `errorMessage` | `string?` | `null` | 토스트/알림용 |
| `infoMessage` | `string?` | `null` | |
| `defaultTabPaths` | `List<string>` | 영속 로드 | 기본 탭 (§7.4) |
| `typeEntries` (private, 관찰 제외) | `List<TypeFileEntry>` | `[]` | 카테고리 내역 인덱스(경로·크기) |
| `typeLoaded` (private, 관찰 제외) | `int` | `0` | 지금까지 목록에 옮긴 개수 — **`items.count`로 세지 말 것**(삭제 시 중복 로드 버그) |
| `folderSizeCache` (private) | `Dictionary<string,long>` | `{}` | 폴더 용량 캐시 (창 수명) |
| `sizeTask`/`listTask`/`searchTask` (private) | 취소 가능 작업 | — | CTS로 구현 |
| `recentsLoader`/`tagLoader` (private) | 로더 객체 | — | `cancel()` 지원 |
| `tabColorCounter` (private, 관찰 제외) | `int` | `1` | 다음 탭 색 인덱스 (첫 탭은 0) |
| `typeSelectBuffer`/`typeSelectLastKey`/`typeSelectRaw`/`typeSelectHideTask` (private) | — | — | type-select 상태 (§6.10) |
| `typeSelectDisplay` | `string?` | `null` | 입력 HUD 표시 텍스트 (null = 숨김) |
| `didBootstrap` (private) | `bool` | `false` | bootstrap 1회 가드 |

참조 타입(다른 스펙 영역 소유, 시그니처만):
- `AIProvider { ollama, gemini }`, `AIConfig { provider, geminiAPIKey, geminiModel, ollamaBaseURL, ollamaModel, fallbackToOllama=true }`
- `FinderTag { name, colorIndex }`; `TagService.standard` = [빨간색(6), 주황색(7), 노란색(5), 초록색(2), 파란색(4), 보라색(3), 회색(1)]
- `TypeBreakdown { name, bytes, count, files: [TypeFileEntry] }`, `TypeFileEntry { path, size }`

### 6.2 init / bootstrap — **생성자는 가볍게, 부수효과 금지**

`init()`:
1. 홈 폴더(`Environment.SpecialFolder.UserProfile`) 결정.
2. `favoritePaths`, `excludedPaths`, `folderViewModes` 영속 로드.
3. 홈을 가리키는 `PaneTab` 1개 생성: `history=[home]`, `historyIndex=0`,
   `viewMode = folderViewModes[home] ?? .full`. `tabs=[pane]`, `sections=[]`.
4. **디렉터리 읽기·사이드바 구성·Recents 시작 등 무거운 작업 절대 금지.**
   (SwiftUI가 렌더마다 모델을 재생성하는 문제 때문 — WPF에서는 덜 위험하지만 규칙 유지:
   ViewModel 생성자에서 I/O 금지, `Window.Loaded`에서 `Bootstrap()` 호출.)

`bootstrap()` (1회 가드):
1. `reloadDetail()` (동기 1회 목록 읽기), 커서 = 첫 항목.
2. `rebuildSections()` — [즐겨찾기, 위치, 태그] 3개 섹션 구성.
3. `selectedSidebarID = sidebarItem(matching: selectedFolder)`.
4. 저장된 기본 탭 중 **존재하는 경로만** 필터 → 비어 있으면 `showRecents()`(Finder처럼 최근 항목으로 시작),
   있으면 `restoreDefaultTabs(saved)`.

### 6.3 사이드바 구성

`rebuildSections()`: `[buildFavoritesSection(), buildLocationsSection(), buildTagsSection()]` —
전체 재구성(시작 시에만; 트리 펼침 상태 리셋됨).

- **즐겨찾기 섹션** (`buildFavoritesSection`):
  - 첫 행: `SidebarItem("최근 항목", icon "clock", url null, kind recents, hasChildren false)`.
  - 이후 `favoritePaths` 중 **존재하는** 폴더마다: title = `favoriteTitle(url)`, icon = `favoriteIcon(url)`,
    kind folder, `hasChildren = 하위 폴더 존재 여부`.
- **위치 섹션** (`buildLocationsSection`):
  - 홈 폴더 행 (title = 홈 폴더명, icon "house").
  - 마운트된 **탐색 가능한** 볼륨들 — resolved 경로로 중복 제거(부트 볼륨의 /Volumes 심볼릭 링크로
    생기는 두 번째 "Macintosh HD" 방지). 루트 볼륨 icon "internaldrive", 그 외 "externaldrive".
    볼륨명 없으면 루트는 "Macintosh HD", 그 외 lastPathComponent.
  - "컴퓨터" 노드는 의도적으로 **표시하지 않음** (루트 볼륨과 중복이라).
  - **Windows 대응**: `DriveInfo.GetDrives()`에서 `IsReady` 드라이브만;
    title = `VolumeLabel` 비면 "로컬 디스크 (C:)" 식; 고정 디스크 → internaldrive 아이콘,
    이동식/네트워크 → externaldrive. 중복 제거 불필요. 홈 행은 사용자 프로필 폴더.
- **태그 섹션** (`buildTagsSection`): `TagService.standard`의 7개 태그 각각
  `SidebarItem(title: 태그명, icon "circle.fill", url null, kind tag, hasChildren false)` —
  아이콘은 태그 색으로 칠함(뷰 책임). 섹션 제목 "태그".

`rebuildFavoritesSection()`: 즐겨찾기 섹션만 교체(위치 트리 펼침 상태 보존). 섹션이 없으면 맨 앞 insert.

`hostName()`: 컴퓨터 이름 (Windows: `Environment.MachineName`) — 현재 UI에선 미사용에 가까움.

`favoriteTitle(url)` (한국어 표시명 매핑 — **그대로 사용**):
- `/Applications` → "응용 프로그램"
- 폴더명 기준: Desktop→"데스크탑", Documents→"문서", Downloads→"다운로드", Pictures→"사진",
  Movies→"동영상", Music→"음악", Public→"공용", Library→"라이브러리", 그 외 폴더명 그대로.
- Windows: KnownFolder 경로와 비교해 같은 한국어 라벨 사용 (Videos→"동영상").

`favoriteIcon(url)`:
- `/Applications` → "square.grid.2x2"; Desktop→"menubar.dock.rectangle"; Documents→"doc";
  Downloads→"arrow.down.circle"; Pictures→"photo"; Movies→"film"; Music→"music.note"; 그 외 "folder".

### 6.4 즐겨찾기 (영속, §7.1)

- `isFavorite(url)`: 표준화 경로 일치 검사 (Windows: OrdinalIgnoreCase).
- `addFavorite(url)`: **폴더만** (디렉터리 아니면 무시), 중복 무시 → append + 저장 + 즐겨찾기 섹션 재구성.
- `removeFavorite(url)`: 제거 + 저장 + 섹션 재구성.
- `toggleFavoriteForCursor()`: 커서 항목이 폴더일 때 토글 (파일이면 no-op).
- `moveFavorite(fromPath:toBefore:)` (드래그 재정렬): movedPath 항목을 target **앞**으로 이동,
  target null이면 맨 뒤. 순서 변화 없으면 무시. 저장 후
  `reorderFavoritesSectionItems()` — **기존 SidebarItem 인스턴스를 재배치**(새로 만들지 않음;
  recents 행은 항상 맨 앞 유지) → 동일 identity로 애니메이션 이동.
  WPF: ObservableCollection의 `Move` 사용으로 동일 효과.

### 6.5 AI 정리 예외/보호 폴더

- `isDirectlyExcluded(url)`: 정확히 등록된 폴더인지 (메뉴 토글 표시용).
- `isExcluded(url)`: 자신 또는 등록 폴더의 하위인지 — **경로 경계 비교**
  (`path == base || path.StartsWith(base + separator)`) — "/A/B"가 "/A/BC"를 포함하지 않게.
- `addExcludedFolder(url)`: 폴더만, 중복 무시 → 등록 + 저장 +
  `infoMessage = "“{name}” 및 하위 폴더를 AI 정리 예외로 등록했습니다."`
- `removeExcludedFolder(url)`: 해제 + 저장 +
  `infoMessage = "“{name}”의 AI 정리 예외를 해제했습니다."`
- `isApplicationsLocation(url)`: `/Applications`, `/System/Applications`, `~/Applications` 하위 여부.
  **Windows**: `Program Files`, `Program Files (x86)`, `%LOCALAPPDATA%\Programs`, Windows 스토어 앱 폴더 등으로 대체.
- `isProtectedLocation(url)`: 재귀 보호 — `/Applications`, `/System`, `/Library`, `~/Library` 하위 전부;
  그 폴더 자체만 보호 — `/`, `/Users`.
  **Windows**: 재귀 보호 = `C:\Windows`, `C:\Program Files*`, `%APPDATA%`, `%LOCALAPPDATA%`;
  자체만 보호 = 드라이브 루트, `C:\Users`.
- `aiOrganizeBlocked(url) = isExcluded || isProtectedLocation` — 툴바 AI 버튼 비활성 + 실행 차단의 단일 기준.

### 6.6 탐색 (select / history / 상하좌우)

`select(url, addHistory = true, sidebarID = null)` — **모든 폴더 이동의 단일 진입점**:
1. `target = url.standardized`; `selectedFolder = target` (= `detail.directory`).
2. 상태 리셋: `selection=∅`, `filter=""`, `searchMode/recentsMode/tagMode/typeMode=false`,
   `tagName/typeName=null`, `typeTotal=0`, `typeEntries=[]`.
3. 진행 중 작업 취소: `searchTask`, `recentsLoader`, `tagLoader`.
4. `cursor = null`.
5. `viewMode = folderViewModes[target] ?? .full` (폴더별 보기 모드 복원).
6. `addHistory`면 `pushHistory(target)`.
7. `selectedSidebarID = sidebarID ?? sidebarItem(matching: target)` —
   명시 클릭이면 그 행, 아니면 url 일치 행(컴퓨터 노드보다 실제 폴더/볼륨 우선).
   **트리는 자동으로 펼치지 않음** (사용자가 삼각형으로만 펼침).
8. `loadDetail(target)` — 비동기 로드.

`loadDetail(dir)` (private):
- `listTask`/`sizeTask` 취소. **`let pane = detail`로 활성 탭 캡처** —
  로드 중 탭을 전환해도 결과가 "요청한 탭"에 들어가게 (탭 포워딩 계약의 핵심, 반드시 유지).
- 백그라운드에서 `FileSystemService.list(dir)` 실행.
- 완료 시: 취소됐거나 `pane.directory != dir`(이미 다른 폴더로 이동)이면 폐기.
  성공 → `pane.rawItems = items; pane.loadError = null`,
  실패 → `pane.rawItems = []; pane.loadError = 에러 메시지`.
- `pane.rebuild(showHidden)`, `pane.cursor = items.first`, `computeFolderSizes()`.
- 여유공간을 **백그라운드에서** 계산해(`statusFreeSpace`) 캐시 — 현재 폴더가 그대로일 때만 반영.
  (mac에서 느린 호출이라 캐시; Windows `DriveInfo.AvailableFreeSpace`는 빠르지만 패턴 유지 무방.)

`pushHistory(url)`: 현재 위치와 같으면 무시; `historyIndex` 뒤(forward) 항목 잘라내고 append; index = last.
`canGoBack = historyIndex > 0`; `canGoForward = historyIndex < count-1`.
`goBack()/goForward()`: index 이동 후 `select(history[index], addHistory: false)`.
`goUp()`: 부모 폴더로 `select` — 부모 == 자신(루트)이면 no-op. (단축키: ⌘↑/Backspace → Windows: Alt+↑/Backspace)

`activateSidebar(item)`:
- folder/trash → `select(item.url, sidebarID: item.id)`
- computer → `select("/")` (Windows: "내 PC" 가상 위치 또는 첫 드라이브 — 자체 결정 필요; 현재 mac UI엔 없음)
- recents → `showRecents()`
- tag → `showTag(해당 FinderTag, sidebarID)`
- airDrop → 아무것도 안 함

`reloadDetail()`: **동기** 재목록 + `rebuild` + `computeFolderSizes` (파일 작업 후 갱신용).
`refresh()` (⌘R/F5): `recentsMode`면 `showRecents()` 재실행하고 끝; 아니면 현재 `rawItems`의
url들을 `folderSizeCache`에서 제거(용량 재계산 강제) 후 `reloadDetail()`.

`goToFolder(path)` (⇧⌘G 시트): `~` 확장 + 경로 표준화(Windows: 환경변수 `%VAR%` 확장 추가 권장)
→ 존재하는 디렉터리가 아니면 `errorMessage = "폴더를 찾을 수 없습니다:\n{path}"`, 맞으면 `select`.

`open(item)`: 디렉터리이고 번들 아님 → `select(item.url)` (폴더 진입);
그 외 → `SystemActions.open` (Windows: `Process.Start(new ProcessStartInfo(path){UseShellExecute=true})`).

`revealInList(item)` (검색/가상 목록 우클릭 "위치로 이동"): 부모 폴더 `select` 후 `cursor = item.url`.
`relativeDisplay(item)`: 검색 결과 표시명 — 검색 루트 기준 상대 경로(루트 밖이면 그냥 name).

`isViewingTrash` (계산): `selectedFolder.lastPathComponent == ".Trash"`.
**Windows**: 휴지통은 파일시스템 경로로 직접 탐색 불가 — `isViewingTrash`는 별도 가상 모드로
재설계하거나(Shell API로 항목 나열) 1차 포팅에서는 사이드바 휴지통 항목을
`explorer.exe shell:RecycleBinFolder` 실행으로 대체하고 이 프로퍼티는 항상 false.

### 6.7 탭

- `newTab(folder = null)` (⌘T → Ctrl+T / 탭 바 ＋ / 우클릭 "새 탭에서 열기"):
  대상 = `folder ?? selectedFolder` (기본: 현재 폴더 복제).
  새 `PaneTab`: `history=[target]`, `historyIndex=0`, `viewMode = folderViewModes[target] ?? full`,
  **`groupKey`는 현재 탭에서 상속**, `colorIndex = tabColorCounter++` (카운터는 1부터; 첫 탭은 0).
  append → 활성화 → 사이드바 강조 갱신 → `focusedPane = detail` → `loadDetail(target)`.
- `closeTab(index)` (⌘W → Ctrl+W / 탭 ✕): **탭이 1개면 무시** (그때 ⌘W는 창 닫기 — 키 라우팅 쪽 책임).
  제거 후 `index < activeTabIndex`면 activeTabIndex--; 범위 초과면 마지막으로 클램프; `afterTabSwitch()`.
- `closeCurrentTab()` = `closeTab(activeTabIndex)`.
- `closeOtherTabs(index)`: 해당 탭만 남김, activeTabIndex=0, `afterTabSwitch()`.
- `selectTab(index)`: 같은 인덱스면 무시. **내용을 다시 읽지 않음** — 목록·커서·선택·스크롤 보존(의도된 동작).
- `cycleTab(delta)` (⌃Tab/⌃⇧Tab → Ctrl+Tab/Ctrl+Shift+Tab): `(active + delta + count) % count`.
- `afterTabSwitch()` (private): 사이드바 강조를 새 활성 탭 폴더에 맞추고, 여유공간만 백그라운드 갱신.

탭 바 외형(레이아웃은 다른 스펙 영역이지만 모델 관련 수치): 탭 2개 이상일 때만 표시.
파스텔 팔레트 8색 — **파랑/초록/주황/보라/분홍/청록/남색/갈색** — `colorIndex % 8`로 순환.
같은 색의 불투명도로 상태 구분: 비활성 0.16 / 호버 0.25 / 활성 0.38 + 굵은 글씨.
(0.14 미만으로 내리지 말 것 — 일부 디스플레이에서 안 보임.) 구분선 없음, 균등 폭 둥근 알약형.

### 6.8 기본 탭 (영속, §7.4)

- `saveCurrentTabsAsDefault()`: 현재 모든 탭의 폴더 경로 저장 +
  `infoMessage = "현재 탭 {n}개를 기본 탭으로 저장했습니다.\n다음 실행부터 이 탭들로 시작합니다."`
- `clearDefaultTabs()`: 저장 삭제 → 기본 동작(최근 항목 1개) 복귀.
- `restoreDefaultTabs(paths)` (bootstrap 전용): 첫 폴더는 기존 탭이 그대로 `select`,
  나머지는 `newTab(folder:)`로 열고 `selectTab(0)`.

### 6.9 가상 목록 모드 4종 — 공통 규약

| 모드 | 플래그 | 진입점 | 데이터 |
|---|---|---|---|
| 검색 | `searchMode` | `updateSearch` | 현재 폴더 재귀 검색 (limit 1000) |
| 최근 항목 | `recentsMode` | `showRecents` | 시스템 최근 파일 (limit 100, 카테고리 필터) |
| 태그 | `tagMode`+`tagName` | `showTag` | 태그 붙은 파일 목록 |
| 종류별 내역 | `typeMode`+`typeName`/`typeTotal` | `showTypeBreakdown` | 디스크 팝업 카테고리, 크기 내림차순, 페이징 |

**진입 시 반드시**: 다른 모드 플래그 전부 초기화 + 관련 로더/태스크 전부 취소 + `selection=∅` +
`items=[]` + `cursor=null`. **해제**는 `select()`(일반 폴더 이동)가 담당.
가상 모드 중에는 `rebuild()`가 no-op이고 `items`는 AppModel이 직접 채운다.
가상 모드에서도 열기/복사/삭제/우클릭은 동작하되, **"새 폴더·붙여넣기" 등 폴더 전제 작업은 숨김**.

`showRecents()`:
- searchTask/tagLoader 취소, 플래그 정리, `recentsMode=true`, `selectedSidebarID = recents 행`.
- `RecentsLoader.load(limit: 100, categories: recentsCategories)` → 콜백에서
  **여전히 recentsMode일 때만** `items` 반영 + 커서 첫 항목.
- Windows: `%APPDATA%\Microsoft\Windows\Recent`의 .lnk 해석(또는 Windows Search) — 베스트에포트.

`showTag(tag, sidebarID = null)`:
- 다른 작업 취소(search/recents/list), 플래그 정리, `tagMode=true; tagName=tag.name`.
- `TagLoader.load(tagName:)` → 콜백 가드: `tagMode && tagName == tag.name`일 때만 반영.
- Windows: Finder 태그(xattr `com.apple.metadata:_kMDItemUserTags`, "이름\n색번호" plist) 대응 없음 —
  NTFS ADS(`file:XFinder.Tags`) 또는 로컬 JSON DB(경로→태그 목록)로 자체 구현. 태그 7색 이름은 동일 유지.

`showTypeBreakdown(stat)` + 무한 스크롤 페이징:
- 상수 `typePageSize = 500`. 카테고리당 인덱스 상한 20만 개(스캔 쪽 책임).
- 진입: 취소·플래그 정리 후 `typeMode=true; typeName=stat.name; typeTotal=stat.count`,
  `typeEntries = stat.files`(크기순 정렬 완료 상태), `typeLoaded=0`, 첫 페이지 즉시 추가(재스캔 없음),
  커서 첫 항목, `selectedSidebarID=null`.
- `loadMoreTypeItemsIfNeeded(currentIndex)` — 행이 화면에 나타날 때 호출:
  `typeMode && currentIndex >= items.count - 100 && typeLoaded < typeEntries.count`면 다음 페이지.
- `appendNextTypePage()`: 다음 500개를 `FileItem`으로 변환(파일로 간주: isDirectory=false,
  size=entry.size, modified=distantPast) 후 append; `typeLoaded += page.count`;
  그 페이지의 수정일·생성일·종류만 백그라운드 조회(`enrichTypeItems`).
- `enrichTypeItems(paths, category)`: 백그라운드에서 메타데이터 일괄 조회 후
  **`typeMode && typeName == category` 가드** → 경로 매칭으로 현재 `items`에 병합
  (페이지 추가/삭제와 경합해도 안전).
- 경로 표시줄 예: `파일 계산 — 기타 (크기순 · 500개 로드됨 / 전체 131,170개)`.

`updateSearch(query)` (툴바 검색창):
- `detail.filter = query`; needle = trim+소문자; `searchTask` 취소.
- needle 비면: `searchMode=false`, `reloadDetail()`, 커서 첫 항목. (일반 목록 복귀)
- 아니면: tagLoader 취소, tag/type/recents 플래그 정리, `searchMode=true`, `selection=∅`, `items=[]`,
  루트/`showHidden` 캡처 → 백그라운드 `searchRecursive(root, needle, showHidden, limit: 1000)`.
- 완료 가드 3중: 취소 안 됨 && `selectedFolder == root` && 현재 filter(trim+소문자) == needle.
  (낡은 결과 폐기 — 입력이 빠르게 바뀌어도 마지막 질의만 반영.)

### 6.10 키보드 — 커서/선택/type-select

- `moveCursor(delta)` (↑↓/PageUp·Down): 커서 인덱스 ± delta 클램프.
  **일반 화살표는 선택을 해제**하고(`selection=∅`) 앵커=커서.
- `cursorToTop()/cursorToBottom()` (Home/End): 동일하게 선택 해제.
- `extendCursor(delta)` (Shift+화살표): 앵커 없으면 현재 커서(없으면 첫 항목)를 앵커로;
  커서 이동 후 `applyAnchorRange` — 앵커~커서 구간 전체 선택(`isParent` 제외).
- `extendCursorToTop()/extendCursorToBottom()` (Shift+Home/End).
- `selectRange(fromIndex:toIndex:)` (마우스 드래그 선택): lo..hi 전체 선택, 커서=드래그 끝(hi), 앵커=lo.
- `openCursorItem()` (Return): `currentItem()` 열기.
- `currentItem()`: 커서 행, 없으면 첫 항목, 그것도 없으면 null.

Type-select (목록에서 글자 입력 → 해당 항목으로 점프; **textInputActive면 동작 정지**):
- 상수: 버퍼 리셋 1.0초(`typeSelectResetInterval`), HUD 자동 숨김 1.2초.
- `typeSelect(chars) -> bool`: 첫 문자가 글자/숫자가 아니면 false(이벤트 미소비).
  목록 비면 true만 반환.
- 비교 키 `jamoKey(s)`: NFC 정규화 + 소문자화 후, **한글 음절(U+AC00~U+D7A3)을 키 입력 단위
  자모로 분해** — `idx = code - 0xAC00`; 초성 `idx/588`, 중성 `(idx%588)/28`, 종성 `idx%28`.
  - 초성 19자: `ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ`
  - 중성 21개(겹모음은 타이핑 순서로 분해): ㅏ ㅐ ㅑ ㅒ ㅓ ㅔ ㅕ ㅖ ㅗ ㅗㅏ ㅗㅐ ㅗㅣ ㅛ ㅜ ㅜㅓ ㅜㅔ ㅜㅣ ㅠ ㅡ ㅡㅣ ㅣ
  - 종성 28개(빈 종성 포함, 겹받침 분해): "" ㄱ ㄲ ㄱㅅ ㄴ ㄴㅈ ㄴㅎ ㄷ ㄹ ㄹㄱ ㄹㅁ ㄹㅂ ㄹㅅ ㄹㅌ ㄹㅍ ㄹㅎ ㅁ ㅂ ㅂㅅ ㅅ ㅆ ㅇ ㅈ ㅊ ㅋ ㅌ ㅍ ㅎ
  - 단독 겹모음/겹받침 문자도 분해: ㅘ→ㅗㅏ, ㅙ→ㅗㅐ, ㅚ→ㅗㅣ, ㅝ→ㅜㅓ, ㅞ→ㅜㅔ, ㅟ→ㅜㅣ, ㅢ→ㅡㅣ,
    ㄳ→ㄱㅅ, ㄵ→ㄴㅈ, ㄶ→ㄴㅎ, ㄺ→ㄹㄱ, ㄻ→ㄹㅁ, ㄼ→ㄹㅂ, ㄽ→ㄹㅅ, ㄾ→ㄹㅌ, ㄿ→ㄹㅍ, ㅀ→ㄹㅎ
  - 이 알고리즘은 100% 이식 가능(C#에서 `s.Normalize(NormalizationForm.FormC)` + 동일 테이블).
- 동작:
  1. 마지막 입력에서 1초 초과 → 버퍼 새로 시작.
  2. **같은 키 반복**(만료 전 && 버퍼 == 그 키 하나): 같은 접두어 일치 항목 사이를 순환
     (현재 커서 다음 일치, 끝이면 처음). HUD에 원본 입력 표시.
  3. 아니면 버퍼/원본에 누적. 일치 검색: `jamoKey(name).hasPrefix(buffer)`인 첫 항목(`isParent` 제외).
  4. 접두어 일치 없으면 **사전순 후속 항목**: `jamoKey(name) > needle`인 항목 중 최소,
     그것도 없으면(예: "zzz") 사전순 마지막 항목.
  5. 커서 이동 시 선택 해제 + 앵커=커서 (자동 스크롤은 뷰의 커서 변경 감지가 담당).
- HUD: `typeSelectDisplay`에 원본 입력(자모 분해 전) 표시 → 1.2초 후 자동 숨김(새 입력마다 타이머 리셋).

사이드바 키보드 (focusedPane == sidebar일 때):
- `toggleFocusedPane()` (Tab): sidebar ⇄ detail. 사이드바 진입 시 강조 없으면 현재 폴더 행(없으면 첫 행).
- `visibleSidebarItems`: 펼침 상태를 반영한 위→아래 평탄화 목록.
- `moveSidebarSelection(delta)` (↑↓): 강조 이동 + **즉시 그 행으로 탐색**(Finder식 — activateSidebar 호출).
- `expandSidebarSelection()` (→): 펼칠 수 있고 안 펼쳐졌으면 펼침; 이미 펼쳐졌으면 첫 자식으로(+1 이동).
- `collapseSidebarSelection()` (←): 펼쳐져 있으면 접기; 리프면 부모 행으로 이동(activate).
- `activateSelectedSidebar()` (Return): 강조 행 활성화 + `focusedPane = detail`.
- `toggleExpand(item)`: 펼침 토글; 펼치면 `loadChildren(showHidden)`; 강조 없으면 현재 폴더 행 강조.
- `sidebarItem(matching: url)`: url이 일치하는 행 중 **computer가 아닌 행 우선** 1개의 id.
- `refreshSidebar(at: url)` (private): 그 url 노드의 children을 null로 리셋, 펼쳐져 있으면 재로드
  (파일 작업 후 트리 갱신).

확인 대화상자 키보드:
- `moveConfirmFocus(delta)`: `(confirmFocus + delta + 2) % 2` — 좌우(또는 상하)로 0/1 토글.
- `executeConfirmFocus()` (Enter): 포커스된 버튼 실행.
- `executeConfirm(index)`: `confirm = null` 먼저, `index == 0`이면 action 실행.
- `cancelConfirm()` (Esc): `confirm = null`.

### 6.11 폴더 용량 계산 (옵션, 기본 끔)

`computeFolderSizes()`:
- `sizeTask` 취소. `calculateFolderSizes == false`면 종료(Finder처럼 기본 끔 — 즉각적 탐색 유지).
- 대상: `items` 중 `isDirectory && !isSymlink && !isParent`.
- 캐시 히트 항목은 **즉시** 반영; 미캐시 항목은 백그라운드에서 **병렬** 스캔
  (Windows: `Parallel.ForEach` + 재귀 `Directory.EnumerateFiles` — 접근 거부는 건너뜀).
- 완료 시 취소/폴더 변경 검사 후 캐시에 기록, `applyFolderSizes`로 **한 번에** 반영
  (items와 rawItems 양쪽의 해당 항목 `size` 갱신 — 재렌더 1회).
- 설정 토글 didSet: 켜면 즉시 계산, 끄면 sizeTask 취소.
- `refresh()`가 캐시를 비워 재계산을 강제.

### 6.12 클립보드 (복사/잘라내기/붙여넣기) — Explorer 스타일

- `forwardToTextResponder(selector)`: 텍스트 필드/에디터가 포커스 중이면 표준 텍스트 동작을 거기로 보내고
  true (검색창에서 Ctrl+C/X/V가 텍스트로 동작하게). WPF: `Keyboard.FocusedElement is TextBoxBase`면
  `ApplicationCommands.Copy/Cut/Paste` 실행.
- `copyShortcut()/cutShortcut()/pasteShortcut()`: 텍스트 포워딩 실패 시에만 파일 동작.
- `copySelection()` (⌘C → Ctrl+C): `actionTargets()`의 url들로 `clipboard = (urls, isCut:false)` +
  시스템 클립보드에 파일 목록 기록(다른 앱에서 붙여넣기 가능) + changeCount 기억.
- `cutSelection()` (⌘X → Ctrl+X): 동일하되 `isCut:true`. **외부 앱이 붙여넣으면 복사**, 우리 앱 붙여넣기만 이동.
- `copySelectedPath()`: 경로들을 텍스트로(여러 개면 `"\n"` 연결) 시스템 클립보드에.
- `paste()` (⌘V → Ctrl+V) 분기 — **순서 중요**:
  1. 시스템 클립보드의 파일 목록과 changeCount 조회. `weOwnPasteboard = (changeCount == 기록값)`.
  2. **남이 쓴** 클립보드 + 파일 있음 → 그 파일들 **복사**.
  3. 아니면 내부 `clipboard` 있음 → 그 urls, `isCut`이면 **이동** 아니면 복사.
  4. 그것도 없고 클립보드에 파일 있으면 복사. 다 없으면 no-op.
  5. `runTransfer(urls, to: selectedFolder, move:, clearClipboardOnSuccess: true)`.
- **Windows 구현**:
  - 파일 기록: `Clipboard.SetFileDropList` + `"Preferred DropEffect"` 포맷에
    `DROPEFFECT_MOVE(2)`/`DROPEFFECT_COPY(5)` 기록 → 탐색기와 잘라내기/복사 의미 상호 호환.
  - changeCount 대응: `GetClipboardSequenceNumber()` P/Invoke (또는 자체 마커 포맷 등록).
  - 읽기: `Clipboard.GetFileDropList` + `"Preferred DropEffect"`를 읽어 외부 잘라내기도 이동으로
    존중하면 더 자연스러움(원본은 외부=항상 복사 — 선택 사항으로 명시).

### 6.13 드래그&드롭 / 전송 실행

`dropFiles(urls, onto folder, copy)`: 앱 내 폴더에 드롭. **기본 이동, ⌥/⌘(→ Ctrl) 누르면 복사.**
유효성 필터(각 src에 대해):
- 자기 자신에 드롭 금지 (`src == dest`).
- 폴더를 **자기 하위 트리**에 드롭 금지 (`dest.StartsWith(src + sep)`).
- 이동인데 src의 부모 == dest면 no-op 제외.

`runTransfer(urls, to, move, clearClipboardOnSuccess=false)` (private, 공용 실행기):
1. 빈 목록이면 종료. `OperationProgress(title: move ? "이동 중…" : "복사 중…")` 생성,
   `sheet = .progress(progress)`.
2. 소스 부모 폴더 집합 기억(사이드바 갱신용).
3. `await FileOperations.transfer(...)` → `dismissProgress(progress)` —
   **자기 progress일 때만 시트 닫기** (다른 시트/다른 작업의 진행을 덮지 않게, 객체 identity 비교).
4. `clearClipboardOnSuccess && move && 성공`일 때만 `clipboard = null`
   (부분 실패면 클립보드 유지 — 재시도 가능).
5. `reloadDetail()` + 대상/소스 부모들 `refreshSidebar`.
6. 실패면 `errorMessage = 메시지`.

### 6.14 파일 작업

- `viewSelected()` (Space/F3): Quick Look. **Windows: 자체 미리보기 창**(viewer 시트로 라우팅하거나
  이미지/텍스트/PDF 자체 렌더). 원 코드의 `sheet = .viewer`가 아닌 OS Quick Look 호출임에 유의 —
  Windows에서는 `AppSheet.Viewer`로 통일 권장.
- `editSelected()` (F4) / `openSelected()`: 기본 앱으로 열기 / open().
- `requestNewFolder()` (⇧⌘N → Ctrl+Shift+N): `sheet = .newFolder`.
- `requestGoToFolder()` (⇧⌘G → Ctrl+Shift+G 또는 Ctrl+L): `sheet = .goToFolder`.
- `requestRename()` (F2): 커서 항목으로 `sheet = .rename(item)`.
- `createFolder(named)`: trim → 비면 무시 → `WindowsName.sanitize`(금지 문자/예약 이름 처리 —
  Windows에서는 동일 규칙이 필수) → 생성 → `reloadDetail` + `refreshSidebar` + 새 폴더에 커서.
  이름이 바뀌었으면 `infoMessage = "윈도우 호환을 위해 폴더명을 “{safe}”(으)로 저장했습니다."`
  실패: `errorMessage = "폴더를 만들 수 없습니다: {err}"`.
- `rename(item, to)`: trim, 동일 이름이면 무시, sanitize 후도 동일하면 무시 → move →
  갱신 + 커서. 바뀌었으면 `infoMessage = "윈도우 호환을 위해 이름을 “{safe}”(으)로 저장했습니다."`
  실패: `errorMessage = "이름을 바꿀 수 없습니다: {err}"`.
- `cursorToItem(named:)` (private): 마지막 경로 요소 이름으로 목록에서 찾아 커서 배치
  (생성 직후 URL 형태 차이를 흡수).
- `duplicate()` (⌘D): 각 대상을 같은 폴더에 `"{sanitize(이름)} copy"` + 중복 회피(`uniqueURL`)로 복사;
  마지막 복제본에 커서; 실패들은 `errorMessage`로 합쳐 표시.
- `requestDelete()` (⌘⌫ → Delete): 대상 없으면 무시.
  `names = 대상 1개면 "“{name}”" 아니면 "{n}개 항목"`.
  `ConfirmRequest(title: "휴지통으로 이동", message: "{names}을(를) 휴지통으로 옮기시겠습니까?",
  confirmTitle: "휴지통으로 이동", isDestructive: true)`.
- `performDelete(targets)`: 각 항목 휴지통으로 (Windows: `SHFileOperation`/`IFileOperation`
  `FOF_ALLOWUNDO`, 또는 `Microsoft.VisualBasic.FileIO.FileSystem.Delete*(RecycleOption.SendToRecycleBin)`).
  실패는 `"{name}: {err}"` 수집.
  **가상 모드(recents/search/type)면 디렉터리 재목록이 불가하므로 `removeFromListing(removed)`**,
  아니면 `reloadDetail` + `refreshSidebar`. 실패 있으면 errorMessage(줄바꿈 연결).
- `removeFromListing(urls)` (private): items/rawItems에서 제거, selection에서 빼고,
  커서가 삭제됐으면 **같은 인덱스 위치(클램프)** 항목으로, 인덱스 모르면 첫 항목, 목록 비면 null.
- `compressSelected()`: 대상 1개면 그 이름, 여러 개면 `"Archive"`를 base로 `.zip` 고유 이름 생성;
  progress "압축 중…" 시트; 완료 후 reload + zip에 커서; 실패 errorMessage.
- `extractSelected()`: 커서 항목이 `.zip`이 아니면
  `errorMessage = "압축을 풀 .zip 파일을 선택하세요."`; progress "압축 푸는 중…"; 완료 후 reload + 사이드바 갱신.
- `revealInFinder()`: 커서 항목(없으면 현재 폴더)을 OS 탐색기에서 표시.
  Windows: `explorer.exe /select,"{path}"`.
- `getInfoSelection()` (⌘I): Finder '정보 가져오기' — AppleScript로 Finder 제어, 권한(-1743) 거부 시
  `errorMessage = "‘정보 가져오기’는 Finder 를 제어해 여는 기능이라 자동화 권한이 필요합니다.\n설정 → 개인정보 보호 및 보안 → 자동화에서 XFinder 의 Finder 항목을 켜 주세요."` + 설정 열기.
  **Windows 대응**: 셸 속성 대화상자 — `SHObjectProperties(hwnd, SHOP_FILEPATH, path, null)` P/Invoke.
  권한 안내 분기는 통째로 불필요(제거).
- `openTerminal()`: 현재 폴더에서 터미널. Windows: `wt.exe -d "{path}"` / `powershell -NoExit -Command Set-Location`.
- `openDayflow()`: macOS 전용 메뉴바 앱(DayFlow)을 distributed notification으로 깨우는 기능 —
  **포팅 무의미. 제거** (또는 임의 외부 앱 실행 설정으로 대체).
- `toggleHidden()` (⇧⌘. → Ctrl+H 제안): `showHidden` 토글 + `reloadDetail()` +
  **이미 로드된 사이드바 가지 전부 children=null 리셋 후 펼쳐진 것만 재로드** (숨김 폴더 반영).
- `toggleViewMode()` (⌃M 제안 → Ctrl+M): full ⇄ icon 토글 + `folderViewModes[현재 폴더] = 모드` 저장.
- `setGroupKey(key)`: `detail.groupKey = key` + `rebuild`. 가상 모드에서는 적용되지 않음(activeGroups 가드).
- `showHelp()`: `infoMessage = AppModel.helpText` (§8.1 전문).

### 6.15 한글 자소(NFD) 파일명 복원

`fixDecomposedNames(recursive = false)`:
- `HangulNormalize.scan(directory, recursive)`로 NFD 분리 파일명 수집.
- 없으면: recursive ? `"하위 폴더까지 살펴봤지만 자소가 분리된 한글 파일명이 없습니다."`
  : `"이 폴더에는 자소가 분리된 한글 파일명이 없습니다."` (infoMessage).
- 있으면 ConfirmRequest:
  - title: `"한글 파일명 복원"`
  - message: `"{scope}자소가 분리된 한글 파일명 {n}개를 찾았습니다. 정상 형태로 바꾸시겠습니까?\n\n{preview}{more}"`
    - scope = recursive ? `"하위 폴더까지 포함해 "` : `""`
    - preview = 최대 6개, 각 줄 `"• {복원된 이름}"`; more = 7개 이상이면 `"\n…외 {n-6}개"`
  - confirmTitle: `"복원"`, isDestructive: false.
- `performHangulFix(targets)`: **깊은 경로부터** 처리(부모 이름 변경으로 경로 깨짐 방지);
  존재 확인; 유니코드 **스칼라 비교**로 이미 NFC면 건너뜀; 같은 폴더 안 rename.
  결과: 전부 성공 → `"한글 파일명 {n}개를 복원했습니다."`;
  실패 있으면 `"{fixed}개 복원, {failCount}개 실패:\n{최대 5개}{\n…외 N개}"` (errorMessage).
- **Windows 노트**: NTFS는 정규화 비구분이 아니므로 NFD→NFC rename이 "같은 슬롯"이 아니라
  실제 이름 변경이다(충돌 가능 — 동명 NFC 파일 존재 시 uniqueURL로 회피 필요).
  mac에서 복사해 온 파일에 유용하므로 기능 유지 권장. C#: `name.Normalize(FormC)` 비교는
  서수(ordinal) 문자열 비교로.

### 6.16 AI 파일 정리 / 응용 프로그램 삭제 (모델 쪽 책임 부분만)

- `openSettings()`: 설정 단독 창 표시 (⌘, → Ctrl+,).
- `requestAIOrganize()`: 보호 폴더면
  `errorMessage =` (응용 프로그램 위치면) `"응용 프로그램 폴더는 AI 파일 정리에서 제외됩니다."`
  아니면 `"시스템 폴더는 AI 파일 정리에서 제외됩니다."`;
  예외 폴더면 `"이 폴더는 AI 정리 예외 폴더로 지정되어 정리할 수 없습니다."`; 통과 시 `sheet = .aiOrganize`.
- `currentFolderEntries(limit = 300)`: 현재 폴더 최상위 항목명(숨김 제외) 정렬 후 최대 300개 — LLM 후보 목록.
- `applyAIPlan(ops)`: 시작 시 `aiOrganizeBlocked` 재검사(위 3종 메시지).
  각 op: 원본 없음 → `"{file}: 항목 없음"` 실패.
  - 삭제: 휴지통으로(영구 삭제 아님). mac은 실패 시 Finder 위임 폴백 — Windows는 SHFileOperation 한 번이면 충분.
  - 이동: `isSafeDestination` 검사(상대 하위 경로만), 자기 자신으로 이동 방지, 대상 폴더 없으면 생성,
    동명 존재 시 `uniqueURL`로 회피.
  - 결과: `"AI가 {n}개 정리, {m}개 휴지통으로 이동했습니다."` 형태(있는 항목만 ", " 연결;
    둘 다 0이면 `"처리한 항목이 없습니다"`). 실패 있으면
    `"{doneText}\n{failCount}개 실패:\n{최대 5개}{\n…외 N개}"` (errorMessage).
- `requestUninstall(item)`: `.app`일 때만 `sheet = .uninstall(item)`.
  **Windows: .app 개념 없음** — 이 기능은 "프로그램 제거"(`ms-settings:appsfeatures` 열기 또는
  레지스트리 Uninstall 키 실행)로 대체하거나 1차 포팅에서 제외.
- `performUninstall(urls)`: 휴지통 시도 → 실패분 Finder 위임 → 자동화 권한(-1743) 거부 시 ConfirmRequest
  (title `"‘자동화’ 권한이 필요합니다"`, message `"보호된 항목을 삭제하려면 XFinder가 Finder를 제어하도록 허용해야 합니다.\n\n시스템 설정 > 개인정보 보호 및 보안 > 자동화에서 XFinder 아래의 Finder를 켠 뒤 다시 시도하세요."`,
  confirmTitle `"자동화 설정 열기"`); 그래도 남은 항목은
  `errorMessage = "다음 항목을 삭제하지 못했습니다:\n" + "• {이름}" 줄들`.
  **Windows: 권한 위임 분기 전체가 무의미** — 관리자 권한 필요 시 UAC 승격 재시도 안내로 대체.

---

## 7. 영속화 — UserDefaults 키 전체

Windows 구현: `%APPDATA%\XFinder\settings.json` 단일 JSON 파일 권장 (키 이름 그대로 JSON 속성으로).
모든 didSet 저장은 "변경 즉시 저장" 의미 유지.

| # | 키 | 형식 | 기본값(미설정 시) | 내용 |
|---|---|---|---|---|
| 1 | `XFinder.favorites.v1` | `[String]` 경로 배열 | `defaultFavorites` | 즐겨찾기. 기본: `/Applications`, `~/Desktop`, `~/Documents`, `~/Downloads`, `~/Pictures`, `~/Movies`, `~/Music` 중 존재하는 것 (Windows: Desktop/Documents/Downloads/Pictures/Videos/Music KnownFolder; Applications 항목은 제외 또는 시작 메뉴로 대체) |
| 2 | `XFinder.aiExcludedFolders.v1` | `[String]` | `[]` | AI 정리 예외 폴더(표준화 경로) |
| 3 | `XFinder.folderViewModes.v1` | `[String: String]` 경로→`"full"|"icon"` | `{}` | 폴더별 보기 모드 |
| 4 | `XFinder.appearance.v1` | `String` | `"system"` | 화면 모드 |
| 5 | `XFinder.dateStyle.v1` | `String` | `"absolute"` | 날짜 표시 방식 |
| 6 | `XFinder.searchPosition.v1` | `String` | `"toolbar"` | 검색창 위치 |
| 7 | `XFinder.terminalApp.v1` | `String` | `"auto"` | 터미널 앱 (키는 `SystemActions.terminalPrefKey`) |
| 8 | `XFinder.listScale.v1` | `Double` | `1.0` (로드 시 0.8~1.8 클램프) | 목록 글자/아이콘 배율 |
| 9 | `XFinder.columnWidths.v1` | `[String: Double]` 키 = ListColumn rawValue | `{}` | 열 너비(배율 적용 전; set 시 44~360 클램프) |
| 10 | `XFinder.recentsCategories.v1` | `[String]` | 키 없음 → `["문서","이미지"]` | 최근 항목 카테고리. **빈 배열도 유효 저장값(=전체)** — "키 없음"과 "빈 배열" 구분 필수 |
| 11 | `XFinder.calculateFolderSizes.v1` | `Bool` | `false` | 폴더 용량 계산 |
| 12 | `XFinder.aiProvider.v1` | `String` (`"ollama"|"gemini"`) | `"gemini"` | AI 제공자 |
| 13 | `XFinder.geminiAPIKey.v1` | `String` | `""` | Gemini API 키 (Windows: DPAPI 암호화 저장 권장) |
| 14 | `XFinder.geminiModel.v1` | `String` | 빈 값 → `"gemini-flash-latest"` | |
| 15 | `XFinder.ollamaBaseURL.v1` | `String` | 빈 값 → `"http://localhost:11434"` | |
| 16 | `XFinder.ollamaModel.v1` | `String` | 빈 값 → `"gemma4:latest"` | |
| 17 | `XFinder.defaultTabs.v1` | `[String]` 경로 배열 | 키 없음 = 기본 동작 | 기본 탭. `clearDefaultTabs`는 키 자체를 삭제 |

비영속(의도적): `showHidden`, `sortKey/sortAscending`, `groupKey`, 탭 구성(기본 탭 제외), 히스토리, 클립보드.

---

## 8. UI 문자열 (사용자에게 보이는 한국어 — 원문 그대로 사용)

### 8.1 도움말 전문 (`AppModel.helpText`; ⌘→Ctrl 치환은 포팅 시 별도 결정)

```
XFinder — 키보드 단축키 (클릭 없이 항상 동작)

↑ ↓ / PageUp·Down / Home·End   커서 이동
Return          파일 열기 / 폴더 진입
⌘↓              선택 항목 열기
⌘↑ / Backspace  상위 폴더로
⌘[ ⌘] / ⌘← ⌘→   뒤로 / 앞으로
Space (F3)      빠른 보기      F4  기본 앱으로 열기

⌘C 복사   ⌘X 잘라내기   ⌘V 붙여넣기
⌘D 복제   ⌘⌫ 휴지통으로   F2 이름 변경
⇧⌘N 새 폴더   ⌘R·F5 새로고침
⇧⌘.  숨김 파일   ⇧⌘G  폴더로 이동   ⌃M  목록/아이콘

⌘T  새 탭   ⌘W  탭 닫기(마지막 탭이면 창 닫기)   ⌃Tab / ⌃⇧Tab  탭 전환

Tab             사이드바 ⇄ 파일 목록 포커스 전환
사이드바 포커스 시  ↑↓ 이동 · → 펼치기 · ← 접기 · Return 열기
경로 막대 더블클릭(또는 ✎) → 경로 직접 입력 후 Return

폴더 우클릭 → 즐겨찾기에 추가 / 제거
```

### 8.2 고정 라벨/제목
- 섹션: "즐겨찾기" / "위치" / "태그"; 행: "최근 항목"; 루트 볼륨 기본명 "Macintosh HD"
- 즐겨찾기 표시명: "응용 프로그램", "데스크탑", "문서", "다운로드", "사진", "동영상", "음악", "공용", "라이브러리"
- 태그명: "빨간색", "주황색", "노란색", "초록색", "파란색", "보라색", "회색"
- 탭 제목: "최근 항목" / "{태그명}" / "파일 계산 — {typeName}" / "검색: {filter}"
- 그룹 제목: "폴더", "#", "1 GB 이상", "100 MB ~ 1 GB", "1 MB ~ 100 MB", "1 MB 미만",
  "오늘", "어제", "지난 7일", "지난 30일", "{year}년", "날짜 없음"
- 상대 시간: "방금 전", "{n}분 전", "{n}시간 전", "{n}일 전", "{n}주 전", "{n}개월 전", "{n}년 전"
- 진행 제목: "복사 중…", "이동 중…", "압축 중…", "압축 푸는 중…"
- enum 라벨: §1.6, §2.2, §2.3 참조 (AIProvider: "로컬 (Ollama)" / "Gemini")

### 8.3 메시지 (infoMessage / errorMessage / ConfirmRequest) — §6의 각 동작 항목에 원문 수록
핵심 목록(치환 변수 포함):
- "“{name}” 및 하위 폴더를 AI 정리 예외로 등록했습니다." / "“{name}”의 AI 정리 예외를 해제했습니다."
- "현재 탭 {n}개를 기본 탭으로 저장했습니다.\n다음 실행부터 이 탭들로 시작합니다."
- "윈도우 호환을 위해 폴더명을 “{safe}”(으)로 저장했습니다." / "윈도우 호환을 위해 이름을 “{safe}”(으)로 저장했습니다."
- "폴더를 만들 수 없습니다: {err}" / "이름을 바꿀 수 없습니다: {err}" / "폴더를 찾을 수 없습니다:\n{path}"
- 삭제 확인: "휴지통으로 이동" / "{names}을(를) 휴지통으로 옮기시겠습니까?" / 버튼 "휴지통으로 이동"
- 한글 복원 일련(§6.15) / AI 정리 일련(§6.16) / 압축: "압축을 풀 .zip 파일을 선택하세요."
- (mac 전용, Windows 제거 대상) 자동화 권한 안내 2종, "다음 항목을 삭제하지 못했습니다:\n• …"

---

## 9. UI 외형 관련 수치·아이콘 (모델이 정의하는 부분)

### 9.1 수치 상수
| 항목 | 값 |
|---|---|
| 목록 배율 `listScale` | 0.8 ~ 1.8, 기본 1.0 |
| 열 기본 너비 | size 70 / modified 120 / created 120 / kind 96 (pt, 배율 전) |
| 열 너비 범위 | 44 ~ 360 |
| 검색 결과 상한 | 1000 |
| 최근 항목 상한 | 100 |
| type 페이지 크기 | 500 (트리거: 끝에서 100행 이내) |
| type-select 버퍼 리셋 | 1.0초; HUD 숨김 1.2초 |
| 탭 색 불투명도 | 비활성 0.16 / 호버 0.25 / 활성 0.38 (0.14 미만 금지) |
| 탭 팔레트 | 8색: 파랑/초록/주황/보라/분홍/청록/남색/갈색 (colorIndex % 8) |
| 확인 메시지 미리보기 | 한글 복원 6개, 실패 표시 5개 상한 |

### 9.2 SF Symbol → Windows 대응 (Segoe Fluent Icons 글리프 제안)

| SF Symbol | 용도 | Segoe Fluent/MDL2 제안 | 비고 |
|---|---|---|---|
| `clock` | 최근 항목 | U+E823 (Recent) | |
| `house` | 홈 | U+E80F (Home) | |
| `internaldrive` | 내장 디스크 | U+EDA2 (HardDrive) | |
| `externaldrive` | 외장/기타 볼륨 | U+E88E (USB) 또는 U+EDA2 | 이동식은 U+E88E |
| `circle.fill` | 태그 색 점 | 글리프 대신 **WPF `Ellipse`(지름 ~10px) 색 채움** 권장 | FinderTag.colorIndex → 색: 1=회색, 2=초록, 3=보라, 4=파랑, 5=노랑, 6=빨강, 7=주황 |
| `folder` | 리프 폴더 | U+E8B7 (Folder) | |
| `folder.fill` | 하위 폴더 있는 폴더 | U+E8D5 (FolderFill) | 없으면 E8B7로 통일 가능 |
| `square.grid.2x2` | 응용 프로그램 | U+E71D (AllApps) | |
| `menubar.dock.rectangle` | 데스크탑 | U+E7F4 (TVMonitor) 또는 U+E8FC | |
| `doc` | 문서 | U+E8A5 (Document) | |
| `arrow.down.circle` | 다운로드 | U+E896 (Download) | |
| `photo` | 사진 | U+E8B9 (Picture) | |
| `film` | 동영상 | U+E714 (Video) | |
| `music.note` | 음악 | U+E8D6 (MusicInfo) 또는 U+EC4F | |

(글리프 코드는 빌드 시 Segoe Fluent Icons 폰트에서 시각 확인 필수 — 코드포인트 차이가 있는 항목은
근사 글리프로 대체.)

---

## 10. mac 전용 API → Windows 대응 총정리 (이 모듈 범위)

| mac API/개념 | Windows 대응 |
|---|---|
| `UserDefaults` | `%APPDATA%\XFinder\settings.json` (키 이름 유지) |
| `@Observable` / didSet | `ObservableObject` + 속성 setter에서 저장/부수효과 |
| Swift `Task` + `Task.isCancelled` | `Task.Run` + `CancellationTokenSource`; 완료 후 "여전히 같은 폴더/모드/질의인가" 가드 패턴 그대로 복제 |
| `FileManager.trashItem` | `SHFileOperation`/`IFileOperation` + `FOF_ALLOWUNDO` (휴지통) |
| Finder 위임(AppleScript)·자동화 권한(-1743) | 불필요 — 제거. 관리자 필요 시 UAC 승격 안내 |
| Quick Look (`SystemActions.quickLook`) | 자체 미리보기 창 (`AppSheet.Viewer`) |
| `NSWorkspace.open` / reveal | `Process.Start(UseShellExecute)` / `explorer.exe /select,` |
| `NSPasteboard` + changeCount | `Clipboard.SetFileDropList` + `Preferred DropEffect`; `GetClipboardSequenceNumber()` |
| `mountedVolumeURLs` + browsable/dedup | `DriveInfo.GetDrives()` (`IsReady`), dedup 불필요 |
| `volumeAvailableCapacityForImportantUsage` | `DriveInfo.AvailableFreeSpace` (백그라운드 캐시 패턴 유지 무방) |
| `NSApp.appearance` (aqua/darkAqua) | WPF 테마 리소스 교체; system = 레지스트리 `AppsUseLightTheme` 감지 + `UserPreferenceChanged` 구독 |
| `localizedStandardCompare` | `StrCmpLogicalW` P/Invoke (자연 정렬) |
| `localizedTypeDescription` | `SHGetFileInfo(SHGFI_TYPENAME)` |
| Spotlight Recents (`NSMetadataQuery`) | `%APPDATA%\Microsoft\Windows\Recent` .lnk 해석 (베스트에포트) |
| Finder 태그 xattr | NTFS ADS 또는 로컬 JSON DB (태그명/색 체계는 동일 유지) |
| `.app` 번들 / `isBundle` | 항상 false; uninstall 기능은 "프로그램 제거"로 대체 또는 제외 |
| `.Trash` 직접 탐색 | 불가 — `shell:RecycleBinFolder` 열기로 대체 |
| AirDrop | 대응 없음 — 행 제거 (또는 Windows 근거리 공유) |
| DayFlow distributed notification | 포팅 무의미 — 제거 |
| 터미널/iTerm | Windows Terminal(wt.exe)/PowerShell/cmd |
| NFD/NFC rename(정규화 비구분 볼륨) | NTFS는 실제 rename — 충돌 시 uniqueURL 회피 추가 |
| `~` 틸드 확장 (`expandingTildeInPath`) | `%USERPROFILE%` 치환 + `Environment.ExpandEnvironmentVariables` |
| fts/병렬 폴더 스캔 | `Parallel.ForEach` + `Directory.EnumerateFileSystemEntries` (접근 거부 skip) |
| ⌘ 단축키 | Ctrl 계열로 치환 (⌘⌫→Delete, ⇧⌘.→Ctrl+H, ⌘↑→Alt+↑, ⌘[/⌘]→Alt+←/→) |
