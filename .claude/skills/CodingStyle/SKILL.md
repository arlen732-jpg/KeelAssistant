---
name: CodingStyle
description: >
  KeelAssistant 的 Coding Style 規範。
  在此專案中撰寫或修改程式碼時自動套用，確保風格一致。
  涵蓋 .NET 8 WinForms + 內嵌 ASP.NET Core (MCP Server) 的命名、格式化、
  錯誤處理、JSON 持久化、COM Interop、變更追蹤標記等慣例。
---

# KeelAssistant Coding Style Guide

單一 .csproj 專案（無 .sln），因此僅有一個子專案目錄。

## 子專案參考

修改程式碼時，先讀 `通用.md`，再依功能類型讀對應檔案：

- **KeelAssistant** (.NET 8, WinForms + ASP.NET Core Minimal Hosting, MCP Server)：[KeelAssistant/通用.md](KeelAssistant/通用.md)
  - [MCP工具方法.md](KeelAssistant/MCP工具方法.md) — 新增/修改 `[McpServerTool]` 方法時讀
  - [JSON持久化模組.md](KeelAssistant/JSON持久化模組.md) — 新增/修改 Load/Save 型狀態儲存（如 tasks.json、memory.json）時讀
  - [彈出視窗.md](KeelAssistant/彈出視窗.md) — 新增/修改無 Designer 的程式碼建構 Form 時讀

## 全專案共通規範

以下為跨功能類型一致遵守的規則，細節與範例見 `KeelAssistant/通用.md`：

- **變更追蹤標記**：修改既有邏輯時，前後加註解 `// [日期] - [作者] - [原因] - Start` … `// [日期] - [作者] - [原因] - end`（SQL 用 `--`）。
- **命名**：類別/方法/屬性 PascalCase，私有欄位 `_camelCase`，區域變數 camelCase；WinForms 事件處理器不使用 `btnXxx_Click`（本專案改用 lambda inline）。
- **錯誤處理**：對外拋例外一律用 `ArgumentException`（參數缺漏/格式錯誤）或 `InvalidOperationException`（狀態錯誤），訊息一律繁體中文；背景/資源釋放路徑用空 `catch { }` 靜默吞錯，不影響主流程。
- **JSON 序列化**：一律 `System.Text.Json`，`Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping` 讓中文直出，不用 `\uXXXX`。
- **XML Doc 註解**：public 類別/方法一律加 `<summary>`，說明「做什麼＋為什麼這樣設計」，非重複參數型別。
- **執行緒切換**：任何觸及 WinForms 控件或 Outlook COM 的呼叫，一律經 `UiDispatcher.Send`/`Send<T>`，禁止直接跨執行緒操作。
