using System.Text;

namespace KeelAssistant.Db;

/// <summary>
/// SQL 排版器，**僅供人工確認視窗顯示用**。
///
/// 設計原則：只在 token 之間插入換行與縮排，**絕不改動任何 token 的內容**
/// （字串常值、識別字、註解一律原文照搬）。因為實際送進資料庫的永遠是原始 SQL，
/// 若排版過程改動了內容，確認視窗顯示的就不是真正要執行的東西，人工閘門即失去意義。
/// 基於同樣理由，排版途中發生任何例外一律退回原文——顯示未排版的正確 SQL，
/// 遠優於顯示排版過的錯誤 SQL。
///
/// 刻意不引入第三方 formatter：需同時吃 T-SQL 與 PostgreSQL 語法，
/// 且此處只需要「看得清楚子句邊界」，不需要完整的 SQL 重寫引擎。
/// </summary>
public static class SqlFormatter
{
    private enum Kind
    {
        Word,      // 關鍵字、識別字、數字
        Literal,   // 字串常值、引號識別字、註解——內容不解析
        Symbol     // 括號、逗號、運算子
    }

    private readonly record struct Token(string Text, Kind Kind)
    {
        /// <summary>供關鍵字比對用的大寫形式；非 Word 一律回空字串，避免字串內容被當關鍵字。</summary>
        public string Upper => Kind == Kind.Word ? Text.ToUpperInvariant() : "";
    }

    /// <summary>主要子句：在其前換行，縮排回到當前括號層級。</summary>
    private static readonly string[][] ClauseBreaks =
    {
        new[] { "SELECT" }, new[] { "FROM" }, new[] { "WHERE" }, new[] { "HAVING" },
        new[] { "GROUP", "BY" }, new[] { "ORDER", "BY" },
        new[] { "UNION", "ALL" }, new[] { "UNION" }, new[] { "EXCEPT" }, new[] { "INTERSECT" },
        new[] { "INNER", "JOIN" }, new[] { "CROSS", "JOIN" },
        new[] { "LEFT", "OUTER", "JOIN" }, new[] { "RIGHT", "OUTER", "JOIN" }, new[] { "FULL", "OUTER", "JOIN" },
        new[] { "LEFT", "JOIN" }, new[] { "RIGHT", "JOIN" }, new[] { "FULL", "JOIN" }, new[] { "JOIN" },
        new[] { "INSERT", "INTO" }, new[] { "DELETE", "FROM" },
        new[] { "UPDATE" }, new[] { "SET" }, new[] { "VALUES" }, new[] { "RETURNING" },
        new[] { "LIMIT" }, new[] { "OFFSET" }, new[] { "WITH" }
    };

    /// <summary>次要子句：在其前換行並多縮一層，讓它從屬於上一個主要子句。</summary>
    private static readonly string[][] SubBreaks =
    {
        new[] { "AND" }, new[] { "OR" }, new[] { "ON" },
        new[] { "WHEN" }, new[] { "ELSE" }
    };

    /// <summary>前後都不加空白的符號（成員存取與陳述式結尾）。</summary>
    private static readonly char[] TightSymbols = { '.', ';' };

    public static string Format(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return sql;

        try
        {
            var tokens = Tokenize(sql);
            return tokens.Count == 0 ? sql : Render(tokens);
        }
        catch
        {
            // 排版失敗一律退回原文：寧可難讀，不可失真
            return sql;
        }
    }

    // ── 詞法切分 ──────────────────────────────────────────────────

    /// <summary>
    /// 切成 token，原文完整保留。字串常值／引號識別字／註解整段視為單一 Literal，
    /// 內容不再解析，因此 'DROP TABLE' 這種字串不會被誤認成關鍵字而換行。
    /// </summary>
    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        int i = 0;

        while (i < sql.Length)
        {
            char c = sql[i];
            char next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (char.IsWhiteSpace(c)) { i++; continue; }

            // 註解：整段（含結尾）視為 Literal
            if (c == '-' && next == '-')
            {
                int end = sql.IndexOf('\n', i);
                if (end < 0) end = sql.Length;
                tokens.Add(new Token(sql[i..end].TrimEnd(), Kind.Literal));
                i = end;
                continue;
            }
            if (c == '/' && next == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? sql.Length : end + 2;
                tokens.Add(new Token(sql[i..end], Kind.Literal));
                i = end;
                continue;
            }

            // 引號類：' 有 '' 逸出，" 與 [ ] 為識別字
            if (c is '\'' or '"' or '[')
            {
                char close = c == '[' ? ']' : c;
                int j = i + 1;
                while (j < sql.Length)
                {
                    if (sql[j] == close)
                    {
                        // 單引號的 '' 是逸出，不是結束
                        if (close == '\'' && j + 1 < sql.Length && sql[j + 1] == '\'') { j += 2; continue; }
                        j++;
                        break;
                    }
                    j++;
                }
                tokens.Add(new Token(sql[i..Math.Min(j, sql.Length)], Kind.Literal));
                i = j;
                continue;
            }

            // 識別字／關鍵字／數字
            if (char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$')
            {
                int j = i;
                while (j < sql.Length &&
                       (char.IsLetterOrDigit(sql[j]) || sql[j] is '_' or '@' or '#' or '$'))
                    j++;
                tokens.Add(new Token(sql[i..j], Kind.Word));
                i = j;
                continue;
            }

            // 多字元運算子（PG 的 :: 轉型、比較運算子等）
            foreach (var op in new[] { "::", "<>", "!=", "<=", ">=", "||" })
            {
                if (i + op.Length <= sql.Length && sql.AsSpan(i, op.Length).SequenceEqual(op))
                {
                    tokens.Add(new Token(op, Kind.Symbol));
                    i += op.Length;
                    goto next;
                }
            }

            tokens.Add(new Token(c.ToString(), Kind.Symbol));
            i++;

        next: ;
        }

        return tokens;
    }

    // ── 排版輸出 ──────────────────────────────────────────────────

    private static string Render(List<Token> tokens)
    {
        var sb = new StringBuilder();
        int depth = 0;         // 括號深度
        bool atLineStart = true;

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (token.Text == ")") depth = Math.Max(0, depth - 1);

            int breakLen = MatchLength(tokens, i, ClauseBreaks);
            int subLen = breakLen > 0 ? 0 : MatchLength(tokens, i, SubBreaks);

            if ((breakLen > 0 || subLen > 0) && sb.Length > 0 && !atLineStart)
            {
                sb.AppendLine();
                sb.Append(Indent(depth + (subLen > 0 ? 1 : 0)));
                atLineStart = true;
            }

            if (!atLineStart && NeedSpace(tokens[i - 1], token))
                sb.Append(' ');

            sb.Append(token.Text);
            atLineStart = false;

            if (token.Text == "(") depth++;

            // 逗號後換行：SELECT 欄位清單與 UPDATE ... SET 的多欄位賦值最受益
            if (token.Text == "," && i + 1 < tokens.Count)
            {
                sb.AppendLine();
                sb.Append(Indent(depth + 1));
                atLineStart = true;
            }
        }

        return sb.ToString();
    }

    private static string Indent(int level) => new(' ', Math.Max(0, level) * 4);

    /// <summary>
    /// 比對從 <paramref name="start"/> 起的 token 是否構成任一片語，回傳命中的片語長度（0 = 未命中）。
    /// 片語表已由長到短排列，故先命中者即為最長匹配（LEFT OUTER JOIN 優先於 LEFT JOIN）。
    /// </summary>
    private static int MatchLength(List<Token> tokens, int start, string[][] phrases)
    {
        foreach (var phrase in phrases)
        {
            if (start + phrase.Length > tokens.Count) continue;

            bool hit = true;
            for (int k = 0; k < phrase.Length; k++)
            {
                if (tokens[start + k].Upper != phrase[k]) { hit = false; break; }
            }
            if (hit) return phrase.Length;
        }
        return 0;
    }

    /// <summary>判斷兩個相鄰 token 之間是否需要空白。</summary>
    private static bool NeedSpace(Token prev, Token curr)
    {
        if (curr.Text.Length == 1 && TightSymbols.Contains(curr.Text[0])) return false;
        if (prev.Text.Length == 1 && TightSymbols.Contains(prev.Text[0])) return false;
        if (curr.Text is "," or ")") return false;
        if (prev.Text == "(") return false;
        if (curr.Text == "::" || prev.Text == "::") return false;

        // 函式呼叫的左括號緊貼函式名；關鍵字後的括號（IN (...)、VALUES (...)）則留空白
        if (curr.Text == "(")
            return prev.Kind != Kind.Word || IsKeywordBeforeParen(prev.Upper);

        return true;
    }

    /// <summary>這些關鍵字後面的括號是語法結構而非函式呼叫，需留一個空白。</summary>
    private static bool IsKeywordBeforeParen(string upper) => upper
        is "IN" or "VALUES" or "AND" or "OR" or "NOT" or "ON" or "WHERE" or "SELECT"
        or "EXISTS" or "FROM" or "BY" or "SET" or "HAVING" or "ALL" or "ANY" or "USING";
}
