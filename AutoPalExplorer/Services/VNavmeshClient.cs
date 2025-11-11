using System;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace AutoPalExplorer.Services;

/// <summary>
/// vnavmesh IPC 封装。
/// 基于你提供的 NavmeshIPC 映射：
///
/// [EzIPC("Nav.%m")]        Func&lt;bool&gt;                 IsReady
/// [EzIPC("SimpleMove.%m")] Func&lt;Vector3, bool, bool&gt;  PathfindAndMoveTo
/// [EzIPC("SimpleMove.%m")] Func&lt;bool&gt;                 PathfindInProgress
/// [EzIPC("Path.%m")]       Action                      Stop
///
/// 展开后的通道名：
/// vnavmesh.Nav.IsReady
/// vnavmesh.SimpleMove.PathfindAndMoveTo
/// vnavmesh.SimpleMove.PathfindInProgress
/// vnavmesh.Path.Stop
/// </summary>
public sealed class VNavmeshClient
{
    private readonly IPluginLog log;

    /// <summary>vnavmesh.Nav.IsReady</summary>
    private readonly ICallGateSubscriber<bool>? isReadySub;

    /// <summary>vnavmesh.SimpleMove.PathfindAndMoveTo(dest: Vector3, flag: bool) -> bool</summary>
    private readonly ICallGateSubscriber<Vector3, bool, bool>? pathfindAndMoveToSub;

    /// <summary>vnavmesh.SimpleMove.PathfindInProgress() -> bool</summary>
    private readonly ICallGateSubscriber<bool>? pathfindInProgressSub;

    /// <summary>vnavmesh.Path.Stop() -> void</summary>
    private readonly ICallGateSubscriber<object>? stopSub;

    public VNavmeshClient(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;

        try
        {
            isReadySub = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] Failed to hook vnavmesh.Nav.IsReady: {ex.Message}");
        }

        try
        {
            // Func<Vector3, bool, bool> => ICallGateSubscriber<Vector3, bool, bool>
            pathfindAndMoveToSub =
                pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] Failed to hook vnavmesh.SimpleMove.PathfindAndMoveTo: {ex.Message}");
        }

        try
        {
            // Func<bool> => ICallGateSubscriber<bool>
            pathfindInProgressSub =
                pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] Failed to hook vnavmesh.SimpleMove.PathfindInProgress: {ex.Message}");
        }

        try
        {
            // Action() => 使用 TRet=object 的订阅者，并通过 InvokeAction() 调用
            stopSub = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] Failed to hook vnavmesh.Path.Stop: {ex.Message}");
        }
    }

    public bool IsReady()
    {
        try
        {
            return isReadySub?.InvokeFunc() ?? false;
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] vnavmesh.Nav.IsReady error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 让 vnavmesh 直接寻路并移动到目标点。
    /// 这里调用 PathfindAndMoveTo(dest, false)：
    /// 第二个参数的语义请对照 vnavmesh 文档（通常是飞行/地面之类的开关）。
    /// </summary>
    public bool PathfindAndMoveTo(Vector3 dest)
    {
        if (pathfindAndMoveToSub is null)
            return false;

        try
        {
            return pathfindAndMoveToSub.InvokeFunc(dest, false);
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] vnavmesh.SimpleMove.PathfindAndMoveTo error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 是否当前仍在 vnavmesh 的 SimpleMove 路径中。
    /// </summary>
    public bool IsMoving()
    {
        if (pathfindInProgressSub is null)
            return false;

        try
        {
            return pathfindInProgressSub.InvokeFunc();
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] vnavmesh.SimpleMove.PathfindInProgress error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 停止当前路径移动。
    /// </summary>
    public void Stop()
    {
        if (stopSub is null)
            return;

        try
        {
            stopSub.InvokeAction();
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] vnavmesh.Path.Stop error: {ex.Message}");
        }
    }
}
