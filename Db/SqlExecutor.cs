using System.Data.Common;
using System.Diagnostics;
using KeelAssistant.Models;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace KeelAssistant.Db;

/// <summary>
/// 依 SQLConnID 執行 SQL 並轉成可 JSON 化的結果。
///
/// 安全設計：連線字串與明文密碼只在本類別內部短暫存在，任何對外拋出的例外訊息
/// 都會先經 <see cref="Sanitize"/> 遮蔽主機名稱與帳號，避免 DB 位置與帳號經由
/// 錯誤訊息洩漏給 Claude。
/// </summary>
public sealed class SqlExecutor(DbConnectionStore store)
{
    private readonly DbConnectionStore _store = store;

    /// <summary>
    /// 回傳結果集的查詢。maxRows 為硬性上限，超過即截斷並標記 Truncated。
    ///
    /// 連線標記為唯讀時，整段查詢包在一個永不 commit 的交易內（見 <see cref="BeginReadOnly"/>），
    /// 作為 DB 唯讀帳號與 <see cref="SqlGuard"/> 之外的第三道防線。
    /// </summary>
    public SqlQueryResult Query(DbConnectionEntry entry, string sql, int maxRows, int timeoutSec)
    {
        var sw = Stopwatch.StartNew();
        var result = new SqlQueryResult();

        try
        {
            using var conn = OpenConnection(entry);
            using var tx = entry.ReadOnly ? BeginReadOnly(conn) : null;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSec;
            cmd.Transaction = tx;

            using var reader = cmd.ExecuteReader();
            result.Columns = Enumerable.Range(0, reader.FieldCount)
                                       .Select(reader.GetName)
                                       .ToArray();

            while (reader.Read())
            {
                if (result.Rows.Count >= maxRows)
                {
                    result.Truncated = true;
                    break;
                }

                var row = new object?[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                    row[i] = Normalize(reader.GetValue(i));
                result.Rows.Add(row);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"查詢失敗：{Sanitize(ex.Message, entry)}", ex);
        }

        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    /// <summary>執行寫入類語句，回傳受影響列數。</summary>
    public int NonQuery(DbConnectionEntry entry, string sql, int timeoutSec)
    {
        try
        {
            using var conn = OpenConnection(entry);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSec;
            return cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"執行失敗：{Sanitize(ex.Message, entry)}", ex);
        }
    }

    /// <summary>
    /// 測試連線是否可用（供設定視窗的「測試連線」按鈕）。
    /// 這是給 Arlen 看的，訊息不遮蔽，方便診斷。
    /// </summary>
    public void TestConnection(DbConnectionEntry entry)
    {
        using var conn = OpenConnection(entry);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        cmd.CommandTimeout = 15;
        cmd.ExecuteScalar();
    }

    /// <summary>
    /// 為唯讀連線開一個永不 commit 的交易，離開 using 範圍時自動 rollback。
    ///
    /// PostgreSQL 用 <c>SET TRANSACTION READ ONLY</c>，寫入會被 DB 引擎當場拒絕
    /// （不是事後撤銷），是真正的強制唯讀。
    /// MSSQL 無等價語法，只能靠不 commit 讓變更被撤銷——注意這不保證完全還原
    /// （IDENTITY/SEQUENCE 消耗、觸發程序的外部副作用撤不回來），故 DB 端唯讀帳號
    /// 仍是不可省略的主防線。
    /// </summary>
    private static DbTransaction BeginReadOnly(DbConnection conn)
    {
        var tx = conn.BeginTransaction();

        if (conn is NpgsqlConnection)
        {
            using var setCmd = conn.CreateCommand();
            setCmd.Transaction = tx;
            setCmd.CommandText = "SET TRANSACTION READ ONLY";
            setCmd.ExecuteNonQuery();
        }

        return tx;
    }

    private DbConnection OpenConnection(DbConnectionEntry entry)
    {
        var cs = _store.BuildConnectionString(entry);
        DbConnection conn = entry.Provider switch
        {
            DbProvider.Mssql => new SqlConnection(cs),
            DbProvider.Postgresql => new NpgsqlConnection(cs),
            _ => throw new InvalidOperationException($"不支援的資料庫類型：{entry.Provider}")
        };
        conn.Open();
        return conn;
    }

    /// <summary>
    /// 把 DB 型別轉成 JSON 友善的值。DateTime 統一格式化為字串（避免時區與序列化格式歧義），
    /// byte[] 只回報長度（二進位內容對 Claude 無意義且會炸掉 context）。
    /// </summary>
    private static object? Normalize(object value) => value switch
    {
        DBNull => null,
        DateTime dt => dt.ToString("yyyy\\/MM\\/dd HH:mm:ss"),
        DateTimeOffset dto => dto.ToString("yyyy\\/MM\\/dd HH:mm:ss zzz"),
        TimeSpan ts => ts.ToString(@"hh\:mm\:ss"),
        byte[] bytes => $"<binary {bytes.Length} bytes>",
        Guid g => g.ToString(),
        _ => value
    };

    /// <summary>
    /// 遮蔽例外訊息中的主機名稱與帳號。DB 錯誤訊息（如 Invalid object name）本身可放行，
    /// 但連線層的錯誤常把 server/user 直接寫進訊息裡。
    /// </summary>
    private static string Sanitize(string message, DbConnectionEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Server))
            message = message.Replace(entry.Server, "<server>", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(entry.User))
            message = message.Replace(entry.User, "<user>", StringComparison.OrdinalIgnoreCase);
        return message;
    }
}
