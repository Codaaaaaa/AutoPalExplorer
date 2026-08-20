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

        // 2) 没有敌人 -> 认为 Boss 已击破
        // 99 层特殊收尾：不走普通出口，改为交互 2014940 → 等 5 秒 → 走进传送装置传送到 100 层
        if (currentFloor == 99)
            return HandleFloor99Exit(pos);

        // 普通 Boss 层：前往出口 BaseId=2005809
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

        // 非队长：到 WaitingRoom 后原地站着不动，不去互动“下一层入口”NPC 排本，
        // 只点掉队长排本弹给自己的确认窗口，等着被带进下一层。
        if (!IsPartyLeader())
        {
            if (navigator.IsBusy)
                navigator.Stop();

            SetIntent("Boss层：非队长，原地等待队长排本（不互动NPC）");
            if (config.devMode)
                log.Information("[AutoPalExplorer] [Boss层] [Queue] 非队长：原地等待，不互动下一层入口NPC。");
            return true;
        }

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
    /// - DeepDungeonResult -> Callback.Fire(a, true, -1)
    ///     （30/50/70/90 层 Boss 结束、与退出点交互后会弹结算界面，点掉后回到入口地图 816 重新进本）
    /// - DeepDungeonMenu   -> Callback.Fire(a, true, 0)
    /// - SelectYesno       -> Callback.Fire(a, true, 0)
    /// 带节流，避免每帧对同一个窗口狂点。返回是否点了其中一个。
    /// </summary>
    private unsafe bool TryConfirmBossQueueAddons()
    {
        var now = DateTime.UtcNow;
        if (now < nextBossAddonFireAt)
            return false;

        if (TryGetAddonByName<AtkUnitBase>("DeepDungeonResult", out var result) && IsAddonReady(result))
        {
            Callback.Fire(result, true, -1);
            nextBossAddonFireAt = now.AddSeconds(1.0);

            if (config.devMode)
                log.Information("[AutoPalExplorer] [Boss层] [Queue] 点击 DeepDungeonResult -> -1（阶段结算，返回入口重进）。");
            return true;
        }

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

    /// <summary>
    /// 99 层 Boss 已清后的收尾：
    /// 1) 找 2014940 走过去交互；
    /// 2) 交互后等 5 秒；
    /// 3) 走进传送装置（exitIds，如 2014756），进入后游戏自动把人传送到 100 层。
    /// </summary>
    private bool HandleFloor99Exit(Vector3 pos)
    {
        EnsureBmraiOff();
        EnsureRotationOff();

        // 阶段 1：前往并交互 2014940
        if (!floor99AltarInteracted)
        {
            var altar = FindObjectByBaseId(ObjectIds.Floor99AltarBaseId);
            if (altar is null)
            {
                SetIntent("99层：Boss 已清，等待 2014940 出现");
                return true;
            }

            var adx = altar.Position.X - pos.X;
            var adz = altar.Position.Z - pos.Z;
            var adistSq = adx * adx + adz * adz;

            if (adistSq > ExitStopRadius * ExitStopRadius)
            {
                SetIntent("99层：前往 2014940");
                if (!navigator.IsBusy || IsDifferentTarget(navigator.CurrentTarget, altar.Position, 1.0f))
                {
                    navigator.Stop();
                    navigator.TryMoveTo(altar.Position);
                }
                return true;
            }

            if (navigator.IsBusy)
                navigator.Stop();

            if (altar.IsTargetable)
            {
                TryInteractWithObject(altar, "99层物件(2014940)");
                floor99AltarInteracted = true;
                floor99AltarInteractedAt = DateTime.UtcNow;
                SetIntent("99层：已交互 2014940，等待 5 秒");

                if (config.devMode)
                    log.Information("[AutoPalExplorer] [99层] 已交互 2014940，等待 {Sec}s 后走进传送装置。", Floor99WaitSeconds);
            }
            return true;
        }

        // 阶段 2：等待 5 秒
        if ((DateTime.UtcNow - floor99AltarInteractedAt).TotalSeconds < Floor99WaitSeconds)
        {
            SetIntent("99层：交互后等待中");
            if (navigator.IsBusy)
                navigator.Stop();
            return true;
        }

        // 阶段 3：走进传送装置（进入后自动传送到 100 层，之后由换层逻辑接管）
        var teleport = FindObjectByBaseIds(ObjectIds.exitIds);
        if (teleport is null)
        {
            SetIntent("99层：等待传送装置出现");
            return true;
        }

        var tdx = teleport.Position.X - pos.X;
        var tdz = teleport.Position.Z - pos.Z;
        var tdistSq = tdx * tdx + tdz * tdz;
        if (tdistSq > ExitStopRadius * ExitStopRadius)
        {
            SetIntent("99层：走进传送装置");
            if (!navigator.IsBusy || IsDifferentTarget(navigator.CurrentTarget, teleport.Position, 1.0f))
            {
                navigator.Stop();
                navigator.TryMoveTo(teleport.Position);
            }
            return true;
        }

        SetIntent("99层：已进入传送装置，等待传送至 100 层");
        return true;
    }

    /// <summary>
    /// 100 层收尾流程（从 99 层传送过来后，isBossFloor 已在 NotifyFloorNumber 里清掉）：
    /// 1) 移动到固定坐标 Floor100StartPoint；
    /// 2) 移动到 2014754 并交互；
    /// 3) 交互后等 3 秒，退出点 2005809 出现；
    /// 4) 走到退出点交互，随后 SelectYesno→0、DeepDungeonResult→退出（与 30/50 层一致，交给 TryConfirmBossQueueAddons）。
    /// </summary>
    private bool HandleFloor100(Vector3 pos)
    {
        // 每帧尝试点掉可能弹出的确认窗口（SelectYesno→0 / DeepDungeonResult→退出 / DeepDungeonMenu→0）
        TryConfirmBossQueueAddons();

        switch (floor100Stage)
        {
            case Floor100Stage.MovingToCoord:
            {
                var dx = Floor100StartPoint.X - pos.X;
                var dz = Floor100StartPoint.Z - pos.Z;
                if (dx * dx + dz * dz > Floor100ReachRadius * Floor100ReachRadius)
                {
                    SetIntent("100层：移动到起始坐标");
                    if (!navigator.IsBusy || IsDifferentTarget(navigator.CurrentTarget, Floor100StartPoint, 1.0f))
                    {
                        navigator.Stop();
                        navigator.TryMoveTo(Floor100StartPoint);
                    }
                    return true;
                }

                if (navigator.IsBusy)
                    navigator.Stop();
                floor100Stage = Floor100Stage.MovingToInteract;
                return true;
            }

            case Floor100Stage.MovingToInteract:
            {
                var obj = FindObjectByBaseId(ObjectIds.Floor100InteractBaseId);
                if (obj is null)
                {
                    SetIntent("100层：等待 2014754 出现");
                    return true;
                }

                var dx = obj.Position.X - pos.X;
                var dz = obj.Position.Z - pos.Z;
                if (dx * dx + dz * dz > ExitStopRadius * ExitStopRadius)
                {
                    SetIntent("100层：前往 2014754");
                    if (!navigator.IsBusy || IsDifferentTarget(navigator.CurrentTarget, obj.Position, 1.0f))
                    {
                        navigator.Stop();
                        navigator.TryMoveTo(obj.Position);
                    }
                    return true;
                }

                if (navigator.IsBusy)
                    navigator.Stop();

                if (obj.IsTargetable)
                {
                    TryInteractWithObject(obj, "100层物件(2014754)");
                    floor100InteractedAt = DateTime.UtcNow;
                    floor100Stage = Floor100Stage.WaitingAfterInteract;
                    SetIntent("100层：已交互 2014754，等待 3 秒");
                }
                return true;
            }

            case Floor100Stage.WaitingAfterInteract:
            {
                if ((DateTime.UtcNow - floor100InteractedAt).TotalSeconds < Floor100WaitSeconds)
                {
                    SetIntent("100层：交互后等待退出点出现");
                    if (navigator.IsBusy)
                        navigator.Stop();
                    return true;
                }

                floor100Stage = Floor100Stage.GoingToExit;
                return true;
            }

            case Floor100Stage.GoingToExit:
            {
                EnsureBmraiOff();
                EnsureRotationOff();

                var exitObj = FindObjectByBaseId(ObjectIds.BossExitBaseId); // 2005809
                if (exitObj is null)
                {
                    SetIntent("100层：等待退出点(2005809)出现");
                    return true;
                }

                var dx = exitObj.Position.X - pos.X;
                var dz = exitObj.Position.Z - pos.Z;
                if (dx * dx + dz * dz > ExitStopRadius * ExitStopRadius)
                {
                    SetIntent("100层：前往退出点");
                    if (!navigator.IsBusy || IsDifferentTarget(navigator.CurrentTarget, exitObj.Position, 1.0f))
                    {
                        navigator.Stop();
                        navigator.TryMoveTo(exitObj.Position);
                    }
                    return true;
                }

                if (navigator.IsBusy)
                    navigator.Stop();

                if (exitObj.IsTargetable)
                {
                    TryInteractWithObject(exitObj, "100层退出点(2005809)");
                    floor100Stage = Floor100Stage.Done;
                    SetIntent("100层：已交互退出点，点确认窗口退出");
                }
                return true;
            }

            case Floor100Stage.Done:
            default:
                // 交互后弹出的 SelectYesno / DeepDungeonResult 由顶部 TryConfirmBossQueueAddons 逐个点掉
                SetIntent("100层：等待并点击退出确认窗口");
                return true;
        }
    }

    /// <summary>
    /// 在入口地图（terr 816）自动进本：
    /// 1. 靠近入口坐标（不足 5 格用 /vnav flyto 接近）；
    /// 2. 交互入口物件 (BaseId=1054942)；
    /// 3. DeepDungeonMenu -> callback(0)；
    /// 4. DeepDungeonSaveData -> callback(0,0)；
    /// 5. SelectString -> 选 0；
    /// 6. 之后不断 SelectYesno 选 0，直到出现 SelectString 再选 0，进入地宫。
    /// 收到“成功发送了参加申请”后结束。
    /// </summary>
    private void HandleDungeonEntry(Vector3 pos)
    {
        // 非队长：不排本、不选存档进本，只在「队长要重开一轮」时同步删掉自己的存档，
        // 其余时间原地站着等队长把自己带进本。
        if (!IsPartyLeader())
        {
            HandleNonLeaderEntry(pos);
            return;
        }

        // 轮次控制：回到入口时先判断是否“一轮打完”（打满轮数会直接 Stop）。仅队长驱动排本轮次。
        // 注意：每轮等待放在“删除存档之后”，由进本序列里的 EnqueueRoundWaitAfterDelete 处理，这里不再前置等待。
        // 财运亨通模式永远续打同一个存档，不走删档 / 轮次那一套
        if (config.EnableRoundLimit && !config.FortuneMode)
        {
            HandleRoundTransitionAtEntrance();
            if (!IsRunning)
                return; // 打满轮数，已 Stop
        }

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
        TryCommand("/vnav stop");
        SetIntent("地宫入口：交互并处理进本菜单");
        EnqueueEntrySequence(entryObj.GameObjectId);
        if (config.devMode)
            log.Information("[AutoPalExplorer] [入口] 已到入口附近，开始交互进本序列（队长）。");
    }

    /// <summary>
    /// 入口地图上的队员（非队长）：
    /// - 组队进本要求全队存档进度一致，所以队长删档重开的那一次，队员也必须把自己的存档删掉；
    ///   删档时机跟队长的 <c>fresh</c> 条件完全一致（EnableRoundLimit &amp;&amp; startFreshRound），
    ///   中途出本续打（如 StopFloor=50 时 30 层出来）双方都保留存档，谁都不能删。
    /// - 除此之外全程不碰入口、不选存档、不排本，原地站着只点掉确认窗口，等队长带自己进本。
    /// </summary>
    private unsafe void HandleNonLeaderEntry(Vector3 pos)
    {
        // 队员也跟着算“这次回入口是不是要重开一轮”，好让删档时机和队长对齐。
        // 只更新 startFreshRound，不计轮数、不 Stop —— 那是队长的事。
        UpdateNonLeaderFreshRoundFlag();

        if (entryTaskManager.IsBusy)
        {
            SetIntent("地宫入口：队员，正在重置自己的存档");
            return;
        }

        // 需要跟队长一起删档重开：走到入口交互一次，删完就关菜单，不排本
        if (config.EnableRoundLimit && !config.FortuneMode && startFreshRound && !entrySubmitted)
        {
            var dist = (pos - EntryPoint).Length();
            if (dist > EntryReachRadius)
            {
                SetIntent($"地宫入口：队员，接近入口准备删档（{dist:0.0} 格）");
                var flyNow = DateTime.UtcNow;
                if (flyNow >= nextEntryFlytoAt)
                {
                    TryCommand("/vnav moveto 424.2 89.4 -772.7");
                    nextEntryFlytoAt = flyNow.AddSeconds(2);
                }
                return;
            }

            var entryObj = FindObjectByBaseId(EntryObjectBaseId);
            if (entryObj is null)
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] [入口][队员] 已到入口附近，但未找到入口物件 (BaseId={BaseId})，等待。", EntryObjectBaseId);
                return;
            }

            TryCommand("/vnav stop");
            SetIntent("地宫入口：队员，删除自己的存档（与队长保持一致），不排本");
            EnqueueNonLeaderSaveCleanupSequence(entryObj.GameObjectId);
            if (config.devMode)
                log.Information("[AutoPalExplorer] [入口][队员] 队长要重开一轮，同步删除自己的存档。");
            return;
        }

        // 其余情况：原地站着等队长排本，只点掉弹给自己的确认窗口
        if (navigator.IsBusy)
            navigator.Stop();

        SetIntent("地宫入口：队员，原地等待队长排本");

        var now = DateTime.UtcNow;
        if (now >= nextEntryConfirmFireAt &&
            TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yn) && IsAddonReady(yn))
        {
            Callback.Fire(yn, true, 0);
            nextEntryConfirmFireAt = now.AddSeconds(1.0); // 节流，避免对同一弹窗连点

            if (config.devMode)
                log.Information("[AutoPalExplorer] [入口][队员] 点击 SelectYesno -> 0（跟随队长进本）。");
        }
    }

    /// <summary>
    /// 队员侧的“重开一轮”判定：和 <see cref="HandleRoundTransitionAtEntrance"/> 用同一套依据
    /// （层数来自聊天“第N朝圣路”，全队都能收到），但只更新 <see cref="startFreshRound"/>，
    /// 不累加轮数、不播提示音、不 Stop。目的只有一个：让队员的删档时机和队长一模一样。
    /// </summary>
    private void UpdateNonLeaderFreshRoundFlag()
    {
        if (!config.EnableRoundLimit || config.FortuneMode)
        {
            startFreshRound = false;
            return;
        }

        if (roundCounted)
            return; // 本次入口访问已处理过
        if (currentFloor <= 0 || currentFloor < config.StopFloor)
            return; // 还没打到停止层（或只是中途出本续打），队长不会删档，队员也别删

        roundCounted = true;
        startFreshRound = true;

        if (config.devMode)
            log.Information("[AutoPalExplorer] [入口][队员] 打到第 {Floor} 层出本，队长会重开一轮，本次需要同步删档。", currentFloor);
    }

    /// <summary>
    /// 队员的存档清理：交互入口 -> 打开存档界面 -> 检查目标存档槽 -> 有存档则删除 -> 关闭菜单。
    /// 全程不选存档进本、不排本，等队长排本把自己带进去。
    /// （队长删完档会等 RoundWaitSeconds 秒再排本，正好留给队员做完这一套。）
    /// </summary>
    private unsafe void EnqueueNonLeaderSaveCleanupSequence(ulong entryObjectId)
    {
        var slot = SaveSlotIndex;

        // 1. 交互入口物件 -> DeepDungeonMenu -> callback(0) 打开存档界面
        entryTaskManager.Enqueue(() => InteractEntryObject(entryObjectId));
        EnqueueWaitAddon("DeepDungeonMenu");
        EnqueueFireAddon("DeepDungeonMenu", 0);
        EnqueueWaitAddon("DeepDungeonSaveData");

        // 2. 检查目标存档槽是否有存档
        saveSlotHasData = false;
        entryTaskManager.Enqueue(() =>
        {
            if (TryGetAddonByName<AtkUnitBase>("DeepDungeonSaveData", out var sd) && IsAddonReady(sd))
            {
                saveSlotHasData = !IsSaveSlotEmpty(sd, slot);
                log.Information("[AutoPalExplorer] [入口][队员] 存档槽 {Slot} {State}。",
                    slot, saveSlotHasData ? "有存档，删除" : "为空，无需删除");
                return true;
            }
            return false;
        });

        // 3. 有存档则删除（删完 saveSlotHasData 为真时会重开 savedata；为空则 savedata 仍开着）
        EnqueueDeleteSaveIfNeeded(slot);

        // 4. 关闭：savedata -1 回菜单，再 menu -1 关闭；标记本次入口已处理，不排本
        EnqueueWaitAddon("DeepDungeonSaveData");
        EnqueueFireAddon("DeepDungeonSaveData", -1);
        EnqueueWaitAddon("DeepDungeonMenu");
        EnqueueFireAddon("DeepDungeonMenu", -1);
        entryTaskManager.Enqueue(() =>
        {
            entrySubmitted = true; // 复用：本次入口已处理，等队长排本（离开入口地图会重置）
            SetIntent("地宫入口：队员已重置存档，原地等待队长排本");
            log.Information("[AutoPalExplorer] [入口][队员] 存档已处理，关闭菜单，等待队长排本。");
            return true;
        });
    }

    /// <summary>
    /// 是否为小队队长（单人视为队长）：只有队长才排本 / 选存档，非队长一律原地等待被带进本。
    ///
    /// 判定带两层保护，因为直接读 partyList 在换图瞬间并不可靠：
    /// 1) 换图 / 出本回到入口地图的那几帧里 partyList.Length 会短暂为 0，
    ///    旧实现会把这一帧当成“单人 = 队长”，非队长就会自己去排本（本次修复的 bug）。
    ///    这里累计“连续读到空小队”的时长，没超过 <see cref="PartyListSettleSeconds"/> 就沿用上一次判定。
    /// 2) 队长身份用 ContentId / EntityId / 名字三种方式依次比对，
    ///    避免某些场景下 ContentId 为 0 导致比不上。
    /// </summary>
    private bool IsPartyLeader()
    {
        var now = DateTime.UtcNow;
        var dt = lastPartyCheckAt == DateTime.MinValue ? 0.0 : (now - lastPartyCheckAt).TotalSeconds;
        lastPartyCheckAt = now;
        // 两次判定间隔过大（切图读条、插件被暂停）时不计入空窗，
        // 否则一次长加载就能把粘滞窗口耗光，又变回“队员自己去排本”。
        if (dt < 0.0 || dt > 1.0)
            dt = 0.0;

        if (partyList.Length > 0)
        {
            partyEmptySeconds = 0.0;
            UpdatePartyLeaderCache(ResolveIsPartyLeader());
            return lastIsPartyLeader;
        }

        // 小队列表为空：可能真是单人，也可能只是换图/进本瞬间的空窗期。
        // 之前判定过且空窗还没超时 -> 沿用上一次判定，别把队员误判成单人去排本。
        partyEmptySeconds += dt;
        if (partyLeaderCacheValid && partyEmptySeconds < PartyListSettleSeconds)
            return lastIsPartyLeader;

        UpdatePartyLeaderCache(true); // 真·单人
        return true;
    }

    /// <summary>读当前 partyList 判断自己是不是队长；取不到队长信息时保守视为队长，避免完全不排本。</summary>
    private bool ResolveIsPartyLeader()
    {
        var leaderIndex = (int)partyList.PartyLeaderIndex;
        if (leaderIndex < 0 || leaderIndex >= partyList.Length)
            return true;

        var leader = partyList[leaderIndex];
        if (leader == null)
            return true;

        // 1) ContentId：最可靠，但个别场景下会是 0
        var myContentId = Plugin.PlayerState.ContentId;
        if (leader.ContentId != 0 && myContentId != 0)
            return leader.ContentId == myContentId;

        // 2) EntityId：队长和自己同图时有效（入口地图 / 地宫内都满足）
        var me = objectTable.LocalPlayer;
        if (me is not null && leader.EntityId != 0 && me.EntityId != 0)
            return leader.EntityId == me.EntityId;

        // 3) 名字兜底
        if (me is not null && !string.IsNullOrEmpty(leader.Name.TextValue))
            return leader.Name.TextValue == me.Name.TextValue;

        return true;
    }

    /// <summary>写入队长判定缓存，身份变化时打一条日志方便排查。</summary>
    private void UpdatePartyLeaderCache(bool isLeader)
    {
        if (partyLeaderCacheValid && lastIsPartyLeader == isLeader)
            return;

        lastIsPartyLeader = isLeader;
        partyLeaderCacheValid = true;
        log.Information("[AutoPalExplorer] [队长] 当前身份：{Role}（小队人数={Count}）。",
            isLeader ? "队长（负责排本）" : "队员（原地等待）", partyList.Length);
    }

    private unsafe void EnqueueEntrySequence(ulong entryObjectId)
    {
        var slot = SaveSlotIndex;
        // 是否“重开一轮”：需要保证存档槽为空，才能在最后的 SelectString 选起始层
        // （财运亨通模式永远续打旧存档，不会重开）
        var fresh = config.EnableRoundLimit && !config.FortuneMode && startFreshRound;
        // 最后一个 SelectString 的选项索引：重开一轮 -> 起始层索引；否则 0（第一层 / 续打旧存档）
        entryStartFloorIndex = fresh ? StartFloorSelectIndex : 0;

        if (config.devMode)
            log.Information("[AutoPalExplorer] [入口] 进本序列：存档槽={Slot}，重开一轮={Fresh}，起始层索引={Idx}。",
                slot, fresh, entryStartFloorIndex);

        // 1. 交互入口物件
        entryTaskManager.Enqueue(() => InteractEntryObject(entryObjectId));

        // 2. DeepDungeonMenu -> callback(0)
        EnqueueWaitAddon("DeepDungeonMenu");
        EnqueueFireAddon("DeepDungeonMenu", 0);

        // 3. DeepDungeonSaveData 就绪
        EnqueueWaitAddon("DeepDungeonSaveData");

        // 3b. 重开一轮：先检查目标存档槽有没有存档，有则删档（删完重新回到 DeepDungeonSaveData 起始模式）
        if (fresh)
        {
            saveSlotHasData = false;
            entryTaskManager.Enqueue(() =>
            {
                if (TryGetAddonByName<AtkUnitBase>("DeepDungeonSaveData", out var sd) && IsAddonReady(sd))
                {
                    saveSlotHasData = !IsSaveSlotEmpty(sd, slot);
                    if (config.devMode)
                        log.Information("[AutoPalExplorer] [入口] 存档槽 {Slot} {State}。",
                            slot, saveSlotHasData ? "有存档，准备删除" : "为空（从头开始）");
                }
                return true;
            });
            EnqueueDeleteSaveIfNeeded(slot);
            // 删除存档之后再等待 RoundWaitSeconds（仅本轮确实删了存档时才等），然后继续排本
            EnqueueRoundWaitAfterDelete();
        }

        // 4. DeepDungeonSaveData -> callback(slot, second)：second = 有存档?1(继续):0(从头开始)
        //    有存档 -> 1号(0,1)/2号(1,1)；空存档 -> 1号(0,0)/2号(1,0)。实时读一次，续打/重开/首次进本都对。
        entryTaskManager.Enqueue(() =>
        {
            if (TryGetAddonByName<AtkUnitBase>("DeepDungeonSaveData", out var sd) && IsAddonReady(sd))
            {
                var empty = IsSaveSlotEmpty(sd, slot);
                var second = empty ? 0 : 1;
                log.Information("[AutoPalExplorer] [入口] step4：选存档槽 DeepDungeonSaveData -> callback({Slot}, {Second})（{Name}，{State}）。",
                    slot, second, slot == 1 ? "2号存档" : "1号存档", empty ? "空/从头开始" : "有存档续打");
                Callback.Fire(sd, true, slot, second);
                return true;
            }
            return false;
        });

        // 5. SelectString -> 选 0（进本设置菜单）
        EnqueueWaitAddon("SelectString");
        EnqueueFireAddon("SelectString", 0);

        // 6. 不断 SelectYesno 选 0，直到再次出现 SelectString 选“起始层索引”
        nextEntryConfirmFireAt = DateTime.MinValue;
        entryTaskManager.Enqueue(HandleEntryConfirmLoop);
    }

    /// <summary>入队一个“等待指定 Addon 就绪”的任务。</summary>
    private unsafe void EnqueueWaitAddon(string addonName)
        => entryTaskManager.Enqueue(() =>
            TryGetAddonByName<AtkUnitBase>(addonName, out var a) && IsAddonReady(a));

    /// <summary>入队一个“对指定 Addon 触发 Callback.Fire(true, values)”的任务。</summary>
    private unsafe void EnqueueFireAddon(string addonName, params object[] values)
        => entryTaskManager.Enqueue(() =>
        {
            if (TryGetAddonByName<AtkUnitBase>(addonName, out var a))
                Callback.Fire(a, true, values);
        });

    /// <summary>
    /// 重开一轮：若目标存档槽有存档，则删除它（删完存档槽变空，之后进本才能选起始层）。
    /// 通过 <see cref="saveSlotHasData"/> 控制——为 false 时下面每个任务都会直接跳过。
    /// 删档流程（据实测）：
    ///   DeepDungeonSaveData callback(-1) 回 DeepDungeonMenu
    ///   -> DeepDungeonMenu callback(2) 进入删档模式的 DeepDungeonSaveData
    ///   -> callback(slot, 0) 弹出 SelectYesno
    ///   -> 勾选 SelectYesno 里的确认复选框(CheckBox 4 / Collision 5) 再 callback(0) 删除
    ///   -> callback(-1) 回 DeepDungeonMenu -> callback(0) 重新打开起始模式的 DeepDungeonSaveData
    /// </summary>
    // 调试用：删档流程每一步之间的观察延迟（毫秒）。定位好问题后可改回 0 关闭。
    private int deleteStepDebugDelayMs = 1000;

    private void EnqueueDeleteSaveIfNeeded(int slot)
    {
        // a. 回到 DeepDungeonMenu
        EnqueueDeleteDebugLog("a. DeepDungeonSaveData -> callback(-1) 回菜单");
        EnqueueFireIfHasData("DeepDungeonSaveData", -1);
        EnqueueDeleteStepDelay();
        EnqueueWaitIfHasData("DeepDungeonMenu");
        EnqueueDeleteStepDelay();
        // b. 进入删档模式的 DeepDungeonSaveData
        EnqueueDeleteDebugLog("b. DeepDungeonMenu -> callback(2) 进删档模式");
        EnqueueFireIfHasData("DeepDungeonMenu", 2);
        EnqueueDeleteStepDelay();
        EnqueueWaitIfHasData("DeepDungeonSaveData");
        EnqueueDeleteStepDelay();
        // c. 选择要删的存档槽 -> 弹出 SelectYesno
        //    删档回调值与进本不同：第二个参数固定为 1 -> 1号存档(0,1)，2号存档(1,1)
        var deleteSecond = 1;
        EnqueueDeleteDebugLog($"c. DeepDungeonSaveData -> callback({slot}, {deleteSecond}) 选存档槽");
        EnqueueFireIfHasData("DeepDungeonSaveData", slot, deleteSecond);
        EnqueueDeleteStepDelay();
        EnqueueWaitIfHasData("SelectYesno");
        EnqueueDeleteStepDelay();
        // d. 勾选确认复选框
        EnqueueDeleteDebugLog("d. SelectYesno -> 勾选确认复选框(CheckBox 4 / Collision 5)");
        EnqueueClickDeleteCheckbox();
        EnqueueDeleteStepDelay();
        // e. callback(0) 确认删除
        EnqueueDeleteDebugLog("e. SelectYesno -> callback(0) 确认删除");
        EnqueueFireIfHasData("SelectYesno", 0);
        EnqueueDeleteStepDelay();
        // f. 删完确保回到 DeepDungeonMenu（删完可能停在 savedata 需 -1，也可能已直接回 menu）
        EnqueueDeleteDebugLog("f. 确保回到 DeepDungeonMenu（若还在 savedata 则 -1）");
        EnqueueEnsureBackToMenuAfterDelete();
        EnqueueDeleteStepDelay();
        // g. 重新打开起始模式的 DeepDungeonSaveData
        EnqueueDeleteDebugLog("g. DeepDungeonMenu -> callback(0) 重开起始模式 SaveData");
        EnqueueFireIfHasData("DeepDungeonMenu", 0);
        EnqueueDeleteStepDelay();
        EnqueueWaitIfHasData("DeepDungeonSaveData");
    }

    /// <summary>删除存档之后的每轮等待（仅当本轮确实删了存档时才等 RoundWaitSeconds）。</summary>
    private void EnqueueRoundWaitAfterDelete()
        => entryTaskManager.Enqueue(() =>
        {
            if (saveSlotHasData && config.RoundWaitSeconds > 0)
            {
                log.Information("[AutoPalExplorer] [入口][轮次] 删档完成，等待 {Sec}s 后重新排本。", config.RoundWaitSeconds);
                entryTaskManager.InsertDelay(config.RoundWaitSeconds * 1000);
            }
            return true;
        });

    /// <summary>删档流程步骤间的观察延迟（仅在有存档、真正走删档分支时才延迟）。</summary>
    private void EnqueueDeleteStepDelay()
    {
        if (deleteStepDebugDelayMs <= 0)
            return;
        entryTaskManager.Enqueue(() =>
        {
            if (!saveSlotHasData)
                return true; // 空存档不走删档分支，无需延迟
            entryTaskManager.InsertDelay(deleteStepDebugDelayMs);
            return true;
        });
    }

    /// <summary>删档流程里打一条日志，方便观察卡在哪一步（仅有存档时打印）。</summary>
    private void EnqueueDeleteDebugLog(string step)
        => entryTaskManager.Enqueue(() =>
        {
            if (saveSlotHasData)
                log.Information("[AutoPalExplorer] [入口][删档] {Step}", step);
            return true;
        });

    /// <summary>只有 saveSlotHasData 为真时才等待 Addon；否则直接完成（跳过删档分支）。</summary>
    private unsafe void EnqueueWaitIfHasData(string addonName)
        => entryTaskManager.Enqueue(() =>
            !saveSlotHasData || (TryGetAddonByName<AtkUnitBase>(addonName, out var a) && IsAddonReady(a)));

    /// <summary>只有 saveSlotHasData 为真时才触发 Callback；否则直接完成（跳过删档分支）。</summary>
    private unsafe void EnqueueFireIfHasData(string addonName, params object[] values)
        => entryTaskManager.Enqueue(() =>
        {
            if (!saveSlotHasData)
                return true;
            if (TryGetAddonByName<AtkUnitBase>(addonName, out var a) && IsAddonReady(a))
            {
                Callback.Fire(a, true, values);
                return true;
            }
            return false;
        });

    // 删档收尾：返回菜单时 -1 的节流，避免每帧连点把菜单也退掉
    private DateTime nextDeleteReturnFireAt = DateTime.MinValue;

    /// <summary>
    /// 删档确认后确保回到 DeepDungeonMenu：
    /// - 已在菜单 -> 完成；
    /// - 还停在删档模式的 DeepDungeonSaveData -> 触发 -1 返回（带节流），下一帧再检查；
    /// - 两者都还没出现 -> 继续等。
    /// </summary>
    private unsafe void EnqueueEnsureBackToMenuAfterDelete()
        => entryTaskManager.Enqueue(() =>
        {
            if (!saveSlotHasData)
                return true;

            if (TryGetAddonByName<AtkUnitBase>("DeepDungeonMenu", out var menu) && IsAddonReady(menu))
                return true; // 已回到菜单

            var now = DateTime.UtcNow;
            if (now >= nextDeleteReturnFireAt &&
                TryGetAddonByName<AtkUnitBase>("DeepDungeonSaveData", out var sd) && IsAddonReady(sd))
            {
                Callback.Fire(sd, true, -1);
                nextDeleteReturnFireAt = now.AddSeconds(1.0);
                if (config.devMode)
                    log.Information("[AutoPalExplorer] [入口][删档] 仍在 SaveData，触发 -1 返回菜单。");
            }

            return false; // 继续等，直到 DeepDungeonMenu 出现
        });

    /// <summary>入队“勾选删档确认框”任务（saveSlotHasData 为假时跳过）。</summary>
    private unsafe void EnqueueClickDeleteCheckbox()
        => entryTaskManager.Enqueue(() =>
        {
            if (!saveSlotHasData)
                return true;
            if (TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yn) && IsAddonReady(yn))
            {
                ClickDeleteConfirmCheckbox(yn);
                return true;
            }
            return false;
        });

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
            // 这个 SelectString 是“起始层选择”：0=第1层, 1=21, 2=31, 3=51, 4=71
            Callback.Fire(ss, true, entryStartFloorIndex);
            if (config.devMode)
                log.Information("[AutoPalExplorer] [入口] 选择起始层索引 {Idx}。", entryStartFloorIndex);
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
    /// 轮次控制：回到入口地图时判断是否“刚打完一轮”。
    /// - 中途出本（当前层 &lt; 停止层）：不计数，正常续打。
    /// - 打到停止层出本：完成一轮。若已达设定轮数则播放提示音、通知并 Stop；
    ///   否则标记下一轮“重开”（删存档 + 选起始层）并进入 RoundWaitSeconds 等待。
    /// </summary>
    private void HandleRoundTransitionAtEntrance()
    {
        if (roundCounted)
            return; // 本次入口访问已处理过
        if (currentFloor <= 0 || currentFloor < config.StopFloor)
            return; // 还没打到停止层（或只是中途出本），不算一轮

        roundCounted = true;
        completedRounds++;

        var total = Math.Max(1, config.RoundCount);
        log.Information("[AutoPalExplorer] [轮次] 第 {Done}/{Total} 轮完成（打到第 {Floor} 层）。",
            completedRounds, total, currentFloor);

        if (completedRounds >= total)
        {
            NotifyRoundsAllDone(total);
            Stop();
            return;
        }

        // 还有轮次：下一轮重开（删存档 -> 等待 RoundWaitSeconds -> 选起始层排本）
        startFreshRound = true;
        entrySubmitted = false;
        entryTaskManager.Abort();
        SetIntent($"轮次：第 {completedRounds}/{total} 轮完成，重开下一轮（删档后等待 {config.RoundWaitSeconds}s）");
    }

    /// <summary>全部轮次打完：聊天通知 + 播放提示音。</summary>
    private void NotifyRoundsAllDone(int total)
    {
        var msg = $"[AutoPalExplorer] 已完成设定的 {total} 轮（打到第 {config.StopFloor} 层），自动停止。";
        log.Information(msg);

        try { ECommons.DalamudServices.Svc.Chat?.Print(msg); }
        catch (Exception ex) { log.Warning($"[AutoPalExplorer] 轮次完成通知失败：{ex.Message}"); }

        try { FFXIVClientStructs.FFXIV.Client.UI.UIGlobals.PlayChatSoundEffect(6); }
        catch (Exception ex) { log.Warning($"[AutoPalExplorer] 轮次完成提示音失败：{ex.Message}"); }
    }

    /// <summary>
    /// 读取 DeepDungeonSaveData 中指定存档槽的文本，判断是否为空存档（“从头开始”）。
    /// 节点路径：List Component Node 6 -> ListItemRenderer(slot1=2 / slot2=21001) -> Text Node 7。
    /// 读不到节点时，为避免误触发删档流程把进本卡死，默认按“空存档”处理。
    /// </summary>
    private unsafe bool IsSaveSlotEmpty(AtkUnitBase* addon, int slot)
    {
        if (addon == null)
        {
            log.Warning("[AutoPalExplorer] [入口][存档检测] addon 为空。");
            return true;
        }

        var itemNodeId = slot == 1 ? 21001u : 2u;
        log.Information("[AutoPalExplorer] [入口][存档检测] 目标 slot={Slot}（{Name}），ListItemRenderer 节点 id={ItemId}。",
            slot, slot == 1 ? "2号存档" : "1号存档", itemNodeId);

        // 先把 List(6) 组件里的所有子节点 dump 出来，核对真实节点 id / 文本
        DumpSaveSlotList(addon);

        var listNode = addon->GetNodeById(6);                 // List Component Node 6
        var itemNode = GetComponentNodeById(listNode, itemNodeId); // ListItemRenderer
        var textNode = GetComponentNodeById(itemNode, 7);     // Text Node 7
        if (textNode == null)
        {
            log.Warning("[AutoPalExplorer] [入口][存档检测] slot={Slot} 读不到文本节点（List6={L} Item{ItemId}={I} Text7=null），默认按“空存档”处理。",
                slot, listNode == null ? "null" : "ok", itemNodeId, itemNode == null ? "null" : "ok");
            return true;
        }

        var text = ((AtkTextNode*)textNode)->NodeText.GetText() ?? string.Empty;
        var empty = text.Contains("从头开始");
        log.Information("[AutoPalExplorer] [入口][存档检测] slot={Slot} 读到文本=\"{Text}\" -> {State}。",
            slot, text, empty ? "空(从头开始)" : "有存档");
        return empty;
    }

    /// <summary>调试：dump DeepDungeonSaveData 里 List(6) 组件的所有子节点（NodeId/类型/内部 Text7 文本）。</summary>
    private unsafe void DumpSaveSlotList(AtkUnitBase* addon)
    {
        var listNode = addon->GetNodeById(6);
        if (listNode == null)
        {
            log.Information("[AutoPalExplorer] [入口][存档检测][Dump] 未找到 List Node(6)。");
            return;
        }

        var comp = listNode->GetAsAtkComponentNode();
        if (comp == null || comp->Component == null)
        {
            log.Information("[AutoPalExplorer] [入口][存档检测][Dump] Node(6) 不是组件节点。");
            return;
        }

        var count = comp->Component->UldManager.NodeListCount;
        log.Information("[AutoPalExplorer] [入口][存档检测][Dump] List(6) 组件内节点数={Count}：", count);
        for (var i = 0; i < count; i++)
        {
            var n = comp->Component->UldManager.NodeList[i];
            if (n == null)
                continue;

            var txt = string.Empty;
            var cn = n->GetAsAtkComponentNode();
            if (cn != null && cn->Component != null)
            {
                var t = cn->Component->UldManager.SearchNodeById(7);
                if (t != null)
                    txt = ((AtkTextNode*)t)->NodeText.GetText() ?? string.Empty;
            }

            log.Information("[AutoPalExplorer] [入口][存档检测][Dump]   [{I}] NodeId={Id} Type={Type} 内部Text7=\"{Txt}\"",
                i, n->NodeId, n->Type, txt);
        }
    }

    /// <summary>
    /// 勾选“删除存档”确认窗口(SelectYesno)里的确认复选框。
    /// 节点路径：CheckBox Component Node 4 -> Collision Node 5，通过模拟点击碰撞节点触发。
    /// </summary>
    private unsafe void ClickDeleteConfirmCheckbox(AtkUnitBase* addon)
    {
        var checkboxNode = addon->GetNodeById(4);
        var collision = GetComponentNodeById(checkboxNode, 5);
        if (collision == null)
        {
            log.Warning("[AutoPalExplorer] [入口] 删档：未找到确认复选框碰撞节点(4/5)。");
            return;
        }

        ClickCollisionNode(addon, collision);
        if (config.devMode)
            log.Information("[AutoPalExplorer] [入口] 删档：已勾选确认复选框。");
    }

    /// <summary>
    /// 从一个组件节点内部按 id 取子节点。
    /// 先用 SearchNodeById；对动态生成的列表项(ListItemRenderer，如 21001/21002)SearchNodeById 找不到，
    /// 再回退遍历 NodeList 按 NodeId 匹配。
    /// </summary>
    private static unsafe AtkResNode* GetComponentNodeById(AtkResNode* node, uint id)
    {
        if (node == null)
            return null;

        var comp = node->GetAsAtkComponentNode();
        if (comp == null || comp->Component == null)
            return null;

        var found = comp->Component->UldManager.SearchNodeById(id);
        if (found != null)
            return found;

        // 回退：扫组件的 NodeList（运行时动态生成的列表项只在这里，SearchNodeById 走模板树扫不到）
        var count = comp->Component->UldManager.NodeListCount;
        for (var i = 0; i < count; i++)
        {
            var n = comp->Component->UldManager.NodeList[i];
            if (n != null && n->NodeId == id)
                return n;
        }

        return null;
    }

    /// <summary>对碰撞节点模拟一次鼠标点击（取该节点上注册的 MouseClick 事件转发给 addon）。</summary>
    private static unsafe void ClickCollisionNode(AtkUnitBase* addon, AtkResNode* node)
    {
        if (addon == null || node == null)
            return;

        var evt = node->AtkEventManager.Event;
        while (evt != null && evt->State.EventType != AtkEventType.MouseClick)
            evt = evt->NextEvent;

        if (evt == null)
            return;

        var data = new AtkEventData();
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, &data);
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
