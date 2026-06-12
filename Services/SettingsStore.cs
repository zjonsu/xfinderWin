using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XFinder.Services;

/// <summary>
/// UserDefaults 대응 — %APPDATA%\XFinder\settings.json 에 키-값 저장.
/// 쓰기는 디바운스 없이 즉시 저장(설정 변경 빈도가 낮음).
/// </summary>
public static class SettingsStore
{
    private static readonly object Lock = new();
    private static JsonObject _root = Load();

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XFinder");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static JsonObject Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject();
        }
        catch { /* 손상된 설정은 무시하고 초기화 */ }
        return new JsonObject();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 저장 실패는 치명적이지 않음 */ }
    }

    public static T? Get<T>(string key, T? fallback = default)
    {
        lock (Lock)
        {
            if (_root.TryGetPropertyValue(key, out var node) && node is not null)
            {
                try { return node.Deserialize<T>(); }
                catch { }
            }
            return fallback;
        }
    }

    public static void Set<T>(string key, T value)
    {
        lock (Lock)
        {
            _root[key] = JsonSerializer.SerializeToNode(value);
            Save();
        }
    }

    public static void Remove(string key)
    {
        lock (Lock)
        {
            _root.Remove(key);
            Save();
        }
    }
}
