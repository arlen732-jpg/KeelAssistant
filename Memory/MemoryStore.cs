using System.Text.Json;
using KeelAssistant.Models;

namespace KeelAssistant.Memory;

/// <summary>
/// 跨專案生活習慣記憶：以 key 為主鍵存放 value，可附 tags 分類查詢。
/// 持久化於 memory.json，同 <see cref="Alarm.AlarmEngine"/> 的檔案持久化模式（Load/Save + lock）。
/// 僅存生活習慣類資料，不涉及專案開發內容。
/// </summary>
public sealed class MemoryStore
{
    private readonly string _memoryPath;
    private readonly Dictionary<string, MemoryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public MemoryStore(string baseDir)
    {
        _memoryPath = Path.Combine(baseDir, "memory.json");
        Load();
    }

    /// <summary>新增或覆寫一筆記憶。</summary>
    public MemoryEntry Set(string key, string value, IReadOnlyList<string>? tags = null)
    {
        var entry = new MemoryEntry
        {
            Key = key,
            Value = value,
            Tags = tags?.ToArray() ?? Array.Empty<string>(),
            UpdatedAt = DateTime.Now
        };
        lock (_gate)
        {
            _entries[key] = entry;
            Save();
        }
        return entry;
    }

    public MemoryEntry? Get(string key)
    {
        lock (_gate)
            return _entries.GetValueOrDefault(key);
    }

    /// <summary>列出所有記憶，可選以 tag 篩選（不分大小寫），並可用 keyword 對 key/value/tags 做模糊比對。</summary>
    public IReadOnlyList<MemoryEntry> List(string? tag = null, string? keyword = null)
    {
        lock (_gate)
        {
            var query = _entries.Values.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(tag))
                query = query.Where(e => e.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(keyword))
                query = query.Where(e =>
                    e.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    e.Value.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    e.Tags.Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
            return query.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public bool Delete(string key)
    {
        lock (_gate)
        {
            var removed = _entries.Remove(key);
            if (removed) Save();
            return removed;
        }
    }

    private void Load()
    {
        if (!File.Exists(_memoryPath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<MemoryEntry>>(
                File.ReadAllText(_memoryPath), JsonOpts);
            if (loaded is not null)
            {
                _entries.Clear();
                foreach (var e in loaded) _entries[e.Key] = e;
            }
        }
        catch { /* 壞檔忽略，視為無記憶 */ }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_memoryPath, JsonSerializer.Serialize(_entries.Values.ToList(), JsonOpts));
        }
        catch { /* 寫入失敗不影響執行期 */ }
    }
}
