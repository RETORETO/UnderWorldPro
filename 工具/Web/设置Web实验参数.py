#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
设置Web实验参数.py  --  编辑导出产物 index.html 里的 GODOT_CONFIG.args，从而在不重新导出的前提下切换实验开关
================================================================================

背景
----
Web 导出的 `build/web/index.html` 里有这么一行（导出时由引擎写入）：

    const GODOT_CONFIG = {"args":[],"canvasResizePolicy":2,...};

其中 `args` 数组就是**传给引擎的命令行参数**。它是纯文本、就在导出目录里，
所以改实验参数只需要改这个数组 + 刷新页面，**不必重新导出**（一次导出约 10 分钟）。

本项目里读取它的代码是 `Script/Web/WebPerfParams.cs`（只认 `--wp-` 前缀的参数）。
注意：它是**纯静态类**、由场景脚本按需惰性解析，不是 autoload（原因见该文件顶部注释）。

用法
----
    # 查看当前参数
    python 设置Web实验参数.py --show

    # 基线：开明细日志 + 自动进卡组编辑
    python 设置Web实验参数.py --perf --auto deck

    # E1：单页行数 40 -> 8
    python 设置Web实验参数.py --perf --auto deck --pagesize 8

    # E2：完全不加载卡图
    python 设置Web实验参数.py --perf --auto deck --notex

    # 注意：没有「关闭字体回退」的开关（启动期在 Web 上读开关会导致原生层 abort）。
    #       若要验证字形栅格化是否为瓶颈，只能临时改 WebFontFallback.cs 后重导出。

    # 试试修复方案：渐进填充
    python 设置Web实验参数.py --perf --auto deck --prog

    # 清空（回到完全无开关的默认行为）
    python 设置Web实验参数.py --clear

每次执行都会打印改动前后的 args，并在文件里同步更新 `fileSizes` 之外的其它内容（其余字节原样保留）。
"""

import argparse
import os
import re
import sys

DEFAULT_HTML = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "build", "web", "index.html"
)

# 与 WebPerfParams.cs 的开关一一对应：长名称 -> 命令行写法
# 说明：**没有 nofont**。字体回退必须无条件执行 —— 启动期（autoload）里读性能开关
# 在 Web 上会触发原生层 abort，所以字体那侧刻意不参与开关机制。
FLAG_ARGS = {
    "perf": "--wp-perf",
    "probe": "--wp-probe",
    "notex": "--wp-notex",
    "prog": "--wp-prog",
    "sync": "--wp-sync",
}


def read(path):
    with open(path, "r", encoding="utf-8") as fp:
        return fp.read()


def write(path, text):
    # 保持与 Godot 导出一致的换行风格：原文件是什么就用什么
    with open(path, "w", encoding="utf-8", newline="") as fp:
        fp.write(text)


ARGS_RE = re.compile(r'("args"\s*:\s*)\[[^\]]*\]')


def get_args(text):
    m = ARGS_RE.search(text)
    if not m:
        sys.exit("× 在 index.html 里找不到 \"args\":[...]（文件格式可能已变）")
    body = m.group(0)
    inner = body[body.index("[") + 1 : body.rindex("]")]
    out = []
    for raw in inner.split(","):
        raw = raw.strip()
        if not raw:
            continue
        if (raw.startswith('"') and raw.endswith('"')) or (
            raw.startswith("'") and raw.endswith("'")
        ):
            raw = raw[1:-1]
        out.append(raw)
    return out


def set_args(text, args):
    payload = "[" + ", ".join('"%s"' % a.replace('"', '\\"') for a in args) + "]"
    new, n = ARGS_RE.subn(lambda m: m.group(1) + payload, text, count=1)
    if n != 1:
        sys.exit("× 替换 args 失败")
    return new


def main():
    ap = argparse.ArgumentParser(
        description="改 build/web/index.html 里的 GODOT_CONFIG.args，免重导切换实验开关",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument("--html", default=DEFAULT_HTML, help="index.html 路径")
    ap.add_argument("--show", action="store_true", help="只查看当前参数")
    ap.add_argument("--clear", action="store_true", help="清空所有参数")
    for name in FLAG_ARGS:
        ap.add_argument("--" + name, action="store_true", help="打开 --wp-%s" % name)
    ap.add_argument("--pagesize", type=int, help="卡池单页行数（覆盖默认 12）")
    ap.add_argument("--auto", metavar="TARGET", help='自动进入的界面，目前支持 deck')
    ap.add_argument("--autodelay", type=float, help="自动进入前的等待秒数（默认 1.2）")
    a = ap.parse_args()

    if not os.path.isfile(a.html):
        sys.exit("× 找不到 %s\n  （先跑一次 导出Web版.ps1）" % a.html)

    text = read(a.html)
    before = get_args(text)

    print("index.html : %s" % a.html)
    print("当前 args  : %s" % (before if before else "(空)"))

    if a.show:
        return

    if a.clear:
        args = []
    else:
        args = list(before)
        # 幂等：先摘掉本项目会写入的参数，其它来源的一律保留
        def is_ours(x):
            return x.startswith("--wp-")

        args = [x for x in args if not is_ours(x)]

        for name, flag in FLAG_ARGS.items():
            if getattr(a, name):
                args.append(flag)
        if a.pagesize is not None:
            args.append("--wp-pagesize=%d" % a.pagesize)
        if a.auto:
            args.append("--wp-auto=%s" % a.auto)
        if a.autodelay is not None:
            args.append("--wp-autodelay=%s" % a.autodelay)

    write(a.html, set_args(text, args))
    print("写入后 args: %s" % (args if args else "(空)"))
    print()
    print("现在刷新浏览器（或跑一回 Web运行自检.mjs）即可生效，无需重新导出。")


if __name__ == "__main__":
    main()
