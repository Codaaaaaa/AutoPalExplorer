using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

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
    private float ExitStopRadius => MathF.Max(0.1f, config.ExitStopRadius);
    private float ChestDoneRadius => MathF.Max(0.1f, config.ChestDoneRadius);
    private float BuriedChestDoneRadius => MathF.Max(0.1f, config.BuriedChestDoneRadius);
    private bool isBossFloor;
    private bool isBossFloorQueueing;
    private double ChallengeIntervalSeconds => MathF.Max(1.0f, config.ChallengeIntervalSeconds);
    private DateTime nextChallengeAttemptAt = DateTime.MinValue;
    private float EnemySearchRadius => MathF.Max(1.0f, config.EnemySearchRadius);
    private float TrapAvoidRadiusCfg => MathF.Max(0.1f, config.TrapAvoidRadius);
    private int ChestInteractIntervalMs => Math.Max(50, config.ChestInteractIntervalMs);
    private bool nextLevelBool = false;
    private bool hasOpenedNextPilgrimWindow = false;

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

        if (config.devMode)
            log.Information("[AutoPalExplorer] 已停止。");
    }

    /// <summary>
    /// 从插件 OnChatMessage 调用，当聊天出现“传送装置已激活”等信息时。
    /// </summary>
    public void NotifyExitActivated()
    {
        exitDetector.MarkExitActivated();
        if (config.devMode)
            log.Information("[AutoPalExplorer] 收到传送装置激活通知。");
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
        if (config.devMode)
            log.Information("[AutoPalExplorer] 标记换层（nextLevelActivated）。");
    }
    public void NotifyBossFloor()
    {
        isBossFloor = true;
        isBossFloorQueueing = false;
        hasOpenedNextPilgrimWindow = false;
        nextChallengeAttemptAt = DateTime.MinValue;

        if (config.devMode)
            log.Information("[AutoPalExplorer] 检测到 Boss 层聊天提示，启用 Boss 房逻辑。");
    }

    public void NotifyChallengeRequestSent()
    {
        // 收到“成功发送了参加申请”，说明排队申请已发出，可以退出 Boss 流程
        isBossFloor = false;
        isBossFloorQueueing = false;
        hasOpenedNextPilgrimWindow = false;
        nextChallengeAttemptAt = DateTime.MinValue;

        if (config.devMode)
            log.Information("[AutoPalExplorer] 收到成功发送参加申请提示，结束 Boss 流程逻辑。");
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

        var pos = player.Position;
        if (config.devMode)
        {
            log.Information("[AutoPalExplorer] Update Tick：Territory={Territory}, 位置=({X:0.00}, {Y:0.00}, {Z:0.00})，BMRAI={Bmrai}，NavigatorBusy={Busy}",
                clientState.TerritoryType, pos.X, pos.Y, pos.Z, bmraiOn, navigator.IsBusy);
        }

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

            if (config.devMode)
                log.Information("[AutoPalExplorer] 检测到换层，已重置状态 (Territory={Territory}).", clientState.TerritoryType);
        }

        // 0.5 检测状态并且使用魔陶器
        if (!isBossFloor && !isBossFloorQueueing)
            pomanderManager.UsingPomander();
        
        // 1. 战斗状态：交给 BMRAI，暂停导航
        var inCombat = condition[ConditionFlag.InCombat];
        if (inCombat)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] 当前处于战斗中，交给 BMRAI 处理移动/战斗。");

            if (!bmraiOn)
                EnsureBmraiOn();

            if (navigator.IsBusy && config.devMode)
                log.Information("[AutoPalExplorer] 战斗中停止导航。");

            navigator.Stop();
            return;
        }
        else
        {
            if (bmraiOn)
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] 脱离战斗，关闭 BMRAI。");
                EnsureBmraiOff();
            }
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
            if (HandleBossFloorQueueing(pos))
                return; // 队列逻辑接管
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

        // ==== 3.1 宝箱（优先度：有就去） ====
        if (exitDetector.HasChest && exitDetector.Chest is { } chest)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] 检测到宝箱 BaseId={BaseId}, 位置=({X:0.00},{Y:0.00},{Z:0.00}), Targetable={Targetable}",
                    chest.BaseId, chest.Position.X, chest.Position.Y, chest.Position.Z, chest.IsTargetable);

            if (!ShouldOpenChest(chest.BaseId))
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] 配置不允许当前类型宝箱，跳过。");
            }
            else if (chest.IsTargetable)
            {
                var dx = chest.Position.X - pos.X;
                var dz = chest.Position.Z - pos.Z;
                var distSq = dx * dx + dz * dz;

                if (distSq > ChestDoneRadius * ChestDoneRadius)
                {
                    if (config.devMode)
                        log.Information("[AutoPalExplorer] 前往宝箱中，当前距离={Dist:0.00} (> {R})。",
                            MathF.Sqrt(distSq), ChestDoneRadius);

                    if (IsDifferentTarget(currentTarget, chest.Position, 1.0f))
                    {
                        if (config.devMode)
                            log.Information("[AutoPalExplorer] 切换导航目标为宝箱。");
                        navigator.Stop();
                        navigator.TryMoveTo(chest.Position);
                    }
                    return;
                }
                else
                {
                    if (config.devMode)
                        log.Information("[AutoPalExplorer] 已到宝箱边，尝试交互开箱。");
                    TryOpenChest(chest);
                    // 不 return，让后续逻辑继续，看是否有门/怪
                }
            }
            else
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] 宝箱目前不可交互 (可能已开/动画中)，跳过。");
            }
        }

        // ==== 3.2 激活传送装置（最高优先级） ====
        if (exitDetector.HasActiveExit && exitDetector.Exit is { } activeExit)
        {
            var dx = activeExit.Position.X - pos.X;
            var dz = activeExit.Position.Z - pos.Z;
            var distSq = dx * dx + dz * dz;

            if (config.devMode)
                log.Information("[AutoPalExplorer] 检测到已激活传送装置，距离={Dist:0.00}。",
                    MathF.Sqrt(distSq));

            if (distSq <= ExitStopRadius * ExitStopRadius)
            {
                if (navigator.IsBusy)
                {
                    navigator.Stop();
                    if (config.devMode)
                        log.Information("[AutoPalExplorer] 已到达激活传送装置旁，停止导航等待玩家手动交互。");
                }
                return;
            }

            if (!navigator.IsBusy || IsDifferentTarget(currentTarget, activeExit.Position, 1.0f))
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] 导航至已激活传送装置。");
                navigator.Stop();
                navigator.TryMoveTo(activeExit.Position);
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

    private unsafe void TryOpenChest(IGameObject chest)
    {
        if (!ShouldOpenChest(chest.BaseId))
            return;

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
            }
            else
            {
                // 已到出口旁边，尝试交互
                TryInteractWithObject(exitObj, "Boss层出口");
                // 可选：交互后清掉 Boss 标记，避免下一层误用
                isBossFloorQueueing = true;
                nextChallengeAttemptAt = DateTime.UtcNow.AddSeconds(ChallengeIntervalSeconds);

                if (config.devMode)
                    log.Information("[AutoPalExplorer] [Boss层] 已与 Boss 出口交互，进入挑战下一朝圣路流程。");
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

}
