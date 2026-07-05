using System;
using System.Numerics;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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

public sealed class AutoPalController
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
    private float EnemySearchRadius => MathF.Max(1.0f, config.EnemySearchRadius);
    // 车头支援：到进战队友多近算“到位”
    private float HelpPartyArriveRadius => MathF.Max(0.5f, config.HelpPartyArriveRadius);
    // 远程开怪
    private float PullRange => config.PullRange;
    private int PullActionIntervalMs => Math.Max(200, config.PullActionIntervalMs);
    private DateTime nextPullActionAt = DateTime.MinValue;

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

    // 跟车模式
    private bool wasInCombatOnBossFloor = false;
    private bool IsFollowMode => config.Mode == AutoMode.Follow;

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
        ClearLockedChest();
        // 跟车
        wasInCombatOnBossFloor = false;
        // 地宫入口
        entrySubmitted = false;
        entryTaskManager.Abort();

        if (IsFollowMode)
        {
            StartFollowLoop();
        }

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
        ClearLockedChest();
        // 跟车
        wasInCombatOnBossFloor = false;
        // 地宫入口
        entryTaskManager.Abort();
        BreakActWithShift();

        if (config.devMode)
            log.Information("[AutoPalExplorer] 已停止。");
    }

    /// <summary>
    /// 从插件 OnChatMessage 调用，当聊天出现“传送装置已激活”等信息时。
    /// </summary>
    public void NotifyExitActivated()
    {
        exitActivatedByChat = true;
        exitDetector.MarkExitActivated();
        if (config.devMode)
            log.Information("[AutoPalExplorer] 收到传送装置激活通知。");
    }

    public void NotifyRegenerationActivated()
    {
        regenerationActivated = true;
        if (config.devMode)
            log.Information("[AutoPalExplorer] 收到再生祭坛激活通知。");
    }


    public void NotifyBuriedtActivated()
    {
        hasOpenBurinedChest = true;
        if (config.devMode)
            log.Information("[AutoPalExplorer] 收到埋藏的宝藏通知。");
    }
    public void nextLevelActivated()
    {
        nextLevelBool = true;
        hasOpenedNextPilgrimWindow = false;
        bossExitReachedAt = DateTime.MinValue;
        if (config.devMode)
            log.Information("[AutoPalExplorer] 标记换层（nextLevelActivated）。");
    }
    public void NotifyBossFloor()
    {
        isBossFloor = true;
        isBossFloorQueueing = false;
        hasOpenedNextPilgrimWindow = false;
        bossExitReachedAt = DateTime.MinValue;
        nextChallengeAttemptAt = DateTime.MinValue;
        wasInCombatOnBossFloor = false;
        if (IsFollowMode)
        {
            BreakActWithShift();
        }

        if (config.devMode)
            log.Information("[AutoPalExplorer] 检测到 Boss 层聊天提示，启用 Boss 房逻辑。");
    }

    public void NotifyChallengeRequestSent()
    {
        // 收到“成功发送了参加申请”，说明排队申请已发出，可以退出 Boss 流程
        isBossFloor = false;
        isBossFloorQueueing = false;
        hasOpenedNextPilgrimWindow = false;
        bossExitReachedAt = DateTime.MinValue;
        nextChallengeAttemptAt = DateTime.MinValue;
        wasInCombatOnBossFloor = false;
        // 地宫入口：申请已发出，结束入口 UI 流程
        entrySubmitted = true;
        entryTaskManager.Abort();

        if (config.devMode)
            log.Information("[AutoPalExplorer] 收到成功发送参加申请提示，结束 Boss 流程逻辑。");
        
        if (IsFollowMode && IsRunning)
        {
            StartFollowLoop();
        }
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

        // Territory 变化 / 换层重置（由 nextLevelBool 控制）
        if (nextLevelBool)
        {
            nextLevelBool = false;
            lastTerritoryType = clientState.TerritoryType;
            pomanderManager.ResetBuff();
            wallFollower.Reset();
            exitDetector.Reset();
            navigator.Stop();
            // isBossFloor = false;
            hasOpenBurinedChest = false;
            EnsureBmraiOff();
            EnsureRotationOff();
            ignoredChestIds.Clear();
            lastChestInteractObjectId = 0;
            ResetBlindWalkState();
            ResetStaticObjectsState();
            ClearLockedChest();

            // 跟车
            wasInCombatOnBossFloor = false;

            if (config.devMode)
                log.Information("[AutoPalExplorer] 检测到换层，已重置状态 (Territory={Territory}).", clientState.TerritoryType);
        }

        // 0.5 检测状态并且使用魔陶器
        if (!IsFollowMode)
        {
            // 跟车模式不使用魔陶器
            if (!isBossFloor && !isBossFloorQueueing && config.UsingPomander)
                pomanderManager.UsingPomander();
        }
        
        // 1. 战斗状态：交给 BMRAI，暂停导航
        if (IsFollowMode && isBossFloor)
        {
            if (inCombat)
            {
                // Boss 战中
                EnsureBmraiOn();
                wasInCombatOnBossFloor = true;
            }
            else if (wasInCombatOnBossFloor)
            {
                // 刚刚从 Boss 战中脱战：关闭 BMRAI/Rotation
                wasInCombatOnBossFloor = false;
                EnsureBmraiOff();
                EnsureRotationOff();

                if (config.devMode)
                    log.Information("[AutoPalExplorer] 跟车模式：Boss 战结束，已关闭 BMRAI 和 Rotation。");
            }
        }

        // 1. 战斗状态：交给 BMRAI，暂停导航
        if (inCombat)
        {
            SetIntent("战斗中：交给 BMRAI 处理，暂停导航");
            if (config.devMode)
                log.Information("[AutoPalExplorer] 当前处于战斗中，交给 BMRAI 处理移动/战斗。");

            // 非跟车模式：按原逻辑自动开 BMRAI
            if (!bmraiOn)
                EnsureBmraiOn();

            if (navigator.IsBusy && config.devMode)
                log.Information("[AutoPalExplorer] 战斗中停止导航。");

            navigator.Stop();
            return;
        }
        else
        {
            // 非跟车模式：按原逻辑离战斗就关 BMRAI
            if (bmraiOn)
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] 脱离战斗，关闭 BMRAI。");
                EnsureBmraiOff();
            }
            // 跟车模式：BMRAI 的开关由 StartFollowLoop / Boss 战结束那段逻辑控制，这里不动
        }

        // 跟车模式：非 Boss 楼层不执行任何探索逻辑，直接返回
        if (IsFollowMode && !isBossFloor && !isBossFloorQueueing)
        {
            SetIntent("跟车模式：非 Boss 楼层待命");
            if (config.devMode)
                log.Information("[AutoPalExplorer] 跟车模式：非 Boss 楼层，跳过自动探索逻辑。");
            return;
        }

        // 1.1 是否进入boss房间
        if (isBossFloor && !isBossFloorQueueing)
        {
            if (HandleBossFloor(pos))
                return; // 已经处理了（找 Boss 或找出口），不走下面普通逻辑
        }

        // 1.2 已从Boss房传送出，正在处理“挑战下一朝圣路”
        if (isBossFloor && isBossFloorQueueing)
        {
            pomanderManager.ResetBuff();
            pomanderManager.ResetBuriedBuff();

            if (!IsFollowMode)
            {
                // 原来的自动排队行为
                if (HandleBossFloorQueueing(pos))
                    return;
            }
            else
            {
                // EnsureBmraiOff();
                // 跟车模式：Queue 楼层什么都不做，等聊天出现“成功发送了参加申请”
                if (config.devMode)
                    log.Information("[AutoPalExplorer] [Boss层] [Queue] 跟车模式：暂停所有排队逻辑，等待参加申请结果。");
                return;
            }
        }

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

        // ==== 2.1 再生祭坛（已激活 + 队友死亡） ====
        if (regenerationActivated && HasDeadOtherPlayer())
        {
            // 优先尝试拿当前场景中的祭坛对象，并更新缓存坐标
            IGameObject? regenObj = null;
            foreach (var obj in objectTable)
            {
                if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)
                    continue;

                if (!ObjectIds.regenerationIds.Contains(obj.BaseId))
                    continue;

                regenObj = obj;
                savedRegenerationPos = obj.Position;
                break;
            }

            Vector3? regenPos = regenObj?.Position ?? savedRegenerationPos;
            if (regenPos is { } rp)
            {
                var dxR = rp.X - pos.X;
                var dzR = rp.Z - pos.Z;
                var distSqR = dxR * dxR + dzR * dzR;
                var distR = MathF.Sqrt(distSqR);

                if (config.devMode)
                    log.Information("[AutoPalExplorer] 再生祭坛已激活且有队友阵亡，距离祭坛={Dist:0.00}。", distR);

                // 到点附近：停下并尝试交互
                if (distSqR <= ChestDoneRadius * ChestDoneRadius)
                {
                    if (navigator.IsBusy)
                        navigator.Stop();

                    SetIntent("再生祭坛：交互（复活阵亡队友）");
                    if (regenObj is not null && regenObj.IsTargetable)
                    {
                        TryInteractWithObject(regenObj, "再生祭坛");
                    }

                    // 本帧由再生祭坛逻辑接管
                    return;
                }

                // 不在范围内：导航过去（带简单避陷阱）
                if (!navigator.IsBusy || IsDifferentTarget(currentTarget, rp, 1.0f))
                {
                    SetIntent("再生祭坛：前往（有队友阵亡）");
                    if (config.devMode)
                        log.Information("[AutoPalExplorer] 导航至再生祭坛。");

                    TrySafeMoveTo(rp, TrapAvoidRadiusCfg);
                }

                return;
            }
        }
        // ==== 2.2 车头模式：队友进战则前去支援打怪 ====
        // 只有车头（探索）模式支援；本地玩家已进战会在上面的战斗分支直接 return，走不到这里。
        if (!IsFollowMode && TryHelpPartyInCombat(pos, player, currentTarget))
        {
            return;
        }

        // 3. 优先级决策
        // ==== 3.0 埋藏的宝藏（最高优先级） ====
        var buried = FindNearestBuriedChest(pos, 500f);
        if (buried is not null && !hasOpenBurinedChest)
        {
            var dxB = buried.Position.X - pos.X;
            var dzB = buried.Position.Z - pos.Z;
            var distSqB = dxB * dxB + dzB * dzB;
            var distB = MathF.Sqrt(distSqB);

            if (config.devMode)
                log.Information("[AutoPalExplorer] 检测到埋藏的宝藏 BaseId={BaseId}, 距离={Dist:0.00}。",
                    buried.BaseId, distB);

            // 在触发半径内：停止移动，只等触发，直接吃掉这一帧后续逻辑
            if (distSqB <= BuriedChestDoneRadius * BuriedChestDoneRadius)
            {
                SetIntent("埋藏宝藏：已到范围内，等待触发");
                if (navigator.IsBusy)
                {
                    navigator.Stop();
                    if (config.devMode)
                        log.Information("[AutoPalExplorer] 已到埋藏的宝藏区域，停止导航等待触发。");
                }
                else if (config.devMode)
                {
                    log.Information("[AutoPalExplorer] 正在埋藏的宝藏区域内，等待触发。");
                }

                return; // ⭐ 关键：不再执行宝箱/门/贴墙逻辑
            }

            // 不在范围内：作为最高优先级目标引路
            if (!navigator.IsBusy || IsDifferentTarget(currentTarget, buried.Position, 0.5f))
            {
                SetIntent("埋藏宝藏：前往");
                if (config.devMode)
                    log.Information("[AutoPalExplorer] 导航至埋藏的宝藏。");

                TrySafeMoveTo(buried.Position, TrapAvoidRadiusCfg);
            }
            else if (config.devMode)
            {
                log.Information("[AutoPalExplorer] 已在前往埋藏的宝藏路上。");
            }

            return; // ⭐ 有埋藏宝藏就只处理这一件事
        }

        // ==== 3.0b 盲踩埋藏宝藏位置（PalacePal 数据） ====
        // 只有在还没有“埋藏宝藏”Buff 时才盲踩；一旦有 Buff 或已经开过本层埋藏宝藏，就交回上面的 buried 逻辑
        if (config.BlindChests)
        {
            if (config.devMode)
                    log.Information("[AutoPalExplorer] 开始盲踩逻辑");
            if (!pomanderManager.HasBuriedBuff && !hasOpenBurinedChest)
            {
                if (TryHandleBlindBuriedSearch(pos))
                {
                    SetIntent("盲踩：前往疑似埋藏宝藏点");
                    return; // 被盲踩逻辑接管，本帧不走后续宝箱/门/贴墙
                }
            }
        }

        // ==== 3.0c 锁定的普通宝箱（防止宝箱和门/敌人之间来回切） ====
        if (HandleLockedChest(pos, currentTarget))
        {
            SetIntent("前往锁定中的宝箱");
            return;
        }

        // ==== 3.1 宝箱（优先度：有就去） ====
        if (FindNextChestToOpen(pos) is { } chest)
        {
            if (HandleChest(pos, chest, currentTarget))
            {
                SetIntent("前往 / 开启宝箱");
                return;
            }
        }

        // ==== 3.2 激活传送装置（最高优先级） ====
        Vector3? exitPos = null;
        if (exitDetector.HasActiveExit && exitDetector.Exit is { } activeExit)
        {
            exitPos = activeExit.Position;
            savedExitPos = activeExit.Position; // 顺便更新缓存
        }
        else if (exitActivatedByChat && savedExitPos is { } cachedExit)
        {
            // ExitDetector 找不到对象（走太远）时，用聊天激活 + 缓存坐标兜底
            exitPos = cachedExit;
        }

        if (exitPos is { } ep)
        {
            var dx = ep.X - pos.X;
            var dz = ep.Z - pos.Z;
            var distSq = dx * dx + dz * dz;

            if (config.devMode)
                log.Information("[AutoPalExplorer] 使用{Source}的传送装置坐标，距离={Dist:0.00}。",
                    exitDetector.HasActiveExit ? "当前对象" : "缓存",
                    MathF.Sqrt(distSq));

            if (distSq <= ExitStopRadius * ExitStopRadius)
            {
                SetIntent("传送装置：已到达，等待");
                if (navigator.IsBusy)
                {
                    navigator.Stop();
                    if (config.devMode)
                        log.Information("[AutoPalExplorer] 已到达激活传送装置附近，停止导航等待玩家手动交互。");
                }
                return;
            }

            if (!navigator.IsBusy || IsDifferentTarget(currentTarget, ep, 1.0f))
            {
                SetIntent("前往激活的传送装置");
                if (config.devMode)
                    log.Information("[AutoPalExplorer] 导航至激活传送装置位置。");
                navigator.Stop();
                navigator.TryMoveTo(ep);
            }

            return;
        }

        // ==== 3.3 查找最近怪物 ====
        var enemy = FindNearestEnemy(pos, EnemySearchRadius);
        if (enemy is not null)
        {
            EngageEnemy(enemy, pos, player, currentTarget);
            return;
        }

        // ⬇️ 门附近也没怪：交给贴墙逻辑（不要 return，让下面的 wallFollower 分支接管）
        if (config.devMode)
            log.Information("[AutoPalExplorer] 未激活门附近没有敌人，交给贴墙探索逻辑。");


        // ==== 3.4 没有更高优先级 & 当前没有在移动：靠墙探索 ====
        if (!navigator.IsBusy)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] 当前空闲，尝试贴墙探索下一步。");

            if (!wallFollower.TryStep())
            {
                SetIntent("贴墙探索：无可探索路径");
                log.Warning("[AutoPalExplorer] 无可探索路径");
                // Stop();
            }
            else
            {
                SetIntent("贴墙探索：前往下一探索点");
                if (config.devMode)
                    log.Information("[AutoPalExplorer] 贴墙探索已生成新移动目标。");
            }
        }
        else
        {
            SetIntent("沿当前路径移动中");
            if (config.devMode)
                log.Information("[AutoPalExplorer] Navigator 正在移动中，保持当前路径。");
        }
    }

    // ===== Utils =====

    private bool ShouldOpenChest(uint baseId)
    {
        if (ObjectIds.IsBronzeChest(baseId))
            return config.OpenBronzeChests;

        if (ObjectIds.IsSilverChest(baseId))
            return config.OpenSilverChests;

        if (ObjectIds.IsGoldChest(baseId))
            return config.OpenGoldChests;
        
        if (ObjectIds.IsBuriedChest(baseId))
            return true;

        return false;
    }
    private void UpdateStaticObjectPositions()
    {
        foreach (var obj in objectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)
                continue;

            // 传送装置
            if (ObjectIds.exitIds.Contains(obj.BaseId))
            {
                savedExitPos = obj.Position;
            }
            // 再生祭坛
            else if (ObjectIds.regenerationIds.Contains(obj.BaseId))
            {
                savedRegenerationPos = obj.Position;
            }
        }
    }
    private unsafe void TryOpenChest(IGameObject chest)
    {
        if (!ShouldOpenChest(chest.BaseId))
            return;
        
        var player = objectTable.LocalPlayer;
        if (player == null)
            return;

        foreach (var status in player.StatusList)
        {
            if (status.StatusId == DebuffIds.changeBuff)
            {
                log.Debug($"Player has change buff {status.StatusId}, skip chest.");
                return;
            }
        }

        if (chest == null)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] TryOpenChest 防呆：chest 为 null。");
            return;
        }

        if (!chest.IsTargetable)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] TryOpenChest：宝箱不可交互（可能已开/动画中），跳过。");
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - lastChestInteractAt).TotalMilliseconds < ChestInteractIntervalMs)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] TryOpenChest：节流中，跳过本帧开箱请求。");
            return;
        }

        try
        {
            var ptr = (GameObject*)chest.Address;
            if (ptr == null)
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] TryOpenChest：GameObject 指针为空。");
                return;
            }

            var ts = TargetSystem.Instance();
            if (ts == null)
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] TryOpenChest：TargetSystem 实例为空。");
                return;
            }

            lastChestInteractObjectId = chest.GameObjectId;

            ts->InteractWithObject(ptr, false);
            lastChestInteractAt = now;

            if (config.devMode)
            {
                log.Information("[AutoPalExplorer] 已尝试与宝箱交互，位置=({X:0.00}, {Y:0.00}, {Z:0.00})。",
                    chest.Position.X, chest.Position.Y, chest.Position.Z);
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] TryOpenChest 异常：{ex.Message}");
        }
    }

    private static bool IsDifferentTarget(Vector3? current, Vector3 desired, float threshold)
    {
        if (current is null)
            return true;

        var v = current.Value;
        var dx = v.X - desired.X;
        var dz = v.Z - desired.Z;
        return dx * dx + dz * dz > threshold * threshold;
    }

    private void ClearLockedChest()
    {
        if (!hasLockedChest)
            return;

        if (config.devMode)
            log.Information("[AutoPalExplorer] 锁定宝箱已清除：GameObjectId={Id}。", lockedChestId);

        hasLockedChest = false;
        lockedChestId = 0;
        lockedChestPos = Vector3.Zero;
    }

    private void EnsureBmraiOn()
    {
        if (!config.UseBmrai)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] 配置未启用 BMRAI，跳过开启。");
            return;
        }

        if (bmraiOn)
            return;

        bmraiOn = true;
        TryCommand("/bmrai on");
        TryCommand("/rotation Auto");

        if (config.devMode)
            log.Information("[AutoPalExplorer] 已发送 BMRAI 开启指令。");
    }

    private void EnsureBmraiOff()
    {
        if (!bmraiOn)
            return;

        bmraiOn = false;
        TryCommand("/bmrai off");
        // TryCommand("/rotation Off");

        if (config.devMode)
            log.Information("[AutoPalExplorer] 已发送 BMRAI 关闭指令。");
    }

    private void EnsureRotationOff()
    {
        TryCommand("/rotation Off");

        if (config.devMode)
            log.Information("[AutoPalExplorer] 已发送 Rotation 关闭指令。");
    }

    public void TryCommand(string command)
    {
        try
        {
            commandManager.ProcessCommand(command);
            if (config.devMode)
                log.Information("[AutoPalExplorer] 执行指令：{Cmd}", command);
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] 执行指令失败 '{command}': {ex.Message}");
        }
    }

    /// <summary>
    /// 通过游戏原生聊天框发送指令。
    /// 注意：ICommandManager.ProcessCommand 只会执行 Dalamud/插件注册的指令（/bmrai、/vnav…），
    /// 不会执行游戏原生指令（/ac、/action、/merror 等）。开怪用的 /ac 必须走这里。
    /// </summary>
    private void SendGameChatCommand(string command)
    {
        try
        {
            Chat.SendMessage(command);
            if (config.devMode)
                log.Information("[AutoPalExplorer] 发送游戏指令：{Cmd}", command);
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] 发送游戏指令失败 '{command}': {ex.Message}");
        }
    }

    /// <summary>
    /// Boss 层逻辑：
    /// - 如果还有敌人：视作 Boss，导航过去，接近后交给 BMRAI 输出。
    /// - 如果没有敌人：寻找 BaseId=2005809 的出口，走过去并交互。
    /// </summary>
    private bool HandleBossFloor(Vector3 pos)
    {
        // 1) 尝试找到 Boss（这里直接用最近敌人即可）
        var boss = FindNearestEnemy(pos, EnemySearchRadius);
        if (boss is not null)
        {
            var dx = boss.Position.X - pos.X;
            var dz = boss.Position.Z - pos.Z;
            var distSq = dx * dx + dz * dz;
            var dist = MathF.Sqrt(distSq);

            if (config.devMode)
                log.Information("[AutoPalExplorer] [Boss层] 检测到 Boss Name={Name}, 距离={Dist:0.00}。",
                    boss.Name.TextValue, dist);

            SetIntent($"Boss 层：前往 / 攻击 Boss（{dist:0.0} 格）");
            // 距离较远：导航过去
            if (!navigator.IsBusy || IsDifferentTarget(navigator.CurrentTarget, boss.Position, 1.0f))
            {
                navigator.Stop();
                navigator.TryMoveTo(boss.Position);

                if (config.devMode)
                    log.Information("[AutoPalExplorer] [Boss层] 导航至 Boss。");
            }

            // 靠近 Boss 时提前开 BMRAI，方便自动输出
            if (distSq <= 5.0f * 5.0f)
            {
                EnsureBmraiOn();
            }

            return true; // Boss 还活着，只做打 Boss 的逻辑
        }

        // 2) 没有敌人 -> 认为 Boss 已击破，前往出口 BaseId=2005809
        var exitObj = FindObjectByBaseId(ObjectIds.BossExitBaseId);
        if (exitObj is not null)
        {
            var ex = exitObj.Position.X - pos.X;
            var ez = exitObj.Position.Z - pos.Z;
            var distSq = ex * ex + ez * ez;
            var dist = MathF.Sqrt(distSq);

            EnsureBmraiOff();
            EnsureRotationOff();

            if (config.devMode)
                log.Information("[AutoPalExplorer] [Boss层] 找到出口(2005809)，距离={Dist:0.00}。", dist);

            if (distSq > ExitStopRadius * ExitStopRadius)
            {
                SetIntent("Boss 层：Boss 已清，前往出口");
                if (!navigator.IsBusy || IsDifferentTarget(navigator.CurrentTarget, exitObj.Position, 1.0f))
                {
                    navigator.Stop();
                    navigator.TryMoveTo(exitObj.Position);

                    if (config.devMode)
                        log.Information("[AutoPalExplorer] [Boss层] 导航至出口(2005809)。");
                }

                bossExitReachedAt = DateTime.MinValue;
            }
            else
            {
                if (bossExitReachedAt == DateTime.MinValue)
                {
                    // 第一次进入出口范围，记录时间
                    bossExitReachedAt = DateTime.UtcNow;
                    if (config.devMode)
                        log.Information("[AutoPalExplorer] [Boss层] 已到出口旁边，开始等待 {Delay}s 后再交互。",
                            BossExitInteractDelaySeconds);
                    return true;
                }

                var now = DateTime.UtcNow;
                var wait = now - bossExitReachedAt;
                if (wait.TotalSeconds < BossExitInteractDelaySeconds)
                {
                    // 还没等够 8 秒，啥也不做
                    if (config.devMode)
                        log.Information("[AutoPalExplorer] [Boss层] 已到出口旁边，已等待 {Elapsed:0.0}s / {Delay:0.0}s，继续等待。",
                            wait.TotalSeconds, BossExitInteractDelaySeconds);
                    return true;
                }

                // 等够了，真正交互出口
                TryInteractWithObject(exitObj, "Boss层出口");
                isBossFloorQueueing = true;
                nextChallengeAttemptAt = DateTime.UtcNow.AddSeconds(ChallengeIntervalSeconds);
                bossExitReachedAt = DateTime.MinValue; // 用完重置

                if (config.devMode)
                    log.Information("[AutoPalExplorer] [Boss层] 等待 {Delay}s 后已与 Boss 出口交互，进入挑战下一朝圣路流程。",
                        BossExitInteractDelaySeconds);
            }

            return true;
        }

        if (config.devMode)
            log.Information("[AutoPalExplorer] [Boss层] 未找到 BaseId=2005809 出口物件，等待下一帧。");

        return true; // 仍视为 Boss 层逻辑已接管（避免跑去贴墙乱逛）
    }

    /// <summary>
    /// Boss 流程第二阶段：
    /// - 在新房间寻找 BaseId=2014758。
    /// - 靠近后交互弹出窗口。
    /// - 每 10 秒尝试点击一次“挑战下一朝圣路”选项/按钮。
    /// - 真正结束条件：收到聊天“成功发送了参加申请”，由 NotifyChallengeRequestSent() 重置状态。
    /// </summary>
    private bool HandleBossFloorQueueing(Vector3 pos)
    {
        // 每帧先尝试点掉本流程会弹出的确认窗口：
        // - 与出口(2005809)交互后弹出的 SelectYesno -> 0
        // - 与机关(2014758)交互后弹出的 DeepDungeonMenu -> 0，随后的 SelectYesno -> 0
        // 若点击失败（窗口还没出来）就等一会下一帧再试，直到 NotifyChallengeRequestSent 结束流程。
        TryConfirmBossQueueAddons();

        var npc = FindObjectByBaseId(ObjectIds.NextPilgrimNpcBaseId);
        if (npc is not null)
        {
            var dx = npc.Position.X - pos.X;
            var dz = npc.Position.Z - pos.Z;
            var distSq = dx * dx + dz * dz;
            var dist = MathF.Sqrt(distSq);

            if (config.devMode)
                log.Information("[AutoPalExplorer] [Boss层] [Queue] 找到挑战NPC/机关(2014758)，距离={Dist:0.00}。", dist);

            if (distSq > ExitStopRadius * ExitStopRadius)
            {
                SetIntent("Boss 层：前往“挑战下一朝圣路”NPC");
                if (!navigator.IsBusy || IsDifferentTarget(navigator.CurrentTarget, npc.Position, 0.5f))
                {
                    navigator.Stop();
                    navigator.TryMoveTo(npc.Position);

                    if (config.devMode)
                        log.Information("[AutoPalExplorer] [Boss层] [Queue] 导航至挑战NPC/机关(2014758)。");
                }
                return true;
            }
            else
            {
                // 已到 NPC 身边：只在「第一次」靠近时交互一次，打开窗口
                if (!hasOpenedNextPilgrimWindow)
                {
                    TryInteractWithObject(npc, "挑战下一朝圣路NPC");
                    hasOpenedNextPilgrimWindow = true;
                    nextChallengeAttemptAt = DateTime.UtcNow.AddSeconds(ChallengeIntervalSeconds);

                    if (config.devMode)
                        log.Information("[AutoPalExplorer] [Boss层] [Queue] 已与 NPC 交互一次，等待窗口并开始定时点击。");
                }
            }
        }
        else
        {
            // NPC 不在视野里，重置一下状态，下次看到再交互
            if (hasOpenedNextPilgrimWindow && config.devMode)
                log.Information("[AutoPalExplorer] [Boss层] [Queue] NPC 不在场景中，重置窗口状态。");

            hasOpenedNextPilgrimWindow = false;
        }

        // 每隔一定时间尝试点“挑战下一朝圣路”按钮（而不是再跟 NPC 说话）
        var now = DateTime.UtcNow;
        if (hasOpenedNextPilgrimWindow && now >= nextChallengeAttemptAt)
        {
            TryInteractWithObject(npc, "挑战下一朝圣路NPC");
            nextChallengeAttemptAt = now.AddSeconds(ChallengeIntervalSeconds);

            if (config.devMode)
                log.Information("[AutoPalExplorer] [Boss层] [Queue] 定时尝试点击“挑战下一朝圣路”按钮。");
        }

        // 这里仍返回 true，让通用逻辑不要乱跑，直到 NotifyChallengeRequestSent 把 isBossFloor 清掉。
        return true;
    }

    /// <summary>
    /// 点掉 Boss 排队流程里会弹出的确认窗口：
    /// - DeepDungeonMenu -> Callback.Fire(a, true, 0)
    /// - SelectYesno     -> Callback.Fire(a, true, 0)
    /// 带节流，避免每帧对同一个窗口狂点。返回是否点了其中一个。
    /// </summary>
    private unsafe bool TryConfirmBossQueueAddons()
    {
        var now = DateTime.UtcNow;
        if (now < nextBossAddonFireAt)
            return false;

        if (TryGetAddonByName<AtkUnitBase>("DeepDungeonMenu", out var menu) && IsAddonReady(menu))
        {
            Callback.Fire(menu, true, 0);
            nextBossAddonFireAt = now.AddSeconds(1.0);

            if (config.devMode)
                log.Information("[AutoPalExplorer] [Boss层] [Queue] 点击 DeepDungeonMenu -> 0。");
            return true;
        }

        if (TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yn) && IsAddonReady(yn))
        {
            Callback.Fire(yn, true, 0);
            nextBossAddonFireAt = now.AddSeconds(1.0);

            if (config.devMode)
                log.Information("[AutoPalExplorer] [Boss层] [Queue] 点击 SelectYesno -> 0。");
            return true;
        }

        return false;
    }

    private unsafe void TryInteractWithObject(IGameObject obj, string purpose)
    {
        if (obj == null)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] TryInteractWithObject({Purpose})：目标为 null。", purpose);
            return;
        }

        if (!obj.IsTargetable)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] TryInteractWithObject({Purpose})：目标不可交互，BaseId={BaseId}。", purpose, obj.BaseId);
            return;
        }

        try
        {
            var ptr = (GameObject*)obj.Address;
            if (ptr == null)
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] TryInteractWithObject({Purpose})：GameObject 指针为空。", purpose);
                return;
            }

            var ts = TargetSystem.Instance();
            if (ts == null)
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] TryInteractWithObject({Purpose})：TargetSystem 实例为空。", purpose);
                return;
            }

            ts->InteractWithObject(ptr, false);

            if (config.devMode)
                log.Information("[AutoPalExplorer] 已尝试与 {Purpose} 交互，BaseId={BaseId}。", purpose, obj.BaseId);
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] TryInteractWithObject({purpose}) 异常：{ex.Message}");
        }
    }

    private IGameObject? FindObjectByBaseId(uint baseId)
    {
        foreach (var obj in objectTable)
        {
            if (obj.BaseId == baseId)
                return obj;
        }
        return null;
    }

    // ================= 地宫入口自动化（terr 816）=================

    /// <summary>
    /// 在入口地图（terr 816）自动进本：
    /// 1. 靠近入口坐标（不足 5 格用 /vnav flyto 接近）；
    /// 2. 交互入口物件 (BaseId=1054942)；
    /// 3. DeepDungeonMenu -> callback(0)；
    /// 4. DeepDungeonSaveData -> callback(0,0)；
    /// 5. SelectString -> 选 0；
    /// 6. 之后不断 SelectYesno 选 0，直到出现 SelectString 再选 0，进入地宫。
    /// 只有车头模式运行；收到“成功发送了参加申请”后结束。
    /// </summary>
    private void HandleDungeonEntry(Vector3 pos)
    {
        if (IsFollowMode)
            return; // 只有车头模式自动进本

        if (entrySubmitted)
        {
            SetIntent("地宫入口：已发送参加申请，等待进本");
            return; // 申请已发出
        }

        if (entryTaskManager.IsBusy)
        {
            SetIntent("地宫入口：正在处理进本菜单");
            return; // UI 序列进行中，等它跑完
        }

        var dist = (pos - EntryPoint).Length();
        if (dist > EntryReachRadius)
        {
            SetIntent($"地宫入口：flyto 接近入口（{dist:0.0} 格）");
            var now = DateTime.UtcNow;
            if (now >= nextEntryFlytoAt)
            {
                TryCommand("/vnav moveto 424.2 89.4 -772.7");
                nextEntryFlytoAt = now.AddSeconds(2);

                if (config.devMode)
                    log.Information("[AutoPalExplorer] [入口] 距离入口 {Dist:0.0} 格，flyto 接近。", dist);
            }
            return;
        }

        var entryObj = FindObjectByBaseId(EntryObjectBaseId);
        if (entryObj is null)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] [入口] 已到入口附近，但未找到入口物件 (BaseId={BaseId})，等待。", EntryObjectBaseId);
            return;
        }

        // 停止导航后开始 UI 交互序列
        SetIntent("地宫入口：交互并处理进本菜单");
        TryCommand("/vnav stop");
        EnqueueEntrySequence(entryObj.GameObjectId);

        if (config.devMode)
            log.Information("[AutoPalExplorer] [入口] 已到入口附近，开始交互进本序列。");
    }

    private unsafe void EnqueueEntrySequence(ulong entryObjectId)
    {
        // 1. 交互入口物件
        entryTaskManager.Enqueue(() => InteractEntryObject(entryObjectId));

        // 2. DeepDungeonMenu -> callback(0)
        entryTaskManager.Enqueue(() =>
            TryGetAddonByName<AtkUnitBase>("DeepDungeonMenu", out var a) && IsAddonReady(a));
        entryTaskManager.Enqueue(() =>
        {
            if (TryGetAddonByName<AtkUnitBase>("DeepDungeonMenu", out var a))
                Callback.Fire(a, true, 0);
        });

        // 3. DeepDungeonSaveData -> callback(0, 0)
        entryTaskManager.Enqueue(() =>
            TryGetAddonByName<AtkUnitBase>("DeepDungeonSaveData", out var a) && IsAddonReady(a));
        entryTaskManager.Enqueue(() =>
        {
            if (TryGetAddonByName<AtkUnitBase>("DeepDungeonSaveData", out var a))
                Callback.Fire(a, true, 0, 0);
        });

        // 4. SelectString -> 选 0
        entryTaskManager.Enqueue(() =>
            TryGetAddonByName<AtkUnitBase>("SelectString", out var a) && IsAddonReady(a));
        entryTaskManager.Enqueue(() =>
        {
            if (TryGetAddonByName<AtkUnitBase>("SelectString", out var a))
                Callback.Fire(a, true, 0);
        });

        // 5. 不断 SelectYesno 选 0，直到再次出现 SelectString 选 0
        nextEntryConfirmFireAt = DateTime.MinValue;
        entryTaskManager.Enqueue(HandleEntryConfirmLoop);
    }

    private unsafe bool InteractEntryObject(ulong entryObjectId)
    {
        foreach (var obj in objectTable)
        {
            if (obj.GameObjectId != entryObjectId)
                continue;

            TryInteractWithObject(obj, "地宫入口");
            return true;
        }
        return false; // 找不到就重试
    }

    /// <summary>
    /// SelectYesno 全部选 0（是），直到出现 SelectString 再选 0 并结束。
    /// </summary>
    private unsafe bool HandleEntryConfirmLoop()
    {
        if (TryGetAddonByName<AtkUnitBase>("SelectString", out var ss) && IsAddonReady(ss))
        {
            Callback.Fire(ss, true, 0);
            return true; // 完成
        }

        var now = DateTime.UtcNow;
        if (now >= nextEntryConfirmFireAt &&
            TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yn) && IsAddonReady(yn))
        {
            Callback.Fire(yn, true, 0);
            nextEntryConfirmFireAt = now.AddSeconds(1.0); // 节流，避免对同一弹窗连点
        }

        return false; // 继续等待/重试
    }

    /// <summary>
    /// 检查某个点附近是否有陷阱（EventObj + TrapIds）。
    /// 返回是否危险，以及最近陷阱的位置。
    /// </summary>
    private bool IsNearTrap(Vector3 point, float radius, out Vector3 nearestTrapPos)
    {
        var radiusSq = radius * radius;
        nearestTrapPos = default;
        var found = false;
        var bestSq = float.MaxValue;

        foreach (var obj in objectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)
                continue;

            if (!ObjectIds.TrapIds.Contains(obj.BaseId))
                continue;

            var dx = obj.Position.X - point.X;
            var dz = obj.Position.Z - point.Z;
            var distSq = dx * dx + dz * dz;

            if (distSq < radiusSq && distSq < bestSq)
            {
                bestSq = distSq;
                nearestTrapPos = obj.Position;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// 包一层导航：如果目标点太靠近陷阱，尝试往远离陷阱方向偏移；如果仍然不安全则放弃该移动。
    /// </summary>
    private bool TrySafeMoveTo(Vector3 destination, float avoidRadius)
    {
        if (IsNearTrap(destination, avoidRadius, out var trapPos))
        {
            // 计算一个从陷阱往外偏移的新目标点
            var offset = destination - trapPos;
            offset.Y = 0;

            if (offset.LengthSquared() < 0.0001f)
            {
                // 和陷阱几乎重合，随便给个方向
                offset = new Vector3(1, 0, 0);
            }

            offset = Vector3.Normalize(offset) * (avoidRadius + 0.5f);
            var newDest = trapPos + offset;

            // 再检查一次新目标是否仍然贴陷阱
            if (IsNearTrap(newDest, avoidRadius, out _))
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] TrySafeMoveTo: 目标及偏移点均过近陷阱，放弃该导航目标。");
                return false;
            }

            if (config.devMode)
            {
                log.Information("[AutoPalExplorer] TrySafeMoveTo: 目标靠近陷阱，调整至安全点 ({X:0.00}, {Y:0.00}, {Z:0.00})。",
                    newDest.X, newDest.Y, newDest.Z);
            }

            navigator.Stop();
            navigator.TryMoveTo(newDest);
            return true;
        }

        navigator.Stop();
        navigator.TryMoveTo(destination);
        return true;
    }

    /// <summary>
    /// 在一定范围内寻找最近的埋藏宝藏（EventObj + BuriedChestIds）。
    /// </summary>
    private IGameObject? FindNearestBuriedChest(Vector3 from, float maxDistance)
    {
        IGameObject? best = null;
        var bestDistSq = maxDistance * maxDistance;

        foreach (var obj in objectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)
                continue;

            if (!ObjectIds.BuriedChestIds.Contains(obj.BaseId))
                continue;

            var dx = obj.Position.X - from.X;
            var dz = obj.Position.Z - from.Z;
            var distSq = dx * dx + dz * dz;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = obj;
            }
        }

        return best;
    }
    /// <summary>
    /// 前往一个敌人并开怪：
    /// - 若配置了远程开怪且已进入 PullRange：停下、锁定目标、按职业远程技能开怪；
    /// - 否则：导航到敌人身边（进战后交给 BMRAI）。
    /// 从“找怪”与“支援队友”两处复用。
    /// </summary>
    private void EngageEnemy(IBattleChara enemy, Vector3 pos, IPlayerCharacter? player, Vector3? currentTarget)
    {
        var ex = enemy.Position.X - pos.X;
        var ez = enemy.Position.Z - pos.Z;
        var edist = MathF.Sqrt(ex * ex + ez * ez);

        // 远程开怪：走进 PullRange 内就停下、锁定目标、用当前职业的远程技能开怪，避免脸开
        var pullCommand = ResolvePullCommand(player);
        if (config.PullRange > 0f && pullCommand is not null && edist <= PullRange)
        {
            SetIntent($"发现怪物：{edist:0.0}m 内远程开怪");

            if (navigator.IsBusy)
                navigator.Stop();

            // 锁定最近的怪作为技能目标
            if (targetManager.Target?.GameObjectId != enemy.GameObjectId)
                targetManager.Target = enemy;

            // 读条技能必须站定才放，否则移动会打断读条：还在移动（减速中）就本帧只停不放，等站稳
            if (IsPlayerMoving)
            {
                SetIntent($"发现怪物：{edist:0.0}m 内，等待站定后开怪");
                if (config.devMode)
                    log.Information("[AutoPalExplorer] 远程开怪：仍在移动(speed={Speed:0.00} m/s)，等待站定后再放技能。", currentSpeedMps);
                return;
            }

            // 节流发开怪指令，避免每帧狂点（进战后由顶部战斗分支交给 BMRAI）
            var now = DateTime.UtcNow;
            if (now >= nextPullActionAt)
            {
                // /ac 是游戏原生指令，必须走聊天框而不是 ProcessCommand
                SendGameChatCommand(pullCommand);
                nextPullActionAt = now.AddMilliseconds(PullActionIntervalMs);

                if (config.devMode)
                    log.Information("[AutoPalExplorer] 远程开怪：目标={Name}, 距离={Dist:0.00}, 执行指令 {Cmd}。",
                        enemy.Name.TextValue, edist, pullCommand);
            }

            return;
        }

        SetIntent("发现怪物：前往并交给 BMRAI");

        if (config.devMode)
            log.Information("[AutoPalExplorer] 找到最近敌人 Name={Name}, 距离={Dist:0.00}，发送导航到敌人位置。",
                enemy.Name.TextValue, edist);

        if (!navigator.IsBusy || IsDifferentTarget(currentTarget, enemy.Position, 1.0f))
        {
            navigator.Stop();
            navigator.TryMoveTo(enemy.Position);
        }
    }

    /// <summary>
    /// 车头模式支援：如果有队友进入战斗状态，停止当前探索，前去支援。
    /// - 离进战队友较远：导航到队友身边；
    /// - 到队友身边后：找最近的怪开打（复用 EngageEnemy）；
    /// - 队友在战斗但附近没有可打的怪：待在队友身边，不跑去开箱/贴墙。
    /// 返回 true 表示本帧由支援逻辑接管。
    /// </summary>
    private bool TryHelpPartyInCombat(Vector3 pos, IPlayerCharacter? player, Vector3? currentTarget)
    {
        if (!config.HelpPartyInCombat)
            return false;

        var mate = FindNearestInCombatPartyMember(pos);
        if (mate is null)
            return false;

        var dx = mate.Position.X - pos.X;
        var dz = mate.Position.Z - pos.Z;
        var dist = MathF.Sqrt(dx * dx + dz * dz);

        // 距离较远：先跑到队友身边
        if (dist > HelpPartyArriveRadius)
        {
            SetIntent($"车头：队友进战，前去支援（{dist:0.0}m）");

            if (!navigator.IsBusy || IsDifferentTarget(currentTarget, mate.Position, 1.5f))
            {
                navigator.Stop();
                navigator.TryMoveTo(mate.Position);
            }

            if (config.devMode)
                log.Information("[AutoPalExplorer] [支援] 队友 {Name} 进战，前往支援，距离={Dist:0.00}。",
                    mate.Name.TextValue, dist);

            return true;
        }

        // 已到队友身边：找最近的怪开打
        var enemy = FindNearestEnemy(pos, EnemySearchRadius);
        if (enemy is not null)
        {
            SetIntent("车头：在队友身边帮忙打怪");
            EngageEnemy(enemy, pos, player, currentTarget);
            return true;
        }

        // 队友在战斗但附近找不到可打的怪：待命在旁，别跑去开箱
        SetIntent("车头：队友进战，待命在旁");
        if (navigator.IsBusy)
            navigator.Stop();

        return true;
    }

    /// <summary>
    /// 找最近的、处于战斗状态且未阵亡的队友（排除自己）。
    /// </summary>
    private IBattleChara? FindNearestInCombatPartyMember(Vector3 from)
    {
        var localId = objectTable.LocalPlayer?.GameObjectId ?? 0;

        IBattleChara? best = null;
        var bestDistSq = float.MaxValue;

        foreach (var member in partyList)
        {
            var obj = member.GameObject;
            if (obj is null)
                continue;

            if (obj.GameObjectId == localId)
                continue;

            if (obj is not IBattleChara bc)
                continue;

            if (bc.IsDead || bc.CurrentHp <= 0)
                continue;

            if (!bc.StatusFlags.HasFlag(StatusFlags.InCombat))
                continue;

            var dx = bc.Position.X - from.X;
            var dz = bc.Position.Z - from.Z;
            var distSq = dx * dx + dz * dz;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = bc;
            }
        }

        return best;
    }

    private IBattleChara? FindNearestEnemy(Vector3 from, float maxDistance)
    {
        IBattleChara? best = null;
        var bestDistSq = maxDistance * maxDistance;

        foreach (var obj in objectTable)
        {
            if (obj is not IBattleChara bc)
                continue;

            if (bc.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)
                continue;

            if (bc is not IBattleNpc bn)
                continue;

            if (bn.BattleNpcKind != Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant)
                continue;

            if (!bc.IsTargetable || bc.CurrentHp <= 0)
                continue;

            var dx = bc.Position.X - from.X;
            var dz = bc.Position.Z - from.Z;
            var distSq = dx * dx + dz * dz;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = bc;
            }
        }

        if (config.devMode)
        {
            if (best is null)
            {
                log.Information("[AutoPalExplorer] FindNearestEnemy：范围内未找到可攻击敌人。");
            }
            else
            {
                var dx = best.Position.X - from.X;
                var dz = best.Position.Z - from.Z;
                var dist = MathF.Sqrt(dx * dx + dz * dz);
                log.Information("[AutoPalExplorer] FindNearestEnemy：最近敌人 Name={Name}, 距离={Dist:0.00}。",
                    best.Name.TextValue, dist);
            }
        }

        return best;
    }

    private bool IsIgnoredChest(IGameObject chest)
        => ignoredChestIds.Contains(chest.GameObjectId);
    private unsafe void TryClickNextPilgrim()
    {
        try
        {
            // TODO: 根据实际 Addon 名称和文本调整：
            // 下方只是示意写法（需要你替换成自己目前项目里用于点窗口按钮的那套工具函数）

            // 示例：有一行写着 "挑战下一朝圣路"

            if (config.devMode)
                log.Information("[AutoPalExplorer] [Boss层] [Queue] 尝试点击“挑战下一朝圣路”（具体实现请按实际Addon调整）。");
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] TryClickNextPilgrim 异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 当收到“无法获得更多的魔陶器：xxx 被重新放回宝箱中……”的聊天提示时调用。
    /// 把最近一次尝试交互的宝箱加入忽略列表，避免在该层无限尝试。
    /// </summary>
    public void NotifyChestPomanderOverflow()
    {
        if (lastChestInteractObjectId == 0)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] PomanderOverflow：没有记录到最近交互的宝箱ID，忽略。");
            return;
        }

        if (ignoredChestIds.Add(lastChestInteractObjectId))
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] PomanderOverflow：将 GameObjectId={Id} 加入忽略宝箱列表。",
                    lastChestInteractObjectId);
        }

        // ✅ 新增：如果当前锁的是这个箱子，也顺便解锁
        if (hasLockedChest && lockedChestId == lastChestInteractObjectId)
        {
            ClearLockedChest();
        }
    }

    private void StartFollowLoop()
    {
        // 1. 先从配置里拿到要跟随的槽位
        var index = config.FollowPartyIndex;

        if (partyList.Length == 0)
        {
            log.Warning("[AutoPalExplorer] 跟车模式：当前不在队伍中，无法跟随。");
            Stop();
            return;
        }

        if (index < 0 || index >= partyList.Length)
        {
            log.Warning("[AutoPalExplorer] 跟车模式：FollowPartyIndex={Index} 无效（队伍人数={Count}），停止。", index, partyList.Length);
            Stop();
            return;
        }

        var member = partyList[index];
        var actor = member.GameObject;

        if (actor is null)
        {
            log.Warning("[AutoPalExplorer] 跟车模式：选中目标队友 GameObject 为空（可能还没加载），停止。");
            Stop();
            return;
        }

        // 2. 尝试把他设为当前目标（等价于你手动点人）
        try
        {
            targetManager.Target = actor;
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] 跟车模式：设置 Target 失败：{ex}");
            Stop();
            return;
        }

        // 3. 再读一遍当前 Target，确认确实选中了这个人
        if (targetManager.Target is not IGameObject currentTarget ||
            currentTarget.GameObjectId != actor.GameObjectId)
        {
            log.Warning("[AutoPalExplorer] 跟车模式：尝试选中队友失败（Target 不一致），停止。");
            Stop();
            return;
        }

        // 4. 选中成功，发送 /pdr follow
        TryCommand("/pdrfollow");

        if (config.UseBmrai)
        {
            EnsureBmraiOn();
        }

        if (config.devMode)
        {
            log.Information(
                "[AutoPalExplorer] 跟车模式：已选中队友 {Name} 并发送 /pdr follow。",
                member.Name.TextValue
            );
        }
    }


    private void TryChatCommand(string text)
    {
        try
        {
            Plugin.ChatGui.Print(text);

            if (config.devMode)
                log.Information("[AutoPalExplorer] 发送聊天命令：{Cmd}", text);
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] 发送聊天命令失败 '{text}': {ex.Message}");
        }
    }

    private void ResetBlindWalkState()
    {
        ignoredBlindLocations.Clear();
        blindLocations.Clear();
        allBlindLocations.Clear();
        blindLocationsTerritory = 0;
        currentBlindTarget = null;
        blindArrivedAt = DateTime.MinValue;
        blindLastProgressPos = Vector3.Zero;
        blindLastProgressCheckAt = DateTime.MinValue;

        // 丢弃任何进行中/未处理的预定结果，避免跨层残留。
        // 自增世代号：让已在路上的后台请求完成时校验失败，从而不再写回状态位。
        reserveGeneration++;
        reservePendingCandidate = null;
        reserveHasResult = false;
        reserveInFlight = false;
        reserveSucceeded = false;

        if (IsOnlineMode)
        {
            OnlineClearIgnoredOnServer();
        }
    }
    private void ResetStaticObjectsState()
    {
        savedExitPos = null;
        savedRegenerationPos = null;
        exitActivatedByChat = false;
        regenerationActivated = false;
    }

    private void OnlineClearIgnoredOnServer()
    {
        var apiKey = config.OnlineApiKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        var territory = clientState.TerritoryType;
        var url = $"{OnlineServerUrl}/ignored/clear";

        var payload = new OnlineIgnoredClearRequest
        {
            ApiKey = apiKey,
            Territory = territory
        };

        _ = Task.Run(async () =>
        {
            try
            {
                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await httpClient.PostAsync(url, content).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode && config.devMode)
                {
                    log.Warning("[AutoPalExplorer] 联机盲踩：清空 ignored 失败 HTTP {Code}", resp.StatusCode);
                }
            }
            catch (Exception ex)
            {
                if (config.devMode)
                    log.Warning($"[AutoPalExplorer] 联机盲踩：清空 ignored 异常：{ex.Message}");
            }
        });
    }

    private bool HandleChest(Vector3 playerPos, IGameObject chest, Vector3? currentTarget)
    {
        if (IsIgnoredChest(chest))
            return false;

        if (!ShouldOpenChest(chest.BaseId))
            return false;

        if (!chest.IsTargetable)
            return false;

        var dx = chest.Position.X - playerPos.X;
        var dz = chest.Position.Z - playerPos.Z;
        var distSq = dx * dx + dz * dz;

        if (distSq > ChestDoneRadius * ChestDoneRadius)
        {
            // ✅ 新增：在决定走向这个宝箱时上锁，记录 ID 和坐标
            if (!hasLockedChest || lockedChestId != chest.GameObjectId)
            {
                hasLockedChest = true;
                lockedChestId = chest.GameObjectId;
                lockedChestPos = chest.Position;

                if (config.devMode)
                {
                    log.Information("[AutoPalExplorer] 锁定宝箱：GameObjectId={Id}, Pos=({X:0.00}, {Y:0.00}, {Z:0.00})。",
                        lockedChestId, lockedChestPos.X, lockedChestPos.Y, lockedChestPos.Z);
                }
            }

            if (!navigator.IsBusy || IsDifferentTarget(currentTarget, chest.Position, 1.0f))
            {
                navigator.Stop();
                navigator.TryMoveTo(chest.Position);
            }
            return true; // 本帧由宝箱逻辑接管
        }

        // 已在开箱半径内
        // ✅ 新增：到点后可以把锁清掉（不再需要防抖）
        if (hasLockedChest && lockedChestId == chest.GameObjectId)
        {
            ClearLockedChest();
        }

        TryOpenChest(chest);
        return true; // 建议这里也直接 true，后面不再处理门
    }


    // ✅ 新增：处理锁定中的宝箱，防止在宝箱和门/怪之间来回切
    private bool HandleLockedChest(Vector3 playerPos, Vector3? currentTarget)
    {
        if (!hasLockedChest)
            return false;

        // 尝试在 objectTable 中找到这个 GameObjectId
        IGameObject? lockedChestObj = null;
        foreach (var obj in objectTable)
        {
            if (obj.GameObjectId == lockedChestId)
            {
                lockedChestObj = obj;
                break;
            }
        }

        // 1) 找到了真实对象
        if (lockedChestObj is not null)
        {
            var dx = lockedChestObj.Position.X - playerPos.X;
            var dz = lockedChestObj.Position.Z - playerPos.Z;
            var distSq = dx * dx + dz * dz;

            // 如果现在配置里已经不打算开这个箱子，或者已经被标记 ignore，直接解锁
            if (!ShouldOpenChest(lockedChestObj.BaseId) || IsIgnoredChest(lockedChestObj))
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] 锁定宝箱已被配置忽略或在忽略列表中，解除锁定。");
                ClearLockedChest();
                return false;
            }

            // 如果已经不可交互且在 ChestDoneRadius 范围内，认为已经被开过，加入 ignore 并解锁
            if (!lockedChestObj.IsTargetable && distSq <= ChestDoneRadius * ChestDoneRadius && !ObjectIds.IsBuriedChest(lockedChestObj.BaseId))
            {
                if (ignoredChestIds.Add(lockedChestObj.GameObjectId) && config.devMode)
                {
                    log.Information("[AutoPalExplorer] 锁定宝箱在 ChestDoneRadius 内且不可交互，视为已开，加入忽略列表并解锁。");
                }
                ClearLockedChest();
                return false;
            }

            // 正常情况：交给现有 HandleChest 处理（移动 / 开箱），同时保持锁定
            return HandleChest(playerPos, lockedChestObj, currentTarget);
        }

        // 2) 在 objectTable 中找不到这个箱子（被裁剪掉 / despawn）
        var dx2 = lockedChestPos.X - playerPos.X;
        var dz2 = lockedChestPos.Z - playerPos.Z;
        var distSq2 = dx2 * dx2 + dz2 * dz2;

        var giveUpDistSq = LockedChestGiveUpDistance * LockedChestGiveUpDistance;

        if (distSq2 > giveUpDistSq)
        {
            // 距离原始宝箱位置 > 100：即使暂时不可见，也继续朝 lockedChestPos 走，不切换到门/怪
            if (!navigator.IsBusy || IsDifferentTarget(currentTarget, lockedChestPos, 1.0f))
            {
                if (config.devMode)
                {
                    var dist = MathF.Sqrt(distSq2);
                    log.Information("[AutoPalExplorer] 锁定宝箱暂时不可见，距离={Dist:0.0} > {Limit}，继续导航到记录位置。",
                        dist, LockedChestGiveUpDistance);
                }

                TrySafeMoveTo(lockedChestPos, TrapAvoidRadiusCfg);
            }

            // 本帧由锁定宝箱逻辑接管
            return true;
        }
        else
        {
            // 距离原始宝箱位置 <= 100 且仍然看不到这个箱子：按你说的，当作已经开过
            if (lockedChestId != 0 && ignoredChestIds.Add(lockedChestId))
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] 锁定宝箱在100范围内仍不可见，视为已开，加入忽略列表。");
            }

            ClearLockedChest();
            // 返回 false，让后面的门/怪逻辑可以接管
            return false;
        }
    }

    private void EnsureBlindLocationsLoaded()
    {
        var territory = clientState.TerritoryType;

        // 如果当前缓存的就是这个 Territory，而且已经有数据，就不用重复查
        if (blindLocationsTerritory == territory && blindLocations.Count > 0 && allBlindLocations.Count > 0)
            return;

        blindLocations.Clear();
        allBlindLocations.Clear();
        blindLocationsTerritory = territory;
        currentBlindTarget = null;
        blindArrivedAt = DateTime.MinValue;
        ignoredBlindLocations.Clear();
        blindLastProgressPos = Vector3.Zero;
        blindLastProgressCheckAt = DateTime.MinValue;

        if (string.IsNullOrEmpty(config.PalacePalDbPath))
        {
            if (config.devMode)
                log.Warning("[AutoPalExplorer] PalacePalDbPath 未配置，跳过盲踩坐标加载。");
            return;
        }

        var filePath = Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName!, config.PalacePalDbPath);
        if (config.devMode)
            log.Information(filePath.ToString());
        if (!Path.IsPathRooted(config.PalacePalDbPath) && !File.Exists(filePath))
        {
            // 如果你允许直接填绝对路径，也可以再试一次绝对路径
            if (File.Exists(config.PalacePalDbPath))
            {
                filePath = config.PalacePalDbPath;
            }
            else
            {
                if (config.devMode)
                    log.Warning("[AutoPalExplorer] PalacePal 数据库不存在：{Path}", filePath);
                return;
            }
        }

        try
        {
            EnsureSQLiteProvider();

            sqlite3 db;
            var rc = raw.sqlite3_open(filePath, out db);
            if (rc != raw.SQLITE_OK)
            {
                if (config.devMode)
                    log.Warning("[AutoPalExplorer] 打开 PalacePal DB 失败，rc={Rc}", rc);
                // 如果 open 失败，db 可能是非 null，保险起见关一下
                try { raw.sqlite3_close(db); } catch { }
                return;
            }

            try
            {
                // -------- 第一条查询：Type = 2 -> blindLocations --------
                {
                    log.Warning("[AutoPalExplorer] blindLocations开始读取。", rc);
                    var sql = $"SELECT X, Y, Z FROM Locations WHERE Type = 2 AND TerritoryType = {territory}";
                    sqlite3_stmt stmt;
                    rc = raw.sqlite3_prepare_v2(db, sql, out stmt);
                    if (rc != raw.SQLITE_OK)
                    {
                        if (config.devMode)
                            log.Warning("[AutoPalExplorer] 准备查询 Type=2 盲踩坐标失败，rc={Rc}", rc);
                    }
                    else
                    {
                        try
                        {
                            while ((rc = raw.sqlite3_step(stmt)) == raw.SQLITE_ROW)
                            {
                                var x = (float)raw.sqlite3_column_double(stmt, 0);
                                var y = (float)raw.sqlite3_column_double(stmt, 1);
                                var z = (float)raw.sqlite3_column_double(stmt, 2);
                                blindLocations.Add(new Vector3(x, y, z));
                            }

                            if (rc != raw.SQLITE_DONE && config.devMode)
                            {
                                log.Warning("[AutoPalExplorer] 读取 Type=2 盲踩坐标时返回 rc={Rc}（非 SQLITE_DONE）。", rc);
                            }
                        }
                        finally
                        {
                            raw.sqlite3_finalize(stmt);
                        }
                    }
                }

                // -------- 第二条查询：所有点 -> allBlindLocations --------
                {
                    var sql = $"SELECT X, Y, Z FROM Locations WHERE TerritoryType = {territory}";
                    sqlite3_stmt stmt;
                    rc = raw.sqlite3_prepare_v2(db, sql, out stmt);
                    if (rc != raw.SQLITE_OK)
                    {
                        if (config.devMode)
                            log.Warning("[AutoPalExplorer] 准备查询全部盲踩坐标失败，rc={Rc}", rc);
                    }
                    else
                    {
                        try
                        {
                            while ((rc = raw.sqlite3_step(stmt)) == raw.SQLITE_ROW)
                            {
                                var x = (float)raw.sqlite3_column_double(stmt, 0);
                                var y = (float)raw.sqlite3_column_double(stmt, 1);
                                var z = (float)raw.sqlite3_column_double(stmt, 2);
                                allBlindLocations.Add(new Vector3(x, y, z));
                            }

                            if (rc != raw.SQLITE_DONE && config.devMode)
                            {
                                log.Warning("[AutoPalExplorer] 读取全部盲踩坐标时返回 rc={Rc}（非 SQLITE_DONE）。", rc);
                            }
                        }
                        finally
                        {
                            raw.sqlite3_finalize(stmt);
                        }
                    }
                }
            }
            finally
            {
                // ✅ 只在这里关一次
                raw.sqlite3_close(db);
            }

            if (config.devMode)
            {
                log.Information(
                    "[AutoPalExplorer] 盲踩：加载完成，Territory={Territory}, Type=2 点位={CountType2}, 全部点位={CountAll}",
                    territory, blindLocations.Count, allBlindLocations.Count);
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] 加载 PalacePal 盲踩坐标失败：{ex}");
        }
    }

    private Vector3? GetNextBlindLocation(Vector3 from, float maxDistance)
    {
        Vector3? best = null;
        var maxDistSq = maxDistance * maxDistance;
        var bestDistSq = maxDistSq;
        var locationList = config.BlindChestsWithTrap ? allBlindLocations : blindLocations;

        // log.Warning($"[AutoPalExplorer] location数量：{locationList.Count.ToString()}");
        foreach (var p in locationList)
        {
            if (IsBlindLocationIgnored(p))
                continue;

            var dx = p.X - from.X;
            var dz = p.Z - from.Z;
            var distSq = dx * dx + dz * dz;

            if (distSq > maxDistSq)
                continue;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = p;
            }
        }

        return best;
    }

    private long PackBlindKey(Vector3 p)
    {
        // 把坐标粗略量化一下，避免浮点误差导致同一点重复
        var x = (int)MathF.Round(p.X * 10); // 0.1 精度
        var z = (int)MathF.Round(p.Z * 10);
        return ((long)x << 32) | (uint)z;
    }

    private bool IsBlindLocationIgnored(Vector3 p)
    {
        var key = PackBlindKey(p);
        lock (ignoredBlindLocations)
        {
            return ignoredBlindLocations.Contains(key);
        }
    }


    private void IgnoreBlindLocation(Vector3 p)
    {
        var key = PackBlindKey(p);
        var added = false;

        lock (ignoredBlindLocations)
        {
            if (!ignoredBlindLocations.Contains(key))
            {
                ignoredBlindLocations.Add(key);
                added = true;
            }
        }

        if (added && config.devMode)
        {
            log.Information("[AutoPalExplorer] 盲踩：忽略点位 ({X:0.00}, {Y:0.00}, {Z:0.00})。", p.X, p.Y, p.Z);
        }

        // 联机模式：把新忽略的点推到服务器
        // if (added && IsOnlineMode)
        // {
        //     PushIgnoredKeyToServer(key);
        // }
    }

    private void PushIgnoredKeyToServer(long key)
    {
        var apiKey = config.OnlineApiKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        var territory = clientState.TerritoryType;
        var url = $"{OnlineServerUrl}/ignored/add";

        var payload = new OnlineIgnoredAddRequest
        {
            ApiKey = apiKey,
            Territory = territory,
            Keys = new[] { key }
        };

        _ = Task.Run(async () =>
        {
            try
            {
                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await httpClient.PostAsync(url, content).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode && config.devMode)
                {
                    log.Warning("[AutoPalExplorer] 联机盲踩：上报 ignored 点位失败 HTTP {Code}", resp.StatusCode);
                    // log.Warning(url);
                }
            }
            catch (Exception ex)
            {
                if (config.devMode)
                    log.Warning($"[AutoPalExplorer] 联机盲踩：上报 ignored 点位异常：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 联机模式下非阻塞地选定盲踩目标点。只在主线程调用。
    /// 状态机：
    /// - 有请求在路上 (reserveInFlight)：本帧什么都不做，返回 false（还没拿到目标）。
    /// - 有已完成结果 (reserveHasResult)：
    ///     成功  -> 设为 currentBlindTarget，返回 true；
    ///     失败  -> 本地忽略该点，继续本帧发起下一个候选点的预定，返回 false。
    /// - 空闲：挑一个候选点，发起后台预定，返回 false。
    /// 返回 true 表示已经拿到一个可用的 currentBlindTarget。
    /// </summary>
    private bool TryPickBlindTargetOnline(Vector3 playerPos)
    {
        // 1) 处理已完成的预定结果
        if (reserveHasResult)
        {
            reserveHasResult = false;
            var candidate = reservePendingCandidate;
            reservePendingCandidate = null;

            if (reserveSucceeded && candidate is { } okPos)
            {
                currentBlindTarget = okPos;
                blindArrivedAt = DateTime.MinValue;
                blindLastProgressPos = playerPos;
                blindLastProgressCheckAt = DateTime.UtcNow;

                if (config.devMode)
                {
                    log.Information("[AutoPalExplorer] 盲踩：预定成功，选择新目标 ({X:0.00}, {Y:0.00}, {Z:0.00})，Key={Key}。",
                        okPos.X, okPos.Y, okPos.Z, PackBlindKey(okPos));
                }
                return true;
            }

            // 抢不到：本地也忽略这个点，稍后（下面）尝试下一个候选点
            if (candidate is { } failPos)
            {
                IgnoreBlindLocation(failPos);
                if (config.devMode)
                {
                    log.Information("[AutoPalExplorer] 盲踩：目标 ({X:0.00}, {Y:0.00}, {Z:0.00}) 已被占用，尝试下一个。",
                        failPos.X, failPos.Y, failPos.Z);
                }
            }
        }

        // 2) 已经有请求在路上：等结果，本帧不再发起、也不阻塞
        if (reserveInFlight)
            return false;

        // 3) 空闲：挑下一个候选点并发起后台预定
        var next = GetNextBlindLocation(playerPos, config.BlindMaxDistance);
        if (next is null)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] 盲踩：没有更多可踩的坐标。");
            return false;
        }

        StartReserveKeyOnServer(next.Value);
        return false;
    }

    /// <summary>
    /// 在后台线程向服务器发起“预定点位”请求，不阻塞主线程。
    /// 完成后写回 reserveSucceeded / reserveHasResult，并最后清 reserveInFlight。
    /// </summary>
    private void StartReserveKeyOnServer(Vector3 candidate)
    {
        var key = PackBlindKey(candidate);
        reservePendingCandidate = candidate;
        reserveInFlight = true;
        reserveHasResult = false;
        var gen = reserveGeneration; // 捕获本次请求的世代，完成时校验是否已被换层作废

        var apiKey = config.OnlineApiKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // 没配置 api_key：视为预定失败，交回主线程处理（会本地忽略后继续）
            reserveSucceeded = false;
            reserveHasResult = true;
            reserveInFlight = false;
            return;
        }

        var territory = clientState.TerritoryType;
        var url = $"{OnlineServerUrl}/ignored/reserve";
        var payload = new OnlineIgnoredAddRequest
        {
            ApiKey = apiKey,
            Territory = territory,
            Keys = new[] { key }
        };

        _ = Task.Run(async () =>
        {
            var ok = false;
            try
            {
                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await httpClient.PostAsync(url, content).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    if (config.devMode)
                        log.Warning("[AutoPalExplorer] 联机盲踩：reserve HTTP {Code}", resp.StatusCode);
                }
                else
                {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var dto = JsonSerializer.Deserialize<OnlineIgnoredReserveResponse>(text);
                    if (dto is null)
                    {
                        if (config.devMode)
                            log.Warning("[AutoPalExplorer] 联机盲踩：reserve 解析失败，返回为空。");
                    }
                    else if (!dto.Ok)
                    {
                        if (config.devMode)
                            log.Information("[AutoPalExplorer] 联机盲踩：reserve 被拒绝，Taken={TakenCount}，Key={Key}",
                                dto.Taken?.Length ?? 0, key);
                    }
                    else
                    {
                        ok = true;
                        // 成功抢到：本地也加入 ignoredBlindLocations，避免后面再选到
                        lock (ignoredBlindLocations)
                        {
                            ignoredBlindLocations.Add(key);
                        }

                        if (config.devMode)
                            log.Information("[AutoPalExplorer] 联机盲踩：reserve 成功，Key={Key}，服务器总数={Count}",
                                key, dto.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                if (config.devMode)
                    log.Warning($"[AutoPalExplorer] 联机盲踩：reserve 异常：{ex.Message}");
            }
            finally
            {
                // 换层/重置已经作废了这次请求：丢弃结果，也不要动状态位
                // （否则可能覆盖掉换层后新发起的请求）
                if (gen == reserveGeneration)
                {
                    // 顺序很重要：先写结果，最后放开 inflight（volatile release 保证主线程可见性）
                    reserveSucceeded = ok;
                    reserveHasResult = true;
                    reserveInFlight = false;
                }
            }
        });
    }


    /// <summary>
    /// 盲踩埋藏宝藏逻辑：
    /// - pomanderManager.HasBuriedBuff 为 false 时生效；
    /// - 从 PalacePal 数据库中取出当前 Territory 的 Type=2 坐标；
    /// - 选最近一个没被忽略的点，引导玩家走过去；
    /// - 到点后停 5 秒，然后标记该点为忽略；
    /// - 如果前往途中 4 秒几乎没移动，则判定“卡住”，也忽略该点。
    /// 返回 true 表示本帧由盲踩逻辑接管。
    /// </summary>
    private bool TryHandleBlindBuriedSearch(Vector3 playerPos)
    {
        // 没配置 DB 就不跑
        if (string.IsNullOrEmpty(config.PalacePalDbPath))
            return false;

        // 如果途中已经拿到埋藏宝藏 Buff 或已经开过本层埋藏宝藏，停止盲踩
        if (pomanderManager.HasBuriedBuff || hasOpenBurinedChest)
            return false;

        EnsureBlindLocationsLoaded();

        if (blindLocations.Count == 0 && !config.BlindChestsWithTrap)
            return false;

        if (allBlindLocations.Count == 0 && config.BlindChestsWithTrap)
            return false;

        // ====== 关键：当前没有目标时，先抢点 ======
        if (currentBlindTarget is null)
        {
            if (IsOnlineMode)
            {
                // 联机模式：预定要走网络，绝不能在主线程上同步等待（高延迟会卡帧）。
                // 改为非阻塞状态机：本帧最多发起一个后台预定请求，然后立刻返回，
                // 结果由后续帧轮询处理。
                if (!TryPickBlindTargetOnline(playerPos))
                {
                    // 还没抢到目标（请求在路上 / 没更多点）：盲踩逻辑占用本帧但不阻塞。
                    return true;
                }
            }
            else
            {
                // 本地模式：没有网络，直接同步选点即可。
                var next = GetNextBlindLocation(playerPos, config.BlindMaxDistance);
                if (next is null)
                {
                    if (config.devMode)
                        log.Information("[AutoPalExplorer] 盲踩：没有更多可踩的坐标。");
                    return false;
                }

                var candidate = next.Value;
                currentBlindTarget = candidate;
                blindArrivedAt = DateTime.MinValue;
                blindLastProgressPos = playerPos;
                blindLastProgressCheckAt = DateTime.UtcNow;

                if (config.devMode)
                {
                    log.Information("[AutoPalExplorer] 盲踩：选择新目标 ({X:0.00}, {Y:0.00}, {Z:0.00})，Key={Key}。",
                        candidate.X, candidate.Y, candidate.Z, PackBlindKey(candidate));
                }
            }
        }

        // ====== 后面的逻辑保持不变：走路、到点等待、卡住检测 ======

        var target = currentBlindTarget.Value;
        var dx = target.X - playerPos.X;
        var dz = target.Z - playerPos.Z;
        var distSq = dx * dx + dz * dz;

        if (distSq <= BlindArriveRadius * BlindArriveRadius)
        {
            if (blindArrivedAt == DateTime.MinValue)
            {
                blindArrivedAt = DateTime.UtcNow;
                navigator.Stop();

                if (config.devMode)
                    log.Information("[AutoPalExplorer] 盲踩：已到盲踩目标点，开始原地等待 {Seconds}s。", BlindWaitDuration.TotalSeconds);
            }
            else
            {
                var elapsed = DateTime.UtcNow - blindArrivedAt;
                if (elapsed >= BlindWaitDuration)
                {
                    IgnoreBlindLocation(target);
                    TickOnlineIgnoredSync();
                    currentBlindTarget = null;
                    blindArrivedAt = DateTime.MinValue;

                    if (config.devMode)
                        log.Information("[AutoPalExplorer] 盲踩：在目标点停留 {Elapsed:0.0}s，标记为已踩过并忽略。", elapsed.TotalSeconds);
                }
            }

            return true;
        }

        if (!navigator.IsBusy || IsDifferentTarget(navigator.CurrentTarget, target, 0.5f))
        {
            navigator.Stop();
            navigator.TryMoveTo(target);

            if (config.devMode)
                log.Information("[AutoPalExplorer] 盲踩：导航前往目标点。");
        }
        else
        {
            var now = DateTime.UtcNow;
            if ((now - blindLastProgressCheckAt) >= BlindStuckTimeout)
            {
                var mdx = playerPos.X - blindLastProgressPos.X;
                var mdz = playerPos.Z - blindLastProgressPos.Z;
                var moveSq = mdx * mdx + mdz * mdz;

                if (moveSq < BlindStuckMoveThreshold * BlindStuckMoveThreshold)
                {
                    if (config.devMode)
                        log.Information("[AutoPalExplorer] 盲踩：前往目标途中疑似卡住，忽略该盲踩点位。");

                    IgnoreBlindLocation(target);
                    currentBlindTarget = null;
                    blindArrivedAt = DateTime.MinValue;
                    navigator.Stop();
                }
                else
                {
                    blindLastProgressPos = playerPos;
                }

                blindLastProgressCheckAt = now;
            }
        }

        return true;
    }

    private bool HasDeadOtherPlayer()
    {
        var localId = objectTable.LocalPlayer?.GameObjectId ?? 0;

        foreach (var ch in objectTable.PlayerObjects)
        {
            if (ch.GameObjectId == localId)
                continue; // 自己死了也没法走过去，就不算在这里

            if (ch.IsDead)
                return true;
        }

        return false;
    }

    private IGameObject? FindNextChestToOpen(Vector3 playerPos)
    {
        IGameObject? best = null;
        var bestDistSq = float.MaxValue;

        foreach (var obj in objectTable)
        {
            // 只看事件物件（宝藏和EventObj）
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj && obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure)
                continue;

            // 根据配置决定开不打开这种箱子
            if (!ShouldOpenChest(obj.BaseId))
                continue;

            var dx = obj.Position.X - playerPos.X;
            var dz = obj.Position.Z - playerPos.Z;
            var distSq = dx * dx + dz * dz;

            // ✅ 情况一：在开箱半径内，但已经不可交互
            // 说明 99% 是刚开完的箱子（或者被队友开完），直接加入 ignore，避免一直把它当目标。
            if (!obj.IsTargetable && distSq <= ChestDoneRadius * ChestDoneRadius && !ObjectIds.IsBuriedChest(obj.BaseId))
            {
                if (ignoredChestIds.Add(obj.GameObjectId) && config.devMode)
                {
                    log.Information(
                        "[AutoPalExplorer] 宝箱 BaseId={BaseId} 在 ChestDoneRadius 内且不可交互，视为已开，加入 ignore 列表。",
                        obj.BaseId
                    );
                }
                continue;
            }

            // 不可交互而且距离很远：可能是别层/奇怪残影，一律不当成候选
            if (!obj.IsTargetable)
                continue;

            // 忽略列表里的箱子直接跳过
            if (IsIgnoredChest(obj))
                continue;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = obj;
            }
        }

        return best;
    }

    private void BreakActWithShift()
    {
        try
        {
            // 按下 Shift
            keyState[VirtualKey.SHIFT] = true;

            // 1 帧后抬起（可以根据需要改成 delayTicks: 2 或 TimeSpan）
            _ = framework.RunOnTick(
                () =>
                {
                    keyState[VirtualKey.SHIFT] = false;
                    if (config.devMode)
                        log.Information("[AutoPalExplorer] 已抬起 Shift，用于打断 ACT/E。");
                },
                delayTicks: 1
            );

            if (config.devMode)
                log.Information("[AutoPalExplorer] 按下 Shift 用于打断 ACT/E。");
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] BreakActWithShift 异常：{ex}");
        }
    }

    private void TickOnlineIgnoredSync()
    {
        if (!IsOnlineMode)
            return;

        // 没填 api_key 就不走联机逻辑
        var apiKey = config.OnlineApiKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        // 防止刷屏请求：每 2 秒一次，同时只允许一个请求在路上
        // var now = DateTime.UtcNow;
        // if (onlineSyncInProgress || now < nextOnlineFetchAt)
        //     return;

        // onlineSyncInProgress = true;
        // nextOnlineFetchAt = now.AddSeconds(2);

        var territory = clientState.TerritoryType;
        var url = $"{OnlineServerUrl}/ignored?territory={territory}&api_key={Uri.EscapeDataString(apiKey)}";

        _ = Task.Run(async () =>
        {
            try
            {
                using var resp = await httpClient.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    if (config.devMode)
                        log.Warning("[AutoPalExplorer] 联机盲踩：GET ignored 返回 {Code}", resp.StatusCode);
                    return;
                }

                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                // log.Warning(text);
                var dto = JsonSerializer.Deserialize<OnlineIgnoredGetResponse>(text);
                // log.Warning(dto.Keys.Length.ToString());
                if (dto == null)
                {
                    log.Warning("dto null");
                    return;
                }

                lock (ignoredBlindLocations)
                {
                    ignoredBlindLocations.Clear();
                    foreach (var k in dto.Keys)
                        ignoredBlindLocations.Add(k);
                }

                if (config.devMode)
                    log.Information("[AutoPalExplorer] 联机盲踩：从服务器同步 {Count} 个 ignoredBlindLocations。", dto.Keys.Length);
            }
            catch (Exception ex)
            {
                if (config.devMode)
                    log.Warning($"[AutoPalExplorer] 联机盲踩：同步 ignored 失败：{ex.Message}");
            }
            finally
            {
                onlineSyncInProgress = false;
            }
        });
    }
}
