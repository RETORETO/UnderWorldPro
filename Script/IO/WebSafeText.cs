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

        // ==================== 数字解析（2026-10-13 新增） ====================

        /// <summary>
        /// 【Web 安全】整数解析，**完全不经过 NumberFormatInfo / 全球化**。
        ///
        /// 为什么必须要有它：
        ///   `ushort.TryParse(s, out v)` / `int.Parse(s)` 这类**不带 IFormatProvider** 的重载，
        ///   内部会走 `NumberFormatInfo.CurrentInfo`（判断正负号/千分位），
        ///   而 `PositiveSign` / `NegativeSign` 的取值要落到**文化数据（ICU）**上 ——
        ///   与 `"..." + (-1)` 是同一条必崩路径。
        ///   （2026-10-13 实测：Web 版大厅页点「连接」时，进程在进到 `ClientSession.Connect` **之前**就 Aborted，
        ///    夹在"参数解析"与"保存偏好"之间；换成下面的实现后可继续走到连接。）
        ///
        /// 语义与 `int.TryParse(s, out v)` 的默认重载一致：允许首尾 ASCII 空白、可选 '+'/'-' 前缀、纯十进制数字。
        /// </summary>
        public static bool TryParseInt(string s, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;

            int i = 0, n = s.Length;
            while (i < n && IsAsciiWhitespace(s[i])) i++;

            bool neg = false;
            if (i < n && (s[i] == '-' || s[i] == '+')) { neg = s[i] == '-'; i++; }
            if (i >= n) return false;

            long acc = 0;
            bool any = false;
            while (i < n && s[i] >= '0' && s[i] <= '9')
            {
                acc = acc * 10 + (s[i] - '0');
                if (acc > 2147483648L) acc = 2147483648L;   // 溢出即钳制，最终会被下面的范围判断拒掉
                any = true;
                i++;
            }
            if (!any) return false;

            while (i < n && IsAsciiWhitespace(s[i])) i++;
            if (i != n) return false;                        // 尾部还有别的东西 → 非法

            long v = neg ? -acc : acc;
            if (v > int.MaxValue || v < int.MinValue) return false;
            value = (int)v;
            return true;
        }

        /// <summary>【Web 安全】等价于 `ushort.TryParse`（范围 0..65535）。见 <see cref="TryParseInt"/>。</summary>
        public static bool TryParseUshort(string s, out ushort value)
        {
            value = 0;
            if (!TryParseInt(s, out int v)) return false;
            if (v < 0 || v > 65535) return false;
            value = (ushort)v;
            return true;
        }

        /// <summary>【Web 安全】等价于 `string.IsNullOrWhiteSpace`，但空白判定只认 ASCII（不碰 Unicode 类别表）。</summary>
        public static bool IsNullOrWhiteSpaceAscii(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            for (int i = 0; i < s.Length; i++)
                if (!IsAsciiWhitespace(s[i])) return false;
            return true;
        }

        // ==================== 忽略大小写的比较（2026-10-13 新增） ====================
        //
        // 【为什么 "序数忽略大小写" 在 Web 上一样会崩】
        //   `StringComparison.OrdinalIgnoreCase` / `StringComparer.OrdinalIgnoreCase` 看着像纯序数，
        //   其实 .NET 的实现（Ordinal.CompareStringIgnoreCase）只对 **ASCII** 字符走算术快路径：
        //        while (length != 0 && charA <= 0x7F && charB <= 0x7F) { ... }
        //   一旦比较区间里出现任何一个 >0x7F 的字符，就落到
        //        OrdinalCasing.CompareStringIgnoreCaseNonAscii -> Interop.Globalization.ChangeCase -> ICU
        //   Web 版运行时没有 ICU -> 直接 abort。
        //   2026-10-13 实测：`DeckCatalog.ListDeckPaths()` 里对含中文文件名的卡组目录
        //   `list.Sort(StringComparer.OrdinalIgnoreCase)`，正好崩在 Sort 这一行
        //   （日志停在 "p7 ListDirEnd 完成，即将 Sort"，紧接着 Aborted()）。
        //   这也解释了 `ResourceDirScan` 用 OrdinalIgnoreCase 为何一直没事：它比的是 `.tres`/`.`/`.remap`
        //   这类**纯 ASCII 字面量**，永远走快路径。
        //
        //   结论：只要比较的两侧**可能含非 ASCII**（卡组名、卡名、用户输入……），
        //   就必须换成下面这套只折叠 ASCII 的版本。中文本身没有大小写，
        //   因此对非 ASCII 串，本实现与 OrdinalIgnoreCase 的结果完全一致。

        /// <summary>ASCII 大小写折叠（'A'-'Z' → 'a'-'z'），非 ASCII 字符原样保留。不碰 TextInfo/ICU。</summary>
        private static char FoldAscii(char c)
        {
            return (c >= 'A' && c <= 'Z') ? (char)(c + 32) : c;
        }

        /// <summary>
        /// 【Web 安全】等价于 `string.Equals(a, b, StringComparison.OrdinalIgnoreCase)`，但只折叠 ASCII 大小写。
        /// 见上文“为什么序数忽略大小写也会崩”。
        /// </summary>
        public static bool EqualsIgnoreCaseAscii(string a, string b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (FoldAscii(a[i]) != FoldAscii(b[i])) return false;
            return true;
        }

        /// <summary>
        /// 【Web 安全】ASCII 折叠版的序数比较（&lt;0 / 0 / &gt;0）。
        /// 替代 `StringComparer.OrdinalIgnoreCase.Compare`，可直接当 `Comparison&lt;string&gt;` / `IComparer&lt;string&gt;` 用。
        /// </summary>
        public static int CompareIgnoreCaseAscii(string a, string b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            int n = a.Length < b.Length ? a.Length : b.Length;
            for (int i = 0; i < n; i++)
            {
                char ca = FoldAscii(a[i]), cb = FoldAscii(b[i]);
                if (ca != cb) return ca < cb ? -1 : 1;
            }
            return a.Length - b.Length;
        }

        /// <summary>
        /// 【Web 安全】原地按「ASCII 折叠」忽略大小写排序。
        /// 替代 `list.Sort(StringComparer.OrdinalIgnoreCase)`（该写法在含中文名的目录上实测 abort）。
        /// `List&lt;T&gt;.Sort(Comparison&lt;T&gt;)` 全程走托管比较器 + 内省排序，不碰 ICU。
        /// </summary>
        public static void SortIgnoreCaseAscii(System.Collections.Generic.List<string> list)
        {
            if (list == null || list.Count < 2) return;
            list.Sort(CompareIgnoreCaseAscii);
        }

        /// <summary>【Web 安全】等价于 `s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)`（只折叠 ASCII）。</summary>
        public static bool EndsWithIgnoreCaseAscii(string s, string suffix)
        {
            if (s == null || suffix == null) return false;
            if (suffix.Length > s.Length) return false;
            int off = s.Length - suffix.Length;
            for (int i = 0; i < suffix.Length; i++)
                if (FoldAscii(s[off + i]) != FoldAscii(suffix[i])) return false;
            return true;
        }

        /// <summary>【Web 安全】等价于 `s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)`（只折叠 ASCII）。</summary>
        public static bool StartsWithIgnoreCaseAscii(string s, string prefix)
        {
            if (s == null || prefix == null) return false;
            if (prefix.Length > s.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (FoldAscii(s[i]) != FoldAscii(prefix[i])) return false;
            return true;
        }

        /// <summary>【Web 安全】在字符串列表里找与 value 忽略大小写（ASCII）相等的项，返回下标，找不到返回 -1。</summary>
        public static int IndexOfIgnoreCaseAscii(System.Collections.Generic.IReadOnlyList<string> list, string value)
        {
            if (list == null) return -1;
            for (int i = 0; i < list.Count; i++)
                if (EqualsIgnoreCaseAscii(list[i], value)) return i;
            return -1;
        }
    }
}
