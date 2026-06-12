// mac 소스 대응: Sources/XFinder/Services/AIService.swift 전체 + AppModel.swift의 AI 관련 부분(설정 키, 예외/보호 폴더, currentFolderEntries, applyAIPlan)
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XFinder.Services;

// ═════════════════════════════════════════════════════════════════════════
// 데이터 구조 (스펙 07 §1)
// ═════════════════════════════════════════════════════════════════════════

/// <summary>LLM이 제안한 단일 작업. action은 LLM 출력 JSON 호환을 위해 enum이 아닌 문자열 그대로 유지.</summary>
public sealed record AIOperation
{
    [JsonPropertyName("action")] public string Action { get; init; } = "";      // "move" | "delete"
    [JsonPropertyName("file")] public string File { get; init; } = "";          // 현재 폴더 안 항목 이름 (경로 아님)
    [JsonPropertyName("destination")] public string? Destination { get; init; } // move 전용 하위 폴더 이름

    /// <summary>SwiftUI Identifiable 대응 (목록 키).</summary>
    [JsonIgnore] public string Id => $"{Action}:{File}→{Destination ?? ""}";
    [JsonIgnore] public bool IsDelete => Action == "delete";
}

/// <summary>LLM의 전체 계획.</summary>
public sealed record AIPlan
{
    [JsonPropertyName("operations")] public List<AIOperation> Operations { get; init; } = new();
    [JsonPropertyName("summary")] public string Summary { get; init; } = "";    // 한 줄 한국어 요약
}

/// <summary>LLM 백엔드 선택.</summary>
public enum AIProvider { Ollama, Gemini }

public static class AIProviderExtensions
{
    /// <summary>설정 저장값 (UserDefaults rawValue 대응 — 소문자 문자열).</summary>
    public static string RawValue(this AIProvider p) => p == AIProvider.Ollama ? "ollama" : "gemini";

    /// <summary>저장값 → 제공자. 파싱 실패 포함 기본값은 Gemini.</summary>
    public static AIProvider FromRawValue(string? raw) =>
        raw == "ollama" ? AIProvider.Ollama : AIProvider.Gemini;

    /// <summary>표시 라벨.</summary>
    public static string Label(this AIProvider p) => p == AIProvider.Ollama ? "로컬 (Ollama)" : "Gemini";
}

/// <summary>분석 호출에 전달되는 설정 스냅숏.</summary>
public sealed record AIConfig
{
    public AIProvider Provider { get; init; }
    public string GeminiApiKey { get; init; } = "";
    public string GeminiModel { get; init; } = "";          // 비면 AIService 쪽 기본 "gemini-2.5-flash"
    public string OllamaBaseUrl { get; init; } = AIService.DefaultOllamaBaseUrl;
    public string OllamaModel { get; init; } = AIService.DefaultOllamaModel;
    public bool FallbackToOllama { get; init; } = true;     // Gemini 실패 시 Ollama 재시도

    /// <summary>Trim 후 URL 파싱 실패 시 기본 주소("http://localhost:11434")로 폴백.</summary>
    public string ResolvedOllamaBase
    {
        get
        {
            var t = OllamaBaseUrl.Trim().TrimEnd('/');
            return Uri.TryCreate(t, UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? t
                : AIService.DefaultOllamaBaseUrl;
        }
    }

    /// <summary>Trim 후 빈 문자열이면 "gemma4:latest".</summary>
    public string ResolvedOllamaModel =>
        string.IsNullOrWhiteSpace(OllamaModel) ? AIService.DefaultOllamaModel : OllamaModel.Trim();
}

/// <summary>사용자에게 보이는 AI 오류 종류.</summary>
public enum AIErrorKind { NotRunning, NoModel, GeminiNoKey, BadResponse }

/// <summary>AI 오류 — 메시지는 스펙 원문 그대로 (한국어, \n 포함).</summary>
public sealed class AIError : Exception
{
    public AIErrorKind Kind { get; }
    public string? Detail { get; }

    public AIError(AIErrorKind kind, string? detail = null) : base(MessageFor(kind, detail))
    {
        Kind = kind;
        Detail = detail;
    }

    private static string MessageFor(AIErrorKind kind, string? detail) => kind switch
    {
        AIErrorKind.NotRunning =>
            "로컬 LLM(Ollama)에 연결할 수 없습니다.\n터미널에서 `ollama serve` 가 실행 중인지 확인하세요.",
        AIErrorKind.NoModel =>
            "사용할 수 있는 채팅 모델이 없습니다.\n`ollama pull gemma4` 등으로 모델을 받아 주세요.",
        AIErrorKind.GeminiNoKey =>
            "Gemini API 키가 없습니다.\n설정 → AI 모델에서 키를 입력하세요. (aistudio.google.com 에서 발급)",
        AIErrorKind.BadResponse =>
            $"AI 응답을 이해하지 못했습니다.\n{detail}",
        _ => "오류가 발생했습니다.",
    };
}

// ═════════════════════════════════════════════════════════════════════════
// 설정 (스펙 07 §8 — UserDefaults 키 → SettingsStore 키 1:1 매핑)
// ═════════════════════════════════════════════════════════════════════════

/// <summary>AI 관련 설정 — SettingsStore(settings.json) 즉시 저장.
/// 보안 노트: 원본(mac)과 동일하게 API 키는 평문 저장 (DPAPI는 추가 NuGet 필요로 보류, 스펙 §8 주의사항 참고).</summary>
public static class AISettings
{
    public const string ProviderKey = "XFinder.aiProvider.v1";
    public const string GeminiApiKeyKey = "XFinder.geminiAPIKey.v1";
    public const string GeminiModelKey = "XFinder.geminiModel.v1";
    public const string OllamaBaseUrlKey = "XFinder.ollamaBaseURL.v1";
    public const string OllamaModelKey = "XFinder.ollamaModel.v1";
    public const string ExcludedFoldersKey = "XFinder.aiExcludedFolders.v1";

    /// <summary>LLM 제공자. 미설정/파싱 실패 시 Gemini.</summary>
    public static AIProvider Provider
    {
        get => AIProviderExtensions.FromRawValue(SettingsStore.Get<string>(ProviderKey));
        set => SettingsStore.Set(ProviderKey, value.RawValue());
    }

    /// <summary>Gemini API 키 (기본 "").</summary>
    public static string GeminiApiKey
    {
        get => SettingsStore.Get<string>(GeminiApiKeyKey, "") ?? "";
        set => SettingsStore.Set(GeminiApiKeyKey, value);
    }

    /// <summary>Gemini 모델 이름. 빈 값이면 "gemini-flash-latest".</summary>
    public static string GeminiModel
    {
        get
        {
            var v = SettingsStore.Get<string>(GeminiModelKey, "") ?? "";
            return string.IsNullOrWhiteSpace(v) ? "gemini-flash-latest" : v;
        }
        set => SettingsStore.Set(GeminiModelKey, value);
    }

    /// <summary>Ollama 서버 주소. 빈 값이면 "http://localhost:11434".</summary>
    public static string OllamaBaseUrl
    {
        get
        {
            var v = SettingsStore.Get<string>(OllamaBaseUrlKey, "") ?? "";
            return string.IsNullOrWhiteSpace(v) ? AIService.DefaultOllamaBaseUrl : v;
        }
        set => SettingsStore.Set(OllamaBaseUrlKey, value);
    }

    /// <summary>우선 로컬 모델. 빈 값이면 "gemma4:latest".</summary>
    public static string OllamaModel
    {
        get
        {
            var v = SettingsStore.Get<string>(OllamaModelKey, "") ?? "";
            return string.IsNullOrWhiteSpace(v) ? AIService.DefaultOllamaModel : v;
        }
        set => SettingsStore.Set(OllamaModelKey, value);
    }

    /// <summary>현재 설정 스냅숏 — AppModel.aiConfig 대응 (fallbackToOllama는 항상 true).</summary>
    public static AIConfig CurrentConfig() => new()
    {
        Provider = Provider,
        GeminiApiKey = GeminiApiKey,
        GeminiModel = GeminiModel,
        OllamaBaseUrl = OllamaBaseUrl,
        OllamaModel = OllamaModel,
        FallbackToOllama = true,
    };
}

// ═════════════════════════════════════════════════════════════════════════
// 진입 차단 / 제외 폴더 규칙 (스펙 07 §6 — Windows 경로 번역)
// ═════════════════════════════════════════════════════════════════════════

/// <summary>AI 정리 차단 판정 — 보호 폴더(시스템 기본 예외) + 사용자 지정 예외 폴더.</summary>
public static class AIOrganizeGuard
{
    public const string MsgBlockedApplications = "응용 프로그램 폴더는 AI 파일 정리에서 제외됩니다.";
    public const string MsgBlockedSystem = "시스템 폴더는 AI 파일 정리에서 제외됩니다.";
    public const string MsgBlockedExcluded = "이 폴더는 AI 정리 예외 폴더로 지정되어 정리할 수 없습니다.";

    // 하위 폴더까지 재귀 차단 (mac: /Applications, /System, /Library, ~/Library)
    private static readonly string[] RecursiveProtected = BuildPaths(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),                       // C:\Windows
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),                  // C:\Program Files
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),               // C:\Program Files (x86)
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),         // C:\ProgramData
        Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData") // %USERPROFILE%\AppData
    );

    // 응용 프로그램 위치 (mac: /Applications, /System/Applications, ~/Applications) — 오류 문구 구분용
    private static readonly string[] ApplicationLocations = BuildPaths(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") // %LOCALAPPDATA%\Programs
    );

    private static string Combine(string basePath, string sub) =>
        string.IsNullOrEmpty(basePath) ? "" : Path.Combine(basePath, sub);

    private static string[] BuildPaths(params string[] paths) =>
        paths.Where(p => !string.IsNullOrEmpty(p))
             .Select(Normalize)
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .ToArray();

    /// <summary>경로 정규화 — 전체 경로화 + 끝 \ 제거 (드라이브 루트 "C:\"는 "C:"가 됨).</summary>
    private static string Normalize(string path)
    {
        try { path = Path.GetFullPath(path); } catch { /* 잘못된 경로는 원문 유지 */ }
        return path.TrimEnd('\\');
    }

    /// <summary>경계(\) 기준 포함 비교 — "C:\A\B"가 "C:\A\BC"를 잘못 포함하지 않게.</summary>
    private static bool IsSelfOrUnder(string normPath, string normBase)
    {
        if (normBase.Length == 0) return false;
        return normPath.Equals(normBase, StringComparison.OrdinalIgnoreCase)
            || normPath.StartsWith(normBase + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDriveRoot(string normPath) =>
        normPath.Length == 2 && normPath[1] == ':';

    private static bool IsUsersRoot(string normPath) =>
        normPath.Length >= 3 && normPath[1] == ':'
        && string.Equals(normPath[2..], @"\Users", StringComparison.OrdinalIgnoreCase);

    /// <summary>보호 폴더 (시스템 기본 예외). 재귀 차단 + 드라이브 루트/Users 자체만 차단.</summary>
    public static bool IsProtectedLocation(string path)
    {
        var p = Normalize(path);
        if (IsDriveRoot(p) || IsUsersRoot(p)) return true;                 // 그 폴더 자체만 차단
        return RecursiveProtected.Any(b => IsSelfOrUnder(p, b));           // 하위까지 재귀 차단
    }

    /// <summary>응용 프로그램 위치 (오류 문구 구분용).</summary>
    public static bool IsApplicationsLocation(string path)
    {
        var p = Normalize(path);
        return ApplicationLocations.Any(b => IsSelfOrUnder(p, b));
    }

    /// <summary>등록된 예외 폴더 자신 또는 그 하위 폴더이면 true.</summary>
    public static bool IsExcluded(string path)
    {
        var p = Normalize(path);
        return ExcludedFolders.Any(b => IsSelfOrUnder(p, Normalize(b)));
    }

    /// <summary>정확히 그 경로가 등록돼 있는지 (컨텍스트 메뉴 토글 표시용).</summary>
    public static bool IsDirectlyExcluded(string path)
    {
        var p = Normalize(path);
        return ExcludedFolders.Any(b => Normalize(b).Equals(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>툴바 AI 버튼 비활성화와 실행 차단의 단일 기준.</summary>
    public static bool AiOrganizeBlocked(string path) =>
        IsExcluded(path) || IsProtectedLocation(path);

    /// <summary>차단 사유 메시지 (우선순위: 응용 프로그램 → 시스템 → 사용자 예외). 허용이면 null.</summary>
    public static string? BlockMessage(string path)
    {
        if (IsProtectedLocation(path))
            return IsApplicationsLocation(path) ? MsgBlockedApplications : MsgBlockedSystem;
        if (IsExcluded(path))
            return MsgBlockedExcluded;
        return null;
    }

    /// <summary>사용자 지정 예외 폴더 목록 (절대 경로 문자열 배열, settings.json).</summary>
    public static List<string> ExcludedFolders =>
        SettingsStore.Get<List<string>>(ExcludedFoldersKeyName) ?? new List<string>();

    private const string ExcludedFoldersKeyName = AISettings.ExcludedFoldersKey;

    /// <summary>예외 폴더 등록 — 디렉터리만 허용, 중복 등록 무시.
    /// 성공 시 토스트 문자열 반환, 디렉터리가 아니면 null.</summary>
    public static string? AddExcludedFolder(string path)
    {
        if (!Directory.Exists(path)) return null;
        var norm = Normalize(path);
        var list = ExcludedFolders;
        if (!list.Any(b => Normalize(b).Equals(norm, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(norm);
            SettingsStore.Set(ExcludedFoldersKeyName, list);
        }
        return $"“{DisplayName(norm)}” 및 하위 폴더를 AI 정리 예외로 등록했습니다.";
    }

    /// <summary>예외 폴더 해제 — 해제 토스트 문자열 반환.</summary>
    public static string RemoveExcludedFolder(string path)
    {
        var norm = Normalize(path);
        var list = ExcludedFolders;
        var removed = list.RemoveAll(b => Normalize(b).Equals(norm, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
            SettingsStore.Set(ExcludedFoldersKeyName, list);
        return $"“{DisplayName(norm)}”의 AI 정리 예외를 해제했습니다.";
    }

    private static string DisplayName(string normalizedPath)
    {
        var name = Path.GetFileName(normalizedPath);
        return string.IsNullOrEmpty(name) ? normalizedPath : name;
    }
}

// ═════════════════════════════════════════════════════════════════════════
// 계획 적용 결과 (스펙 07 §5)
// ═════════════════════════════════════════════════════════════════════════

/// <summary>계획 적용의 항목별 진행 보고 (작업 스레드에서 호출됨 — UI 반영은 Dispatcher로).</summary>
public readonly record struct AIApplyProgress(int Completed, int Total, AIOperation Operation, bool Success, string? Error);

/// <summary>계획 적용 결과 — 성공/실패 집계와 토스트 문자열.</summary>
public sealed record AIApplyResult
{
    public int Moved { get; init; }
    public int Trashed { get; init; }
    public List<string> Failures { get; init; } = new();

    /// <summary>차단 시 비-null (작업 미수행). 우선순위: 응용 프로그램 → 시스템 → 사용자 예외.</summary>
    public string? BlockedMessage { get; init; }

    public bool HasFailures => Failures.Count > 0;
    public bool IsError => BlockedMessage is not null || HasFailures;

    /// <summary>완료 문구: "AI가 {N}개 정리, {M}개 휴지통으로 이동했습니다." / "처리한 항목이 없습니다".</summary>
    public string CompletionMessage
    {
        get
        {
            var parts = new List<string>();
            if (Moved > 0) parts.Add($"{Moved}개 정리");
            if (Trashed > 0) parts.Add($"{Trashed}개 휴지통으로 이동");
            return parts.Count == 0 ? "처리한 항목이 없습니다" : $"AI가 {string.Join(", ", parts)}했습니다.";
        }
    }

    /// <summary>최종 토스트 문자열 — 실패 없으면 완료 문구(정보), 실패 있으면 실패 목록 포함(오류).</summary>
    public string ToastMessage
    {
        get
        {
            if (BlockedMessage is not null) return BlockedMessage;
            if (Failures.Count == 0) return CompletionMessage;
            var shown = string.Join("\n", Failures.Take(5));
            var more = Failures.Count > 5 ? $"\n…외 {Failures.Count - 5}개" : "";
            return $"{CompletionMessage}\n{Failures.Count}개 실패:\n{shown}{more}";
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════
// AIService 본체 (스펙 07 §2~§5)
// ═════════════════════════════════════════════════════════════════════════

/// <summary>AI 파일 정리 백엔드 — Ollama(로컬)/Gemini 호출, 응답 파싱·검증, 계획 적용.</summary>
public static class AIService
{
    public const string DefaultOllamaBaseUrl = "http://localhost:11434";
    public const string DefaultOllamaModel = "gemma4:latest";
    /// <summary>하위 호환 별칭 (mac preferredOllamaModel).</summary>
    public const string PreferredOllamaModel = DefaultOllamaModel;

    private const string DefaultGeminiModel = "gemini-2.5-flash";
    private static readonly TimeSpan OllamaTagsTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OllamaChatTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan GeminiTimeout = TimeSpan.FromSeconds(120);

    // 타임아웃은 요청별 CancellationTokenSource로 재현 (URLSession 요청 타임아웃 대응).
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    // 직렬화: 비ASCII(한국어 파일명) 이스케이프 없이 — mac JSONSerialization 동작에 맞춤.
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // 파싱: 키 대소문자 무시 + LLM 잡음(끝 콤마/주석) 허용.
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // ── 프롬프트 (스펙 §3 원문 — Windows 포팅 노트에 따라 첫 줄만 "Windows file manager") ──

    public const string SystemPrompt =
        """
        You are a file-organization assistant inside a Windows file manager.
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
        """;

    /// <summary>user 프롬프트 템플릿 (스펙 §3.2 원문). 파일 목록은 JSON 배열 문자열로 직렬화(실패 시 "[]").</summary>
    public static string BuildUserPrompt(string folderName, IReadOnlyList<string> files, string instruction)
    {
        string fileList;
        try { fileList = JsonSerializer.Serialize(files, SerializeOptions); }
        catch { fileList = "[]"; }
        return $"현재 폴더: {folderName}\n파일 목록: {fileList}\n명령: {instruction.Trim()}";
    }

    // ── 폴더 내용 직렬화 (스펙 §2 — AppModel.currentFolderEntries 대응) ──

    /// <summary>현재 폴더 최상위 항목 이름 목록 (숨김/시스템/점으로 시작 제외, 이름 오름차순, 최대 limit개).
    /// 나열 실패(권한 등) 시 빈 배열.</summary>
    public static List<string> FolderEntries(string folder, int limit = 300)
    {
        try
        {
            var names = new List<string>();
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                if (entry.Name.StartsWith('.')) continue;
                names.Add(entry.Name);
            }
            names.Sort(StringComparer.Ordinal);   // Swift 기본 `<` 대응
            if (names.Count > limit) names.RemoveRange(limit, names.Count - limit);
            return names;
        }
        catch
        {
            return new List<string>();
        }
    }

    // ── 진입점 (스펙 §4.1) ──

    /// <summary>폴더 이름·파일 목록·자연어 명령으로 AIPlan을 받는다.
    /// 폴백 규칙: Gemini 1차 실패 + FallbackToOllama이면 같은 프롬프트로 Ollama 한 번 더 (단방향).</summary>
    public static async Task<AIPlan> OrganizeAsync(
        string folderName,
        IReadOnlyList<string> files,
        string instruction,
        AIConfig config,
        CancellationToken ct = default)
    {
        var userPrompt = BuildUserPrompt(folderName, files, instruction);
        try
        {
            var content = config.Provider == AIProvider.Ollama
                ? await CallOllamaAsync(SystemPrompt, userPrompt, config, ct).ConfigureAwait(false)
                : await CallGeminiAsync(SystemPrompt, userPrompt, config, ct).ConfigureAwait(false);
            return Parse(content, files);
        }
        catch (Exception) when (!ct.IsCancellationRequested
                                && config.Provider == AIProvider.Gemini
                                && config.FallbackToOllama)
        {
            // Gemini 실패 → Ollama 폴백. 폴백도 실패하면 폴백의 예외를 그대로 던진다.
            var content = await CallOllamaAsync(SystemPrompt, userPrompt, config, ct).ConfigureAwait(false);
            return Parse(content, files);
        }
    }

    /// <summary>현재 설정(AISettings)으로 분석하는 편의 오버로드.</summary>
    public static Task<AIPlan> OrganizeAsync(
        string folderName,
        IReadOnlyList<string> files,
        string instruction,
        CancellationToken ct = default)
        => OrganizeAsync(folderName, files, instruction, AISettings.CurrentConfig(), ct);

    // ── Ollama 백엔드 (스펙 §4.2) ──

    /// <summary>설치된 모델에서 채팅 모델 자동 감지. 연결 실패 → NotRunning, 후보 없음 → NoModel.</summary>
    public static async Task<string> OllamaChatModelAsync(string baseUrl, string preferred, CancellationToken ct = default)
    {
        byte[] body;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(OllamaTagsTimeout);
            body = await Http.GetByteArrayAsync($"{baseUrl}/api/tags", cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new AIError(AIErrorKind.NotRunning); }

        var names = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                foreach (var m in models.EnumerateArray())
                    if (m.TryGetProperty("name", out var n) && n.GetString() is { } s)
                        names.Add(s);
        }
        catch { /* 파싱 실패 시 빈 목록 */ }

        // preferred(소문자화)와 정확히 일치하거나 ':' 앞 베이스 이름이 같은 모델 우선.
        var want = preferred.ToLowerInvariant();
        var wantBase = want.Split(':')[0];
        foreach (var name in names)
        {
            var lower = name.ToLowerInvariant();
            if (lower == want || lower.Split(':')[0] == wantBase) return name;
        }
        // 없으면 임베딩 모델이 아닌 첫 모델.
        foreach (var name in names)
        {
            var lower = name.ToLowerInvariant();
            if (lower.Contains("embed") || lower.StartsWith("bge") || lower.StartsWith("nomic")) continue;
            return name;
        }
        throw new AIError(AIErrorKind.NoModel);
    }

    private static async Task<string> CallOllamaAsync(string systemPrompt, string userPrompt, AIConfig config, CancellationToken ct)
    {
        var baseUrl = config.ResolvedOllamaBase;
        var model = await OllamaChatModelAsync(baseUrl, config.ResolvedOllamaModel, ct).ConfigureAwait(false);

        var payload = new
        {
            model,
            stream = false,
            format = "json",
            options = new { temperature = 0.1 },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
        };

        byte[] body;
        try
        {
            (_, body) = await PostJsonAsync($"{baseUrl}/api/chat", payload, OllamaChatTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new AIError(AIErrorKind.NotRunning); }   // 네트워크 예외 → notRunning

        // {"message":{"content":"..."}} 에서 content 추출. 실패 시 응답 앞 300바이트로 badResponse.
        string? content = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var c))
                content = c.GetString();
        }
        catch { /* 아래에서 badResponse */ }
        return content ?? throw new AIError(AIErrorKind.BadResponse, Utf8Prefix(body, 300));
    }

    // ── Gemini 백엔드 (스펙 §4.3) ──

    private static async Task<string> CallGeminiAsync(string systemPrompt, string userPrompt, AIConfig config, CancellationToken ct)
    {
        var key = config.GeminiApiKey.Trim();
        if (key.Length == 0) throw new AIError(AIErrorKind.GeminiNoKey);

        var model = config.GeminiModel.Trim();
        if (model.Length == 0) model = DefaultGeminiModel;

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={Uri.EscapeDataString(key)}";
        var payload = new
        {
            systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = userPrompt } } } },
            generationConfig = new { responseMimeType = "application/json", temperature = 0.1 },
        };

        int status;
        byte[] body;
        try
        {
            (status, body) = await PostJsonAsync(url, payload, GeminiTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            throw new AIError(AIErrorKind.BadResponse, $"네트워크 오류: {ex.Message}");
        }

        if (status != 200)
        {
            string? message = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err)
                    && err.TryGetProperty("message", out var msg))
                    message = msg.GetString();
            }
            catch { /* 본문 앞부분으로 대체 */ }
            throw new AIError(AIErrorKind.BadResponse, $"Gemini {status}: {message ?? Utf8Prefix(body, 200)}");
        }

        // candidates[0].content.parts[0].text 추출. 실패 시 앞 300바이트로 badResponse.
        string? text = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            text = doc.RootElement.GetProperty("candidates")[0]
                .GetProperty("content").GetProperty("parts")[0]
                .GetProperty("text").GetString();
        }
        catch { /* 아래에서 badResponse */ }
        return text ?? throw new AIError(AIErrorKind.BadResponse, Utf8Prefix(body, 300));
    }

    // ── HTTP 공통 ──

    private static async Task<(int Status, byte[] Body)> PostJsonAsync(string url, object payload, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SerializeOptions), Encoding.UTF8, "application/json"),
        };
        using var res = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
        var body = await res.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        return ((int)res.StatusCode, body);
    }

    private static string Utf8Prefix(byte[] bytes, int maxBytes) =>
        Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, maxBytes));

    // ── 파싱 + 검증 (스펙 §4.4) ──

    /// <summary>LLM 응답을 AIPlan으로 파싱하고 검증. 통과 못 한 작업은 조용히 제거.
    /// 디코드 자체가 실패하면 badResponse(content 전체).</summary>
    public static AIPlan Parse(string content, IReadOnlyList<string> files)
    {
        AIPlan? plan = null;
        try { plan = JsonSerializer.Deserialize<AIPlan>(CleanJson(content), ParseOptions); }
        catch { /* 아래에서 badResponse */ }
        if (plan is null) throw new AIError(AIErrorKind.BadResponse, content);

        // 환각 이름 차단: 원본 목록에 정확히(대소문자 포함) 존재해야 함.
        var allowed = new HashSet<string>(files, StringComparer.Ordinal);
        var source = plan.Operations ?? new List<AIOperation>();
        var ops = source.Where(op =>
            allowed.Contains(op.File)
            && (op.Action == "delete"                                          // delete는 무조건 통과 (휴지통 이동 — 복구 가능)
                || (op.Action == "move" && IsSafeDestination(op.Destination))) // move는 목적지 안전성 통과
            ).ToList();                                                        // 그 외 action 값은 제거
        return plan with { Operations = ops };
    }

    /// <summary>코드펜스/잡음 제거 — ```json 펜스 벗기기 + 첫 '{'부터 마지막 '}'까지만 사용.</summary>
    private static string CleanJson(string content)
    {
        var s = content.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = s.IndexOf('\n');
            if (firstNewline >= 0) s = s[(firstNewline + 1)..];
            var fenceEnd = s.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0) s = s[..fenceEnd];
            s = s.Trim();
        }
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        if (start >= 0 && end > start) s = s[start..(end + 1)];
        return s;
    }

    // ── 목적지 안전성 (스펙 §4.5 + Windows 추가 검증) ──

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>단일 하위 폴더 이름 1단계만 허용 — 경로 분리자/예약 이름/금지 문자 거부.</summary>
    public static bool IsSafeDestination(string? name)
    {
        if (name is null) return false;
        var t = name.Trim();
        if (t.Length == 0) return false;
        if (t.Contains('/')) return false;          // mac 규칙
        if (t == "..") return false;
        if (t.StartsWith('.')) return false;
        if (t.StartsWith('~')) return false;
        // Windows 추가 검증
        if (t.Contains('\\')) return false;
        if (t.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (t.EndsWith('.') || t.EndsWith(' ')) return false;
        var stem = t.Split('.')[0].TrimEnd();
        if (ReservedNames.Contains(stem)) return false;
        return true;
    }

    // ── 이름 충돌 회피 (FileOperations.uniqueURL 규칙 — "이름 2.ext", "이름 3.ext", …) ──

    /// <summary>대상에 같은 이름이 있으면 "이름 2.ext", "이름 3.ext"… 로 회피한 경로 반환.</summary>
    public static string UniquePath(string desiredPath)
    {
        if (!File.Exists(desiredPath) && !Directory.Exists(desiredPath)) return desiredPath;
        var dir = Path.GetDirectoryName(desiredPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);   // ".txt" 또는 "" (확장자가 없으면 점 없이)
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} {n}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    // ── 계획 적용 (스펙 §5 — AppModel.applyAIPlan 대응) ──

    /// <summary>계획 실행 (백그라운드 Task). progress는 항목별 보고 — 작업 스레드에서 호출되므로 UI 반영은 Dispatcher로.
    /// 실행 전 차단 재검사를 하며, 차단 시 BlockedMessage가 채워진 결과를 반환한다.</summary>
    public static Task<AIApplyResult> ApplyPlanAsync(
        string basePath,
        IReadOnlyList<AIOperation> operations,
        Action<AIApplyProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => ApplyPlan(basePath, operations, progress, ct), CancellationToken.None);

    /// <summary>계획 실행 (동기). delete는 휴지통 이동(영구 삭제 아님), move는 하위 폴더 생성 + 이름 충돌 회피.</summary>
    public static AIApplyResult ApplyPlan(
        string basePath,
        IReadOnlyList<AIOperation> operations,
        Action<AIApplyProgress>? progress = null,
        CancellationToken ct = default)
    {
        // 재검사: 실행 직전에도 차단 여부 확인 (계획 자동 실행 경로 금지 원칙의 마지막 방어선).
        var blocked = AIOrganizeGuard.BlockMessage(basePath);
        if (blocked is not null)
            return new AIApplyResult { BlockedMessage = blocked };

        int moved = 0, trashed = 0, done = 0;
        var failures = new List<string>();

        foreach (var op in operations)
        {
            ct.ThrowIfCancellationRequested();
            var ok = true;
            string? error = null;

            // 방어: 파일 이름에 경로 조작이 섞여 있으면 항목 없음으로 처리 (파싱 검증을 우회한 호출 대비).
            if (op.File.Length == 0 || op.File.Contains('\\') || op.File.Contains('/')
                || op.File == "." || op.File == "..")
            {
                error = $"{op.File}: 항목 없음";
            }
            else
            {
                var src = Path.GetFullPath(Path.Combine(basePath, op.File));
                if (!File.Exists(src) && !Directory.Exists(src))
                {
                    // 시트 오픈 시점 스냅숏이므로 적용 시점에 사라졌을 수 있다.
                    error = $"{op.File}: 항목 없음";
                }
                else if (op.IsDelete)
                {
                    // 휴지통으로 이동 (영구 삭제 아님). AppleScript 폴백은 Windows에서 불필요 — 실패는 그대로 기록.
                    if (ShellInterop.MoveToRecycleBin(new[] { src })) trashed++;
                    else error = $"{op.File}: 휴지통으로 이동하지 못했습니다";
                }
                else if (op.Action == "move")
                {
                    if (!IsSafeDestination(op.Destination))
                    {
                        // 재검증 실패 시 조용히 skip (failure 기록 없음).
                    }
                    else
                    {
                        var destDir = Path.GetFullPath(Path.Combine(basePath, op.Destination!.Trim()));
                        if (SamePath(src, destDir))
                        {
                            // 폴더를 자기 자신으로 이동 방지 — skip.
                        }
                        else
                        {
                            try
                            {
                                Directory.CreateDirectory(destDir);   // 없으면 생성 (중간 경로 포함)
                                var dest = Path.Combine(destDir, op.File);
                                if (File.Exists(dest) || Directory.Exists(dest))
                                    dest = UniquePath(dest);
                                if (Directory.Exists(src)) Directory.Move(src, dest);
                                else File.Move(src, dest);
                                moved++;
                            }
                            catch (Exception ex)
                            {
                                error = $"{op.File}: {ex.Message}";
                            }
                        }
                    }
                }
                // 알 수 없는 action은 파싱 단계에서 걸러지므로 여기서는 무시.
            }

            if (error is not null)
            {
                failures.Add(error);
                ok = false;
            }
            done++;
            progress?.Invoke(new AIApplyProgress(done, operations.Count, op, ok, error));
        }

        return new AIApplyResult { Moved = moved, Trashed = trashed, Failures = failures };
    }

    private static bool SamePath(string a, string b)
    {
        static string Norm(string p)
        {
            try { p = Path.GetFullPath(p); } catch { }
            return p.TrimEnd('\\');
        }
        return Norm(a).Equals(Norm(b), StringComparison.OrdinalIgnoreCase);
    }
}
