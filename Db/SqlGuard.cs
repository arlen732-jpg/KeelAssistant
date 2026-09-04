using System.Text;
using System.Text.RegularExpressions;

namespace KeelAssistant.Db;

/// <summary>SQL 語句的安全分類。</summary>
public enum SqlKind
{
    /// <summary>純查詢（SELECT/WITH），可直接執行不需人工確認。</summary>
    ReadOnly,

    /// <summary>會異動資料或結構，需經人工確認視窗核可。</summary>
    Write,

    /// <summary>一律拒絕：多語句、系統預存程序、OS 層級指令。</summary>
    Blocked
}

/// <param name="Kind">分類結果</param>
/// <param name="Reason">Blocked/Write 時的中文原因，ReadOnly 時為空字串</param>
public readonly record struct SqlClassification(SqlKind Kind, string Reason);

/// <summary>
/// SQL 語句分類器：判斷一段 SQL 是純查詢、會寫入、還是必須直接拒絕。
///
/// 定位是「防呆」而非「防駭」——它擋的是 Claude 誤產生的破壞性 SQL，
/// 動態 SQL 等手法仍有繞過空間，真正的防線是 DB 端的唯讀帳號。
/// 因此本類別刻意保守：判斷不出來的語句一律歸為 <see cref="SqlKind.Write"/>（需人工確認），
/// 而非放行。
/// </summary>
public static class SqlGuard
{
    // 一律拒絕：可觸及 OS、檔案系統或伺服器組態，沒有任何從 Claude 呼叫的正當理由
    private static readonly string[] BlockedKeywords =
    {
        "XP_CMDSHELL", "OPENROWSET", "OPENQUERY", "OPENDATASOURCE",
        "SHUTDOWN", "RECONFIGURE", "DBCC", "BULK"
    };

    // 會異動資料/結構：允許但需人工確認
    private static readonly string[] WriteKeywords =
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "DROP", "TRUNCATE",
        "ALTER", "CREATE", "GRANT", "REVOKE", "EXEC", "EXECUTE", "CALL"
    };

    // 允許作為純查詢起首的關鍵字
    private static readonly string[] ReadOnlyStarters = { "SELECT", "WITH", "SHOW", "EXPLAIN" };

    /// <summary>
    /// 分類一段 SQL。呼叫端依 <see cref="SqlClassification.Kind"/> 決定直接執行、
    /// 跳確認視窗、或拒絕。
    /// </summary>
    public static SqlClassification Classify(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return new SqlClassification(SqlKind.Blocked, "SQL 內容為空");

        // 先移除註解與字串常值，避免 /*x*/DROP 這類繞過，也避免 SELECT 'DROP TABLE' 被誤判
        var clean = Sanitize(sql).ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(clean))
            return new SqlClassification(SqlKind.Blocked, "SQL 移除註解後無實際內容");

        // 多語句一律拒絕：這是整套防線最有效的一環，可擋掉「SELECT 1; DROP TABLE x」
        if (CountStatements(clean) > 1)
            return new SqlClassification(SqlKind.Blocked, "偵測到多個語句（以分號分隔），一次只能執行一個語句");

        foreach (var kw in BlockedKeywords)
            if (ContainsKeyword(clean, kw))
                return new SqlClassification(SqlKind.Blocked, $"含禁用關鍵字 {kw}（可觸及作業系統或伺服器組態）");

        // 系統預存程序（sp_ / xp_ 前綴）
        if (Regex.IsMatch(clean, @"\b(SP|XP)_\w+", RegexOptions.CultureInvariant))
            return new SqlClassification(SqlKind.Blocked, "含系統預存程序呼叫（sp_ / xp_ 前綴）");

        // SELECT ... INTO 新表 屬於寫入（PG 的 SELECT INTO 亦然）；
        // 但要排開 INSERT INTO（已被 WriteKeywords 攔下）與 BULK INSERT
        if (ContainsKeyword(clean, "INTO") && StartsWith(clean, "SELECT"))
            return new SqlClassification(SqlKind.Write, "SELECT ... INTO 會建立新資料表，屬寫入操作");

        foreach (var kw in WriteKeywords)
            if (ContainsKeyword(clean, kw))
                return new SqlClassification(SqlKind.Write, $"含寫入關鍵字 {kw}");

        // 走到這裡沒有任何寫入跡象，再確認起首關鍵字確實是查詢
        foreach (var starter in ReadOnlyStarters)
            if (StartsWith(clean, starter))
                return new SqlClassification(SqlKind.ReadOnly, "");

        // 認不出來的語句（SET / USE / BEGIN 等）保守歸為需確認，不放行
        return new SqlClassification(SqlKind.Write, "無法確認為純查詢語句，保守視為寫入");
    }

    /// <summary>
    /// 移除行註解（--）、區塊註解（/* */，支援巢狀）與字串常值內容，
    /// 保留語法骨架供關鍵字比對。識別字引號（[] 與 ""）內容一併清空，
    /// 避免資料表名叫 [DROP] 之類的情況造成誤判。
    /// </summary>
    private static string Sanitize(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        int i = 0, blockDepth = 0;
        bool lineComment = false;
        char quote = '\0';   // 目前所在的引號種類：' " ]

        while (i < sql.Length)
        {
            char c = sql[i];
            char next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (lineComment)
            {
                if (c is '\n') { lineComment = false; sb.Append(' '); }
                i++;
                continue;
            }

            if (blockDepth > 0)
            {
                if (c == '/' && next == '*') { blockDepth++; i += 2; continue; }
                if (c == '*' && next == '/') { blockDepth--; i += 2; sb.Append(' '); continue; }
                i++;
                continue;
            }

            if (quote != '\0')
            {
                // 單引號的 '' 是逸出而非結束
                if (c == '\'' && quote == '\'' && next == '\'') { i += 2; continue; }
                if (c == quote) { quote = '\0'; sb.Append(' '); }
                i++;
                continue;
            }

            if (c == '-' && next == '-') { lineComment = true; i += 2; continue; }
            if (c == '/' && next == '*') { blockDepth = 1; i += 2; continue; }
            if (c is '\'' or '"') { quote = c; i++; continue; }
            if (c == '[') { quote = ']'; i++; continue; }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>計算實際語句數量（忽略結尾分號與空白段落）。</summary>
    private static int CountStatements(string clean)
        => clean.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Count(seg => !string.IsNullOrWhiteSpace(seg));

    /// <summary>以字界比對關鍵字，避免 updated_at 被當成 UPDATE。</summary>
    private static bool ContainsKeyword(string clean, string keyword)
        => Regex.IsMatch(clean, $@"(?<![\w_]){Regex.Escape(keyword)}(?![\w_])", RegexOptions.CultureInvariant);

    /// <summary>判斷第一個關鍵字是否為指定字（前面可能有括號，如 (SELECT ...)）。</summary>
    private static bool StartsWith(string clean, string keyword)
        => Regex.IsMatch(clean.TrimStart(), $@"^[\s(]*{Regex.Escape(keyword)}(?![\w_])", RegexOptions.CultureInvariant);
}
