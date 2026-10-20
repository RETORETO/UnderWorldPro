/**
 * Web运行自检.mjs —— 用系统自带 Edge 的无头模式实跑 Web 导出，抓控制台 + 截图
 * ============================================================================
 * 为什么不用 --screenshot 单次启动：
 *   `msedge --headless=new --screenshot=...` 会在页面 load 事件后很快就退出截图，
 *   而 Godot 要下载 56MB wasm + 几百 MB pck 并编译/初始化 mono，根本来不及，
 *   截出来只有启动画面，也拿不到后面的报错。
 * 做法：先起一个带 --remote-debugging-port 的无头 Edge（常驻），
 *       再用 CDP 挂上去，持续收集 console / exception，等够了再截图。
 *
 * 零依赖：只用 Node 18+ 自带的 fetch 与 WebSocket。
 *
 * 用法：
 *   node Web运行自检.mjs [--url http://127.0.0.1:8080/index.html]
 *                        [--port 9222] [--wait 90] [--shot out.png]
 */

const args = process.argv.slice(2);
function arg(name, def) {
  const i = args.indexOf('--' + name);
  return i >= 0 && args[i + 1] ? args[i + 1] : def;
}

const URL_ = arg('url', 'http://127.0.0.1:8080/index.html');
const PORT = parseInt(arg('port', '9222'), 10);
const WAIT_S = parseInt(arg('wait', '90'), 10);
const SHOT = arg('shot', 'web_shot.png');
// 只打印包含该子串的控制台行（仍会统计全部日志/异常）。例：--grep "[PERF]"
const GREP = arg('grep', '');

const CDP = `http://127.0.0.1:${PORT}`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function findPage() {
  for (let i = 0; i < 90; i++) {
    try {
      const res = await fetch(CDP + '/json/list');
      const list = await res.json();
      const pages = list.filter((t) => t.type === 'page' && t.webSocketDebuggerUrl);
      // 优先挑已经在跑目标地址的页面；找不到也没关系 —— 后面 ws.onopen 里会主动 Page.navigate，
      // 所以 about:blank 之类的空白页同样可用（Edge 以 about:blank 启动时就属于这种）。
      // 注意要避开 Edge 自己的内部页面（edge://sync-confirmation-dialog 之类的会连不上或直接报错）。
      const page =
        pages.find((t) => t.url && t.url.includes('127.0.0.1')) ||
        pages.find((t) => t.url && t.url.startsWith('about:')) ||
        pages.find((t) => t.url && !t.url.includes('://')) ||
        pages[0];
      if (page) return page;
    } catch (_) { /* 浏览器还没起来 */ }
    await sleep(1000);
  }
  throw new Error('等不到可调试页面，请确认 Edge 是用 --remote-debugging-port=' + PORT + ' 启动的');
}

const logs = [];
const errors = [];
let firstLogTs = null;

function main() {
  return findPage().then((page) => new Promise((resolve, reject) => {
    const ws = new WebSocket(page.webSocketDebuggerUrl);
    let msgId = 0;
    const pending = new Map();

    const send = (method, params = {}) => new Promise((res) => {
      const id = ++msgId;
      pending.set(id, res);
      ws.send(JSON.stringify({ id, method, params }));
    });

    ws.onmessage = (ev) => {
      let m;
      try { m = JSON.parse(ev.data); } catch (_) { return; }
      if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); return; }

      if (m.method === 'Runtime.consoleAPICalled') {
        const text = (m.params.args || []).map((a) => {
          if (a.value !== undefined) return String(a.value);
          if (a.description) return a.description;
          if (a.unserializableValue) return a.unserializableValue;
          return '<' + a.type + '>';
        }).join(' ');
        // CDP 的 timestamp 是「秒（浮点，相对 epoch）」，换算成相对首条日志的毫秒偏移，
        // 这样 [PERF] 各阶段之间隔了多久一眼可见（Godot 只在 GD.Print 时才吐日志，没有自带时间戳）。
        const ts = typeof m.params.timestamp === 'number' ? m.params.timestamp * 1000 : null;
        if (ts !== null && firstLogTs === null) firstLogTs = ts;
        logs.push({
          level: m.params.type,
          text,
          dt: ts !== null && firstLogTs !== null ? Math.round(ts - firstLogTs) : null,
        });
      } else if (m.method === 'Runtime.exceptionThrown') {
        const d = m.params.exceptionDetails || {};
        errors.push(d.exception && d.exception.description ? d.exception.description : (d.text || '未知异常'));
      } else if (m.method === 'Log.entryAdded') {
        const e = m.params.entry;
        logs.push({ level: e.level, text: `[${e.source}] ${e.text}` });
      }
    };

    ws.onerror = (e) => reject(new Error('WebSocket 出错: ' + (e.message || e.type)));

    ws.onopen = async () => {
      await send('Runtime.enable');
      await send('Log.enable');
      await send('Page.enable');
      // 【必须禁缓存】pck 有 250MB+、wasm 50MB+，浏览器很容易把上一次构建缓存下来。
      // 只做 Page.navigate 不保证绕过 HTTP 缓存，所以从 CDP 层直接关掉缓存，
      // 确保测到的一定是磁盘上最新的导出产物。
      try {
        await send('Network.enable');
        await send('Network.setCacheDisabled', { cacheDisabled: true });
      } catch (e) {
        console.log('  (提示) 无法关闭浏览器缓存: ' + (e && e.message ? e.message : e));
      }

      // 主动重新加载：确保测的是磁盘上最新的导出产物，而不是浏览器里缓存的旧包
      // （pck 有 250MB+，很容易被浏览器缓存，不重载会验到上一次构建）
      await send('Page.navigate', { url: URL_ });
      console.log(`已挂上 CDP 并重新加载 ${URL_}，持续采集 ${WAIT_S} 秒 …`);
      const step = 10;
      for (let t = 0; t < WAIT_S; t += step) {
        await sleep(step * 1000);
        const st = await send('Runtime.evaluate', {
          expression: `(() => {
            const c = document.querySelector('canvas');
            return JSON.stringify({
              title: document.title,
              hasCanvas: !!c,
              canvasW: c ? c.width : 0,
              canvasH: c ? c.height : 0,
              status: (document.getElementById('status') || {}).textContent || '',
              progress: (() => { const p = document.getElementById('progress'); return p ? p.style.width : ''; })()
            });
          })()`,
          returnByValue: true,
        });
        let info = {};
        try { info = JSON.parse(st.result.result.value); } catch (_) {}
        console.log(`  [${t + step}s] title=${info.title || '-'} canvas=${info.canvasW}x${info.canvasH} status="${info.status || ''}" progress=${info.progress || '-'}  logs=${logs.length} errs=${errors.length}`);
      }

      const shot = await send('Page.captureScreenshot', { format: 'png' });
      if (shot.result && shot.result.data) {
        const fs = await import('node:fs');
        fs.writeFileSync(SHOT, Buffer.from(shot.result.data, 'base64'));
        console.log(`\n截图已保存: ${SHOT}`);
      }

      console.log('\n================ 控制台输出 ================');
      const shown = GREP ? logs.filter((l) => l.text.includes(GREP)) : logs;
      if (!shown.length) console.log(GREP ? `(没有匹配 "${GREP}" 的行；共 ${logs.length} 条日志)` : '(无)');
      for (const l of shown) {
        const t = l.dt === null ? '' : `+${String(l.dt).padStart(6)}ms `;
        console.log(`${t}[${l.level}] ${l.text}`);
      }

      console.log('\n================ 未捕获异常 ================');
      if (!errors.length) console.log('(无)');
      for (const e of errors) console.log(e);

      ws.close();
      resolve();
    };
  }));
}

main().then(() => process.exit(0)).catch((e) => { console.error('自检失败: ' + e.message); process.exit(1); });
