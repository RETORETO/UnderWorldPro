using System.Collections.Generic;
using System.IO;
using Godot;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Net;

namespace Script.Tools
{
    /// <summary>
    /// 【C/S 化 · M2 卡牌数据 JSON 协议】.tres → JSON 转换器。
    ///
    /// 目的：把 728 个 Godot `.tres` 卡牌模板转成一份纯托管 `CardTemplateData` JSON，
    /// 供纯托管 Server / 未来浏览器客户端按 TemplateUid 反查（服务端读 JSON、不碰 .tres）。
    ///
    /// 本工具【运行在 Godot 环境】（因为要用 ResourceLoader.Load 读 .tres），但它【产出】服务端能读的
    /// 纯 JSON。转换逻辑只读 `.tres`，不修改任何卡牌文件。
    ///
    /// 用法：在 Godot 编辑器/脚本里调用 CardTresToJson.ConvertAll("res://Cards/card_templates.json")。
    /// </summary>
    public static class CardTresToJson
    {
        /// <summary>遍历 CardXml 目录下所有 .tres，输出合并的 JSON 卡牌模板。</summary>
        public static void ConvertAll(string outputPath)
        {
            var templates = new List<CardTemplateData>();
            var scanned = new HashSet<string>();

            // 三类卡目录全部扫描（玩家卡 / 地区卡 / 秘社卡）
            ScanPlayerDir("res://Cards/Data/PlayerCards", templates, scanned);
            ScanAreaDir("res://Cards/Data/AreaCards", templates, scanned);
            ScanCouncilDir("res://Cards/Data/CouncilCards", templates, scanned);

            GD.Print($"[CardTresToJson] 扫描到 {templates.Count} 张卡（去重后），写出到 {outputPath}");
            var json = System.Text.Json.JsonSerializer.Serialize(new { Templates = templates },
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    IncludeFields = true, // CardTemplateData 用 public 字段，必须 IncludeFields 才不丢
                });
            // 输出到绝对路径（res:// 在导出后只读，这里直接用 user:// 或给定路径）
            File.WriteAllText(ProjectSettings.GlobalizePath(outputPath), json);
            GD.Print($"[CardTresToJson] 完成：{ProjectSettings.GlobalizePath(outputPath)}");
        }

        // ---- 玩家卡 ----
        private static void ScanPlayerDir(string dir, List<CardTemplateData> templates, HashSet<string> scanned)
        {
            // 注册全部卡效果构建器（幂等），确保 EffectDataList 经 CardEffectFactory 填充
            Cards.Effects.AllEffects.RegisterAll();
            foreach (var path in EnumerateTres(dir))
            {
                var template = ResourceLoader.Load<PlayerCardData>(path);
                if (template == null || string.IsNullOrEmpty(template.UID)) continue;
                if (!scanned.Add(template.UID)) continue;
                // 经工厂填充结构化效果（EffectDataList），否则只有 .tres 里的 Effects 文本
                Script.Data.Effects.CardEffectFactory.EnsureEffects(template);
                templates.Add(ToPlayerData(template));
            }
        }

        // ---- 地区卡 ----
        private static void ScanAreaDir(string dir, List<CardTemplateData> templates, HashSet<string> scanned)
        {
            foreach (var path in EnumerateTres(dir))
            {
                var template = ResourceLoader.Load<AreaCardData>(path);
                if (template == null || string.IsNullOrEmpty(template.UID)) continue;
                if (!scanned.Add(template.UID)) continue;
                var d = new CardTemplateData
                {
                    Kind = Script.Net.TemplateKind.Area,
                    UID = template.UID,
                    CardName = template.CardName,
                    Score = template.Score,
                    RequiredPower = template.RequiredPower,
                    WinTriggerEffect = template.WinTriggerEffect,
                    CardImagePath = template.CardImagePath,
                    CategoryFlag = template.Category.ToString(),
                };
                foreach (var s in template.SubCategories ?? new Godot.Collections.Array<string>()) d.SubCategories.Add(s);
                templates.Add(d);
            }
        }

        // ---- 秘社卡 ----
        private static void ScanCouncilDir(string dir, List<CardTemplateData> templates, HashSet<string> scanned)
        {
            foreach (var path in EnumerateTres(dir))
            {
                var template = ResourceLoader.Load<CouncilCardData>(path);
                if (template == null || string.IsNullOrEmpty(template.UID)) continue;
                if (!scanned.Add(template.UID)) continue;
                var d = new CardTemplateData
                {
                    Kind = Script.Net.TemplateKind.Council,
                    UID = template.UID,
                    CardName = template.CardName,
                    Score = template.Score,
                    StartingHandSize = template.StartingHandSize,
                    CardText = template.CardText,
                    CardImagePath = template.CardImagePath,
                    SubType = template.SubType,
                    Organization = template.Organization,
                };
                // 颜色与玩家卡共用 CardTemplateData.Colors（同语义同字段），不再单独导出 ColorText
                foreach (var c in template.Colors ?? new Godot.Collections.Array<CardColor>()) d.Colors.Add(c.ToString());
                foreach (var r in template.Restrictions ?? new Godot.Collections.Array<CouncilRestriction>())
                    if (r != null) d.Restrictions.Add(r.Describe());
                templates.Add(d);
            }
        }

        /// <summary>递归枚举目录下所有 .tres 路径。</summary>
        private static List<string> EnumerateTres(string dir)
        {
            var result = new List<string>();
            if (!DirAccess.DirExistsAbsolute(ProjectSettings.GlobalizePath(dir))) return result;
            using var d = DirAccess.Open(dir);
            if (d == null) return result;
            d.ListDirBegin();
            string name;
            while ((name = d.GetNext()) != "")
            {
                if (d.CurrentIsDir())
                {
                    if (!name.StartsWith(".")) result.AddRange(EnumerateTres(dir + "/" + name));
                    continue;
                }
                if (name.EndsWith(".tres")) result.Add(dir + "/" + name);
            }
            d.ListDirEnd();
            return result;
        }

        private static CardTemplateData ToPlayerData(PlayerCardData p)
        {
            var d = new CardTemplateData
            {
                UID = p.UID,
                CardName = p.CardName,
                Score = p.Score,
                SubCardName = p.SubCardName,
                UniqueField = p.UniqueField,
                Cost = p.Cost,
                CategoryFlag = p.CategoryFlag.ToString(),
                Organization = p.Organization,
                Investigation = p.Investigation,
                Strength = p.Strength,
                Power = p.Power,
                FirstStrikeInvestigation = p.FirstStrikeInvestigation,
                FirstStrikeStrength = p.FirstStrikeStrength,
                FirstStrikePower = p.FirstStrikePower,
                Defense = p.Defense,
                ExtraCostDescription = p.ExtraCostDescription,
                IsExtraCostMandatory = p.IsExtraCostMandatory,
            };

            foreach (var s in p.SubCategories ?? new Godot.Collections.Array<string>()) d.SubCategories.Add(s);
            foreach (var c in p.Colors ?? new Godot.Collections.Array<CardColor>()) d.Colors.Add(c.ToString());
            foreach (var m in p.MagicDomains ?? new Godot.Collections.Array<MagicDomain>()) d.MagicDomains.Add(m.ToString());
            foreach (var e in p.Effects ?? new Godot.Collections.Array<string>()) d.Effects.Add(e);
            foreach (var r in p.ColorLoyaltyRequirements ?? new Godot.Collections.Array<ColorLoyaltyRequirement>())
                if (r != null) d.ColorLoyaltyRequirements.Add(new LoyaltyRequirementData { Name = r.Color.ToString(), Amount = r.Amount });
            foreach (var r in p.DomainLoyaltyRequirements ?? new Godot.Collections.Array<DomainLoyaltyRequirement>())
                if (r != null) d.DomainLoyaltyRequirements.Add(new LoyaltyRequirementData { Name = r.Domain.ToString(), Amount = r.Amount });
            foreach (var t in p.TargetRules ?? new Godot.Collections.Array<Script.Data.Effects.TargetRule>())
                if (t != null)
                {
                    var td = new TargetRuleData { Kind = t.Kind.ToString(), Param = t.Param };
                    foreach (var res in t.Restrictions ?? new Godot.Collections.Array<Script.Data.Effects.TargetRestriction>())
                        td.Restrictions.Add(res.ToString());
                    d.TargetRules.Add(td);
                }
            // 结构化效果/能力（EffectDataList → ServerEffect + Actions）
            foreach (var ed in p.EffectDataList ?? new Godot.Collections.Array<Script.Data.Effects.EffectData>())
            {
                if (ed == null) continue;
                var se = new ServerEffect
                {
                    EffectId = ed.EffectId,
                    ActivateWindow = ed.ActivateWindow,
                    ActivateCondition = ed.ActivateCondition,
                    Cost = ed.Cost,
                    Timing = ed.Timing.ToString(),
                    Duration = ed.Duration,
                };
                foreach (var act in ed.Actions ?? new Godot.Collections.Array<Script.Data.Effects.EffectAction>())
                {
                    if (act == null) continue;
                    var sa = new ServerEffectAction { Verb = act.Verb.ToString() };
                    foreach (var arg in act.Args ?? new Godot.Collections.Array<string>()) sa.Args.Add(arg);
                    se.Actions.Add(sa);
                }
                d.EffectDataList.Add(se);
            }
            return d;
        }
    }
}
