using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Game.ClientState.Objects.Enums;

using AutoPalExplorer.Helpers;

namespace AutoPalExplorer.Services;

/// <summary>
/// 路径级陷阱避障。
///
/// 原来的 <c>TrySafeMoveTo</c> 只管「目标点别贴着陷阱」，路上照样会直接踩过去。
/// 这里补上路上的那一段，做法和 BOCCHI 避怪一样，是对 vnav 路线做后处理而不是换寻路算法：
///
///     vnav 正常寻路 -> 读回它的 waypoint -> 自己判断有没有穿过陷阱圈
///       -> 穿了就插入切线 / 圆弧绕行点 -> vnavmesh.Path.MoveTo 交回去走
///
/// 每 <see cref="ReplanCooldownSeconds"/> 秒最多重新规划一次；陷阱是静态的，
/// 所以正常情况下一层只会在「新陷阱被看见」的那一帧真正改一次路线。
///
/// 只在普通模式生效：财运亨通模式全程用传送指令（/vnav moveto）而不是 Navigator，
/// 在 <see cref="Update"/> 里就已经提前 return 了，根本走不到这里。
/// </summary>
public sealed partial class AutoPalController
{
    /// <summary>两次重新规划之间的最小间隔（秒）。</summary>
    private const double ReplanCooldownSeconds = 1.25;

    /// <summary>换目标后先等一下再插手，避开 vnav 异步寻路还没落地的窗口。</summary>
    private const double ReplanInitialDelaySeconds = 0.4;

    /// <summary>只考虑玩家这个范围内的陷阱（超出这个距离的路段还早，等走近了再说）。</summary>
    private const float TrapScanRadius = 60f;

    /// <summary>一次最多喂给规划器多少个陷阱圈。</summary>
    private const int MaxTrapZones = 24;

    /// <summary>绕行路线走了这么久还没挪动，就判定绕废了，回退原路。</summary>
    private const double DetourStuckSeconds = 2.0;

    /// <summary>判定「还在动」的最小位移（2D，米）。</summary>
    private const float DetourProgressThreshold = 0.35f;

    /// <summary>绕废一次之后多久内不再插手（免得来回改路线把人卡死在原地）。</summary>
    private const double GiveUpCooldownSeconds = 15.0;

    /// <summary>替换路径后暂停 Navigator 卡住检测的时长；绕圈时直线距离会先变大。</summary>
    private const double StagnationSuspendSeconds = 5.0;

    private Vector3? trapAvoidTarget;          // 当前正在跟的导航目标（用来判断目标是否换了）
    // 这一趟是否已经发过绕行路线。发过之后一直保持到换目标为止：
    // 绕行点可能把人顶在墙上，得一直盯着有没有在动（见 TickDetourStuck）。
    private bool trapAvoidActive;
    private DateTime nextTrapReplanAt = DateTime.MinValue;
    private DateTime trapAvoidGiveUpUntil = DateTime.MinValue;
    private Vector3 trapAvoidProgressPos;
    private DateTime trapAvoidProgressAt = DateTime.MinValue;
    private readonly List<TrapAvoidPlanner.DangerZone> trapZoneBuffer = new();

    /// <summary>换层 / 启停时清空避障状态。</summary>
    private void ResetTrapAvoidState()
    {
        trapAvoidTarget = null;
        trapAvoidActive = false;
        nextTrapReplanAt = DateTime.MinValue;
        trapAvoidGiveUpUntil = DateTime.MinValue;
        trapAvoidProgressAt = DateTime.MinValue;
        trapZoneBuffer.Clear();
        navigator.Vnav.ClearMeshCache();
    }

    /// <summary>
    /// 每帧调用（普通模式，紧跟在 navigator.Update() 之后）：
    /// 检查 vnav 当前路线有没有穿过陷阱，穿了就换成绕行路线。
    /// </summary>
    private void TickTrapAvoidance(Vector3 playerPos)
    {
        if (!config.AvoidTrapsOnPath || !navigator.Vnav.SupportsPathRewrite)
            return;

        // 没有导航目标就没什么可绕的
        if (navigator.CurrentTarget is not { } target)
        {
            if (trapAvoidTarget is not null)
                ClearTrapAvoidRun();
            return;
        }

        var now = DateTime.UtcNow;

        // 目标换了：整个避障状态重来（包括放弃冷却，新目标值得再试一次）
        if (trapAvoidTarget is not { } tracked || IsDifferentTarget(tracked, target, 0.5f))
        {
            ClearTrapAvoidRun();
            trapAvoidTarget = target;
            nextTrapReplanAt = now.AddSeconds(ReplanInitialDelaySeconds);
            return;
        }

        // 上一次绕行把人卡住了：这段时间内老老实实走 vnav 的原路
        if (now < trapAvoidGiveUpUntil)
            return;

        if (trapAvoidActive && TickDetourStuck(playerPos, now))
            return;

        if (now < nextTrapReplanAt)
            return;

        // vnav 还在异步寻路时不要动路径：它算完会直接把我们的绕行路线覆盖掉
        if (navigator.Vnav.IsMoving())
            return;

        var zones = CollectTrapZones(playerPos);
        if (zones.Count == 0)
        {
            nextTrapReplanAt = now.AddSeconds(ReplanCooldownSeconds);
            return;
        }

        var waypoints = navigator.Vnav.ListWaypoints();
        if (waypoints.Count == 0)
            return;

        // 规划器要求首点是玩家当前位置：第一段就是「人 -> 下一个 waypoint」
        var path = new List<Vector3>(waypoints.Count + 1) { playerPos };
        path.AddRange(waypoints);

        nextTrapReplanAt = now.AddSeconds(ReplanCooldownSeconds);

        var result = TrapAvoidPlanner.Plan(path, zones, navigator.Vnav.ProbeMesh, out var detour);

        switch (result)
        {
            case TrapAvoidPlanner.PlanResult.Clear:
                // 已经不穿圈了（本来就没穿，或者上一轮插的绕行点已经生效）
                return;

            case TrapAvoidPlanner.PlanResult.Failed:
                // 绕不过去（两边都不可走 / 陷阱堆太密）：保持 vnav 原路线，
                // 目标点级别的避让（TrySafeMoveTo）仍然生效。
                if (config.devMode)
                    log.Information("[AutoPalExplorer][避陷阱] 路线穿过陷阱但绕不过去，保持原路线。");
                return;
        }

        // 首点是玩家自己，交给 vnavmesh 前要去掉
        detour.RemoveAt(0);
        if (detour.Count == 0)
            return;

        if (!navigator.Vnav.MoveTo(detour))
            return;

        trapAvoidActive = true;
        trapAvoidProgressPos = playerPos;
        trapAvoidProgressAt = now;
        navigator.SuspendStagnationCheck(StagnationSuspendSeconds);

        if (config.devMode)
        {
            log.Information("[AutoPalExplorer][避陷阱] 原路线 {Old} 点穿过 {Zones} 个陷阱圈，已替换为 {New} 点的绕行路线。",
                waypoints.Count, zones.Count, detour.Count);
        }
    }

    /// <summary>
    /// 绕行路线的兜底：算出来的圆弧点可能落在墙里 / 台阶外，人会原地顶着不动。
    /// 一定时间没挪窝就停下让上层重新用 vnav 原生寻路，并在冷却期内不再插手。
    /// 返回 true 表示本次已经处理了（调用方直接返回）。
    /// </summary>
    private bool TickDetourStuck(Vector3 playerPos, DateTime now)
    {
        if (trapAvoidProgressAt == DateTime.MinValue)
        {
            trapAvoidProgressPos = playerPos;
            trapAvoidProgressAt = now;
            return false;
        }

        var dx = playerPos.X - trapAvoidProgressPos.X;
        var dz = playerPos.Z - trapAvoidProgressPos.Z;
        if (dx * dx + dz * dz >= DetourProgressThreshold * DetourProgressThreshold)
        {
            trapAvoidProgressPos = playerPos;
            trapAvoidProgressAt = now;
            return false;
        }

        if ((now - trapAvoidProgressAt).TotalSeconds < DetourStuckSeconds)
            return false;

        log.Information("[AutoPalExplorer][避陷阱] 绕行路线疑似卡住，回退到 vnav 原生寻路，{Seconds}s 内不再绕行。",
            GiveUpCooldownSeconds);

        // 清掉目标，让上层的优先级链下一帧用 PathfindAndMoveTo 重新发一条正常路线
        navigator.Stop();
        ClearTrapAvoidRun();
        trapAvoidGiveUpUntil = now.AddSeconds(GiveUpCooldownSeconds);
        return true;
    }

    /// <summary>清掉「当前这一趟」的避障状态，但保留放弃冷却。</summary>
    private void ClearTrapAvoidRun()
    {
        trapAvoidTarget = null;
        trapAvoidActive = false;
        trapAvoidProgressAt = DateTime.MinValue;
        nextTrapReplanAt = DateTime.MinValue;
    }

    /// <summary>
    /// 扫出玩家附近的陷阱，建成 XZ 平面上的圆形危险区
    /// （和 BOCCHI 把每只怪建成一个圆是一回事，只是陷阱不会动，圈也更小）。
    /// </summary>
    private List<TrapAvoidPlanner.DangerZone> CollectTrapZones(Vector3 playerPos)
    {
        trapZoneBuffer.Clear();

        var radius = TrapPathAvoidRadiusCfg;
        var scanSq = TrapScanRadius * TrapScanRadius;

        foreach (var obj in objectTable)
        {
            if (trapZoneBuffer.Count >= MaxTrapZones)
                break;

            if (obj.ObjectKind != ObjectKind.EventObj)
                continue;

            if (!ObjectIds.TrapIds.Contains(obj.BaseId))
                continue;

            var dx = obj.Position.X - playerPos.X;
            var dz = obj.Position.Z - playerPos.Z;
            if (dx * dx + dz * dz > scanSq)
                continue;

            trapZoneBuffer.Add(new TrapAvoidPlanner.DangerZone(obj.Position, radius));
        }

        return trapZoneBuffer;
    }
}
