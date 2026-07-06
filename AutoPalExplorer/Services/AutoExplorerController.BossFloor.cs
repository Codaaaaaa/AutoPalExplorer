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
