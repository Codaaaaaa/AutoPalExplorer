using System;
using System.Globalization;
using System.Numerics;

using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;

using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Component.GUI;

using ECommons.Automation;
using static ECommons.GenericHelpers;

using AutoPalExplorer.Helpers;

namespace AutoPalExplorer.Services;

/// <summary>
/// 财运亨通模式：专门刷「埋藏的宝藏」，和常规打本完全不一样。
///
/// 前置准备（人工做一次）：先手动打一个 1-10 层的存档，并在存档里留着「魔陶器：感知宝藏」，
/// 之后在配置里把「使用存档」选成这个存档即可（每次续打都会还原成存档时的魔陶器数量）。
///
/// 循环流程：
///   队长续打存档进 11 层（不删存档），队员原地等着被带进来
///   -> 队长用「魔陶器：感知宝藏」
///      -> 没收到系统消息「这一朝圣路似乎有宝藏……」：全队退本，重新进（不重置存档）
///      -> 有宝藏但不在视野里：队长按房间中心逐个传送过去找
///      -> 看得见宝藏：队长传送到宝藏上，然后站着不动
///                    等宝藏被踩出来 -> 计数 +1 -> 全队退本，重新进
///   -> 一直循环，直到有人手动暂停。
///
/// 移动走配置里的指令前缀（默认 /vnav moveto），发出的是「前缀 x y z」，Y 还会减去配置的偏移（默认 0）。
/// 另外进本 / 退本各会发一组自定义指令（默认是 i-ching-commander 的 y_adjust / speed）。
/// </summary>
public sealed partial class AutoPalController
{
    /// <summary>财运亨通模式的阶段。</summary>
    public enum FortuneStage
    {
        WaitingEnter,     // 刚进本，等读条结束 + 缓冲几秒
        UsingIntuition,   // 队长使用「魔陶器：感知宝藏」
        Detecting,        // 等系统提示 / 宝藏点出现（超时 = 本层没宝藏）
        Searching,        // 有宝藏但不在视野里：队长逐个房间传送去找
        Teleporting,      // 队长传送到宝藏点
        WaitingTreasure,  // 站着不动，等宝藏被踩出来
        Leaving,          // 退本（准备重进）
    }

    // 宝藏点搜索半径：感知宝藏是整层生效的，给足够大的范围
    private const float FortuneSearchRadius = 1000f;
    // 传送后离宝藏点多近算「站到位了」（2D）
    private const float FortuneArriveRadius = 2.0f;
    // 感知宝藏最多请求几次（只有「请求了但根本没点下去」才会重试，不会重复消耗）
    private const int FortuneIntuitionMaxAttempts = 3;
    private const double FortuneIntuitionRetrySeconds = 3.0;
    // 请求感知宝藏后整体的硬超时，防止面板一直不就绪把流程卡死
    private const double FortuneIntuitionStageTimeout = 20.0;
    // 传送指令最多发几次、重发间隔
    private const int FortuneTeleportMaxAttempts = 4;
    private const double FortuneTeleportRetrySeconds = 3.0;
    // 站着等宝藏时，多久没站到点上就再传一次
    private const double FortuneRetryTeleportAfterSeconds = 5.0;
    // 判定「人还在往目标走」的速度阈值（m/s）；比常规的 MovingSpeedThreshold 低，
    // 因为财运亨通模式可能被 i-ching-commander 的 speed 调得很慢
    private const float FortuneMovingThreshold = 0.05f;
    // 退本重试间隔；前几次走正常流程（弹确认窗口），之后强制退
    private const double FortuneLeaveRetrySeconds = 5.0;
    private const int FortuneLeaveSoftAttempts = 3;

    private FortuneStage fortuneStage = FortuneStage.WaitingEnter;
    private DateTime fortuneStageAt = DateTime.MinValue;
    private uint fortuneTerritory;
    private bool fortuneRunActive;               // 当前是否已为「这次进本」初始化过状态机
    private Vector3? fortuneTreasurePos;
    private int fortuneIntuitionAttempts;
    private DateTime nextFortuneIntuitionAt = DateTime.MinValue;
    private int fortuneTeleportAttempts;
    private DateTime nextFortuneTeleportAt = DateTime.MinValue;
    private int fortuneLeaveAttempts;
    private DateTime nextFortuneLeaveAt = DateTime.MinValue;
    private DateTime nextFortuneAddonFireAt = DateTime.MinValue;
    private bool fortuneTreasureCounted;         // 本次进本是否已计过数
    private bool fortuneFloorHasTreasure;        // 收到「这一朝圣路似乎有宝藏」= 本层确实有宝藏
    private bool fortuneFloorNoTreasure;         // 收到「这一朝圣路似乎没有宝藏」= 本层确定没有，直接退
    private DateTime fortuneEntryArrivedAt = DateTime.MinValue; // 退本回到入口地图的时间（重进前的缓冲）
    private bool fortuneEntryDelayDone;          // 本次回到入口的缓冲是否已经报过一次日志
    private bool fortuneIntuitionLanded;         // 收到「可以感知到宝藏埋藏的位置了」= 魔陶器已生效
    private bool fortuneEnterCommandsSent;       // 本次进本的「进本指令」是否已经发过
    private bool fortuneLeaveCommandsSent;       // 本次进本的「退本指令」是否已经发过
    private int fortuneSearchedRoomMask;         // 逐房间搜索时已经传过的房间（按位）
    private int fortuneSearchRoom = -1;          // 正在搜的房间（仅用于日志 / UI）
    private int fortuneSearchHops;               // 已经传送过几个房间
    private DateTime nextFortuneRoomHopAt = DateTime.MinValue;
    private DateTime fortuneSearchStartedAt = DateTime.MinValue;
    private int fortuneTreasureCount;
    private int fortuneRunCount;

    /// <summary>从「开始」按钮按下起，一共踩出了多少个埋藏的宝藏。</summary>
    public int FortuneTreasureCount => fortuneTreasureCount;

    /// <summary>从「开始」按钮按下起，一共进了多少次本。</summary>
    public int FortuneRunCount => fortuneRunCount;

    public FortuneStage CurrentFortuneStage => fortuneStage;

    public string FortuneStageText => fortuneStage switch
    {
        FortuneStage.WaitingEnter => "进本等待中",
        FortuneStage.UsingIntuition => "使用感知宝藏",
        FortuneStage.Detecting => "检测本层有没有宝藏",
        FortuneStage.Searching => "逐房间传送找宝藏",
        FortuneStage.Teleporting => "传送到宝藏点",
        FortuneStage.WaitingTreasure => "站定等宝藏踩出",
        FortuneStage.Leaving => "退本中",
        _ => "未知",
    };

    private double FortuneEnterDelay => Math.Max(0, config.FortuneEnterDelaySeconds);
    private double FortuneDetectWait => Math.Max(1, config.FortuneDetectWaitSeconds);
    private double FortuneMemberExtraWait => Math.Max(0, config.FortuneMemberExtraWaitSeconds);
    private double FortuneTreasureWait => Math.Max(3, config.FortuneTreasureWaitSeconds);
    private double FortuneRoomScanWait => Math.Max(0.2, config.FortuneRoomScanWaitMs / 1000.0);
    private double FortuneSearchTimeout => Math.Max(10, config.FortuneSearchTimeoutSeconds);
    private double FortuneReenterDelay => Math.Max(0, config.FortuneReenterDelaySeconds);

    /// <summary>
    /// 队员在「等宝藏踩出」阶段的等待上限。本层确认有宝藏时要覆盖队长的整个
    /// 「逐房间找 + 站着等」流程，再加上队员额外等待，保证队员一定比队长晚退本。
    /// </summary>
    private double FortuneWaitTreasureLimit
        => fortuneFloorHasTreasure
            ? FortuneSearchTimeout + FortuneTreasureWait + FortuneMemberExtraWait
            : FortuneTreasureWait;

    /// <summary>Start/Stop 时把整个财运亨通状态（含累计计数）清零。</summary>
    private void ResetFortuneState()
    {
        fortuneRunActive = false;
        fortuneTerritory = 0;
        fortuneStage = FortuneStage.WaitingEnter;
        fortuneStageAt = DateTime.MinValue;
        fortuneTreasurePos = null;
        fortuneIntuitionAttempts = 0;
        fortuneTeleportAttempts = 0;
        fortuneLeaveAttempts = 0;
        nextFortuneIntuitionAt = DateTime.MinValue;
        nextFortuneTeleportAt = DateTime.MinValue;
        nextFortuneLeaveAt = DateTime.MinValue;
        nextFortuneAddonFireAt = DateTime.MinValue;
        fortuneTreasureCounted = false;
        fortuneFloorHasTreasure = false;
        fortuneFloorNoTreasure = false;
        fortuneSearchedRoomMask = 0;
        fortuneSearchRoom = -1;
        fortuneSearchHops = 0;
        nextFortuneRoomHopAt = DateTime.MinValue;
        fortuneSearchStartedAt = DateTime.MinValue;
        fortuneEntryArrivedAt = DateTime.MinValue;
        fortuneEntryDelayDone = false;
        fortuneIntuitionLanded = false;
        fortuneEnterCommandsSent = false;
        fortuneLeaveCommandsSent = false;
        fortuneTreasureCount = 0;
        fortuneRunCount = 0;
    }

    /// <summary>只清计数（配置窗口上的「计数清零」按钮）。</summary>
    public void ResetFortuneCounters()
    {
        fortuneTreasureCount = 0;
        fortuneRunCount = 0;
        log.Information("[AutoPalExplorer][财运亨通] 计数已清零。");
    }

    /// <summary>回到地宫入口地图时调用：下次踏进地宫要当成新的一次进本重新初始化。</summary>
    private void NotifyFortuneOutsideDungeon()
    {
        fortuneRunActive = false;
    }

    /// <summary>
    /// 退本回到入口地图后的重进缓冲：读条结束再等 <see cref="FortuneReenterDelay"/> 秒才允许开始进本流程。
    /// 刚落地就去点入口的话，地宫菜单往往还没能弹出来，进本序列会一路等到任务超时（几十秒），
    /// 反而比先站几秒再点慢得多。返回 true 表示可以进本了。
    /// </summary>
    private unsafe bool TickFortuneEntryDelay()
    {
        // 回到入口 = 上一趟结束
        NotifyFortuneOutsideDungeon();

        // 结算界面还开着就先点掉，否则挡着没法交互入口
        if (DateTime.UtcNow >= nextFortuneAddonFireAt &&
            TryGetAddonByName<AtkUnitBase>("DeepDungeonResult", out var result) && IsAddonReady(result))
        {
            Callback.Fire(result, true, -1);
            nextFortuneAddonFireAt = DateTime.UtcNow.AddSeconds(1.0);
        }

        // 还在读条：从加载结束才开始计时
        if (fortuneEntryArrivedAt == DateTime.MinValue ||
            condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            fortuneEntryArrivedAt = DateTime.UtcNow;
            fortuneEntryDelayDone = false;
            SetIntent("财运亨通：回到入口，等待加载完成");
            return false;
        }

        var waited = (DateTime.UtcNow - fortuneEntryArrivedAt).TotalSeconds;
        if (waited < FortuneReenterDelay)
        {
            SetIntent($"财运亨通：退本缓冲 {waited:0.0}/{FortuneReenterDelay:0}s 后重新进本");
            return false;
        }

        // 缓冲结束只报一次；卡在进本流程哪一步可以看这条日志之后的时间差
        if (!fortuneEntryDelayDone)
        {
            fortuneEntryDelayDone = true;
            log.Information("[AutoPalExplorer][财运亨通] 退本缓冲结束（{Sec:0.0}s），开始重新进本。", waited);
        }

        return true;
    }

    /// <summary>
    /// 财运亨通模式主循环（在妖宫地图内每帧调用，完全接管本帧：不探索、不打怪、不开箱）。
    /// </summary>
    private void HandleFortuneMode(IPlayerCharacter player, Vector3 pos)
    {
        // 本模式不会打 Boss，正常不会到「下一层入口」等待室；真到了就直接退本重进，别卡死在那儿
        if (MapIds.IsWaitingRoom(clientState.TerritoryType))
        {
            TickFortuneLeaving();
            return;
        }

        // 已经在本里了，入口缓冲计时作废（下次退本回入口重新开始算）
        fortuneEntryArrivedAt = DateTime.MinValue;
        fortuneEntryDelayDone = false;

        // 房间中心标定：逐房间搜索宝藏要用（强制开启，不受「启用房间图」影响）
        TickRoomState(player, pos, force: true);

        // 每次重新进本（或换了地图）都重开一轮状态机
        if (!fortuneRunActive || fortuneTerritory != clientState.TerritoryType)
        {
            fortuneTerritory = clientState.TerritoryType;
            fortuneRunActive = true;
            fortuneRunCount++;
            BeginFortuneRun();
        }

        switch (fortuneStage)
        {
            case FortuneStage.WaitingEnter:
                TickFortuneWaitingEnter();
                break;
            case FortuneStage.UsingIntuition:
                TickFortuneUsingIntuition(pos);
                break;
            case FortuneStage.Detecting:
                TickFortuneDetecting(pos);
                break;
            case FortuneStage.Searching:
                TickFortuneSearching(pos);
                break;
            case FortuneStage.Teleporting:
                TickFortuneTeleporting(pos);
                break;
            case FortuneStage.WaitingTreasure:
                TickFortuneWaitingTreasure(pos);
                break;
            case FortuneStage.Leaving:
                TickFortuneLeaving();
                break;
        }
    }

    /// <summary>新的一次进本：清掉上一次的痕迹（这些平时由换层逻辑清，本模式不走换层逻辑）。</summary>
    private void BeginFortuneRun()
    {
        fortuneStage = FortuneStage.WaitingEnter;
        fortuneStageAt = DateTime.UtcNow;
        fortuneTreasurePos = null;
        fortuneIntuitionAttempts = 0;
        fortuneTeleportAttempts = 0;
        fortuneLeaveAttempts = 0;
        nextFortuneIntuitionAt = DateTime.MinValue;
        nextFortuneTeleportAt = DateTime.MinValue;
        nextFortuneLeaveAt = DateTime.MinValue;
        fortuneTreasureCounted = false;
        fortuneFloorHasTreasure = false;
        fortuneFloorNoTreasure = false;
        fortuneSearchedRoomMask = 0;
        fortuneSearchRoom = -1;
        fortuneSearchHops = 0;
        nextFortuneRoomHopAt = DateTime.MinValue;
        fortuneSearchStartedAt = DateTime.MinValue;
        fortuneEntryArrivedAt = DateTime.MinValue;
        fortuneIntuitionLanded = false;
        fortuneEnterCommandsSent = false;
        fortuneLeaveCommandsSent = false;

        hasOpenBurinedChest = false;
        nextLevelBool = false;
        pomanderManager.ResetBuriedBuff();
        pomanderManager.ResetIntuitionState();

        if (navigator.IsBusy)
            navigator.Stop();
        EnsureBmraiOff();
        EnsureRotationOff();

        log.Information("[AutoPalExplorer][财运亨通] 第 {Run} 次进本（Territory={Terr}，身份={Role}）。",
            fortuneRunCount, fortuneTerritory, IsPartyLeader() ? "队长" : "队员");
    }

    private void SetFortuneStage(FortuneStage stage)
    {
        if (fortuneStage == stage)
            return;

        fortuneStage = stage;
        fortuneStageAt = DateTime.UtcNow;

        if (config.devMode)
            log.Information("[AutoPalExplorer][财运亨通] 阶段 -> {Stage}", FortuneStageText);
    }

    /// <summary>进本后先等读条结束再等几秒，避免队友还没进齐就用魔陶器。</summary>
    private void TickFortuneWaitingEnter()
    {
        if (navigator.IsBusy)
            navigator.Stop();

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            SetIntent("财运亨通：等待进本读条结束");
            fortuneStageAt = DateTime.UtcNow; // 读条期间不计时
            return;
        }

        var waited = (DateTime.UtcNow - fortuneStageAt).TotalSeconds;
        if (waited < FortuneEnterDelay)
        {
            SetIntent($"财运亨通：进本缓冲 {waited:0.0}/{FortuneEnterDelay:0}s");
            return;
        }

        // 进本指令（y_adjust / speed 之类），队长队员都发一次
        SendFortuneEnterCommands();

        // 队长负责用魔陶器；队员直接进检测阶段跟着走
        SetFortuneStage(IsPartyLeader() ? FortuneStage.UsingIntuition : FortuneStage.Detecting);
    }

    /// <summary>队长：使用「魔陶器：感知宝藏」。</summary>
    private void TickFortuneUsingIntuition(Vector3 pos)
    {
        if (navigator.IsBusy)
            navigator.Stop();

        // 系统已经明说本层没宝藏：不用再等，直接退本重进
        if (fortuneFloorNoTreasure)
        {
            log.Information("[AutoPalExplorer][财运亨通] 本层没有宝藏（系统提示），立刻退本重进。");
            SetFortuneStage(FortuneStage.Leaving);
            return;
        }

        // 魔陶器已经生效（自己刚用的 / 队友用的）或宝藏已经看得见：进检测阶段等系统结论
        if (pomanderManager.HasBuriedBuff || FindNearestBuriedChest(pos, FortuneSearchRadius) is not null)
        {
            SetFortuneStage(FortuneStage.Detecting);
            return;
        }

        switch (pomanderManager.IntuitionState)
        {
            case PomanderManager.IntuitionRequestState.Used:
                // 已经点下去了，接下来只等宝藏点出不出现
                SetFortuneStage(FortuneStage.Detecting);
                return;

            case PomanderManager.IntuitionRequestState.Missing:
            {
                const string msg = "[AutoPalExplorer][财运亨通] 存档里已经没有「魔陶器：感知宝藏」了，自动停止。" +
                                   "请重新准备一个 1-10 层、带感知宝藏的存档。";
                log.Warning(msg);
                TryChatCommand(msg);
                Stop();
                return;
            }
        }

        var now = DateTime.UtcNow;
        var elapsed = (now - fortuneStageAt).TotalSeconds;

        // 面板一直不就绪之类的异常：别卡死，当成「这趟白跑」直接退本重进
        if (elapsed >= FortuneIntuitionStageTimeout)
        {
            log.Warning("[AutoPalExplorer][财运亨通] 使用感知宝藏超时（{Sec:0.0}s 没有结果），退本重进。", elapsed);
            SetFortuneStage(FortuneStage.Leaving);
            return;
        }

        if (now < nextFortuneIntuitionAt)
        {
            SetIntent("财运亨通：等待感知宝藏生效");
            return;
        }

        if (fortuneIntuitionAttempts >= FortuneIntuitionMaxAttempts)
        {
            SetIntent("财运亨通：等待感知宝藏生效");
            return;
        }

        fortuneIntuitionAttempts++;
        nextFortuneIntuitionAt = now.AddSeconds(FortuneIntuitionRetrySeconds);
        pomanderManager.UseIntuitionPomander();
        SetIntent($"财运亨通：使用魔陶器·感知宝藏（第 {fortuneIntuitionAttempts} 次请求）");
    }

    /// <summary>等宝藏点出现；超时就认为本层没宝藏，直接退本重进。</summary>
    private void TickFortuneDetecting(Vector3 pos)
    {
        if (navigator.IsBusy)
            navigator.Stop();

        // 队员：队长已经把宝藏踩出来了（聊天全队可见），跟着一起退本
        if (hasOpenBurinedChest)
        {
            SetFortuneStage(FortuneStage.Leaving);
            return;
        }

        // 系统已经明说本层没宝藏：全队立刻退本重进，不用等检测超时
        if (fortuneFloorNoTreasure)
        {
            log.Information("[AutoPalExplorer][财运亨通] 本层没有宝藏（系统提示），立刻退本重进。");
            SetFortuneStage(FortuneStage.Leaving);
            return;
        }

        // 宝藏就在视野里：直接过去
        if (FindNearestBuriedChest(pos, FortuneSearchRadius) is { } buried)
        {
            fortuneTreasurePos = buried.Position;
            fortuneTeleportAttempts = 0;
            log.Information("[AutoPalExplorer][财运亨通] 感知到埋藏的宝藏：({X:0.00}, {Y:0.00}, {Z:0.00})。",
                buried.Position.X, buried.Position.Y, buried.Position.Z);

            SetFortuneStage(IsPartyLeader() ? FortuneStage.Teleporting : FortuneStage.WaitingTreasure);
            return;
        }

        // 收到「这一朝圣路似乎有宝藏」：本层确实有，只是宝藏不在视野里
        if (fortuneFloorHasTreasure)
        {
            if (IsPartyLeader())
            {
                BeginFortuneSearch();
                SetFortuneStage(FortuneStage.Searching);
            }
            else
            {
                // 队员：原地等队长把宝藏找出来并踩掉
                SetFortuneStage(FortuneStage.WaitingTreasure);
            }
            return;
        }

        // 「可以感知到宝藏埋藏的位置了！」只代表魔陶器生效了，不代表本层有宝藏
        // （紧跟着才是“似乎有/没有宝藏”那条）。这里只用它把检测计时对齐到魔陶器真正生效的时刻。
        if (!fortuneIntuitionLanded && pomanderManager.HasBuriedBuff)
        {
            fortuneIntuitionLanded = true;
            fortuneStageAt = DateTime.UtcNow;

            if (config.devMode)
                log.Information("[AutoPalExplorer][财运亨通] 感知宝藏已生效，等系统给出有 / 没有宝藏的结论。");
        }

        // 队员不用魔陶器，多等一会儿，避免比队长先跑出去
        var limit = IsPartyLeader() ? FortuneDetectWait : FortuneDetectWait + FortuneMemberExtraWait;
        var waited = (DateTime.UtcNow - fortuneStageAt).TotalSeconds;
        if (waited >= limit)
        {
            log.Information("[AutoPalExplorer][财运亨通] 本层没有埋藏的宝藏（等了 {W:0.0}s），退本重进。", waited);
            SetFortuneStage(FortuneStage.Leaving);
            return;
        }

        SetIntent($"财运亨通：检测宝藏中 {waited:0.0}/{limit:0}s");
    }

    /// <summary>开始逐房间传送搜索：当前所在房间已经看得见了，不必再传一次。</summary>
    private void BeginFortuneSearch()
    {
        fortuneSearchedRoomMask = 0;
        fortuneSearchRoom = -1;
        fortuneSearchHops = 0;
        nextFortuneRoomHopAt = DateTime.MinValue;
        fortuneSearchStartedAt = DateTime.UtcNow;

        if (currentRoomIndex >= 0 && currentRoomIndex < 32)
            fortuneSearchedRoomMask |= 1 << currentRoomIndex;

        log.Information("[AutoPalExplorer][财运亨通] 本层有宝藏但不在视野里，开始逐房间传送搜索（当前房间 {Room}）。",
            currentRoomIndex);
    }

    /// <summary>
    /// 队长：本层有宝藏但看不见时，按「离当前位置最近的、还没搜过的房间」逐个传送过去，
    /// 每传一次等物件加载；宝藏一进对象表就切去踩它。
    /// </summary>
    private void TickFortuneSearching(Vector3 pos)
    {
        if (navigator.IsBusy)
            navigator.Stop();

        if (hasOpenBurinedChest)
        {
            SetFortuneStage(FortuneStage.Leaving);
            return;
        }

        // 系统消息比状态机慢半拍时，搜到一半也要能收手
        if (fortuneFloorNoTreasure)
        {
            log.Information("[AutoPalExplorer][财运亨通] 搜索途中收到「本层没有宝藏」，停止搜索退本重进。");
            SetFortuneStage(FortuneStage.Leaving);
            return;
        }

        // 找到了：切去踩宝藏
        if (FindNearestBuriedChest(pos, FortuneSearchRadius) is { } buried)
        {
            fortuneTreasurePos = buried.Position;
            fortuneTeleportAttempts = 0; // 之前的传送是用来找房间的，重新计
            log.Information("[AutoPalExplorer][财运亨通] 第 {Hops} 个房间找到了埋藏的宝藏：({X:0.00}, {Y:0.00}, {Z:0.00})。",
                fortuneSearchHops, buried.Position.X, buried.Position.Y, buried.Position.Z);

            SetFortuneStage(FortuneStage.Teleporting);
            return;
        }

        var now = DateTime.UtcNow;
        var searched = (now - fortuneSearchStartedAt).TotalSeconds;
        if (searched >= FortuneSearchTimeout)
        {
            log.Warning("[AutoPalExplorer][财运亨通] 逐房间搜索超时（{Sec:0.0}s，已传 {Hops} 个房间），退本重进。",
                searched, fortuneSearchHops);
            SetFortuneStage(FortuneStage.Leaving);
            return;
        }

        // 上一次传送之后要留时间让物件加载出来
        if (now < nextFortuneRoomHopAt)
        {
            SetIntent($"财运亨通：房间 {fortuneSearchRoom} 找宝藏中（已传 {fortuneSearchHops} 个房间）");
            return;
        }

        // 传送落点可能和目标房间差一格：把「现在实际所在的房间」也算已搜过
        // （站在这里都没看见宝藏，就不用再传一次了）
        if (currentRoomIndex >= 0 && currentRoomIndex < 32)
            fortuneSearchedRoomMask |= 1 << currentRoomIndex;

        if (!TryPickNextSearchRoom(pos, out var room, out var center))
        {
            log.Warning("[AutoPalExplorer][财运亨通] 能传的房间都传了一遍（{Hops} 个）还是没看到宝藏，退本重进。",
                fortuneSearchHops);
            SetFortuneStage(FortuneStage.Leaving);
            return;
        }

        fortuneSearchedRoomMask |= 1 << room;
        fortuneSearchRoom = room;
        fortuneSearchHops++;
        nextFortuneRoomHopAt = now.AddSeconds(FortuneRoomScanWait);

        SetIntent($"财运亨通：传送到房间 {room} 找宝藏（第 {fortuneSearchHops} 个）");
        log.Information("[AutoPalExplorer][财运亨通] 传送到房间 {Room}（行 {Row} 列 {Col}）找宝藏。",
            room, room / RoomGraph.GridSize, room % RoomGraph.GridSize);

        SendFortuneTeleport(center, $"房间 {room}");
    }

    /// <summary>挑下一个要搜的房间：本层存在、还没搜过、离当前位置最近的那个。</summary>
    private unsafe bool TryPickNextSearchRoom(Vector3 pos, out int room, out Vector3 center)
    {
        room = -1;
        center = default;

        var dd = RoomGraph.GetDeepDungeon();
        if (dd == null)
        {
            log.Warning("[AutoPalExplorer][财运亨通] 读不到深层迷宫房间数据，没法逐房间搜索。");
            return false;
        }

        var bestDistSq = float.MaxValue;
        for (var i = 0; i < RoomGraph.MaxRooms; i++)
        {
            if ((fortuneSearchedRoomMask & (1 << i)) != 0)
                continue;

            if (!RoomGraph.RoomExists(dd, i))
                continue;

            if (!roomCenters.TryGetCenter(i, out var c))
                continue;

            var dx = c.X - pos.X;
            var dz = c.Z - pos.Z;
            var distSq = dx * dx + dz * dz;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                room = i;
                center = c;
            }
        }

        return room >= 0;
    }

    /// <summary>队长：传送到宝藏坐标（y 要减去偏移，否则会落在半空）。</summary>
    private void TickFortuneTeleporting(Vector3 pos)
    {
        if (navigator.IsBusy)
            navigator.Stop();

        if (fortuneTreasurePos is not { } tp)
        {
            SetFortuneStage(FortuneStage.Detecting);
            return;
        }

        var dx = tp.X - pos.X;
        var dz = tp.Z - pos.Z;
        if (dx * dx + dz * dz <= FortuneArriveRadius * FortuneArriveRadius)
        {
            SetFortuneStage(FortuneStage.WaitingTreasure);
            return;
        }

        var now = DateTime.UtcNow;
        if (now < nextFortuneTeleportAt)
        {
            SetIntent("财运亨通：等待移动到宝藏点");
            return;
        }

        // 指令是 /vnav moveto 这类要走过去的移动时，人还在动就别重发，否则会不停重新寻路原地抽搐
        if (currentSpeedMps > FortuneMovingThreshold)
        {
            SetIntent($"财运亨通：正在前往宝藏点（{MathF.Sqrt(dx * dx + dz * dz):0.0} 格）");
            return;
        }

        if (fortuneTeleportAttempts >= FortuneTeleportMaxAttempts)
        {
            log.Warning("[AutoPalExplorer][财运亨通] 传送到宝藏点失败 {N} 次，改为原地等待。", fortuneTeleportAttempts);
            SetFortuneStage(FortuneStage.WaitingTreasure);
            return;
        }

        fortuneTeleportAttempts++;
        nextFortuneTeleportAt = now.AddSeconds(FortuneTeleportRetrySeconds);
        SetIntent($"财运亨通：传送到宝藏点（第 {fortuneTeleportAttempts} 次）");
        SendFortuneTeleport(tp, "宝藏点");
    }

    /// <summary>进本后要发的一组指令（默认把 i-ching-commander 的 y_adjust / speed 设成穿地板模式）。</summary>
    private void SendFortuneEnterCommands()
    {
        if (fortuneEnterCommandsSent)
            return;

        fortuneEnterCommandsSent = true;
        fortuneLeaveCommandsSent = false;
        SendFortuneCommandList(config.FortuneEnterCommands, "进本");
    }

    /// <summary>退本前要发的一组指令（默认把 y_adjust / speed 恢复原状）。</summary>
    private void SendFortuneLeaveCommands()
    {
        if (fortuneLeaveCommandsSent)
            return;

        fortuneLeaveCommandsSent = true;
        SendFortuneCommandList(config.FortuneLeaveCommands, "退本");
    }

    /// <summary>
    /// 手动 Stop / 离开地宫时兜底：进本指令发过、退本指令还没发就补发一次，
    /// 免得 y_adjust / speed 一直留在穿地板状态。
    /// </summary>
    private void EnsureFortuneRestoreCommands()
    {
        if (fortuneEnterCommandsSent && !fortuneLeaveCommandsSent)
            SendFortuneLeaveCommands();
    }

    /// <summary>按 ; / 换行拆开逐条执行。</summary>
    private void SendFortuneCommandList(string raw, string what)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;

        var commands = raw.Split(new[] { ';', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var cmd in commands)
        {
            if (!cmd.StartsWith('/'))
            {
                log.Warning("[AutoPalExplorer][财运亨通] {What}指令不是以 / 开头，跳过：{Cmd}", what, cmd);
                continue;
            }

            if (TryCommand(cmd))
                log.Information("[AutoPalExplorer][财运亨通] 已发送{What}指令：{Cmd}", what, cmd);
            else
                log.Warning("[AutoPalExplorer][财运亨通] {What}指令没有被任何插件处理：{Cmd}", what, cmd);
        }
    }

    /// <summary>发一条移动 / 传送指令；Y 会减去配置里的偏移（默认 0）。</summary>
    private void SendFortuneTeleport(Vector3 target, string what)
    {
        var prefix = string.IsNullOrWhiteSpace(config.FortuneTeleportCommand)
            ? Configuration.DefaultFortuneTeleportCommand
            : config.FortuneTeleportCommand.Trim();

        var cmd = string.Format(CultureInfo.InvariantCulture, "{0} {1:0.00} {2:0.00} {3:0.00}",
            prefix, target.X, target.Y - config.FortuneTeleportYOffset, target.Z);

        if (!TryCommand(cmd))
        {
            log.Warning("[AutoPalExplorer][财运亨通] 传送指令没有被任何插件处理：{Cmd}（目标={What}，检查传送插件是否开着、指令前缀是否正确）",
                cmd, what);
        }
        else if (config.devMode)
        {
            log.Information("[AutoPalExplorer][财运亨通] 传送 -> {What}：{Cmd}", what, cmd);
        }
    }

    /// <summary>站着不动，等「发现了埋藏的宝藏！」。</summary>
    private void TickFortuneWaitingTreasure(Vector3 pos)
    {
        // 「然后不动」：这里不发任何移动指令
        if (navigator.IsBusy)
            navigator.Stop();

        if (hasOpenBurinedChest)
        {
            SetFortuneStage(FortuneStage.Leaving);
            return;
        }

        var waited = (DateTime.UtcNow - fortuneStageAt).TotalSeconds;

        // 队长：传送没落到点上（掉进坑里 / 被顶开）时补一次传送
        if (IsPartyLeader()
            && fortuneTreasurePos is { } tp
            && waited >= FortuneRetryTeleportAfterSeconds
            && fortuneTeleportAttempts < FortuneTeleportMaxAttempts)
        {
            var dx = tp.X - pos.X;
            var dz = tp.Z - pos.Z;
            if (dx * dx + dz * dz > FortuneArriveRadius * FortuneArriveRadius)
            {
                SetFortuneStage(FortuneStage.Teleporting);
                return;
            }
        }

        // 队长站在宝藏上，等 FortuneTreasureWait 就够；
        // 队员要等队长可能的「逐房间找宝藏」跑完，上限相应放宽。
        var limit = IsPartyLeader() ? FortuneTreasureWait : FortuneWaitTreasureLimit;
        if (waited >= limit)
        {
            log.Warning("[AutoPalExplorer][财运亨通] 等宝藏触发超时（{W:0.0}s），退本重进。", waited);
            SetFortuneStage(FortuneStage.Leaving);
            return;
        }

        SetIntent($"财运亨通：站定等宝藏踩出 {waited:0.0}/{limit:0}s");
    }

    /// <summary>退本：全队各自退（队长队员都跑这段），随后回到入口地图由进本流程重进。</summary>
    private void TickFortuneLeaving()
    {
        if (navigator.IsBusy)
            navigator.Stop();

        SetIntent($"财运亨通：退本中（累计宝藏 {fortuneTreasureCount} 个）");

        // 退本指令（把 y_adjust / speed 恢复原状），发一次就够
        SendFortuneLeaveCommands();

        // 退本确认窗口 / 结算界面
        TryConfirmFortuneAddons();

        // 已经在读条切图了：别再重复请求退本
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return;

        var now = DateTime.UtcNow;
        if (now < nextFortuneLeaveAt)
            return;

        nextFortuneLeaveAt = now.AddSeconds(FortuneLeaveRetrySeconds);
        fortuneLeaveAttempts++;

        // 前几次走正常流程（会弹确认窗口，由上面点掉）；一直退不出去就强制退
        var forced = fortuneLeaveAttempts > FortuneLeaveSoftAttempts;
        if (!forced && !EventFramework.CanLeaveCurrentContent())
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer][财运亨通] 现在还不能退本，等下次重试。");
            return;
        }

        EventFramework.LeaveCurrentContent(forced);
        log.Information("[AutoPalExplorer][财运亨通] 请求退本（第 {N} 次，强制={Forced}）。", fortuneLeaveAttempts, forced);
    }

    /// <summary>退本流程里的窗口：SelectYesno -> 是；DeepDungeonResult -> 关掉。</summary>
    private unsafe bool TryConfirmFortuneAddons()
    {
        var now = DateTime.UtcNow;
        if (now < nextFortuneAddonFireAt)
            return false;

        if (TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yn) && IsAddonReady(yn))
        {
            Callback.Fire(yn, true, 0);
            nextFortuneAddonFireAt = now.AddSeconds(1.0);

            if (config.devMode)
                log.Information("[AutoPalExplorer][财运亨通] 点击 SelectYesno -> 0（确认退本）。");
            return true;
        }

        if (TryGetAddonByName<AtkUnitBase>("DeepDungeonResult", out var result) && IsAddonReady(result))
        {
            Callback.Fire(result, true, -1);
            nextFortuneAddonFireAt = now.AddSeconds(1.0);

            if (config.devMode)
                log.Information("[AutoPalExplorer][财运亨通] 点击 DeepDungeonResult -> -1（关闭结算）。");
            return true;
        }

        return false;
    }

    /// <summary>
    /// 收到「发现了埋藏的宝藏！」时计数（每次进本只计一次）。
    /// 由 <see cref="NotifyBuriedtActivated"/> 调用。
    /// </summary>
    private void CountFortuneTreasure()
    {
        if (!config.FortuneMode || !IsRunning || fortuneTreasureCounted)
            return;

        fortuneTreasureCounted = true;
        fortuneTreasureCount++;

        log.Information("[AutoPalExplorer][财运亨通] 踩出宝藏！累计 {Count} 个 / 已进本 {Run} 次。",
            fortuneTreasureCount, fortuneRunCount);
    }
}
