using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
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
///
/// 陷阱避障额外用到的几个（都是同步的，国服 IPC 层也能正常 marshal）：
/// [EzIPC("Path.%m")]       Func&lt;List&lt;Vector3&gt;&gt;              ListWaypoints
/// [EzIPC("Path.%m")]       Action&lt;List&lt;Vector3&gt;, bool&gt;      MoveTo
///
/// 另外 vnavmesh.Query.Mesh.NearestPoint 返回的是 Task&lt;Vector3?&gt;，
/// 国服 Dalamud 的 IPC 层不一定能正确 marshal 这种返回值，所以这里只当作
/// 「有就用、没有就算」的可选校验：见 <see cref="ProbeMesh"/>，全程不阻塞主线程。
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

    /// <summary>vnavmesh.Path.ListWaypoints() -> List&lt;Vector3&gt;</summary>
    private readonly ICallGateSubscriber<List<Vector3>>? listWaypointsSub;

    /// <summary>vnavmesh.Path.MoveTo(waypoints: List&lt;Vector3&gt;, fly: bool) -> void</summary>
    private readonly ICallGateSubscriber<List<Vector3>, bool, object>? moveToSub;

    /// <summary>vnavmesh.Query.Mesh.NearestPoint(p, halfExtentXZ, halfExtentY) -> Task&lt;Vector3?&gt;</summary>
    private readonly ICallGateSubscriber<Vector3, float, float, Task<Vector3?>>? nearestPointSub;

    // ==== navmesh 投影缓存（异步填充，主线程只读缓存，永不阻塞）====
    // value = null 表示「查过了，这个点不可走」。
    private readonly ConcurrentDictionary<long, Vector3?> meshCache = new();
    private readonly ConcurrentDictionary<long, byte> meshPending = new();
    private volatile bool meshQueryDisabled;

    // ListWaypoints / MoveTo 一旦抛异常（比如对面版本签名对不上），就整套关掉不再重试，
    // 免得每次重新规划都刷一条警告。关掉之后避障静默失效，导航退回 vnav 原生行为。
    private volatile bool pathRewriteDisabled;

    /// <summary>投影点离原点超过这个距离就认为原点根本不在可走面上。</summary>
    private const float MaxProjectionShift = 2.0f;

    /// <summary>缓存量化精度（米）：同一个 0.5 米格子里的点共用一次查询结果。</summary>
    private const float MeshCacheQuantum = 0.5f;

    private const float NearestPointHalfExtentXZ = 4f;
    private const float NearestPointHalfExtentY = 8f;

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

        try
        {
            // Func<List<Vector3>> => ICallGateSubscriber<List<Vector3>>
            listWaypointsSub = pi.GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints");
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] Failed to hook vnavmesh.Path.ListWaypoints: {ex.Message}");
        }

        try
        {
            // Action<List<Vector3>, bool> => TRet=object 的订阅者，用 InvokeAction() 调用
            moveToSub = pi.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] Failed to hook vnavmesh.Path.MoveTo: {ex.Message}");
        }

        try
        {
            nearestPointSub =
                pi.GetIpcSubscriber<Vector3, float, float, Task<Vector3?>>("vnavmesh.Query.Mesh.NearestPoint");
        }
        catch (Exception ex)
        {
            // 拿不到就算了：避障会退化成纯几何绕行（外加卡住兜底），不影响主流程
            log.Warning($"[AutoPalExplorer] Failed to hook vnavmesh.Query.Mesh.NearestPoint: {ex.Message}");
        }
    }

    /// <summary>路径级避障需要的两个 IPC（ListWaypoints / MoveTo）都在、且没被用废时才可用。</summary>
    public bool SupportsPathRewrite
        => listWaypointsSub is not null && moveToSub is not null && !pathRewriteDisabled;

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

    /// <summary>
    /// 读出 vnavmesh 当前正在走的 waypoint 列表（不含玩家自身位置）。
    /// 没有路径 / IPC 不可用时返回空列表。
    /// </summary>
    public List<Vector3> ListWaypoints()
    {
        if (listWaypointsSub is null)
            return new List<Vector3>();

        try
        {
            return listWaypointsSub.InvokeFunc() ?? new List<Vector3>();
        }
        catch (Exception ex)
        {
            DisablePathRewrite($"ListWaypoints: {ex.Message}");
            return new List<Vector3>();
        }
    }

    /// <summary>
    /// 直接把一串 waypoint 塞给 vnavmesh 去走（替换当前路径，不再重新寻路）。
    /// 陷阱避障就是靠它把「原路线 + 绕行圆弧」的新点列交回去执行。
    /// </summary>
    public bool MoveTo(List<Vector3> waypoints, bool fly = false)
    {
        if (moveToSub is null || waypoints.Count == 0)
            return false;

        try
        {
            moveToSub.InvokeAction(waypoints, fly);
            return true;
        }
        catch (Exception ex)
        {
            DisablePathRewrite($"MoveTo: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 查一个点在 navmesh 上的最近可走位置。
    ///
    /// vnavmesh 这个查询返回 Task，国服 IPC 层未必能 marshal，而且就算能，
    /// 在主线程上等它也会掉帧。所以这里做成「纯查缓存 + 后台补」：
    ///   · 缓存里有 -> 直接给结果（Walkable / Blocked）；
    ///   · 缓存里没有 -> 立刻返回 Unknown 并在后台发起一次查询，
    ///     等下一轮重新规划（默认 1.25s 后）时就能用上真实结果。
    /// 查询一次抛异常就永久关掉，之后全部走 Unknown，避障退化成纯几何绕行。
    /// </summary>
    public TrapAvoidPlanner.MeshCheck ProbeMesh(Vector3 point, out Vector3 projected)
    {
        projected = point;

        if (nearestPointSub is null || meshQueryDisabled)
            return TrapAvoidPlanner.MeshCheck.Unknown;

        var key = Quantize(point);
        if (meshCache.TryGetValue(key, out var cached))
        {
            if (cached is not { } p)
                return TrapAvoidPlanner.MeshCheck.Blocked;

            projected = p;
            return TrapAvoidPlanner.MeshCheck.Walkable;
        }

        RequestMeshPoint(key, point);
        return TrapAvoidPlanner.MeshCheck.Unknown;
    }

    /// <summary>换层时调用：上一层的 navmesh 投影结果全部作废。</summary>
    public void ClearMeshCache()
    {
        meshCache.Clear();
        meshPending.Clear();
    }

    private void RequestMeshPoint(long key, Vector3 point)
    {
        // 同一个格子只发一次请求
        if (!meshPending.TryAdd(key, 0))
            return;

        try
        {
            var task = nearestPointSub!.InvokeFunc(point, NearestPointHalfExtentXZ, NearestPointHalfExtentY);
            if (task is null)
            {
                DisableMeshQuery("NearestPoint 返回了 null Task");
                return;
            }

            task.ContinueWith(t =>
            {
                try
                {
                    Vector3? value = null;
                    if (t.Status == TaskStatus.RanToCompletion && t.Result is { } p)
                    {
                        // 投影挪得太远说明原点根本不在可走面附近，判为不可走
                        var dx = p.X - point.X;
                        var dz = p.Z - point.Z;
                        if (dx * dx + dz * dz <= MaxProjectionShift * MaxProjectionShift)
                            value = p;
                    }

                    meshCache[key] = value;
                }
                finally
                {
                    meshPending.TryRemove(key, out _);
                }
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            meshPending.TryRemove(key, out _);
            DisableMeshQuery(ex.Message);
        }
    }

    private void DisablePathRewrite(string reason)
    {
        if (pathRewriteDisabled)
            return;

        pathRewriteDisabled = true;
        log.Warning($"[AutoPalExplorer] vnavmesh 路径改写 IPC 不可用（{reason}），已关闭路径级避陷阱。");
    }

    private void DisableMeshQuery(string reason)
    {
        if (meshQueryDisabled)
            return;

        meshQueryDisabled = true;
        log.Warning($"[AutoPalExplorer] vnavmesh.Query.Mesh.NearestPoint 不可用（{reason}），陷阱避障改用纯几何绕行。");
    }

    /// <summary>把坐标量化成缓存 key（0.5 米一格）。</summary>
    private static long Quantize(Vector3 p)
    {
        var qx = (long)MathF.Round(p.X / MeshCacheQuantum);
        var qy = (long)MathF.Round(p.Y / MeshCacheQuantum);
        var qz = (long)MathF.Round(p.Z / MeshCacheQuantum);
        return ((qx & 0x1FFFFF) << 42) | ((qy & 0x1FFFFF) << 21) | (qz & 0x1FFFFF);
    }
}
