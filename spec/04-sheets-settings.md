# 04 — 시트/대화상자 · 설정 창 · 앱 완전 삭제 포팅 스펙

대상 소스:
- `Sources/XFinder/Views/Sheets.swift` (ViewerSheet, GoToFolderSheet, NewFolderSheet, RenameSheet, ProgressSheet, AboutSheet, ManualSheet, KeyCap)
- `Sources/XFinder/Views/SettingsWindow.swift` (SettingsWindowPresenter)
- `Sources/XFinder/Views/RootView.swift` 中 `SettingsView`, `ConfirmDialog`, 시트/알림 표시부 (501~784행, 51~81행, 183~231행)
- `Sources/XFinder/Views/UninstallSheet.swift`, `Sources/XFinder/Services/AppUninstaller.swift`
- `Sources/XFinder/Model/AppModel.swift` 中 `AppSheet`, `ConfirmRequest`, `OperationProgress`, `performUninstall`, `createFolder`, `rename`, 설정 프로퍼티/UserDefaults 키
- `doc/app-uninstall.md`, `doc/ui-appearance.md`

WPF 기준 제안: 시트 = 오너 창 중앙의 모달 `Window`(WindowStyle=None, 둥근 테두리) 또는 창 내 오버레이(ContentDialog 스타일). mac SwiftUI `.sheet`는 창에 부착된 모달이므로 **오버레이 방식**(창 안에 디밍 + 카드)이 가장 비슷하다. 앱 전체 폰트는 SF Rounded → Windows에서는 "Segoe UI Variable" 권장.

---

## 1. 공통 데이터 구조 (C# 변환 기준)

### 1.1 AppSheet (현재 표시 중인 시트, AppModel.swift 21~45행)

```csharp
// Swift: enum AppSheet: Identifiable — 연관값 포함. C#은 추상 레코드 계층으로.
public abstract record AppSheet(string Id)
{
    public sealed record Viewer(FileItem Item)    : AppSheet($"viewer:{Item.Path}");
    public sealed record GoToFolder()             : AppSheet("goto");
    public sealed record NewFolder()              : AppSheet("newfolder");
    public sealed record Rename(FileItem Item)    : AppSheet($"rename:{Item.Path}");
    public sealed record Progress(OperationProgress P) : AppSheet("progress");
    public sealed record About()                  : AppSheet("about");
    public sealed record Manual()                 : AppSheet("manual");
    public sealed record Uninstall(FileItem Item) : AppSheet($"uninstall:{Item.Path}");
    public sealed record AiOrganize()             : AppSheet("aiOrganize");
}
// AppModel.Sheet : AppSheet? — null이면 시트 없음. 값이 바뀌면 해당 시트를 모달로 표시.
```

표시 위치(RootView 51~62행): `app.sheet`가 set되면 switch로 해당 뷰를 시트로 띄움. 시트 닫기 = `app.Sheet = null` (SwiftUI `dismiss()`와 동일).

### 1.2 ConfirmRequest (커스텀 확인 대화상자, AppModel.swift 47~54행)

```csharp
public sealed class ConfirmRequest
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title;          // 굵은 제목
    public string Message;        // 본문(여러 줄 가능)
    public string ConfirmTitle;   // 확인 버튼 라벨 (예: "삭제", "자동화 설정 열기")
    public bool IsDestructive;    // true면 확인 버튼이 빨간색
    public Action Action;         // 확인 시 실행
}
// AppModel.Confirm : ConfirmRequest? — null 아니면 오버레이 표시.
// AppModel.ConfirmFocus : int — 0 = 확인 버튼, 1 = 취소 버튼 (키보드 포커스)
```

키보드 계약(AppModel 1505~1518행, KeyboardMonitor가 구동):
- `←→/↑↓/Tab`: `ConfirmFocus = (ConfirmFocus + delta + 2) % 2` — 두 버튼 사이 순환.
- `Enter`: `ExecuteConfirm(ConfirmFocus)` — 인덱스 0이면 `Action()` 실행, 어느 쪽이든 `Confirm = null`.
- `Esc` / 배경(디밍) 클릭: `CancelConfirm()` → `Confirm = null`, Action 실행 안 함.

### 1.3 OperationProgress (AppModel.swift 7~19행)

```csharp
public sealed class OperationProgress : INotifyPropertyChanged
{
    public string Title;                 // 예: "복사 중…", 생성자 인자
    public string CurrentFile = "";      // 현재 처리 중 파일 이름/경로
    public long CompletedUnits = 0;
    public long TotalUnits = 0;
    public bool IsCancelled = false;     // 취소 버튼이 true로 set — 작업 루프가 폴링해 중단
    public double Fraction => TotalUnits <= 0 ? 0 : Math.Min(1.0, (double)CompletedUnits / TotalUnits);
}
```

취소 모델: CancellationTokenSource 대신 **플래그 폴링**이 원본 동작(작업 스레드가 파일 1개 처리할 때마다 `IsCancelled` 확인). C#에서는 `volatile bool` 또는 `CancellationTokenSource`로 구현해도 무방 — 단, 취소 시 "이미 복사된 파일은 남는다"(롤백 없음)는 동작 유지.

### 1.4 오류/안내 알림 (RootView 70~77행)

- `AppModel.ErrorMessage : string?` → null 아니면 표준 알림창. 제목 **"오류"**, 버튼 **"확인"** 1개.
- `AppModel.InfoMessage : string?` → 제목 **"XFinder"**, 버튼 **"확인"** 1개.
- 닫으면 해당 프로퍼티를 null로 리셋. WPF: `MessageBox` 또는 동일 스타일 커스텀 오버레이(권장 — 앱 톤 유지).

---

## 2. ViewerSheet — 파일 미리보기 (Quick Look 폴백)

Space/F3의 기본 동작은 macOS 네이티브 Quick Look(`SystemActions.quickLook`)이고, 이 시트는 **폴백 뷰어**다. Windows에는 Quick Look이 없으므로 **이 시트를 Space/F3의 기본 미리보기 창으로 승격**한다.

### 데이터
```csharp
enum ViewerContent { Loading, Image(BitmapSource), Text(string), None(string message) }
```

### UI 구조 (고정 640×520)
```
VStack(spacing 0)
├ HStack (padding 10)
│  ├ Text(item.name)  — headline(굵게, ~13pt), lineLimit 1
│  ├ Spacer
│  ├ Button "앱으로 열기"  → SystemActions.open(url)   // Windows: Process.Start(explorer 연결 프로그램)
│  └ Button "닫기" (기본 버튼 = Enter) → dismiss
├ Divider
└ 콘텐츠 영역
   ├ Loading: 중앙 스피너(ProgressRing)
   ├ Image: 양방향 ScrollView 안에 scaledToFit 이미지 + padding
   ├ Text: 세로 ScrollView, 모노스페이스 12pt, 텍스트 선택 가능, padding 10, 좌측 정렬
   └ None: 중앙 VStack(spacing 8) — 아이콘 "doc"(문서, 회색, largeTitle≈26pt) + 회색 메시지
```

### 로딩 로직 (비동기, UI 스레드 밖 — Task.detached와 동일하게 Task.Run)
1. 확장자(소문자)가 `{png, jpg, jpeg, gif, bmp, tiff, tif, heic, webp, icns}`에 있으면 이미지 로드 시도. 성공하면 `.Image`.
   - Windows: WIC(BitmapImage)로 png/jpg/gif/bmp/tiff/webp 처리. `heic`는 HEIF 확장 설치 시만, `icns`는 미지원 → 실패 시 텍스트 시도로 폴백되는 구조 유지. `.ico`를 추가해도 좋음.
2. 아니면 파일 앞부분 **최대 1,000,000바이트**를 읽어 UTF-8 디코딩 성공 시 `.Text`.
   - C#: `FileStream`으로 1MB만 읽고 `Encoding.UTF8.GetString` — 엄격 검증을 원하면 `new UTF8Encoding(false, true)` + try/catch.
3. 둘 다 실패 시 `.None` 메시지: **"“{파일명}”은(는) 미리 볼 수 없습니다."**
4. `task(id: item.url)`: 항목이 바뀌면 재로딩 — 뷰어가 열린 채 다른 파일로 전환될 수 있으면 이전 로드는 무시(최신 요청만 반영).

엣지 케이스: 큰 파일/느린 볼륨에서도 UI가 멈추지 않아야 함(반드시 백그라운드 스레드). 1MB 초과 텍스트는 잘려서 표시됨(원본도 동일 — 별도 표시는 없음).

---

## 3. GoToFolderSheet — 폴더로 이동 (⇧⌘G → Windows: Ctrl+Shift+G 제안)

### UI (VStack 좌측 정렬, spacing 10, padding 16)
- 제목: **"폴더로 이동"** (headline)
- TextField: placeholder **"/경로/폴더  또는  ~/Documents"**, roundedBorder, 폭 420
- 버튼 행(우측 정렬): **"취소"**(Esc) · **"이동"**(Enter 기본 버튼)

### 동작
- 열릴 때 텍스트에 `app.SelectedFolder.Path`(현재 폴더 절대경로) 채움 + 즉시 포커스(원본은 다음 런루프에서 focused = true; WPF는 Loaded에서 `Keyboard.Focus`).
- Enter / "이동": `app.GoToFolder(path)` 호출 후 닫기. 경로 해석(`~` 확장, 존재 검증, 오류 메시지)은 AppModel 담당(다른 영역 스펙).
- Windows 노트: `~` → `%USERPROFILE%` 확장 지원 유지. placeholder는 `C:\경로\폴더  또는  ~\Documents` 식으로 현지화 고려(문자열은 가급적 원문 유지하되 경로 예시만 Windows식 허용).

---

## 4. NewFolderSheet — 새 폴더 (⇧⌘N / F7 → Windows: Ctrl+Shift+N)

### UI (VStack spacing 10, padding 16)
- 제목: **"새 폴더"** (headline)
- TextField: placeholder **"폴더 이름"**, 초기값 **"제목 없는 폴더"**, roundedBorder, 폭 320, 자동 포커스 (가능하면 전체 선택 상태로)
- 버튼: **"취소"**(Esc) · **"생성"**(Enter)

### 동작 — `AppModel.CreateFolder(name)` (AppModel 1524~1541행)
1. trim 후 비어 있으면 무시(시트는 닫힘).
2. `WindowsName.Sanitize(trimmed)` — 금지 문자/예약 이름 정리(별도 스펙 영역; Windows에선 그대로 핵심 검증 로직이 됨: `\/:*?"<>|`, CON/PRN/AUX/NUL/COM1~9/LPT1~9, 끝의 점/공백).
3. 현재 폴더 아래 디렉터리 생성(중간 폴더 생성 안 함). 성공 시 목록 리로드 + 사이드바 갱신 + **새 폴더로 커서 이동**.
4. sanitize로 이름이 바뀌었으면 안내: **"윈도우 호환을 위해 폴더명을 “{safe}”(으)로 저장했습니다."**
5. 실패 시(이미 존재/권한): **"폴더를 만들 수 없습니다: {오류 설명}"** (ErrorMessage 알림).

---

## 5. RenameSheet — 이름 변경 (F2)

### UI (VStack spacing 10, padding 16)
- 제목: **"이름 변경"** (headline)
- TextField: placeholder **"이름"**, 초기값 = 현재 항목 이름(확장자 포함), 폭 320, 자동 포커스. (개선 여지: Windows 탐색기처럼 확장자 제외 선택 — 원본은 안 하므로 선택 사항)
- 버튼: **"취소"**(Esc) · **"변경"**(Enter)

### 동작 — `AppModel.Rename(item, newName)` (AppModel 1543~1561행)
1. trim 후 비었거나 기존 이름과 같으면 무시.
2. `WindowsName.Sanitize` 적용; sanitize 결과가 기존 이름과 같아져도 무시.
3. 같은 폴더 내 move. 성공 시 리로드 + 커서를 새 이름 항목으로.
4. 이름이 바뀌었으면: **"윈도우 호환을 위해 이름을 “{safe}”(으)로 저장했습니다."**
5. 실패: **"이름을 바꿀 수 없습니다: {오류 설명}"**
- 호출 경로: `RequestRename()` — 커서 항목이 있을 때만 시트 오픈.

---

## 6. ProgressSheet — 작업 진행률 (복사/이동/압축 등)

### UI (VStack 좌측 정렬, spacing 12, padding 20; 내용 폭 360)
- `progress.Title` (headline) — 예: "복사 중…"
- ProgressBar(value = `Fraction`, 0~1), 폭 360
- `CurrentFile` — 11pt 회색, 1줄 제한, 폭 360 좌측 정렬
- HStack(폭 360): 좌측 `"{Completed} / {Total}"` (11pt 회색) · 우측 **"취소"** 버튼 → `progress.IsCancelled = true`

### 동작/엣지
- 닫기 버튼/Esc 없음 — 취소만 가능. 작업 완료 시 AppModel이 `Sheet = null`로 직접 닫음. WPF: 오버레이 카드에서 바깥 클릭으로 닫히지 않게 할 것(`interactiveDismissDisabled` 상당).
- 취소를 눌러도 시트는 즉시 닫히지 않고, 작업 루프가 플래그를 보고 중단한 뒤 닫는다.
- 단위는 파일 개수 또는 바이트 — Title과 함께 작업 쪽에서 결정(다른 영역 스펙).

---

## 7. ConfirmDialog — 커스텀 확인 오버레이 (RootView 183~231행)

표준 MessageBox가 아니라 **창 내 오버레이**다. WPF에선 루트 Grid 최상단 레이어로 구현.

### UI
```
ZStack
├ 디밍: 검정 28% 전체 덮음 — 클릭 시 취소
└ 카드 (폭 320, padding 20, cornerRadius 16 continuous, 반투명 머티리얼 배경,
        테두리 stroke Color.primary 10%, 그림자 black 30% radius 24 y+8)
   ├ Title — 15pt bold, 중앙
   ├ Message — 12pt 회색, 중앙 정렬, 여러 줄
   └ HStack(spacing 10, 상단 padding 2)
      ├ [0] ConfirmTitle 버튼 — destructive면 빨강 배경+흰 글씨, 아니면 회색(secondary 28%) 배경+기본 글씨
      └ [1] "취소" 버튼 — 항상 회색 배경
```
버튼 공통: 13pt semibold, maxWidth 균등 분할, 세로 padding 8, cornerRadius 9.
포커스 표시: 포커스된 버튼에 **AccentColor 3px stroke** 테두리. destructive 버튼은 비포커스 시 배경 불투명도 0.75, 포커스 시 1.0.
등장 애니메이션: opacity 트랜지션.

### Windows 노트
- WPF에는 `.regularMaterial`이 없음 — `SolidColorBrush`(라이트 #F2FFFFFF / 다크 #F22C2C2E) 또는 Acrylic(Windows 11 Mica/Acrylic backdrop은 창 단위라 카드에는 단색+블러 효과 어려움; 단색 반투명 권장).
- 키 처리: 오버레이가 떠 있는 동안 메인 키 입력(파일 목록 이동 등)을 차단하고 ←→/↑↓/Tab/Enter/Esc만 처리 (원본은 KeyboardMonitor가 전역 처리).

### 사용 예 (이 영역에서 확인된 곳)
- 앱 삭제 실패 + 자동화 권한 거부 시(§11.4).
- 한글 자소 복원 미리보기, 휴지통 삭제 확인 등(다른 영역 스펙) — 모두 같은 ConfirmRequest 채널 사용.

---

## 8. AboutSheet — 앱 정보 (고정 폭 420, 높이 내용 맞춤)

### UI 구조
```
VStack(spacing 0)
├ 그라데이션 헤더 (높이 92)
│  배경: LinearGradient topLeading→bottomTrailing
│         시작 RGB(0.21, 0.55, 0.99) = #368CFC?? → 정확히 #368CFD ≈ (54,140,253)
│         끝   RGB(0.49, 0.31, 0.96) = (125,79,245) ≈ #7D4FF5
│  HStack(spacing 14, 좌우 padding 22)
│  ├ 앱 아이콘 52×52, 그림자(black 25%, radius 6, y+3)
│  └ VStack: "XFinder" 20pt bold 흰색 / 버전 11pt 흰색 85%
├ 본문 VStack(spacing 14, 좌우 padding 24, 상하 18)
│  ├ "사이드바 + 상세 보기 파일 관리자" — 12pt 회색
│  ├ 개발자 카드 (padding 10, cornerRadius 12, 배경 primary 6%)
│  │  HStack(spacing 8): 원형 30×30 인디고→파랑 그라데이션 + 아이콘 "chevron.left.forwardslash.chevron.right"(12pt bold 흰색)
│  │  "정종수"(13pt semibold) · "Developer"(11.5pt 회색) · "zjonsu@gmail.com"(11.5pt 파랑)
│  │  이메일은 일반 텍스트 + 클릭 시 mailto: 열기 + 호버 시 손가락 커서 (포커스 테두리 없음)
│  ├ 시(詩) 블록: "담쟁이" 14pt semibold / "— 도종환" 10pt 회색
│  │  본문: AppleMyungjo 13pt, primary 70%, 중앙 정렬, 줄간격 5
│  └ "확인" 버튼 (Enter 기본 버튼) → 닫기
```

### 버전 문자열
`CFBundleShortVersionString`(기본 "1.0") + `CFBundleVersion` → **"버전 {v} ({b})"** 또는 빌드 없으면 **"버전 {v}"**.
Windows: `Assembly.GetName().Version` / `FileVersionInfo`로 동일 포맷.

### 시 전문 (원문 그대로 — 리소스 문자열로)
```
저것은 벽
어쩔 수 없는 벽이라고 우리가 느낄 때
그때 담쟁이는 말없이 그 벽을 오른다

물 한 방울 없고 씨앗 한 톨 살아남을 수 없는
저것은 절망의 벽이라고 말할 때
담쟁이는 서두르지 않고 앞으로 나아간다

한 뼘이라도 꼭 여럿이 함께 손을 잡고 올라간다
푸르게 절망을 다 덮을 때까지
바로 그 절망을 잡고 놓지 않는다

저것은 넘을 수 없는 벽이라고
고개를 떨구고 있을 때
담쟁이 잎 하나는 담쟁이 잎 수천 개를 이끌고
결국 그 벽을 넘는다
```
- 폰트 "AppleMyungjo"는 Windows에 없음 → **"Batang"(바탕)** 또는 "Noto Serif KR" 대체.

---

## 9. ManualSheet — 사용설명서 (⌘/ → Windows: Ctrl+/ 또는 F1 제안)

고정 660×760. 구조:
```
VStack(spacing 0)
├ 그라데이션 헤더 (높이 96, AboutSheet와 동일 그라데이션)
│  ├ 앱 아이콘 56×56(그림자 동일) + "XFinder 사용설명서"(20pt bold 흰)
│  │   + "사이드바 + 상세 보기 파일 관리자 — 한눈에 보는 전체 안내"(12pt 흰 85%)
│  └ 우상단 닫기 버튼: "xmark.circle.fill" 18pt 흰 85%, padding 12, Esc 단축
├ Divider
└ ScrollView ▸ VStack(spacing 22, padding 22) — 섹션 11개 + 푸터
```

### 재사용 구성요소 수치
- `sectionHeader(symbol, color, title)`: 30×30 cornerRadius 8 색 그라데이션 사각형 + 흰 아이콘 15pt semibold + 제목 17pt bold, HStack spacing 10.
- `card{}`: VStack spacing 11, padding 14, cornerRadius 12, 배경 primary 4%.
- `feature(symbol, color, title, desc)`: 아이콘 13pt(폭 22 고정) / 제목 13pt semibold / 설명 11.5pt 회색. HStack spacing 11, 상단 정렬.
- `tipRow(text)`: 전구 아이콘 "lightbulb.fill" 12pt 노랑 + 11.5pt medium 텍스트, padding 10, cornerRadius 9, 배경 yellow 12%.
- `shortcutGroup(title, rows)`: 그룹 제목 12pt bold 회색; 행 = 키캡들(폭 132 고정 영역) + 설명 12pt.
- `KeyCap`: 11pt semibold rounded, minWidth 18, padding H6 V3, cornerRadius 6, 배경 primary 8% + 테두리 primary 15% 0.5px.
- 단축키 표 전체 컨테이너: padding 14, cornerRadius 12, 배경 primary 4%.
- 푸터: 중앙 "XFinder · 정종수" 10pt 회색.

### 섹션과 전체 문자열 (아이콘 → Segoe Fluent 제안 글리프 병기)

**1. 화면 구성** — 헤더 아이콘 `rectangle.3.group`(파랑) → ``(ViewAll)
- `slider.horizontal.3`/파랑 "도구 막대": "창 위쪽 — 뒤로·앞으로·상위 이동, 검색창, CPU/메모리/디스크 사용량"
- `sidebar.left`/인디고 "사이드바": "왼쪽 — 즐겨찾기와 위치(드라이브) 트리. 클릭해서 폴더로 이동"
- `list.bullet`/틸 "파일 목록": "가운데 — 이름·크기·수정일. 더블클릭으로 열기/폴더 진입"
- `info.circle`/회색 "상태 막대": "창 아래쪽 — 현재 폴더의 항목 수와 선택 정보"

**2. 기본 탐색** — `arrow.up.arrow.down`(초록) → ``
- "커서 이동": "↑ ↓ 로 항목 사이를 이동하고, PageUp/PageDown · Home/End 로 빠르게 건너뜁니다."
- "폴더 진입 / 열기": "Return 으로 폴더에 들어가거나 파일을 엽니다. 더블클릭도 동일합니다."
- "뒤로 · 앞으로 · 상위": "⌘[ ⌘] (또는 ⌘← ⌘→)로 방문 기록을 오가고, ⌘↑ 또는 ⌫ 로 상위 폴더로 갑니다."
- "포커스 전환": "Tab 으로 사이드바 ⇄ 파일 목록 포커스를 전환합니다. 사이드바에서는 ↑↓ 이동, → 펼치기, ← 접기."
- "새 창": "⌘N 으로 독립된 새 창을 엽니다. 창마다 탐색·선택·히스토리가 따로 유지됩니다."
- "폴더로 이동": "⇧⌘G 로 경로를 직접 입력해 그 폴더로 한 번에 이동합니다."
- "Finder · 터미널 열기": "‘이동’ 메뉴 또는 우클릭으로 현재/선택 위치를 macOS Finder에서 보거나 터미널로 엽니다."

**3. 파일 작업** — `doc.on.doc`(주황) → ``(Copy)
- "복사 · 잘라내기 · 붙여넣기": "⌘C / ⌘X 로 담고 ⌘V 로 붙여넣습니다. 여러 항목을 ⌘·⇧ 클릭으로 선택할 수 있습니다."
- "경로 복사": "⌥⌘C 로 선택 항목의 전체 경로를 텍스트로 복사합니다(여러 개면 줄바꿈으로 구분). 우클릭 메뉴에도 있습니다."
- "복제": "⌘D 로 같은 폴더에 사본을 만듭니다."
- "이름 변경": "F2 를 누르고 새 이름을 입력한 뒤 Return."
- "새 폴더": "⇧⌘N 으로 현재 위치에 새 폴더를 만듭니다."
- "압축 · 압축 풀기": "선택 항목을 우클릭 → 압축(.zip), zip 파일은 우클릭 → 압축 풀기."
- "공유": "우클릭 → ‘공유’로 macOS 공유 시트(메일·메시지·AirDrop 등)에 선택 파일을 바로 넘깁니다."
- "휴지통으로 이동"(빨강): "⌘⌫ 로 선택 항목을 휴지통에 넣습니다."
- "응용 프로그램 삭제"(빨강): "`응용 프로그램`의 앱을 우클릭 → ‘응용 프로그램 삭제…’ 하면 환경설정·캐시·지원 파일 등 관련 항목을 함께 찾아 체크해 한 번에 휴지통으로 보냅니다(AppCleaner 방식)."

**4. 드래그 & 드롭** — `hand.draw`(핑크)
- "목록에서 끌어 이동": "파일을 폴더 위로 끌어다 놓으면 그 폴더로 이동합니다."
- "사이드바로 끌기": "왼쪽 사이드바의 즐겨찾기·폴더 위로 끌어다 놓아도 이동/복사됩니다."
- "다른 앱으로 내보내기": "파일을 메일·메신저·바탕화면 등 다른 앱으로 끌면 복사됩니다."
- 팁: "끌어 놓을 때 ⌃(Control) 을 누르고 있으면 ‘이동’ 대신 ‘복사’가 됩니다." (Windows 표준은 Ctrl=복사이므로 동일 의미 — 문구의 ⌃를 Ctrl로 치환)

**5. 즐겨찾기 & 사이드바** — `star.fill`(노랑) → ``
- "즐겨찾기 추가/제거": "폴더를 우클릭 → ‘즐겨찾기에 추가/제거’. 자주 쓰는 위치를 사이드바 맨 위에 고정합니다."
- "폴더 트리 펼치기": "사이드바 항목의 ▶ 화살표로 하위 폴더를 펼치거나 접습니다."
- "위치(드라이브)": "‘위치’ 섹션에서 홈 폴더와 연결된 드라이브/볼륨에 바로 접근합니다."

**6. 색 태그** — `tag.fill`(빨강) → ``
- "태그 지정": "파일/폴더를 우클릭 → ‘태그’에서 빨강·주황·노랑·초록·파랑·보라·회색을 켜고 끕니다. 여러 색을 동시에 붙일 수 있고, macOS Finder 태그와 호환됩니다."
- "태그 제거": "우클릭 → ‘태그’ → ‘태그 모두 제거’로 선택 항목의 색 태그를 한 번에 지웁니다."
- "태그로 모아보기": "사이드바 ‘태그’ 섹션에서 색을 클릭하면 그 태그가 붙은 파일만 시스템 전체에서 모아 보여줍니다(Spotlight 기반). 우클릭 → ‘위치로 이동’으로 실제 폴더로 갈 수 있습니다."

**7. 최근 항목 & 검색** — `clock.arrow.circlepath`(파랑) → ``
- "최근 항목": "사이드바 ‘최근 항목’은 최근 사용한 파일을 최신순으로 보여줍니다(Spotlight 기반)."
- "검색": "도구 막대 오른쪽 검색창에 입력하면 현재 폴더 안을 빠르게 걸러냅니다."
- "위치로 이동": "검색/최근 항목에서 파일을 우클릭 → ‘위치로 이동’ 하면 실제 폴더로 이동합니다."

**8. 보기 & 미리보기** — `eye`(보라) → ``
- "목록 / 아이콘 전환": "⌃M 으로 목록 보기와 아이콘 보기를 전환합니다. 폴더마다 마지막 보기 방식을 기억합니다."
- "빠른 보기": "Space (또는 F3) 로 선택 파일을 즉시 미리봅니다(이미지·텍스트 등)."
- "기본 앱으로 열기 / 다른 앱으로": "F4 로 macOS 기본 앱에서 열거나, 우클릭 → ‘다음으로 열기’에서 앱을 골라(‘기타…’ 포함) 엽니다."
- "정렬": "열 머리글(이름·크기·종류·수정일·생성일)을 클릭하면 그 기준으로 정렬되고, 다시 누르면 오름/내림차순이 바뀝니다. 빈 영역 우클릭 → ‘정렬 기준’에서도 선택할 수 있습니다."
- "화면 모드": "‘작업 ▸ 화면 모드’에서 시스템 · 라이트 · 다크를 전환합니다. 선택은 다음 실행에도 유지됩니다."
- "숨김 파일": "⇧⌘. 로 숨김 파일 표시를 켜고 끕니다."

**9. 우클릭 메뉴** — `contextualmenu.and.cursorarrow`(인디고)
- "항목 메뉴": "파일/폴더를 우클릭하면 열기·다음으로 열기·Finder에서 보기·공유·태그·복사/경로 복사/잘라내기/붙여넣기·복제·이름 변경·즐겨찾기·압축/풀기·휴지통이 한 메뉴에 모입니다."
- "빈 영역 메뉴": "목록의 빈 공간을 우클릭하면 새 폴더·붙여넣기·보기(목록/아이콘)·정렬 기준·숨김 항목 보기·새로 고침이 나오는 배경 메뉴가 열립니다."
- 팁: "검색·최근 항목·태그 모아보기처럼 실제 폴더가 아닐 때는 ‘새 폴더·붙여넣기’가 숨겨지고 보기/정렬만 표시됩니다."

**10. 시스템 모니터** — `speedometer`(빨강) → `` 또는 ``
- "CPU"(`cpu`): "도구 막대의 CPU 사용량을 클릭하면 추이 그래프·온도·부하가 큰 프로세스를 봅니다."
- "메모리"(`memorychip`): "메모리 사용량을 클릭하면 앱·캐시·스왑 등 상세 내역이 열립니다."
- "디스크"(`internaldrive`): "디스크 사용량을 클릭하면 용량 분류와 S.M.A.R.T. 상태를 확인합니다."

**11. 키보드 단축키 표** — `keyboard`(회색) → ``
- 그룹 "탐색": [↑ ↓]"커서 이동" / [Return]"열기 / 폴더 진입" / [⌘ ↓]"선택 항목 열기" / [⌘ ↑]"상위 폴더 (또는 ⌫)" / [⌘ []"뒤로 (또는 ⌘←)" / [⌘ ]]"앞으로 (또는 ⌘→)" / [Tab]"사이드바 ⇄ 목록"
- 그룹 "파일": [⌘ C]"복사" / [⌥ ⌘ C]"경로 복사" / [⌘ X]"잘라내기" / [⌘ V]"붙여넣기" / [⌘ D]"복제" / [F2]"이름 변경" / [⌘ ⌫]"휴지통으로" / [⇧ ⌘ N]"새 폴더"
- 그룹 "보기 · 기타": [Space]"빠른 보기 (F3)" / [F4]"기본 앱으로 열기" / [⌃ M]"목록 / 아이콘" / [⌘ R]"새로고침 (F5)" / [⇧ ⌘ .]"숨김 파일" / [⇧ ⌘ G]"폴더로 이동" / [⌘ N]"새 창" / [⌘ /]"이 사용설명서"

### Windows 포팅 노트 (매뉴얼)
- 본문 문구의 ⌘→Ctrl, ⌥→Alt, ⇧→Shift, ⌃→Ctrl(또는 별도 키), ⌫→Backspace로 **포팅된 실제 키 배치에 맞춰 전부 치환**해야 함. "macOS Finder에서 보기"→"탐색기에서 보기", "터미널"→"Windows Terminal/cmd", "AirDrop/공유 시트"→Windows 공유(`DataTransferManager`, 데스크톱 앱에선 제한적 — "근거리 공유" 안내로 대체하거나 섹션 축소), "Spotlight 기반"→"Windows Search 색인 기반" 등 설명 텍스트도 함께 수정.
- 실제 키맵 확정 전까지는 위 원문을 보존하고 치환 테이블을 하나 두는 방식 권장.

---

## 10. 설정 창 (SettingsWindowPresenter + SettingsView)

### 10.1 창 동작 (SettingsWindow.swift)
- 호출: 메뉴 ⌘,(Windows 제안: Ctrl+,) 또는 ⋯ 작업 메뉴 → **단독 창** (시트/팝오버 아님).
- 싱글턴: 이미 열려 있으면 새로 만들지 않고 **앞으로 가져오기 + 활성화**.
- 창 속성: 타이틀 **"설정"**, 닫기+최소화만(크기 조절 없음 — styleMask에 resizable 없음), 콘텐츠 520×620, 화면 중앙 배치.
- 닫힐 때 참조 해제 → 다음에 새로 생성.
- **호출한 창의 AppModel을 캡처**해 계속 편집(다중 메인 창 지원 시 주의 — ui-appearance.md 명시). 단, 설정 대부분은 UserDefaults 공유라 실질 효과는 전역.
- WPF: `Window { Title="설정", ResizeMode=CanMinimize, SizeToContent=Manual, Width=520, Height=620, WindowStartupLocation=CenterScreen }` + static 인스턴스 보관, `Closed`에서 null.

### 10.2 SettingsView 레이아웃 (RootView.swift 501~784행)
```
VStack(spacing 0), 최소 500×560 / 권장 520×620
├ 상단 탭 바: HStack(spacing 8, 가운데 정렬, padding H16 T16 B10)
│   탭 버튼: VStack(spacing 4) 아이콘 19pt + 라벨 11pt medium, 폭 74, 세로 padding 9
│   선택: AccentColor 글자 + AccentColor 14% 배경 cornerRadius 9 / 비선택: 회색
├ Divider
└ ScrollView ▸ VStack(spacing 22, padding H28 V24, 좌측 정렬)
```
탭 enum: `SettingsTab { general("일반", gearshape→), display("보기", rectangle.grid.1x2→), ai("AI", sparkles→ 또는 ✨) }` — 탭 상태는 저장 안 함(열 때마다 "일반").

공통 컴포넌트:
- `sectionLabel(title, icon)`: 아이콘+제목 Label, 13pt semibold 회색.
- `hint(text)`: 11pt 회색 설명문.
- 각 설정 묶음: VStack(spacing 6).

### 10.3 일반 탭

1) **"화면 모드"** (`circle.lefthalf.filled`) — 세그먼트 Picker: AppearanceMode `{ system("시스템"), light("라이트"), dark("다크") }`
   - hint: **"시스템: macOS 설정을 따릅니다."** → Windows: "시스템: Windows 설정을 따릅니다."
   - 변경 즉시 `applyAppearance()` — WPF: 테마 리소스 딕셔너리 교체(라이트/다크) + system이면 `AppsUseLightTheme` 레지스트리 감시.
2) **"터미널 앱"** (`terminal`) — 세그먼트 Picker: TerminalApp `{ auto("자동"), terminal("터미널"), iterm("iTerm") }`
   - hint(동적, terminalHint):
     - auto + iTerm 있음: **"자동: iTerm이 설치되어 iTerm으로 엽니다."** / 없음: **"자동: iTerm이 없어 기본 터미널로 엽니다."**
     - terminal: **"기본 터미널로 엽니다."**
     - iterm + 있음: **"iTerm으로 엽니다."** / 없음: **"iTerm이 설치되어 있지 않아 기본 터미널로 엽니다."**
   - Windows 대응: `{ auto("자동"), wt("Windows Terminal"), cmd("명령 프롬프트") }` 등으로 케이스 교체. 감지: `wt.exe` 존재(App Execution Alias `%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe`). 저장 키는 그대로 사용 가능.
3) **"기본 탭"** (`rectangle.split.3x1`)
   - 저장된 기본 탭 없으면 hint: **"저장된 기본 탭이 없습니다 — 시작할 때 '최근 항목' 하나로 시작합니다."**
   - 있으면: 폴더 이름들을 `"  ·  "`로 연결해 12pt, 2줄 제한 중간 생략 (루트 "/"는 "Macintosh HD"로 표기 → Windows: 드라이브 루트는 "C:\" 등 그대로) + hint: **"시작할 때 위 {n}개 폴더가 탭으로 자동으로 열립니다."**
   - 버튼 **"현재 열린 탭을 기본으로 저장"** → `SaveCurrentTabsAsDefault()`: 현재 탭 폴더 경로 배열을 `XFinder.defaultTabs.v1`에 저장하고 InfoMessage: **"현재 탭 {n}개를 기본 탭으로 저장했습니다.\n다음 실행부터 이 탭들로 시작합니다."** 툴팁: **"지금 이 창에 열려 있는 탭들의 폴더를 저장합니다 ({n}개)"**
   - 저장된 게 있으면 빨간 톤 버튼 **"지우기"** → `ClearDefaultTabs()`: 키 삭제, 기본 동작 복귀. 툴팁: **"저장된 기본 탭을 삭제하고 기본 동작(최근 항목)으로 되돌립니다"**
   - 부팅 시 복원: 첫 경로 select + 나머지는 새 탭, 첫 탭 활성화. 존재하지 않는 폴더는 걸러냄.

### 10.4 보기 탭

1) **"파일 목록 크기"** (`textformat.size`) — 우측에 현재값 `"{(listScale*100) 반올림}%"` (11pt medium 회색)
   - − 버튼(`textformat.size.smaller`, 22×22, 툴팁 **"5% 작게"**): `listScale = max(0.8, round((v-0.05)*20)/20)`
   - Slider: 범위 **0.8 ~ 1.8, step 0.05**
   - + 버튼(`textformat.size.larger`, 툴팁 **"5% 크게"**): `min(1.8, …)` 동일 스냅.
   - 효과: 목록 글꼴·툴바 아이콘 등 배율(다른 영역) — 즉시 반영, 저장.
2) **"날짜 표시"** (`calendar.badge.clock`) — 세그먼트: DateDisplayStyle `{ absolute("실제 날짜"), relative("상대 시간") }`
   - hint(동적): relative → **"수정일·생성일을 ‘1분 전’, ‘1시간 전’, ‘1일 전’ 형식으로 표시합니다. 마우스를 올리면 실제 날짜가 보입니다."** / absolute → **"수정일·생성일을 ‘2026-06-09 17:16’ 형식으로 표시합니다."**
3) **"검색창 위치"** (`magnifyingglass`) — 세그먼트: SearchBarPosition `{ toolbar("툴바"), below("툴바 아래") }`
   - hint: toolbar → **"검색창을 툴바 오른쪽에 표시합니다."** / below → **"검색창을 툴바 아래 전체 폭의 별도 줄로 표시합니다."**
4) **"폴더 용량 계산"** (`sum`) — 체크박스 **"폴더 용량 계산 및 표시"** (12pt)
   - hint: **"끄면 파인더처럼 폴더 용량을 계산하지 않아 탐색이 빠릅니다(폴더는 -- 로 표시)."** (기본값 **false** = 끔)
5) **"최근 항목 표시 종류"** (`clock`) — 카테고리별 체크박스, 순서 고정: **"문서", "이미지", "동영상", "음악", "압축", "기타"** (`FileSystemService.fileTypeOrder`)
   - 바인딩: `recentsCategories: Set<string>` — 켜면 insert, 끄면 remove.
   - hint: **"선택한 종류만 최근 항목에 표시 (아무것도 선택 안 하면 전체)."** — 빈 집합 = 전체 표시 의미에 주의.

### 10.5 AI 탭

1) **"AI 모델"** (`sparkles`) — 세그먼트: AIProvider `{ ollama("로컬 (Ollama)"), gemini("Gemini") }` (기본 gemini)
   - gemini 선택 시:
     - SecureField(PasswordBox) placeholder **"Gemini API 키"** (11pt)
     - TextField placeholder **"모델 (예: gemini-2.5-flash)"**
     - hint: **"키는 aistudio.google.com 에서 발급합니다. 정리 시 파일 이름이 구글로 전송됩니다."**
   - ollama 선택 시:
     - hint **"서버 주소"** + TextField placeholder **"http://localhost:11434"** (11pt 모노스페이스)
     - hint **"모델 이름"** + TextField placeholder = `AIService.defaultOllamaModel` = **"gemma4:latest"** (모노스페이스)
     - 작은 버튼 **"기본값"** → BaseURL/모델을 기본값(`http://localhost:11434`, `gemma4:latest`)으로 리셋
     - hint: **"로컬 Ollama 사용 — 입력한 모델이 없으면 자동 감지. 데이터가 기기 밖으로 나가지 않습니다."**
2) **"AI 정리 예외 폴더"** (`hand.raised`) — 우측에 **"{n}개"** 카운트
   - 비었으면 hint: **"등록된 예외 폴더가 없습니다. 폴더를 우클릭해 ‘AI 정리 예외 폴더로 등록’을 선택하세요."**
   - 있으면 ScrollView(maxHeight 132) 안에 행 목록(spacing 4) + hint: **"등록된 폴더와 그 하위 폴더 전체에서 AI 정리가 동작하지 않습니다."**
   - 행(`excludedRow`): `folder.fill` 11pt 파랑 + 폴더명 11pt medium + 경로 9pt 회색(중간 생략) + 돋보기 버튼(툴팁 **"Finder에서 보기"** → "탐색기에서 보기", `SystemActions.reveal` → `explorer.exe /select,`) + `xmark.circle.fill` 버튼(툴팁 **"예외 해제"** → `RemoveExcludedFolder`; InfoMessage: **"“{이름}”의 AI 정리 예외를 해제했습니다."**). 행 스타일: padding H8 V5, cornerRadius 6, 배경 primary 5%.
   - API 키를 UserDefaults(평문)에 저장하는 원본 방식 대신 Windows에서는 **DPAPI(`ProtectedData`) 암호화 후 저장** 권장(키 이름은 동일하게 유지 가능).

---

## 11. 앱 완전 삭제 (UninstallSheet + AppUninstaller)

### 11.1 진입 조건
- `AppUninstaller.isApp(item)`: 디렉터리이며 확장자 == "app" → 컨텍스트 메뉴 "응용 프로그램 삭제…" 노출(다른 영역). 삭제 키(⌘⌫)로 .app을 지울 때도 이 시트로 우회 가능.
- Windows 대응(§11.5): "설치된 프로그램" 항목에 대해 동일 UX 제공.

### 11.2 AppRelatedFile 데이터
```csharp
public sealed record AppRelatedFile
{
    public string Path;        // 식별자(원본 id = URL)
    public long Size;          // 폴더면 재귀 합산
    public bool IsApp;         // 본체(.app / 설치 폴더) — 항상 목록 첫 행
    public string Name => System.IO.Path.GetFileName(Path);
    public string DisplayPath; // 상위 폴더 경로, 홈은 "~"로 축약 → Windows: %USERPROFILE% → "~" 동일 축약 유지
}
```

### 11.3 후보 탐색 규칙 (AppUninstaller.relatedFiles — macOS 원본)
- 입력: .app 번들 URL. `bundleID = Bundle.bundleIdentifier`, `appName = 확장자 뺀 파일명`.
- 결과 목록 첫 항목은 항상 .app 본체. 중복은 표준화 경로(set)로 제거, 존재하는 항목만.
- **belongs(name)** — 번들 ID 토큰 매칭:
  - name 안에 bundleID 부분 문자열이 있어야 함.
  - 시작 경계: bundleID 앞 문자가 있으면 반드시 `.` (팀 ID 접두 "S8EX82NJP6.com.x.app", "group.com.x.app" 허용; "xcom.x.app" 차단).
  - 끝 경계: bundleID 뒤 문자가 있으면 `.`/`_`/공백 중 하나 ("com.x.app.helper.plist", "com.x.app_stats.sqlite3" 허용; "com.x.app2" 차단).
- **nameMatch(name)**: 확장자 뺀 이름 == appName.
- 스캔 대상 (디렉터리 1단계 항목만, 재귀 아님):
  - `~/Library/` 하위에서 belongs로: `Preferences`, `Preferences/ByHost`, `Caches`, `HTTPStorages`, `WebKit`, `Containers`, `Group Containers`, `Saved Application State`, `Application Scripts`, `LaunchAgents`, `Cookies`
  - `~/Library/` 하위에서 belongs ∥ nameMatch로: `Application Support`, `Logs`
  - `/Library/` 하위에서 belongs로: `LaunchAgents`, `LaunchDaemons`, `PrivilegedHelperTools`
  - `/Library/Application Support`: belongs ∥ nameMatch
- 크기: 폴더는 `FileSystemService.folderSize`(재귀), 파일은 fileSize. **느린 작업 — 반드시 백그라운드 스레드.**
- doc/app-uninstall.md 주의: 매칭은 휴리스틱(오탐 가능) → **사용자 검토(체크박스) 단계를 절대 생략하지 말 것**, 삭제는 휴지통 경유만.

### 11.4 UninstallSheet UI (고정 540×580)
```
VStack(spacing 0): header / Divider / content / Divider / footer
header (padding 16): 앱 아이콘 48×48 + VStack(spacing 3)
  ├ 앱 이름 16pt bold (1줄)
  └ 로딩 중: "관련 파일을 찾는 중…" (12pt 회색)
    완료: "{n}개 파일" (12pt 회색) · "·" · 선택 크기 합계 (12pt semibold 파랑, 숫자 고정폭)
content: 로딩 중 = 중앙 스피너 / 완료 = ScrollView + LazyVStack(행 사이 Divider, 좌측 12 들여쓰기)
  행 (padding H12 V6, 전체 클릭 = 체크 토글):
  ├ 체크박스 + 파일 아이콘 26×26
  ├ VStack: 이름 12pt medium (본체면 옆에 "앱" 배지: 9pt bold 파랑, Capsule 파랑 15% 배경, padding H5 V1)
  │         DisplayPath 10pt 회색, 중간 생략
  ├ 크기 11pt 회색 고정폭
  └ 돋보기 버튼(plain) — 툴팁 "Finder에서 보기" → 해당 항목 reveal
footer (padding 12):
  "모두 선택" (로딩 중/전부 선택이면 비활성) · "모두 해제" (선택 없으면 비활성)
  Spacer · "취소"(Esc) · "삭제 ({선택 수})" — 빨간 prominent 버튼, Enter, 선택 0이면 비활성
```
- 로드 완료 시 **전 항목 기본 체크**(AppCleaner 방식 — 원본 코드 기준; doc의 "기본 체크 해제 항목 유지" 문구는 '검토 단계를 없애지 말라'는 취지).
- "삭제" → 시트 닫고 `AppModel.PerformUninstall(urls)`.

### 11.5 performUninstall 동작 (AppModel 1759~1796행)
1. 각 URL을 **휴지통으로**(`trashItem`). 실패한 것만 모음.
2. 실패분은 Finder에 위임(`FileOperations.trashViaFinder` — AppleScript 자동화; 관리자 인증 Finder가 처리). AppleEvent 오류 **-1743** = 자동화 권한 거부 플래그.
3. 목록/사이드바 리로드 후, 원경로에 **여전히 존재하는 것**만 실제 실패로 판정.
4. 자동화 거부면 ConfirmRequest:
   - title: **"‘자동화’ 권한이 필요합니다"**
   - message: **"보호된 항목을 삭제하려면 XFinder가 Finder를 제어하도록 허용해야 합니다.\n\n시스템 설정 > 개인정보 보호 및 보안 > 자동화에서 XFinder 아래의 Finder를 켠 뒤 다시 시도하세요."**
   - confirmTitle: **"자동화 설정 열기"**, isDestructive: false → 시스템 설정 열기.
5. 그 외 실패: ErrorMessage **"다음 항목을 삭제하지 못했습니다:\n• {이름}…"** (줄마다 "• " 접두).

### 11.6 Windows 포팅안 (제안 — 스펙)
macOS의 ".app 번들 + Library 잔재" 모델을 Windows에 그대로 옮길 수 없으므로 다음 2계층으로 대응:

**A. 설치된 프로그램 (정식 제거)**
- 레지스트리 Uninstall 키 열거: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`, `HKLM\SOFTWARE\WOW6432Node\...\Uninstall`, `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` — 값: `DisplayName`, `DisplayIcon`, `DisplayVersion`, `InstallLocation`, `UninstallString`, `QuietUninstallString`, `EstimatedSize`(KB).
- 파일 목록의 `.exe`(또는 InstallLocation에 해당하는 폴더)를 우클릭 → "응용 프로그램 삭제…" 시: exe 경로/제품명으로 Uninstall 키 매칭 → 있으면 `UninstallString` 실행(정식 언인스톨러)을 1차 권장 경로로 안내.
- MSIX/스토어 앱은 `Get-AppxPackage`/`PackageManager.RemovePackageAsync` (베스트에포트, 생략 가능).

**B. 잔여 파일 스캔 (AppCleaner 상당 — 이 시트의 본체)**
- 식별자: 제품명(`FileVersionInfo.ProductName`), 회사명(`CompanyName`), exe 파일명(확장자 제외) — mac의 bundleID/appName 대응. 매칭은 원본 `belongs`처럼 **단어 경계 기반**(이름이 정확히 일치하거나 `회사명\제품명` 폴더 구조)으로 오탐 방지.
- 스캔 디렉터리(1단계 항목만, mac 목록 대응):
  - `%LOCALAPPDATA%`, `%APPDATA%` (← Application Support / Preferences)
  - `%LOCALAPPDATA%\Temp`, `%LOCALAPPDATA%\<회사>\<제품>` 캐시류 (← Caches)
  - `%PROGRAMDATA%` (← /Library/Application Support)
  - `%APPDATA%\Microsoft\Windows\Start Menu\Programs` 의 바로가기(.lnk), 바탕화면 바로가기
  - 자동 시작: 레지스트리 `HKCU\...\Run`, `HKLM\...\Run`, `shell:startup` 폴더 (← LaunchAgents/Daemons)
  - 레지스트리 잔재(선택): `HKCU\SOFTWARE\<회사>\<제품>` — 파일이 아니므로 별도 행 유형으로 표시하거나 v1에서는 제외.
- 크기 계산: 폴더는 병렬 `Directory.EnumerateFiles` 재귀 합산(백그라운드), 파일은 `FileInfo.Length`.
- 삭제: **반드시 휴지통 경유** — `SHFileOperation`(FO_DELETE, `FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT`) 또는 `Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(..., RecycleOption.SendToRecycleBin)`. 실패(잠긴 파일/권한) 항목은 §11.4와 같은 패턴으로 모아 ErrorMessage. UAC 승격이 필요한 항목(Program Files 등)은 "관리자 권한으로 재시도" ConfirmRequest 제안(원본의 Finder 위임 대응).
- 실행 중 프로세스가 점유한 exe는 삭제 실패 — 사전 감지해 "먼저 종료하세요" 안내 추가 권장.

---

## 12. 영속화 — UserDefaults 키 전체 (이 영역 + 인접 설정)

Windows 저장소 제안: `%APPDATA%\XFinder\settings.json` 단일 JSON(키 이름 그대로 보존). 아래 모두 `UserDefaults.standard`.

| 키 | 형식 | 기본값 | 내용 |
|---|---|---|---|
| `XFinder.appearance.v1` | string ("system"/"light"/"dark") | system | 화면 모드 |
| `XFinder.terminalApp.v1` | string ("auto"/"terminal"/"iterm") | auto | 터미널 앱 (SystemActions.terminalPrefKey) |
| `XFinder.dateStyle.v1` | string ("absolute"/"relative") | absolute | 날짜 표시 |
| `XFinder.searchPosition.v1` | string ("toolbar"/"below") | toolbar | 검색창 위치 |
| `XFinder.listScale.v1` | double | 1.0 | 목록 배율 (0.8~1.8) |
| `XFinder.columnWidths.v1` | dict<string,double> | {} | 열 너비(배율 적용 전 포인트) |
| `XFinder.recentsCategories.v1` | string 배열 | (미설정=전체) | 최근 항목 카테고리 집합 |
| `XFinder.calculateFolderSizes.v1` | bool | false | 폴더 용량 계산 |
| `XFinder.aiProvider.v1` | string ("ollama"/"gemini") | gemini | AI 제공자 |
| `XFinder.geminiAPIKey.v1` | string | "" | Gemini 키 (**Windows: DPAPI 암호화 권장**) |
| `XFinder.geminiModel.v1` | string | "" (빈값→기본 모델) | Gemini 모델명 |
| `XFinder.ollamaBaseURL.v1` | string | "" (빈값→`http://localhost:11434`) | Ollama 주소 |
| `XFinder.ollamaModel.v1` | string | "" (빈값→`gemma4:latest`) | Ollama 모델 |
| `XFinder.defaultTabs.v1` | string 배열(절대경로) | 없음(미설정) | 기본 탭 폴더들; 없으면 '최근 항목'으로 시작 |
| `XFinder.favorites.v1` | string 배열(절대경로) | 기본 즐겨찾기 | 사이드바 즐겨찾기 (타 영역) |
| `XFinder.aiExcludedFolders.v1` | string 배열(절대경로) | [] | AI 정리 예외 폴더 |
| `XFinder.folderViewModes.v1` | dict<string,string> (경로→"list"/"icons") | {} | 폴더별 보기 모드 (타 영역) |

저장 패턴: 프로퍼티 `didSet`에서 즉시 저장(디바운스 없음) — C#은 setter에서 저장 호출 + 시작 시 일괄 로드. **새 설정도 같은 패턴으로 추가하고 별도 저장소를 만들지 말 것**(doc 주의사항).

---

## 13. Windows 포팅 노트 요약 (mac 전용 API별)

| mac API/개념 | 사용처 | Windows 대응 |
|---|---|---|
| Quick Look (`SystemActions.quickLook`) | Space/F3 | 자체 미리보기 창 = ViewerSheet 승격 (§2). 선택: PowerToys Peek 스타일 |
| `NSImage(contentsOf:)` | 뷰어 이미지 | WIC `BitmapImage` (+ heic/icns 제한 명시) |
| `NSWorkspace.shared.open(mailto:)` | About 이메일 | `Process.Start(new ProcessStartInfo("mailto:...") { UseShellExecute = true })` |
| `NSWorkspace.shared.icon(forFile:)` | Uninstall 행 아이콘 | `SHGetFileInfo`/`IShellItemImageFactory`로 셸 아이콘 추출 |
| `NSApp.applicationIconImage` | About/Manual 헤더 | 앱 리소스 아이콘(.ico → ImageSource) |
| `NSCursor.pointingHand` | 이메일 호버 | `Cursors.Hand` |
| `NSWindow` 단독 설정 창 | SettingsWindowPresenter | WPF `Window` 싱글턴 (§10.1) |
| `NSWindow.willCloseNotification` | 설정 창 참조 해제 | `Window.Closed` 이벤트 |
| `FileManager.trashItem` | performUninstall | `SHFileOperation` + `FOF_ALLOWUNDO` (휴지통) |
| Finder 위임(`trashViaFinder`, AppleEvent -1743) | 보호 항목 삭제 | 무의미 — UAC 승격 재시도로 대체 (§11.6) |
| 자동화(TCC) 권한 안내 | performUninstall | 무의미 — UAC/잠긴 파일 안내로 대체 |
| `Bundle.bundleIdentifier` | 잔재 매칭 | `FileVersionInfo` ProductName/CompanyName + 레지스트리 Uninstall 키 |
| `~/Library/...` 스캔 목록 | AppUninstaller | `%APPDATA%`/`%LOCALAPPDATA%`/`%PROGRAMDATA%`/시작 메뉴/Run 키 (§11.6) |
| AppleMyungjo 폰트 | About 시 | Batang 또는 Noto Serif KR |
| SF Rounded (`fontDesign(.rounded)`) | 앱 전체·설정 창 | Segoe UI Variable |
| `.regularMaterial` | ConfirmDialog | 반투명 단색 브러시 (§7) |
| SwiftUI `.sheet` | 모든 시트 | 창 내 디밍 오버레이 + 중앙 카드 (권장) |
| `keyboardShortcut(.defaultAction/.cancelAction)` | 모든 시트 버튼 | `Button.IsDefault` / `IsCancel` |
| `@FocusState` + 다음 런루프 포커스 | 입력 시트 | `Dispatcher.BeginInvoke(Keyboard.Focus)` (Loaded 후) |
| `Format.size` (ByteCountFormatter) | 크기 표기 | 자체 포맷터(KB/MB/GB, mac은 10진 단위 — 1000 기준 유지 여부 결정 필요; 탐색기는 1024 기준) |
| SF Symbols 전반 | 아이콘 | Segoe Fluent Icons 글리프 매핑 표(§9, §10) 또는 단색 Path 아이콘 |

---

## 14. 구현 체크리스트 (이 영역)
1. AppSheet/ConfirmRequest/OperationProgress + ErrorMessage/InfoMessage 채널.
2. 입력 시트 3종(이동/새 폴더/이름 변경) — Enter/Esc, 자동 포커스, WindowsName 검증 메시지.
3. ViewerSheet — 비동기 로드, 1MB 텍스트 컷, 이미지 확장자 셋.
4. ProgressSheet — 닫기 불가, 취소 플래그.
5. ConfirmDialog 오버레이 — 키보드 포커스 순환.
6. About/Manual — 문자열 리소스 전체 이식(§8 시 전문, §9 표), 키 표기 치환 테이블.
7. 설정 창 — 3탭 전 항목 + UserDefaults 키 호환 JSON 저장.
8. UninstallSheet + Windows 잔재 스캐너(레지스트리 Uninstall + AppData 스캔) + 휴지통 삭제.
