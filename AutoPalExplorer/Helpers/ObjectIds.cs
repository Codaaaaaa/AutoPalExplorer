using System.Collections.Generic;

namespace AutoPalExplorer.Helpers
{
    /// <summary>
    /// 存放与地宫对象相关的 DataId 集合。
    /// 包含传送装置、宝箱等的 ID。
    /// </summary>
    public static class ObjectIds
    {
        /// <summary>
        /// 传送装置 DataId（Exit）
        /// </summary>
        public static readonly HashSet<uint> exitIds = new()
        {
            2014756u, // 传送装置
        };

        public static readonly HashSet<uint> regenerationIds = new()
        {
            2014755u, // 再生祭坛
        };

        // 光耀烛台
        public static readonly HashSet<uint> RadiantCandlestandIds = new()
        {
            2014759u,
        };

        /// <summary>
        /// 宝箱 DataId（Chest）
        /// </summary>

        // 铜宝箱
        public static readonly HashSet<uint> BronzeChestIds = new()
        {
            1881u,
            1882u,
            1883u,
            1884u,
            1885u,
            1886u,
            1887u,
            1888u,
            1889u,
            1890u,
            1891u,
            1892u,
            1893u,
            1906u,
            1907u,
            1908u,
        };

        // 银宝箱
        public static readonly HashSet<uint> SilverChestIds = new()
        {
            2007357u,
        };

        // 金宝箱
        public static readonly HashSet<uint> GoldChestIds = new()
        {
            2007358u,
            // 埋的
            2007543u,
        };

        // 埋藏的宝藏
        public static readonly HashSet<uint> BuriedChestIds = new()
        {
            2007542u,
            2007543u,
        };

        // 陷阱
        public static readonly HashSet<uint> TrapIds = new()
        {

            2007182, // 地雷陷阱
            2007183, // 诱饵陷阱 
            2007184, // 弱化陷阱
            2007185, // 妨碍陷阱
            2014939, // 妖灵陷阱
        };

        // Boss房出口
        public const uint BossExitBaseId = 2005809;

        // 下10层入口
        public const uint NextPilgrimNpcBaseId = 2014758;

        // 99 层：打完 Boss 后交互的物件（交互后等 5 秒再走进传送装置传送到 100 层）
        public const uint Floor99AltarBaseId = 2014940;

        // 100 层：移动到指定坐标后交互的物件（交互 3 秒后出现退出点 2005809）
        public const uint Floor100InteractBaseId = 2014754;

        public static bool IsBronzeChest(uint baseId)
            => BronzeChestIds.Contains(baseId);

        public static bool IsSilverChest(uint baseId)
            => SilverChestIds.Contains(baseId);

        public static bool IsGoldChest(uint baseId)
            => GoldChestIds.Contains(baseId);

        public static bool IsBuriedChest(uint baseId)
            => BuriedChestIds.Contains(baseId);

        public static bool IsAnyChest(uint baseId)
            => IsBronzeChest(baseId) || IsSilverChest(baseId) || IsGoldChest(baseId)|| IsBuriedChest(baseId);
    }
}
