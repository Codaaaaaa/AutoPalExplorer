using System;
using System.Numerics;
using Dalamud.Plugin.Services;

namespace AutoPalExplorer.Services;

public sealed class Navigator
{
    private readonly IObjectTable objectTable;
    private readonly VNavmeshClient vnavmesh;
    private readonly IPluginLog log;

    private Vector3? currentTarget;
    private float lastDistSq;
    private int stagnantTicks;

    // 陷阱避障插入绕行圆弧时，人会先「绕远」再靠近目标，直线距离一度是变大的，
    // 那段时间不能按卡住处理，否则会误判放弃目标。见 SuspendStagnationCheck。
    private DateTime stagnationSuspendedUntil = DateTime.MinValue;

    private const float ArriveThresholdSq = 0.5f * 0.5f;
    private const float MinProgressSq = 0.01f;      // 认为“有在动”的最小距离变化
    private const int MaxStagnantTicks = 120;       // 卡这么多帧就放弃当前目标

    public Navigator(IObjectTable objectTable, VNavmeshClient vnavmesh, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.vnavmesh = vnavmesh;
        this.log = log;
    }

    public Vector3? CurrentTarget => currentTarget;
    public bool IsBusy => currentTarget.HasValue;

    /// <summary>底层 vnavmesh IPC。陷阱避障要直接读/改当前路径（ListWaypoints / MoveTo）。</summary>
    public VNavmeshClient Vnav => vnavmesh;

    public bool TryMoveTo(Vector3 target)
    {
        // 如果已经有几乎相同的目标，就不要重复发
        if (currentTarget is { } existing)
        {
            var dx = existing.X - target.X;
            var dz = existing.Z - target.Z;
            var distSq = dx * dx + dz * dz;
            if (distSq < 0.01f)
                return true;
        }

        if (!vnavmesh.IsReady())
        {
            log.Warning("[AutoPalExplorer] vnavmesh not ready.");
            return false;
        }

        if (!vnavmesh.PathfindAndMoveTo(target))
        {
            log.Debug($"[AutoPalExplorer] Failed to start path to {target}.");
            return false;
        }

        currentTarget = target;
        lastDistSq = float.MaxValue;
        stagnantTicks = 0;
        log.Debug($"[AutoPalExplorer] Moving to {target}.");
        return true;
    }

    public void Update()
    {
        if (currentTarget is not { } target)
            return;

        var player = objectTable.LocalPlayer;
        if (player is null)
        {
            Stop();
            return;
        }

        var pos = player.Position;
        var dx = target.X - pos.X;
        var dz = target.Z - pos.Z;
        var distSq = dx * dx + dz * dz;

        // 到达
        if (distSq <= ArriveThresholdSq)
        {
            log.Debug($"[AutoPalExplorer] Arrived at {target}, distSq={distSq}.");
            currentTarget = null;
            stagnantTicks = 0;
            lastDistSq = float.MaxValue;
            return;
        }

        // 绕行期间暂停卡住检测（人在绕圈，直线距离本来就会变大）
        if (DateTime.UtcNow < stagnationSuspendedUntil)
        {
            stagnantTicks = 0;
            lastDistSq = distSq;
            return;
        }

        // 卡住检测：如果距离长期没明显变小，就认为失败
        if (lastDistSq < float.MaxValue && distSq + MinProgressSq >= lastDistSq)
        {
            stagnantTicks++;
            if (stagnantTicks >= MaxStagnantTicks)
            {
                log.Debug($"[AutoPalExplorer] Stagnant while moving to {target}, giving up. distSq={distSq}.");
                currentTarget = null;
                stagnantTicks = 0;
                lastDistSq = float.MaxValue;
                return;
            }
        }
        else
        {
            stagnantTicks = 0;
        }

        lastDistSq = distSq;
    }

    /// <summary>
    /// 在接下来这段时间内不做卡住检测。
    /// 陷阱避障替换路径（vnavmesh.Path.MoveTo）之后调用：绕行圆弧会让直线距离先变大，
    /// 那不是卡住。真正卡死由避障自己的进度检测兜底（见 TickTrapAvoidance）。
    /// </summary>
    public void SuspendStagnationCheck(double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        if (until > stagnationSuspendedUntil)
            stagnationSuspendedUntil = until;

        stagnantTicks = 0;
        lastDistSq = float.MaxValue;
    }

    public void Stop()
    {
        currentTarget = null;
        stagnantTicks = 0;
        lastDistSq = float.MaxValue;
        stagnationSuspendedUntil = DateTime.MinValue;
        vnavmesh.Stop();
    }
}
