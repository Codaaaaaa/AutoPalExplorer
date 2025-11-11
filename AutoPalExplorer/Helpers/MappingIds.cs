using System.Collections.Generic;

namespace AutoPalExplorer.Helpers
{
    public static class MapIds
    {
        public const uint PilgrimsTraverse0 = 1281; // 第 1~10朝圣路
        public const uint PilgrimsTraverse1 = 1282; // 第 11~20朝圣路
        public const uint PilgrimsTraverse2 = 1283;
        public const uint PilgrimsTraverse3 = 1284;
        public const uint PilgrimsTraverse4 = 1285;
        public const uint PilgrimsTraverse5 = 1286;
        public const uint PilgrimsTraverse6 = 1287;
        public const uint PilgrimsTraverse7 = 1288;
        public const uint PilgrimsTraverse8 = 1289;
        public const uint PilgrimsTraverse9 = 1290;
        public const uint TheFinalVerse = 1333;      // 卓异的悲寂歼灭战
        public const uint TheFinalVerseQuantum = 1311; // 卓异的悲寂深想战

        /// <summary>
        /// 所有妖宫（Palace of the Dead / Pilgrim’s Traverse）地图ID集合
        /// </summary>
        public static readonly HashSet<uint> AllPilgrimsTraverse = new()
        {
            PilgrimsTraverse0,
            PilgrimsTraverse1,
            PilgrimsTraverse2,
            PilgrimsTraverse3,
            PilgrimsTraverse4,
            PilgrimsTraverse5,
            PilgrimsTraverse6,
            PilgrimsTraverse7,
            PilgrimsTraverse8,
            PilgrimsTraverse9
        };

        /// <summary>
        /// 判断是否在妖宫地图中
        /// </summary>
        public static bool IsPilgrimsTraverse(uint mapId)
        {
            return AllPilgrimsTraverse.Contains(mapId);
        }
    }
}
