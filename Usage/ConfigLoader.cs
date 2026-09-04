using System.Text.Json;
using KeelAssistant.Models;

namespace KeelAssistant.Usage;

/// <summary>載入 appsettings.json（容許註解與尾逗號）。</summary>
public static class ConfigLoader
{
    public static AppSettings LoadSettings(string path)
    {
        if (!File.Exists(path)) return new AppSettings();

        var options = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), options)
               ?? new AppSettings();
    }
}
