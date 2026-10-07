namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC 系列效果统一注册入口。
    /// 加载本系列卡时调用 RegisterAll() 即可；按扩展包划分时，各系列独立注册。
    /// 幂等安全：CardEffectFactory.Register 以最新覆盖，重复调用无副作用。
    /// </summary>
    public static class JCEffects
    {
        public static void RegisterAll()
        {
            JC001_tonglingmaotouying_Effects.Register();
            JC002_jushizhenkanhuren_Effects.Register();
            JC003_weimuhuwei_Effects.Register();
            // 【K17 条件式动作分支】力场法师：进场触发横置目标角色；若已横置则改为移回拥有者手牌。
            JC004_lichangfashi_Effects.Register();
            JC005_liejieshu_Effects.Register();
            JC006_nizhuanyijiezhimen_Effects.Register();
            JC007_changchuntengpaixuezhe_Effects.Register();
            // 【K26】灵能激发：快速行动 +1防御/黑刀，效果声明"不能在分配战斗伤害阶段使用"（阶段排除）。
            JC008_lingnengjifa_Effects.Register();
            // 【P3·动作成果】远东六号科研所：进场把目标角色封印在本卡上，然后遏制1（封印失败则不遏制）。
            JC009_yuandongliuhaokeyansuo_Effects.Register();
            // 注（2026-09 纪律变更）：原先按"任一效果做不了就整卡不接入"撤回本卡；
            // 现改为**效果级推进**（进度表 `JC卡牌效果进度表.md` 一行一个效果）——卡可以部分接入，
            // 未实现的效果在该表里标 ⛔ 即可。故本卡恢复接入："领袖/灵体"两行仍为 ⛔（词条规则未实现）。
            JC010_qingshoufeinandier_Effects.Register();
            // 【P4·领袖】10 张领袖卡的【领袖】词条行（同控制者场上至多 1 个；进场超限立刻选一个牺牲、不入堆叠）。
            JC011_demositinidamofashi_Effects.Register();
            JC023_fanmierdun_Effects.Register();
            JC024_SPTdongshiju_Effects.Register();
            JC037_kelisidinaaifuluode_Effects.Register();
            JC038_lieenaifuluode_Effects.Register();
            JC051_nuoennagesita_Effects.Register();
            // 【K24 玩家目标 + P3·H 检视】监控员：现身触发检视目标玩家（对手）的手牌（第 2 条"持续随时检视"未做）。
            JC057_jiankongyuan_Effects.Register();
            JC052_zhongyandeshikoudi_Effects.Register();
            JC067_yuehanFnateli_Effects.Register();
            JC095_tanggeluni_Effects.Register();
            JC012_weimushouwanghuiji_Effects.Register();
            // 【P3·K2 扩展】貘：牺牲自身 → 横置目标角色/附属 → 消灭其上封印的所有牌。
            JC013_mo_Effects.Register();
            JC014_gongluqishi_Effects.Register();
            JC015_qumoren_Effects.Register();
            // 【K15 多目标 + 确认面板】狭路相逢：目标地区 + 本方/敌方角色（至多 2 个，至少 1 个）→ 移动到目标地区。
            JC021_xialuxiangfeng_Effects.Register();
            JC016_zhixingbujingrui_Effects.Register();
            // 【P3·K2 扩展】除咒师：进场消灭目标角色上的所有敌方附属，该角色本回合获得屏障。
            JC017_chuzhoushi_Effects.Register();
            JC018_morenchuanren_Effects.Register();
            JC020_yinbidefeidao_Effects.Register();
            JC025_lieshouluntanhuiji_Effects.Register();
            JC026_luotenikefu_Effects.Register();
            // 【P3·B 持续前提扩展】街头混混：所在地区有本方标志→白星；有敌方标志→白眼。
            JC084_jietouhunhun_Effects.Register();
            JC029_xinshengxuezu_Effects.Register();
            // 【P3·宿主种类 + 一次性承诺】索亚之书：借阅（结附角色→宿主白眼）/ 收藏（结附秘社→横置付费2，
            // 本回合下一次赢得调查对抗时可抓一张）。
            JC082_suoyazhishu_Effects.Register();
            // 【K23】血族长老：行动2 牌库顶6找吸血鬼→暗藏进场→洗牌；「该能力每回合只能发动一次」。
            JC032_xuezuzhanglao_Effects.Register();
            JC030_juxingbianfu_Effects.Register();
            // 【P3·门禁+分支双条件】唤灵师：另一个本方角色死亡 → 令目标玩家弃牌（吸血鬼则改为随机）；每回合一次。
            JC031_huanlingshi_Effects.Register();
            // 【P3·付费通道隔离】家族传承：本方暗藏者付费翻面（现身）费用 -1（不影响打出）。
            JC033_jiazuchuancheng_Effects.Register();
            JC035_chuanmeimozhou_Effects.Register();
            // 【P3·K4】初拥：持续夺控受结附角色（初拥离场归还拥有者）+ 人类类别改写为吸血鬼；
            // 宿主翻面为暗藏者 ⇒ 初拥按通用规则被消灭（AttachmentRule）。
            JC036_chuyong_Effects.Register();
            JC039_wangzuohuihuiji_Effects.Register();
            // 【K1 动态值 X】安普莎：敌方弃牌时遏制X（X＝此次弃牌数量；X=0 不发动）。
            JC040_anpushamingfudeshinv_Effects.Register();
            // 【P3·多玩家弃牌编排】禁婆：现身强制触发 → 双方各弃一张手牌（各自自选）。
            JC041_jinpo_Effects.Register();
            JC042_morixintu_Effects.Register();
            // 【P3·复合目标限制】纵火狂：进场消灭『结附于秘社或本地区』的附属（两条目标声明 OR）。
            JC043_zonghuokuang_Effects.Register();
            JC044_zainanshizhe_Effects.Register();
            JC045_zhongbaijisi_Effects.Register();
            JC046_wumiantuluzhe_Effects.Register();
            // 【K10 额外费用】"牺牲一个角色"作打出费用（必须支付；无合法牺牲对象则不能打出）
            JC049_shenyuanxiyu_Effects.Register();        // 快速行动：将你牌库底的两张牌置于手中
            // 【K10 额外费用】"支付 X 费"（X＝先选定目标的印刷费用）+ 快速行动：消灭目标印刷费用为 X 的附属
            JC135_yuzhen_Effects.Register();
            // 【K10 额外费用】展示任意数量手牌 → 每张【圣贤】/具【圣】图标之牌费用 -1（可选，不展示＝全额）
            JC081_yaluoer_Effects.Register();
            // 【K1·玩家选择】准备步骤：选一个数字（0～20）→ 展示牌库顶；费用相同入手（不算抓牌），否则置底
            JC083_ruizhidesifenkesi_Effects.Register();
            // 行动1横置：查看目标玩家手牌 → 可选牺牲自身 → 由你指定其一张手牌弃掉
            JC114_haoqideheike_Effects.Register();
            // 【P3·K4 实例级形态】花园地精像：成「地精角色」（角色/附属组合）+ 白眼/黑刀/黑星/1防御 到回合结束
            JC027_huayuandijingxiang_Effects.Register();
            // 【P3·K4 实例级形态】鲜血奴仆：牺牲后以「鲜血」附属身份重新进场并结附于目标吸血鬼角色
            JC028_xianxuenupu_Effects.Register();
            // 【P3·K21】戒严：非【国家机构】角色不可正面打出/移动进入本地区 + 准备步骤"支付1否则牺牲"
            JC064_jieyan_Effects.Register();
            // 【用户 2026-09 口径批】战斗获胜近似判定 / 快速行动授予 / 锁定标记禁现身
            JC022_yinluxianzi_Effects.Register();         // 触发：赢下本卡所在地区的战斗对抗 → 抓一张
            JC034_xinshouzhidao_Effects.Register();       // 【K25】你只能操控至多一个道途（PathRule 与领袖同款）
            JC115_beiyongzijin_Effects.Register();        // 【K19】临时费用池：快速行动横置 → 你获得1
            JC055_annisiduola_Effects.Register();         // 准备步骤：在目标角色或附属上放置一个时间标志
            JC048_zuzhoushalou_Effects.Register();        // 准备步骤强制触发：放标志 → 按地区标志数群体伤害 → ≥3 牺牲自身
            // 【K22·多玩家/对手侧】"由指定玩家自己选择牺牲"（先手先选 → 后选可见 → 一起牺牲）
            JC113_wuxingdaodanjing_Effects.Register();    // 进场强制触发：每位玩家各牺牲一个附属
            JC134_shenpan_Effects.Register();             // 行动：每位玩家牺牲一个资产
            JC065_shenwenxianyiren_Effects.Register();    // 快速行动：选地区 → 对手牺牲该地区一个暗藏者；遏制1
            JC128_tizuiyang_Effects.Register();           // 行动：目标对手移除3标志或牺牲一个角色（A12）
            JC127_zhongju_Effects.Register();             // 行动：清空目标地区势力标志；若该地区时间标志 ≥3 则消灭该地区所有角色
            JC074_tiankongdejiaohui_Effects.Register();   // 快速行动：目标角色 +黑眼×2 +声望 直到回合结束
            // 【K2 群体动作】行动/触发类"对该地区所有角色生效"的卡
            JC047_liehuozhongwei_Effects.Register();      // 行动：选地区 → 对地区内所有角色各 1 点伤害
            JC050_sangzhonghuixiang_Effects.Register();   // 行动：选地区 → 消灭地区内所有角色与暗藏者
            JC056_diqujingyuan_Effects.Register();        // 公开；进场触发：横置本地区所有暗藏者
            JC132_gouzhujingtaizhangbi_Effects.Register();// 行动：选地区 → 横置地区内所有角色
            JC053_mingzhongjiaopaihuiji_Effects.Register();
            JC054_menggusiwangruchong_Effects.Register();
            JC058_jianyidexingjing_Effects.Register();
            // 【P3·E 光环筛选】安全保卫部门（本地区+防御力授予）；噩梦领主（关键词筛选：灵体→白星）。
            JC059_anquanbaoweibumen_Effects.Register();
            JC105_emenglingzhu_Effects.Register();
            JC060_shenmixueguwen_Effects.Register();
            // 【K11 可选支付 + 数值输入框】危机处理特派小组：进入地区时"是否支付1"→ 支付则消灭该地区目标暗藏者。
            JC061_weijichulitepaixiaozu_Effects.Register();
            // 【P3·H 检视】调查档案：检视至多两个目标暗藏者（可 0 个）+ 抓一张牌。
            JC062_diaochadangan_Effects.Register();
            // 【K16 模式分支】警车追捕：选择一项（潜伏目标角色 / 暗藏者回手）；模式在打出付费前选定。
            JC063_jingchezhuibu_Effects.Register();
            JC066_fengmopingselinna_Effects.Register();
            JC068_agesiIV_Effects.Register();
            JC069_jingquan_Effects.Register();
            // 【2026-09 整体复查·补实现】虔心修士：前缀「公开」+「声望」（此前整卡无效果脚本 ⇒ 两个词条都没装载）
            JC070_qianxinxiushi_Effects.Register();
            JC071_awalongyinshi_Effects.Register();
            // 【P3·调查对抗获胜时机】博学的贤者：赢得其所参与的调查对抗 → 获得【声望】直到回合结束。
            JC072_boxuedexianzhe_Effects.Register();
            // 【P3·D】苏族捕梦网：授予**宿主**白眼与屏障（持续授予落点＝结附宿主）。
            JC073_suzubumengwang_Effects.Register();
            JC075_xuedijiuyuandui_Effects.Register();
            JC076_wudaosengren_Effects.Register();
            // 【K1 动态值X + OnDraw 按批】凯拉：每当你抓牌时，在本地区放置 X 个本方势力标志（X＝此次抓牌数量）。
            JC080_kailagediyajiaoshou_Effects.Register();
            JC085_fushengxingshi_Effects.Register();
            // 【K14】窃尸人：进场触发把"本方墓地中的角色牌"移回手牌（墓地目标种类扩展）。
            JC086_qieshiren_Effects.Register();
            JC087_gelunidezousike_Effects.Register();
            JC088_zhiyeshashou_Effects.Register();
            // 【P3·K13】毒血诅咒：宿主 -1 防御 + 白刀/黑刀 + 【威名】（运行期授予的威名可触发）。
            JC089_duxuezuzhou_Effects.Register();
            JC090_linghunqiyue_Effects.Register();
            // 【P3·C】死灵召唤：选地区 + 你墓地角色牌 → 以暗藏者状态置于该地区（K15 多目标 + PutToArea）。
            JC092_silingzhaohuan_Effects.Register();
            // 【P3·C】操控死尸：选地区 + 你墓地角色牌 → 正面进场并额外获得「不死生物」类别。
            JC133_caokongsishi_Effects.Register();
            JC091_mousha_Effects.Register();
            JC097_asiwang_Effects.Register();
            JC099_paihuaideguihun_Effects.Register();
            JC100_jianyahuanxiang_Effects.Register();
            JC102_yaohuo_Effects.Register();
            JC103_sangshilizhidehuajia_Effects.Register();
            // 【P4·预测X】占卜师：进场触发预测3（复用调查对抗分配窗口与联机字段）。
            JC104_zhanbushi_Effects.Register();
            // 【K14 或种类】崩塌的时空：行动，消灭目标附属**或**资产（单目标二选一）。
            JC107_bengtadeshikong_Effects.Register();
            JC108_qiaozhipazuozhimengren_Effects.Register();
            JC109_chenlundebadelei_Effects.Register();
            // 【K24 玩家目标】混沌蝴蝶：两条快速行动以**玩家**为目标（检视其牌库顶 / 洗其牌库）。
            // 【K27 手牌行动】闲人的绿地：手牌中发动（Scope=HandZone）→ 把自身封印在目标角色上。
            JC110_xianrendelvdi_Effects.Register();
            JC111_hundunhudie_Effects.Register();
            JC112_heimao_Effects.Register();
            JC116_keertejumangzuolun_Effects.Register();
            JC119_shoumiren_Effects.Register();
            // 【P3·G 区域转换】庄严圣像：赢取本地区时自身移入资产区成为资产；产业扩张：牌库顶一张 → 资产区。
            JC120_zhuangyanshengxiang_Effects.Register();
            JC126_chanyekuozhang_Effects.Register();
            JC122_hakeyadangsi_Effects.Register();
            JC123_yinhunzhezhudi_Effects.Register();
            JC124_nigulasileimei_Effects.Register();
            JC130_shenruyanjiu_Effects.Register();
            // 【P3·控制权转移 + 到期归还】操控心智：获得目标敌方角色控制权直到回合结束。
            JC129_caokongxinzhi_Effects.Register();
            JC131_kongtouwuzi_Effects.Register();
            // 【P3·K3 第一批】光能盾：快速行动，本回合中为目标角色防止接下来将受到的所有伤害（`PreventDamage` 首次落地）。
            JC078_guangnengdun_Effects.Register();
            // 【P3·K18 第一批】陷入疯狂的人群：持续，本地区略过调查对抗步骤（地区级"略过对抗"静止规则）。
            JC098_xianrufengkuangderenqun_Effects.Register();
            // 【P3·K3 第一批·去处替代】圣婴：本方赢取本地区时置入计分区（而非送回拥有者牌库底）。
            JC079_shengying_Effects.Register();
            // 【P3·K22·牺牲代价第一批】威达的汀巴巴迪：牺牲本地区另一个角色（发动者自选、不可暗藏者）→ 消灭目标角色或暗藏者。
            JC094_weidadetingbabadi_Effects.Register();
            // 【P3·K22·牺牲代价】怨灵咒刃（附属：牺牲一个角色 → 结附宿主获得黑刀黑星到回合结束，每回合两次）
            //                  阿格里帕之犬（快速行动1+横置+牺牲一个角色 → 抓一张牌；未写"另一个"故可牺牲自身）。
            JC093_yuanlingzhouren_Effects.Register();
            JC096_agelipazhiquan_Effects.Register();
            // 【P3·K18 第二批·本回合型】停战协议：选择目标地区，本回合该地区**三步骤全部略过**对抗。
            JC118_tingzhanxieyi_Effects.Register();
            // 【P3·K3·持续型伤害重定向】庇佑圣灵（本地区非灵体本方角色受伤 → 改由本卡承受）
            //                              空鸣御守（受此结附的角色受伤 → 改由本卡承受）。
            JC077_biyoushengling_Effects.Register();
            JC121_kongmingyushou_Effects.Register();
            // 【P3·K20·图标替换＝属性替换】酒神秘仪：目标角色的黑星/白星整块换成黑刀 + 获得灵体（都到回合结束）。
            JC106_jiushenmiyi_Effects.Register();
            // 【P3·K20 收尾】直视深渊：结附宿主**印刷文本框视作空白（失去效果）**且失去全部魔法领域图标。
            JC101_zhishishenyuan_Effects.Register();
            // 【P3·K13·运行期授予激活能力】弥尔顿的镇魔枪：宿主获得黑刀 + 一条"快速行动，横置"的激活能力。
            JC019_mierdundezhenmoqiang_Effects.Register();
            // 【P3·K15 扩展·激活能力的地区目标】红眼航班：行动阶段横置付费，把本方角色移到**另一个**地区（禁原地）。
            JC117_hongyanhangban_Effects.Register();
        }
    }
}
