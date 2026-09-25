"""Launch a Chrome instance under CDP control, with a WebHID tap injected.

Why this approach: the AutoGLM browser skill cannot capture network or HID traffic (its
own documentation rules out DevTools, the Network panel and JS execution), and what we
need is the exact bytes the ATK driver sends to the keyboard. Driving Chrome over CDP
lets us inject a hook before any page script runs, so every HID call the driver makes is
recorded verbatim.

The hook forwards each call to a local HTTP server run by this script, which is more
reliable than reading a JS variable: the data survives page navigation and is written to
disk as it arrives.

A WebHID device grant always requires a user gesture, so after the page loads the user
must click the connect button and pick the keyboard in the browser dialog -- that step
cannot be automated.

Usage:  python tools/capture_atk_hid.py
Output: tools/_atk_hid_log.jsonl
"""
import asyncio
import http.server
import json
import os
import socket
import subprocess
import threading
import time

CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
PROFILE = os.path.join(os.environ["TEMP"], "atk_capture_profile")
LOG = r"C:\code\chat-records\mouse-tray\tools\_atk_hid_log.jsonl"
PORT_HTTP = 8791
PORT_CDP = 9223
TARGET = "https://hub.atk.pro/"

received = []


async def drive(port, hook_js, target):
    """通过 CDP 注入钩子并导航到目标页。

    必须用 Page.addScriptToEvaluateOnNewDocument：它在任何页面脚本之前执行，
    是能拿到原始 HID 字节的前提。页面加载完再注入就已经错过 requestDevice 了。
    """
    import urllib.request

    import websockets

    # 取一个可用的 page target
    for _ in range(40):
        try:
            with urllib.request.urlopen(
                    f"http://127.0.0.1:{port}/json/list", timeout=3) as r:
                targets = json.loads(r.read().decode())
            pages = [t for t in targets if t.get("type") == "page"]
            if pages:
                ws_url = pages[0]["webSocketDebuggerUrl"]
                break
        except Exception:
            pass
        await asyncio.sleep(0.5)
    else:
        raise RuntimeError("找不到可用的 page target")

    async with websockets.connect(ws_url, max_size=64 * 1024 * 1024) as ws:
        mid = 0

        async def send(method, params=None):
            nonlocal mid
            mid += 1
            my = mid
            await ws.send(json.dumps({"id": my, "method": method,
                                      "params": params or {}}))
            while True:
                msg = json.loads(await ws.recv())
                if msg.get("id") == my:
                    return msg

        await send("Page.enable")
        await send("Runtime.enable")

        # 关键一步：钩子在所有页面脚本之前注入
        await send("Page.addScriptToEvaluateOnNewDocument", {"source": hook_js})
        print("已注册 HID 钩子（先于页面脚本执行）")

        await send("Page.navigate", {"url": target})
        print(f"已导航到 {target}")


class Sink(http.server.BaseHTTPRequestHandler):
    """接收钩子发来的每一条 HID 记录。"""

    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(n).decode("utf-8", "replace")
        try:
            rec = json.loads(body)
        except Exception:
            rec = {"raw": body}

        rec["_t"] = time.strftime("%H:%M:%S")
        received.append(rec)
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")

        # 立即打印，便于边看边判断
        kind = rec.get("kind", "?")
        print(f"  [{rec['_t']}] {kind}  {json.dumps(rec, ensure_ascii=False)[:400]}",
              flush=True)

        self.send_response(204)
        # 允许跨域，否则页面上下文的 fetch 会被拦
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Headers", "*")
        self.end_headers()

    def do_OPTIONS(self):
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Headers", "*")
        self.end_headers()

    def log_message(self, *a):
        pass


def start_sink():
    srv = http.server.ThreadingHTTPServer(("127.0.0.1", PORT_HTTP), Sink)
    t = threading.Thread(target=srv.serve_forever, daemon=True)
    t.start()
    return srv


HOOK = r"""
(() => {
  const POST = (o) => {
    try {
      fetch('http://127.0.0.1:%(port)d/log', {
        method: 'POST',
        mode: 'no-cors',
        headers: {'Content-Type': 'text/plain'},
        body: JSON.stringify(o)
      });
    } catch (e) {}
  };
  const hex = (v) => {
    try {
      if (v instanceof DataView) v = new Uint8Array(v.buffer, v.byteOffset, v.byteLength);
      if (v instanceof Uint8Array) return Array.from(v).map(b => b.toString(16).padStart(2,'0')).join(' ');
      return String(v);
    } catch (e) { return '?'; }
  };

  if (!navigator.hid) { POST({kind:'no-webhid'}); return; }

  const origRequest = navigator.hid.requestDevice.bind(navigator.hid);
  navigator.hid.requestDevice = async function (opts) {
    POST({kind:'requestDevice', filters: opts && opts.filters});
    const devs = await origRequest(opts);
    POST({kind:'granted', devices: devs.map(d => ({
      vendorId: d.vendorId, productId: d.productId,
      productName: d.productName, collections: d.collections
    }))});
    devs.forEach(tap);
    return devs;
  };

  const origGet = navigator.hid.getDevices.bind(navigator.hid);
  navigator.hid.getDevices = async function () {
    const devs = await origGet();
    POST({kind:'getDevices', count: devs.length,
          devices: devs.map(d => ({vendorId:d.vendorId, productId:d.productId, productName:d.productName}))});
    devs.forEach(tap);
    return devs;
  };

  const tapped = new WeakSet();
  function tap(dev) {
    if (!dev || tapped.has(dev)) return;
    tapped.add(dev);
    POST({kind:'tap', vendorId:dev.vendorId, productId:dev.productId,
          productName:dev.productName,
          collections: dev.collections});

    const origOpen = dev.open.bind(dev);
    dev.open = async function () {
      POST({kind:'open', vendorId:dev.vendorId});
      const r = await origOpen();
      POST({kind:'opened', vendorId:dev.vendorId});
      return r;
    };

    const origClose = dev.close.bind(dev);
    dev.close = async function () {
      POST({kind:'close'});
      return origClose();
    };

    const origSend = dev.sendReport.bind(dev);
    dev.sendReport = async function (id, data) {
      POST({kind:'sendReport', reportId:id, len:(data?data.byteLength:0), data:hex(data)});
      const r = await origSend(id, data);
      POST({kind:'sendReport-ok', reportId:id});
      return r;
    };

    const origSendF = dev.sendFeatureReport.bind(dev);
    dev.sendFeatureReport = async function (id, data) {
      POST({kind:'sendFeatureReport', reportId:id, len:(data?data.byteLength:0), data:hex(data)});
      const r = await origSendF(id, data);
      POST({kind:'sendFeatureReport-ok', reportId:id});
      return r;
    };

    const origRecvF = dev.receiveFeatureReport.bind(dev);
    dev.receiveFeatureReport = async function (id) {
      const r = await origRecvF(id);
      POST({kind:'receiveFeatureReport', reportId:id, data:hex(r)});
      return r;
    };

    dev.addEventListener('inputreport', (e) => {
      POST({kind:'inputreport', reportId:e.reportId, data:hex(e.data)});
    });
  }

  // 已在页面上注册过的设备也补挂
  navigator.hid.getDevices().then(d => d.forEach(tap)).catch(()=>{});
  POST({kind:'hook-installed'});
})();
""" % {"port": PORT_HTTP}


def main():
    open(LOG, "w", encoding="utf-8").close()
    srv = start_sink()
    print(f"日志写入: {LOG}")
    print(f"本地接收端口: {PORT_HTTP}")

    # 全新 profile，避免与用户日常浏览器互相干扰
    if os.path.exists(PROFILE):
        subprocess.run(["cmd", "/c", "rmdir", "/s", "/q", PROFILE],
                       capture_output=True)
    os.makedirs(PROFILE, exist_ok=True)

    args = [
        CHROME,
        f"--remote-debugging-port={PORT_CDP}",
        f"--user-data-dir={PROFILE}",
        "--no-first-run",
        "--no-default-browser-check",
        # hub.atk.pro 的 DNS 落到保留段，必须走本地代理
        "--proxy-server=http://127.0.0.1:7890",
        "--proxy-bypass-list=127.0.0.1;localhost",
        "--new-window",
        "about:blank",
    ]
    print("启动 Chrome（独立 profile，走本地代理）…")
    proc = subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    # 等 CDP 端口就绪
    deadline = time.time() + 40
    while time.time() < deadline:
        try:
            with socket.create_connection(("127.0.0.1", PORT_CDP), timeout=1):
                break
        except OSError:
            time.sleep(0.5)
    else:
        print("CDP 端口未就绪")
        return 1

    print(f"CDP 就绪 (127.0.0.1:{PORT_CDP})")

    # 用 CDP 注入钩子并导航。
    # addScriptToEvaluateOnNewDocument 保证钩子在页面任何脚本之前执行，
    # 这是能拿到原始字节的前提 —— 页面加载后再注入就已经晚了。
    try:
        asyncio.run(drive(PORT_CDP, HOOK, TARGET))
    except Exception as e:
        print(f"CDP 驱动出错: {e}")

    print()
    print("=" * 70)
    print("  浏览器已打开 hub.atk.pro。请在窗口里：")
    print("    1. 等页面加载完成")
    print("    2. 点击连接设备 / 绑定设备")
    print("    3. 在浏览器弹窗里选择 ATK Z87 键盘")
    print("  钩子会自动记录全部 HID 收发。")
    print("  完成后回到这里按 Ctrl+C 结束。")
    print("=" * 70)
    print()

    try:
        while proc.poll() is None:
            time.sleep(1)
    except KeyboardInterrupt:
        print("\n收到中断，关闭 Chrome")
    finally:
        try:
            proc.terminate()
        except Exception:
            pass
        srv.shutdown()

    print(f"\n共记录 {len(received)} 条")
    print(f"日志: {LOG}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
