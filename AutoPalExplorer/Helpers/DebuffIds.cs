namespace AutoPalExplorer.Helpers
{
    public static class DebuffIds
    {
        // Mimic curse（诅咒：拟态怪 - 怨念）
        public const ushort DebuffCurse = 1087;

        // Debuffs removable by Pomander of Serenity（净化可解除的debuff列表）
        public static readonly ushort[] DebuffsSerenity =
        {
            1089, // Maximum HP Down（最大体力减少）
            1090, // Damage Down（伤害降低）
            1094, // Item Disable（禁止使用道具）
            1097, // No Natural Regen（禁止体力自然恢复）
        };
    }
}
