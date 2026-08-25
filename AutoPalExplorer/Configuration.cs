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
        /// <summary>当前配置版本；改了默认值又想让老配置跟着走时 +1，并在 <see cref="Migrate"/> 里处理。</summary>
        public const int CurrentVersion = 3;

        // 财运亨通模式的默认值（同时用于老配置的迁移，见 Migrate）
        public const string DefaultFortuneTeleportCommand = "/vnav moveto";
        public const float DefaultFortuneTeleportYOffset = 0.0f;
        public const int DefaultFortuneEnterDelaySeconds = 1;
        public const int DefaultFortuneReenterDelaySeconds = 1;
        public const int DefaultFortuneDetectWaitSeconds = 2;
        public const int DefaultFortuneMemberExtraWaitSeconds = 0;
        public const int DefaultFortuneTreasureWaitSeconds = 50;
        public const int DefaultFortuneRoomScanWaitMs = 5000;
        public const int DefaultFortuneSearchTimeoutSeconds = 50;

        // 进本 / 退本时要发的指令（多条用 ; 或换行隔开）
        public const string DefaultFortuneEnterCommands =
            "/i-ching-commander y_adjust -7 true;/i-ching-commander speed 0.3";
        public const string DefaultFortuneLeaveCommands =
            "/i-ching-commander y_adjust 0 true;/i-ching-commander speed 0";

        public int Version { get; set; } = CurrentVersion;

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

        // 避雷圈半径（只管「导航目标点」别贴着陷阱，见 TrySafeMoveTo）
        public float TrapAvoidRadius { get; set; } = 1.5f;

        // ===== 路径级避陷阱（把 vnav 算出来的整条路线绕开陷阱，而不只是终点）=====
        // 只在普通模式生效；财运亨通模式全程用传送指令，不走这套逻辑。
        public bool AvoidTrapsOnPath { get; set; } = true;

        // 绕行时给每个陷阱建的危险圈半径（米）。比 TrapAvoidRadius 大一点，
        // 因为路上是「擦着过去」，留的余量要够角色转向和减速。
        public float TrapPathAvoidRadius { get; set; } = 2.5f;

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

        // ===== 财运亨通模式 =====
        // 专门刷「埋藏的宝藏」的模式：用 1-10 层带「魔陶器：感知宝藏」的存档反复进 11 层，
        // 队长用感知宝藏 -> 有宝藏就传送过去踩出来 -> 全队退本重进（不删存档），一直循环。
        public bool FortuneMode { get; set; } = false;

        // 传送指令前缀，最终发出的是「{前缀} x y z」
        public string FortuneTeleportCommand { get; set; } = DefaultFortuneTeleportCommand;

        // 传送时对坐标的 Y 轴修正（实际传送到 y - 该值）
        public float FortuneTeleportYOffset { get; set; } = DefaultFortuneTeleportYOffset;

        // 进本后先等几秒再用感知宝藏（等加载 / 队友进齐）
        public int FortuneEnterDelaySeconds { get; set; } = DefaultFortuneEnterDelaySeconds;

        // 进本后要发的指令（多条用 ; 或换行隔开），在读条结束、进本缓冲走完之后发
        public string FortuneEnterCommands { get; set; } = DefaultFortuneEnterCommands;

        // 退本时要发的指令（多条用 ; 或换行隔开），在请求退本之前发
        public string FortuneLeaveCommands { get; set; } = DefaultFortuneLeaveCommands;

        // 用完感知宝藏后等多久还没收到「似乎有宝藏」/ 看到宝藏点，就判定「本层没宝藏」
        public int FortuneDetectWaitSeconds { get; set; } = DefaultFortuneDetectWaitSeconds;

        // 队员比队长多等几秒再退本（队员不用魔陶器，只跟着走）
        public int FortuneMemberExtraWaitSeconds { get; set; } = DefaultFortuneMemberExtraWaitSeconds;

        // 传送到宝藏点后，最多站着等多久（超时也退本重进）
        public int FortuneTreasureWaitSeconds { get; set; } = DefaultFortuneTreasureWaitSeconds;

        // 「这一朝圣路似乎有宝藏」但宝藏不在视野里时，逐个房间传送搜索：
        // 每传送到一个房间后等多久（等物件加载出来）
        public int FortuneRoomScanWaitMs { get; set; } = DefaultFortuneRoomScanWaitMs;

        // 逐房间搜索的总超时，超过就放弃这一趟，退本重进
        public int FortuneSearchTimeoutSeconds { get; set; } = DefaultFortuneSearchTimeoutSeconds;

        // 退本回到入口地图后，等几秒再开始重新进本
        // （刚落地就去点入口，菜单往往还没能弹出来，反而要等任务超时，白白卡几十秒）
        public int FortuneReenterDelaySeconds { get; set; } = DefaultFortuneReenterDelaySeconds;

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

        /// <summary>
        /// 老配置迁移。财运亨通模式还在调参阶段，1 -> 2 直接把这几项拉回新默认值，
        /// 免得旧配置里存着上一版的传送指令 / 等待时间。
        /// </summary>
        public void Migrate()
        {
            if (Version >= CurrentVersion)
                return;

            FortuneTeleportCommand = DefaultFortuneTeleportCommand;
            FortuneTeleportYOffset = DefaultFortuneTeleportYOffset;
            FortuneEnterDelaySeconds = DefaultFortuneEnterDelaySeconds;
            FortuneReenterDelaySeconds = DefaultFortuneReenterDelaySeconds;
            FortuneDetectWaitSeconds = DefaultFortuneDetectWaitSeconds;
            FortuneMemberExtraWaitSeconds = DefaultFortuneMemberExtraWaitSeconds;
            FortuneTreasureWaitSeconds = DefaultFortuneTreasureWaitSeconds;
            FortuneRoomScanWaitMs = DefaultFortuneRoomScanWaitMs;
            FortuneSearchTimeoutSeconds = DefaultFortuneSearchTimeoutSeconds;
            FortuneEnterCommands = DefaultFortuneEnterCommands;
            FortuneLeaveCommands = DefaultFortuneLeaveCommands;

            Version = CurrentVersion;
            Save();
        }

        public void Save()
        {
            pluginInterface?.SavePluginConfig(this);
        }
    }
}
