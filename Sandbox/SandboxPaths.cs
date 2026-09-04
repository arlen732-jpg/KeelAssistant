using KeelAssistant.Models;

namespace KeelAssistant.Sandbox;

/// <summary>
/// 沙盒的檔案系統邊界。沙盒本身（WASM）碰不到真實磁碟，真正會接觸磁碟的
/// 只有「執行前把輸入檔讀進虛擬 FS」與「執行後把 /output 寫回實體目錄」兩個動作，
/// 這兩個動作的路徑一律先過本類別驗證。
///
/// 根目錄由 appsettings.json 的 SandboxRoot 決定，啟動時正規化並快取一次——
/// 之後所有比對都用這份快取值，避免執行期被改動而讓白名單失效。
/// MCP 參數不提供覆寫根目錄的管道（可覆寫等於沒有白名單）。
/// </summary>
/// <summary>解析後的來源檔。<paramref name="OutsideInputDir"/> 決定確認視窗是否要以紅字警示。</summary>
public sealed record SandboxInputFile(
    string Requested,
    string FullPath,
    string FinalTarget,
    long Bytes,
    bool OutsideInputDir);

public sealed class SandboxPaths
{
    private readonly long _maxFileBytes;
    private readonly string[] _denyPatterns;

    /// <summary>沙盒根目錄（已正規化的絕對路徑）。</summary>
    public string Root { get; }

    /// <summary>輸入目錄，唯讀來源；沙盒程式只讀得到這裡的檔案。</summary>
    public string InputDir { get; }

    /// <summary>輸出目錄，沙盒唯一能寫回實體磁碟的位置。</summary>
    public string OutputDir { get; }

    /// <summary>沙盒是否可用。根目錄無效時為 false，工具直接停用而非退回其他目錄。</summary>
    public bool Available { get; }

    /// <summary>不可用的原因（Available 為 true 時為 null）。</summary>
    public string? DisabledReason { get; }

    public int MaxFileSizeMb { get; }

    public SandboxPaths(AppSettings settings)
    {
        MaxFileSizeMb = Math.Clamp(settings.SandboxMaxFileSizeMb, 1, 2048);
        _maxFileBytes = MaxFileSizeMb * 1024L * 1024L;
        _denyPatterns = (settings.SandboxDenyPatterns ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToArray();

        string root;
        try
        {
            var configured = settings.SandboxRoot?.Trim();
            if (string.IsNullOrWhiteSpace(configured))
                throw new InvalidOperationException("appsettings.json 未設定 SandboxRoot");

            root = Path.GetFullPath(configured);
            if (!Path.IsPathFullyQualified(root))
                throw new InvalidOperationException("SandboxRoot 必須是絕對路徑");
        }
        catch (Exception ex)
        {
            // 刻意不 fallback 到暫存目錄或執行檔目錄——靜默改用別的位置會讓白名單失去意義
            Root = InputDir = OutputDir = "";
            Available = false;
            DisabledReason = $"沙盒根目錄設定無效：{ex.Message}";
            return;
        }

        Root = root;
        InputDir = Path.Combine(root, "input");
        OutputDir = Path.Combine(root, "output");

        try
        {
            Directory.CreateDirectory(InputDir);
            Directory.CreateDirectory(OutputDir);
            Available = true;
        }
        catch (Exception ex)
        {
            Available = false;
            DisabledReason = $"無法建立沙盒目錄 {root}：{ex.Message}";
        }
    }

    /// <summary>
    /// 解析呼叫端指定的來源檔。相對路徑視為相對於 input 目錄，也接受絕對路徑
    /// （否則每次都要先把資料 copy 進 input，摩擦大到讓人乾脆不用這個功能）。
    ///
    /// 讀取本身是非破壞性的，對「誤傷」零風險；真正要防的是檔案內容進入對話後外流，
    /// 因此改由兩道機制把關：黑名單硬擋憑證類檔案（見 <see cref="AppSettings.SandboxDenyPatterns"/>），
    /// 以及 input 目錄外的路徑一律跳確認視窗並以紅字標示。
    /// </summary>
    public SandboxInputFile ResolveInputFile(string requestedPath)
    {
        EnsureAvailable();

        if (string.IsNullOrWhiteSpace(requestedPath))
            throw new ArgumentException("readPaths 內含空白路徑");

        var raw = requestedPath.Trim().Replace('/', '\\');

        var full = Path.IsPathRooted(raw) || raw.StartsWith(@"\\", StringComparison.Ordinal)
            ? Path.GetFullPath(raw)
            : Path.GetFullPath(Path.Combine(InputDir, raw));   // 先接再正規化，讓 .. 在比對前展開

        // 黑名單先於存在性檢查：否則錯誤訊息會透露某個機敏檔在不在，
        // 等於把「這台機器有沒有設過 DB 連線」這種資訊白送出去。
        EnsureNotDenied(full, requestedPath);

        if (!File.Exists(full))
            throw new InvalidOperationException(
                $"找不到檔案「{requestedPath}」。相對路徑會相對於 {InputDir}；要讀其他位置請給完整絕對路徑。");

        // 黑名單也要比對「連結解析後的最終目標」：否則 input 底下放一個
        // data.csv → dbconnections.json 的連結就能繞過整份黑名單，
        // 而且確認視窗上顯示的還會是無害的 data.csv。
        var final = ResolveFinalTarget(full);
        if (!string.Equals(final, full, StringComparison.OrdinalIgnoreCase))
            EnsureNotDenied(final, requestedPath);

        var size = new FileInfo(final).Length;
        if (size > _maxFileBytes)
            throw new InvalidOperationException(
                $"「{requestedPath}」大小 {size / 1024.0 / 1024.0:N1} MB，超過單檔上限 {MaxFileSizeMb} MB。");

        return new SandboxInputFile(
            Requested: requestedPath.Trim(),
            FullPath: full,
            FinalTarget: final,
            Bytes: size,
            OutsideInputDir: !IsUnder(full, InputDir) || !IsUnder(final, InputDir));
    }

    /// <summary>命中黑名單即拒絕，且不跳確認視窗——這道防線刻意不給人為放行的機會。</summary>
    private void EnsureNotDenied(string fullPath, string requestedPath)
    {
        foreach (var pattern in _denyPatterns)
        {
            if (!System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                    pattern, fullPath, ignoreCase: true))
                continue;

            throw new InvalidOperationException(
                $"「{requestedPath}」命中機敏檔案黑名單（樣式 {pattern}），一律拒絕讀取。"
                + " 這類檔案的內容會進入對話並上傳，若確實需要請 Arlen 自行調整 appsettings.json 的 SandboxDenyPatterns。");
        }
    }

    /// <summary>解析符號連結／junction 的最終目標；非連結則回傳原路徑。</summary>
    private static string ResolveFinalTarget(string fullPath)
    {
        try
        {
            return File.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName ?? fullPath;
        }
        catch
        {
            return fullPath;   // 目標不存在或無權限，交由後續 FileInfo 取大小時報錯
        }
    }

    /// <summary>
    /// 取得輸出檔的實體路徑。同名不覆蓋——加上時間戳另存，
    /// 誤傷情境下「蓋掉上次的結果」和「刪檔」一樣是不可逆的損失。
    /// </summary>
    public string ResolveOutputFile(string relativeName)
    {
        EnsureAvailable();

        var raw = (relativeName ?? "").Trim().Replace('/', '\\');
        if (raw.Length == 0) throw new InvalidOperationException("輸出檔名為空");

        if (Path.IsPathRooted(raw) || raw.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException($"輸出檔名「{relativeName}」不可為絕對路徑");

        var full = Path.GetFullPath(Path.Combine(OutputDir, raw));
        if (!IsUnder(full, OutputDir))
            throw new InvalidOperationException($"輸出路徑「{relativeName}」超出沙盒輸出目錄範圍");

        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);

        if (!File.Exists(full)) return full;

        var stem = Path.GetFileNameWithoutExtension(full);
        var ext = Path.GetExtension(full);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var candidate = Path.Combine(dir, $"{stem}_{stamp}{ext}");

        // 同一秒內連續寫同名檔時再往後加序號
        for (int i = 2; File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{stem}_{stamp}_{i}{ext}");

        return candidate;
    }

    /// <summary>列出 input 目錄下的檔案（相對路徑、大小、修改時間）。</summary>
    public IEnumerable<(string Path, long Bytes, DateTime ModifiedAt)> ListInputFiles(string? pattern)
    {
        EnsureAvailable();

        var glob = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
        return new DirectoryInfo(InputDir)
            .EnumerateFiles(glob, SearchOption.AllDirectories)
            .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(f => (
                Path: Path.GetRelativePath(InputDir, f.FullName).Replace('\\', '/'),
                Bytes: f.Length,
                ModifiedAt: f.LastWriteTime));
    }

    private void EnsureAvailable()
    {
        if (!Available)
            throw new InvalidOperationException(DisabledReason ?? "沙盒不可用");
    }

    /// <summary>路徑是否落在指定目錄之下（比對前補上分隔符，避免 input2 被誤判為 input 的子路徑）。</summary>
    private static bool IsUnder(string fullPath, string directory)
    {
        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

}
