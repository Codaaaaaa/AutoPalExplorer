using Dalamud.Configuration;
using Dalamud.Plugin;

namespace AutoPalExplorer
{
    public enum AutoMode
    {
        Explore = 0,
        Follow = 1
    }
    public enum BlindSyncMode
    {
        Local = 0,   // 本地模式：只在本机记 ignoredBlindLocations
        Online = 1   // 联机模式：和服务器共享 ignoredBlindLocations
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

        // 埋藏宝藏等待时间
        public float BlindWaitDuration { get; set; } = 3.0f;

        // 未激活门附近「算在门边」的范围
        public float InactiveExitNearRadius { get; set; } = 6.0f;

        // 找怪范围
        public float EnemySearchRadius { get; set; } = 500.0f;

        // ===== 远程开怪（避免脸开） =====
        // 走到怪物这个距离（米）以内就停下用远程技能开怪；填 <=0 表示沿用旧的“走到脸上”行为
        public float PullRange { get; set; } = 5.0f;

        // 开怪指令（例如 /ac "炽热光辉" 或某个宏）。留空则不使用远程开怪，退回“走到脸上交给 BMRAI”
        public string PullActionCommand { get; set; } = "";

        // 开怪指令的最小重复间隔（毫秒），避免每帧狂点
        public int PullActionIntervalMs { get; set; } = 1500;

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

        // 跟随队伍成员
        public int FollowPartyIndex { get; set; } = 1;
        public BlindSyncMode BlindSyncMode { get; set; } = BlindSyncMode.Local;

        // 联机服务器地址，支持自定义
        public string OnlineServerUrl { get; set; } = "http://127.0.0.1:8080";

        // 联机 API Key（需要填 123456 才会通过服务器校验）
        public string OnlineApiKey { get; set; } = "";

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
