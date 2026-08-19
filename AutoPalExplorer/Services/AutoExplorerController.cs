using System;
using System.Numerics;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json.Serialization;

using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;

using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using static ECommons.GenericHelpers;

using SQLitePCL;

using AutoPalExplorer.Helpers;

namespace AutoPalExplorer.Services;

public sealed partial class AutoPalController
{
    private readonly IClientState clientState;
    private readonly Navigator navigator;
    private readonly ExitDetector exitDetector;
    private readonly WallFollower wallFollower;
    private readonly IObjectTable objectTable;
    private readonly ICommandManager commandManager;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly PomanderManager pomanderManager;

    public bool IsRunning { get; private set; }

    // 最近一次 AI 意图（用于在配置窗口“内部变量”里查看，替代满屏 xllog）
    private string lastIntent = "空闲";
    private DateTime lastIntentAt = DateTime.MinValue;
    public string LastIntent => lastIntent;
    public DateTime LastIntentAt => lastIntentAt;

    private void SetIntent(string intent)
    {
        lastIntent = intent;
        lastIntentAt = DateTime.Now;
        if (config.devMode)
            log.Information("[AutoPalExplorer][意图] {Intent}", intent);
    }

    private bool bmraiOn;
    private uint lastTerritoryType;
    private bool hasOpenBurinedChest = false;
    private DateTime lastChestInteractAt = DateTime.MinValue;
    // 记录传送装置 / 再生祭坛坐标 & 激活状态
    public Vector3? savedExitPos;
    public Vector3? savedRegenerationPos;
    private bool exitActivatedByChat;
    private bool regenerationActivated;
    private float ExitStopRadius => MathF.Max(0.1f, config.ExitStopRadius);
    private float ChestDoneRadius => MathF.Max(0.1f, config.ChestDoneRadius);
    private float BuriedChestDoneRadius => MathF.Max(0.1f, config.BuriedChestDoneRadius);
    private bool isBossFloor;
    private bool isBossFloorQueueing;
    private double ChallengeIntervalSeconds => MathF.Max(1.0f, config.ChallengeIntervalSeconds);
    private readonly double BossExitInteractDelaySeconds = 2.0;
    private DateTime bossExitReachedAt = DateTime.MinValue;
    private DateTime nextChallengeAttemptAt = DateTime.MinValue;
    // Boss 流程里弹出的确认窗口（DeepDungeonMenu / SelectYesno）点击节流
    private DateTime nextBossAddonFireAt = DateTime.MinValue;
    // 玩家阵亡后点掉复活确认窗口（SelectYesno）的节流
    private DateTime nextReviveAddonFireAt = DateTime.MinValue;

    // ==== 99 / 100 层特殊收尾流程 ====
    // 当前层数（由聊天“第N朝圣路”解析，用于 99/100 层特殊流程）
    private int currentFloor;
    // 99 层：Boss 已清后，前往 2014940 交互 → 等 5 秒 → 走进传送装置
    private bool floor99AltarInteracted;
    private DateTime floor99AltarInteractedAt = DateTime.MinValue;
    private const double Floor99WaitSeconds = 5.0;
    // 100 层：移动到坐标 → 交互 2014754 → 等 3 秒 → 去退出点 2005809 → 点确认窗口退出
    private enum Floor100Stage { MovingToCoord, MovingToInteract, WaitingAfterInteract, GoingToExit, Done }
    private Floor100Stage floor100Stage = Floor100Stage.MovingToCoord;
    private DateTime floor100InteractedAt = DateTime.MinValue;
    private static readonly Vector3 Floor100StartPoint = new(-0.09f, -700.42f, 0.00f);
    private const double Floor100WaitSeconds = 3.0;
    private const float Floor100ReachRadius = 3.0f;

    // ==== 轮次控制：打到指定层停止 + 多轮 ====
    // 存档槽索引：0 = 1号存档(callback 0,0)，1 = 2号存档(callback 1,0)
    private int SaveSlotIndex => config.SaveSlot == 1 ? 1 : 0;
    // 起始层 -> 进本最后一个 SelectString 的选项索引：1=0, 21=1, 31=2, 51=3, 71=4
    private int StartFloorSelectIndex => config.StartFloor switch
    {
        21 => 1,
        31 => 2,
        51 => 3,
        71 => 4,
        _ => 0, // 1 层或未知值
    };
    private int completedRounds;                       // 已完成轮数
    private bool startFreshRound;                       // 下一次进本要“重开一轮”：删存档 + 选起始层
    private bool roundCounted;                          // 本次回到入口是否已计过一轮（防止重复计数）
    private bool saveSlotHasData;                       // 进本时读到的：目标存档槽里是否有存档
    private int entryStartFloorIndex;                   // 本次进本序列最终 SelectString 要选的层索引
    private float EnemySearchRadius => MathF.Max(1.0f, config.EnemySearchRadius);
    // 车头支援：到进战队友多近算“到位”
    private float HelpPartyArriveRadius => MathF.Max(0.5f, config.HelpPartyArriveRadius);
    // 远程开怪
    private float PullRange => config.PullRange;
    private int PullActionIntervalMs => Math.Max(200, config.PullActionIntervalMs);
    private DateTime nextPullActionAt = DateTime.MinValue;

    // 光耀 buff：身上带 status 4708 时，不开 rotation，直接读条使用 GCD 44492（无需目标/靠近）
    private const ushort RadiantStatusId = 4708;
    private const uint RadiantActionId = 44492;
    private DateTime nextRadiantActionAt = DateTime.MinValue;

    // 光耀烛台：≤此距离(米)时抢在宝箱前优先互动，否则等到所有宝箱之后再处理
    private float RadiantCandlestandNearRange => MathF.Max(0f, config.RadiantCandlestandNearRange);
    // 本层是否已点亮过光耀烛台（收到聊天“点亮了光耀烛台”后置真），换层重置，避免重复互动
    private bool hasLitCandle;

    // 移动速度检测：读条技能必须站定才放，否则移动会打断读条
    private Vector3 lastMovePos;
    private DateTime lastMoveAt = DateTime.MinValue;
    private float currentSpeedMps;
    private const float MovingSpeedThreshold = 0.3f; // m/s，低于此值视为已站定
    private bool IsPlayerMoving => currentSpeedMps > MovingSpeedThreshold;

    private void UpdateMovementTracker(Vector3 pos)
    {
        var now = DateTime.UtcNow;
        if (lastMoveAt != DateTime.MinValue)
        {
            var dt = (now - lastMoveAt).TotalSeconds;
            if (dt > 0.0001)
            {
                var dx = pos.X - lastMovePos.X;
                var dz = pos.Z - lastMovePos.Z;
                currentSpeedMps = (float)(MathF.Sqrt(dx * dx + dz * dz) / dt);
            }
        }

        lastMovePos = pos;
        lastMoveAt = now;
    }

    /// <summary>
    /// 解析当前应使用的开怪指令：
    /// - 若手动填了 PullActionCommand，则以它为准（覆盖职业表）；
    /// - 否则按当前职业从 PullActions 表里查技能，返回 /ac "技能名"；
    /// - 该职业没有远程开怪技能则返回 null（退回走到脸上）。
    /// </summary>
    private string? ResolvePullCommand(IPlayerCharacter? player)
    {
        if (!string.IsNullOrWhiteSpace(config.PullActionCommand))
            return config.PullActionCommand;

        if (player is null)
            return null;

        var jobId = player.ClassJob.RowId;
        if (PullActions.ByJob.TryGetValue(jobId, out var skill) && !string.IsNullOrWhiteSpace(skill))
            return $"/ac {skill} <目标>";

        return null;
    }
    private float TrapAvoidRadiusCfg => MathF.Max(0.1f, config.TrapAvoidRadius);
    private int ChestInteractIntervalMs => Math.Max(50, config.ChestInteractIntervalMs);
    private bool nextLevelBool = false;
    private bool hasOpenedNextPilgrimWindow = false;
    public readonly HashSet<ulong> ignoredChestIds = new(); // 需要跳过的宝箱
    private ulong lastChestInteractObjectId = 0;             // 最近一次尝试交互的宝箱ID

    // 锁定中的宝箱（防止在宝箱和门/怪之间来回切）
    private bool hasLockedChest = false;
    private ulong lockedChestId = 0;
    private Vector3 lockedChestPos = Vector3.Zero;
    private const float LockedChestGiveUpDistance = 100.0f;

    // ==== 地宫入口自动化（terr 816，只有车头模式运行）====
    private const uint EntryTerritory = 816;
    private const uint EntryObjectBaseId = 1054942;
    private static readonly Vector3 EntryPoint = new(424.2f, 89.4f, -772.7f);
    private const float EntryReachRadius = 5.0f;
    private bool entrySubmitted = false;                       // 收到“成功发送了参加申请”后为 true
    private DateTime nextEntryFlytoAt = DateTime.MinValue;     // flyto 节流
    private DateTime nextEntryConfirmFireAt = DateTime.MinValue; // SelectYesno 节流
    private readonly TaskManager entryTaskManager = new();

    // 盲踩
    private readonly HashSet<long> ignoredBlindLocations = new();   // 当前层不再去踩的坐标
    public readonly List<Vector3> blindLocations = new();          // 当前层所有 Type=2 点
    public readonly List<Vector3> allBlindLocations = new();          // 当前层所有点，包括陷阱
    private uint blindLocationsTerritory = 0;                        // 这些点对应的 TerritoryType
    private Vector3? currentBlindTarget = null;                      // 正在前往/踩的目标
    private DateTime blindArrivedAt = DateTime.MinValue;            // 到点开始计时
    private Vector3 blindLastProgressPos = Vector3.Zero;            // 上一次检查“卡住”时的位置
    private DateTime blindLastProgressCheckAt = DateTime.MinValue;  // 上一次检查“卡住”的时间
    private static bool sqliteProviderInitialized = false;

    private TimeSpan BlindWaitDuration => TimeSpan.FromSeconds(MathF.Max(2.0f, config.BlindWaitDuration));
    private static readonly TimeSpan BlindStuckTimeout = TimeSpan.FromSeconds(2); // 2 秒没动就判定卡住
    private const float BlindArriveRadius = 0.6f;          // 认为“到点”的半径
    private const float BlindStuckMoveThreshold = 0.2f;    // 判定卡住时允许的移动距离（2D）

    // ==== 联机盲踩同步 ====
    private static readonly HttpClient httpClient = new(); // 整个插件共用一个
    private bool onlineSyncInProgress = false;
    private DateTime nextOnlineFetchAt = DateTime.MinValue;
    private bool IsOnlineMode => config.BlindSyncMode == BlindSyncMode.Online;

    // 联机盲踩：异步“预定点位”状态机
    // 目的：避免在游戏主线程上同步等待服务器响应（高延迟时会卡帧）。
    // 主线程只发起请求并轮询结果，真正的 HTTP 在后台线程完成。
    // 写入顺序（后台线程 finally 内）：先写 reserveSucceeded / reservePendingKey，
    // 最后写 reserveInFlight=false，配合 volatile 的 release 语义保证主线程读到一致结果。
    private volatile bool reserveInFlight = false;   // 是否有一个 reserve 请求在路上
    private volatile bool reserveHasResult = false;  // 是否有一个已完成、待主线程处理的结果
    private volatile bool reserveSucceeded = false;  // 上一次已完成请求是否抢到
    private Vector3? reservePendingCandidate = null; // 正在/刚刚预定的候选点（仅主线程读写）
    private volatile int reserveGeneration = 0;      // 换层/重置时自增，作废在路上的旧请求结果

    // Key
    private readonly IKeyState keyState;
    private readonly IFramework framework;
    private readonly IPartyList partyList;
    private readonly ITargetManager targetManager;


    // 联机盲踩
    private string OnlineServerUrl
        => string.IsNullOrWhiteSpace(config.OnlineServerUrl)
            ? "http://127.0.0.1:8080"
            : config.OnlineServerUrl.TrimEnd('/');

    // 联机 DTO
    private sealed class OnlineIgnoredGetResponse
    {
        [JsonPropertyName("territory")]
        public uint Territory { get; set; }

        [JsonPropertyName("keys")]
        public long[] Keys { get; set; } = Array.Empty<long>();
    }
    private sealed class OnlineIgnoredAddRequest
    {
        [JsonPropertyName("api_key")]
        public string ApiKey { get; set; } = "";

        [JsonPropertyName("territory")]
        public uint Territory { get; set; }

        [JsonPropertyName("keys")]
        public long[] Keys { get; set; } = Array.Empty<long>();

        [JsonPropertyName("test")]
        public string Test { get; set; } = "Coda";
    }

    private sealed class OnlineIgnoredClearRequest
    {
        [JsonPropertyName("api_key")]
        public string ApiKey { get; set; } = "";

        [JsonPropertyName("territory")]
        public uint Territory { get; set; }
    }

    private sealed class OnlineIgnoredReserveResponse
    {
        [JsonPropertyName("territory")]
        public uint Territory { get; set; }

        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("added")]
        public long[] Added { get; set; } = Array.Empty<long>();

        [JsonPropertyName("taken")]
        public long[] Taken { get; set; } = Array.Empty<long>();

        [JsonPropertyName("count")]
        public int Count { get; set; }
    }


    private static void EnsureSQLiteProvider()
    {
        if (sqliteProviderInitialized)
            return;

        // 使用 e_sqlite3 bundle 作为 provider
        raw.SetProvider(new SQLite3Provider_e_sqlite3());
        raw.FreezeProvider(); // 可选，但建议固定 provider，避免被改
        sqliteProviderInitialized = true;
    }

    public AutoPalController(
        IClientState clientState,
        Navigator navigator,
        ExitDetector exitDetector,
        WallFollower wallFollower,
        IObjectTable objectTable,
        ICommandManager commandManager,
        ICondition condition,
        IPluginLog log,
        Configuration config,
        PomanderManager pomanderManager,
        IKeyState keyState,
        IFramework framework,
        IPartyList partyList,
        ITargetManager targetManager)
    {
        this.clientState = clientState;
        this.navigator = navigator;
        this.exitDetector = exitDetector;
        this.wallFollower = wallFollower;
        this.objectTable = objectTable;
        this.commandManager = commandManager;
        this.condition = condition;
        this.log = log;
        this.config = config;
        this.pomanderManager = pomanderManager;
        this.keyState = keyState;
        this.framework = framework;
        this.partyList = partyList;
        this.targetManager = targetManager;
    }

    public void Start()
    {
        if (IsRunning)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] Start 调用被忽略：已经在运行中。");
            return;
        }

        if (objectTable.LocalPlayer is null)
        {
            log.Warning("[AutoPalExplorer] 无法启动：没有本地玩家。");
            return;
        }

        IsRunning = true;
        lastTerritoryType = clientState.TerritoryType;
        wallFollower.Reset();
        exitDetector.Reset();
        navigator.Stop();
        EnsureBmraiOff();
        EnsureRotationOff();
        isBossFloor = false;
        isBossFloorQueueing = false;
        ignoredChestIds.Clear();
        lastChestInteractObjectId = 0;
        bossExitReachedAt = DateTime.MinValue;
        ResetBlindWalkState();
        ResetStaticObjectsState();
        ResetRoomState();
        ClearLockedChest();
        // 99/100 层特殊流程
        currentFloor = 0;
        hasLitCandle = false;
        ResetFloorSpecialState();
        // 地宫入口
        entrySubmitted = false;
        entryTaskManager.Abort();
        // 轮次控制：启用时首次进本即“重开一轮”（删旧存档 + 选起始层）
        completedRounds = 0;
        roundCounted = false;
        saveSlotHasData = false;
        entryStartFloorIndex = 0;
        startFreshRound = config.EnableRoundLimit;

        if (config.devMode)
            log.Information("[AutoPalExplorer] 已启动，当前地城 Territory={TerritoryType}。", clientState.TerritoryType);
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] Stop 调用被忽略：当前未运行。");
            return;
        }

        IsRunning = false;
        navigator.Stop();
        EnsureBmraiOff();
        EnsureRotationOff();
        ignoredChestIds.Clear();
        lastChestInteractObjectId = 0;
        isBossFloor = false;
        isBossFloorQueueing = false;
        bossExitReachedAt = DateTime.MinValue;
        ResetBlindWalkState();
        ResetStaticObjectsState();
        ResetRoomState();
        ClearLockedChest();
        // 99/100 层特殊流程
        currentFloor = 0;
        hasLitCandle = false;
        ResetFloorSpecialState();
        // 地宫入口
        entryTaskManager.Abort();
        // 轮次控制
        startFreshRound = false;
        roundCounted = false;

        if (config.devMode)
            log.Information("[AutoPalExplorer] 已停止。");
    }






    public void Update()
    {
        if (!IsRunning)
            return;

        var player = objectTable.LocalPlayer;
        if (player is null)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] Update：本地玩家为空，等待。");
            return;
        }

        // 0. 最高优先级：先看自己有没有死。死了就只处理复活确认，不执行任何其它逻辑。
        if (HandlePlayerDeath(player))
            return;

        // 地宫入口地图（terr 816）：只有车头模式自动进本，不走下面的探索/停止逻辑
        if (clientState.TerritoryType == EntryTerritory)
        {
            HandleDungeonEntry(player.Position);
            return;
        }

        // 离开入口地图后重置提交状态，方便下一趟再次进本
        entrySubmitted = false;

        // 如果不在目标地图则结束
        if (!MapIds.IsPilgrimsTraverse(clientState.TerritoryType))
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] 不在目标地图 (Territory={TerritoryType})，停止运行。", clientState.TerritoryType);
            Stop();
            return;
        }
        // 在正确地图且正在运行时，如果是联机模式，每 2 秒从服务器拉一次 ignoredBlindLocations
        // TickOnlineIgnoredSync();

        var inCombat = condition[ConditionFlag.InCombat];
        var pos = player.Position;
        UpdateMovementTracker(pos); // 每帧刷新移动速度（用于读条技能站定判断）
        if (config.devMode)
        {
            log.Information("[AutoPalExplorer] Update Tick：Territory={Territory}, 位置=({X:0.00}, {Y:0.00}, {Z:0.00})，BMRAI={Bmrai}，NavigatorBusy={Busy}",
                clientState.TerritoryType, pos.X, pos.Y, pos.Z, bmraiOn, navigator.IsBusy);
        }

        UpdateStaticObjectPositions();
        TickRoomState(player, pos);          // 房间图：换层检测 + 房间中心在线标定

        // ===== 每帧状态维护（仅副作用，不接管本帧） =====
        HandleLevelChangeReset();            // 换层重置（nextLevelBool 触发）
        TickPomanderUsage();                 // 0.5 使用魔陶器

        // ===== 优先级处理链：从高到低，任意一个接管本帧即 return =====

        // 光耀 buff（status 4708）：只要有这个 buff 就直接使用读条 GCD 44492，优先于战斗/探索
        if (HandleRadiantBuff(player))
            return;

        // 1. 本地进战：交给 BMRAI 并暂停导航（脱战则关 BMRAI 后继续往下）
        if (HandleCombat(inCombat))
            return;

        // Boss 房 / 排队“挑战下一朝圣路”
        if (HandleBossFloorPhase(pos))
            return;

        // 100 层特殊收尾流程（从 99 层传送过来后，非 Boss 房）
        if (currentFloor == 100 && HandleFloor100(pos))
            return;

        // 2. 更新导航 & 目标检测
        navigator.Update();
        exitDetector.Update(pos);

        var currentTarget = navigator.CurrentTarget;

        if (config.devMode)
        {
            log.Information("[AutoPalExplorer] 状态：HasChest={HasChest}, HasActiveExit={HasActiveExit}, HasInactiveExit={HasInactiveExit}, 当前导航目标={Target}",
                exitDetector.HasChest, exitDetector.HasActiveExit, exitDetector.HasInactiveExit,
                currentTarget is null ? "null" : $"({currentTarget.Value.X:0.00},{currentTarget.Value.Y:0.00},{currentTarget.Value.Z:0.00})");
        }

        // 3. 探索优先级决策（命中即接管本帧）
        if (HandleRegenerationAltar(pos, currentTarget)) return;                       // 2.1 再生祭坛（复活阵亡队友）
        if (TryHelpPartyInCombat(pos, player, currentTarget)) return;                   // 2.2 支援进战的队友
        if (HandleRadiantCandlestand(pos, currentTarget, RadiantCandlestandNearRange)) return; // 2.3 光耀烛台（≤30m 抢在宝箱前）
        if (HandleBuriedChest(pos, currentTarget)) return;                             // 3.0 埋藏的宝藏
        if (HandleBlindBuriedSearch(pos)) return;                                      // 3.0b 盲踩埋藏宝藏
        if (HandleLockedChestPriority(pos, currentTarget)) return;                     // 3.0c 锁定中的普通宝箱
        if (HandleNormalChest(pos, currentTarget)) return;                             // 3.1 普通宝箱
        if (HandleRadiantCandlestand(pos, currentTarget, float.MaxValue)) return;      // 3.1b 光耀烛台（宝箱之后，任意距离）
        if (HandleActiveExit(pos, currentTarget)) return;                              // 3.2 激活的传送装置
        if (HandleNearestEnemy(pos, player, currentTarget)) return;                    // 3.3 最近怪物

        // 3.4 都没有更高优先级：按房间图探索；房间图不可用时才退回贴墙
        if (!HandleRoomExplore(pos))
            HandleWallFollow();
    }

    // ===== 本类按职责拆分为多个 partial 文件（同一个类，见 AutoExplorerController.*.cs） =====
    //   .Priorities    – Update 每帧的优先级处理器（战斗 / 再生祭坛 / 宝箱 / 传送 / 贴墙…）
    //   .BossFloor     – Boss 房逻辑与地宫入口（terr 816）自动化
    //   .Chests        – 宝箱开启 / 锁定 / 查找
    //   .Combat        – 找怪、开怪、支援进战队友
    //   .Navigation    – 导航、避陷阱、物件交互、静态物件坐标
    //   .Rooms         – 房间图探索 / 房间中心标定 / 按房间顺序盲踩
    //   .BlindSearch   – 盲踩埋藏宝藏（PalacePal 数据）
    //   .Online        – 联机盲踩同步（HTTP）
    //   .Commands      – BMRAI / 游戏指令 / 跟随
    //   .Notifications – 聊天事件通知入口（Notify*）
}
