using Dalamud.Configuration;
using Dalamud.Plugin;

namespace AutoPalExplorer
{
    public enum AutoMode
    {
        Explore = 0,
        Follow = 1
    }
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 1;

        public AutoMode Mode { get; set; } = AutoMode.Explore;

        // 原有
        public bool EnabledByDefault { get; set; } = false;

        // 是否自动控制 BMRAI (/bmrai on/off)
        public bool UseBmrai { get; set; } = true;

        // 宝箱控制
        public bool OpenBronzeChests { get; set; } = true;
        public bool OpenSilverChests { get; set; } = true;
        public bool OpenGoldChests { get; set; } = false; // 默认金关掉
        public bool BlindChests { get; set; } = false;
        public bool BlindChestsWithTrap { get; set; } = false;

        // 开发者模式
        public bool devMode { get; set; } = false;

        // ===== 自动探索参数（可调） =====

        // 到已激活门附近停止距离
        public float ExitStopRadius { get; set; } = 1.0f;

        // 普通宝箱「到这个距离内就认为到了可以开」
        public float ChestDoneRadius { get; set; } = 2.0f;

        // 埋藏宝藏触发半径
        public float BuriedChestDoneRadius { get; set; } = 1.0f;

        // 未激活门附近「算在门边」的范围
        public float InactiveExitNearRadius { get; set; } = 6.0f;

        // 找怪范围
        public float EnemySearchRadius { get; set; } = 500.0f;
        
        // 使用魔陶器
        public bool UsingPomander { get; set; } = true;

        // 避雷圈半径
        public float TrapAvoidRadius { get; set; } = 1.5f;

        // 开箱节流间隔（毫秒）
        public int ChestInteractIntervalMs { get; set; } = 500;

        // 重新排队时间
        public int ChallengeIntervalSeconds { get; set; } = 10;

        // 魔陶器使用间隔
        public int PomanderIntervalSeconds { get; set; } = 5000;

        // 数据库路径
        public string PalacePalDbPath { get; set; } = "palace-pal.data.sqlite3";

        // 盲踩目标最大允许距离

        public float BlindMaxDistance { get; set; } = 25f;

        [System.NonSerialized]
        private IDalamudPluginInterface? pluginInterface;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            this.pluginInterface = pluginInterface;
        }

        public void Save()
        {
            pluginInterface?.SavePluginConfig(this);
        }
    }
}
