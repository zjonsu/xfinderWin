# XFinder for Windows

Windows용 네이티브 파일 관리자 — **macOS Finder 스타일**의 사이드바 + 상세 보기 레이아웃.
macOS용 [XFinder](https://github.com/zjonsu/xFinder)를 **C# / .NET 8 / WPF**로 동일하게 포팅한 버전입니다.

## 레이아웃

```
┌──────────────┬──────────────────────────────────────────┐
│  즐겨찾기      │  ‹ › ⌃  로컬 디스크 (C:) › Users › zjons   │  ← 경로(브레드크럼) + 툴바
│   최근 항목    ├──────────────────────────────────────────┤
│   데스크탑     │  이름            크기   수정일      종류    │
│   문서         │  📁 Documents     --    2026-…    Folder  │
│   다운로드     │  📄 report.pdf   1.2MB  2026-…    PDF     │  ← 선택한 폴더의 내용
│  위치          │  …                                        │
│   ▸ zjons     │                                          │
│   ▸ 로컬 디스크 │                                          │
│  태그          │                                          │
│   ● 빨간색     │  16개 항목              122 GB 사용 가능   │  ← 상태 표시줄
└──────────────┴──────────────────────────────────────────┘
```

## 기능

- 사이드바 트리 탐색 + Finder식 즐겨찾기/위치/태그 (7색 태그)
- **다중 탭** — Ctrl+T 새 탭, Ctrl+W 탭 닫기, Ctrl+Tab 전환 (탭마다 독립 탐색, 파스텔 색 자동 배정)
- **기본 탭** — 설정에서 현재 열린 탭들을 저장하면 다음 실행 때 그 탭들로 시작
- 폴더 더블클릭 진입, 경로 막대 클릭 이동, 뒤로/앞으로/상위 이동 (히스토리, 탭별)
- 파일 작업: **새 폴더, 이름 변경, 복제, 휴지통으로 이동** (휴지통 — 복원 가능)
- **복사 / 잘라내기 / 붙여넣기** — Windows 탐색기와 상호 운용되는 클립보드
- **ZIP 압축 / 압축 풀기** (진행 표시 + 취소)
- **미리 보기** (Space / F3) — 이미지·텍스트·셸 썸네일
- **숨김 파일** 토글, **목록/아이콘** 보기 전환, 이름·크기·날짜·종류 정렬
- **다음으로 그룹화** — 이름/종류/크기/수정일/생성일 구간으로 묶어 보기
- **열 너비 조절** — 헤더 경계 드래그 (더블클릭 = 기본값)
- 빠른 **검색** (하위 폴더 재귀, 최대 1000개), 상태 표시줄(항목 수·선택·여유 공간)
- **AI 파일 정리** ✨ — 한국어 명령으로 파일 자동 정리 (로컬 Ollama 또는 Gemini, 실행 전 미리보기)
- **시스템 모니터** — 툴바에서 CPU/메모리/디스크 실시간 표시, 클릭하면 상세 팝업
  (사용률 그래프·온도·프로세스 목록·용량 분류·S.M.A.R.T.·휴지통 비우기·종류별 파일 계산)
- **프로그램 완전 제거** — 설치된 프로그램 제거 + AppData 등 잔여 파일 정리
- **한글 자소 분리(NFD) 파일명 복원** — mac에서 복사해 온 파일의 깨진 한글 이름을 NFC로 복원
- **최근 항목** — 종류(문서/이미지 등) 필터 지원
- 다중 선택 (Ctrl-클릭 / Shift-클릭 / 러버밴드), 드래그&드롭 (기본 이동, Ctrl = 복사)
- 라이트 / 다크 / 시스템 화면 모드

## 빌드 & 실행

```powershell
# 요구사항: Windows 10/11 + .NET 8 SDK
dotnet run --project XFinder.csproj          # 개발 실행
dotnet publish -c Release -r win-x64         # 배포 빌드
```

## 키보드 단축키

(대화상자·텍스트 입력 중에는 자동으로 비활성화됩니다.)

| 단축키 | 동작 | 단축키 | 동작 |
|---|---|---|---|
| ↑ ↓ / PageUp·Down / Home·End | 커서 이동 | Return | 열기 / 폴더 진입 |
| Ctrl+↓ | 선택 항목 열기 | Alt+↑ / Backspace | 상위 폴더 |
| Alt+← Alt+→ | 뒤로 / 앞으로 | Space, F3 | 미리 보기 |
| Ctrl+C / Ctrl+X / Ctrl+V | 복사 / 잘라내기 / 붙여넣기 | Ctrl+D | 복제 |
| Delete | 휴지통으로 | Ctrl+Shift+N | 새 폴더 |
| F2 | 이름 변경 | F4 | 기본 앱으로 열기 |
| Ctrl+R / F5 | 새로고침 | Ctrl+H | 숨김 파일 |
| Ctrl+Shift+G | 폴더로 이동 | Ctrl+M | 목록/아이콘 전환 |
| Ctrl+T | 새 탭 | Ctrl+W | 탭 닫기 (마지막 탭 = 창 닫기) |
| Ctrl+Tab / Ctrl+Shift+Tab | 탭 전환 | Ctrl+A | 전체 선택 |
| Ctrl+Shift+C | 경로 복사 | Ctrl+I | 속성(정보) |
| Tab | 사이드바 ⇄ 목록 포커스 | F1 | 사용설명서 |
| 글자 입력 | type-select — 그 이름으로 점프 (한글 자모 단위) | | |

마우스 측면 버튼(4/5번) = 뒤로/앞으로. 사이드바 포커스 시 ↑↓ 이동 · → 펼치기 · ← 접기.

## 구조

```
XFinder/
  App.xaml(.cs)             진입점, 테마 초기화
  Models/
    AppModel.cs             루트 상태 (탐색·탭·히스토리·클립보드·설정 — mac AppModel 대응)
    PaneTab.cs              탭 상태 (목록·선택·정렬·가상 모드 4종)
    FileItem.cs             파일 항목 + 표시 포맷
    SidebarItem.cs          사이드바/트리 노드
    Enums.cs / AppSheet.cs  정렬키·보기 모드 / 시트 라우팅
  Services/
    FileSystemService.cs    디렉터리 나열·검색·병렬 크기 계산·종류별 분류
    FileOperations.cs       복사·이동·복제·ZIP (진행률+취소)
    SystemActions.cs        열기·터미널·클립보드·속성
    ShellInterop.cs         휴지통·아이콘·썸네일 (Win32 셸)
    SystemMonitor.cs        CPU/메모리/디스크 샘플링 (싱글턴)
    AIService.cs            AI 파일 정리 (Ollama/Gemini)
    TagService.cs           7색 태그 (로컬 DB)
    RecentsService.cs       최근 항목 (.lnk 해석)
    HangulNormalize.cs      NFD→NFC 한글 파일명 복원
    SettingsStore.cs        설정 저장 (%APPDATA%\XFinder\settings.json)
  Views/
    MainWindow.xaml(.cs)    툴바·경로 막대·탭 바·키 라우팅·오버레이
    SidebarView / DetailView / SettingsWindow / AIOrganizeWindow / UninstallWindow
    Monitor/                CPU·메모리·디스크 상세 팝업
  spec/                     macOS 원본 분석 포팅 스펙 (08-gaps.md가 정본)
```

## 참고 / 제한

- 설정은 `%APPDATA%\XFinder\settings.json`에 저장됩니다 (mac UserDefaults 키 체계 유지).
- 태그는 Windows에 대응 개념이 없어 앱 로컬 DB로 구현됩니다 (mac Finder 태그와 동기화되지 않음).
- CPU/디스크 온도·S.M.A.R.T.는 하드웨어/권한에 따라 표시되지 않을 수 있습니다 (WMI 베스트에포트).
- AirDrop·Quick Look·Finder 연동 등 macOS 전용 기능은 Windows 대응 기능으로 대체했습니다
  (탐색기에서 보기, 자체 미리보기 창, 셸 속성 대화상자).
