# 07. AI 파일 정리 (AI Organize) — Windows 포팅 스펙

원본 소스:
- `Sources/XFinder/Services/AIService.swift` (전체)
- `Sources/XFinder/Views/AIOrganizeSheet.swift` (전체)
- `Sources/XFinder/Model/AppModel.swift` 중 AI 관련 부분 (설정 키, 예외 폴더, `currentFolderEntries`, `applyAIPlan`)
- `doc/ai-organize.md`

기능 요약: 현재 폴더의 **파일 이름 목록**(내용 아님)과 사용자의 한국어 자연어 명령을 LLM(로컬 Ollama 또는 Google Gemini)에 보내,
"하위 폴더로 이동(move)" / "휴지통으로 이동(delete)" 작업 목록(`AIPlan`)을 JSON으로 받고, 미리보기 시트에서 사용자가 확인한 뒤 일괄 적용한다.

> 문서·코드 불일치 주의: `doc/ai-organize.md`는 "작업별 체크박스로 선택 실행"이라고 적혀 있으나,
> **현재 코드(AIOrganizeSheet)는 체크박스가 없고 계획 전체를 적용**한다. 포팅은 현재 코드를 기준으로 하되,
> 체크박스 선택 실행을 개선 항목으로 남겨 둘 것. 단, "실행은 반드시 사용자 검토 시트를 거친다 — 계획 자동 실행 경로 금지,
> delete는 영구 삭제가 아니라 휴지통"이라는 원칙은 절대 규칙이다.

---

## 1. 데이터 구조 (C# 대응)

### 1.1 AIOperation — LLM이 제안한 단일 작업

```csharp
// Swift: struct AIOperation: Decodable, Identifiable, Hashable
public sealed record AIOperation
{
    public string Action { get; init; } = "";      // "move" | "delete" (문자열 그대로 유지!)
    public string File { get; init; } = "";        // 현재 폴더 안 항목 이름 (경로 아님)
    public string? Destination { get; init; }      // move 전용 하위 폴더 이름; delete에는 null/빈 문자열

    public string Id => $"{Action}:{File}→{Destination ?? ""}"; // SwiftUI Identifiable 대응 (목록 키)
    public bool IsDelete => Action == "delete";
}
```

> 원본 doc 주의사항 그대로: `action`은 **enum으로 바꾸지 말 것** — LLM 출력 JSON과의 호환성 때문에 문자열 유지.
> JSON 역직렬화 시 알 수 없는 action 값은 파싱 단계에서 버려진다(아래 4.4 검증 규칙).

### 1.2 AIPlan — LLM의 전체 계획

```csharp
public sealed record AIPlan
{
    public List<AIOperation> Operations { get; init; } = new();
    public string Summary { get; init; } = "";     // 한 줄 한국어 요약
}
```

JSON 스키마 (LLM 응답 원형, 키 이름 소문자):

```json
{
  "operations": [
    { "action": "move",   "file": "<목록에 있는 정확한 이름>", "destination": "<하위 폴더 이름>" },
    { "action": "delete", "file": "<목록에 있는 정확한 이름>" }
  ],
  "summary": "<짧은 한국어 한 문장>"
}
```

### 1.3 AIProvider — 백엔드 선택

```csharp
public enum AIProvider { Ollama, Gemini }
// 직렬화 rawValue: "ollama" / "gemini"  (UserDefaults 저장값과 동일하게 소문자 문자열로 저장)
// 표시 라벨: Ollama → "로컬 (Ollama)",  Gemini → "Gemini"
```

### 1.4 AIConfig — 분석 호출에 전달되는 설정 스냅숏

```csharp
public sealed record AIConfig
{
    public AIProvider Provider { get; init; }
    public string GeminiApiKey { get; init; } = "";
    public string GeminiModel { get; init; } = "";          // 비면 AIService 쪽 기본 "gemini-2.5-flash"
    public string OllamaBaseUrl { get; init; } = AIService.DefaultOllamaBaseUrl; // "http://localhost:11434"
    public string OllamaModel { get; init; } = AIService.DefaultOllamaModel;     // "gemma4:latest"
    public bool FallbackToOllama { get; init; } = true;     // Gemini 실패 시 Ollama 재시도

    // resolvedOllamaBase: Trim 후 URL 파싱 실패 시 기본 주소("http://localhost:11434")로 폴백
    // resolvedOllamaModel: Trim 후 빈 문자열이면 "gemma4:latest"
}
```

`AppModel.aiConfig`는 `AIConfig(provider, geminiAPIKey, geminiModel, ollamaBaseURL, ollamaModel)`로 생성하며
`fallbackToOllama`는 항상 기본값 `true`다(설정 UI에서 바꾸는 경로 없음 — 이 영역 코드 기준).

### 1.5 AIError — 사용자에게 보이는 오류 (한국어 메시지 원문)

```csharp
public enum AIErrorKind { NotRunning, NoModel, GeminiNoKey, BadResponse }
```

| 케이스 | 메시지(원문 그대로, `\n` 포함) |
|---|---|
| `notRunning` | `로컬 LLM(Ollama)에 연결할 수 없습니다.\n터미널에서 `ollama serve` 가 실행 중인지 확인하세요.` |
| `noModel` | `사용할 수 있는 채팅 모델이 없습니다.\n`ollama pull gemma4` 등으로 모델을 받아 주세요.` |
| `geminiNoKey` | `Gemini API 키가 없습니다.\n설정 → AI 모델에서 키를 입력하세요. (aistudio.google.com 에서 발급)` |
| `badResponse(s)` | `AI 응답을 이해하지 못했습니다.\n{s}` |

### 1.6 AIService 상수

```csharp
public static class AIService
{
    public const string DefaultOllamaBaseUrl = "http://localhost:11434";
    public const string DefaultOllamaModel = "gemma4:latest";
    // preferredOllamaModel == DefaultOllamaModel (하위 호환 별칭)
}
```

### 1.7 시트 상태 (AIOrganizeSheet)

```csharp
private enum Phase { Input, Loading, Preview, Empty, Error }

// 상태 필드
string instruction = "";        // 입력 텍스트
Phase phase = Phase.Input;
AIPlan? plan;
string? errorText;
List<string> files = new();     // 시트 열릴 때 currentFolderEntries() 스냅숏
bool showLocalAIGuide, showGeminiGuide;  // 안내 팝오버 표시 여부
// + 입력 텍스트박스 포커스 (열릴 때 비동기로 포커스 부여)
```

빠른 예시(칩) 목록 — 원문 그대로 (`’ ‘` 는 유니코드 곡선 따옴표):

```csharp
private static readonly string[] Examples =
{
    "확장자 종류별로 폴더를 만들어 정리해줘",
    "이미지 파일만 ‘이미지’ 폴더로 모아줘",
    "날짜(연-월)별 폴더로 분류해줘",
    "스크린샷을 ‘스크린샷’ 폴더로 옮겨줘",
};
```

---

## 2. 폴더 내용 직렬화 (LLM 입력)

`AppModel.currentFolderEntries(limit: Int = 300)`:

1. 현재 선택 폴더(`selectedFolder`)의 **최상위 항목만** 나열 (재귀 없음).
2. 숨김 파일 제외 (macOS `.skipsHiddenFiles`).
   - Windows: `FileAttributes.Hidden` 또는 `FileAttributes.System` 플래그가 켜진 항목 제외 권장. 점(`.`)으로 시작하는 이름도 제외하면 mac 동작과 더 가깝다.
3. 항목의 `lastPathComponent`(이름만, 폴더/파일 구분 없음 — 폴더 이름도 포함됨).
4. 이름 오름차순 정렬 (Swift 기본 `<` — C#에서는 `StringComparer.Ordinal` 정렬 권장).
5. **최대 300개**로 절단(`prefix(limit)`).
6. 나열 실패(권한 등) 시 빈 배열.

이 목록은 시트가 열릴 때(`onAppear`) 한 번 스냅숏으로 캡처되어 헤더 카운트·LLM 입력·응답 검증에 모두 동일하게 쓰인다.

LLM user 메시지에서는 이 배열이 **JSON 배열 문자열**로 직렬화된다(`JSONSerialization` → C# `JsonSerializer.Serialize(files)`; 실패 시 `"[]"`).

---

## 3. 프롬프트 원문 (그대로 복사해서 사용할 것)

### 3.1 system 프롬프트 (영문, 한 글자도 바꾸지 말 것)

```
You are a file-organization assistant inside a macOS file manager.
The user gives an instruction in Korean and a list of file names in the current folder.
Respond with ONLY a JSON object, no prose, of this exact shape:
{"operations":[{"action":"move","file":"<exact name from the list>","destination":"<sub-folder name>"},{"action":"delete","file":"<exact name from the list>"}],"summary":"<one short Korean sentence>"}
Two actions are supported:
- "move": relocate the file into the sub-folder "destination" (created if missing).
- "delete": move the file to the Trash. This is what the user means by 삭제/지워/없애/버려/휴지통. Files go to the Trash and remain recoverable. Do NOT set "destination" for delete.
Rules:
- Use ONLY file names that appear in the provided list; never invent names.
- Match files by the criteria in the instruction. For "확장자 X" or "X 파일", match files whose name ends with ".X" (case-insensitive). e.g. "hwp 파일 삭제" → delete every file ending in ".hwp".
- For "move", "destination" is a single sub-folder name (no slashes, no "..", no leading dot). It is created if it does not exist.
- Only include files that should actually be acted on. If none match, return an empty "operations" array.
- Folder/destination names should be short and human-friendly, in Korean when natural.
- The "summary" must state in Korean what will happen (e.g. "hwp 파일 3개를 휴지통으로 옮깁니다.").
```

> Windows 포팅 시 첫 줄의 "macOS file manager"는 "Windows file manager"로 바꾸는 것을 권장(동작 동일).
> "Trash"는 "Recycle Bin"으로 바꿔도 무방하나, 나머지 규칙 문구는 유지할 것.

### 3.2 user 프롬프트 템플릿 (한국어, 그대로)

```
현재 폴더: {folderName}
파일 목록: {files를 JSON 배열로 직렬화한 문자열}
명령: {instruction}
```

- `folderName` = 현재 폴더의 마지막 경로 구성요소 (예: `Downloads`).
- `instruction` = 사용자가 입력한 명령(앞뒤 공백 Trim 후).

---

## 4. AIService 동작 명세

### 4.1 진입점 `organize(folderName, files, instruction, config) → AIPlan`

1. system/user 프롬프트 생성 (3장).
2. `config.Provider`에 따라 `callOllama` 또는 `callGemini` 호출 → 응답 본문 문자열.
3. `parse(content, files)`로 JSON 파싱 + 검증.
4. **폴백 규칙**: 어떤 예외든 발생했고 `Provider == Gemini && FallbackToOllama == true`이면,
   같은 프롬프트로 `callOllama`를 **한 번 더** 시도하고 그 결과를 파싱해 반환. 폴백도 실패하면 폴백의 예외를 던진다.
   (Ollama가 1차 제공자일 때는 폴백 없음. Gemini→Ollama 단방향.)

### 4.2 Ollama 백엔드

**모델 자동 감지 `ollamaChatModel(base, preferred)`**:
1. `GET {base}/api/tags` — 연결 실패 시 `AIError.notRunning`.
2. 응답 JSON `{"models":[{"name":"..."}]}`에서 이름 목록 추출 (파싱 실패 시 빈 목록).
3. `preferred`(소문자화) 와 정확히 일치하거나, `:` 앞 베이스 이름이 같은 모델이 있으면 그것을 사용.
   (예: preferred `gemma4:latest` → 설치된 `gemma4:latest` 또는 `gemma4:xxx` 모두 매치)
4. 없으면 임베딩 모델이 아닌 첫 모델 사용 — 소문자 이름에 `embed` 포함, 또는 `bge`/`nomic` 접두 모델은 건너뜀.
5. 그래도 없으면 `AIError.noModel`.

**채팅 호출**: `POST {base}/api/chat`, `Content-Type: application/json`, **타임아웃 180초**.

```json
{
  "model": "<감지된 모델>",
  "stream": false,
  "format": "json",
  "options": { "temperature": 0.1 },
  "messages": [
    { "role": "system", "content": "<system 프롬프트>" },
    { "role": "user",   "content": "<user 프롬프트>" }
  ]
}
```

- 네트워크 예외 → `AIError.notRunning`.
- 응답은 `{"message":{"content":"..."}}` 형태에서 `message.content` 추출. 디코딩 실패 시
  `AIError.badResponse(응답 앞 300바이트 UTF-8 문자열)`.

### 4.3 Gemini 백엔드

1. API 키 Trim; 비어 있으면 `AIError.geminiNoKey`.
2. 모델 ID: 설정값 Trim, 비어 있으면 `"gemini-2.5-flash"`.
   (주의: AppModel 설정 로드 기본값은 `"gemini-flash-latest"` — 설정이 한 번도 저장되지 않았으면 그 값이 들어오므로
   실제로 `gemini-2.5-flash` 폴백이 쓰이는 건 설정값이 공백뿐일 때.)
3. `POST https://generativelanguage.googleapis.com/v1beta/models/{modelID}:generateContent?key={key}`,
   `Content-Type: application/json`, **타임아웃 120초**.

```json
{
  "systemInstruction": { "parts": [ { "text": "<system 프롬프트>" } ] },
  "contents": [ { "role": "user", "parts": [ { "text": "<user 프롬프트>" } ] } ],
  "generationConfig": { "responseMimeType": "application/json", "temperature": 0.1 }
}
```

4. 네트워크 예외 → `AIError.badResponse("네트워크 오류: {예외 메시지}")`.
5. HTTP status != 200 → 응답 JSON의 `error.message`를 꺼내
   `AIError.badResponse("Gemini {statusCode}: {message 또는 응답 앞 200바이트}")`.
6. 정상 응답에서 `candidates[0].content.parts[0].text` 추출. 실패 시 `badResponse(앞 300바이트)`.

### 4.4 파싱 + 검증 `parse(content, files)`

1. `content`를 UTF-8 → `AIPlan` JSON 디코드. 실패 시 `AIError.badResponse(content 전체)`.
2. `operations` 필터링(통과 못 한 항목은 **조용히 제거**):
   - `op.file`이 **원본 `files` 목록에 정확히(대소문자 포함) 존재**해야 함 — 환각 이름 차단.
   - `action == "move"` → `isSafeDestination(destination)` 통과해야 함.
   - `action == "delete"` → 무조건 통과 (destination 불필요; 휴지통 이동이므로 복구 가능).
   - 그 외 action 값 → 제거.
3. `summary`는 그대로 유지하고 필터링된 operations로 `AIPlan` 반환.

### 4.5 목적지 안전성 `isSafeDestination(name)`

Trim(공백) 후:
- 비어 있지 않고
- `/` 미포함
- `".."` 아님
- `.` 으로 시작하지 않음
- `~` 로 시작하지 않음

**Windows 추가 검증 필수**: `\`, `:`, `*?"<>|` 등 금지 문자, 예약 이름(`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`),
이름 끝의 점/공백도 거부할 것 (`Path.GetInvalidFileNameChars()` 활용). 단일 폴더 이름 1단계만 허용(경로 분리자 일절 금지).

---

## 5. 계획 적용 `AppModel.applyAIPlan(ops)`

시트의 "적용" 버튼에서 호출. `base = selectedFolder` (시트가 떠 있는 동안 폴더가 바뀌지 않는다는 전제).

1. **재검사**: `aiOrganizeBlocked(base)`이면 중단하고 오류 메시지(우선순위 순):
   - 응용 프로그램 위치: `응용 프로그램 폴더는 AI 파일 정리에서 제외됩니다.`
   - 보호 폴더: `시스템 폴더는 AI 파일 정리에서 제외됩니다.`
   - 사용자 예외: `이 폴더는 AI 정리 예외 폴더로 지정되어 정리할 수 없습니다.`
2. 각 op에 대해 (`moved`, `trashed`, `failures: [String]` 집계):
   - `src = base + op.file`. 존재하지 않으면 실패 기록 `"{file}: 항목 없음"` 후 다음 항목.
   - **delete**: 휴지통으로 이동(영구 삭제 아님). mac: `FileManager.trashItem`; 실패 시 Finder AppleScript(`FileOperations.trashViaFinder`)로 재시도, 그래도 실패하면 `"{file}: {오류 메시지}"` 기록.
   - **move**:
     - `isSafeDestination` 재검증 — 실패 시 **조용히 skip** (failure 기록 없음).
     - `destDir = base + destination`. `src`와 `destDir`이 같은 경로(정규화 비교)면 skip — 폴더를 자기 자신으로 이동 방지.
     - `destDir` 없으면 생성(중간 경로 포함).
     - `dest = destDir + op.file`. 대상에 같은 이름이 이미 있으면 `FileOperations.uniqueURL`로 회피:
       `"{base} 2.{ext}"`, `"{base} 3.{ext}"`, … (n=1은 원래 이름, 2부터 공백+숫자 접미; 확장자가 없으면 점 없이).
     - 이동 실행. 예외 시 `"{file}: {오류 메시지}"` 기록.
3. 디테일 뷰 새로고침(`reloadDetail`) + 사이드바 갱신(`refreshSidebar`).
4. **결과 알림 문자열**:
   - 부분 문구: moved>0 → `{moved}개 정리`, trashed>0 → `{trashed}개 휴지통으로 이동`.
   - 둘 다 0 → `처리한 항목이 없습니다`, 아니면 `AI가 {부분들을 ", "로 연결}했습니다.`
   - 실패 없음 → 정보 토스트(infoMessage)로 위 문자열.
   - 실패 있음 → 오류 토스트(errorMessage):
     `{완료 문구}\n{failures.count}개 실패:\n{앞 5개를 \n로 연결}{6개 이상이면 "\n…외 {count-5}개"}`

---

## 6. 진입 차단 / 제외 폴더 규칙

### 6.1 진입점 `requestAIOrganize()` (툴바 AI 버튼)

1. `isProtectedLocation(selectedFolder)` → 차단 + 메시지:
   - `isApplicationsLocation`이면 `응용 프로그램 폴더는 AI 파일 정리에서 제외됩니다.`
   - 아니면 `시스템 폴더는 AI 파일 정리에서 제외됩니다.`
2. `isExcluded(selectedFolder)` → 차단 + `이 폴더는 AI 정리 예외 폴더로 지정되어 정리할 수 없습니다.`
3. 통과하면 AI 정리 시트 열기 (`sheet = .aiOrganize`).

툴바 AI 아이콘 비활성화와 실행 차단의 단일 기준은 `aiOrganizeBlocked(url) = isExcluded(url) || isProtectedLocation(url)`.

### 6.2 보호 폴더 (시스템 기본 예외) `isProtectedLocation`

- **하위 폴더까지 재귀 차단**: `/Applications`, `/System`, `/Library`, `~/Library`
- **그 폴더 자체만 차단** (하위는 허용): `/` (루트), `/Users`
- 경로 비교는 경계(`/`) 기준 — `/A/B`가 `/A/BC`를 잘못 포함하지 않게 `path == base || path.StartsWith(base + "/")`.

**Windows 대응안**:
- 재귀 차단: `C:\Windows`, `C:\Program Files`, `C:\Program Files (x86)`, `C:\ProgramData`, `%USERPROFILE%\AppData`
- 자체만 차단: 각 드라이브 루트(`C:\` 등), `C:\Users`
- 경로 비교는 대소문자 무시(`OrdinalIgnoreCase`) + `\` 경계.

### 6.3 응용 프로그램 위치 `isApplicationsLocation` (오류 문구 구분용)

`/Applications`, `/System/Applications`, `~/Applications` 및 그 하위.
Windows: `C:\Program Files`, `C:\Program Files (x86)`, `%LOCALAPPDATA%\Programs` 및 그 하위로 매핑.

### 6.4 사용자 지정 예외 폴더

- 저장: UserDefaults `XFinder.aiExcludedFolders.v1` — **경로 문자열 배열**.
- `isExcluded(url)`: 등록된 폴더 자신 또는 그 하위 폴더이면 true (경로 경계 비교).
- `isDirectlyExcluded(url)`: 정확히 그 경로가 등록돼 있는지 (컨텍스트 메뉴 토글 표시용).
- `addExcludedFolder(url)`: 디렉터리만 허용, 중복 등록 무시.
  성공 토스트: `“{폴더이름}” 및 하위 폴더를 AI 정리 예외로 등록했습니다.`
- `removeExcludedFolder(url)`: 해제 토스트: `“{폴더이름}”의 AI 정리 예외를 해제했습니다.`

---

## 7. AIOrganizeSheet UI 명세

### 7.1 전체 구조와 상태 흐름

- 모달 시트, **고정 크기 480 × 600**.
- 세로 스택: [헤더(74px)] → [Divider] → [본문: phase별 뷰가 가득 채움].
- 상태 흐름: `input` →(정리 분석)→ `loading` →(성공·작업 있음)→ `preview` →(적용/취소)→ 닫기
  - 성공·작업 0개 → `empty`, 실패 → `error`. `preview`의 "다시 입력", `empty`의 "다시 입력", `error`의 "다시 시도"는 모두 `input`으로 복귀(입력 내용 유지).
- 열릴 때(`onAppear`): `files = currentFolderEntries()` 스냅숏 + 입력창에 비동기 포커스.
- **비동기/취소**: 분석은 백그라운드 Task. 명시적 취소 없음 — 시트를 닫아도 요청은 계속 진행되고 결과는 버려진다.
  Windows에서는 창 닫힘 시 `CancellationTokenSource.Cancel()`로 HTTP 요청을 끊는 것을 권장(개선).
  결과 반영은 UI 스레드(Dispatcher)에서 수행.

### 7.2 헤더 (모든 phase 공통)

- 배경: 대각선 LinearGradient (topLeading → bottomTrailing)
  - 시작: RGB(0.49, 0.31, 0.96) ≈ `#7D4FF5` (보라)
  - 끝: RGB(0.21, 0.55, 0.99) ≈ `#368CFC` (파랑)
- 높이 74, 좌우 패딩 20, 내부 HStack 간격 12.
- 좌측 아이콘: SF `sparkles`, 22pt semibold, 흰색.
- 제목: `AI 파일 정리` — 18pt bold, 흰색.
- 부제(11pt, 흰색 85% 불투명): `‘{폴더이름}’ · 항목 {files.count}개 · {제공자 라벨}`
  (제공자 라벨: `로컬 (Ollama)` 또는 `Gemini`)
- 우상단 닫기 버튼: SF `xmark.circle.fill` 17pt, 흰색 85%, 패딩 10, **Esc 키 바인딩**(cancelAction), 포커스 링 없음.

### 7.3 input 화면 (패딩 18, 세로 간격 14)

1. 안내문: `어떻게 정리할까요? 자연어로 알려 주세요.` — 13pt semibold.
2. 멀티라인 입력창: 높이 96, 코너 반경 10, 배경 `Color.primary.opacity(0.05)`,
   테두리 `Color.secondary.opacity(0.25)` 1px. 텍스트 13pt.
   플레이스홀더(입력이 비었을 때만): `예: 확장자별로 폴더를 만들어 정리해줘` — 13pt, secondary 색,
   패딩 가로 12 / 세로 11. 에디터 자체 패딩 가로 8 / 세로 6.
3. 라벨 `빠른 예시` — 11pt semibold, secondary.
4. 예시 칩 목록(`FlowChips`, 실제로는 세로 스택 간격 6): 각 칩은 11pt 텍스트,
   패딩 가로 10 / 세로 5, 캡슐 배경 `accentColor.opacity(0.12)`, 글자색 accentColor.
   클릭하면 입력창 내용이 해당 예시로 **교체**된다.
5. (Spacer)
6. 하단 행:
   - 좌측 개인정보 라벨(10pt, secondary, 아이콘+텍스트):
     - Ollama: 아이콘 SF `lock.shield`, 텍스트 `로컬 LLM(Ollama)으로 처리 — 파일이 외부로 나가지 않습니다.`
     - Gemini: 아이콘 SF `cloud`, 텍스트 `Gemini(클라우드)로 처리 — 파일 ‘이름’이 구글로 전송됩니다.`
   - 우측 `정리 분석` 버튼 — **Enter 키 바인딩**(defaultAction), 입력이 공백뿐이면 비활성화.
7. Divider.
8. 안내 링크 2줄 (세로 간격 6). 각 링크(`guideLink`): 아이콘 + 제목 + 우측 SF `chevron.right`(9pt semibold),
   11pt medium, 글자색 accentColor, 패딩 가로 12 / 세로 9, 코너 반경 8 배경 `accentColor.opacity(0.08)`, 전체 폭.
   - SF `questionmark.circle` + `로컬 AI 설정 방법 — Ollama 설치 안내` → LocalAIGuideView 팝오버(위쪽 화살표)
   - SF `cloud` + `Gemini 설정 방법 — API 키 발급 안내` → GeminiGuideView 팝오버(위쪽 화살표)

### 7.4 loading 화면 (패딩 24, 세로 간격 14, 중앙 정렬)

- 큰 ProgressView(불확정 스피너).
- `AI가 파일을 분석하고 있습니다…` — 13pt medium.
- `로컬 모델이라 수십 초 걸릴 수 있어요.` — 11pt, secondary.

### 7.5 preview 화면 (패딩 18, 세로 간격 12)

- 첫 줄: SF `checkmark.circle.fill`(초록) + `plan.summary` — 13pt semibold, 줄바꿈 허용.
- 둘째 줄(11pt, secondary): `summaryLine` 조합 —
  - moves>0 → `파일 {moves}개를 {고유 목적지 폴더 수}개 폴더로 이동`
  - deletes>0 → `{deletes}개를 휴지통으로 이동`
  - 둘 다 0 → `적용할 항목이 없습니다.` / 있으면 부분들을 ` · `로 연결 후 끝에 `합니다.` 붙임.
    (예: `파일 5개를 2개 폴더로 이동 · 3개를 휴지통으로 이동합니다.`)
- 작업 목록 ScrollView: 배경 코너 반경 10 `Color.primary.opacity(0.04)`, 내부 패딩 12, 그룹 간 간격 14.
  - **이동 그룹**: 목적지 폴더별로 묶음(LLM 출력의 첫 등장 순서 유지 — `groupedDestinations`).
    그룹 헤더: SF `folder.fill` + 폴더 이름 — 12pt semibold, **파란색**.
    각 파일 행: SF `arrow.turn.down.right`(9pt, secondary) + 파일명 12pt, 1줄 제한·중간 생략(truncationMode .middle), 좌측 들여쓰기 6.
  - **삭제 그룹**(있을 때만): 헤더 SF `trash.fill` + `휴지통으로 이동 (복구 가능)` — 12pt semibold, **빨간색**. 파일 행 형식은 동일.
- 삭제가 있으면 하단 안내(10pt, secondary, SF `info.circle`):
  `삭제 항목은 영구 삭제가 아니라 휴지통으로 이동되며, 필요하면 복구할 수 있습니다.`
- 버튼 행: 좌측 `다시 입력`(phase=input) / 우측 `취소`(닫기), `적용`(강조 스타일, **Enter 바인딩**) — 적용 시 `applyAIPlan(plan.operations)` 호출 후 시트 닫기.

### 7.6 empty 화면 (패딩 24, 세로 간격 12, 중앙)

- SF `tray` 30pt, secondary.
- `옮길 파일이 없다고 판단했어요.` — 13pt medium.
- `plan.summary` — 11pt, secondary, 중앙 정렬.
- `다시 입력` 버튼 (Enter 바인딩).

### 7.7 error 화면 (패딩 24, 세로 간격 12, 중앙)

- SF `exclamationmark.triangle.fill` 28pt, **주황색**.
- 오류 텍스트(`errorText`, 없으면 `오류가 발생했습니다.`) — 12pt, 중앙 정렬, 여러 줄 허용.
- `다시 시도` 버튼 (Enter 바인딩) — input으로 복귀.

### 7.8 LocalAIGuideView (로컬 AI 안내 팝오버, 360 × 440)

- 헤더(패딩 가로 16 / 세로 13, 간격 10): SF `lock.shield` 18pt semibold accentColor /
  제목 `로컬 AI 설정 방법`(14pt bold) / 부제 `Ollama로 내 Mac에서 직접 정리 — 파일이 외부로 나가지 않습니다.`(10pt secondary) /
  우측 닫기 SF `xmark.circle.fill` 15pt secondary. 이어서 Divider, 본문 ScrollView(패딩 16, 단계 간격 16).
- 단계 블록(`step`): 좌측 번호 원(지름 20, accentColor 채움, 흰색 11pt bold 숫자) + 제목 12pt semibold + 내용. 본문 텍스트는 11pt secondary.
- `model` = 설정의 ollamaModel(Trim, 비면 `gemma4:latest`) — 안내문에 동적으로 삽입.
- 단계 내용(문자열 원문):
  1. `Ollama 설치` — `아래 버튼으로 Ollama 공식 사이트에서 macOS용 앱을 받아 설치하세요.` + 버튼
     `ollama.com/download 열기`(SF `arrow.up.right.square`, 강조·small) → 기본 브라우저로 `https://ollama.com/download` 열기.
     (Windows 포팅 시 "macOS용 앱" → "Windows용 앱"으로 수정 권장)
  2. `‘{model}’ 모델 내려받기` — `터미널에서 아래 명령으로 현재 설정된 모델({model})을 한 번 받아 두세요. 다른 모델을 쓰려면 설정 → ‘AI 모델’에서 모델 이름을 바꾸면 안내도 그에 맞춰 바뀝니다.` + 명령 행 `ollama pull {model}`.
  3. `Ollama 실행 확인` — `Ollama 앱이 실행 중이면 자동으로 준비됩니다. 수동 실행이 필요하면:` + `ollama serve` 명령 행 +
     `받은 모델 목록은 아래 명령으로 확인할 수 있어요.` + `ollama list` 명령 행.
  4. `xFinder에서 로컬 AI 켜기` — `설정(⚙️) → ‘AI 모델’에서 제공자를 ‘로컬 (Ollama)’로 바꾸세요. 서버 주소와 모델 이름도 같은 화면에서 바꿀 수 있습니다. 이후 AI 파일 정리가 내 Mac에서 ‘{model}’ 모델로 처리됩니다.`
     (Windows: "내 Mac" → "내 PC")
- 명령 행(`commandRow`): 모노스페이스 11pt, 텍스트 선택 가능, 패딩 가로 10 / 세로 7,
  코너 반경 6 배경 `Color.primary.opacity(0.06)`, 우측 복사 버튼 SF `doc.on.doc` 11pt — 클릭 시 클립보드에 명령 복사. 툴팁: `명령어 복사`.

### 7.9 GeminiGuideView (Gemini 안내 팝오버, 360 × 440 — 형식 동일)

- 헤더: SF `cloud` / 제목 `Gemini 설정 방법` / 부제 `Google Gemini API로 정리 — 파일 ‘이름’만 전송되고 내용은 전송되지 않습니다.`
- `model` = 설정의 geminiModel(Trim, 비면 `gemini-flash-latest`).
- 단계:
  1. `Google AI Studio에서 API 키 발급` — `구글 계정으로 로그인한 뒤 ‘API 키 만들기(Create API key)’ 버튼을 누르면 무료로 발급됩니다. 신용카드 등록 없이 무료 등급만으로도 파일 정리에는 충분합니다.` + 버튼 `aistudio.google.com/apikey 열기` → `https://aistudio.google.com/apikey` 열기.
  2. `xFinder에 API 키 등록` — `설정(⚙️) → ‘AI 모델’에서 제공자를 ‘Gemini’로 선택하고, 발급받은 키를 ‘Gemini API 키’ 칸에 붙여넣으세요. 키는 이 Mac의 설정에만 저장되며 구글 외 다른 곳으로 보내지 않습니다.` (Windows: "이 Mac" → "이 PC")
  3. `모델 확인 (선택)` — `현재 설정된 모델은 ‘{model}’ 입니다. 같은 화면의 ‘모델’ 칸에서 다른 Gemini 모델로 바꿀 수 있어요. 예:` + 복사 행 `gemini-flash-latest`, `gemini-2.5-flash`. 복사 버튼 툴팁: `복사`.
  4. `무엇이 전송되나요?` — `정리 분석 시 파일 ‘이름’과 폴더 구조만 Google 서버로 전송됩니다. 파일 내용은 절대 전송되지 않습니다. 파일 이름조차 외부로 보내고 싶지 않다면 ‘로컬 AI(Ollama)’를 사용하세요.`

### 7.10 아이콘 매핑 (SF Symbol → Segoe Fluent Icons 글리프 제안)

| SF Symbol | 용도 | Segoe Fluent Icons 제안 | 대안 이모지 |
|---|---|---|---|
| `sparkles` | 헤더/AI 버튼 | `U+E78D` (AutoEnhanceOn) | ✨ |
| `xmark.circle.fill` | 닫기 | `U+E8BB` (ChromeClose) 또는 `U+EB90` (StatusErrorFull) | — |
| `lock.shield` | 로컬 처리 안내 | `U+EA18` (Shield) | 🔒 |
| `cloud` | Gemini 안내 | `U+E753` (Cloud) | ☁️ |
| `questionmark.circle` | 안내 링크 | `U+E897` (Help) | ❓ |
| `chevron.right` | 링크 화살표 | `U+E76C` (ChevronRight) | › |
| `checkmark.circle.fill` | 계획 요약 | `U+E930` (Completed) | ✅ |
| `folder.fill` | 이동 그룹 헤더 | `U+E8D5` (FolderFill) | 📁 |
| `arrow.turn.down.right` | 파일 행 들여쓰기 | 텍스트 글리프 `↳` 권장 | ↳ |
| `trash.fill` | 삭제 그룹 헤더 | `U+E74D` (Delete) | 🗑️ |
| `info.circle` | 휴지통 안내 | `U+E946` (Info) | ℹ️ |
| `tray` | empty 상태 | `U+ED25` (FolderOpen) | 📥 |
| `exclamationmark.triangle.fill` | error 상태 | `U+E7BA` (Warning) | ⚠️ |
| `arrow.up.right.square` | 외부 링크 버튼 | `U+E8A7` (OpenInNewWindow) | ↗ |
| `doc.on.doc` | 복사 버튼 | `U+E8C8` (Copy) | 📋 |

(글리프 코드는 Segoe MDL2/Fluent 공통 베스트에포트 — 빌드 시 실제 폰트에서 확인할 것.)

---

## 8. 영속화 (UserDefaults → Windows 설정 파일)

모두 `UserDefaults.standard` (Windows: `%APPDATA%\XFinder\settings.json` 등 단일 JSON 설정 파일 권장).

| 키 | 형식 | 기본값(미설정 시) | 설명 |
|---|---|---|---|
| `XFinder.aiProvider.v1` | String (`"ollama"`/`"gemini"`) | **`gemini`** (파싱 실패 포함) | LLM 제공자 |
| `XFinder.geminiAPIKey.v1` | String | `""` | Gemini API 키 — **평문 저장** (소스에는 키를 절대 두지 않음) |
| `XFinder.geminiModel.v1` | String | 빈 값이면 `"gemini-flash-latest"` | Gemini 모델 이름 |
| `XFinder.ollamaBaseURL.v1` | String | 빈 값이면 `"http://localhost:11434"` | Ollama 서버 주소 |
| `XFinder.ollamaModel.v1` | String | 빈 값이면 `"gemma4:latest"` | 우선 로컬 모델 |
| `XFinder.aiExcludedFolders.v1` | String 배열 (절대 경로) | `[]` | AI 정리 예외 폴더 목록 (하위 폴더 포함 차단) |

각 프로퍼티는 변경 즉시(didSet) 저장된다. Windows에서도 설정 변경 시 즉시 파일에 반영할 것.
**보안 노트**: 원본은 API 키를 평문 UserDefaults에 저장(문서에 명시). Windows에서는 DPAPI(`ProtectedData.Protect`, CurrentUser 범위)로
암호화 저장을 권장하되, 마이그레이션 시 키 이름/저장 방식 변경에 주의(doc 주의사항).

---

## 9. Windows 포팅 노트 (mac 전용 API → 대응안)

| macOS API / 개념 | 원본 용도 | Windows 대응안 |
|---|---|---|
| `FileManager.trashItem` | delete 작업을 휴지통으로 | `SHFileOperation`(FO_DELETE + `FOF_ALLOWUNDO\|FOF_NOCONFIRMATION\|FOF_SILENT`) 또는 `IFileOperation`. C#에서는 `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile/DeleteDirectory(..., RecycleOption.SendToRecycleBin)`이 가장 간단 |
| `FileOperations.trashViaFinder` (AppleScript로 Finder에 위임 — 권한 실패 시 2차 시도) | 휴지통 폴백 | **불필요/포팅 불가** — Windows 셸 API가 이미 셸 경유라 동일 폴백 무의미. 실패 시 그대로 실패 기록. (선택: 권한 오류 시 UAC 승격 재시도 안내) |
| `NSWorkspace.shared.open(URL)` | 안내 팝업에서 웹사이트 열기 | `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })` |
| `NSPasteboard.general` | 명령어/모델명 복사 | `System.Windows.Clipboard.SetText(...)` |
| `URLSession.shared.data` (async) | Ollama/Gemini HTTP | `HttpClient` (요청별 `CancellationTokenSource(TimeSpan)`으로 타임아웃 180s/120s 재현) |
| `JSONSerialization` / `JSONDecoder` | 페이로드 생성·파싱 | `System.Text.Json` (`JsonSerializerOptions { PropertyNameCaseInsensitive = true }`, 직렬화는 소문자 키 명시) |
| SwiftUI sheet | 모달 시트 | WPF 모달 `Window`(WindowStyle=None 커스텀 헤더, 480×600 고정, ResizeMode=NoResize), Owner=메인 창 |
| SwiftUI popover (arrowEdge: .top) | 안내 팝업 2종 | WPF `Popup`(StaysOpen=false) 또는 작은 모달 창 360×440 |
| `@FocusState` + async 포커스 | 입력창 자동 포커스 | `Dispatcher.BeginInvoke(() => textBox.Focus(), DispatcherPriority.Input)` |
| `keyboardShortcut(.defaultAction/.cancelAction)` | Enter/Esc | WPF `Button.IsDefault = true` / `IsCancel = true` |
| `Color.primary.opacity(x)` / `Color.secondary` | 반투명 배경/보조 텍스트 | 테마 전경색에 알파 적용 (라이트: 검정 기반, 다크: 흰색 기반). secondary ≈ 60% 전경 |
| `Color.accentColor` | 칩/링크/단계 배지 | 시스템 강조색 `SystemParameters.WindowGlassBrush` 또는 `UISettings.GetColorValue(UIColorType.Accent)` |
| `.skipsHiddenFiles` | 목록에서 숨김 제외 | `attributes.HasFlag(FileAttributes.Hidden)` (또는 System 포함) 제외 |
| `URL.standardizedFileURL` 경로 비교 | 예외/보호 폴더 판정 | `Path.GetFullPath` + `TrimEnd('\\')` + `OrdinalIgnoreCase` 비교, `\` 경계 체크 |
| `ollama serve` 안내 (mac 터미널) | 로컬 AI 가이드 | Windows에서는 Ollama 설치 시 서비스가 자동 실행 — 안내 문구를 PowerShell 기준으로 수정 |
| macOS 보호 경로 (`/System`, `/Library` 등) | 시스템 폴더 차단 | 6.2의 Windows 경로 세트로 교체 |
| Gemini 키 평문 UserDefaults | 키 저장 | settings.json + DPAPI 암호화 권장 (8장) |

**포팅 불가/무의미 항목**
- `trashViaFinder` AppleScript 폴백: 상기처럼 생략.
- "Finder 안의 macOS 파일 관리자"라는 프롬프트 문구: "Windows file manager"로 자연스럽게 교체 (스키마·규칙은 동일 유지).
- 가이드 문구의 "macOS용 앱" / "내 Mac" / "이 Mac": Windows용 문구로 치환 (해당 위치 7.8 / 7.9에 명시).

**기타 구현 메모**
- LLM 응답 검증에서 탈락한 항목은 사용자에게 알리지 않는다(원본과 동일) — 결과가 0개가 되면 `empty` 화면이 그 역할을 한다.
- `files`는 시트 오픈 시점 스냅숏이므로, 적용 시점에 파일이 사라졌으면 `"{file}: 항목 없음"` 실패로 처리된다(5장).
- move 충돌 회피 이름 규칙(`이름 2.ext`, `이름 3.ext`…)은 다른 파일 작업(복사/이동)과 공유되는 `uniqueURL` 규칙이다 — 동일하게 구현.
- 분석 중 시트 강제 닫기 → 진행 중 요청 결과는 무시(원본). Windows에선 취소 토큰 연결 권장.

---

## 10. UI 문자열 전체 목록 (원문 그대로 — 포팅 시 동일하게 사용)

### 시트 본문
- `AI 파일 정리`
- `‘{폴더}’ · 항목 {N}개 · {제공자}` (제공자: `로컬 (Ollama)` / `Gemini`)
- `어떻게 정리할까요? 자연어로 알려 주세요.`
- `예: 확장자별로 폴더를 만들어 정리해줘` (플레이스홀더)
- `빠른 예시`
- 예시 칩 4종: `확장자 종류별로 폴더를 만들어 정리해줘` / `이미지 파일만 ‘이미지’ 폴더로 모아줘` / `날짜(연-월)별 폴더로 분류해줘` / `스크린샷을 ‘스크린샷’ 폴더로 옮겨줘`
- `로컬 LLM(Ollama)으로 처리 — 파일이 외부로 나가지 않습니다.`
- `Gemini(클라우드)로 처리 — 파일 ‘이름’이 구글로 전송됩니다.`
- `정리 분석`
- `로컬 AI 설정 방법 — Ollama 설치 안내`
- `Gemini 설정 방법 — API 키 발급 안내`
- `AI가 파일을 분석하고 있습니다…`
- `로컬 모델이라 수십 초 걸릴 수 있어요.`
- `파일 {N}개를 {M}개 폴더로 이동` / `{K}개를 휴지통으로 이동` / 연결자 ` · ` / 접미 `합니다.` / `적용할 항목이 없습니다.`
- `휴지통으로 이동 (복구 가능)`
- `삭제 항목은 영구 삭제가 아니라 휴지통으로 이동되며, 필요하면 복구할 수 있습니다.`
- `다시 입력` / `취소` / `적용`
- `옮길 파일이 없다고 판단했어요.`
- `오류가 발생했습니다.` / `다시 시도`

### 오류 (AIError, 1.5 표 참고)
- `로컬 LLM(Ollama)에 연결할 수 없습니다.` + 줄바꿈 + `터미널에서 \`ollama serve\` 가 실행 중인지 확인하세요.`
- `사용할 수 있는 채팅 모델이 없습니다.` + 줄바꿈 + `\`ollama pull gemma4\` 등으로 모델을 받아 주세요.`
- `Gemini API 키가 없습니다.` + 줄바꿈 + `설정 → AI 모델에서 키를 입력하세요. (aistudio.google.com 에서 발급)`
- `AI 응답을 이해하지 못했습니다.` + 줄바꿈 + 상세
- `네트워크 오류: {상세}` / `Gemini {상태코드}: {상세}` (badResponse 상세부)

### 차단/적용 결과 (AppModel)
- `응용 프로그램 폴더는 AI 파일 정리에서 제외됩니다.`
- `시스템 폴더는 AI 파일 정리에서 제외됩니다.`
- `이 폴더는 AI 정리 예외 폴더로 지정되어 정리할 수 없습니다.`
- `{file}: 항목 없음`
- `{N}개 정리` / `{N}개 휴지통으로 이동` / `처리한 항목이 없습니다` / `AI가 {…}했습니다.`
- `{완료}\n{K}개 실패:\n{목록}` / `…외 {K}개`
- `“{폴더}” 및 하위 폴더를 AI 정리 예외로 등록했습니다.`
- `“{폴더}”의 AI 정리 예외를 해제했습니다.`

### 안내 팝업 (7.8 / 7.9에 전체 원문 수록)
- 로컬: `로컬 AI 설정 방법` / `Ollama로 내 Mac에서 직접 정리 — 파일이 외부로 나가지 않습니다.` / 단계 제목 `Ollama 설치`, `‘{model}’ 모델 내려받기`, `Ollama 실행 확인`, `xFinder에서 로컬 AI 켜기` / 버튼 `ollama.com/download 열기` / 명령 `ollama pull {model}`, `ollama serve`, `ollama list` / 툴팁 `명령어 복사`
- Gemini: `Gemini 설정 방법` / `Google Gemini API로 정리 — 파일 ‘이름’만 전송되고 내용은 전송되지 않습니다.` / 단계 제목 `Google AI Studio에서 API 키 발급`, `xFinder에 API 키 등록`, `모델 확인 (선택)`, `무엇이 전송되나요?` / 버튼 `aistudio.google.com/apikey 열기` / 복사 값 `gemini-flash-latest`, `gemini-2.5-flash` / 툴팁 `복사`
- 본문 긴 문장들은 7.8 / 7.9 절의 원문을 그대로 사용할 것 (`’ ‘` 곡선 따옴표 유지).
