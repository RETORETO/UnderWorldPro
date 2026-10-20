#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把本工程里的 .ps1 统一规范为「UTF-8 带 BOM + CRLF」。

为什么必须带 BOM：PowerShell 5.1 读 .ps1 时，若文件没有 BOM 就按系统 ANSI 代码页解码，
脚本里的中文注释会被解成乱码，进而导致「语法错误」这类看似莫名其妙的解析失败
（本项目已踩过一次：导出Web版.ps1 缺 BOM 直接无法运行）。
"""
import glob
import os

HERE = os.path.dirname(os.path.abspath(__file__))


def normalize(path):
    with open(path, "rb") as fp:
        raw = fp.read()
    had_bom = raw[:3] == b"\xef\xbb\xbf"
    text = raw.decode("utf-8-sig")
    text = text.replace("\r\n", "\n").replace("\r", "\n").replace("\n", "\r\n")
    out = text.encode("utf-8")
    if had_bom:
        out = b"\xef\xbb\xbf" + out
    with open(path, "wb") as fp:
        fp.write(out)
    after = open(path, "rb").read()
    return (
        after[:3] == b"\xef\xbb\xbf",
        after.count(b"\r\n"),
        after.count(b"\n") - after.count(b"\r\n"),
        len(after),
    )


def main():
    files = sorted(glob.glob(os.path.join(HERE, "*.ps1")))
    for p in files:
        bom, crlf, lone, size = normalize(p)
        flag = "OK " if bom and lone == 0 else "!! "
        print("%s%-28s BOM=%-5s CRLF=%-4d 裸LF=%d  大小=%d"
              % (flag, os.path.basename(p), bom, crlf, lone, size))
    print("\n共处理 %d 个 .ps1" % len(files))


if __name__ == "__main__":
    main()
