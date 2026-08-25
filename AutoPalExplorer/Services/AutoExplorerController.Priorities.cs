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
    /// <summary>换层重置：由 nextLevelBool 触发，清理上一层的探索 / 宝箱 / 盲踩等状态。</summary>
    private void HandleLevelChangeReset()
    {
        if (!nextLevelBool)
            return;

        nextLevelBool = false;
        lastTerritoryType = clientState.TerritoryType;
        hasLitCandle = false; // 换层重置：新一层可以再互动光耀烛台
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
        ResetRoomState();         // 房间图：上一层的标定 / 目标 / 拉黑名单全部作废
        ResetTrapAvoidState();    // 避陷阱：上一层的陷阱圈 / navmesh 投影缓存作废
        ResetFloorSpecialState(); // 99/100 层收尾状态（不清 currentFloor，那是聊天设置的）
        ClearLockedChest();

        if (config.devMode)
            log.Information("[AutoPalExplorer] 检测到换层，已重置状态 (Territory={Territory}).", clientState.TerritoryType);
    }

    /// <summary>重置 99 / 100 层特殊收尾流程的状态（不重置 currentFloor，它由聊天“第N朝圣路”维护）。</summary>
    private void ResetFloorSpecialState()
    {
        floor99AltarInteracted = false;
        floor99AltarInteractedAt = DateTime.MinValue;
        floor100Stage = Floor100Stage.MovingToCoord;
        floor100InteractedAt = DateTime.MinValue;
    }

    /// <summary>0.5 检测状态并使用魔陶器（Boss 层 / 排队中不使用）。</summary>
    private void TickPomanderUsage()
    {
        if (!isBossFloor && !isBossFloorQueueing && config.UsingPomander)
            pomanderManager.UsingPomander();
    }

    /// <summary>
    /// 1. 本地进战处理：进战时交给 BMRAI 并暂停导航（返回 true 接管本帧）；
    /// 脱战时关闭 BMRAI 后返回 false，让后续探索逻辑继续。
    /// </summary>
    private bool HandleCombat(bool inCombat)
    {
        if (inCombat)
        {
            SetIntent("战斗中：交给 BMRAI 处理，暂停导航");
            if (config.devMode)
                log.Information("[AutoPalExplorer] 当前处于战斗中，交给 BMRAI 处理移动/战斗。");

            if (!bmraiOn)
                EnsureBmraiOn();

            if (navigator.IsBusy && config.devMode)
                log.Information("[AutoPalExplorer] 战斗中停止导航。");

            navigator.Stop();
            return true;
        }

        if (bmraiOn)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] 脱离战斗，关闭 BMRAI 和 Rotation。");
            EnsureBmraiOff();
            // 之前只关了 BMRAI 没关 Rotation（EnsureBmraiOff 里的 /rotation Off 被注释掉了），
            // 导致脱战后 Rotation 仍在运行。这里补一刀，脱战时一并关闭。
            EnsureRotationOff();
        }

        return false;
    }

    /// <summary>
    /// Boss 房阶段调度：
    /// - 1.1 进入 Boss 房（未排队）：交给 HandleBossFloor 打 Boss / 找出口；
    /// - 1.2 已传送出、正在排队“挑战下一朝圣路”：交给 HandleBossFloorQueueing（内部只有队长真正排本）。
    /// </summary>
    private bool HandleBossFloorPhase(Vector3 pos)
    {
        // 1.1 是否进入 boss 房间
        if (isBossFloor && !isBossFloorQueueing)
        {
            if (HandleBossFloor(pos))
                return true; // 已经处理了（找 Boss 或找出口），不走下面普通逻辑
        }

        // 1.2 已从 Boss 房传送出，正在处理“挑战下一朝圣路”
        if (isBossFloor && isBossFloorQueueing)
        {
            pomanderManager.ResetBuff();
            pomanderManager.ResetBuriedBuff();

            if (HandleBossFloorQueueing(pos))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 2.1 再生祭坛：祭坛已激活且有队友阵亡时，前往祭坛并交互复活队友。
    /// 找不到祭坛坐标时返回 false，交回后续逻辑。
    /// </summary>
    private bool HandleRegenerationAltar(Vector3 pos, Vector3? currentTarget)
    {
        if (!(regenerationActivated && HasDeadOtherPlayer()))
            return false;

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
        if (regenPos is not { } rp)
            return false;

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
            return true;
        }

        // 不在范围内：导航过去（带简单避陷阱）
        if (!navigator.IsBusy || IsDifferentTarget(currentTarget, rp, 1.0f))
        {
            SetIntent("再生祭坛：前往（有队友阵亡）");
            if (config.devMode)
                log.Information("[AutoPalExplorer] 导航至再生祭坛。");

            TrySafeMoveTo(rp, TrapAvoidRadiusCfg);
        }

        return true;
    }

    /// <summary>
    /// 光耀烛台：找到烛台对象就前往并交互（交互方式同再生祭坛）。
    /// maxDistance 限制触发距离：≤30m 时以高优先级抢在宝箱前；宝箱之后再用 float.MaxValue 兜底任意距离。
    /// 找不到（或超出 maxDistance）时返回 false，交回后续逻辑。
    /// </summary>
    private bool HandleRadiantCandlestand(Vector3 pos, Vector3? currentTarget, float maxDistance)
    {
        // 本层已点亮过烛台（聊天“点亮了光耀烛台”）就不再互动，交回后续逻辑；换层会重置 hasLitCandle
        if (hasLitCandle)
            return false;

        // 找最近的、在 maxDistance 内的烛台对象
        IGameObject? candle = null;
        var bestDistSq = maxDistance * maxDistance;
        foreach (var obj in objectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)
                continue;

            if (!ObjectIds.RadiantCandlestandIds.Contains(obj.BaseId))
                continue;

            var ddx = obj.Position.X - pos.X;
            var ddz = obj.Position.Z - pos.Z;
            var dsq = ddx * ddx + ddz * ddz;
            if (dsq < bestDistSq)
            {
                bestDistSq = dsq;
                candle = obj;
            }
        }

        if (candle is null)
            return false;

        var cp = candle.Position;
        var dx = cp.X - pos.X;
        var dz = cp.Z - pos.Z;
        var distSq = dx * dx + dz * dz;

        if (config.devMode)
            log.Information("[AutoPalExplorer] 光耀烛台：距离={Dist:0.00}。", MathF.Sqrt(distSq));

        // 到点附近：停下并尝试交互
        if (distSq <= ChestDoneRadius * ChestDoneRadius)
        {
            if (navigator.IsBusy)
                navigator.Stop();

            SetIntent("光耀烛台：交互");
            if (candle.IsTargetable)
                TryInteractWithObject(candle, "光耀烛台");

            return true;
        }

        // 不在范围内：导航过去（带简单避陷阱）
        if (!navigator.IsBusy || IsDifferentTarget(currentTarget, cp, 1.0f))
        {
            SetIntent("光耀烛台：前往");
            if (config.devMode)
                log.Information("[AutoPalExplorer] 导航至光耀烛台。");

            TrySafeMoveTo(cp, TrapAvoidRadiusCfg);
        }

        return true;
    }

    /// <summary>3.0 埋藏的宝藏（最高优先级实体）：找到就只做这一件事——前往并停在触发范围内等触发。</summary>
    private bool HandleBuriedChest(Vector3 pos, Vector3? currentTarget)
    {
        var buried = FindNearestBuriedChest(pos, 500f);
        if (buried is null || hasOpenBurinedChest)
            return false;

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

            return true; // ⭐ 关键：不再执行宝箱/门/贴墙逻辑
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

        return true; // ⭐ 有埋藏宝藏就只处理这一件事
    }

    /// <summary>
    /// 3.0b 盲踩埋藏宝藏位置（PalacePal 数据）：
    /// 只有还没有“埋藏宝藏”Buff 且本层未开过埋藏宝藏时才盲踩，否则交回上面的 buried 逻辑。
    /// </summary>
    private bool HandleBlindBuriedSearch(Vector3 pos)
    {
        if (!config.BlindChests)
            return false;

        if (config.devMode)
            log.Information("[AutoPalExplorer] 开始盲踩逻辑");

        if (pomanderManager.HasBuriedBuff || hasOpenBurinedChest)
            return false;

        if (!TryHandleBlindBuriedSearch(pos))
            return false;

        SetIntent("盲踩：前往疑似埋藏宝藏点");
        return true; // 被盲踩逻辑接管，本帧不走后续宝箱/门/贴墙
    }

    /// <summary>3.0c 锁定中的普通宝箱：防止在宝箱和门/敌人之间来回切。</summary>
    private bool HandleLockedChestPriority(Vector3 pos, Vector3? currentTarget)
    {
        if (!HandleLockedChest(pos, currentTarget))
            return false;

        SetIntent("前往锁定中的宝箱");
        return true;
    }

    /// <summary>3.1 普通宝箱：找到可开的宝箱就前往 / 开启。</summary>
    private bool HandleNormalChest(Vector3 pos, Vector3? currentTarget)
    {
        if (FindNextChestToOpen(pos) is not { } chest)
            return false;

        if (!HandleChest(pos, chest, currentTarget))
            return false;

        SetIntent("前往 / 开启宝箱");
        return true;
    }

    /// <summary>3.2 激活的传送装置：走到装置旁停下，等待玩家手动交互。</summary>
    private bool HandleActiveExit(Vector3 pos, Vector3? currentTarget)
    {
        // 限制：只要有队友阵亡就不去传送装置，先留在本层继续探索，
        // 直到找到并激活再生祭坛、复活队友后（无人阵亡）才允许前往传送装置。
        if (HasDeadOtherPlayer())
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] 有队友阵亡，暂不前往传送装置，继续探索寻找再生祭坛。");
            return false;
        }

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

        if (exitPos is not { } ep)
            return false;

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
            return true;
        }

        if (!navigator.IsBusy || IsDifferentTarget(currentTarget, ep, 1.0f))
        {
            SetIntent("前往激活的传送装置");
            if (config.devMode)
                log.Information("[AutoPalExplorer] 导航至激活传送装置位置。");
            navigator.Stop();
            navigator.TryMoveTo(ep);
        }

        return true;
    }

    /// <summary>3.3 最近怪物：范围内找到怪就前往 / 开怪（交给 EngageEnemy）；没怪则交回贴墙探索。</summary>
    private bool HandleNearestEnemy(Vector3 pos, IPlayerCharacter? player, Vector3? currentTarget)
    {
        var enemy = FindNearestEnemy(pos, EnemySearchRadius);
        if (enemy is null)
        {
            // 门附近也没怪：交给贴墙逻辑
            if (config.devMode)
                log.Information("[AutoPalExplorer] 未激活门附近没有敌人，交给贴墙探索逻辑。");
            return false;
        }

        EngageEnemy(enemy, pos, player, currentTarget);
        return true;
    }

    /// <summary>3.4 没有更高优先级：靠墙探索下一步（或保持当前路径）。</summary>
    private void HandleWallFollow()
    {
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

    /// <summary>
    /// 玩家阵亡处理（最高优先级）：
    /// - 若玩家还活着，返回 false，交回上层执行原本的探索逻辑。
    /// - 若玩家已死亡：停止导航，检测屏幕上的 SelectYesno（复活确认窗口），
    ///   有就点 0（“是”/o [int]: 0），点完带 1 秒节流；随后等待游戏自动复活。
    /// </summary>
    private unsafe bool HandlePlayerDeath(IPlayerCharacter player)
    {
        if (!(player.IsDead || player.CurrentHp == 0))
            return false;

        SetIntent("玩家已阵亡：停止移动，等待复活");

        // 死了就别再跑图
        if (navigator.IsBusy)
            navigator.Stop();

        var now = DateTime.UtcNow;
        if (now < nextReviveAddonFireAt)
            return true;

        if (TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yn) && IsAddonReady(yn))
        {
            Callback.Fire(yn, true, 0);
            nextReviveAddonFireAt = now.AddSeconds(1.0);

            if (config.devMode)
                log.Information("[AutoPalExplorer] [死亡] 检测到 SelectYesno，点击 0 确认复活。");
        }

        return true;
    }
}
