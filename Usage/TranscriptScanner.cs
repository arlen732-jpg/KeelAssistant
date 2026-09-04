using System.Text;
using System.Text.Json;
using KeelAssistant.Models;

namespace KeelAssistant.Usage;

/// <summary>
/// 增量掃描 ~/.claude/projects 下所有 *.jsonl，累計每日 token 與成本。
/// 記住每個檔案已讀到的 byte offset，只解析新增內容，避免重複整檔讀取。
/// </summary>
public sealed class TranscriptScanner
{
    private readonly string _projectsRoot;
    private readonly PricingTable _pricing;

    // 檔案路徑 -> 已讀 byte offset
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);

    // 本地日期 -> (model -> 統計)
    private readonly Dictionary<DateOnly, Dictionary<string, TokenStats>> _byDate = new();

    public string LatestVersion { get; private set; } = "2.1.128";

    public TranscriptScanner(PricingTable pricing)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _projectsRoot = Path.Combine(home, ".claude", "projects");
        _pricing = pricing;
    }

    /// <summary>掃描所有新增的 JSONL 內容，更新累計統計。</summary>
    public void Scan()
    {
        if (!Directory.Exists(_projectsRoot)) return;

        foreach (var file in Directory.EnumerateFiles(_projectsRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            try { ScanFile(file); }
            catch { /* 單一檔案壞行不影響整體 */ }
        }
    }

    private void ScanFile(string file)
    {
        var info = new FileInfo(file);
        var start = _offsets.TryGetValue(file, out var off) ? off : 0L;

        // 檔案被截斷或重建（length 變小）→ 從頭讀
        if (info.Length < start) start = 0L;
        if (info.Length == start) return;

        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, Encoding.UTF8);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            ParseLine(line);
        }

        _offsets[file] = info.Length;
    }

    private void ParseLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (root.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.String)
        {
            var v = ver.GetString();
            if (!string.IsNullOrEmpty(v)) LatestVersion = v;
        }

        if (!root.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("usage", out var usage)) return;

        var model = msg.TryGetProperty("model", out var m) ? m.GetString() : null;

        long input = GetLong(usage, "input_tokens");
        long output = GetLong(usage, "output_tokens");
        long cacheWrite = GetLong(usage, "cache_creation_input_tokens");
        long cacheRead = GetLong(usage, "cache_read_input_tokens");
        if (input == 0 && output == 0 && cacheWrite == 0 && cacheRead == 0) return;

        DateOnly date = ResolveLocalDate(root);

        var stats = new TokenStats
        {
            InputTokens = input,
            OutputTokens = output,
            CacheCreationTokens = cacheWrite,
            CacheReadTokens = cacheRead,
            CostUsd = _pricing.CostOf(model, input, output, cacheWrite, cacheRead)
        };

        var bucket = GetBucket(date);
        var key = model ?? "unknown";
        if (!bucket.TryGetValue(key, out var agg))
        {
            agg = new TokenStats();
            bucket[key] = agg;
        }
        agg.Add(stats);
    }

    private static long GetLong(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0L;

    private static DateOnly ResolveLocalDate(JsonElement root)
    {
        if (root.TryGetProperty("timestamp", out var ts) &&
            ts.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(ts.GetString(), out var dto))
        {
            return DateOnly.FromDateTime(dto.ToLocalTime().DateTime);
        }
        return DateOnly.FromDateTime(DateTime.Now);
    }

    private Dictionary<string, TokenStats> GetBucket(DateOnly date)
    {
        if (!_byDate.TryGetValue(date, out var b))
        {
            b = new Dictionary<string, TokenStats>(StringComparer.OrdinalIgnoreCase);
            _byDate[date] = b;
        }
        return b;
    }

    /// <summary>今日（本地日）跨模型合計。</summary>
    public TokenStats TodayTotal()
    {
        var total = new TokenStats();
        if (_byDate.TryGetValue(DateOnly.FromDateTime(DateTime.Now), out var bucket))
            foreach (var s in bucket.Values) total.Add(s);
        return total;
    }

    /// <summary>今日各模型明細（成本由高到低）。</summary>
    public IReadOnlyList<(string Model, TokenStats Stats)> TodayByModel()
    {
        if (!_byDate.TryGetValue(DateOnly.FromDateTime(DateTime.Now), out var bucket))
            return Array.Empty<(string, TokenStats)>();

        return bucket
            .Select(kv => (kv.Key, kv.Value))
            .OrderByDescending(x => x.Value.CostUsd)
            .ToList();
    }
}
