# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 版本控制：SVN

## 專案簡介

Keel Assistant：系統匣常駐助理（.NET 8 WinForms），整合時間、鬧鐘/計時、音樂、Claude 用量監控、Outlook 行事曆、會議錄音、SQL 執行、Python 沙盒等模組，並內嵌本機 MCP Server（Streamable HTTP，`127.0.0.1:38600/mcp`）供 Claude Code（Keel）以工具直接調用，取代舊有 PowerShell 拼 JSON 的作法。

## 常用指令

```powershell
dotnet build      # 編譯
dotnet run        # 執行（出現在系統匣，單一實例，Mutex 防多開）
```

- 無獨立測試專案，無 lint 設定。
- 關閉視窗＝縮回系統匣，右鍵選單「結束」才真正退出行程。
- 開機自動啟動：執行 `install-autostart.ps1`（工作排程器，登入後延遲 30 秒）。
- 註冊到 Claude Code：`claude mcp add --scope user --transport http keel http://127.0.0.1:38600/mcp`（需先啟動 exe，且要新開 session 工具才會出現，詳見 `MCP-Setup.md`）。

## 架構

### 進入點與生命週期

`Program.cs` 用 Mutex 確保單一實例，`ApplicationConfiguration.Initialize()` 後以 `using` 建立 `TrayApplicationContext` 並 `Application.Run`；結束時強制 `Environment.Exit(0)`，確保 Kestrel／WebView2 殘留執行緒不會卡住行程退出。

`TrayApplicationContext.cs` 是整個應用的組裝根：建立 `NotifyIcon`、初始化五大模組實例（`AlarmEngine`、`MusicController`、`CalendarService`、`MemoryStore`、用量輪詢用的 `CredentialStore`/`TranscriptScanner`/`OAuthUsageClient`），再把這些單例塞進 `McpHost` 供 MCP 工具方法使用。托盤選單在每次 `Opening` 時透過 `RebuildMenuItems` 全部重建（非增量更新）。

### MCP Server（`Api/`）

- `McpHost.cs`：內嵌 Kestrel（`WebApplication.CreateSlimBuilder`），只綁 `IPAddress.Loopback`，`Stateless = true`，用 `WithToolsFromAssembly()` 掃描組件內所有 `[McpServerToolType]`。各模組單例以 `AddSingleton` 注入 DI，供工具方法用參數注入取得。
- `UiDispatcher`：所有需要碰 WinForms 視窗或 Outlook COM（需 STA）的 MCP 工具呼叫，必須透過此類別的 `Send`/`Send<T>`（用建構時記下的 UI `SynchronizationContext`）切回 UI 執行緒同步執行，否則會有跨執行緒例外或 COM 執行緒模型錯誤。
- `KeelTools.cs`：實際的 `[McpServerTool]` 工具方法定義（`get_time`、`set_alarm`、`play_music`、`calendar_add` 等，完整清單見 `README.md`／`MCP-Setup.md`）。新增工具時比照既有方法在此檔案加上 `[McpServerTool]` 方法，並經由建構子注入的模組單例操作狀態。
- 因為常駐狀態（鬧鐘排程、音樂播放中、用量快照）活在這個 WinForm process 記憶體裡，MCP 必須用 **HTTP transport**（非 stdio），否則每次呼叫都是新行程、狀態會遺失。

### 功能模組

- `Alarm/AlarmEngine.cs`：鬧鐘/計時引擎，記憶體 Timer + `tasks.json` 持久化（重啟後復原排程），到期可觸發 `stopMusic` callback 連動音樂模組。
- `Music/MusicController.cs` + `MusicForm.cs`：以 WebView2 內嵌播放線上服務網址（YouTube 等）。
- `Calendar/CalendarService.cs`：透過 Outlook COM 互操作讀寫行事曆，因此相關呼叫需經 `UiDispatcher` 切到 STA 執行緒。
- `Memory/MemoryStore.cs`：鍵值式生活習慣記憶儲存（給 Keel 的 `memory_*` 工具用，與本專案的開發用 auto-memory 是不同機制）。
- `Db/`：SQL 執行模組（MSSQL / PostgreSQL）。`DbConnectionStore` 以 DPAPI（CurrentUser + 額外熵值）加密密碼存於 `dbconnections.json`，明文密碼只在 `BuildConnectionString` 內部短暫存在；`SqlGuard` 是語句分類器（剝除註解/字串常值後比對關鍵字，回傳 ReadOnly/Write/Blocked，認不出來的一律歸 Write）；`SqlExecutor` 執行並轉 JSON，唯讀連線會包一層永不 commit 的交易（PG 用 `SET TRANSACTION READ ONLY`），例外訊息會遮蔽主機名與帳號；`DbConnectionForm`/`SqlConfirmForm` 分別是連線設定與寫入前的人工確認視窗。
  - **MCP 刻意不提供列舉連線的工具**，`connId` 必須由 Arlen 主動告知，`ResolveConnection` 的錯誤訊息也不列出可用代號（避免變相列舉）。修改此模組時請維持這個約束。
  - 安全設計是三層：DB 端唯讀帳號（主防線）→ `SqlGuard`（防呆）→ 唯讀交易（兜底）。`SqlGuard` 是啟發式的，不可當成唯一防線。
- `Sandbox/`：Python 沙盒模組（Pyodide / WebAssembly）。`PyodideRunner` 以 WebView2 載入 `wwwroot/sandbox/` 內隨程式打包的 Pyodide 314.0.2（CPython 3.14 + numpy/pandas/openpyxl），跑 LLM 產生的 Python；`SandboxPaths` 負責檔案邊界（黑名單、連結解析、大小上限、輸出不覆蓋）；`SandboxConfirmForm` 是讀檔前的人工確認視窗。
  - **隔離來自 WASM 本身**：沒有 syscall、沒有真實檔案系統、沒有 socket。威脅模型是「誤傷」而非「刻意逃逸」——WASM 對前者是完備的，因此不採容器方案（省掉 Docker 常駐與映像維護）。
  - **三個併發設計不可拿掉**（多個 Claude Code session 與 SubAgent 共用同一個 `Stateless=true` 的 MCP Server，沒有 session 身分可區分）：`SemaphoreSlim(1,1)` 全域序列化、每次執行建新 Worker 跑完即銷毀、**逾時等待跑在執行緒池而非 `UiDispatcher`**。
  - WebView2 用**專屬的 `webview2-sandbox` user data folder**，與音樂播放器的 WebView2 分屬不同行程樹。這既讓記憶體歸屬算得清（看門狗據此判斷是否超過 `SandboxMemoryLimitMb`），也讓沙盒的建立／銷毀完全不影響音樂播放。修改時請維持分開。
  - WebView2 用時才建、閒置 `SandboxIdleShutdownMinutes` 後整組銷毀（實測可完全回收，不殘留 `msedgewebview2.exe`）。
  - `wwwroot/sandbox/pyodide/pyodide-lock.json` 是**裁剪過的**——只列隨程式打包的 7 個 wheel，讓 `loadPackagesFromImports` 遇到未打包的套件直接失敗，而不是去 CDN 抓（沙盒無網路，否則會變成難懂的逾時錯誤）。其中 `openpyxl` 刻意掛成 `pandas` 的相依，因為 pandas 是在 `to_excel` 內部才延遲 import，靜態掃不到。
- `Notifications/`：Windows 通知監看（防漏看 LINE 訊息）。`NotificationWatcher` 以 `UserNotificationListener` 每 3 秒輪詢通知中心，比對 `appsettings.json` 的三組規則後跳 `MentionAlertForm`（置頂但不搶焦點）或托盤氣泡。
  - **刻意用輪詢而非 `NotificationChanged` 事件**：該事件在傳統桌面程式上長期不可靠，輪詢成本僅毫秒級。同理，**不掃 UI**——通知的視覺元素無穩定定位點，`UserNotificationListener` 才是官方管道。
  - **強弱兩級訊號不可合併**：關鍵聯絡人／被 @ 屬強訊號才准跳視窗；「內文提到名字」誤報率高，只跳氣泡。合併後會因警報疲勞讓整個機制失效。
  - 首次掃描只建 id 索引不提醒（否則開機會被通知中心的舊訊息洗版）；同一聊天室有冷卻期。
  - 因此模組需要 WinRT 投影，`TargetFramework` 為 `net8.0-windows10.0.19041.0`（非 `net8.0-windows`），改動 TFM 前請確認此依賴。
  - 三個外部前置條件任一不成立即靜默失效，故 `Status` 一律呈現在托盤選單：① 系統通知存取授權 ② LINE 須為市集版（傳統桌面版通知不進通知中心）③ LINE 未關閉訊息預覽（關閉後內文只剩「你有一則新訊息」，內文比對失效）。
- `Usage/`：移植自 ClaudeUsageMonitor，`ConfigLoader` 讀 `appsettings.json`、`CredentialStore` 讀本機 OAuth 憑證、`OAuthUsageClient` 打官方用量 API、`TranscriptScanner` 掃本機 transcript 估算 token/成本、`PricingTable` 讀 `pricing.json` 算費用。輪詢間隔硬限 `>= 180` 秒。
  - **MCP 的 `get_usage_5h` 只讀 `UsageProvider` 的共享快照，不得改成即時呼叫 `OAuthUsageClient`**：官方 usage 端點有嚴格 429 桶（見 `OAuthUsageClient` 的註解與 `RateLimitedException`），而 MCP 是 `Stateless=true`、多 session 與 SubAgent 共用同一行程，各自即時抓必然撞 429 並連帶讓托盤輪詢失效。快照過舊的問題用回傳 `snapshotAgeSeconds` 揭露，而非改抓法。
- `Time/TimeService.cs`：提供當下時間/星期/時段（取代舊 `get_local_time.py`）。
- `Models/Models.cs`：跨模組共用的資料模型（`AppSettings`、`OAuthUsageResponse`、`UsageWindow`、`Spend` 等）。

### 設定檔

`appsettings.json`：`ApiPort`（MCP 埠，預設 38600）、`AlarmSoundPath`、用量輪詢的 `PollIntervalSeconds`/`WarnThresholdPercent`/`CriticalThresholdPercent`、`OAuthClientId`。埠衝突時改此檔重啟即可，不需改程式碼。

Python 沙盒設定同樣在 `appsettings.json`：`SandboxRoot`（預設 `C:\Vista\_sandbox`，刻意與專案目錄平行而非置於其下——路徑驗證一旦有疏漏，逃逸的第一站不該是放憑證的地方）、`SandboxTimeoutSeconds`、`SandboxMemoryLimitMb`、`SandboxMaxFileSizeMb`、`SandboxIdleShutdownMinutes`、`SandboxDenyPatterns`。**根目錄只能由設定檔決定，MCP 參數不可覆寫**（可覆寫等於沒有邊界）；查詢現值用 `sandbox_info` 工具，不要把路徑寫死在文件或記憶裡。

通知監看設定同樣在 `appsettings.json`：`NotificationWatchEnabled`、`NotificationScanSeconds`、`NotificationApps`（監看哪些 App）、`NotificationSenders`／`NotificationMentions`（強訊號）、`NotificationKeywords`（弱訊號）、`NotificationCooldownSeconds`。**規則調整一律改設定檔，不要寫死在程式碼裡**。

`dbconnections.json`（執行期產生，**不進版控**）：SQL 連線設定，密碼欄為 DPAPI 密文。只能透過托盤選單「SQL 連線設定」維護——手動編輯無法產生正確密文。

`webview2-sandbox/`（執行期產生，**不進版控**）：Python 沙盒專屬的 WebView2 user data folder。

## 模組技術文件

目前無獨立技術文件（`_doc/` 已於 2026/07/23 移除）。模組說明以本檔的「架構」段落與 `README.md` 為準；需要時再用 `GenAnalysis` 重新產出並在此建立索引表格。

## 程式碼慣例（此專案內已觀察到的既有風格）

- 異動既有邏輯時，在變更區塊前後加註解標記：`// [日期] - Arlen - [原因] - Start` … `// [日期] - Arlen - [原因] - end`，維持修改歷程可追溯（見 `Program.cs`、`McpHost.cs`、`TrayApplicationContext.cs` 既有範例）。
- 所有需要操作 WinForms UI 或 Outlook COM 的程式碼路徑，一律經 `UiDispatcher`，不要直接跨執行緒呼叫。
- 但 **`UiDispatcher.Send` 只能包「瞬間完成」的動作**。它會同步佔住 UI 執行緒，而 UI 執行緒上跑著 `AlarmEngine` 的 1 秒 Timer 與 `RecordingController` 的 100ms pump——包住一個會跑數十秒的操作，會讓鬧鐘延遲、錄音掉資料、整個程式看起來當掉。長時間等待請用 `TaskCompletionSource` 在執行緒池上 await，只把「送出請求」與「取消／銷毀」這兩個動作切回 UI 執行緒（見 `Sandbox/PyodideRunner.cs`）。SQL 模組沒踩到是因為查詢通常幾百毫秒，不代表這個模式可以照抄到耗時操作上。
