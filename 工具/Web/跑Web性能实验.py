#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
跑Web性能实验.py  --  自动切换 index.html 里的实验开关、跑无头浏览器、抓 [PERF] 日志并存档
================================================================================

配合 `Script/Web/WebPerfConfig.cs`（识别 `--wp-*` 命令行参数）与 `设置Web实验参数.py` 使用。
它的价值在于：**一次导出，跑完所有对照组**。

前置条件
--------
1. 已导出：`build/web/` 里要有 index.html / index.js / index.wasm / index.pck
2. 预览服务在跑（**必须**是带 COOP/COEP 的那个）：
       python 工具/Web/Web预览服务器.py --dir build/web --port 8080
3. 无头浏览器在跑（带远程调试端口）：
       msedge --headless=new --remote-debugging-port=9222 --user-data-dir=<dir> \
              --enable-unsafe-swiftshader --window-size=1280,720 about:blank

用法
----
    python 跑Web性能实验.py --name base   --args perf auto=deck
    python 跑Web性能实验.py --name e1_p8  --args perf auto=deck pagesize=8
    python 跑Web性能实验.py --name e2_notex --args perf auto=deck notex
    python 跑Web性能实验.py --name fix_prog --args perf auto=deck prog
    python 跑Web性能实验.py --list                # 列出预设
    python 跑Web性能实验.py --all                 # 依次跑完全部预设

参数写法：`key` 或 `key=value`，会自动加上 `--wp-` 前缀。
结果保存在 `logs/perf_<name>.txt`，并在屏幕上只打印 `[PERF]` 行，方便横向对比。
"""

import argparse
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
# 工具\Web\ -> 工具\ -> 工程根
ROOT = os.path.dirname(os.path.dirname(HERE))


def _find_node():
    """定位 node：先看 NODE 环境变量，再查 PATH，最后退回裸命令名。"""
    env = os.environ.get("NODE")
    if env and os.path.isfile(env):
        return env
    found = shutil.which("node")
    if found:
        return found
    return "node"


NODE = _find_node()
SELFCHECK = os.path.join(HERE, "Web运行自检.mjs")
SETTER = os.path.join(HERE, "设置Web实验参数.py")
HTML = os.path.join(ROOT, "build", "web", "index.html")
LOGDIR = os.path.join(ROOT, "logs")

# 预设实验组：name -> (args, 说明)
PRESETS = [
    ("base",     (["perf", "auto=deck"],                       "基线：5 个 Label 全 WordSmart、PageSize=40、同步加载卡图")),
    ("e1_p8",    (["perf", "auto=deck", "pagesize=8"],         "E1：单页行数 40 -> 8")),
    ("e2_notex", (["perf", "auto=deck", "notex"],              "E2：完全不加载卡图")),
    ("fix_prog", (["perf", "auto=deck", "prog"],               "修复方案：卡图渐进填充")),
    ("fix_p12",  (["perf", "auto=deck", "pagesize=12", "prog"],"修复方案：PageSize=12 + 渐进填充")),
]


def set_args(args):
    cmd = [sys.executable, SETTER, "--clear"]
    # 先把每个 key / key=value 拆出来映射到 setter 的参数
    flags = [a for a in args if "=" not in a]
    kv = [a for a in args if "=" in a]
    subprocess.run(cmd, check=True, capture_output=True)
    cmd = [sys.executable, SETTER]
    for f in flags:
        cmd.append("--" + f)
    for item in kv:
        k, v = item.split("=", 1)
        cmd += ["--" + k, v]
    subprocess.run(cmd, check=True)


def run(name, args, wait):
    print("=" * 78)
    print("实验 %-10s args=%s" % (name, " ".join(args) or "(无)"))
    print("=" * 78)
    set_args(args)

    os.makedirs(LOGDIR, exist_ok=True)
    out = os.path.join(LOGDIR, "perf_%s.txt" % name)
    shot = os.path.join(LOGDIR, "perf_%s.png" % name)

    cmd = [NODE, SELFCHECK, "--url", "http://127.0.0.1:8080/index.html",
           "--wait", str(wait), "--shot", shot]
    with open(out, "w", encoding="utf-8") as fp:
        subprocess.run(cmd, stdout=fp, stderr=subprocess.STDOUT)

    text = open(out, encoding="utf-8", errors="replace").read()
    perf = [l for l in text.splitlines() if "[PERF]" in l or "[WebPerf]" in l or "[WebFont" in l]
    if perf:
        for l in perf:
            print("  " + l.strip())
    else:
        print("  (没有抓到 [PERF] 行 —— 看完整日志：%s)" % out)
        tail = [l for l in text.splitlines() if l.strip()][-12:]
        for l in tail:
            print("  | " + l[:180])
    errs = text.split("未捕获异常")
    if len(errs) > 1:
        body = "\n".join(errs[1].splitlines()[1:6]).strip()
        print("  未捕获异常: " + (body if body and body != "(无)" else "无"))
    print("  完整日志: %s" % out)
    print()
    return out


def main():
    ap = argparse.ArgumentParser(description="跑 Web 性能对照实验并抓 [PERF] 日志")
    ap.add_argument("--name", help="实验名（存档名）")
    ap.add_argument("--args", nargs="*", default=[], help="开关，如 perf auto=deck pagesize=8")
    ap.add_argument("--wait", type=int, default=150, help="每组采集秒数（默认 150）")
    ap.add_argument("--list", action="store_true", help="列出预设实验")
    ap.add_argument("--all", action="store_true", help="依次跑完所有预设")
    a = ap.parse_args()

    if a.list:
        print("预设实验：")
        for n, (args, desc) in PRESETS:
            print("  %-10s %-42s %s" % (n, " ".join(args), desc))
        return

    if not os.path.isfile(HTML):
        sys.exit("× 找不到 %s（先跑一次 导出Web版.ps1）" % HTML)
    if not os.path.isfile(SELFCHECK):
        sys.exit("× 找不到 %s" % SELFCHECK)

    if a.all:
        for n, (args, desc) in PRESETS:
            run(n, args, a.wait)
        return

    if not a.name:
        sys.exit("× 需要 --name（或用 --list / --all）")
    run(a.name, a.args, a.wait)


if __name__ == "__main__":
    main()
