using System.Collections.Generic;
using Godot;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Game.Entities;
using Script.Manager;
using Script.Net;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【正式代码·真跨进程】客户端独立 CardManager：重载 ResolveInstance，本地无实例时从模板（.tres）按权威 UID 新建。
    ///
    /// 跨进程客户端进程与服务端【不共享】CardManager（各进程实例独立），客户端唯一的真相来源是服务端下发的快照。
    /// 本类让客户端能从快照里的 TemplateUid 反查本地模板库并重建卡实例，同时把 UID 重置为服务端权威值，
    /// 保证 GetInstance 能按快照 UID 命中、且后续快照能正确复用/覆盖同一实例。
    ///
    /// 与单进程回环的区别：单进程回环客户端复用服务端 CardManager（ResolveInstance 直接命中已建实例）；
    /// 本类用于真跨进程（两个独立 Godot 进程）。
    /// </summary>
    [GlobalClass]
    public partial class NetClientCardManager : CardManager
    {
        /// <summary>TemplateUid → 模板数据（客户端本地模板库，运行时扫描 res://Cards/Data 构建）。</summary>
        private Dictionary<string, BaseCardData> _templates;

        /// <summary>模板扫描根目录（与 GameInitializer 装配的卡模板来源一致）。</summary>
        private static readonly string[] TemplateDirs =
        {
            "res://Cards/Data/PlayerCards",
            "res://Cards/Data/AreaCards",
            "res://Cards/Data/CouncilCards",
        };

        /// <summary>构建并设置客户端本地模板库索引（跨进程客户端在首次 ResolveInstance 前调用一次）。
        ///
        /// 【效果系统接入·关键】客户端进程【没有 GameInitializer】（不跑规则），若不在此处补做效果注入，
        /// 则 .tres 里的通用 PlayerCardData 只带牌面静态数据、EffectDataList / TargetRules 为空
        /// （效果由 CardEffectFactory 按 UID 构建，不序列化进 .tres），表现为联机客户端
        /// 「双击附属/角色卡 → 无可激活能力」「无合法目标」等效果链断裂。
        /// 这里与 GameInitializer.InitializeAll 的第一件事一致：先注册全部效果构建器，再逐张模板注入（幂等）。
        /// </summary>
        public void EnsureTemplateIndex()
        {
            if (_templates != null) return;
            // 0) 注册全部卡效果构建器（幂等）：必须在任何 EnsureEffects 之前，否则查表为空、注入无效果。
            Cards.Effects.AllEffects.RegisterAll();
            _templates = new Dictionary<string, BaseCardData>();
            foreach (var dir in TemplateDirs)
                foreach (var path in EnumerateTres(dir))
                {
                    var template = ResourceLoader.Load<BaseCardData>(path);
                    if (template == null || string.IsNullOrEmpty(template.UID)) continue;
                    // 1) 玩家卡：按 UID 构建 EffectDataList / TargetRules（与单机/服务端同一份构建器，保证 EffectId 一致）。
                    if (template is PlayerCardData playerTmpl)
                        CardEffectFactory.EnsureEffects(playerTmpl);
                    _templates[template.UID] = template;
                }
            GD.Print($"[NetClientCardManager] 客户端模板库就绪：{_templates.Count} 张卡（效果构建器已注册并注入）");
        }

        /// <summary>重载 ResolveInstance：本地无实例时从模板按权威 UID 新建（跨进程核心重建路径）。</summary>
        protected override CardInstance ResolveInstance(ZoneCardRef r)
        {
            // 1) 本地已建（重复快照复用同一实例，保持投影状态累积正确）。
            //    但若键命中而模板不符（历史残留的本地自增临时实例被权威 UID 撞上），则视为残留，
            //    落到下方按权威模板重建覆盖，避免归位到错误的卡。
            var hit = GetInstance(r.Uid);
            if (hit != null && (r == null || hit.Template?.UID == r.TemplateUid)) return hit;

            // 注：隐藏占位（r.IsHidden）已在基类 RebuildFromRef 中统一处理，不会进入本方法。
            // 2) 从模板按权威 UID 新建（TemplateUid 为空时不查表：避免以 null 作为字典键，也避免误命中）
            EnsureTemplateIndex();
            if (r != null && !string.IsNullOrEmpty(r.TemplateUid) && _templates.TryGetValue(r.TemplateUid, out var tmpl))
            {
                // 防御性再注入（幂等）：即使模板经其它路径进入（未过 EnsureTemplateIndex 的注入循环），
                // 也保证其 EffectDataList / TargetRules 已由 CardEffectFactory 构建，避免效果链断裂。
                if (tmpl is PlayerCardData playerTmpl) CardEffectFactory.EnsureEffects(playerTmpl);
                // 直接用权威 UID 建实例并登记，避免走 CreateCardInstance 的本地自增 UID（inst_N）。
                // 跨进程下客户端自增 UID 与服务端权威 UID 同前缀 inst_，若经 CreateCardInstance 生成临时 UID
                // 再覆盖，会产生残留临时键，后续快照 GetInstance 可能命中残留实例导致归位错乱
                // （症状：建立资产/移动卡后，UI 手牌出现本应在资产区的卡、或漏掉应在手牌的卡）。
                var inst = new CardInstance(tmpl, r.Uid) { OwnerId = r.OwnerId, ControllerId = r.ControllerId };
                AdoptInstanceWithUid(inst, r.Uid); // 按权威 UID 登记
                return inst;
            }
            GD.PrintErr($"[NetClientCardManager] 无法解析模板：{r?.TemplateUid ?? "null"}（UID={r?.Uid ?? "null"}）");
            return null;
        }

        private static List<string> EnumerateTres(string dir)
        {
            var result = new List<string>();
            // ⚠️ 不要用 ProjectSettings.GlobalizePath + DirAccess.DirExistsAbsolute 判断 res:// 是否存在：
            //    导出后素材在 .pck 内，GlobalizePath 得到的是磁盘路径（不存在）→ 这里会直接返回空表，
            //    症状：联机客户端「客户端模板库就绪：0 张卡」+ 全部卡「无法解析模板」，卡面/卡图全空。
            //    DirAccess.Open 对 res:// 在编辑器和导出包内都有效，直接用即可。
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
                // 导出后 .tres 被转成二进制资源，目录列表给出的是 "xxx.tres.remap"；编辑器下是 "xxx.tres"。
                // 两种都要识别，remap 条目去掉后缀后仍按原 .tres 路径加载（ResourceLoader 会自动重映射）。
                if (name.EndsWith(".tres"))
                    result.Add(dir + "/" + name);
                else if (name.EndsWith(".tres.remap"))
                    result.Add(dir + "/" + name.Substring(0, name.Length - ".remap".Length));
            }
            d.ListDirEnd();
            return result;
        }
    }
}
