// Keel Assistant Python 沙盒 — 執行 Worker
// 作者：Arlen
//
// 每次執行由 index.html 建立一個全新的 Worker 實例，跑完即 terminate。
// 因此本檔不需處理任何「重置狀態」的邏輯——狀態隨 Worker 一起消失。
//
// 隔離來源是 WebAssembly 本身：沒有 syscall、沒有真實檔案系統、沒有 socket。
// 這裡的虛擬檔案系統（Emscripten FS）完全活在記憶體，寫入不會落到磁碟；
// 要落地的檔案由 .NET 端在 Worker 結束後從 /output 取回再寫入白名單目錄。

import { loadPyodide } from './pyodide/pyodide.mjs';

const INDEX_URL = new URL('./pyodide/', import.meta.url).href;

// 輸出上限：避免 print 迴圈把 MCP 回應灌爆（Claude 的 context 有限）
const MAX_STREAM_CHARS = 8000;
// 取回檔案的總量上限，超過即停止收集並標記
const MAX_OUTPUT_BYTES = 200 * 1024 * 1024;

/** 有上限的字串累積器：達上限後只計數不再累積，避免記憶體被輸出本身吃光。 */
class CappedText {
    constructor(limit) {
        this.limit = limit;
        this.parts = [];
        this.length = 0;
        this.truncated = false;
    }

    push(text) {
        const line = text + '\n';
        if (this.length >= this.limit) {
            this.truncated = true;
            return;
        }
        const room = this.limit - this.length;
        if (line.length > room) {
            this.parts.push(line.slice(0, room));
            this.truncated = true;
            this.length = this.limit;
        } else {
            this.parts.push(line);
            this.length += line.length;
        }
    }

    toString() {
        return this.parts.join('');
    }
}

function toBase64(bytes) {
    // 分段轉換：String.fromCharCode 對超大陣列會爆堆疊
    let binary = '';
    const chunk = 0x8000;
    for (let i = 0; i < bytes.length; i += chunk) {
        binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
    }
    return btoa(binary);
}

function fromBase64(text) {
    const binary = atob(text);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes;
}

/** 遞迴收集 /output 下的檔案。目錄結構以相對路徑保留，供 .NET 端還原。 */
function collectOutputs(pyodide, dir, prefix, files, state) {
    for (const name of pyodide.FS.readdir(dir)) {
        if (name === '.' || name === '..') continue;
        const full = `${dir}/${name}`;
        const stat = pyodide.FS.stat(full);
        if (pyodide.FS.isDir(stat.mode)) {
            collectOutputs(pyodide, full, `${prefix}${name}/`, files, state);
            continue;
        }
        if (state.bytes + stat.size > MAX_OUTPUT_BYTES) {
            state.truncated = true;
            continue;
        }
        state.bytes += stat.size;
        files.push({ name: prefix + name, b64: toBase64(pyodide.FS.readFile(full)) });
    }
}

/** 取回 /output 內容。本身不得拋例外——它在錯誤路徑上也會被呼叫。 */
function harvest(pyodide) {
    const files = [];
    const state = { bytes: 0, truncated: false };
    try {
        collectOutputs(pyodide, '/output', '', files, state);
    } catch {
        // 虛擬 FS 已毀損之類的極端情況：回報既有結果即可，不要蓋掉原本的錯誤
    }
    return { files, truncated: state.truncated };
}

self.onmessage = async (e) => {
    const req = e.data;
    const started = performance.now();
    const stdout = new CappedText(MAX_STREAM_CHARS);
    const stderr = new CappedText(MAX_STREAM_CHARS);

    let pyodide;
    try {
        pyodide = await loadPyodide({
            indexURL: INDEX_URL,
            stdout: (line) => stdout.push(line),
            stderr: (line) => stderr.push(line)
        });
    } catch (err) {
        self.postMessage({
            kind: 'result', ok: false,
            error: `Pyodide 初始化失敗：${err && err.message ? err.message : err}`
        });
        return;
    }

    try {
        // 虛擬目錄：input 放呼叫端指定載入的檔案、output 供程式寫出、work 是工作目錄
        pyodide.FS.mkdirTree('/input');
        pyodide.FS.mkdirTree('/output');
        pyodide.FS.mkdirTree('/work');

        for (const file of req.files || []) {
            const bytes = fromBase64(file.b64);
            const path = `/input/${file.name}`;
            const slash = path.lastIndexOf('/');
            if (slash > 0) pyodide.FS.mkdirTree(path.slice(0, slash));
            pyodide.FS.writeFile(path, bytes);
        }

        pyodide.FS.chdir('/work');

        // 工作目錄下開兩個連結，讓 'output/x.csv'、'/output/x.csv'、'input/a.csv' 都指得到同一處。
        // 沒有這層的話，cwd 在 /work 時相對路徑會解析成 /work/output（不存在），
        // 而「寫到 output/ 之下」是工具說明給呼叫端的約定，得讓它照字面就能成立。
        try { pyodide.FS.symlink('/output', '/work/output'); } catch { }
        try { pyodide.FS.symlink('/input', '/work/input'); } catch { }

        // 只載入程式碼實際 import 到的套件。純標準庫的程式碼不必付 pandas 的載入成本；
        // lockfile 已裁剪成只含隨程式打包的 wheel，未打包的套件不會嘗試連外抓取。
        await pyodide.loadPackagesFromImports(req.code);

        const value = await pyodide.runPythonAsync(req.code);

        // 最後一行若是運算式，把它的 repr 一併回傳（等同 REPL 行為，省得每次都要 print）
        let valueText = null;
        if (value !== undefined && value !== null) {
            try { valueText = String(value); } catch { valueText = '<無法轉為字串>'; }
            if (typeof value.destroy === 'function') {
                try { value.destroy(); } catch { }
            }
        }

        const collected = harvest(pyodide);

        self.postMessage({
            kind: 'result',
            ok: true,
            stdout: stdout.toString(),
            stderr: stderr.toString(),
            truncated: stdout.truncated || stderr.truncated,
            value: valueText,
            files: collected.files,
            filesTruncated: collected.truncated,
            elapsedMs: Math.round(performance.now() - started)
        });
    } catch (err) {
        // 失敗也要取回 /output：程式寫了三個檔才在第四步炸掉是常態，
        // 把已產出的成果一起丟掉只會逼使用者重跑一次。
        const collected = harvest(pyodide);

        // Python 例外：err.message 已含完整 traceback，直接透出供 Claude 自我修正
        self.postMessage({
            kind: 'result',
            ok: false,
            stdout: stdout.toString(),
            stderr: stderr.toString(),
            truncated: stdout.truncated || stderr.truncated,
            error: err && err.message ? err.message : String(err),
            files: collected.files,
            filesTruncated: collected.truncated,
            elapsedMs: Math.round(performance.now() - started)
        });
    }
};
