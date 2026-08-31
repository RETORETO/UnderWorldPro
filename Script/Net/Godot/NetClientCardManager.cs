using System.Collections.Generic;
using Godot;
using Script.Data.Cards;
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

        /// <summary>构建并设置客户端本地模板库索引（跨进程客户端在首次 ResolveInstance 前调用一次）。</summary>
        public void EnsureTemplateIndex()
        {
            if (_templates != null) return;
            _templates = new Dictionary<string, BaseCardData>();
            foreach (var dir in TemplateDirs)
                foreach (var path in EnumerateTres(dir))
                {
                    var template = ResourceLoader.Load<BaseCardData>(path);
                    if (template == null || string.IsNullOrEmpty(template.UID)) continue;
                    _templates[template.UID] = template;
                }
            GD.Print($"[NetClientCardManager] 客户端模板库就绪：{_templates.Count} 张卡");
        }

        /// <summary>重载 ResolveInstance：本地无实例时从模板按权威 UID 新建（跨进程核心重建路径）。</summary>
        protected override CardInstance ResolveInstance(ZoneCardRef r)
        {
            // 1) 本地已建（重复快照复用同一实例，保持投影状态累积正确）。
            //    但若键命中而模板不符（历史残留的本地自增临时实例被权威 UID 撞上），则视为残留，
            //    落到下方按权威模板重建覆盖，避免归位到错误的卡。
            var hit = GetInstance(r.Uid);
            if (hit != null && (r == null || hit.Template?.UID == r.TemplateUid)) return hit;
            // 2) 从模板按权威 UID 新建
            EnsureTemplateIndex();
            if (r != null && _templates.TryGetValue(r.TemplateUid, out var tmpl))
            {
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
    }
}
