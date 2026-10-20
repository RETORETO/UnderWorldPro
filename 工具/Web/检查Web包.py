#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
检查Web包.py —— 导出的 index.pck 自检工具
=========================================================

为什么需要它
------------
魔改版 Godot 的 Web 导出把 .NET 程序集放在 pck 里
（路径形如 `.godot/mono/publish/wasm32/*.dll`），而 browser-wasm 的
`dotnet publish` **默认开启 ILLink 裁剪**。一旦被裁，游戏入口
`GodotPlugins.Game.Main` 会被删掉，浏览器里只报：

    ERROR: .NET: Couldn't find class 'GodotPlugins.Game.Main'.
    ERROR: .NET: Failed to initialize Mono runtime.
    RuntimeError: null function

这个脚本把 pck 拆开核对，能在打开浏览器之前就发现问题。

pck 格式（Godot 4.x，pack_version=4）要点
----------------------------------------
  · 文件头 40 字节：magic "GDPC" + u32 版本 + 三个 u32 引擎版本
    + u32 pack_flags + u64 file_base(=112) + u64 目录表偏移
  · **目录项里的 offset 是相对 file_base 的**，真实偏移 = file_base + offset
    （踩过坑：忘了加 file_base 会全部错位 112 字节）
  · 目录表第一个 u32 是文件数量，随后每项：
        u32 路径长度（含结尾 0，已按 4 字节对齐）
        char[...] 路径（**不带 res:// 前缀**）
        u64 偏移 / u32 大小 / u32 保留 / u8 md5[16] / u32 标志
  · 路径不带 res:// 前缀，所以 `data.find(b'res://...')` 永远找不到东西

用法
----
    python 检查Web包.py
    python 检查Web包.py build/web/index.pck
"""

import os
import re
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
# 工具\Web\ -> 工具\ -> 工程根
ROOT = os.path.dirname(os.path.dirname(HERE))

def parse_pck(path):
    with open(path, 'rb') as f:
        d = f.read()
    sz = len(d)
    if d[:4] != b'GDPC':
        raise SystemExit('不是有效的 pck 文件（缺少 GDPC 魔数）：%s' % path)

    pack_ver, v1, v2, v3 = struct.unpack_from('<IIII', d, 4)
    pack_flags, file_base = struct.unpack_from('<IQ', d, 20)
    dir_off, = struct.unpack_from('<Q', d, 32)
    count, = struct.unpack_from('<I', d, dir_off)

    entries = []
    pos = dir_off + 4
    for i in range(count):
        plen, = struct.unpack_from('<I', d, pos)
        name = d[pos + 4:pos + 4 + plen].rstrip(b'\x00').decode('utf-8', 'replace')
        q = pos + 4 + plen
        off, = struct.unpack_from('<Q', d, q)
        size, = struct.unpack_from('<I', d, q + 8)
        entries.append((name, off + file_base, size))
        pos = q + 36

    info = dict(size=sz, pack_ver=pack_ver, engine=(v1, v2, v3),
                pack_flags=pack_flags, file_base=file_base,
                dir_off=dir_off, count=count, tail_gap=sz - pos)
    return d, entries, info


# pck 内 .NET 程序集的期望大小：直接从构建目录读，用来判断有没有被裁
def expected_sizes(proj):
    out = {}
    for sub in ('ExportRelease', 'Release'):
        base = os.path.join(proj, '.godot', 'mono', 'temp', 'bin', sub,
                            'browser-wasm')
        if not os.path.isdir(base):
            continue
        for n in os.listdir(base):
            if n.lower().endswith(('.dll', '.json')):
                out.setdefault(n, os.path.getsize(os.path.join(base, n)))
    return out


def main():
    proj = ROOT
    pck = sys.argv[1] if len(sys.argv) > 1 else os.path.join(proj, 'build', 'web', 'index.pck')
    if not os.path.isfile(pck):
        raise SystemExit('找不到 %s —— 请先运行 导出Web版.ps1' % pck)

    d, entries, info = parse_pck(pck)
    print('=' * 78)
    print('pck  : %s' % pck)
    print('大小 : %.1f MB   条目数: %d   pack_version=%d   引擎 %d.%d.%d'
          % (info['size'] / 1048576, info['count'], info['pack_ver'],
             *info['engine']))
    print('目录表自检: 解析尾部剩余 %d 字节（应为 0，非 0 说明格式理解有误）'
          % info['tail_gap'])
    print('=' * 78)

    exp = expected_sizes(proj)
    mono = sorted(e for e in entries if '/mono/publish/' in e[0])

    print('\n【.NET 发布文件】共 %d 个' % len(mono))
    print('%-46s %12s %12s  %s' % ('文件', 'pck 内', '构建产物', '判定'))
    bad = []
    for name, off, size in mono:
        base = name.split('/')[-1]
        want = exp.get(base)
        if want is None:
            verdict = '(构建目录无同名文件)'
        elif want == size:
            verdict = 'OK'
        else:
            verdict = '!! 被裁剪 %.0f%%' % (100 - size * 100.0 / want)
            bad.append((base, size, want))
        print('%-46s %12d %12s  %s' % (base, size, want if want else '-', verdict))

    print('\n【关键判定】')
    # 1) 游戏程序集是否完整
    uw = [e for e in mono if e[0].endswith('/UWPro.dll')]
    if not uw:
        print('  [致命] pck 里没有 .godot/mono/publish/wasm32/UWPro.dll')
    else:
        _, off, size = uw[0]
        blob = d[off:off + size]
        has_cls = b'GodotPlugins.Game' in blob
        has_init = b'InitializeFromGameProject' in blob
        print('  UWPro.dll 大小 = %d 字节' % size)
        print('    含类名 GodotPlugins.Game       : %s' % ('是' if has_cls else '否'))
        print('    含方法 InitializeFromGameProject: %s' % ('是' if has_init else '否'))
        if size < 100000 or not (has_cls and has_init):
            print('    [致命] 游戏程序集不完整 —— 引擎会报')
            print('           ".NET: Couldn\'t find class \'GodotPlugins.Game.Main\'."')
            print('    修复：UWPro.csproj 的 web 分支加 <PublishTrimmed>false</PublishTrimmed>')
            print('          并执行 <TrimmerRootAssembly Include="UWPro" />，然后删掉')
            print('          .godot/mono/temp 重新导出。')
        else:
            print('    [通过] 游戏程序集完整，引擎可以找到引导类。')

    print('  被裁剪的程序集数量: %d' % len(bad))

    # 2) 顺手看看空间分布，方便排查 pck 过大
    import collections
    agg = collections.Counter()
    for name, off, size in entries:
        agg[name.split('/')[0]] += size
    print('\n【空间分布 Top 6】')
    for k, v in agg.most_common(6):
        print('  %-40s %8.1f MB' % (k, v / 1048576.0))

    return 0 if not bad else 2


if __name__ == '__main__':
    sys.exit(main())
