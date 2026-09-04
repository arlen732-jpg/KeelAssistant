// Keel SSH 終端 — xterm.js 與 .NET 端的橋接
// 作者：Arlen
//
// 訊息協定（雙向，皆為 JSON 字串）：
//   .NET → JS   { kind: "data",  b64 }   遠端 PTY 輸出，寫進終端
//   JS   → .NET { kind: "ready" }        xterm 已建立，可以開始收資料
//   JS   → .NET { kind: "input", b64 }   使用者按鍵，送往遠端
//
// 一律用 Base64 而非直接傳字串：終端資料是原始位元組流，會出現控制字元與
// 跨 chunk 切斷的 UTF-8 序列，交給 JSON 字串處理必然損毀。解碼後直接餵給
// xterm 的 write(Uint8Array)，由它自己處理不完整的多位元組字元。

(function () {
  'use strict';

  const params = new URLSearchParams(location.search);
  const cols = parseInt(params.get('cols'), 10) || 120;
  const rows = parseInt(params.get('rows'), 10) || 32;
  const fontSize = parseInt(params.get('font'), 10) || 14;

  const term = new Terminal({
    cols: cols,
    rows: rows,
    fontSize: fontSize,
    fontFamily: 'Cascadia Mono, Consolas, "Microsoft JhengHei UI", monospace',
    cursorBlink: true,
    // 捲動緩衝開大一點：apt / npm 的輸出動輒上千行，回頭查證用得到
    scrollback: 10000,
    theme: {
      background: '#1e1e1e',
      foreground: '#d4d4d4',
      cursor: '#d4d4d4'
    }
  });

  term.open(document.getElementById('term'));
  term.focus();

  function post(msg) {
    try {
      window.chrome.webview.postMessage(JSON.stringify(msg));
    } catch (e) {
      // WebView2 尚未就緒或已銷毀，丟棄即可
    }
  }

  // 使用者按鍵 → 送往遠端。這條路徑讓 Arlen 能在哨符機制失效
  //（nano、apt 設定檔對話框等互動式程式）時直接接手操作。
  term.onData(function (data) {
    const bytes = new TextEncoder().encode(data);
    post({ kind: 'input', b64: bytesToB64(bytes) });
  });

  window.chrome.webview.addEventListener('message', function (e) {
    let msg;
    try {
      msg = JSON.parse(e.data);
    } catch (err) {
      return;
    }
    if (msg.kind !== 'data' || !msg.b64) return;
    term.write(b64ToBytes(msg.b64));
  });

  function b64ToBytes(b64) {
    const bin = atob(b64);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
  }

  function bytesToB64(bytes) {
    let bin = '';
    for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
    return btoa(bin);
  }

  post({ kind: 'ready' });
})();
