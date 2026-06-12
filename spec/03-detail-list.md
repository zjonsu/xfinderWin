# 03 — 상세 목록 (DetailView · FileDrag · FolderDrop · KeyboardMonitor)

> 원본 소스: `Sources/XFinder/Views/DetailView.swift`(전체 795줄), `Views/FileDrag.swift`,
> `Views/FolderDrop.swift`, `Services/KeyboardMonitor.swift`, `doc/keyboard.md`
> 참조(계약 확인용): `Model/Enums.swift`(ListColumn/ViewMode/SortKey/GroupKey), `Model/PaneTab.swift`(FileGroup),
> `Model/AppModel.swift`(columnWidth/typeSelect/extendCursor/selectRange/loadMoreTypeItemsIfNeeded)
>
> 이 문서는 오른쪽 상세 패널(파일 목록) 전체를 C# .NET 8 WPF로 포팅하기 위한 스펙이다.

---

## 1. 데이터 구조 (C# 매핑)

### 1.1 레이아웃 상수 `Col`

```csharp
public static class Col
{
    public const double Icon = 18;       // 행 아이콘 폭/높이 (배율 적용 전)
    public const double RowHeight = 22;  // 행 높이 (배율 적용 전)
}
```

- 모든 수치에 `listScale`(0.8~1.8, 기본 1.0)을 곱해 렌더링한다. 이하 "×s"로 표기.

### 1.2 `ListColumn` — 고정 너비 열 (이름 열은 나머지 공간 전부 차지하므로 미포함)

```csharp
public enum ListColumn { Size, Modified, Created, Kind }
// rawValue 문자열(영속화 키): "size", "modified", "created", "kind"
```

| 케이스 | defaultWidth(pt) | minWidth | maxWidth |
|---|---|---|---|
| Size | 70 | 44 | 360 |
| Modified | 120 | 44 | 360 |
| Created | 120 | 44 | 360 |
| Kind | 96 | 44 | 360 |

- `AppModel.ColumnWidth(ListColumn)` → 사용자 지정값(`columnWidths` 딕셔너리) 없으면 defaultWidth. **배율 적용 전 값**으로 저장하며, 표시 시 호출 측에서 ×s.
- `SetColumnWidth(column, width)` → `Math.Clamp(width, 44, 360)` 후 딕셔너리에 저장(저장 즉시 영속화).
- `ResetColumnWidths()` → 딕셔너리를 빈 값으로(전 열 기본 너비 복귀).

### 1.3 `ViewMode`

```csharp
public enum ViewMode { Full, Icon }   // rawValue: "full", "icon"
// Full.Label = "목록", Icon.Label = "아이콘"
```

### 1.4 `SortKey`

```csharp
public enum SortKey { Name, Ext, Size, Modified, Created, Kind }
// rawValue: "name","ext","size","modified","created","kind"
// 영문 Label(코드 내부): Name/Ext/Size/Date Modified/Date Created/Kind
// 메뉴 표시용 한국어(sortLabel): 이름/확장자/크기/수정일/생성일/종류
```

- 배경 컨텍스트 메뉴의 "정렬 기준"에는 `[name, size, kind, modified, created]`만 노출(**ext 제외**).

### 1.5 `GroupKey` — "다음으로 그룹화"

```csharp
public enum GroupKey { None, Name, Kind, Size, Modified, Created }
// rawValue: "none","name","kind","size","modified","created"
// Label: 없음/이름/종류/크기/수정일/생성일
```

### 1.6 `FileGroup` — 그룹 보기의 한 구간

```csharp
public sealed record FileGroup(string Title, Range RangeInItems);
// id = Title (제목이 곧 식별자). 항목을 복사하지 않고 items 안의 인덱스 범위만 가리킴
// → 수만 개 목록에서도 메모리 부담 없음. 헤더에는 Title + 항목 수(range.Count) 표시.
```

### 1.7 DetailView가 사용하는 `PaneTab` 필드(계약)

| 필드 | 형 | 용도 |
|---|---|---|
| `items` | `List<FileItem>` | 표시 중인 목록(정렬·그룹 반영 완료 상태) |
| `cursor` | `URL?` (C#: string path?) | 키보드 커서(단일 강조 행) |
| `selection` | `Set<URL>` | 다중 마크 선택 |
| `selectionAnchor` | `URL?` | Shift 범위 선택의 기준점 |
| `sortKey` / `sortAscending` | SortKey / bool | 정렬 상태 |
| `viewMode` | ViewMode | 목록/아이콘 |
| `groupKey` | GroupKey | 그룹 기준 |
| `activeGroups` | `List<FileGroup>?` | null이면 그룹 없음(평면 목록) |
| `searchMode` / `recentsMode` / `tagMode` / `typeMode` | bool | 가상 목록 모드 플래그 |
| `typeName`, `typeTotal` | string?, int | 파일 계산 내역(종류별) 모드 메타 |
| `loadError` | string? | 폴더 읽기 실패 메시지 |
| `directory` | URL | 현재 폴더 |
| `toggleMark(url)` | 메서드 | selection에 있으면 제거, 없으면 추가 |
| `rebuild(showHidden:)` | 메서드 | 정렬/필터 재적용 |

### 1.8 DetailView 로컬 상태

```csharp
// DetailViewModel / 코드비하인드 필드
Url?     lastClickURL;                 // 더블클릭 판정용
DateTime lastClickAt = DateTime.MinValue;
bool     suppressCursorScroll;         // 마우스 클릭으로 커서가 바뀐 직후 1회 자동 스크롤 억제
const double DoubleClickInterval = 0.3; // 초 — 시스템 기본(≈0.5s)보다 짧게
```

### 1.9 Type-select 상태(AppModel 소유)

```csharp
string   typeSelectBuffer = "";          // 자모 분해 키 누적 버퍼
DateTime typeSelectLastKey = DateTime.MinValue;
const double TypeSelectResetInterval = 1.0;  // 초
string?  TypeSelectDisplay;              // HUD 표시 텍스트(원본 입력). null = 숨김 (관찰 가능 속성)
string   typeSelectRaw = "";             // HUD용 원본 입력(분해 전)
CancellationTokenSource? typeSelectHideCts;  // 1.2초 후 HUD 자동 숨김 태스크
```

### 1.10 `FocusPane`

```csharp
public enum FocusPane { Sidebar, Detail }   // Tab 키로 토글, 키보드 라우팅 분기
```

---

## 2. UI 외형 / 레이아웃

### 2.1 전체 구조 (VStack, spacing 0)

```
┌──────────────────────────────────────────────┐
│ ColumnHeader (정렬 가능한 열 헤더)              │
├──────────────────────────────────────────────┤ ← Divider
│ ScrollView                                    │
│   목록(LazyVStack) 또는 아이콘 그리드(LazyVGrid)│
│   ⤷ overlay(bottom): type-select HUD          │
├──────────────────────────────────────────────┤ ← Divider
│ StatusBar (항목 수 · 클립보드 · 남은 공간)       │
└──────────────────────────────────────────────┘
배경: textBackgroundColor (WPF: SystemColors.WindowBrush / 다크 대응 리소스)
```

- 목록 영역에는 `FolderDropModifier(enabled: !searchMode && !recentsMode && !typeMode, folder: tab.directory)`
  가 붙는다 — 빈 영역에 드롭하면 **현재 폴더로** 이동/복사(다른 창에서 끌어와도 됨).
- HUD 표시/숨김에 `easeOut 0.15s` 애니메이션.

### 2.2 컬럼 헤더 (`ColumnHeader`)

- HStack(spacing **6**), padding 가로 **10** / 세로 **4**, 배경 `windowBackgroundColor`(목록 배경보다 약간 어두운/콘트라스트 배경).
- 구성(왼→오): `[아이콘 자리 Spacer 18×s]` `[이름(가변폭, leading)]` `[크기 70×s, trailing]` `[수정일 120×s, leading]` `[생성일 120×s, leading]` `[종류 96×s, leading]`
- 각 헤더는 버튼: 폰트 **11×s semibold**. 현재 정렬 열이면 옆에 chevron(`chevron.up`/`chevron.down`, 폰트 **7×s**).
- 클릭: 같은 열이면 `sortAscending` 토글, 다른 열이면 그 열로 바꾸고 오름차순. 이후 `tab.rebuild(showHidden)`.
- trailing 정렬 열(크기)은 Spacer가 앞에, leading 열은 Spacer가 뒤에 붙어 텍스트 정렬을 맞춘다.

#### 열 너비 조절 핸들 (`ColumnResizeHandle`)

- 각 고정 열 헤더의 **왼쪽 경계**에 오버레이. `offset(x: -7.5)` — 9pt 핸들을 6pt 열 간격 중앙에 정렬.
- 비주얼: 세로선 `separatorColor`, 1pt 폭 × 14pt 높이. 히트 영역: 폭 **9pt** × 헤더 전체 높이(투명).
- 호버 시 좌우 리사이즈 커서(WPF: `Cursors.SizeWE`).
- 드래그(`minimumDistance: 1`): 시작 시점 너비 `baseWidth`(배율 전 값)를 캡처하고,
  `SetColumnWidth(column, baseWidth - translation.X / scale)` — **오른쪽으로 끌면 열이 좁아진다**
  (핸들이 열의 왼쪽 경계이고 열들이 오른쪽 정렬이라 핸들이 마우스를 따라옴). 드래그 끝나면 baseWidth = null.
- **더블클릭**: 그 열만 `defaultWidth`로 리셋.
- 이름 열이 나머지 공간을 흡수한다(WPF: Grid에서 이름 열 `Width="*"`, 고정 열은 바인딩된 픽셀 폭).

### 2.3 목록 보기 행 (`FileRowView`)

- HStack(spacing **6**), padding 가로 **10**, 높이 **22×s**.
- 셀 구성:
  1. 파일 아이콘 18×s × 18×s (`SystemActions.swiftUIImage(for:)` — Windows: SHGetFileInfo/IShellItemImageFactory 아이콘 캐시)
  2. 이름: 폰트 **12×s**, 1줄, **가운데 말줄임**(`truncationMode(.middle)` — WPF엔 없음, 자체 컨버터로 "abc…xyz" 생성), 가변폭 leading.
     검색 모드에서는 `displayName`(검색 폴더 기준 상대 경로)을 대신 표시.
  3. 크기: `Format.size(...)`, 폰트 **11×s**, trailing, 폭 `ColumnWidth(Size)×s`, tail 말줄임.
  4. 수정일: `Format.date(modified, style)`, 폰트 11×s, secondary 색, leading, 폭 `ColumnWidth(Modified)×s`.
     상대 시간 모드(`dateStyle == relative`)일 때 툴팁(help)으로 실제 날짜 표시.
  5. 생성일: 동일 규칙, 폭 `ColumnWidth(Created)×s`, 툴팁 동일.
  6. 종류: `Format.kindLabel(item)`, 폰트 11×s, secondary, leading, 폭 `ColumnWidth(Kind)×s`.
- **배경색**:
  - 커서 행: `AccentColor.opacity(0.85)`
  - 마크 행: `AccentColor.opacity(0.22)`
  - 그 외: 짝수 행 투명, **홀수 행** `alternatingContentBackgroundColors[1]`(줄무늬. WPF: 라이트 약 `#F5F5F5`/`#0D000000`, 다크 약 `#0DFFFFFF` 수준의 리소스 정의). 줄무늬 인덱스는 그룹 보기에서도 **전역 인덱스** 기준(그룹마다 리셋 안 함).
- **전경색**: 커서 행 흰색 / 마크 행 AccentColor / 숨김 파일 secondary / 평소 primary.
  보조 텍스트(날짜·종류): 커서 행 `White.opacity(0.85)`, 그 외 secondary.

### 2.4 아이콘 보기 셀 (`IconCellView`) + 그리드

- `LazyVGrid(columns: adaptive(minimum: 112×s), spacing: 8)`, 그리드 spacing **8**, 전체 padding **12**.
  WPF: `VirtualizingWrapPanel`(서드파티 또는 자체) — 가상화 필수.
- 셀: VStack(spacing **4**), 프레임 **104×s × 104×s**(top 정렬), 상단 padding 4.
  1. `ThumbnailView(item, size: 70×s)` — 썸네일(미리보기 스펙 참조; Windows: IShellItemImageFactory).
  2. 이름: 폰트 **11×s**, 최대 2줄, 가운데 정렬, 가운데 말줄임. padding 가로 4/세로 1.
     라벨 배경: 커서면 AccentColor(코너 반경 **4**), 마크면 Accent 0.22, 평소 투명.
     전경: 커서 흰색 / 숨김 secondary / 평소 primary.
- 아이콘 보기에는 **rubberBand 콜백이 없다**(목록 보기 전용). 클릭/더블클릭/드래그/드롭/컨텍스트 메뉴는 행과 동일.

### 2.5 그룹 보기 (고정 헤더)

- `viewMode == full`: `LazyVStack(spacing: 0, pinnedViews: [.sectionHeaders])` — 섹션 헤더가 스크롤 시 상단에 **고정(sticky)**.
- `viewMode == icon`: `LazyVGrid(..., pinnedViews: [.sectionHeaders])` + padding 12.
- 그룹 헤더(`groupHeader`): HStack(spacing 6) — 제목 폰트 **11×s semibold** + 항목 수 폰트 **10×s** secondary + Spacer.
  padding 가로 10 / 세로 4, 배경 `windowBackgroundColor`, 하단에 Divider 오버레이.
- **엣지 케이스(필수 포팅)**: 그룹의 `range`로 인덱싱하기 전에 반드시 `index < items.Count` 경계 검사.
  (지연 렌더링 중 items가 교체되어 줄어들면 — 탭 전환·검색 진입 등 — 인덱스 초과 크래시.
  WPF에서도 가상화 패널 + 비동기 컬렉션 교체 시 같은 경합이 있으므로 동일 가드 적용.)
- 인덱스는 전역이므로 줄무늬·typeMode 페이징(2.10)을 그룹과 무관하게 공유.

### 2.6 상태 표시줄 (`StatusBar`)

- HStack(spacing 8), padding 가로 **10** / 세로 **3**, 배경 `windowBackgroundColor`. 폰트 전부 **11**, secondary.
- 왼쪽: 선택이 있으면 `"{items.Count}개 중 {selection.Count}개 선택"`, 없으면 `"{items.Count}개 항목"`.
- 클립보드가 차 있으면 이어서: `"· 클립보드: {n}개 (잘라냄)"` — 잘라내기일 때만 `(잘라냄)` 접미.
- 오른쪽(Spacer 뒤): `app.statusFreeSpace`(캐시된 문자열)가 있으면 `"{free} 사용 가능"`.
  남은 공간 계산은 느리므로 폴더 진입 시 백그라운드에서 갱신한 **캐시 값만** 표시(렌더 중 디스크 조회 금지).

### 2.7 Type-select HUD

- 목록 위 `overlay(alignment: .bottom)`. `TypeSelectDisplay`가 비어있지 않을 때만 표시, opacity 트랜지션(0.15s easeOut).
- HStack(spacing **7**): 키보드 아이콘(`keyboard`, 폰트 13, secondary) + 입력 텍스트(폰트 **18 semibold**, 1줄).
- padding 가로 **16** / 세로 **8**, 배경 `.ultraThinMaterial` + 코너 반경 **10**
  (WPF: 반투명 브러시 예 `#CCF5F5F5`(라이트)/`#CC2B2B2B`(다크) + CornerRadius 10; Win11이면 acrylic 선택적),
  테두리 `Primary.opacity(0.08)` 1px, 그림자 `Black 0.18, blur 8, offsetY 2`, 하단 여백 **26**.
- 히트 테스트 비활성(`IsHitTestVisible=false`).

### 2.8 빈/오류 상태

| 조건 | 표시 |
|---|---|
| `loadError != null && app.isViewingTrash` | 휴지통 전용 안내: `trash` 아이콘(32pt, secondary) + "휴지통은 Finder에서만 열 수 있습니다"(13 semibold) + "macOS가 앱의 휴지통 직접 접근을 제한합니다.\n아래 버튼으로 Finder에서 휴지통을 여세요."(11, secondary, 가운데 정렬) + 버튼 `[Finder에서 휴지통 열기]`(아이콘 arrow.up.forward.app). VStack spacing 10, minHeight 200 |
| `loadError != null` | `exclamationmark.triangle`(title2 크기) + 오류 문자열(11, secondary). spacing 8, minHeight 140 |
| `items.isEmpty` | "빈 폴더" (12, secondary, minHeight 140) |

- **Windows 노트**: 휴지통 접근 제한은 macOS 전용 문제. Windows는 Shell API(`shell:RecycleBinFolder`,
  `SHGetDesktopFolder`→Recycle Bin enumeration)로 직접 나열 가능 → 이 특수 상태는 (a) 휴지통을 읽기 전용으로
  직접 나열하거나 (b) `explorer.exe shell:RecycleBinFolder` 열기 버튼으로 대체. 문자열 차용 시
  "Finder" → "탐색기"로 치환 권장.

### 2.9 SF Symbol → Segoe Fluent Icons 매핑 제안

| SF Symbol | 용도 | Segoe Fluent/MDL2 글리프 | 대안 이모지 |
|---|---|---|---|
| trash | 휴지통 | E74D (Delete) | 🗑️ |
| exclamationmark.triangle | 오류 | E7BA (Warning) | ⚠️ |
| keyboard | HUD | E765 (KeyboardClassic) | ⌨️ |
| folder.badge.plus | 새 폴더 | E8F4 (NewFolder) | 📁+ |
| clipboard | 붙여넣기 | E77F (Paste) | 📋 |
| arrow.uturn.backward | 열 너비 재설정 | E7A7 (Undo) | ↩️ |
| eye.slash | 숨김 보기(off) | ED1A (Hide) | 🙈 |
| checkmark | 체크 표시 | E73E (CheckMark) | ✓ |
| arrow.clockwise | 새로 고침 | E72C (Refresh) | 🔄 |
| rectangle.grid.1x2 | "보기" 메뉴 | E8A9 (ViewAll) | — |
| list.bullet | 목록 보기 | E8FD (BulletedList) | ☰ |
| square.grid.2x2 | 아이콘 보기 / 다음으로 열기 | F0E2 (GridView) | ▦ |
| arrow.up.arrow.down | 정렬 기준 | E8CB (Sort) | ↕️ |
| square.grid.3x1.below.line.grid.1x2 | 그룹화 | F168 (GroupList) 또는 E8FD | — |
| chevron.up / chevron.down | 정렬 방향 | E70E / E70D | ▲ ▼ |
| arrow.up / arrow.down | 오름/내림차순 | E74A / E74B | ↑ ↓ |
| folder | 폴더 열기 | E8B7 (Folder) | 📁 |
| arrow.up.forward.app | 외부 앱으로 열기 | E8A7 (OpenInNewWindow) | ↗️ |
| plus.rectangle.on.rectangle | 새 탭에서 열기 | F5ED (또는 E710 Add) | ➕ |
| scope | 위치로 이동 | E81D (Location) 또는 F272 | 🎯 |
| magnifyingglass | 탐색기에서 보기 | E721 (Search) | 🔍 |
| info.circle | 정보 가져오기 | E946 (Info) | ℹ️ |
| tag | 태그 | E8EC (Tag) | 🏷️ |
| doc.on.doc | 복사 | E8C8 (Copy) | 📄 |
| doc.on.clipboard | 경로 복사 | F0E3 (또는 E8C8) | 📋 |
| scissors | 잘라내기 | E8C6 (Cut) | ✂️ |
| plus.square.on.square | 복제 | E8C8 + 변형 또는 F413 | ⧉ |
| pencil | 이름 변경 | E70F (Edit) | ✏️ |
| star / star.slash | 즐겨찾기 추가/제거 | E734 (FavoriteStar) / E8D9 (Unfavorite) | ⭐ |
| sparkles | AI 정리 예외 해제 | E945 (LightningBolt) 또는 ✨ | ✨ |
| hand.raised | AI 정리 예외 등록 | E72E (Lock) 또는 F140 (Blocked) | ✋ |
| archivebox | 압축 | F012 (ZipFolder) 또는 E7B8 | 🗜️ |
| doc.zipper | 압축 풀기 | F012 | 📦 |
| xmark.bin | 응용 프로그램 삭제 | E74D + 강조색 | 🗑️ |

---

## 3. 선택 모델 & 마우스 동작

### 3.1 모델

- **cursor**(단일, 짙은 강조) + **selection**(다중 마크 집합) + **selectionAnchor**(Shift 범위 기준)의 3요소.
- `".." (isParent)` 행은 선택 집합에 절대 들어가지 않으며 드래그 대상도 아니다.

### 3.2 마우스 다운(`pressDown`) — **mouse-DOWN 즉시 선택** (Finder 감각)

순서대로:
1. `app.focusedPane = .detail`.
2. **더블클릭 판정**: 같은 URL을 0.3초 내 다시 누르면 `app.open(item)` 후 판정 상태 리셋, 종료.
   아니면 `lastClickURL/lastClickAt` 갱신.
3. 커서가 바뀌는 클릭이면 `suppressCursorScroll = true` (클릭으로 목록이 점프하지 않게 — 자동 스크롤은 키보드 전용).
4. 수정키 분기:
   - **⌘(Ctrl) 클릭**: `toggleMark(url)` + cursor = item.
   - **⇧(Shift) 클릭**: `selectRange(to: item)` — 기준은 `cursor ?? items.first`, 두 인덱스 사이 전체를 selection으로. cursor = item.
   - **이미 selection에 포함된 행**: cursor만 옮기고 **selection 유지** (드래그로 그룹 전체를 옮길 수 있게. 축소는 mouse-up으로 연기).
   - **그 외(평범한 클릭)**: cursor = item, selection = [] — 즉시 단일 선택.

### 3.3 마우스 업(`clickUp`) — 드래그 없이 떼었을 때

- ⌘/⇧가 없고 selection이 비어있지 않으면: cursor = item, selection = [] (연기했던 다중 선택 축소).

### 3.4 러버밴드(목록 보기 전용)

- **선택되지 않은** 행에서 드래그 시작 → 러버밴드 다중 선택.
- 콜백 인자 `dy` = 누른 지점에서 아래로 이동한 거리(pt).
  `target = clamp(index + round(dy / rowH), 0, count-1)` → `app.selectRange(fromIndex: index, toIndex: target)`.
- `selectRange(fromIndex:toIndex:)`: lo..hi 범위에서 isParent 제외하고 selection 구성, cursor = items[hi], anchor = items[lo].
- 아이콘 보기에는 러버밴드 없음(onRubberBand 미전달).

### 3.5 드래그 소스 (`FileDrag` — AppKit 오버레이의 의미론)

- 행/셀 위에 투명 오버레이를 깔고 **왼쪽 버튼 다운만** 가로챈다. 우클릭(컨텍스트 메뉴)·스크롤·호버·드롭 호버는
  아래 SwiftUI 뷰로 그대로 통과. (WPF: PreviewMouseLeftButtonDown/Move/Up를 행 컨테이너에서 처리하면 동등.)
- `wasDraggable`은 **mouse-down 시점에**(selection 변경 전에) 캡처: `!isParent && (selection.Contains(url) || cursor == url)`.
- 이동 거리 `hypot(dx,dy) <= 4pt`면 무시(클릭으로 간주). 4pt 초과 시:
  - `wasDraggable == true` → 실제 드래그 세션 시작(한 번만).
  - 아니고 onRubberBand 있으면 → 러버밴드 갱신(연속).
- 드래그가 없었던 mouse-up → `onClick`(= clickUp).
- **드래그 페이로드** `dragURLs(item)`: item이 selection에 포함되면 마크된 전체(parents 제외), 아니면 item 하나, parent면 빈 배열.
- 드래그 비주얼: 파일별 32×32 아이콘, 다중 파일은 항목마다 **6pt씩 어긋나게**(부채꼴 스택) 마우스 중심 기준 배치.
- **드래그 연산 마스크**: 앱 내부 = copy|move(대상 폴더가 move 요청 가능), **외부 앱으로는 copy 전용**
  (밖으로 끌어도 원본이 사라지지 않게).
- `acceptsFirstMouse = true`(비활성 창에서도 첫 클릭이 동작) — Windows에서는 기본 동작이라 별도 처리 불필요.

### 3.6 드롭 대상 (`FolderDropModifier` / `FolderDrop`)

- 부착 위치: (a) 목록 전체(현재 폴더 대상, `!searchMode && !recentsMode && !typeMode`일 때만 활성),
  (b) 각 폴더 행/셀(`isDirectory && !isBundle && !isParent`일 때만 활성).
- **중요 구현 노트**: 활성 여부로 핸들러 부착/탈착을 갈아끼우지 말 것 — 원본은 뷰 정체성 유지를 위해
  항상 onDrop을 붙이고 enabled를 델리게이트 안에서 검사한다(탈착하면 하위 트리 재생성 → 검색창 포커스 강탈 버그).
  WPF: `AllowDrop=true` 고정 + 핸들러 내부에서 enabled 검사.
- `validateDrop`: enabled && 파일 URL 포함 여부.
- `dropUpdated`: **⌃(Control) 누르면 copy, 아니면 move** 제안.
  → **Windows 관례로 치환**: Ctrl = 복사, 기본 = 이동(같은 볼륨), `DragDropEffects` + 커서 피드백.
- `dropEntered`(라이브): 드래그 중인 항목이 **즐겨찾기 재정렬**(`app.draggingFavorite != null`)이고 대상도 즐겨찾기면
  즉시 `moveFavorite(fromPath:, toBefore:)`를 0.16s easeInOut 애니메이션으로 실행(행이 밀려나는 Finder 느낌).
  비-즐겨찾기 영역에 들어오면 `draggingFavorite = null`로 플래그 정리(오작동 방지).
- `performDrop`:
  1. copy 여부·폴더·draggingFavorite을 캡처하고 플래그를 즉시 null.
  2. ItemProvider들에서 파일 URL을 **비동기로** 수집(스레드 안전 누적, 완료 시 메인 스레드 콜백 —
     WPF: `DataObject.GetData(DataFormats.FileDrop)`은 동기라 단순화 가능).
  3. 떨어진 것이 방금 끌던 즐겨찾기 1개와 동일 경로면 **아무것도 안 함**(재정렬은 dropEntered에서 끝났고,
     비-즐겨찾기 위에 떨궈도 폴더를 이동시키지 않음).
  4. 그 외: `app.dropFiles(urls, onto: folder, copy: copy)` (파일 작업 스펙 참조 — 충돌 처리/진행률 포함).

### 3.7 목록 빈 영역

- 탭(클릭): 목록 포커스 획득 + `focusedPane = .detail` + **selection 해제**(커서는 유지).
- 우클릭: 배경 컨텍스트 메뉴(§5.2). 행 위 우클릭은 행 메뉴가 우선.

---

## 4. 열기 / 스크롤 / 포커스

- **열기 경로**: 더블클릭(0.3s) / Return·Enter / ⌘↓ → `app.open(item)` — 폴더(번들 제외)는 진입,
  파일·번들은 기본 앱 실행(내비게이션 스펙 참조).
- **자동 스크롤**: `cursor` 변경 시 그 행을 **가운데 앵커로** 스크롤. 단 `suppressCursorScroll`(마우스 클릭 직후)이면
  1회 건너뛰고 플래그 해제. 폴더 변경 시(텍스트 입력 중이 아니면) 목록에 포커스를 주고 커서 위치로 스크롤.
- **포커스**: 목록은 보이지 않는 키보드 포커스를 유지(포커스 링 없음 — WPF: `FocusVisualStyle=null`).
  onAppear 시 비동기로 포커스 획득하되 `app.textInputActive`(검색창·경로 입력·이름 변경 중)이면 가져오지 않는다.
- **type-select 입력 수신**: 전역 모니터가 아니라 **목록이 실제 포커스를 가질 때만** 글자/숫자를 받는다
  (⌘/⌃/⌥ 수정키가 있으면 무시). 그래서 검색창 타이핑을 절대 가로채지 않는다.
  WPF: 목록 컨트롤의 `PreviewTextInput`(IME 한글 조합 포함 — `TextComposition` 사용)으로 동등 구현.

---

## 5. 컨텍스트 메뉴

### 5.1 행(파일/폴더) 메뉴 — 항목 순서 그대로

대상 결정 `contextTargets(item)`: 우클릭한 항목이 selection에 포함되면 **마크된 전체**(parents 제외), 아니면 그 항목 하나, parent면 빈 배열.
`selectIfNeeded(item)`: 항목이 selection에 없으면 cursor=item, selection=[] 후 동작 실행.

| # | 라벨 | 아이콘 | 노출 조건 | 동작 |
|---|---|---|---|---|
| 1 | 열기 | folder(폴더·비번들) / arrow.up.forward.app(그 외) | 항상 | `app.open(item)` |
| 2 | 새 탭에서 열기 | plus.rectangle.on.rectangle | isDirectory && !isBundle | `app.newTab(folder:)` |
| 3 | 다음으로 열기 ▸ | square.grid.2x2 | !isParent | §5.3 |
| 4 | 위치로 이동 | scope | searchMode∨recentsMode∨typeMode | `app.revealInList(item)` — 실제 폴더로 이동 후 커서 |
| 5 | Finder에서 보기 | magnifyingglass | 항상 | `SystemActions.reveal(url)` → Win: `explorer.exe /select,"path"` |
| 6 | 정보 가져오기 (⌘I) | info.circle | 항상 | selectIfNeeded 후 `app.getInfoSelection()` → Win: `SHObjectProperties` 속성 대화상자 |
| 7 | 공유 ▸ | (ShareLink) | targets 비어있지 않음 | 시스템 공유 시트 → Win: `DataTransferManager`(IDataTransferManagerInterop) 베스트에포트, 불가 시 항목 숨김 |
| 8 | 태그 ▸ | tag | 항상 | §5.4 |
| — | (구분선) | | | |
| 9 | 복사 | doc.on.doc | 항상 | selectIfNeeded → `copySelection()` |
| 10 | 경로 복사 (⌥⌘C) | doc.on.clipboard | 항상 | selectIfNeeded → `copySelectedPath()` |
| 11 | 잘라내기 | scissors | 항상 | selectIfNeeded → `cutSelection()` |
| 12 | 붙여넣기 | clipboard | `app.clipboard != null` | `paste()` |
| 13 | 복제 | plus.square.on.square | 항상 | selectIfNeeded → `duplicate()` |
| 14 | 이름 변경… | pencil | 항상 | cursor=item 후 `requestRename()` — **인라인 편집이 아니라 시트(대화상자)**로 진행(시트 스펙 참조) |
| 15 | 즐겨찾기에 추가 / 즐겨찾기에서 제거 | star / star.slash | isDirectory | `addFavorite` / `removeFavorite` |
| 16 | AI 정리 예외 폴더로 등록 / AI 정리 예외 해제 | hand.raised / sparkles | isDirectory | `addExcludedFolder` / `removeExcludedFolder` (등록 시 하위 폴더 전체에서 AI 정리 차단) |
| — | (구분선) | | | |
| 17 | 압축 | archivebox | 항상 | selectIfNeeded → `compressSelected()` |
| 18 | 압축 풀기 | doc.zipper | `ext == "zip"` | cursor=item → `extractSelected()` |
| — | (구분선) | | | |
| 19 | 휴지통으로 이동 | trash (destructive 빨강) | 항상 | selectIfNeeded → `requestDelete()` (확인 대화상자) |
| 20 | 응용 프로그램 삭제… | xmark.bin (destructive) | `AppUninstaller.isApp(item)` | `requestUninstall(item)` — Win: 제어판 언인스톨 연동 또는 항목 제거 검토(앱삭제 스펙 참조) |

- **성능 계약(필수)**: 행 메뉴 콘텐츠는 행을 그릴 때마다 eagerly 평가될 수 있다.
  공유 대상·"다음으로 열기" 앱 조회 같은 **느린 시스템 호출은 메뉴가 실제로 열릴 때**(WPF: `SubmenuOpened`)로 미룬다.
  원본에서 이걸 어기자 폴더 이동마다 ~750ms 지연이 생겼다.

### 5.2 배경(빈 영역) 메뉴

`canEdit = !searchMode && !recentsMode && !tagMode && !typeMode`

| 라벨 | 아이콘 | 조건 | 동작 |
|---|---|---|---|
| 새 폴더 | folder.badge.plus | canEdit | `requestNewFolder()` |
| 붙여넣기 | clipboard | canEdit && clipboard≠null | `paste()` |
| (구분선) | | canEdit | |
| 보기 ▸ | rectangle.grid.1x2 | 항상 | 목록/아이콘 — 현재 모드에 checkmark, 비현재는 list.bullet/square.grid.2x2 아이콘. 선택 시 모드 다르면 `toggleViewMode()` |
| 정렬 기준 ▸ | arrow.up.arrow.down | 항상 | 이름/크기/종류/수정일/생성일(현재 키 checkmark) — 같은 키 재선택 시 방향 토글, 새 키는 오름차순. 구분선 아래 "오름차순"/"내림차순" 토글(arrow.up/arrow.down) |
| 다음으로 그룹화 ▸ | square.grid.3x1.below.line.grid.1x2 | canEdit | 없음/이름/종류/크기/수정일/생성일(현재 checkmark) → `setGroupKey(key)` |
| 열 너비 재설정 | arrow.uturn.backward | viewMode == full | `resetColumnWidths()` |
| 숨김 항목 보기 | showHidden ? checkmark : eye.slash | 항상 | `toggleHidden()` |
| (구분선) | | | |
| 새로 고침 | arrow.clockwise | 항상 | `reloadDetail()` |

### 5.3 "다음으로 열기" 하위 메뉴

- 열릴 때 lazily: `contextTargets` + 그 파일을 열 수 있는 앱 목록(macOS `urlsForApplications(toOpen:)`).
- 비어있으면 비활성 항목 **"열 수 있는 앱 없음"**.
- 각 앱: 앱 아이콘 + 표시 이름, 기본 앱이면 이름 뒤에 **" (기본)"**. 클릭 시 그 앱으로 대상 전체 열기.
- 구분선 + **"기타…"** → 앱 선택 패널(/Applications, 단일 선택, 버튼 "열기",
  안내문 "이 파일을 열 응용 프로그램을 선택하세요.") → 선택 앱으로 열기.
- **Windows 대응**: `SHAssocEnumHandlers`(IEnumAssocHandlers)로 확장자 핸들러 나열 + `AssocQueryString`으로 기본 앱 판별,
  실행은 `IAssocHandler.Invoke` 또는 `Process.Start(exe, files)`. "기타…" → `SHOpenWithDialog`(OAIF_EXEC).
  하위 메뉴는 반드시 `SubmenuOpened`에서 채울 것.

### 5.4 "태그" 하위 메뉴

- `TagService.standard`의 표준 색 태그들을 **컬러 점 아이콘**(non-template 비트맵)과 이름으로 나열,
  클릭 시 대상들에 토글. 구분선 아래 **"태그 모두 제거"**.
- **렌더 중 태그 상태(xattr)를 읽지 않으므로 현재 상태 체크마크는 표시하지 않는다**(목록 지연 방지) — 클릭 = 토글.
- Windows: Finder 태그(xattr) → NTFS ADS(`file:Xfinder.Tags`) 또는 로컬 JSON DB(태그 스펙 참조). 동작·메뉴 구조는 동일하게.

---

## 6. 키보드 — 전체 표 (KeyboardMonitor)

### 6.1 아키텍처 계약

- macOS: 창마다 `NSEvent` 로컬 키 모니터 설치 — **포커스와 무관하게** 모든 keyDown을 선점.
  **Windows 대응**: `Window.PreviewKeyDown`(터널링) 1곳에서 라우팅하고 처리 시 `e.Handled = true`.
  XButton은 `PreviewMouseDown`의 `MouseButton.XButton1/2`.
- **이중 실행 방지 계약**: 모니터가 처리한 키는 이벤트를 **소비**(`nil` 반환 = e.Handled) → 같은 단축키의
  메뉴(InputBinding)는 실행되지 않는다. 텍스트 필드 포커스 중에는 모니터가 비켜나고 메뉴 경로가 동작한다.
  새 단축키 추가 시 **모니터 + 메뉴 양쪽** 충돌 확인 필수.
- **다중 창**: 이벤트의 소속 창과 자기 `app.window`가 다르면 무시(창마다 모델 1개).
  WPF는 창별 PreviewKeyDown이라 자연 해결.
- **비활성 가드(우선순위 순)**:
  1. 확인 대화상자(`app.confirm != null`) 표시 중 → §6.2의 전용 키만 처리, **그 외 모든 키 삼킴**.
  2. 시트/오류/안내(`sheet`/`errorMessage`/`infoMessage`) 표시 중 → 모두 통과(해당 UI가 처리).
  3. `app.textInputActive` 또는 첫 응답자가 텍스트 입력 계열 → 모두 통과.
     (macOS는 클래스명에 "text" 포함까지 폭넓게 검사 — WPF: `Keyboard.FocusedElement is TextBoxBase/PasswordBox`로 충분.)

### 6.2 확인 대화상자 전용 키

| 키 | 동작 |
|---|---|
| ← / ↑ | `moveConfirmFocus(-1)` — 버튼 포커스 이동 |
| → / ↓ / Tab | `moveConfirmFocus(+1)` |
| Return / Enter | `executeConfirmFocus()` — 포커스된 버튼 실행 |
| Esc | `cancelConfirm()` |
| 그 외 전부 | 무시(삼킴 — 뒤의 목록이 반응하지 못하게) |

### 6.3 마우스 측면 버튼

| 버튼 | 동작 | 조건 |
|---|---|---|
| 버튼 3 (XButton1) | `goBack()` | 대화상자/시트/오류/안내 없음 |
| 버튼 4 (XButton2) | `goForward()` | 〃 |

### 6.4 전역 단축키 표 (mac 키 → 제안 Windows 키)

⌘→Ctrl, ⌥→Alt, ⇧→Shift 로 기본 치환. macOS의 ⌃(Control) 단축키는 충돌 없게 재배치 제안.

| mac | Windows 제안 | 동작 | 조건/비고 |
|---|---|---|---|
| ⌃M | Ctrl+M | `toggleViewMode()` 목록⇄아이콘 | ⌘ 미포함일 때만 |
| ⌃Tab / ⌃⇧Tab | Ctrl+Tab / Ctrl+Shift+Tab | `cycleTab(+1/-1)` 탭 순환 | |
| ⌘Q | Alt+F4 (기본 제공) | 앱 종료 | |
| ⌘T | Ctrl+T | `newTab()` | shift 미포함 |
| ⌘W | Ctrl+W | 탭 여러 개면 `closeCurrentTab()`, 마지막 탭이면 **창 닫기** | shift 미포함 |
| ⌘M | (생략 — Win 최소화는 Win+↓) | 창 최소화 | |
| ⌘↑ | Alt+↑ | `goUp()` 상위 폴더 | 탐색기 관례 |
| ⌘↓ | Alt+↓ 또는 Ctrl+Enter | `openSelected()` 열기 | |
| ⌘← / ⌘[ | Alt+← | `goBack()` | |
| ⌘→ / ⌘] | Alt+→ | `goForward()` | |
| ⌘⌫ | Delete | `requestDelete()` 휴지통 이동(확인) | Win 관례: Delete=휴지통, Shift+Delete=영구(영구 삭제는 파일작업 스펙) |
| ⌥⌘C | Ctrl+Shift+C | `copySelectedPath()` 경로 복사 | 탐색기 관례와 일치 |
| ⌘C | Ctrl+C | `copySelection()` | shift 미포함 |
| ⌘X | Ctrl+X | `cutSelection()` | |
| ⌘V | Ctrl+V | `paste()` | |
| ⌘D | Ctrl+D | `duplicate()` 복제 | Win에서 Ctrl+D는 종종 삭제 관례 — 충돌 검토 후 Ctrl+Shift+D 대안 |
| ⌘I | Alt+Enter | `getInfoSelection()` 속성 | 탐색기 관례 |
| ⌘R / F5 | F5 / Ctrl+R | `refresh()` | |
| ⇧⌘N | Ctrl+Shift+N | `requestNewFolder()` | 탐색기 관례와 일치 |
| ⇧⌘G | Ctrl+L 또는 Ctrl+Shift+G | `requestGoToFolder()` 경로 입력 | |
| ⇧⌘. | Ctrl+H | `toggleHidden()` 숨김 토글 | |
| ⌘A | Ctrl+A | 전체 선택 — **모니터는 통과시키고 메뉴가 처리** | WPF: InputBinding으로 구현 |

### 6.5 포커스 패널 전환·사이드바 키 (수정키 없음)

| 키 | 동작 | 조건 |
|---|---|---|
| Tab / ⇧Tab (⌃ 없이) | `toggleFocusedPane()` 사이드바⇄목록 | 텍스트 입력 아님 |
| ↑ / ↓ | `moveSidebarSelection(∓1)` | focusedPane == sidebar |
| → | `expandSidebarSelection()` 펼침/자식 진입 | 〃 |
| ← | `collapseSidebarSelection()` 접기/부모로 | 〃 |
| Return / Enter / Space | `activateSelectedSidebar()` 열고 목록 포커스 | 〃 |

### 6.6 목록 커서 키 (focusedPane == detail, 수정키 없음)

| 키 (mac keyCode) | 동작 |
|---|---|
| ↑ (126) / ↓ (125) | `moveCursor(∓1)` — **selection 해제** + anchor=cursor |
| PageUp (116) / PageDown (121) | `moveCursor(∓15)` — 고정 15행 점프 |
| Home (115) / End (119) | `cursorToTop()` / `cursorToBottom()` — selection 해제 |
| Return / Enter (36/76) | `openCursorItem()` |
| Space (49) | `viewSelected()` — Quick Look → **Win: 자체 미리보기 창** |
| Backspace (51) | `goUp()` 상위 폴더 (탐색기 관례와 동일) |
| F2 (120) | `requestRename()` 이름 변경 시트 |
| F3 (99) | `viewSelected()` 미리보기 |
| F4 (118) | `editSelected()` 기본 앱으로 열기 |
| F5 (96) | `refresh()` |
| 글자/숫자 | (모니터에서 처리 안 함) 목록 포커스 시 type-select §7 |

### 6.7 Shift+커서 — 범위 선택 확장 (⌘ 없음)

| 키 | 동작 |
|---|---|
| ⇧↑ / ⇧↓ | `extendCursor(∓1)` |
| ⇧PageUp / ⇧PageDown | `extendCursor(∓15)` |
| ⇧Home / ⇧End | `extendCursorToTop()` / `extendCursorToBottom()` |

`extendCursor` 의미: anchor가 없으면 `cursor ?? first`로 설정 → 커서를 새 인덱스로 이동(클램프) →
`selection = anchor..커서` 범위 전체(isParent 제외). 일반 화살표를 누르면 selection이 해제되고 anchor가 커서로 리셋.

---

## 7. Type-select (글자 입력으로 파일 찾기)

### 7.1 입력 조건

- 목록이 키보드 포커스를 가질 때, ⌘/⌃/⌥ 수정키 없이 들어온 문자. 첫 문자가 **letter 또는 number**가 아니면 무시.
- items가 비어 있으면 (처리한 것으로 치고) 종료.
- WPF: `PreviewTextInput` + IME 한글 조합 이벤트로 수신. KeyDown의 Key 코드가 아니라 **조합된 문자**를 받아야 한다.

### 7.2 알고리즘 (정확히 이대로)

1. 입력 문자열을 `jamoKey()`로 정규화(§7.3) → `key`.
2. `expired = (now - typeSelectLastKey) > 1.0s`; lastKey 갱신.
3. **같은 키 반복**(`!expired && buffer == key`, 즉 버퍼가 그 글자 하나일 때):
   같은 접두어 일치 항목들 사이에서 **현재 커서 다음 항목으로 순환**(매칭 인덱스 중 `> 현재` 첫 항목, 없으면 첫 매칭).
   HUD에 `typeSelectRaw` 표시 후 종료.
4. 아니면 버퍼 누적: `buffer = expired ? key : buffer + key`, `raw = expired ? chars : raw + chars`. HUD 갱신.
5. 대상: `items.First(i => !i.isParent && jamoKey(i.name).StartsWith(buffer))`.
   없으면 **사전순 후속자**: `jamoKey(name) > buffer`인 항목 중 키가 최소인 것, 그것도 없으면(예: "zzz") 키가 최대인 항목.
6. 대상이 있으면 `cursor = target; selection = []; anchor = target` → onChange(cursor)로 자동 가운데 스크롤.

### 7.3 `jamoKey(string)` — 한글 자모 분해 비교 키

- 입력을 **NFC 정규화 + 소문자화** 후 문자 단위 처리:
  - 한글 음절(U+AC00~U+D7A3): `idx = code - 0xAC00`;
    초성 `choseong[idx / 588]`, 중성 `jungseong[(idx % 588) / 28]`, 종성 `jongseong[idx % 28]` 순서로 이어붙임.
    - choseong(19): `ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ`
    - jungseong(21): `ㅏ ㅐ ㅑ ㅒ ㅓ ㅔ ㅕ ㅖ ㅗ ㅗㅏ ㅗㅐ ㅗㅣ ㅛ ㅜ ㅜㅓ ㅜㅔ ㅜㅣ ㅠ ㅡ ㅡㅣ ㅣ`
      (겹모음은 **타이핑 순서로 분해**: ㅘ→ㅗㅏ 등)
    - jongseong(28): `"" ㄱ ㄲ ㄱㅅ ㄴ ㄴㅈ ㄴㅎ ㄷ ㄹ ㄹㄱ ㄹㅁ ㄹㅂ ㄹㅅ ㄹㅌ ㄹㅍ ㄹㅎ ㅁ ㅂ ㅂㅅ ㅅ ㅆ ㅇ ㅈ ㅊ ㅋ ㅌ ㅍ ㅎ`
  - **단독 겹자모**도 분해 테이블로: ㅘ→ㅗㅏ, ㅙ→ㅗㅐ, ㅚ→ㅗㅣ, ㅝ→ㅜㅓ, ㅞ→ㅜㅔ, ㅟ→ㅜㅣ, ㅢ→ㅡㅣ,
    ㄳ→ㄱㅅ, ㄵ→ㄴㅈ, ㄶ→ㄴㅎ, ㄺ→ㄹㄱ, ㄻ→ㄹㅁ, ㄼ→ㄹㅂ, ㄽ→ㄹㅅ, ㄾ→ㄹㅌ, ㄿ→ㄹㅍ, ㅀ→ㄹㅎ
  - 그 외 문자는 그대로.
- 이렇게 하면 키보드에서 자모(ㅁ, ㅜ…)로 들어오는 입력과 파일명("문서.pdf")의 접두어 비교가 맞는다.
  **초성만 쳐도 매칭**("ㅁ" → "문서.pdf"). 파일명이 NFD(분해형)여도 NFC 정규화로 동일 결과.
- C# 포팅: `s.Normalize(NormalizationForm.FormC).ToLowerInvariant()` 후 동일 테이블 — 그대로 이식 가능.

### 7.4 HUD 수명

- 입력마다 `TypeSelectDisplay = raw` 갱신 + 기존 숨김 태스크 취소 → **1.2초** 뒤(취소 안 됐으면) null로 숨김.
  C#: `CancellationTokenSource` 교체 + `Task.Delay(1200, token)`.

---

## 8. 파일 계산 내역(typeMode) 무한 스크롤

- 행/셀이 화면에 나타날 때(`onAppear` — WPF: 스크롤 이벤트에서 마지막 실현 인덱스 검사 또는 ItemContainerGenerator):
  `currentIndex >= items.Count - 100`이고 `typeLoaded < typeEntries.Count`면 다음 페이지 append.
- 페이지 항목은 경로·크기만 채워서 즉시 추가(`modified = DateTime.MinValue`, isDirectory=false 등 기본값),
  수정일·생성일·종류 메타데이터는 **백그라운드에서 보강** 후 경로 매칭으로 갱신.
- typeMode에서는 편집성 드롭/배경 편집 메뉴(새 폴더·붙여넣기)/그룹화 메뉴가 비활성(§3.6, §5.2).

---

## 9. 영속화 (UserDefaults → Windows 제안: `%APPDATA%\XFinder\settings.json`)

| 키 | 형식 | 내용 |
|---|---|---|
| `XFinder.columnWidths.v1` | `Dictionary<string,double>` | 사용자 지정 열 너비. 키 = ListColumn rawValue(`"size"/"modified"/"created"/"kind"`), 값 = pt(배율 적용 전). 없는 키는 기본 너비. "열 너비 재설정" = 빈 딕셔너리 저장. 변경 즉시 저장 |
| `XFinder.listScale.v1` | Double | 목록 크기 배율. 로드 시 0.8~1.8 클램프, 기본 1.0 (설정 스펙 소유 — 본 뷰의 모든 ×s에 사용) |
| `XFinder.folderViewModes.v1` | (다른 스펙) | 폴더별 viewMode 기억 — 본 뷰의 보기 전환과 연동 |
| `XFinder.dateStyle.v1` | (다른 스펙) | absolute/relative — 행의 날짜 표시·툴팁에 사용 |

---

## 10. Windows 포팅 노트 (mac 전용 API → 대응)

| mac API/개념 | Windows 대응 |
|---|---|
| `NSEvent.addLocalMonitorForEvents`(로컬 키 모니터) | `Window.PreviewKeyDown`/`PreviewMouseDown` 터널링 + `e.Handled`. 비활성 가드(텍스트 포커스/대화상자) 동일 적용 |
| `NSEvent.modifierFlags`(클릭 시 수정키) | `Keyboard.Modifiers` (ModifierKeys.Control/Shift) |
| `NSDraggingSession`(커스텀 드래그 소스) | `DragDrop.DoDragDrop(element, dataObject, Copy\|Move)` + `DataObject.SetFileDropList`. 32px 아이콘 부채꼴 스택은 `IDragSourceHelper`(SHDoDragDrop) 또는 Adorner로 재현(생략 가능) |
| 내부 copy\|move / 외부 copy-only 마스크 | DoDragDrop allowedEffects를 Copy\|Move로 주되, 외부 창 드롭은 OS가 효과 결정 — "밖으로 끌면 복사" 보장은 DataObject에 `Preferred DropEffect=COPY` 힌트 추가로 베스트에포트 |
| `onDrop` + `NSItemProvider` 비동기 URL 로드 | `Drop` 이벤트 + `e.Data.GetData(DataFormats.FileDrop)`(동기, string[]) — 간단해짐 |
| ⌃ 누르면 copy(mac 드롭) | **Windows 관례로 교체**: Ctrl=복사, 기본=이동(`DragEventArgs.KeyStates`) |
| `NSWorkspace.icon(forFile:)` | `SHGetFileInfo` / `IShellItemImageFactory` + 확장자별 캐시 |
| Quick Look (`SystemActions.quickLook`) | **자체 미리보기 창**(이미지/텍스트/PDF — 미리보기 스펙). Space/F3 토글 |
| `NSWorkspace.open`/`urlsForApplications(toOpen:)` | `Process.Start(new ProcessStartInfo(path){UseShellExecute=true})`; 앱 목록은 `SHAssocEnumHandlers`, 기본 앱은 `AssocQueryString` |
| `NSOpenPanel`(앱 선택 "기타…") | `SHOpenWithDialog`(OAIF_EXEC) 권장 — 직접 패널 불필요 |
| `SystemActions.reveal`(Finder에서 보기) | `explorer.exe /select,"C:\path"` (라벨 "탐색기에서 보기"로 변경) |
| 정보 가져오기(Finder Get Info) | `SHObjectProperties` 셸 속성 대화상자 |
| 휴지통(requestDelete) | `IFileOperation`/`SHFileOperation` + `FOF_ALLOWUNDO` (파일작업 스펙) |
| 휴지통 보기 제한(Finder만 열림) | **해당 없음** — Shell로 직접 나열 가능. 특수 화면은 explorer 열기 버튼으로 대체하거나 제거 |
| `ShareLink`(공유) | `DataTransferManagerInterop.ShowShareUIForWindow` 베스트에포트; 실패 환경(서버 SKU 등)에선 메뉴 숨김 |
| Finder 태그(`TagService`, xattr) | NTFS ADS 또는 로컬 JSON DB(태그 스펙). 메뉴 UI는 동일 |
| `alternatingContentBackgroundColors` | 자체 리소스(라이트 `#FFF5F5F5` 근사 / 다크 `#0DFFFFFF`) — 테마 사전으로 분리 |
| `.ultraThinMaterial` | 반투명 브러시(+선택적 Win11 acrylic). 수치는 §2.7 |
| `NSCursor.resizeLeftRight` | `Cursors.SizeWE` |
| `pinnedViews: .sectionHeaders`(고정 그룹 헤더) | `ListView` GroupStyle + sticky 헤더 비헤이비어(스크롤 오프셋 따라 헤더를 Canvas 상단 고정) 자체 구현 |
| `LazyVStack`/`LazyVGrid` 가상화 | `VirtualizingStackPanel`(Recycling) / VirtualizingWrapPanel. **그룹+가상화 경계 검사**(§2.5) 유지 |
| `.onKeyPress`(포커스 기반 type-select) | 목록 컨트롤 `PreviewTextInput`(+IME TextComposition) |
| `acceptsFirstMouse` | 불필요(Windows 기본 동작) |
| `focusEffectDisabled` | `FocusVisualStyle=null` |
| AppKit 오버레이 hitTest 트릭 | 불필요 — WPF는 행 컨테이너에서 Preview 마우스 이벤트로 클릭/드래그 판별(4px 임계는 `SystemParameters.MinimumHorizontalDragDistance` 사용 가능) |
| 마우스 버튼 3/4 (otherMouseDown) | `MouseButton.XButton1/XButton2` |
| `truncationMode(.middle)` | WPF TextTrimming에 middle 없음 → 측정 기반 "앞…뒤" 문자열 컨버터 자체 구현 |
| `.help()` 툴팁 | `ToolTip` (relative 날짜 모드에서만 설정) |

**포팅 불가/무의미**: macOS 휴지통 접근 제한 안내 화면(대체: §2.8), ⌘M 최소화(OS 제공), AirDrop 공유 대상(Windows 공유 시트가 대체).

---

## 11. UI 문자열 전체 목록 (원문 그대로 사용)

빈/오류 상태: `휴지통은 Finder에서만 열 수 있습니다` · `macOS가 앱의 휴지통 직접 접근을 제한합니다.\n아래 버튼으로 Finder에서 휴지통을 여세요.` · `Finder에서 휴지통 열기` · `빈 폴더`

배경 메뉴: `새 폴더` · `붙여넣기` · `보기` · `정렬 기준` · `다음으로 그룹화` · `열 너비 재설정` · `숨김 항목 보기` · `새로 고침` · `오름차순` · `내림차순`

보기/정렬/그룹 라벨: `목록` · `아이콘` · `이름` · `확장자` · `크기` · `수정일` · `생성일` · `종류` · `없음`

행 메뉴: `열기` · `새 탭에서 열기` · `다음으로 열기` · `위치로 이동` · `Finder에서 보기` · `정보 가져오기` · `공유` · `태그` · `태그 모두 제거` · `복사` · `경로 복사` · `잘라내기` · `복제` · `이름 변경…` · `즐겨찾기에 추가` · `즐겨찾기에서 제거` · `AI 정리 예외 폴더로 등록` · `AI 정리 예외 해제` · `압축` · `압축 풀기` · `휴지통으로 이동` · `응용 프로그램 삭제…`

다음으로 열기: `열 수 있는 앱 없음` · ` (기본)` (이름 접미) · `기타…` · `열기` (패널 버튼) · `이 파일을 열 응용 프로그램을 선택하세요.`

열 헤더: `이름` · `크기` · `수정일` · `생성일` · `종류`

상태 표시줄: `{n}개 항목` · `{n}개 중 {m}개 선택` · `· 클립보드: {n}개 (잘라냄)` · `{free} 사용 가능`

※ "Finder"가 들어간 문자열은 Windows에서 "탐색기"로 치환 권장(예: `탐색기에서 보기`, `탐색기에서 휴지통 열기`).
