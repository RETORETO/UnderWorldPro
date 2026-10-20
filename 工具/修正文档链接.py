#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""按「文件实际位置」重算 文档/ 下 md 里的相对链接。

背景：2026-10 把根目录 39 个文档移进 文档/ 分类子目录，跨目录的 `](./xxx.md)` 会断。
本脚本不依赖"我记得哪几条断了"，而是逐条按目标文件的真实位置重算。

用法：
    python 工具/修正文档链接.py            # 干跑，只打印
    python 工具/修正文档链接.py --apply    # 真改

边界：
  · 只处理目标以 .md/.txt/.png/.jpg/.json 结尾的链接 —— 排除表格里 `[N](1)` 这类假链接
  · 跳过 http:/https:/mailto:/data:/file:/javascript:、以 # 开头的纯锚点、
    以 / 开头的绝对路径、形如 D:\\ D:/ 的 Windows 绝对路径
  · 目标里的 `#锚点` / `?query` 原样保留，只重写路径部分
  · 先按原文查找；找不到再 unquote 百分号编码后查找（兼容 %20 空格）
  · 按文件名建索引；同名多于 1 个 -> 打印 ambiguous 并跳过，绝不猜
  · 保留原 BOM 状态（本仓库 md 多为无 BOM，个别带 BOM）
  · 跳过 ``` 围栏代码块内的内容
"""
import os
import re
import sys
from urllib.parse import unquote

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)            # 工具/ 的上一级 = 工程根
DOC = os.path.join(ROOT, "文档")
EXTS = (".md", ".txt", ".png", ".jpg", ".jpeg", ".json")
SKIP_SCHEMES = ("http:", "https:", "mailto:", "data:", "file:", "javascript:")
SKIP_DIRS = {".git", ".godot", ".vs", ".idea", ".workbuddy", ".codebuddy",
             "packages", "build", "deploy", "logs", "obj", "bin", "__pycache__"}
LINK = re.compile(r"\]\(([^()\s]+)\)")


def build_index():
    idx = {}
    for base, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in files:
            idx.setdefault(f, []).append(os.path.relpath(os.path.join(base, f), ROOT))
    return idx


def load(p):
    raw = open(p, "rb").read()
    return raw.decode("utf-8-sig"), raw[:3] == b"\xef\xbb\xbf"


def save(p, text, bom):
    out = text.encode("utf-8")
    if bom:
        out = b"\xef\xbb\xbf" + out
    open(p, "wb").write(out)


def fix(path, text, idx, report):
    out_dir = os.path.dirname(path)
    lines = text.split("\n")
    in_fence = False

    for i, line in enumerate(lines):
        if line.lstrip().startswith("```"):
            in_fence = not in_fence
            continue
        if in_fence:
            continue

        def repl(m):
            raw = m.group(1)
            low = raw.lower()
            if low.startswith(SKIP_SCHEMES) or raw.startswith(("#", "/")):
                return m.group(0)
            if re.match(r"^[A-Za-z]:[\\/]", raw):
                return m.group(0)
            target, sep, frag = raw.partition("#")
            if not target.lower().endswith(EXTS):
                return m.group(0)
            target = unquote(target)
            if os.path.exists(os.path.join(out_dir, target)):
                report.append(("ok", path, raw))
                return m.group(0)
            cands = idx.get(os.path.basename(target), [])
            if len(cands) != 1:
                report.append(("ambiguous", path, raw))
                return m.group(0)
            new = os.path.relpath(cands[0], out_dir).replace(os.sep, "/")
            if not new.startswith("."):
                new = "./" + new
            report.append(("fix", path, raw + "  ->  " + new))
            return "](" + new + sep + frag + ")"

        lines[i] = LINK.sub(repl, line)
    return "\n".join(lines)


def main():
    apply = "--apply" in sys.argv
    if not os.path.isdir(DOC):
        raise SystemExit("找不到 %s" % DOC)
    idx, report = build_index(), []
    changed = 0
    for base, dirs, files in os.walk(DOC):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in sorted(files):
            if not f.lower().endswith(".md"):
                continue
            p = os.path.join(base, f)
            text, bom = load(p)
            new = fix(p, text, idx, report)
            if new != text:
                changed += 1
                if apply:
                    save(p, new, bom)
    for kind in ("fix", "ambiguous", "ok"):
        for k, p, msg in report:
            if k == kind:
                print("[%-9s] %s  %s" % (k, os.path.relpath(p, ROOT).replace(os.sep, "/"), msg))
    print("\nfix=%d ambiguous=%d ok(已有效)=%d  受影响文件=%d"
          % (sum(1 for k, _, _ in report if k == "fix"),
             sum(1 for k, _, _ in report if k == "ambiguous"),
             sum(1 for k, _, _ in report if k == "ok"),
             changed))
    if not apply:
        print("（干跑，未写盘；确认数字后加 --apply）")


if __name__ == "__main__":
    main()
