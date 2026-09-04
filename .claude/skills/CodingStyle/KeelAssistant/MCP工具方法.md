# MCP 工具方法

新增/修改 `Api/KeelTools.cs` 內的 `[McpServerTool]` 方法時適用。這是 Claude（Keel）唯一的對外操作介面，等同於其他專案的「API Controller」，但沒有 HTTP 路由，是一個工具對一個靜態方法。

## 完整範本結構

每個工具方法固定四段：**驗證參數 → （必要時）切 UI 執行緒 → 呼叫模組方法 → 回傳匿名物件**。

```csharp
[McpServerTool(Name = "cancel_task"), Description("依任務 id 取消單一鬧鐘或計時任務。")]
public static object CancelTask(AlarmEngine alarm, [Description("任務 id")] string id)
{
    if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("缺少 id");
    var ok = alarm.Cancel(id);
    if (!ok) throw new InvalidOperationException("找不到該任務");
    return new { cancelled = true, id };
}
```

## 與通用寫法的差異

- **方法必須是 `static`**，類別本身是 `static class`，靠 `[McpServerToolType]` 讓框架掃描——不像一般 Service 用實例方法。
- **依賴用參數注入取得，不是建構子注入**：模組單例（`AlarmEngine`、`MusicController`、`UiDispatcher` 等）直接宣告在方法簽名裡，框架會從 DI 容器解析並傳入：
  ```csharp
  public static object PlayMusic(MusicController music, UiDispatcher ui, [Description("音樂網址")] string url)
  ```
- **每個參數都要 `[Description("...")]`**，這段文字會直接顯示給 Claude 當作呼叫依據，用繁體中文寫清楚格式/預設值，例如：
  ```csharp
  [Description("時間：HH:mm 或 yyyy/MM/dd HH:mm")] string at,
  ```
- **參數驗證一律拋例外，不回傳錯誤物件**。`[McpServerTool]` 框架會把未捕捉例外轉成工具錯誤回給 Claude，不需要自己包 try/catch 轉換成 `{ error: ... }`：
  ```csharp
  if (string.IsNullOrWhiteSpace(at)) throw new ArgumentException("缺少 at（HH:mm 或完整日期時間）");
  ```
- **回傳值用匿名物件 `new { ... }`，不用 DTO 類別**（除非該回傳已有既有型別如 `ScheduledTask`、`MemoryEntry`）。欄位命名用 camelCase 貼近 JSON 慣例（非 PascalCase）：
  ```csharp
  return new { added, total = events.Count, skippedPast, skippedAllDay, skippedDup, minutesBefore, offsetHours };
  ```
- **需要碰 WinForms/COM 的呼叫，用 `ui.Send(...)` 包住整段邏輯**（不是只包最後一步），確保整個操作在同一個 UI 執行緒內完成，避免跨執行緒讀寫共享狀態：
  ```csharp
  return ui.Send(() =>
  {
      var events = calendar.List(from, to);
      // ... 後續依賴 events 的邏輯也在同一個 Send 內
  });
  ```
- **同一分類的工具用區塊註解分隔**，並依「時間／鬧鐘／即時通知／音樂／行事曆／記憶」分組排列（見 `KeelTools.cs` 的 `// ── 分類 ──` 註解），新增工具要歸類到對應區塊，不要全部塞在檔案尾端。
- **共用的解析邏輯放在檔案底部的 `private static` 工具方法**（如 `TryParseFireAt`、`ParseTags`），不要在每個工具方法內重複實作。

⚠️ 注意：`[Description]` 是 Claude 決定何時/如何呼叫工具的唯一依據，寫不清楚（例如漏掉時間格式、預設值）會導致 Claude 誤用參數。

---
## 分析檔案清單

> 本次分析日期：2026/07/06

| # | 檔案路徑 | 分類 |
|---|---------|------|
| 1 | Api/KeelTools.cs | MCP 工具方法（全檔） |
| 2 | Api/McpHost.cs | UiDispatcher 定義 |
