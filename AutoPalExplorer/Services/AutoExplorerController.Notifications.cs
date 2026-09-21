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
    /// 收到聊天“传送装置启动了”：标记传送装置已激活。
    /// 这只是三个数据源里最弱的一个（队友触发时可能收不到），真正的判定在
    /// TickExitActivation 里每帧重算；本层换层时由 ResetStaticObjectsState 清掉。
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

        // 记下踩出来的位置：接下来几帧要站在这儿把出土的宝箱开掉，
        // 不然箱子还在出土动画（不可交互）时就会被普通宝箱逻辑带去开别的箱子。
        if (objectTable.LocalPlayer is { } me)
        {
            unearthedChestPending = true;
            unearthedChestPos = me.Position;
            unearthedChestPendingAt = DateTime.UtcNow;
        }

        // 财运亨通模式：本次进本第一次踩出宝藏时计数
        CountFortuneTreasure();
        if (config.devMode)
            log.Information("[AutoPalExplorer] 收到埋藏的宝藏通知。");
    }

    /// <summary>
    /// 收到系统消息「这一朝圣路似乎有宝藏……」：本层确实有埋藏的宝藏。
    /// 只有这条（或宝藏物件真的出现）才算数，感知宝藏的 Buff 提示不算。
    /// 目前只有财运亨通模式会用这个标记。
    /// </summary>
    public void NotifyFloorHasTreasure()
    {
        if (fortuneFloorHasTreasure)
            return;

        fortuneFloorHasTreasure = true;
        if (config.FortuneMode)
            log.Information("[AutoPalExplorer] 本层有埋藏的宝藏（系统提示）。");
    }

    /// <summary>
    /// 收到系统消息「这一朝圣路似乎没有宝藏……」：本层确定没有埋藏的宝藏，
    /// 财运亨通模式据此立刻退本重进，不用再等检测超时。（其它模式不读这个标记。）
    /// </summary>
    public void NotifyFloorNoTreasure()
    {
        if (fortuneFloorNoTreasure)
            return;

        fortuneFloorNoTreasure = true;
        if (config.FortuneMode)
            log.Information("[AutoPalExplorer] 本层没有埋藏的宝藏（系统提示）。");
    }

    /// <summary>收到聊天“点亮了光耀烛台”：本层已互动过烛台，不再重复互动（换层重置）。</summary>
    public void NotifyCandleLit()
    {
        hasLitCandle = true;
        if (config.devMode)
            log.Information("[AutoPalExplorer] 收到点亮光耀烛台通知，本层不再互动烛台。");
    }

    public void nextLevelActivated()
    {
        nextLevelBool = true;
        hasOpenedNextPilgrimWindow = false;
        bossExitReachedAt = DateTime.MinValue;
        if (config.devMode)
            log.Information("[AutoPalExplorer] 标记换层（nextLevelActivated）。");
    }

    public void NotifyFloorNumber(int floor)
    {
        currentFloor = floor;

        // 进入了新的一层，说明已离开入口地图 -> 允许下次回到入口时重新计一轮
        roundCounted = false;

        // 100 层不是 Boss 房，且是从 99 层直接传送过来的（不会触发“成功发送了参加申请”重置），
        // 需要在这里手动清掉 Boss 房标记，改走 100 层收尾流程。
        if (floor == 100)
        {
            isBossFloor = false;
            isBossFloorQueueing = false;
        }

        if (config.devMode)
            log.Information("[AutoPalExplorer] 当前层数 = {Floor}。", floor);
    }

    public void NotifyBossFloor()
    {
        isBossFloor = true;
        isBossFloorQueueing = false;
        hasOpenedNextPilgrimWindow = false;
        bossExitReachedAt = DateTime.MinValue;
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
        bossExitReachedAt = DateTime.MinValue;
        nextChallengeAttemptAt = DateTime.MinValue;
        // 地宫入口：申请已发出，结束入口 UI 流程
        entrySubmitted = true;
        entryTaskManager.Abort();
        // 本轮已成功进本：后续中途出本(如 30 层出来续打)不再走“重开一轮”的删档 + 选层流程
        startFreshRound = false;

        if (config.devMode)
            log.Information("[AutoPalExplorer] 收到成功发送参加申请提示，结束 Boss 流程逻辑。");
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

        MarkChestDone(lastChestInteractObjectId, "魔陶器已满，不再开这个箱子");

        // ✅ 新增：如果当前锁的是这个箱子，也顺便解锁
        if (hasLockedChest && lockedChestId == lastChestInteractObjectId)
        {
            ClearLockedChest();
        }
    }
}
