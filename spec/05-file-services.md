# 05 — 파일 서비스 포팅 스펙 (File Services)

macOS XFinder → Windows C# .NET 8 WPF 포팅 스펙.
대상 소스:
- `Sources/XFinder/Services/FileSystemService.swift`
- `Sources/XFinder/Services/FileOperations.swift`
- `Sources/XFinder/Services/SystemActions.swift`
- `Sources/XFinder/Services/RecentsService.swift`
- `Sources/XFinder/Services/TagService.swift`
- `Sources/XFinder/Services/ThumbnailCache.swift`
- `Sources/XFinder/Services/HangulNormalize.swift`
- `Sources/XFinder/Services/WindowsName.swift`
- `doc/file-operations.md`
- (참조) `Model/FileItem.swift`의 `FileItem`, `Model/AppModel.swift`의 `OperationProgress`/`Clipboard` — 클립보드 연동 동작 명세 포함.

---

## 0. 공통 참조 타입 (다른 스펙 영역 소유, 여기서 시그니처만)

```csharp
// FileItem (Model 영역 소유) — 이 문서의 서비스들이 생산/소비하는 단위
public sealed record FileItem
{
    public string Path;          // Swift: url (절대 경로)
    public string Name;
    public bool IsDirectory;
    public bool IsSymlink;       // Windows: ReparsePoint(심볼릭 링크/정션)
    public bool IsHidden;
    public long Size;            // 바이트; 폴더 등 미측정이면 -1
    public DateTime Modified;
    public string Ext;           // 소문자, 점 없음, 없으면 ""
    public bool IsParent;        // ".." 행
    public DateTime Created;     // 기본 DateTime.MinValue
    public string TypeName;      // 종류(현지화 설명, 예: "PDF 문서"); 빈 값이면 표시층에서 대체
    // mac의 isBundle: 디렉터리 + 확장자 ∈ {app, bundle, framework, rtfd, playground}
    //  → Windows에선 의미 없음. 항상 false 또는 프로퍼티 자체 제거.
}

// OperationProgress (Model 영역 소유) — UI 스레드에서 관찰되는 진행률 객체
public sealed class OperationProgress   // INotifyPropertyChanged
{
    public string Title;          // 예: "이동 중…", "복사 중…"
    public string CurrentFile = "";
    public long CompletedUnits = 0;
    public long TotalUnits = 0;
    public bool IsCancelled = false;   // UI의 취소 버튼이 true로 설정
    public double Fraction => TotalUnits <= 0 ? 0 : Math.Min(1, (double)CompletedUnits / TotalUnits);
}
```

---

## 1. FileSystemService — 디렉터리 나열·검색·크기 계산

### 1.1 데이터 구조

```csharp
/// 종류별(by-type) 분석 결과 한 카테고리.
/// Files 는 크기 내림차순 정렬된 (경로, 크기) 인덱스로 최대 TypeIndexLimit개.
/// UI는 여기서 페이지 단위로 잘라 FileItem으로 변환한다(전체를 한꺼번에 만들지 않음).
public readonly record struct TypeBreakdown(
    string Name,                       // "문서"/"이미지"/"동영상"/"음악"/"압축"/"기타"
    long Bytes,
    int Count,
    IReadOnlyList<TypeFileEntry> Files);

/// 카테고리 내역 한 항목 — 경로와 크기만 담는 경량 값. Id = Path.
public readonly record struct TypeFileEntry(string Path, long Size);
```

### 1.2 `list(directory)` — 디렉터리 한 단계 나열

- mac: `FileManager.contentsOfDirectory`(옵션 없음 = **숨김 포함**). 숨김 표시 여부는 모델(상위)에서 필터.
- 항목마다 채우는 값:
  - `name`: 파일 시스템 이름
  - `isDirectory`
  - `isSymlink`
  - `isHidden`: 리소스 값 또는 (실패 시) 이름이 `.`으로 시작하면 true
  - `size`: **디렉터리면 -1**, 파일이면 논리 크기(실패 시 0)
  - `modified`: 수정일 (실패 시 `DateTime.MinValue` 상당)
  - `ext`: 확장자 소문자(점 제외)
  - `isParent: false`
  - `created`: 생성일 (실패 시 MinValue)
  - `typeName`: 현지화된 종류 설명(macOS `localizedTypeDescription`)
- 반환: `Result<[FileItem], Error>` — 실패 시 에러 그대로 전달(권한 없음 등). 정렬은 하지 않음(상위 모델이 정렬).
- **Windows 포팅**:
  - `new DirectoryInfo(dir).EnumerateFileSystemInfos()` 1회 순회로 attributes/size/시각 모두 획득 (`FileSystemInfo`는 find-data 캐시라 항목당 추가 syscall 없음 — mac의 resourceValues 일괄 조회와 등가).
  - `IsHidden`: `FileAttributes.Hidden | FileAttributes.System` 보유 **또는** 이름이 `.`으로 시작(맥/리눅스에서 온 도트파일 처리) → true.
  - `IsSymlink`: `FileAttributes.ReparsePoint`.
  - `TypeName`: `SHGetFileInfo(SHGFI_TYPENAME)` (예: "텍스트 문서"). 호출이 비싸면 확장자별 캐시.
  - 실패(UnauthorizedAccessException/IOException)는 Result 실패로 반환해 UI가 메시지 표시.

### 1.3 `searchRecursive(root, needle, showHidden, limit = 1000)` — 이름 부분일치 재귀 검색

- needle은 **이미 소문자로 정규화되어 들어옴** — 비교는 `name.lowercased().contains(needle)` (대소문자 무시 부분일치).
- 숨김: `showHidden == false`면 숨김 항목·숨김 트리 전체 스킵(mac `.skipsHiddenFiles`).
- 에러 핸들러는 항상 계속(true) — 권한 없는 폴더는 조용히 건너뜀.
- `limit`(기본 1000)개 모이면 즉시 중단.
- 결과 정렬: **폴더 우선**, 그다음 이름 `localizedStandardCompare`(Finder식 자연 정렬: "파일 2" < "파일 10").
- 채우는 필드는 1.2와 동일하되 `created`/`typeName` 없음(기본값).
- 느릴 수 있으므로 **백그라운드 스레드에서 호출** (UI 측에서 Task.Run + 재검색 시 CancellationToken으로 이전 검색 취소 권장).
- **Windows 포팅**: `Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = showHidden ? 0 : (FileAttributes.Hidden|FileAttributes.System) })`. 자연 정렬은 `StrCmpLogicalW`(shlwapi) P/Invoke 비교자 사용.

### 1.4 `subfolders(of:showHidden:)` — 사이드바 트리용 직속 하위 폴더

- 디렉터리이면서 **패키지가 아닌** 것만 (mac 패키지 = .app 등; Windows에선 패키지 개념 없음 → 조건 생략).
- 이름 자연 정렬(localizedStandardCompare) 오름차순.
- 실패 시 빈 배열.

### 1.5 `hasSubfolders(url, showHidden = false)` — 트리 행 확장 가능 여부

- 하위에 (비패키지) 폴더가 하나라도 있으면 true. 첫 발견 즉시 반환. 실패 시 false.
- Windows: `Directory.EnumerateDirectories(url).Any(...)` + 숨김 필터.

### 1.6 `folderSize(url)` — 재귀 총 크기

- fts 순회(아래 1.7)로 **일반 파일의 논리 크기 합**. 숨김 포함(`skipHidden: false`). 심볼릭 링크는 따라가지 않음. 에러/빈 폴더면 0.
- 메인 스레드 밖에서 호출.

### 1.7 fts 고속 순회 — Windows 대응

mac 구현 세부(이식 시 동등 동작 보장용):
- `fts_open(FTS_PHYSICAL | FTS_NOCHDIR)` — 물리 순회(심링크 미추적).
- `FTS_D`(디렉터리 진입) 시 `skipHidden`이고 레벨>0이며 숨김이면 `FTS_SKIP`으로 서브트리 전체 스킵. **루트 자체는 숨김이어도 스킵하지 않음**(레벨>0 조건).
- `FTS_F`(일반 파일)만 콜백 — `(이름 C문자열, 전체 경로, st_size)`.
- 숨김 판정: 이름 첫 바이트가 `.`(0x2E) **또는** `UF_HIDDEN` 플래그.
- 확장자 추출: 마지막 점 뒤, 길이 1~12자만 인정(13자 이상의 "확장자"는 "" 처리 → 기타), 첫 글자가 점인 이름(`.gitignore`)은 확장자 없음(`i > 0` 조건), ASCII 대문자만 소문자화.
- **Windows 포팅**: fts는 없음. 동등 성능 대안:
  - `Directory.Enumerate*` + `EnumerationOptions { IgnoreInaccessible = true }` (내부적으로 `FindFirstFileEx(FIND_FIRST_EX_LARGE_FETCH)` 사용, 항목당 크기·속성 무료 제공) — `FileSystemEnumerable<T>`로 직접 구현하면 문자열 할당 최소화 가능.
  - 숨김 판정: `Hidden|System` 속성 또는 이름 `.` 시작.
  - 심링크/정션(`ReparsePoint`)은 디렉터리 재귀에서 제외(무한 루프 방지 — mac FTS_PHYSICAL 동등).
  - 확장자 추출 규칙(1~12자, 소문자화)은 동일하게 구현해야 카테고리 결과가 mac과 일치.

### 1.8 `folderStats(url)` — 재귀 파일/폴더 수 + 총 바이트

- 반환: `(files, folders, bytes)`. 숨김 **포함**(옵션 없음). 일반 파일만 files/bytes에, 디렉터리는 folders에 가산. 에러 항목은 건너뜀.
- Windows: 재귀 열거 1회로 동일 집계.

### 1.9 종류별 카테고리 맵 (확장자 → 카테고리) — **그대로 이식**

```csharp
static readonly string[] FileTypeOrder = { "문서", "이미지", "동영상", "음악", "압축", "기타" };

// 확장자 → 카테고리 (전부 소문자 키)
"문서":   pdf, doc, docx, xls, xlsx, ppt, pptx, txt, hwp, hwpx,
          pages, numbers, key, md, csv, rtf, odt, ods, odp, epub
"이미지": jpg, jpeg, png, gif, heic, heif, webp, tiff, tif, bmp,
          svg, raw, cr2, nef, dng, psd, ai
"동영상": mp4, mov, avi, mkv, m4v, wmv, flv, webm, mpg, mpeg, 3gp
"음악":   mp3, wav, aac, flac, m4a, aiff, aif, ogg, wma, opus
"압축":   zip, rar, 7z, tar, gz, bz2, xz, dmg, pkg, iso
// 그 외 전부 → "기타"
```

- `fileCategory(forExtension:)`: `map[ext.ToLowerInvariant()] ?? "기타"`.
- Windows 노트: `dmg`/`pkg`는 mac 전용 포맷이지만 분류 목적이므로 그대로 유지해도 무방. 원하면 `msi`, `exe`는 추가하지 말 것(원본과 결과 달라짐) — 1차 포팅은 목록 동일 유지.

### 1.10 `sizeByFileType(root)` — 병렬 종류별 크기 계산 (핵심 알고리즘)

- 상수: `TypeIndexLimit = 200_000` (카테고리당 내역 인덱스 최대 파일 수. 항목당 ~100B → 수십 MB 상한 안전판).
- **숨김 트리 스킵** (`~/Library` 등 제외 — "사용자 콘텐츠"만 반영). 루트 직속부터 `.skipsHiddenFiles` 적용.
- 알고리즘:
  1. **작업 단위 수집**: 루트의 1단계 하위 폴더에 대해 다시 그 자식들(2단계)을 작업 단위 목록 `units`(경로 문자열)에 추가. 1·2단계를 훑는 동안 만나는 **직속 일반 파일은 그 자리에서 누적**(acc/details에 바로 추가). 심볼릭 링크는 전부 무시. 1단계 폴더의 자식 나열에 실패하면 그 폴더는 통째로 건너뜀(주의: 원본도 동일 — 권한 없으면 누락).
     - 정확한 재귀 구조: `listLevel(root, splitChildren: true)` → 자식 디렉터리에 대해 `listLevel(child, splitChildren: false)` → 그 안의 디렉터리는 `units`에 추가, 파일은 즉시 누적.
  2. **병렬 스캔**: `units`를 코어 수만큼 병렬로(`DispatchQueue.concurrentPerform` ≒ `Parallel.For`/`Parallel.ForEach`) fts 순회(`skipHidden: true`). 각 워커는 **로컬 딕셔너리에 락 없이 누적**, 단위 완료 시에만 락 잡고 전역 acc/details에 병합.
  3. **prune 규칙**: 어떤 카테고리 리스트가 `2 * TypeIndexLimit`개에 도달하면 크기 내림차순 정렬 후 상위 `TypeIndexLimit`개만 남김. (버려지는 항목은 그 시점에 이미 자기보다 큰 파일이 N개 존재하므로 최종 top-N에 들 수 없음 → 결과 정확성 보장.) 로컬 누적과 전역 병합 직후 양쪽 모두에서 적용.
  4. **결과**: `FileTypeOrder` 순서대로 6개 `TypeBreakdown` 반환(빈 카테고리도 bytes=0, count=0, files=[]로 포함). files는 크기 내림차순 정렬 후 상위 `TypeIndexLimit`개.
- 메인 스레드 밖에서 호출(수 초 소요 가능).
- **Windows 포팅**: `Parallel.ForEach(units, () => 로컬상태, body, 병합)` 패턴이 정확히 대응. 단위 스캔은 1.7의 재귀 열거 사용. 취소 지원이 필요하면 CancellationToken을 열거 루프에 전파(원본은 취소 없음 — UI 영역이 Task 폐기로 처리).

### 1.11 `freeSpace(at url)` — 볼륨 여유 공간

- mac: `volumeAvailableCapacityForImportantUsage` → `ByteCountFormatter`(file 스타일, 십진 단위) 문자열. 실패 시 null.
- Windows: `new DriveInfo(Path.GetPathRoot(url)).AvailableFreeSpace` → 같은 포맷터(다른 스펙 영역의 Format.size와 동일 규칙: 십진 KB/MB/GB)로 문자열화.

---

## 2. FileOperations — 복사/이동/ZIP/휴지통

### 2.1 데이터 구조

```csharp
public abstract record OpResult
{
    public sealed record Success : OpResult;
    public sealed record Failure(string Message) : OpResult;  // 줄바꿈으로 합쳐진 실패 목록
    public sealed record Cancelled : OpResult;
}
```

### 2.2 `uniqueURL(directory, base, ext)` — 이름 충돌 회피 규칙 (**그대로 이식**)

- n=1이면 `base`(+확장자), n≥2이면 `"{base} {n}"`(+확장자). 즉 `보고서.pdf` → `보고서 2.pdf` → `보고서 3.pdf` …
- `ext`가 빈 문자열이면 점 없이 stem만.
- 존재하지 않는 첫 이름을 반환(루프). Windows: `File.Exists || Directory.Exists`로 검사.

### 2.3 `transfer(items, toDirectory, move, progress)` — 복사/이동 (비동기)

동작 순서(항목당):
1. 백그라운드(Task.Run)에서 실행. 시작 시 UI 스레드에서 `progress.TotalUnits = items.Count; CompletedUnits = 0`.
2. 각 항목 처리 전 `progress.IsCancelled` 확인(UI 스레드 값) — true면 루프 중단, 결과는 (실패 없을 때) `Cancelled`.
3. `name = WindowsName.Sanitize(원본 이름)` — **모든 복사/이동에서 이름 정리 적용**. `progress.CurrentFile = 원본 이름`(정리 전 이름 표시).
4. **같은 폴더로의 이동은 no-op**(Finder 동작): `move == true`이고 `name == original`(정리 불필요)이며 src의 부모(정규화 경로) == destDir(정규화 경로)면 카운트만 +1 하고 건너뜀. 단, **이름 정리가 필요한 경우는 같은 폴더라도 진행**(정리 목적 이동).
5. 대상 `destDir/name`이 이미 존재하면 `uniqueURL(destDir, Sanitize(확장자 뗀 원본 이름), 원본 확장자)`로 회피. (확장자는 sanitize하지 않음 — 원본 그대로.)
6. `move`면 이동, 아니면 복사. 예외 발생 시 `"{name}: {메시지}"`를 failures에 누적하고 **다음 항목 계속**.
7. 항목마다 UI 스레드에서 `CompletedUnits += 1`.
8. 반환 우선순위: failures 있으면 `Failure(failures를 \n으로 join)` > 취소됐으면 `Cancelled` > `Success`.

- 진행률 단위는 **파일 개수**(바이트 아님). 폴더 복사는 통째로 1단위.
- **Windows 포팅**:
  - 이동: 같은 볼륨 `File.Move`/`Directory.Move`, 볼륨 간 디렉터리는 복사+삭제 (또는 `SHFileOperation`/`IFileOperation`에 위임하면 자동 처리 + 시스템 진행률 — 단, 원본의 "충돌 시 자동 리네임" 규칙을 유지하려면 직접 구현 권장).
  - 복사: 파일은 `File.Copy(src, dest)`(overwrite: false), 폴더는 재귀 복사 구현(.NET에 내장 재귀 복사 없음). 재귀 복사 시 내부 항목 이름은 정리하지 않음(원본도 루트 항목 이름만 정리).
  - 취소 정밀도: 원본과 동일하게 **항목 경계에서만** 취소 확인(대용량 단일 파일 도중 취소는 안 됨 — 동등 동작). 개선하려면 `CopyFileEx` + `CancellationToken`.
  - UI 스레드 반영: `Dispatcher.InvokeAsync` 또는 `IProgress<T>`.

### 2.4 `zip(items, to dest, progress)` — ZIP 압축

- 전제: items는 **같은 부모 디렉터리** 안에 있어야 함(상위 호출부 책임).
- mac: `/usr/bin/zip -r -X dest.zip 이름1 이름2 …`를 **부모 디렉터리를 cwd로** 실행 → 아카이브 내부 경로가 상대 이름이 됨. `-X`는 확장 속성/리소스 포크 제외.
- 진행률: `TotalUnits = 1`, `CurrentFile = dest 파일명`, 끝나면 `CompletedUnits = 1` — **불확정(indeterminate) 진행률**, 취소 불가(원본도 프로세스 도중 취소 없음).
- 실패 시 `Failure("zip failed ({status}):\n{출력}")`.
- **Windows 포팅**: 외부 프로세스 대신 `System.IO.Compression.ZipArchive` 직접 사용.
  - 파일: `archive.CreateEntryFromFile(path, entryName)`. 폴더: 재귀하며 entryName은 부모 기준 상대 경로(`/` 구분자), 빈 폴더는 `이름/` 엔트리 생성.
  - **엔트리 이름 인코딩**: `new ZipArchive(stream, Create, false, Encoding.UTF8)` + 한글 이름은 NFC로 정규화해 기록(mac zip은 UTF-8 플래그 사용).
  - 직접 구현하므로 **엔트리 단위 진행률·취소 추가 가능**(개선): `TotalUnits = 총 파일 수`로 바꿔도 좋음. 최소 요구는 원본과 같은 불확정 1단위.
  - dest가 이미 존재하는 경우: 상위 호출부가 `uniqueURL`로 충돌 없는 이름을 만들어 넘긴다는 전제 유지.

### 2.5 `unzip(archive, toDirectory, progress)` — 압축 해제

- mac: `/usr/bin/ditto -x -k archive destDir` (destDir에 풀기, 기존 파일은 ditto가 덮어씀).
- 진행률: zip과 동일한 1단위 불확정. 실패 시 `Failure("extract failed ({status}):\n{출력}")`.
- **Windows 포팅**: `ZipFile.ExtractToDirectory(archive, destDir, overwriteFiles: true)`.
  - **인코딩 함정**: UTF-8 플래그 없는 한글 zip(과거 윈도우/알집 제작)은 CP949일 수 있음 — `Encoding.GetEncoding(949)`를 entryNameEncoding 폴백으로 시도(개선 사항, 원본엔 없음).
  - **Zip Slip 방어**: 엔트리 경로가 destDir 밖으로 나가지 않는지 검증(ExtractToDirectory는 .NET 8에서 자체 방어함).
  - 엔트리 이름의 NFD 한글은 풀 때 NFC로 정규화 권장(mac에서 만든 zip 대비) — `WindowsName.Sanitize` 통과시키면 일석이조.

### 2.6 `trashViaFinder(urls)` — 휴지통 이동

- mac: AppleScript로 **Finder에 위임** `tell application "Finder" / delete { POSIX file "...", … }` — 일반 API가 못 지우는 타 앱 번들/TCC 보호 폴더도 Finder 권한으로 삭제. 동기 실행(권한 대화상자 동안 블록). 반환: 성공 nil, 실패 시 AppleScript 오류 번호(특기: **-1743 = 자동화(Apple Events) 권한 미허용** — UI가 권한 안내로 연결). 경로의 `\`와 `"`는 이스케이프.
- doc 명세: **삭제는 항상 휴지통 경유(복구 가능) — 영구 삭제 기능을 추가하지 말 것.**
- **Windows 포팅**: `SHFileOperation(FO_DELETE, FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT)` 또는 `IFileOperation`(권장, per-monitor DPI/긴 경로 안전) — 휴지통으로 이동. 여러 경로는 이중 null 종단 문자열로 한 번에 전달. 실패 시 반환 코드를 메시지로 변환. macOS의 권한 체계(-1743, Full Disk Access)는 Windows에 없음 — 관리자 권한 필요 항목은 OS가 UAC 동의 대화상자를 띄우므로 별도 처리 불필요. **영구 삭제(Shift+Delete) 금지** 원칙 유지.

### 2.7 `runProcess(launchPath, args, cwd)` — 프로세스 헬퍼

- stdout+stderr 합쳐 캡처, 종료 코드와 출력 반환. 실행 실패 시 `(-1, 예외 메시지)`.
- Windows: `ProcessStartInfo { RedirectStandardOutput/Error = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd }`. ZIP을 내장 라이브러리로 바꾸면 이 헬퍼의 용도는 줄지만 범용 유틸로 유지.

### 2.8 클립보드 연동 (AppModel 소유 로직이지만 형식 명세는 여기 기록)

- 내부 상태: `Clipboard { urls: [URL], isCut: Bool }` + 마지막으로 **우리가 쓴** 시스템 클립보드의 changeCount 기억.
- 복사(⌘C) / 잘라내기(⌘X): 내부 Clipboard 설정 + **시스템 클립보드에 파일 URL 목록 기록**(Finder 등 외부 앱에 붙여넣기 가능). 텍스트 필드에 포커스가 있으면 파일 동작 대신 텍스트 복사/잘라내기/붙여넣기로 전달.
- 경로 복사(⌥⌘C): 선택 항목들의 절대 경로를 **텍스트로**, 여러 개면 `\n` 구분.
- 붙여넣기(⌘V) 판정 순서:
  1. 시스템 클립보드 changeCount ≠ 우리가 기록한 값(= 외부 앱이 마지막으로 씀)이고 파일 URL이 있으면 → 그 파일들을 **복사**.
  2. 내부 Clipboard가 있으면 → isCut이면 **이동**, 아니면 **복사**.
  3. 그 외 시스템 클립보드에 파일이 있으면 → 복사.
  4. 아무것도 없으면 무시.
  - 이동(잘라내기 붙여넣기)이 **완전 성공**했을 때만 내부 Clipboard를 비움(부분 실패 시 유지).
- **Windows 포팅 형식**:
  - 파일 목록: `DataObject.SetFileDropList(StringCollection)` (CF_HDROP).
  - 잘라내기/복사 구분: `"Preferred DropEffect"` 스트림에 DWORD 기록 — 잘라내기 = `DragDropEffects.Move(2)`, 복사 = `DragDropEffects.Copy|Link(5)` (탐색기 관례). 읽을 때도 같은 포맷 확인 → **탐색기에서 Ctrl+X 한 파일을 XFinder에 붙여넣으면 이동**되도록 외부 cut도 존중 가능(원본 mac은 외부는 항상 복사 — 최소 포팅은 외부=복사 유지, 탐색기 호환은 개선 옵션).
  - "우리가 썼는지" 판정: Win32 `GetClipboardSequenceNumber()`가 changeCount의 정확한 대응. 기록해 두고 비교.
  - 경로 복사: `Clipboard.SetText(string.Join("\n", paths))`.

### 2.9 드래그&드롭 (doc/file-operations.md 명세)

- **드래그 아웃**: 선택 파일들을 다른 앱/탐색기/바탕화면으로 끌면 **복사**. mac은 SwiftUI `.onDrag` 대신 AppKit 오버레이 NSView로 구현(다중 파일 + 클릭 제스처 충돌 회피; 우클릭/스크롤/호버는 히트 테스트 통과시켜 아래 뷰가 받게 함).
  - Windows: `DragDrop.DoDragDrop(source, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy)` — WPF는 오버레이 트릭 불필요. MouseMove에서 `SystemParameters.MinimumHorizontalDragDistance` 초과 시 시작.
- **폴더에 드롭**: 끌어온 파일을 그 폴더로 **이동**, 보조키를 누르면 **복사**. 대상 폴더 행 하이라이트.
  - 보조키: doc에는 "⌃(Control) 누르면 복사", AppModel 주석에는 "⌥/⌘ held copies" — **코드 기준(⌥/⌘)이 최신**. Windows 관례로는 **Ctrl = 복사, 무보조 = 이동**(탐색기와 동일)으로 통일 권장.
  - 드롭 유효성 필터(AppModel.dropFiles, 그대로 이식):
    - 자기 자신 위에 드롭 금지 (`src == dest`)
    - 폴더를 자기 하위 트리로 드롭 금지 (`destPath.StartsWith(srcPath + "\\")`)
    - 이동인데 이미 그 폴더에 있으면 no-op으로 제외
  - 이후 2.3 `transfer` 호출. 진행률 시트 제목: 이동 = **"이동 중…"**, 복사 = **"복사 중…"**.
- 파괴적/확인 필요한 작업은 ConfirmRequest 대화상자 경유(다른 스펙 영역).

### 2.10 단축키 매핑 (doc 명세 → Windows 제안)

| 기능 | mac | Windows 제안 |
|---|---|---|
| 새 폴더 | ⇧⌘N | Ctrl+Shift+N |
| 이름 변경 | F2 | F2 |
| 복제 | ⌘D | Ctrl+D |
| 휴지통으로 이동 | ⌘⌫ | Delete |
| 복사/잘라내기/붙여넣기 | ⌘C/⌘X/⌘V | Ctrl+C/X/V |
| 경로 복사 | ⌥⌘C | Ctrl+Shift+C |

- 복제(⌘D) 이름 규칙(AppModel.duplicate, 그대로 이식): `uniqueURL(현재 폴더, Sanitize(확장자 뗀 이름) + " copy", 원래 확장자)` → `보고서 copy.pdf`, 또 복제하면 `보고서 copy 2.pdf`. 복제 후 목록 새로고침 + 마지막 복제본으로 커서 이동. 실패들은 `"{이름}: {오류}"` 줄바꿈 join으로 오류 메시지 표시.

---

## 3. SystemActions — 열기/탐색기/터미널/Quick Look/아이콘

### 3.1 동작 명세 + Windows 대응

| 멤버 | mac 동작 | Windows 대응 |
|---|---|---|
| `open(url)` | `NSWorkspace.open` — 기본 앱으로 열기 | `Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })` |
| `reveal(url)` | Finder에서 항목 선택 표시 | `Process.Start("explorer.exe", $"/select,\"{path}\"")` |
| `openFullDiskAccessSettings()` | 시스템 설정 → 개인정보 보호 → 전체 디스크 접근 (`x-apple.systempreferences:...Privacy_AllFiles`) | **포팅 불필요** — Windows에 대응 개념 없음(UAC가 대체). 메서드 제거 |
| `openAutomationSettings()` | 시스템 설정 → 자동화 권한 (`...Privacy_Automation`) | **포팅 불필요** — 제거 |
| `showFinderInfo(urls)` | AppleScript로 Finder '정보 가져오기' 창(N개). 권한 없으면 -1743 반환 | 셸 속성 대화상자: `SHObjectProperties(hwnd, SHOP_FILEPATH, path, null)` P/Invoke (파일별 1창). 실패 개념 단순화(권한 오류 없음) |
| `openTrash()` | Finder로 휴지통 열기(직접 열거 불가) | `Process.Start("explorer.exe", "shell:RecycleBinFolder")` |
| `quickLook(url)` | `qlmanage -p 경로`로 Quick Look 패널. **새로 열기 전에 이전 프로세스 terminate**(패널 1개 유지). 출력은 /dev/null, 좀비 방지 terminationHandler | **자체 미리보기 창** 구현: 싱글턴 Window 재사용(이전 닫기 = mac의 terminate 대응). 이미지(BitmapImage), 텍스트(TextBox 읽기전용), PDF(WebView2), 그 외 = 아이콘+메타데이터. 스페이스 키 토글은 UI 영역 담당 |
| `iTermURL()` / `terminalURL()` | iTerm/Terminal 앱 위치 탐색 | Windows Terminal(`wt.exe`) 존재 검사(`where wt` 또는 `%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe`) / 폴백 `cmd.exe` |
| `openTerminal(at:)` | 설정 키 `XFinder.terminalApp.v1` ∈ {"auto","terminal","iterm"} — "terminal"이면 Terminal, 그 외(auto/iterm)는 iTerm 있으면 iTerm 폴백 Terminal. 해당 디렉터리로 열기 | 설정값 {"auto","terminal","iterm"} → Windows에선 {"auto","cmd","wt"} 제안. auto = wt 있으면 `wt -d "<dir>"`, 없으면 `cmd /K cd /d "<dir>"`. (PowerShell 옵션 추가 가능) |
| `showDayflow()` | DayFlow(메뉴바 캘린더 앱) 실행 + 분산 알림 `com.zjonsu.dayflow.showPopover`(userInfo: 마우스 좌표 x/y 문자열) 게시. 미실행이면 실행 후 0.8초 뒤 게시. 번들 ID `com.zjonsu.dayflow`, 경로 `/Applications/DayFlow.app` | **포팅 무의미**(mac 전용 동반 앱) — 제거. 굳이 대응하면 명명 파이프/`SendMessage`로 자체 위젯과 통신 |
| `launchApp(named:extraPaths:)` | /Applications, ~/Applications에서 `{이름}.app` 탐색, 폴백 LaunchServices | 용도 한정적 — `Process.Start(UseShellExecute=true)`로 충분. 필요 시 시작 메뉴/레지스트리 App Paths 검색 |

### 3.2 아이콘 캐시 (성능 규칙 — 그대로 이식할 것)

행 렌더링 중 호출되므로 아이콘은 **싸야 한다**. 캐시 키 체계:

| 항목 | 캐시 키 | 소스 |
|---|---|---|
| `..` 행 / 일반 폴더 | `"__folder__"` (전체 폴더가 아이콘 1개 공유) | 시스템 폴더 아이콘 |
| 번들 또는 심링크 | `"path:" + 경로` (항목별 고유) | 파일 실제 아이콘(디스크 접근, 느림 — 여기만 허용) |
| 확장자 있는 파일 | `"ext:" + ext` (확장자당 1개 공유) | 종류별 아이콘 |
| 확장자 없는 파일 | `"__file__"` | 일반 문서 아이콘 |

- 근거(원본 주석): 파일별 실제 아이콘은 폴더 첫 페인트에 수백 ms, 종류별 캐시는 ~0.
- 부가 메서드:
  - `monochromeImage(forFile:)` — 키 `"tmpl:" + 경로`, 아이콘을 템플릿(단색 실루엣)으로: 사이드바의 응용 프로그램을 Finder처럼 단색 글리프로 표시. **Windows 대응**: 실아이콘의 단색화는 어색 — Segoe Fluent Icons 글리프로 대체 권장(예: 앱 `` AppIconDefault).
  - `folderImage(for:)` — 키 `"folder:" + 경로`, 폴더의 (사용자 지정 포함) 실제 아이콘: 사이드바 폴더 행용.
- **Windows 구현**: `SHGetFileInfo`/`IShellItemImageFactory`로 HICON → `Imaging.CreateBitmapSourceFromHIcon`, `Dictionary<string, ImageSource>` 캐시(+ `BitmapSource.Freeze()` 필수 — 백그라운드 생성 시). 확장자별 아이콘은 `SHGetFileInfo(SHGFI_USEFILEATTRIBUTES)`로 실제 파일 없이 취득 가능(디스크 무접근 — mac의 종류별 아이콘과 등가).

---

## 4. RecentsService — 최근 항목 (Spotlight)

### 4.1 mac 동작 (`RecentsLoader.load(limit:categories:completion:)`)

- `NSMetadataQuery`(Spotlight) — Finder "최근 항목"과 동일 메커니즘.
- 조건: `kMDItemLastUsedDate >= (지금 - 60일)` (60*60*24*60초). 정렬: 마지막 사용일 **내림차순**. 범위: **사용자 홈 폴더**.
- 수집 완료 알림에서:
  - **재진입 가드**: `finished` 플래그 + 결과 처리 **전에** 쿼리 stop/옵저버 해제(라이브 쿼리가 알림을 재게시해 CPU 폭주하는 버그 회피 — 원본 주석 명시).
  - 결과를 앞에서부터 순회, `limit`(기본 100)개 모이면 중단.
  - **카테고리 필터**: `categories`(예: {"문서","이미지"})가 비어있지 않으면 `FileSystemService.fileCategory(확장자)`가 집합에 포함된 것만. 빈 집합 = 전체.
  - FileItem 구성: `modified = 마지막 사용일`(수정일 아님!), `size`는 폴더면 -1, `isSymlink=false`, `isHidden=false` 고정.
- `completion`은 메인 큐에서 호출. `cancel()`은 옵저버 해제 + 쿼리 stop. 새 load는 항상 기존 것 cancel 후 시작.

### 4.2 Windows 포팅

Spotlight 부재 — 두 가지 대안:
1. **권장(단순)**: `%APPDATA%\Microsoft\Windows\Recent`의 `.lnk` 바로가기 열거 → COM `IShellLink`(또는 셸 네임스페이스 `shell:recent`)로 대상 경로 해석 → 존재하는 파일만, `.lnk` 자체의 수정 시각 = 마지막 사용 시각으로 내림차순 정렬 → 60일 컷 + 카테고리 필터 + limit 100. 비동기(Task.Run) 실행, 완료 콜백은 Dispatcher로.
2. (대안) Windows Search OLE DB (`SELECT System.ItemPathDisplay, System.DateAccessed FROM SYSTEMINDEX ...`) — 인덱서 의존이라 신뢰성 낮음. 1안 권장.
- 재진입 가드는 Windows 구현에선 불필요(폴링 알림 없음) — 단순 CancellationToken으로 대체.

---

## 5. TagService — Finder 태그 (7색) + Spotlight 태그 검색

### 5.1 데이터 구조

```csharp
public readonly record struct FinderTag(string Name, int ColorIndex)
{
    // Id = Name
}

// 표준 7색 (사이드바·컨텍스트 메뉴 표시 순서 그대로, 색번호는 Finder 규칙):
// ("빨간색", 6), ("주황색", 7), ("노란색", 5), ("초록색", 2),
// ("파란색", 4), ("보라색", 3), ("회색", 1)

// 색번호 → 색 (mac 시스템 색상 대응값; WPF 제안 값 병기)
// 1 회색   systemGray    → #8E8E93
// 2 초록   systemGreen   → #34C759
// 3 보라   systemPurple  → #AF52DE
// 4 파랑   systemBlue    → #007AFF
// 5 노랑   systemYellow  → #FFCC00
// 6 빨강   systemRed     → #FF3B30
// 7 주황   systemOrange  → #FF9500
// default → 보조 텍스트 색
```

### 5.2 mac 저장 형식

- 확장 속성 `com.apple.metadata:_kMDItemUserTags` = **문자열 배열의 바이너리 plist**.
- 각 문자열은 `"이름"` 또는 `"이름\n색번호"`(예: `"빨간색\n6"`) — 색번호가 있으면 Finder가 같은 색 점으로 표시.
- 읽기 `rawTags(of:)`: xattr 읽어 plist 디코드, 실패/없음 → 빈 배열.
- 쓰기 `setRawTags(_:on:)`: 빈 배열이면 **xattr 자체를 삭제**, 아니면 바이너리 plist로 기록.
- `displayName(raw)`: `\n` 앞부분만(색번호 제거).
- `hasTag(tag, url)`: rawTags 중 displayName == tag.Name 존재 여부 (색번호 무시 — 이름만 비교).
- `toggle(tag, urls)`: **선택 전부가** 그 태그를 갖고 있으면 모두에서 제거, 아니면(일부만/아무도 없음) **모두에 추가**. 추가 시 기존 동명 태그 먼저 제거 후 `"이름\n색번호"` append (중복/색 불일치 정리 효과).
- `clear(urls)`: 각 파일의 태그 전부 제거(xattr 삭제).
- `colorDot(tag, size = 11)`: 컨텍스트 메뉴용 **컬러 점 이미지** — size×size(기본 11pt) 캔버스에 0.5pt 인셋의 원을 태그 색으로 채움. 비-템플릿이어야 메뉴에서 색 유지(원본 주석).

### 5.3 `TagLoader` — 태그 검색 (Spotlight)

- `NSMetadataQuery`: `kMDItemUserTags == 태그이름`, 파일명 오름차순 정렬, 홈 폴더 범위, limit 500.
- RecentsLoader와 동일한 재진입 가드/정리 절차. FileItem 구성: 수정일 = 실제 수정일, `isSymlink=false`/`isHidden=false` 고정. completion은 메인 큐.

### 5.4 Windows 포팅 (설계 결정 필요)

xattr/Spotlight 모두 부재. **권장: NTFS ADS(대체 데이터 스트림) + 로컬 JSON 인덱스 병행**:
- 저장: `파일경로:XFinder.Tags`(ADS)에 JSON 배열 `[{"name":"빨간색","colorIndex":6}]` 기록. 파일과 함께 이동/복사됨(같은 NTFS 볼륨 내 — 탐색기 복사도 보존). 삭제 = 스트림 삭제(`DeleteFile("path:XFinder.Tags")`).
  - 주의: FAT/exFAT(USB), 클라우드 동기화 폴더, zip 압축 시 ADS 소실. 비-NTFS이면 조용히 실패 → 폴백으로 JSON DB에만 기록.
- 태그 검색(TagLoader 대응): Spotlight 같은 전역 인덱스가 없으므로 **로컬 JSON DB**(`%APPDATA%\XFinder\tags.json`: 경로 → 태그 배열 맵)를 쓰기 시마다 동기화하고, 태그 클릭 시 DB에서 경로 목록을 읽어 존재 확인 후 FileItem 생성(파일명 오름차순, limit 500). ADS는 "파일을 따라다니는 사본", DB는 "검색 인덱스" 역할.
- mac과의 상호운용(같은 파일을 양쪽에서 보는 NAS 등)은 포기 — 명시적 비호환.
- 컬러 점: WPF `Ellipse`(11×11, 0.5 인셋) 또는 DrawingImage — 이미지 생성 불필요.

---

## 6. ThumbnailCache — 비동기 썸네일

### 6.1 mac 동작

- `ThumbnailCache.shared` 싱글턴, `NSCache<키, 이미지>`(스레드 안전·메모리 압박 시 자동 퇴출).
- 키: `"{정수 size}:{경로}"` — 크기별 별도 캐시.
- 생성: QuickLookThumbnailing `QLThumbnailGenerator` — size×size 포인트, 화면 배율(scale) 반영, `.thumbnail` 표현. 실패 시 nil(캐시 안 함).
- `ThumbnailView`(아이콘 보기 셀):
  - 썸네일 도착 전까지(또는 실패 시) **종류별 시스템 아이콘** 표시(3.2 캐시 사용).
  - `frame(width: size, height: size)`, aspect fit.
  - `.task(id: item.url)` — **항목 URL이 바뀌면 이전 로드 자동 취소** 후 재시작.
  - **폴더는 스킵**(시스템 폴더 아이콘으로 충분), 단 **번들은 썸네일 시도**.

### 6.2 Windows 포팅

- `IShellItemImageFactory.GetImage(SIIGBF_THUMBNAILONLY)` — 탐색기와 동일한 썸네일 캐시 활용(이미지/동영상/PDF/Office 등 설치된 썸네일 핸들러 전부). 실패 시(핸들러 없음) E_FAIL → null 반환, 셀은 종류 아이콘 유지.
- HBITMAP → `Imaging.CreateBitmapSourceFromHBitmap` + `Freeze()` + `DeleteObject` 누수 방지. 요청 픽셀 크기 = size × `VisualTreeHelper.GetDpi().DpiScaleX`.
- 캐시: `Dictionary<string, BitmapSource>`(키 동일 형식) + 상한(예: 항목 수 제한 LRU — NSCache의 자동 퇴출 대응).
- 비동기: 백그라운드 스레드에서 생성(COM은 호출 스레드 어디든 가능, STA 불필요한 SIIGBF 경로 사용), 셀 재활용/스크롤 시 `CancellationToken`으로 취소(.task(id:) 대응 — ListView 가상화 항목의 DataContext 변경 시 토큰 취소).

---

## 7. HangulNormalize — NFD 자소 분리 한글 파일명 복원

### 7.1 mac 알고리즘 (함정 포함 — 원본 주석 기준)

- 목적: NFD(분해형, `ㅎㅏㄴ`)로 저장된 한글 이름을 NFC(`한`)로 재조합.
- **함정 1**: Swift `String ==`는 **정규 동등(canonical equivalence)** 비교라 NFD와 NFC가 *같다고* 판정 → 감지는 반드시 **raw 유니코드 스칼라 배열 비교**.
- **함정 2**: macOS `FileManager`/`URL`은 NFC 이름을 `fileSystemRepresentation`에서 **다시 NFD로 분해**해버림 → 복원 rename은 POSIX `rename(2)`를 직접 호출해 NFC UTF-8 바이트를 그대로 통과시킴.
- `isDecomposed(name)`:
  1. `nfc = name.precomposedStringWithCanonicalMapping`
  2. 스칼라 배열이 nfc와 다르고, **그리고**
  3. 이름에 결합 자모가 하나라도 있어야 true. 결합 자모 범위(그대로 이식):
     - `U+1100–U+11FF` (Hangul Jamo, conjoining)
     - `U+A960–U+A97F` (Hangul Jamo Extended-A)
     - `U+D7B0–U+D7FF` (Hangul Jamo Extended-B)
  - (조건 3이 없으면 한글 무관한 NFD 악센트 문자까지 잡혀버림 — 한글만 대상.)
- `recomposed(name)`: NFC 변환 결과.
- `scan(directory, recursive)`:
  - recursive: 전체 트리 열거(**패키지 내부 제외** `.skipsPackageDescendants`) 중 `isDecomposed(이름)`인 항목 수집.
  - 비재귀: 직속 항목만.
- `rename(at:to:)`: POSIX rename(2) 직접 호출. 성공 nil, 실패 시 `strerror(errno)` 문자열.

### 7.2 Windows 포팅

- **시나리오는 여전히 유효**: mac에서 SMB/zip/클라우드로 넘어온 파일이 NFD 이름을 가진 채 NTFS에 저장될 수 있음(NTFS는 이름을 정규화하지 않고 그대로 보존).
- .NET에서의 차이(오히려 단순):
  - C# `string ==`는 **서수(ordinal) 비교**라 mac 함정 1이 없음 — `name != name.Normalize(NormalizationForm.FormC)` 그대로 동작. 결합 자모 범위 검사(3개 범위)는 동일하게 적용.
  - `File.Move`/`MoveFileW`는 이름을 재분해하지 않음 — mac 함정 2 부재, **일반 File.Move로 충분**(rename(2) 트릭 불필요).
  - **Windows 고유 함정**: NFD 이름과 NFC 이름은 NTFS에서 **서로 다른 파일로 공존 가능** → rename 전 대상 존재 확인, 존재하면 `uniqueURL` 규칙으로 회피하거나 오류 보고.
  - 대소문자 무시 충돌(NFC 결과가 기존 파일과 대소문자만 다른 경우)도 `File.Move`가 실패시킴 — 오류 문자열 그대로 표시.
- scan은 `Directory.EnumerateFileSystemEntries`(+재귀 옵션, IgnoreInaccessible)로; 패키지 개념은 없으므로 skipsPackageDescendants 조건은 생략.

---

## 8. WindowsName — Windows 호환 파일명 정리 (포팅 시 핵심 검증 유틸)

mac 쪽에서 "윈도우에서도 깨지지 않는 이름"을 만들기 위한 변환 — **Windows 포팅에서는 사용자 입력 이름 검증/정리로 역할이 이어진다** (생성·이름 변경·복사 시 적용).

### 8.1 규칙 (그대로 이식)

1. **제어 문자 제거**: 스칼라 값 < 0x20 은 삭제.
2. **금지 문자 → `_` 치환**: `<` `>` `:` `"` `/` `\` `|` `?` `*` (9개 — `Path.GetInvalidFileNameChars()`와 일치하는 인쇄 가능 부분).
3. **끝의 공백·마침표 제거**: 뒤에서부터 ` `와 `.`를 모두 strip (Windows 금지).
4. **예약 장치 이름 회피**: 첫 마침표 앞 부분(stem)을 대문자로 비교 — 목록(그대로):
   `CON, PRN, AUX, NUL, COM1…COM9, LPT1…LPT9`
   해당하면 이름 앞에 `_` 부착 (`NUL.txt` → `_NUL.txt`).
5. **빈 결과 → `"_"`**.
6. **NFC 정규화**: 마지막에 항상 `precomposedStringWithCanonicalMapping` (C#: `.Normalize(NormalizationForm.FormC)`) — 한글 자소 분리 방지.
- `needsSanitizing(name)` = `Sanitize(name) != name`.

### 8.2 적용 지점 (원본 기준 — 동일하게 유지)

- `FileOperations.transfer` 항목 이름 (2.3)
- 복제 시 base 이름 (2.10)
- 새 폴더/이름 변경 입력값 (AppModel — 다른 영역이지만 동일 유틸 호출)

### 8.3 Windows 포팅 노트

- Windows에선 OS가 어차피 거부하므로 "방어"가 아니라 **사전 검증 + 자동 정리**(사용자에게 정리된 이름으로 진행) 역할. 규칙·치환 결과는 mac과 동일하게 유지(크로스 플랫폼 동작 일관성).
- 추가 권장: `COM0`/`LPT0`/`CONIN$`/`CONOUT$`는 원본 목록에 없음 — **목록을 늘리지 말고 원본 그대로** (불일치 방지). 실제 OS 거부는 예외 메시지로 표면화됨.

---

## 9. 영속화 (UserDefaults — 이 영역 소유분)

| 키 | 형식 | 의미 |
|---|---|---|
| `XFinder.terminalApp.v1` | 문자열 `"auto"` \| `"terminal"` \| `"iterm"` (기본 `"auto"`) | 터미널 열기에 쓸 앱 선택. Windows: 값 집합을 `"auto"`/`"cmd"`/`"wt"`로 재정의 권장, 키 이름은 유지 가능 |

Windows 저장소: 다른 영역과 합쳐 `%APPDATA%\XFinder\settings.json` (키 이름 그대로 직렬화) 권장.

---

## 10. 사용자에게 보이는 문자열 (원문 그대로 사용)

- 카테고리: `문서` `이미지` `동영상` `음악` `압축` `기타`
- 태그 이름: `빨간색` `주황색` `노란색` `초록색` `파란색` `보라색` `회색`
- 진행률 시트 제목: `이동 중…` `복사 중…` (말줄임표는 U+2026)
- 오류 형식: `"{이름}: {시스템 오류 메시지}"` (여러 건은 `\n` join), `"zip failed ({코드}):\n{출력}"`, `"extract failed ({코드}):\n{출력}"` (zip/extract failed는 내부 포맷 — 한국어화 여부는 UI 영역 결정)
- 복제 접미사: `" copy"` / 충돌 접미사: `" {n}"` (n ≥ 2)
- `..` (부모 폴더 행 이름)

---

## 11. C# 서비스 시그니처 제안 (요약)

```csharp
static class FileSystemService
{
    static Result<List<FileItem>> List(string directory);
    static List<FileItem> SearchRecursive(string root, string needleLower, bool showHidden, int limit = 1000, CancellationToken ct = default);
    static List<string> Subfolders(string dir, bool showHidden);
    static bool HasSubfolders(string dir, bool showHidden = false);
    static long FolderSize(string dir);                                  // 숨김 포함
    static (int Files, int Folders, long Bytes) FolderStats(string dir); // 숨김 포함
    static string FileCategory(string ext);
    static readonly string[] FileTypeOrder;
    const int TypeIndexLimit = 200_000;
    static List<TypeBreakdown> SizeByFileType(string root);              // 숨김 제외, 병렬
    static string? FreeSpace(string anyPathOnVolume);
}

static class FileOperations
{
    static string UniqueUrl(string directory, string baseName, string ext);
    static Task<OpResult> Transfer(IReadOnlyList<string> items, string destDir, bool move, OperationProgress progress);
    static Task<OpResult> Zip(IReadOnlyList<string> items, string destZip, OperationProgress progress);
    static Task<OpResult> Unzip(string archive, string destDir, OperationProgress progress);
    static OpResult MoveToRecycleBin(IReadOnlyList<string> paths);       // SHFileOperation FOF_ALLOWUNDO
}

static class SystemActions
{
    static void Open(string path);                  // ShellExecute
    static void RevealInExplorer(string path);      // explorer /select,
    static void ShowProperties(string path);        // SHObjectProperties
    static void OpenRecycleBin();                   // shell:RecycleBinFolder
    static void OpenTerminal(string directory);     // 설정 키 참조
    static ImageSource Icon(FileItem item);         // 3.2 캐시 규칙
    static ImageSource FolderIcon(string path);
}

sealed class RecentsLoader  { Task<List<FileItem>> LoadAsync(int limit = 100, ISet<string>? categories = null, CancellationToken ct = default); }
static class TagService     { /* 5.x 그대로: RawTags/Toggle/Clear/HasTag/Standard */ }
sealed class TagLoader      { Task<List<FileItem>> LoadAsync(string tagName, int limit = 500, CancellationToken ct = default); }
sealed class ThumbnailCache { Task<BitmapSource?> ThumbnailAsync(string path, double size, CancellationToken ct = default); }
static class HangulNormalize{ static bool IsDecomposed(string name); static string Recomposed(string name);
                              static List<string> Scan(string dir, bool recursive); static string? Rename(string src, string dst); }
static class WindowsName    { static string Sanitize(string name); static bool NeedsSanitizing(string name); }
```
