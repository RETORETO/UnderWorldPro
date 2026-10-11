# -*- coding: utf-8 -*-
"""
Web 导出产物本地预览服务器
=========================

为什么必须用它，而不能双击 index.html？
--------------------------------------
本工程的 Web 预设开启了线程（export_presets.cfg 里 variant/thread_support=true），
Godot 的 Web 运行时依赖 SharedArrayBuffer。浏览器只在页面处于「跨源隔离」
（Cross-Origin Isolation）状态时才允许 SharedArrayBuffer，而进入该状态的唯一途径是
服务端返回下面两个响应头：

    Cross-Origin-Opener-Policy: same-origin
    Cross-Origin-Embedder-Policy: require-corp

file:// 协议下没有 HTTP 响应头，浏览器**永远**无法满足条件，于是报：
    Cross-Origin Isolation / SharedArrayBuffer missing
这不是 bug，是浏览器的安全机制，任何 Godot 线程版 Web 导出都一样。

用法
----
    python Web预览服务器.py                          # 预览 ./build/web，端口 8080
    python Web预览服务器.py <目录> <端口>             # 位置参数（兼容旧写法）
    python Web预览服务器.py --dir build/web --port 8080 --open

    --open   绑定成功后自动用默认浏览器打开（端口被占用会自动顺延，不会打开错地址）

停止：窗口里按 Ctrl+C。
"""
import argparse
import http.server
import mimetypes
import os
import socketserver
import sys
import threading
import webbrowser

mimetypes.add_type("application/wasm", ".wasm")
mimetypes.add_type("text/javascript", ".js")
mimetypes.add_type("application/octet-stream", ".pck")

ROOT = os.path.dirname(os.path.abspath(__file__))


def parse_args(argv):
    # 兼容 `python Web预览服务器.py <目录> <端口>` 的旧写法
    positional = [a for a in argv if not a.startswith("-")]
    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument("dirs", nargs="*", default=[])
    parser.add_argument("--dir", dest="directory", default=None)
    parser.add_argument("--port", dest="port", type=int, default=None)
    parser.add_argument("--open", dest="open_browser", action="store_true")
    args = parser.parse_args(argv)

    directory = args.directory
    port = args.port
    if positional:
        if len(positional) >= 2 and positional[1].isdigit():
            directory = directory or positional[0]
            port = port or int(positional[1])
        elif len(positional) == 1:
            if positional[0].isdigit():
                port = port or int(positional[0])
            else:
                directory = directory or positional[0]

    directory = directory or os.path.join(ROOT, "build", "web")
    return os.path.abspath(directory), port if port else 8080, args.open_browser


class ThreadingTCPServer(socketserver.ThreadingMixIn, socketserver.TCPServer):
    daemon_threads = True
    allow_reuse_address = True


def make_handler(directory):
    class Handler(http.server.SimpleHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def translate_path(self, path):
            return super().translate_path(path)

        def __init__(self, *a, **kw):
            super().__init__(*a, directory=directory, **kw)

        def end_headers(self):
            # 跨源隔离三件套：缺一不可，否则 SharedArrayBuffer 不可用
            self.send_header("Cross-Origin-Opener-Policy", "same-origin")
            self.send_header("Cross-Origin-Embedder-Policy", "require-corp")
            self.send_header("Cross-Origin-Resource-Policy", "same-origin")
            self.send_header("Cache-Control", "no-store, must-revalidate")
            super().end_headers()

        def log_message(self, fmt, *args):
            pass

    return Handler


def main():
    argv = sys.argv[1:]
    directory, port, open_browser = parse_args(argv)

    if not os.path.isdir(directory):
        print(f"[错误] 目录不存在：{directory}")
        print("        先执行一次 Web 导出，或用 --dir 指定导出目录。")
        sys.exit(1)

    if not os.path.isfile(os.path.join(directory, "index.html")):
        print(f"[警告] 目录里没有 index.html：{directory}")
        print("        这里可能不是 Web 导出目录，仍继续启动服务。")

    handler = make_handler(directory)
    httpd = None
    actual_port = port
    for offset in range(0, 21):
        try:
            httpd = ThreadingTCPServer(("127.0.0.1", port + offset), handler)
            actual_port = port + offset
            break
        except OSError:
            continue

    if httpd is None:
        print(f"[错误] 端口 {port}~{port + 20} 都被占用，请用 --port 换一个。")
        sys.exit(1)

    if actual_port != port:
        print(f"[提示] 端口 {port} 已被占用，自动改用 {actual_port}。")

    url = f"http://127.0.0.1:{actual_port}/index.html"
    print("=" * 58)
    print(f"  预览地址： {url}")
    print(f"  服务目录： {directory}")
    print("  已发送跨源隔离响应头（COOP/COEP），线程版 Web 导出可直接运行。")
    print("  首次加载需编译 wasm，大包体请耐心等待 30 秒以上。")
    print("  按 Ctrl+C 停止服务。")
    print("=" * 58)

    if open_browser:
        threading.Timer(1.0, lambda: webbrowser.open(url)).start()

    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\n已停止。")
    finally:
        httpd.server_close()


if __name__ == "__main__":
    main()
