using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeelAssistant.Usage;

/// <summary>每百萬 token 單價表，從 pricing.json 載入。</summary>
public sealed class PricingTable
{
    public sealed class Rate
    {
        [JsonPropertyName("match")] public string Match { get; set; } = "*";
        [JsonPropertyName("input")] public decimal Input { get; set; }
        [JsonPropertyName("output")] public decimal Output { get; set; }
        [JsonPropertyName("cacheWrite")] public decimal CacheWrite { get; set; }
        [JsonPropertyName("cacheRead")] public decimal CacheRead { get; set; }
    }

    private sealed class PricingFile
    {
        [JsonPropertyName("models")] public List<Rate> Models { get; set; } = new();
        [JsonPropertyName("default")] public Rate Default { get; set; } = new();
    }

    private readonly List<Rate> _models;
    private readonly Rate _default;

    private PricingTable(List<Rate> models, Rate def)
    {
        _models = models;
        _default = def;
    }

    public static PricingTable Load(string path)
    {
        var options = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        var file = JsonSerializer.Deserialize<PricingFile>(File.ReadAllText(path), options)
                   ?? new PricingFile();
        return new PricingTable(file.Models, file.Default);
    }

    public Rate For(string? model)
    {
        if (!string.IsNullOrEmpty(model))
        {
            foreach (var rate in _models)
            {
                if (model.Contains(rate.Match, StringComparison.OrdinalIgnoreCase))
                    return rate;
            }
        }
        return _default;
    }

    /// <summary>依各類 token 數與單價換算成本（USD）。單價以每百萬 token 計。</summary>
    public decimal CostOf(string? model, long input, long output, long cacheWrite, long cacheRead)
    {
        var r = For(model);
        return (input * r.Input
              + output * r.Output
              + cacheWrite * r.CacheWrite
              + cacheRead * r.CacheRead) / 1_000_000m;
    }
}
