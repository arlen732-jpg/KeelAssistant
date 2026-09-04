using System.Text.Json;
using KeelAssistant.Models;

namespace KeelAssistant.Usage;

/// <summary>
/// 負責讀寫 ~/.claude/.credentials.json。
/// 機敏資訊一律從檔案讀取，禁止寫死於程式碼。
/// </summary>
public sealed class CredentialStore
{
    private readonly string _path;

    public CredentialStore()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _path = System.IO.Path.Combine(home, ".claude", ".credentials.json");
    }

    public string Path => _path;

    public OAuthCredentials? Load()
    {
        if (!File.Exists(_path)) return null;
        var json = File.ReadAllText(_path);
        var file = JsonSerializer.Deserialize<CredentialsFile>(json);
        return file?.ClaudeAiOauth;
    }

    /// <summary>
    /// Token 換新後寫回檔案，僅更新 token 與到期時間，保留其餘欄位。
    /// </summary>
    public void Save(OAuthCredentials cred)
    {
        var wrapper = new CredentialsFile { ClaudeAiOauth = cred };
        var json = JsonSerializer.Serialize(wrapper, new JsonSerializerOptions
        {
            WriteIndented = false
        });
        File.WriteAllText(_path, json);
    }
}
