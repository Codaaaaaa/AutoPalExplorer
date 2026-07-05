using System.Collections.Generic;

namespace AutoPalExplorer.Helpers;

/// <summary>
/// 各职业的“远程开怪”技能表。Key 为 ClassJob 的 RowId，Value 为技能名（用于 /ac "技能名"）。
/// 没有列出的职业（如武僧/龙骑/忍者/武士/蝰蛇）暂时没有远程开怪技能，会退回“走到脸上交给 BMRAI”。
/// </summary>
public static class PullActions
{
    public static readonly IReadOnlyDictionary<uint, string> ByJob = new Dictionary<uint, string>
    {
        [19] = "投盾",       // 骑士 PLD
        [21] = "飞斧",       // 战士 WAR
        [32] = "伤残",       // 暗黑骑士 DRK
        [37] = "闪雷弹",     // 绝枪战士 GNB
        [20] = "连击",       // 武僧 MNK  —— 暂无
        [22] = "精准刺",     // 22 龙骑士 DRG —— 暂无
        [20] = "双刃旋",     // 30 忍者 NIN   —— 暂无
        [34] = "刃风",       // 34 武士 SAM   —— 暂无
        [39] = "切割",       // 钐镰客 RPR
        [41] = "咬噬尖齿",   // 41 蝰蛇剑士 VPR —— 暂无
        [23] = "强力射击",   // 吟游诗人 BRD
        [31] = "分裂弹",     // 机工士 MCH
        [38] = "瀑泻",       // 舞者 DNC
        [25] = "崩溃",       // 黑魔法师 BLM
        [27] = "毁灭",       // 召唤师 SMN
        [35] = "摇荡",       // 赤魔法师 RDM
        [42] = "火炎之红",   // 绘灵法师 PCT
        [24] = "疾风",       // 白魔法师 WHM
        [28] = "毁坏",       // 学者 SCH
        [33] = "烧灼",       // 占星术士 AST
        [40] = "注药",       // 贤者 SGE
    };
}
