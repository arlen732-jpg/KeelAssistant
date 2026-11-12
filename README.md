# Keel Assistant

系統匣常駐助理（.NET 8 WinForm），整合九大模組，並開本機 MCP Server 供 Claude（Keel）以工具調用下指令。作者：Arlen。

## 模組

| 模組 | 說明 |
|------|------|
| 🕒 時間服務 | 當下時間/星期/時段 |
| ⏰ 鬧鐘/計時 | 記憶體 Timer + `tasks.json` 持久化（重啟復原），鬧鈴循環指定音檔 |
| 🎙️ 會議錄音 | 麥克風＋系統音雙軌混音（線上會議適用），事後 Whisper 逐字稿 |
| 📅 行事曆 | Outlook COM 讀寫，可把會議同步成鬧鐘 |
| 🗄️ SQL 執行 | MSSQL / PostgreSQL 查詢，帳密以 DPAPI 加密由本程式控管，Claude 只拿得到 SQLConnID |
| 🐍 Python 沙盒 | Pyodide／WebAssembly 隔離執行，無網路、碰不到真實檔案系統，含 numpy / pandas / openpyxl / Pillow |
| 🖥️ SSH 終端 | 以 sshId 連遠端主機，持續 shell（cd／export 會延續），可視終端，每條指令人工確認 |
| 🔔 通知監看 | 輪詢 Windows 通知中心，關鍵聯絡人／被 @ 跳視窗，提到名字只跳氣泡（防漏看 LINE） |
| 📊 Claude 用量 | 移植 ClaudeUsageMonitor（5h/7d 額度、今日 token/成本、逼近上限通知） |

## 執行

```powershell
dotnet build      # 編譯
dotnet run        # 執行（出現在系統匣）
```

- 單一實例（Mutex），避免多開
- 關閉視窗 = 縮回系統匣常駐，右鍵選單「結束」才真正退出
- 開機自動啟動：執行 `install-autostart.ps1`（工作排程器，登入後延遲 30 秒）

## 會議錄音

專為**線上會議**設計：同時擷取麥克風與系統喇叭輸出（WASAPI loopback）並混成單軌 WAV。只錄麥克風的話，Teams/Meet/Zoom 對方的聲音是從喇叭出來的，錄不到。

- 輸出：`{專案路徑}\_Project\Recordings\{yyyyMMdd-HHmm}-{會議名稱}.wav`（16kHz mono，約 115MB/小時，上限 180 分鐘自動停）
- **錄音中托盤圖示變紅**，且 MCP 只能「開啟錄音視窗」，開始／暫停／停止一律由人在畫面上按 —— 錄音無法在無人知情下進行
- 錄音視窗的**兩條音量條**是關鍵：開錄前確認「系統音」那條有跳動，才代表對方的聲音真的錄得到

### 一場會議 = 三件同名檔案

| 檔案 | 內容 | 產生方式 |
|------|------|---------|
| `.wav` | 原始音訊 | 錄音當下 |
| `.md` | **人工筆記**：重點、決議、待辦 | 錄音視窗下半部的筆記框，**自動存檔** |
| `.txt` | **逐字稿**：誰講了什麼的完整流水帳 | 會後 `transcribe_recording` 轉出 |

筆記與逐字稿職責不同、不是冗餘：逐字稿是原料，筆記是判斷。`GenMeetingMinutes` 兩份一起吃，品質遠勝只有逐字稿。

筆記框的設計：

- **自動存檔**：停止打字 1.5 秒後落檔，另在停止錄音／視窗失焦／關閉時強制寫入。不會有「忘記存檔」這回事
- **會前可先打**：還沒按「開始」時打的議程先留記憶體，按下開始拿到檔名後一併寫入
- **Ctrl+T 插入時間戳**：在游標處插入當下錄音時間 `[00:12:34] `。日後筆記寫「[00:23:10] 這裡有爭議」，就能直接跳到逐字稿同一段——這是把筆記與逐字稿黏起來的關鍵

### 逐字稿（Whisper `medium`，事後轉檔）

錄音本身**不做即時辨識**——即時方案（Vosk 無繁中、Whisper 小模型）品質實測都不堪用，索性拿掉，專心把事後這份做好。

一小時會議在純 CPU 上約需 40~60 分鐘，所以 `transcribe_recording` **立即回傳 jobId 不等待**，背景跑完托盤跳通知，不必守著。產出的 `.txt`（帶時間戳）有標點、中英混雜正常、輸出繁體，直接餵 `GenMeetingMinutes`。

模型（約 1.5GB）**不隨程式發佈，也不會自動下載**——按需觸發，兩種方式：

- 請 Claude 呼叫 `download_whisper_model()`，再用 `get_model_status()` 查進度
- 或自己抓：`curl -L -o models\ggml-medium.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin`

模型未安裝時只是無法轉逐字稿，**錄音完全不受影響**。路徑見 `appsettings.json` 的 `TranscriptionModelPath`。

## SQL 執行（MSSQL / PostgreSQL）

讓 Claude 能直接查資料庫，但**帳號密碼永遠不進 Claude 的 context**。Claude 只知道一個代號（SQLConnID），連線資訊由 KeelAssistant 保管。

### 連線設定

托盤右鍵 →「🗄️ SQL 連線」→「連線設定…」。密碼輸入後即以 **DPAPI（CurrentUser scope）** 加密寫入 `dbconnections.json`：

- 密文綁定目前 Windows 帳號，`dbconnections.json` 被複製到別台機器或別的帳號**解不開**
- 明文密碼不落地、不回填畫面（編輯時密碼欄一律空白，留空 = 不變更）
- **`dbconnections.json` 不要進版控**（含主機位址與帳號）

### 三層防護

| 層級 | 機制 | 擋什麼 |
|------|------|--------|
| 1. DB 端 | 唯讀帳號（MSSQL `db_datareader`／PG `GRANT SELECT`） | **主防線**，DB 引擎強制，繞不過 |
| 2. 程式端 | `SqlGuard` 語句分類器 | Claude 誤產生的破壞性 SQL、多語句、`sp_`/`xp_`、`OPENROWSET`、`DBCC` |
| 3. 交易層 | 唯讀連線包永不 commit 的交易 | 前兩層的漏網之魚 |

第 3 層兩種 DB 做法不同：**PostgreSQL 用 `SET TRANSACTION READ ONLY`，寫入被 DB 當場拒絕**；MSSQL 無等價語法，只能靠不 commit 事後撤銷（IDENTITY 消耗、觸發程序外部副作用撤不回來）——所以**第 1 層的唯讀帳號不可省略**。

`SqlGuard` 會先剝除註解、字串常值與識別字引號再比對關鍵字，因此 `/*x*/DROP` 繞不過，`SELECT 'DROP TABLE'` 也不會誤判；`updated_at` 不會被當成 `UPDATE`。

### 寫入一律人工確認

`sql_exec` 送進 DB 前會彈出視窗顯示完整 SQL，**按下「執行」才真的送出**。視窗置頂、預設焦點在「取消」、且不接受 Enter 直接放行。唯讀連線則直接拒絕，連視窗都不跳。

### 查 schema

沒有專用工具，直接用 `sql_query` 查 `INFORMATION_SCHEMA` 即可（純 SELECT，會通過）：

```sql
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Orders' ORDER BY ORDINAL_POSITION
```

`EXEC sp_help` 這類會被擋（`sp_` 前綴），改用 `INFORMATION_SCHEMA` 查同樣的東西。建議按表查、不要一次撈全庫（`maxRows` 預設 200、上限 1000，且大量欄位會吃掉 context）。

## Python 沙盒（Pyodide / WebAssembly）

讓 Claude 產生的 Python **有地方跑，但跑不壞任何東西**。用的是 Pyodide 314.0.2（CPython 3.14 編譯成 WebAssembly），隨程式打包在 `wwwroot/sandbox/`，**完全離線**。

### 為什麼是 WASM 而不是 Docker

要防的是「LLM 誤產生破壞性程式碼」，不是「有人刻意逃逸」。WASM 對前者是**完備的**——沒有 syscall、沒有真實檔案系統、沒有 socket，`shutil.rmtree(r"C:\Vista")` 這種東西在裡面連 `C:\` 都找不到。而它不必像容器方案那樣背上 Docker Desktop 常駐、映像維護與 WSL2 的記憶體。

實測（沙盒內）：

| 嘗試 | 結果 |
|------|------|
| `os.path.exists(r"C:\Vista\...")` | `False`——根目錄只有 `/input /output /work /tmp /lib /dev /home /proc` |
| `shutil.rmtree(r"C:\Vista")` | `FileNotFoundError`，真實目錄毫髮無傷 |
| `urllib.request.urlopen(...)` / raw socket | `Host is unreachable` |
| `subprocess.run(["cmd", "/c", "dir"])` | `emscripten does not support processes` |

### 可用套件

`numpy 2.4.3`、`pandas 3.0.2`、`openpyxl 3.1.5`、`Pillow 12.2.0`（+ python-dateutil / pytz / six），其餘只有標準庫。Pillow 授權為 MIT-CMU（寬鬆授權）。**沒有網路就沒有 `pip install`**——`pyodide-lock.json` 已裁剪成只含打包的 wheel，用到沒打包的套件會直接失敗，不會卡在連線逾時。

只載入程式碼實際 `import` 到的套件：純標準庫冷啟動約 2 秒，用到 pandas 約 6 秒。

### 檔案進出

沙盒碰不到磁碟，讀寫都由 KeelAssistant 代勞：

- **讀**：`readPaths` 明確列出要讀的檔（可用絕對路徑，免得每次都要先 copy）。檔案以**檔名**掛在沙盒的 `input/` 之下，程式碼一律用 `input/檔名` 存取。
- **寫**：程式寫到 `output/` 之下的檔案，執行結束後取回並存到 `%SandboxRoot%\output`。**同名不覆蓋**，自動加時間戳。
- 執行失敗也會取回 `output/`——寫了三個檔才在第四步炸掉是常態，沒必要連前面的成果一起丟。

### 讀檔的兩道防線

讀取本身不會破壞環境，真正的風險是**檔案內容進入對話後上傳**。所以：

| 機制 | 行為 |
|------|------|
| **黑名單**（`SandboxDenyPatterns`） | `dbconnections.json`、`.credentials.json`、`appsettings*.json`、`*.pfx`、`*.key`、`.ssh\*` 等 **一律拒絕，連視窗都不跳**，刻意不給人為放行的機會 |
| **確認視窗** | 列出完整路徑與大小；沙盒 `input` 以外的檔案標**大紅字**警示。預設焦點在「拒絕」，不接受 Enter 放行 |

黑名單比對**連結解析後的最終目標**，所以 `data.csv → dbconnections.json` 的符號連結一樣擋得住，視窗上也會顯示「↳ 實際指向」。純計算（沒給 `readPaths`）不跳視窗——沙盒已隔離，逐次確認只會養成不看內容就按允許的習慣。

### 資源上限與併發

| 項目 | 值 | 說明 |
|------|-----|------|
| 逾時 | 30 秒 | 硬殺 Worker，無窮迴圈也停得下來 |
| 記憶體 | 1024 MB | 看門狗每 0.5 秒觀測 WebView2 行程群組，超過即中止 |
| stdout/stderr | 各 8000 字 | 超過標記 `truncated`，避免 print 迴圈灌爆 context |
| 併發 | 一次一個 | 多 session／SubAgent 排隊，等超過 60 秒回報忙碌 |

**每次執行都建立全新 Worker、跑完即銷毀**，所以跨次不會殘留變數、虛擬檔案或被 monkey-patch 的模組。代價是每次重新初始化（2~6 秒），換來的是不必擔心上一次的 `df` 汙染這一次。

WebView2 **用時才建、閒置 5 分鐘整組銷毀**（實測可完全回收 350MB，不殘留行程）。它用專屬的 user data folder，與其他 WebView2 分屬不同行程樹，記憶體歸屬才算得清。

> 記憶體上限是外部觀測而非硬性 cap：Pyodide 沒有暴露 WASM 線性記憶體的上限設定，只能從 .NET 端觀測行程記憶體。目的是「不吃光主機記憶體」，精度足夠，但可能超標一兩百 MB 才被抓到。

### 設定（`appsettings.json`）

```jsonc
"SandboxRoot": "C:\\Vista\\_sandbox",   // 刻意與專案目錄平行，不放在專案內
"SandboxTimeoutSeconds": 30,
"SandboxMemoryLimitMb": 1024,
"SandboxMaxFileSizeMb": 50,
"SandboxIdleShutdownMinutes": 5,        // 0 = 用完立即銷毀
"SandboxDenyPatterns": [ "*\\dbconnections.json", "*.key", ... ]
```

路徑可能變動，**用 `sandbox_info` 查現值，不要把路徑寫死**。

## MCP Server（`127.0.0.1:38600/mcp`，無 token）

以 **Streamable HTTP transport** 對外（避開 IIS 佔用的 80/443/8080）。Keel 直接以 MCP 工具調用，不再透過 PowerShell 拼 JSON。常駐狀態活在 WinForm process，故採 HTTP transport 而非 stdio。

### 在 Claude Code 註冊

```bash
claude mcp add --scope user --transport http keel http://127.0.0.1:38600/mcp
```

### 工具清單

**時間**

| 工具 | 參數 | 說明 |
|------|------|------|
| `get_time` | — | 取得當下時間/星期/時段 |

**鬧鐘 / 計時 / 通知**

| 工具 | 參數 | 說明 |
|------|------|------|
| `list_tasks` | — | 列出待命的鬧鐘與計時任務 |
| `set_alarm` | `at`（HH:mm 或完整日期時間）, `label?`, `loop?` | 絕對時間鬧鐘（已過則排明天） |
| `set_timer` | `minutes`（>0）, `label?` | 相對計時 |
| `stop_alarm` | — | 停止響鈴 |
| `cancel_task` | `id` | 取消單一任務 |
| `notify` | `message?` | 立即響鈴一次＋彈窗，提醒 Arlen 查看回覆 |

**會議錄音 / 逐字稿**

| 工具 | 參數 | 說明 |
|------|------|------|
| `open_recorder` | `projectPath` | 開啟錄音視窗（僅開畫面，錄音由人按） |
| `get_recording_status` | — | 查錄音狀態（idle/recording/paused）、已錄長度 |
| `list_recordings` | `projectPath` | 列出錄音檔，含逐字稿與筆記檔的路徑 |
| `transcribe_recording` | `filePath` | 排入逐字稿轉檔，**立即回傳 jobId 不等待** |
| `get_transcription_status` | `jobId?` | 查轉檔進度（queued/running/completed/failed + %） |
| `download_whisper_model` | — | 下載逐字稿模型（約 1.5GB），立即回傳不等待 |
| `get_model_status` | — | 查模型安裝狀態與下載進度 |

**行事曆（Outlook）**

| 工具 | 參數 | 說明 |
|------|------|------|
| `calendar_list` | `start?`, `offsetHours?`（預設 24） | 列出「起始時間 + 區間」內的行事曆事件 |
| `calendar_sync_alarms` | `start?`, `offsetHours?`, `minutesBefore?`（預設 10） | 會議同步成鬧鐘（開始前 N 分鐘響） |
| `calendar_add` | `subject`, `start`, `end?`, `location?`, `body?`, `reminder?` | 新增 Outlook 行程 |

**SQL 執行**

| 工具 | 參數 | 說明 |
|------|------|------|
| `sql_query` | `connId`, `sql`, `maxRows?`（預設 200，上限 1000）, `timeoutSec?`（預設 30） | 執行單一 SELECT，回傳 `columns` + `rows` |
| `sql_exec` | `connId`, `sql`, `timeoutSec?`（預設 60） | 執行寫入語句，**執行前彈窗人工確認**，回傳受影響列數 |

**SSH 終端**

| 工具 | 參數 | 說明 |
|------|------|------|
| `ssh_exec` | `sshId`, `command`, `timeoutSec?`（0＝設定檔預設，上限 1800） | 在持續 shell 執行一條指令，**每條都彈窗人工確認**，回傳輸出與 `cwd`。不可跑互動式程式（vim、top 等） |
| `ssh_status` | `sshId` | 查連線狀態與目前工作目錄，不主動建立連線 |
| `ssh_open` | `sshId` | 建立連線並把終端視窗帶到前景，讓 Arlen 接手打字 |
| `ssh_close` | `sshId` | 中斷連線並關閉終端視窗，shell 累積的目錄與環境變數一併清除 |

> 與 SQL 相同，**沒有列舉連線的工具**，`sshId` 由 Arlen 告知；連線在托盤「SSH 連線設定」維護（`sshconnections.json`，不進版控）。

**Python 沙盒**

| 工具 | 參數 | 說明 |
|------|------|------|
| `python_run` | `code`, `readPaths?` | 在 WASM 沙盒執行 Python。`readPaths` 非空時彈窗人工確認；最後一行若是運算式，其 repr 一併回傳 |
| `list_sandbox_files` | `pattern?` | 列出沙盒 `input` 目錄下的檔案，不需確認 |
| `sandbox_info` | — | 查目前路徑、逾時／記憶體上限、可用套件 |

> 與 SQL 模組相反，**沙盒刻意提供 `sandbox_info` 讓 Claude 查路徑**。兩者保護的東西不同：SQL 的 `connId` 洩漏會變相列舉連線、擴大攻擊面；沙盒路徑不是機密，安全性來自「只能存取這個範圍」這道硬邊界，隱藏路徑只會製造摩擦（改了設定還要回頭改文件），沒有防護價值。

**Claude 用量**

| 工具 | 參數 | 說明 |
|------|------|------|
| `get_usage_5h` | — | 查 5 小時（session）額度：已用／剩餘百分比、`severity`、重置時間與剩餘分鐘 |

> 讀的是**托盤輪詢的共享快照**，不另外打官方 usage 端點。該端點有嚴格 429 桶，而 MCP 是 `Stateless`、多個 session 與 SubAgent 共用同一個行程，各自即時抓會撞 429，還會連帶弄壞托盤的用量顯示（那才是主要用途）。代價是資料最舊可達 `PollIntervalSeconds`（預設 300，硬底 180），因此回傳一併帶 `snapshotAt` 與 `snapshotAgeSeconds` 揭露陳舊程度。尚未完成首輪或上次取得失敗時回錯誤，不回舊值。

> **沒有列舉連線的工具**——`connId` 必須由 Arlen 主動告知（例：「用 `vista-prod` 查一下訂單表」）。連線清單屬於 Arlen 控管的資訊，錯誤訊息也不會提示可用代號。托盤選單的「🗄️ SQL 連線」子選單可自行查看已設定的 id。

### 使用範例

**午睡**：「30 分鐘後叫我起床」→ `set_timer(30, "起床囉")`

**會議錄音**：「開錄音」→ `open_recorder(專案路徑)`，人按開始，開完會按停止。
之後（可能隔了幾小時）「把今天那場會議轉逐字稿」
→ `list_recordings` 找檔 → `transcribe_recording` 排入 → 跑完托盤通知 → 逐字稿餵 `GenMeetingMinutes`。

> 首次使用需先下載模型：「下載 Whisper 模型」→ `download_whisper_model()`，1.5GB 抓完才能轉檔。

**查資料庫**：「用 `vista-prod` 看一下 Orders 最近 10 筆」
→ `sql_query(connId="vista-prod", sql="SELECT TOP 10 * FROM Orders ORDER BY created_at DESC")`

> 第一次用要先在托盤「SQL 連線設定」建好連線，並記得**正式庫請填 DB 端的唯讀帳號**。

**跑 Python**：「幫我算一下這份報表各倉庫的金額小計」
→ `python_run(code="...", readPaths=["D:\\報表\\庫存.xlsx"])` → 彈窗確認 → 結果寫回 `_sandbox\output`

> 純計算（例如換算單位、驗證演算法、產生測試資料）不必給 `readPaths`，不會跳視窗。

## 模組技術文件

各模組的完整規格（架構圖、API 規格、驗證規則、測試案例、常見問題排查）在 `_Docs/`：

| 文件 | 涵蓋 |
|------|------|
| [MCP主機與工具](_Docs/MCP主機與工具.md) | MCP 主機、全部工具、托盤組裝根 |
| [鬧鐘排程](_Docs/鬧鐘排程.md) | 鬧鐘／計時引擎、Outlook 行事曆 |
| [SQL執行](_Docs/SQL執行.md) | SQL 連線、`SqlGuard`、寫入確認 |
| [SSH終端](_Docs/SSH終端.md) | SSH 持續 shell、終端視窗、指令確認 |
| [Python沙盒](_Docs/Python沙盒.md) | Pyodide 執行引擎、檔案邊界 |
| [用量監控](_Docs/用量監控.md) | 額度輪詢快照、token 成本估算 |

會議錄音與通知監看尚未建立技術文件。
