# -*- coding: utf-8 -*-
"""解析 Card.xml，按行首句式严格分类效果，并统计复合结构/目标模式/费用模式。"""
import re
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict

PATH = "d:/MYPROJECT/UWPRO/Card.xml"

tree = ET.parse(PATH)
root = tree.getroot()
cards = root.find("cards").findall("card")

# 严格按行首句式分类（最长优先）
def classify_prefix(ln):
    order = [
        "进场触发", "离场触发", "翻面触发", "游戏开始时", "抽到时", "攻击触发",
        "防御触发", "快速行动", "持续", "行动", "部署", "暗藏", "响应",
        "强制触发", "触发", "强化",
    ]
    for p in order:
        if ln.startswith(p):
            return p
    if ln.startswith("◆") or "作为响应" in ln:
        return "响应(◆)"
    if ln.startswith("每当") or ln.startswith("当"):
        return "触发(当/每当)"
    return "未前缀/其他"

ACTION_VERBS = [
    "横置", "重置", "移回", "抽", "造成", "获得", "失去", "消灭", "移除", "生成",
    "选择", "查看", "展示", "牺牲", "支付", "翻面", "潜伏", "现身", "弃", "置入",
    "放置", "指示物", "附属", "调查", "战斗", "力量", "增加", "减少", "操控",
    "拥有", "胜利", "失败", "结束", "开始", "复制", "放逐", "洗", "摧毁", "翻回",
    "伤害", "防止", "回复", "保护", "交换", "冻结", "解锁", "终止", "饮血",
    "势力标志", "时间标志", "锁定标志", "推进标志", "名誉点", "威名",
]

prefix_count = Counter()
verb_count = Counter()
sample = defaultdict(list)
total_cards = 0
cards_with_effect = 0
total_lines = 0

# 复合结构 & 目标模式
compound_choice = 0      # 包含 "·" 或 "选择一项"
conditional_if = 0       # 包含 "若"/"如果"
cost_pattern = 0         # 含 行动,数字 / 快速行动,数字 / 支付/牺牲 等费用
target_mention = 0       # 含 目标

for c in cards:
    total_cards += 1
    text_el = c.find("text")
    if text_el is None or not text_el.text:
        continue
    text = text_el.text.strip()
    idx = text.find("------")
    simp = text[:idx] if idx >= 0 else text
    simp = simp.strip()
    if not simp:
        continue
    cards_with_effect += 1
    lines = [l.strip() for l in simp.split("\n") if l.strip()]
    for ln in lines:
        if re.match(r"^［.*］", ln) and not re.search(r"触发|持续|行动", ln):
            continue
        if ln.startswith(("智谋", "智謀", "能力图标", "能力圖示", "防御力", "防禦力")):
            continue
        total_lines += 1
        pre = classify_prefix(ln)
        prefix_count[pre] += 1
        if len(sample[pre]) < 3:
            sample[pre].append(f"{c.find('name').text} :: {ln}")
        for v in ACTION_VERBS:
            if v in ln:
                verb_count[v] += 1
        if ("·" in ln) or ("选择一项" in ln) or ("选择" in ln and "：·" in ln):
            compound_choice += 1
        if ("若" in ln) or ("如果" in ln) or ("每" in ln and "当" in ln):
            conditional_if += 1
        if re.search(r"(行动|快速行动)[，,]\s*\d", ln) or ("支付" in ln) or ("牺牲" in ln) or ("，弃" in ln) or ("横置" in ln and "：" in ln):
            cost_pattern += 1
        if "目标" in ln:
            target_mention += 1

print("===== 统计概览 =====")
print(f"总卡数: {total_cards} | 含效果文本卡数: {cards_with_effect} | 提取效果行数: {total_lines}")
print()
print("===== 效果大类（行首句式，严格分类）频次 =====")
for k, v in prefix_count.most_common():
    print(f"  {k:<14} {v:>5}")
print()
print("===== 复合结构 / 条件 / 费用 / 目标 模式 =====")
print(f"  含选择项(·/选择一项): {compound_choice}")
print(f"  含条件(若/如果/每当): {conditional_if}")
print(f"  含费用(行动,数字/支付/牺牲/弃): {cost_pattern}")
print(f"  提及'目标'(需选目标): {target_mention}")
print()
print("===== 原子动作动词频次 (Top 30) =====")
for k, v in verb_count.most_common(30):
    print(f"  {k:<8} {v:>5}")
print()
print("===== 各大类代表样例 =====")
for k in sorted(sample.keys()):
    print(f"--- {k} ---")
    for s in sample[k]:
        print(f"  {s}")
