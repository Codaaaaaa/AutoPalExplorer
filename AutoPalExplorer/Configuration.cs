using Dalamud.Configuration;
using Dalamud.Plugin;

namespace AutoPalExplorer
{
    public enum BlindSyncMode
    {
        Local = 0,   // 本地模式：只在本机记 ignoredBlindLocations
        Online = 1   // 联机模式：和服务器共享 ignoredBlindLocations
    }

    public enum PomanderUsageMode
    {
        SelfBuffOnly = 0, // 仅使用强化自身和防御魔陶器
        All = 1           // 使用全部魔陶器和杜松香
    }

    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 1;

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

        // ===== 队友进战支援 =====
        // 检测到队友进入战斗状态时，停止探索前去支援打怪
        public bool HelpPartyInCombat { get; set; } = true;

        // 到进战队友这个距离（米）内就算“到位”，开始就近打怪
        public float HelpPartyArriveRadius { get; set; } = 3.0f;

        // ===== 远程开怪（避免脸开） =====
        // 走到怪物这个距离（米）以内就停下用远程技能开怪；填 <=0 表示沿用旧的“走到脸上”行为
        public float PullRange { get; set; } = 5.0f;

        // 开怪指令（例如 /ac "炽热光辉" 或某个宏）。留空则不使用远程开怪，退回“走到脸上交给 BMRAI”
        public string PullActionCommand { get; set; } = "";

        // 开怪指令的最小重复间隔（毫秒），避免每帧狂点
        public int PullActionIntervalMs { get; set; } = 1500;

        // 光耀烛台：≤此距离(米)时抢在宝箱前优先互动，否则等到所有宝箱之后再处理
        public float RadiantCandlestandNearRange { get; set; } = 30.0f;

        // 使用魔陶器
        public bool UsingPomander { get; set; } = true;

        // 魔陶器使用范围（仅在 UsingPomander 为 true 时生效）
        // SelfBuffOnly：仅使用强化自身和防御魔陶器
        // All：使用全部魔陶器和杜松香
        public PomanderUsageMode PomanderMode { get; set; } = PomanderUsageMode.All;

        // 避雷圈半径
        public float TrapAvoidRadius { get; set; } = 1.5f;

        // 开箱节流间隔（毫秒）
        public int ChestInteractIntervalMs { get; set; } = 500;

        // 重新排队时间
        public int ChallengeIntervalSeconds { get; set; } = 10;

        // ===== 存档槽位 =====
        // 使用几号存档：0 = 1号存档(callback 0,0)，1 = 2号存档(callback 1,0)
        public int SaveSlot { get; set; } = 0;

        // ===== 轮次控制：打到指定层停止 + 多轮 =====
        // 是否启用（关闭时保持原来的“无限连续刷本”行为）
        public bool EnableRoundLimit { get; set; } = false;

        // 起始层（决定进本时最后一个 SelectString 的选项索引）：1 / 21 / 31 / 51 / 71
        public int StartFloor { get; set; } = 21;

        // 停止层：30 / 50 / 70 / 100，打到该层出本后算“一轮”结束
        public int StopFloor { get; set; } = 50;

        // 要打几轮
        public int RoundCount { get; set; } = 1;

        // 每轮之间（删除存档后、重新排本前）等待秒数
        public int RoundWaitSeconds { get; set; } = 15;

        // 魔陶器使用间隔
        public int PomanderIntervalSeconds { get; set; } = 5000;

        // 数据库路径
        public string PalacePalDbPath { get; set; } = "palace-pal.data.sqlite3";

        // 盲踩目标最大允许距离（只在没启用「按房间顺序盲踩」时生效）
        public float BlindMaxDistance { get; set; } = 25f;

        // ===== 房间图（读游戏内 InstanceContentDeepDungeon.MapData 的 5x5 房间网格）=====

        // 总开关：关掉之后所有房间相关功能都回退到原来的贴墙 / 最近点行为
        public bool UseRoomGraph { get; set; } = true;

        // 用房间图做探索（替代贴墙探索）；房间图没标定好时仍会自动回退贴墙
        public bool RoomExplore { get; set; } = true;

        // 盲踩按房间顺序推进：先搜完当前房间，再按最短路去下一个房间
        public bool BlindRoomOrder { get; set; } = true;

        // 各地图学到的房间间距（米），由插件自动标定并记忆，一般不用手动改
        public Dictionary<uint, float> RoomGridPitchX { get; set; } = new();
        public Dictionary<uint, float> RoomGridPitchZ { get; set; } = new();

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
