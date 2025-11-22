using System.Numerics;
using Dalamud.Plugin.Services;

namespace AutoPalExplorer.Services;

public sealed class Navigator
{
    private readonly IClientState clientState;
    private readonly VNavmeshClient vnavmesh;
    private readonly IPluginLog log;

    private Vector3? currentTarget;
    private float lastDistSq;
    private int stagnantTicks;

    private const float ArriveThresholdSq = 0.5f * 0.5f;
    private const float MinProgressSq = 0.01f;      // 认为“有在动”的最小距离变化
    private const int MaxStagnantTicks = 120;       // 卡这么多帧就放弃当前目标

    public Navigator(IClientState clientState, VNavmeshClient vnavmesh, IPluginLog log)
    {
        this.clientState = clientState;
        this.vnavmesh = vnavmesh;
        this.log = log;
    }

    public Vector3? CurrentTarget => currentTarget;
    public bool IsBusy => currentTarget.HasValue;

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

        var player = clientState.LocalPlayer;
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

    public void Stop()
    {
        currentTarget = null;
        stagnantTicks = 0;
        lastDistSq = float.MaxValue;
        vnavmesh.Stop();
    }
}
