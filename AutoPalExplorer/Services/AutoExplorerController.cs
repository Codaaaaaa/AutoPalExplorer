using System;
using System.Numerics;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

using System.IO;
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

    private bool bmraiOn;
    private uint lastTerritoryType;
    private bool hasOpenBurinedChest = false;
    private DateTime lastChestInteractAt = DateTime.MinValue;
    // 记录传送装置 / 再生祭坛坐标 & 激活状态
    private Vector3? savedExitPos;
    private Vector3? savedRegenerationPos;
    private bool exitActivatedByChat;
    private bool regenerationActivated;
    private float ExitStopRadius => MathF.Max(0.1f, config.ExitStopRadius);
    private float ChestDoneRadius => MathF.Max(0.1f, config.ChestDoneRadius);
    private float BuriedChestDoneRadius => MathF.Max(0.1f, config.BuriedChestDoneRadius);
    private bool isBossFloor;
    private bool isBossFloorQueueing;
    private double ChallengeIntervalSeconds => MathF.Max(1.0f, config.ChallengeIntervalSeconds);
    private readonly double BossExitInteractDelaySeconds = 8.0;
    private DateTime bossExitReachedAt = DateTime.MinValue;
    private DateTime nextChallengeAttemptAt = DateTime.MinValue;
    private float EnemySearchRadius => MathF.Max(1.0f, config.EnemySearchRadius);
    private float TrapAvoidRadiusCfg => MathF.Max(0.1f, config.TrapAvoidRadius);
    private int ChestInteractIntervalMs => Math.Max(50, config.ChestInteractIntervalMs);
    private bool nextLevelBool = false;
    private bool hasOpenedNextPilgrimWindow = false;
    private readonly HashSet<ulong> ignoredChestIds = new(); // 需要跳过的宝箱
    private ulong lastChestInteractObjectId = 0;             // 最近一次尝试交互的宝箱ID

    // 跟车模式
    private bool wasInCombatOnBossFloor = false;
    private bool IsFollowMode => config.Mode == AutoMode.Follow;

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

    private static readonly TimeSpan BlindWaitDuration = TimeSpan.FromSeconds(3); // 在点上站 3 秒
    private static readonly TimeSpan BlindStuckTimeout = TimeSpan.FromSeconds(2); // 2 秒没动就判定卡住
    private const float BlindArriveRadius = 0.6f;          // 认为“到点”的半径
    private const float BlindStuckMoveThreshold = 0.2f;    // 判定卡住时允许的移动距离（2D）

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
        PomanderManager pomanderManager)
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
    }

    public void Start()
    {
        if (IsRunning)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] Start 调用被忽略：已经在运行中。");
            return;
        }

        if (clientState.LocalPlayer is null)
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
        isBossFloor = false;
        isBossFloorQueueing = false;
        ignoredChestIds.Clear();
        lastChestInteractObjectId = 0;
        bossExitReachedAt = DateTime.MinValue;
        ResetBlindWalkState();
        ResetStaticObjectsState();
        // 跟车
        wasInCombatOnBossFloor = false;

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
        ignoredChestIds.Clear();
        lastChestInteractObjectId = 0;
        isBossFloor = false;
        isBossFloorQueueing = false;
        bossExitReachedAt = DateTime.MinValue;
        ResetBlindWalkState();
        ResetStaticObjectsState();
        // 跟车
        wasInCombatOnBossFloor = false;
        TryChatCommand("123456789987654321");

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
            TryChatCommand("123456789987654321");
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

        var player = clientState.LocalPlayer;
        if (player is null)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] Update：本地玩家为空，等待。");
            return;
        }

        // 如果不在目标地图则结束
        if (!MapIds.IsPilgrimsTraverse(clientState.TerritoryType))
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] 不在目标地图 (Territory={TerritoryType})，停止运行。", clientState.TerritoryType);
            Stop();
            return;
        }

        var inCombat = condition[ConditionFlag.InCombat];
        var pos = player.Position;
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
            ignoredChestIds.Clear();
            lastChestInteractObjectId = 0;
            ResetBlindWalkState();
            ResetStaticObjectsState();
            // 跟车
            wasInCombatOnBossFloor = false;

            if (config.devMode)
                log.Information("[AutoPalExplorer] 检测到换层，已重置状态 (Territory={Territory}).", clientState.TerritoryType);
        }

        // 0.5 检测状态并且使用魔陶器
        if (!IsFollowMode)
        {
            // 跟车模式不使用魔陶器
            if (!isBossFloor && !isBossFloorQueueing)
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

                if (config.devMode)
                    log.Information("[AutoPalExplorer] 跟车模式：Boss 战结束，已关闭 BMRAI 和 Rotation。");
            }
        }

        // 1. 战斗状态：交给 BMRAI，暂停导航
        if (inCombat)
        {
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
                    if (config.devMode)
                        log.Information("[AutoPalExplorer] 导航至再生祭坛。");

                    TrySafeMoveTo(rp, TrapAvoidRadiusCfg);
                }

                return;
            }
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
                    return; // 被盲踩逻辑接管，本帧不走后续宝箱/门/贴墙
            }
        }
        
        // ==== 3.1 宝箱（优先度：有就去） ====
        if (FindNextChestToOpen(pos) is { } chest)
        {
            if (HandleChest(pos, chest, currentTarget))
                return;
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
            var ex = enemy.Position.X - pos.X;
            var ez = enemy.Position.Z - pos.Z;
            var edist = MathF.Sqrt(ex * ex + ez * ez);

            if (config.devMode)
                log.Information("[AutoPalExplorer] 找到最近敌人 Name={Name}, 距离={Dist:0.00}，发送导航到敌人位置。",
                    enemy.Name.TextValue, edist);

            if (!navigator.IsBusy || IsDifferentTarget(currentTarget, enemy.Position, 1.0f))
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] 导航至已激活传送装置。");
                navigator.Stop();
                navigator.TryMoveTo(enemy.Position);
            }

            // navigator.Stop();
            // navigator.TryMoveTo(enemy.Position);
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
                log.Warning("[AutoPalExplorer] 无可探索路径");
                // Stop();
            }
            else
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] 贴墙探索已生成新移动目标。");
            }
        }
        else
        {
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
        
        var player = clientState.LocalPlayer;
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
        TryCommand("/rotation Off");

        if (config.devMode)
            log.Information("[AutoPalExplorer] 已发送 BMRAI 关闭指令。");
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

            if (config.devMode)
                log.Information("[AutoPalExplorer] [Boss层] 找到出口(2005809)，距离={Dist:0.00}。", dist);

            if (distSq > ExitStopRadius * ExitStopRadius)
            {
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

            if (bn.BattleNpcKind != Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Enemy)
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
    }

    private void StartFollowLoop()
    {
        // TryCommand("/follow <2>");
        TryChatCommand("ygf2start");

        if (config.UseBmrai)
        {
            EnsureBmraiOn();
        }

        if (config.devMode)
            log.Information("[AutoPalExplorer] 跟车模式：已发送 /follow <2> + /bmrai on + /rotation Auto");
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
    }
    private void ResetStaticObjectsState()
    {
        savedExitPos = null;
        savedRegenerationPos = null;
        exitActivatedByChat = false;
        regenerationActivated = false;
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
            if (!navigator.IsBusy || IsDifferentTarget(currentTarget, chest.Position, 1.0f))
            {
                navigator.Stop();
                navigator.TryMoveTo(chest.Position);
            }
            return true; // 本帧由宝箱逻辑接管
        }

        // 已在开箱半径内
        TryOpenChest(chest);
        return true; // 建议这里也直接 true，后面不再处理门
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
        => ignoredBlindLocations.Contains(PackBlindKey(p));

    private void IgnoreBlindLocation(Vector3 p)
    {
        ignoredBlindLocations.Add(PackBlindKey(p));

        if (config.devMode)
            log.Information("[AutoPalExplorer] 盲踩：忽略点位 ({X:0.00}, {Y:0.00}, {Z:0.00})。",
                p.X, p.Y, p.Z);
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

        // 如果当前没有目标，挑一个最近的
        if (currentBlindTarget is null)
        {
            var next = GetNextBlindLocation(playerPos, config.BlindMaxDistance);
            if (next is null)
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] 盲踩：没有更多可踩的 Type=2 坐标。");
                return false;
            }

            currentBlindTarget = next.Value;
            blindArrivedAt = DateTime.MinValue;
            blindLastProgressPos = playerPos;
            blindLastProgressCheckAt = DateTime.UtcNow;

            if (config.devMode)
            {
                var t = currentBlindTarget.Value;
                log.Information("[AutoPalExplorer] 盲踩：选择新目标 ({X:0.00}, {Y:0.00}, {Z:0.00})。",
                    t.X, t.Y, t.Z);
            }
        }

        var target = currentBlindTarget.Value;
        var dx = target.X - playerPos.X;
        var dz = target.Z - playerPos.Z;
        var distSq = dx * dx + dz * dz;

        // 1) 已经到点：停 5 秒，然后忽略这个点
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
                    // 停满 5 秒：通知控制器忽略当前位置（这个点）
                    IgnoreBlindLocation(target);
                    currentBlindTarget = null;
                    blindArrivedAt = DateTime.MinValue;

                    if (config.devMode)
                        log.Information("[AutoPalExplorer] 盲踩：在目标点停留 {Elapsed:0.0}s，标记为已踩过并忽略。", elapsed.TotalSeconds);
                }
            }

            // 不管有没有刚好等完，本帧都算盲踩接管
            return true;
        }

        // 2) 还在路上：导航 & 卡住检测
        if (!navigator.IsBusy || IsDifferentTarget(navigator.CurrentTarget, target, 0.5f))
        {
            navigator.Stop();
            navigator.TryMoveTo(target);

            if (config.devMode)
                log.Information("[AutoPalExplorer] 盲踩：导航前往目标点。");
        }
        else
        {
            // 每 BlindStuckTimeout 秒检查一次有没有明显前进
            var now = DateTime.UtcNow;
            if ((now - blindLastProgressCheckAt) >= BlindStuckTimeout)
            {
                var mdx = playerPos.X - blindLastProgressPos.X;
                var mdz = playerPos.Z - blindLastProgressPos.Z;
                var moveSq = mdx * mdx + mdz * mdz;

                if (moveSq < BlindStuckMoveThreshold * BlindStuckMoveThreshold)
                {
                    // 判定为卡住：通知控制器忽略当前位置（这个目标点），不再来了
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
        var localId = clientState.LocalPlayer?.GameObjectId ?? 0;

        foreach (var obj in objectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player)
                continue;

            if (obj.GameObjectId == localId)
                continue; // 自己死了也没法走过去，就不算在这里

            if (obj is ICharacter ch && ch.IsDead)
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
            // 只看事件物件（箱子）
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)
                continue;

            // 根据配置决定开不打开这种箱子
            if (!ShouldOpenChest(obj.BaseId))
                continue;

            var dx = obj.Position.X - playerPos.X;
            var dz = obj.Position.Z - playerPos.Z;
            var distSq = dx * dx + dz * dz;

            // ✅ 情况一：在开箱半径内，但已经不可交互
            // 说明 99% 是刚开完的箱子（或者被队友开完），直接加入 ignore，避免一直把它当目标。
            if (!obj.IsTargetable && distSq <= ChestDoneRadius * ChestDoneRadius)
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
}
