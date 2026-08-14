using Godot;
using Script.Data.Cards;
using Script.Game.Entities;
using Script.Enums;
using System.Collections.Generic;

public partial class CardTest : Node
{
    public override void _Ready()
    {
        GD.Print("========== 开始卡牌数据测试 ==========");



        // ==================== 第一步：加载卡牌资源 ====================
        GD.Print("【步骤1】尝试加载 PlayerCardData 资源 ...");
        string path = "res://Cards/Data/PlayerCards/JC/JC005_liejieshu.tres";
        var cardData = ResourceLoader.Load<PlayerCardData>(path);

        if (cardData == null)
        {
            GD.PrintErr($"❌ 加载失败！未找到资源：{path}");
            return;
        }
        GD.Print($"✅ 加载成功！资源类型: {cardData.GetType().Name}");

        // ==================== 第二步：读取基本属性 ====================
        GD.Print("【步骤2】读取卡牌基本信息 ...");
        GD.Print($"  卡名: {cardData.CardName}");
        GD.Print($"  UID: {cardData.UID}");
        GD.Print($"  费用: {(cardData.Cost >= 0 ? cardData.Cost.ToString() : "不可直接打出")}");
        GD.Print($"  颜色: [{string.Join(", ", cardData.Colors)}]");
        GD.Print($"  魔法领域: [{string.Join(", ", cardData.MagicDomains)}]");
        GD.Print($"  类别标志: {cardData.CategoryFlag}");
        GD.Print($"  防御: {(cardData.Defense >= 0 ? cardData.Defense.ToString() : "无防御")}");
        GD.Print($"  调查: {cardData.Investigation}, 先手调查: {cardData.FirstStrikeInvestigation}");
        GD.Print($"  战斗: {cardData.Combat}, 先手战斗: {cardData.FirstStrikeCombat}");
        GD.Print($"  势力: {cardData.Power}, 先手势力: {cardData.FirstStrikePower}");

        // ==================== 第三步：忠诚需求验证 ====================
        GD.Print("【步骤3】验证忠诚需求 ...");
        // 模拟资产区颜色：假设有 1 红色
        var availableColors = new Dictionary<CardColor, int> { { CardColor.Yellow, 1 } };
        var availableDomains = new Dictionary<MagicDomain, int>();
        bool loyal = cardData.ValidateLoyalty(availableColors, availableDomains);
        GD.Print($"  模拟资产颜色: 黄色×1, 领域: 无");
        GD.Print($"  忠诚验证结果: {(loyal ? "✅ 满足" : "❌ 不满足")}");

        // 若卡牌有领域需求，再测一次模拟
        if (cardData.DomainLoyaltyRequirements.Count > 0)
        {
            availableDomains.Add(MagicDomain.Mind, 1); // 假设有1心灵
            bool loyal2 = cardData.ValidateLoyalty(availableColors, availableDomains);
            GD.Print($"  补充模拟领域: 心灵×1");
            GD.Print($"  再次验证结果: {(loyal2 ? "✅ 满足" : "❌ 不满足")}");
        }





        // ==================== 第四步：实例化并计算能力 ====================
        GD.Print("【步骤4】创建 CardInstance 并重置能力值 ...");
        var instance = new CardInstance(cardData);

        // 测试先手
        instance.ResetToBaseValues(isFirstPlayer: true);
        GD.Print($"  先手状态：调查={instance.CurrentInvestigation}, 战斗={instance.CurrentCombat}, 势力={instance.CurrentPower}, 防御={instance.CurrentDefense}");

        // 测试后手
        instance.ResetToBaseValues(isFirstPlayer: false);
        GD.Print($"  后手状态：调查={instance.CurrentInvestigation}, 战斗={instance.CurrentCombat}, 势力={instance.CurrentPower}, 防御={instance.CurrentDefense}");

        // ==================== 第五步：伤害与死亡逻辑 ====================
        GD.Print("【步骤5】测试伤害与死亡判定 ...");
        instance.ResetToBaseValues(isFirstPlayer: false); // 重置为后手基础
        GD.Print($"  当前防御: {instance.CurrentDefense} (原始: {cardData.Defense})");

        // 添加伤害
        instance.AddCounter("Damage", 2);
        GD.Print($"  添加 2 点伤害标记，当前伤害: {instance.Counters["Damage"]}");
        bool dead = instance.IsDead();
        GD.Print($"  死亡判定: {(dead ? "⚠️ 已死亡" : "✅ 存活")}");

        // 再加伤害
        instance.AddCounter("Damage", 3);
        GD.Print($"  再添加 3 点伤害，总伤害: {instance.Counters["Damage"]}");
        dead = instance.IsDead();
        GD.Print($"  死亡判定: {(dead ? "⚠️ 已死亡" : "✅ 存活")}");

        // ==================== 第六步（可选）：秘社限制测试 ====================
        // 如果你有 CouncilCardData 资源，可以继续测试
        GD.Print("【步骤6】秘社限制测试（如果有秘社卡） ...");
        // 示例：加载一个秘社卡并验证卡组
        // 这里只做演示，你可以自行扩展

        GD.Print("========== 测试全部完成 ==========");
    }
}