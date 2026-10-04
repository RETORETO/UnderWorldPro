using System;
using System.Collections.Generic;
using Godot;

namespace Script.IO
{
    /// <summary>
    /// 【导出安全】res:// 目录扫描工具 —— 运行时枚举 res:// 目录的地方都统一走这里。
    ///
    /// 为什么必须统一：同一个工程在编辑器里跑和导出成 .pck 后跑有两个差异，
    /// 手写扫描极易踩坑，而且踩了在编辑器里完全看不出来（编辑器一切正常、打包后才 0 张）：
    ///
    ///   1) 目录项名带"重映射"后缀。
    ///      - 文本资源（.tres / .tscn）会被转成二进制资源，目录列表给出的是 "X.tres.remap"；
    ///      - 被导入的素材（.jpg / .png / .wav 等）目录列表给出的是 "X.jpg.import"。
    ///        只判断 ".tres"（或不处理 ".import"）会得到 0 个结果。
    ///   2) 资源全在 .pck 内、磁盘上没有对应目录，因此**不能**用
    ///      ProjectSettings.GlobalizePath + DirAccess.DirExistsAbsolute 做存在性判断：
    ///      导出后 GlobalizePath 得到的是 &lt;exe目录&gt;\Cards\... 这种磁盘路径（不存在），
    ///      守卫恒为 false，函数会静默返回空表。
    ///      DirAccess.Open("res://...") 在编辑器和导出包内都有效。
    ///
    /// 本工具返回的路径一律是去掉后缀后的原始 res:// 路径，可直接交给 ResourceLoader.Load
    /// （引擎会自动把它重映射到包内的二进制资源）。
    ///
    /// 历史事故（导出后分别表现为）：NetClientCardManager「客户端模板库 0 张卡」、
    /// DeckBuilder「卡池 0 张」、LocalWorldDeckProvider「地区卡 0 张」and CardUI 取不到卡图，
    /// 根因都是上述两点，各自手写了一份扫描/匹配。
    /// </summary>
    public static class ResourceDirScan
    {
        /// <summary>文本资源转二进制后，.pck 目录列表里的后缀（"X.tres.remap"）。</summary>
        private const string PackRemapSuffix = ".remap";

        /// <summary>被导入的素材在 .pck 目录列表里的后缀（"X.jpg.import"）。</summary>
        private const string ImportRemapSuffix = ".import";

        /// <summary>递归收集 dir 下所有 .tres（兼容导出包内的 "X.tres.remap"）。</summary>
        public static List<string> TresRecursive(string dir)
        {
            var result = new List<string>();
            TresRecursiveInto(dir, result);
            return result;
        }

        /// <summary>同 <see cref="TresRecursive"/>，但把结果追加进调用方已有的列表。</summary>
        public static void TresRecursiveInto(string dir, List<string> sink)
        {
            using var d = DirAccess.Open(dir);
            if (d == null) return;
            d.ListDirBegin();
            string name;
            while ((name = d.GetNext()) != "")
            {
                if (d.CurrentIsDir())
                {
                    if (!name.StartsWith(".")) TresRecursiveInto(Join(dir, name), sink);
                    continue;
                }
                if (IsTresEntry(name)) sink.Add(Join(dir, NormalizePackedName(name)));
            }
            d.ListDirEnd();
        }

        /// <summary>只收集 dir 这一层（不递归）的 .tres。</summary>
        public static List<string> TresInDir(string dir)
        {
            var result = new List<string>();
            using var d = DirAccess.Open(dir);
            if (d == null) return result;
            d.ListDirBegin();
            string name;
            while ((name = d.GetNext()) != "")
            {
                if (!d.CurrentIsDir() && IsTresEntry(name))
                    result.Add(Join(dir, NormalizePackedName(name)));
            }
            d.ListDirEnd();
            return result;
        }

        /// <summary>目录项名是否是 .tres 资源。编辑器下是 "X.tres"，导出包里是 "X.tres.remap"，两种都认。</summary>
        public static bool IsTresEntry(string name)
        {
            return name.EndsWith(".tres", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".tres" + PackRemapSuffix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 把 .pck 目录项名还原成真实资源名（去掉导出时附加的后缀）。
        /// 例："JC005_liejieshu.tres.remap" → "JC005_liejieshu.tres"；
        ///     "JC005_liejieshu.jpg.import" → "JC005_liejieshu.jpg"。
        /// 编辑器下（无后缀）原样返回。
        /// </summary>
        public static string NormalizePackedName(string name)
        {
            if (name.EndsWith(PackRemapSuffix, StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - PackRemapSuffix.Length);
            if (name.EndsWith(ImportRemapSuffix, StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - ImportRemapSuffix.Length);
            return name;
        }

        /// <summary>拼接 res:// 路径，避免 "res://a/" + "/" + "b" 这种双斜杠。</summary>
        private static string Join(string dir, string name)
        {
            return dir.EndsWith("/") ? dir + name : dir + "/" + name;
        }
    }
}
