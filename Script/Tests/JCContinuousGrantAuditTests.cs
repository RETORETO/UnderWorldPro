using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;

namespace Script.Tests
{
    /// <summary>
    /// 【2026-10·层级计算改造前置审计】**卡文（`.tres` 的 `Effects`）声明了能力图标 ⇒ 脚本必须真的授予该图标**。
    ///
    /// 判据来源（用户 2026-10 口径）："卡的效果不要直接根据代码效果分层，需要结合 TRES 内实际描述分层
    /// （**tres 的描述是实际判据**）"。本套件把该口径落成一条**可执行**判据。
    ///
    /// 为什么要这条判据（`ContinuousGrants` 的"两份账"）：
    ///  · 本项目把"**能力图标**"与"**数值属性**"记成**两份独立的账**
    ///    （`CardInstance._grantedIcons` / `_grantedAttributes`），且**三条通道的折算规则不一致**：
    ///      - 动作通道 `Gain`（`EffectEngine.ApplyGain`，4476–4484）→ **自动折算**（同时写 Icon + Attribute）；
    ///      - 形态通道 `FormChangeSpec.Grants`（`ApplyFormGrants`，4694–4700）→ **自动折算**；
    ///      - **持续通道 `ContinuousGrants`（`RegisterPersistent`，7535–7541）→ 不折算**，
    ///        卡作者**必须手写两条**（`Icon:X` + `Attribute:<对应属性>`）。
    ///  · 于是"只写 `Attribute`"的持续效果会**数值对、图标缺**；而 `Icon` 通道当前几乎无消费者
    ///    （只有持续授予自愈判定 `EffectEngine.IsGrantPresent` 与测试读它；卡面 UI 只渲染 调/战/势 数值
    ///    与子类别/魔法领域标签）⇒ **既不报错、界面也看不出来**，所以这类不一致能长期潜伏。
    ///
    /// 判据规则（**只认卡文措辞**）：
    ///  ① 把卡文（去掉 `FAQ：` 之后的部分）按 `。；\n` 切句；
    ///  ② 句中出现"**获得**"或"**（额外）具有**"、且出现某个**能力图标名**（黑星/白星/黑眼/白眼/黑刀/白刀）、
    ///     且**不含**"替换为"/"失去"（那是改写/移除，不是授予）⇒ 该卡**承诺**了这个图标；
    ///  ③ 脚本侧必须能找到该图标的**授予**，三条任一即可：
    ///     `ContinuousGrants` 的 `Kind="Icon"`、`FormChangeSpec.Grants` 的 `Kind="Icon"`、
    ///     或任一动作的 `Gain` 且 `Args[0]` ＝该图标名（动作通道引擎会自动折算并写 Icon）。
    ///  ④ 承诺了却找不到 ⇒ **违规**。
    ///
    /// 已知违规（本套件首轮即查出 4 张）由 <see cref="KnownDefectAttribute"/> 标记为"已知缺陷"：
    /// **不计入套件失败**，但每次运行都会以 `[KNOWN]` 打印完整清单；修好后自动翻绿并打印 `[FIXED?]`。
    /// </summary>
    public class JCContinuousGrantAuditTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        /// <summary>6 个**能力图标**（卡文写成［X］）。魔法领域图标（心/血/死/星/灵/…）不在本判据内
        /// ——卡文自己就区分"能力栏内的能力图标"与"魔法领域图标"（见 JC101/JC066 的括注）。</summary>
        private static readonly string[] AbilityIcons = { "黑星", "白星", "黑眼", "白眼", "黑刀", "白刀" };

        private static List<string> AllCardFiles()
        {
            var files = new List<string>();
            foreach (var f in global::Godot.DirAccess.GetFilesAt(Dir))
                if (!string.IsNullOrEmpty(f) && f.EndsWith(".tres")) files.Add(f);
            files.Sort();
            return files;
        }

        private static PlayerCardData LoadCard(string file)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = global::Godot.ResourceLoader.Load<PlayerCardData>(Dir + file);
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        /// <summary>卡文（去掉 FAQ 段——FAQ 是解释性文字，常提到别的词条，会造成误报）。</summary>
        private static string CardText(PlayerCardData tmpl)
        {
            var sb = new System.Text.StringBuilder();
            if (tmpl?.Effects != null)
                foreach (var line in tmpl.Effects)
                    if (!string.IsNullOrEmpty(line)) sb.Append(line).Append(' ');
            string s = sb.ToString();
            int faq = s.IndexOf("FAQ：", System.StringComparison.Ordinal);
            return faq >= 0 ? s.Substring(0, faq) : s;
        }

        /// <summary>卡文是否**承诺**了图标 `icon`（"获得［X］"/"（额外）具有…［X］"，且不是"替换为/失去"）。</summary>
        private static bool TextPromisesIcon(string text, string icon)
        {
            foreach (var raw in text.Split('。', '；', '\n', ' '))
            {
                string sent = raw ?? "";
                if (!sent.Contains(icon)) continue;
                if (sent.Contains("替换为") || sent.Contains("失去")) continue;   // 改写/移除 ≠ 授予
                if (sent.Contains("获得") || sent.Contains("具有")) return true;
            }
            return false;
        }

        /// <summary>该授予条目是否等价于"授予了图标 `icon`"——**"图标即属性"**（用户 2026-10 口径）：
        /// 写 `Icon:黑刀` 与写 `Attribute:战斗` 是同一次写入的两面；引擎侧已在 **7c 写入收口**
        /// （`EffectEngine.FoldIconAttributePairs`：持续/光环/重判/镜像四条施加路径统一折算）
        /// ⇒ 声明任一侧，运行时**两条账都会成立**（`HasIcon` 为真、数值也加上）。</summary>
        private static bool GrantYieldsIcon(Script.Data.Effects.EffectGrant g, string icon)
        {
            if (g == null || string.IsNullOrEmpty(g.Value)) return false;
            if (g.Kind == "Icon") return g.Value == icon;
            if (g.Kind != "Attribute") return false;
            return Script.Manager.EffectEngine.AttributeToIcon(g.Value) == icon;
        }

        /// <summary>脚本是否真的授予了该图标（四条通道任一）。</summary>
        private static bool ScriptGrantsIcon(PlayerCardData t, string icon)
        {
            if (t?.EffectDataList == null) return false;
            foreach (var e in t.EffectDataList)
            {
                if (e == null) continue;
                // ① 持续通道：ContinuousGrants（`Icon:` 直写，或 `Attribute:` 由引擎折算 ⇒ 同一件事）
                if (e.ContinuousGrants != null)
                    foreach (var g in e.ContinuousGrants)
                        if (GrantYieldsIcon(g, icon)) return true;
                // ② 形态通道：FormChangeSpec.Grants（同上）
                if (e.FormChange?.Grants != null)
                    foreach (var g in e.FormChange.Grants)
                        if (GrantYieldsIcon(g, icon)) return true;
                // ③ 动作通道：Gain 的 Args[0] ＝图标名 **或** 属性名（引擎会自动折算并写两条账）
                if (e.Actions != null)
                    foreach (var a in e.Actions)
                        if (a != null && a.Verb == ActionVerb.Gain && a.Args != null && a.Args.Count > 0
                            && (a.Args[0] == icon
                                || Script.Manager.EffectEngine.AttributeToIcon(a.Args[0]) == icon)) return true;
            }
            return false;
        }

        /// <summary>
        /// 判据本体：遍历 JC 全量卡，收集"卡文承诺了能力图标、脚本却没授予"的卡。
        /// 期望 0 违规（7c 写入收口后即 0；违规会直接红，不再有已知缺陷豁免）。
        /// </summary>
        public void TestAudit_卡文能力图标与脚本授予一致()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string text = CardText(tmpl);
                if (string.IsNullOrEmpty(text)) continue;
                foreach (var icon in AbilityIcons)
                {
                    if (!TextPromisesIcon(text, icon)) continue;
                    if (ScriptGrantsIcon(tmpl, icon)) continue;
                    violations.Add($"{tmpl.CardName}（{tmpl.UID}）：卡文承诺［{icon}］但脚本未授予该图标"
                                   + "（持续效果只写了 Attribute ⇒ 数值对、图标缺；HasIcon 为假）");
                }
            }
            if (violations.Count > 0)
                global::Godot.GD.Print("[审计·能力图标] 违规 " + violations.Count + " 条：\n  "
                                       + string.Join("\n  ", violations));
            Assert.AreEqual(0, violations.Count,
                "卡文（.tres）声明了能力图标，脚本必须授予该图标（共 " + violations.Count + " 处违规）：\n  "
                + string.Join("\n  ", violations));
        }

        /// <summary>
        /// 【对照·防误报】"替换为/失去"不算授予：JC106 的"都替换为［黑刀］"、JC123 的"失去所有［黑星］［白星］"
        /// 都**不得**被本判据当成"承诺授予"。这条锁住判据本身的措辞边界（防止将来把改写/移除也算成授予）。
        /// </summary>
        public void TestAudit_替换与失去不算授予()
        {
            Assert.IsFalse(TextPromisesIcon("目标角色上的所有［黑星］和［白星］能力图标都替换为［黑刀］，并获得灵体。", "黑星"),
                "『替换为』是改写，不算承诺授予［黑星］");
            Assert.IsFalse(TextPromisesIcon("所有具有锁定标记的角色或暗藏者失去所有［黑星］和［白星］能力图标。", "白星"),
                "『失去』是移除，不算承诺授予［白星］");
            Assert.IsTrue(TextPromisesIcon("所有其他本方灵体角色获得［白星］。", "白星"),
                "『获得［白星］』是承诺授予");
            Assert.IsTrue(TextPromisesIcon("所有未印刷有魔法领域图标的本方角色获得【心】图标和［黑刀］。", "黑刀"),
                "同句内的［黑刀］应被识别（即便同句还有魔法领域图标）");
        }
    }
}
