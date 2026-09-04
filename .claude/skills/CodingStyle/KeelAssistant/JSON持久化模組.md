# JSON 持久化模組

適用於「記憶體常駐狀態 + 寫檔持久化、重啟復原」的模組，如 `Alarm/AlarmEngine.cs`（`tasks.json`）、`Memory/MemoryStore.cs`（`memory.json`）。這類模組的存取模式高度一致，新增同類模組時直接照此範本，不要另創資料庫或改用其他儲存方式。

## 完整範本結構

```csharp
public sealed class XxxStore
{
    private readonly string _xxxPath;
    private readonly List<XxxEntry> _entries = new();      // 或 Dictionary，依查詢需求
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public XxxStore(string baseDir)
    {
        _xxxPath = Path.Combine(baseDir, "xxx.json");
        Load();
    }

    public XxxEntry Add(...)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            Save();
        }
        return entry;
    }

    private void Load()
    {
        if (!File.Exists(_xxxPath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<XxxEntry>>(File.ReadAllText(_xxxPath), JsonOpts);
            if (loaded is not null) { _entries.Clear(); _entries.AddRange(loaded); }
        }
        catch { /* 壞檔忽略，視為無資料 */ }
    }

    private void Save()
    {
        try { File.WriteAllText(_xxxPath, JsonSerializer.Serialize(_entries, JsonOpts)); }
        catch { /* 寫入失敗不影響執行期 */ }
    }
}
```

## 與通用寫法的差異

- **每個會修改內部集合的公開方法都要 `lock (_gate)` 包住「改集合 + Save()」這兩步**，確保寫檔前後狀態一致，不會有兩個執行緒交錯寫出半套資料：
  ```csharp
  public bool Cancel(string id)
  {
      lock (_gate)
      {
          int removed = _tasks.RemoveAll(t => t.Id == id);
          if (removed > 0) Save();
          return removed > 0;
      }
  }
  ```
- **檔案存於 `baseDir`（`AppContext.BaseDirectory`）而非 `%AppData%`**，因為這是單機常駐工具，資料跟著執行檔走。
- **`Load()`/`Save()` 一律吞掉例外，只在註解說明「為何可以吞」**：壞檔視為無資料（不阻擋啟動）、寫入失敗不影響執行期（下次操作會再存一次）。這是刻意的容錯策略，不要改成向上拋例外中斷啟動流程。
- **查詢方法（`List`/`Get`）不用另外 lock 讀完整個檔案**，直接鎖 `_gate` 讀記憶體集合再回傳，因為真實資料源是記憶體，`Save()` 只是同步副本：
  ```csharp
  public IReadOnlyList<ScheduledTask> List()
  {
      lock (_gate)
          return _tasks.OrderBy(t => t.FireAt).ToList();
  }
  ```
- **需要「依鍵覆寫」語意時用 `Dictionary<string, T>`**（`MemoryStore` 用 `StringComparer.OrdinalIgnoreCase` 讓 key 不分大小寫），**需要「依到期時間觸發」語意時用 `List<T>` + 定時掃描**（`AlarmEngine` 搭配 `System.Windows.Forms.Timer`）。依實際查詢/更新模式選擇，不要固定套用其中一種。
- **啟動時若該模組有「到期觸發」需求，建構子內 `Load()` 完要立即補跑一次檢查**（見 `AlarmEngine` 建構子最後呼叫 `CheckDue()`），涵蓋常駐程式離線期間已到期的任務。

---
## 分析檔案清單

> 本次分析日期：2026/07/06

| # | 檔案路徑 | 分類 |
|---|---------|------|
| 1 | Alarm/AlarmEngine.cs | JSON 持久化（List 型） |
| 2 | Memory/MemoryStore.cs | JSON 持久化（Dictionary 型） |
| 3 | Models/Models.cs | 持久化用資料模型（ScheduledTask/MemoryEntry） |
