using System;

namespace Script.IO
{
    /// <summary>
    /// 【Web 安全文本工具 · 2026-10-12】
    ///
    /// 背景（本轮实测确认的重大 Web 事故）：魔改版 Godot 4.7.2 的 browser-wasm 运行时里，
    /// **任何走「全球化 / ICU」路径的托管 API 一调用就让整个 wasm 进程 abort**
    /// （表现为浏览器控制台 Aborted() + RuntimeError: unreachable，且 wasm 栈每次完全一样）。
    ///
    /// 已实测确认为「必崩」的写法：
    ///     "BH".StartsWith(".")        // 不带 StringComparison 的 StartsWith（文化敏感）
    ///     "ABC".ToLower()             // 不带 Invariant 的 ToLower
    ///     "..." + (-1)                // 【2026-10-12 新确认】**格式化负整数**
    ///     $"{(int)1.2f}"              // 同上（正整数走纯算术快路径所以看不出来，负数才触发）
    /// 逐 token 二分实测（MainMenu 的 b01~b14 探针）：
    ///     `"[WebPerf] b05 delayms=" + (int)(_autoDelay * 1000f)`  → 值 1200，**正常打印**
    ///     `"[WebPerf] b06 pagesize=" + _pageSizeOverride`         → 值 -1，**紧接着 Aborted()**
    /// 机理：.NET 里正整数格式化走纯算术（Int32ToDecStr），**负数才需要
    /// NumberFormatInfo.NegativeSign -> CultureInfo.CurrentCulture.NumberFormat**，
    /// 而后者会落到全球化/ICU 实现上 -> abort。
    /// 打开 csproj 的 InvariantGlobalization 之后**依然必崩**
    ///（已实测 runtimeconfig.json 里 `System.Globalization.Invariant: true` 与
    ///  `Hybrid: false`、`PredefinedCulturesOnly: true` 都在，照样崩），
    /// 所以不能指望运行时开关，必须在代码层面绕开 —— 数字一律走本类的 Int() / Float()。
    ///
    /// 已实测确认「安全」的等价写法（见 MainMenu 里的探针 [P] 序列）：
    ///     s.StartsWith(x, StringComparison.Ordinal / OrdinalIgnoreCase)
    ///     s.EndsWith(x, StringComparison.Ordinal / OrdinalIgnoreCase)
    ///     list.Sort(StringComparer.Ordinal)
    ///     string.CompareOrdinal(a, b)
    ///     ulong / 非负 int 的默认格式化
    /// 【注意】`ToLowerInvariant()` **不要**当成安全写法：它同样要经过 TextInfo
    ///（invariant 只是"数据是恒定的"，不代表"不经过 ICU 实现"）。本工程里凡是要 ASCII 语义的
    /// 大小写折叠，一律用下面的 ToLowerAscii。
    ///
    /// 本类补充两类小工具：① 序数/ASCII 实现，替代原先依赖 Unicode 类别表或文化敏感比较的地方；
    /// ② 与全球化无关的数字格式化，替代 $"{负数}" 这类会让 Web 崩掉的写法。
    /// 工程内卡号 UID 一律是 ASCII（如 JC005_liejieshu、XG01_yinyaotanqiuzhe），
    /// 因此用 ASCII 判定与原先的 Char.IsLetter 行为完全一致，桌面端结果不变。
    /// </summary>
    public static class WebSafeText
    {
        /// <summary>
        /// 【Web 安全】整数转字符串，**完全不经过 NumberFormatInfo / 全球化**。
        /// 只要值可能为负就必须用它：`"x=" + negativeValue` 在 Web 上会直接 abort（见类头说明）。
        /// 桌面端输出与 int.ToString() 完全一致（已处理 int.MinValue 溢出边界）。
        /// </summary>
        public static string Int(int v)
        {
            if (v == 0) return "0";

            // 用 long 承接，专门处理 int.MinValue（-int.MinValue 会溢出）
            long x = v;
            bool neg = x < 0;
            if (neg) x = -x;

            char[] buf = new char[12];      // int 最长 "-2147483648" = 11 字符
            int i = buf.Length;
            while (x > 0)
            {
                buf[--i] = (char)('0' + (int)(x % 10));
                x /= 10;
            }
            if (neg) buf[--i] = '-';
            return new string(buf, i, buf.Length - i);
        }

        /// <summary>
        /// 【Web 安全】浮点转字符串，**完全不经过 NumberFormatInfo**。
        /// 为什么连正浮点也要绕开：浮点格式化在几乎所有文化下都要取小数点符号（NumberDecimalSeparator），
        /// 与负整数取 NegativeSign 是同一条 NumberFormatInfo 路径。
        /// 输出规则：最多 3 位小数，去掉末尾多余的 0（例：1200、1.25、0.5、-3.14）。
        /// </summary>
        public static string Float(float v)
        {
            if (float.IsNaN(v)) return "NaN";
            if (float.IsPositiveInfinity(v)) return "inf";
            if (float.IsNegativeInfinity(v)) return "-inf";

            bool neg = v < 0f;
            if (neg) v = -v;
            if (v > 1e9f) return neg ? "-huge" : "huge";

            // 四舍五入到 3 位小数，全程整数运算
            long scaled = (long)((double)v * 1000.0 + 0.5);
            long ip = scaled / 1000;
            long fp = scaled % 1000;

            string s = LongToDecStr(ip);
            if (fp != 0)
            {
                char[] f = { '0', '0', '0' };
                f[0] = (char)('0' + (int)(fp / 100));
                f[1] = (char)('0' + (int)(fp / 10 % 10));
                f[2] = (char)('0' + (int)(fp % 10));
                int end = 3;
                while (end > 0 && f[end - 1] == '0') end--;
                if (end > 0) s = s + "." + new string(f, 0, end);
            }

            if (neg && !(ip == 0 && fp == 0)) s = "-" + s;
            return s;
        }

        private static string LongToDecStr(long x)
        {
            if (x == 0) return "0";
            if (x < 0) x = -x;                      // 调用点已保证非负；这里只做兜底
            char[] buf = new char[20];
            int i = buf.Length;
            while (x > 0)
            {
                buf[--i] = (char)('0' + (int)(x % 10));
                x /= 10;
            }
            return new string(buf, i, buf.Length - i);
        }

        /// <summary>是否是 ASCII 字母（A-Z / a-z）。等价于 Char.IsLetter 在卡号前缀上的用法，但不碰 Unicode 类别表。</summary>
        public static bool IsAsciiLetter(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
        }

        /// <summary>
        /// 取字符串开头连续 ASCII 字母作为前缀（卡图目录名规则）。
        /// 三个 UI 类（CardUI / StackEntryUI / TriggerEntryUI）与 ArenaOl 共用同一规则，集中在这里避免各写一份。
        /// </summary>
        public static string LeadingAsciiLetters(string s, string fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            int i = 0;
            while (i < s.Length && IsAsciiLetter(s[i])) i++;
            return i > 0 ? s.Substring(0, i) : fallback;
        }

        /// <summary>
        /// 安全版 StartsWith（序数）。
        /// 【务必用这个或显式 Ordinal 重载】不要写单参数的 StartsWith —— Web 上会直接崩。
        /// </summary>
        public static bool StartsWithOrdinal(string s, string prefix)
        {
            return !string.IsNullOrEmpty(s) && s.StartsWith(prefix, StringComparison.Ordinal);
        }

        /// <summary>安全版 EndsWith（序数）。理由同 <see cref="StartsWithOrdinal"/>。</summary>
        public static bool EndsWithOrdinal(string s, string suffix)
        {
            return !string.IsNullOrEmpty(s) && s.EndsWith(suffix, StringComparison.Ordinal);
        }

        /// <summary>
        /// 安全版「转小写」：只折叠 ASCII 大写字母，完全不碰 TextInfo/ICU。
        /// 用于命令解析、关键词匹配等只需要 ASCII 语义的场合（原 ToLower() 的调用点都属于这类）。
        /// </summary>
        public static string ToLowerAscii(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            char[] buf = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 'A' && c <= 'Z')
                {
                    if (buf == null) buf = s.ToCharArray();
                    buf[i] = (char)(c + 32);
                }
            }
            return buf == null ? s : new string(buf);
        }

        /// <summary>
        /// 安全版「裁剪首尾空白」：只裁 ASCII 空白（空格 / Tab / CR / LF）。
        /// 为什么不用 string.Trim()：它内部循环调用 char.IsWhiteSpace，而该判定在 Web 运行时上
        /// 同样可能落到 Unicode 类别表（同一类全球化路径）。卡号、卡名、卡组名里的空白都是 ASCII，
        /// 因此本实现与 Trim() 的可见行为一致。
        /// </summary>
        public static string TrimAscii(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int b = 0, e = s.Length - 1;
            while (b <= e && IsAsciiWhitespace(s[b])) b++;
            while (e >= b && IsAsciiWhitespace(s[e])) e--;
            if (b == 0 && e == s.Length - 1) return s;
            return s.Substring(b, e - b + 1);
        }

        /// <summary>ASCII 空白判定（不碰 Unicode 类别表）。</summary>
        public static bool IsAsciiWhitespace(char c)
        {
            return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\v';
        }
    }
}
