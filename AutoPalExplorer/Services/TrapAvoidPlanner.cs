using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoPalExplorer.Services;

/// <summary>
/// 陷阱避障路径规划器：把 vnavmesh 已经算好的一串 waypoint 当成折线，
/// 在 XZ 平面上把「穿过陷阱危险圈」的那一段替换成「切线 + 圆弧」的绕行点，
/// 再交回 vnavmesh 走（vnavmesh.Path.MoveTo）。
///
/// 也就是说这里不做寻路，只做路径后处理：
///
///     Player --------xxxxxxxxx--------- Target          原始 vnav 路线穿圈
///                   xxx     xxx
///                  xx   陷阱  xx
///                   xxx     xxx
///                     xxxxx
///
///     Player --------\                                  绕行后
///                     *  *  *
///                   *         *
///                  *    陷阱   *
///                   *         *
///                     * * * *\
///                             \------- Target
///
/// 只用纯几何，不依赖任何游戏 / IPC 类型，方便单独推理与复用。
/// navmesh 可走性校验通过 <see cref="MeshProbe"/> 回调外接（拿不到就按原样用，见 <see cref="MeshCheck.Unknown"/>）。
/// </summary>
public static class TrapAvoidPlanner
{
    /// <summary>一个圆形危险区（XZ 平面）。</summary>
    public readonly struct DangerZone
    {
        public readonly Vector2 Center;
        public readonly float Radius;

        public DangerZone(Vector3 center, float radius)
        {
            Center = new Vector2(center.X, center.Z);
            Radius = MathF.Max(0.1f, radius);
        }
    }

    /// <summary>navmesh 校验结果。</summary>
    public enum MeshCheck
    {
        /// <summary>查不到（IPC 不可用 / 结果还没回来）：按原样用这个点。</summary>
        Unknown,

        /// <summary>可走，<c>projected</c> 是吸附到 navmesh 上之后的点。</summary>
        Walkable,

        /// <summary>不可走（落在墙里 / 悬崖外）：这个绕行方向作废。</summary>
        Blocked,
    }

    /// <summary>把一个算出来的绕行点投影回 navmesh 上；实现见 <see cref="VNavmeshClient.ProbeMesh"/>。</summary>
    public delegate MeshCheck MeshProbe(Vector3 point, out Vector3 projected);

    public enum PlanResult
    {
        /// <summary>原路线没有穿任何危险圈，不需要改。</summary>
        Clear,

        /// <summary>已经生成了绕行路线。</summary>
        Detoured,

        /// <summary>路线穿圈但绕不过去（两个方向都不可走 / 情况太复杂）：调用方保持原路线。</summary>
        Failed,
    }

    /// <summary>圆弧采样步长：约每 22.5° 一个 waypoint。</summary>
    private const float ArcStepRadians = MathF.PI / 8f;

    /// <summary>一段圆弧最多采样多少个点（整圈 2π / 22.5° = 16，留点余量）。</summary>
    private const int MaxArcSteps = 20;

    /// <summary>最多插入几段绕行（每段处理一个圈），防止怪路况下无限展开。</summary>
    private const int MaxIterations = 8;

    private const float Epsilon = 0.05f;

    /// <summary>
    /// 对 <paramref name="path"/> 做避障后处理。<paramref name="path"/> 第一个点应当是玩家当前位置。
    /// 返回 <see cref="PlanResult.Detoured"/> 时 <paramref name="result"/> 才是新路线（同样包含首点）。
    /// </summary>
    public static PlanResult Plan(
        IReadOnlyList<Vector3> path,
        IReadOnlyList<DangerZone> zones,
        MeshProbe? probe,
        out List<Vector3> result)
    {
        result = new List<Vector3>(path);

        if (path.Count < 2 || zones.Count == 0)
            return PlanResult.Clear;

        var changed = false;
        for (var iter = 0; iter < MaxIterations; iter++)
        {
            if (!FindFirstHit(result, zones, out var segIdx, out var zoneIdx))
                return changed ? PlanResult.Detoured : PlanResult.Clear;

            var detour = BuildBestDetour(result[segIdx], result[segIdx + 1], zones, zoneIdx, probe);
            if (detour is null)
                return PlanResult.Failed;

            result.InsertRange(segIdx + 1, detour);
            changed = true;
        }

        // 迭代次数用尽还没清干净：说明陷阱堆得太密，交回调用方按原路线处理
        return PlanResult.Failed;
    }

    /// <summary>路线是否还有任意一段穿过危险圈（外部想单独判断时用）。</summary>
    public static bool HitsAnyZone(IReadOnlyList<Vector3> path, IReadOnlyList<DangerZone> zones)
        => FindFirstHit(path, zones, out _, out _);

    /// <summary>
    /// 找到第一段穿圈的线段。
    ///
    /// 只有「两个端点都在圈外」才算穿圈，这条规则同时解决了三个问题：
    ///   · 玩家已经站在危险圈里时不会试图绕自己脚下的圈（也就不会来回抖）；
    ///   · 目标点本身就在圈里（例如狂暴盲踩模式故意去踩陷阱点）时不绕，尊重调用方的意图；
    ///   · 绕行时从圈内往外的那条径向腿不会被判成新的穿圈，避免无限重规划。
    /// </summary>
    private static bool FindFirstHit(
        IReadOnlyList<Vector3> path,
        IReadOnlyList<DangerZone> zones,
        out int segIdx,
        out int zoneIdx)
    {
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var a = ToXZ(path[i]);
            var b = ToXZ(path[i + 1]);

            var bestT = float.MaxValue;
            var best = -1;

            for (var z = 0; z < zones.Count; z++)
            {
                var zone = zones[z];
                var rSq = zone.Radius * zone.Radius;

                if ((a - zone.Center).LengthSquared() <= rSq)
                    continue;
                if ((b - zone.Center).LengthSquared() <= rSq)
                    continue;

                if (DistSqPointSegment(zone.Center, a, b, out var t) >= rSq)
                    continue;

                // 同一段上撞到多个圈时，先处理离起点最近的那个
                if (t < bestT)
                {
                    bestT = t;
                    best = z;
                }
            }

            if (best >= 0)
            {
                segIdx = i;
                zoneIdx = best;
                return true;
            }
        }

        segIdx = -1;
        zoneIdx = -1;
        return false;
    }

    /// <summary>顺时针 / 逆时针各绕一次，取「可走且更短」的那个方案。</summary>
    private static List<Vector3>? BuildBestDetour(
        Vector3 a,
        Vector3 b,
        IReadOnlyList<DangerZone> zones,
        int zoneIdx,
        MeshProbe? probe)
    {
        List<Vector3>? best = null;
        var bestLength = float.MaxValue;

        for (var dir = -1; dir <= 1; dir += 2)
        {
            var candidate = BuildDetour(a, b, zones[zoneIdx], dir, out var length);
            if (candidate is null || length >= bestLength)
                continue;

            if (!Accept(candidate, zones, zoneIdx, probe))
                continue;

            best = candidate;
            bestLength = length;
        }

        return best;
    }

    /// <summary>
    /// 生成绕过单个圈的「切点 + 圆弧」点列（不含 a / b 本身）。
    /// <paramref name="dir"/> = +1 表示绕行时极角递增，-1 表示递减。
    /// </summary>
    private static List<Vector3>? BuildDetour(Vector3 a3, Vector3 b3, DangerZone zone, int dir, out float length)
    {
        length = float.MaxValue;

        var a = ToXZ(a3);
        var b = ToXZ(b3);
        var c = zone.Center;

        // 圆弧要比危险半径大一圈：相邻采样点之间连的是弦，弦中点比采样半径更靠近圆心
        // （近似 r·cos(step/2)），不放大的话新路线自己又会被判成穿圈，从而无限重规划。
        var arcR = zone.Radius / MathF.Cos(ArcStepRadians * 0.5f) + Epsilon;

        var va = a - c;
        var vb = b - c;
        var da = va.Length();
        var db = vb.Length();

        // 端点在圈内的情况由 FindFirstHit 拦掉了，这里只做兜底
        if (da <= zone.Radius + 1e-3f || db <= zone.Radius + 1e-3f)
            return null;

        var thetaA = MathF.Atan2(va.Y, va.X);
        var thetaB = MathF.Atan2(vb.Y, vb.X);

        // 端点落在「危险半径之外、采样半径之内」时切线不存在，退化成径向出圈（alpha = 0）。
        // 这条径向腿离圆心最近也有 da > Radius，仍然是安全的。
        var alphaA = da > arcR ? MathF.Acos(Math.Clamp(arcR / da, -1f, 1f)) : 0f;
        var alphaB = db > arcR ? MathF.Acos(Math.Clamp(arcR / db, -1f, 1f)) : 0f;

        var depart = thetaA + dir * alphaA;
        var arrive = thetaB - dir * alphaB;
        var sweep = NormalizeSweep(arrive - depart, dir);

        var steps = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(sweep) / ArcStepRadians));
        if (steps > MaxArcSteps)
            return null;

        var pts = new List<Vector2>(steps + 1);
        for (var i = 0; i <= steps; i++)
        {
            var ang = depart + sweep * (i / (float)steps);
            pts.Add(c + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * arcR);
        }

        // 总长度（切线腿 + 圆弧腿 + 切线腿），用于在顺/逆两个方向里挑更短的
        length = (pts[0] - a).Length() + (b - pts[^1]).Length() + arcR * MathF.Abs(sweep);

        // Y 按沿路累计长度在 a.Y / b.Y 之间插值；navmesh 校验可用时会再被投影修正
        var cum = 0f;
        var prev = a;
        var spans = new float[pts.Count];
        for (var i = 0; i < pts.Count; i++)
        {
            cum += (pts[i] - prev).Length();
            spans[i] = cum;
            prev = pts[i];
        }

        var total = cum + (b - prev).Length();
        var result = new List<Vector3>(pts.Count);
        for (var i = 0; i < pts.Count; i++)
        {
            var t = total > 1e-4f ? spans[i] / total : 0f;
            result.Add(new Vector3(pts[i].X, a3.Y + (b3.Y - a3.Y) * t, pts[i].Y));
        }

        return result;
    }

    /// <summary>
    /// 校验一个绕行方案：不能有点落进别的危险圈，navmesh 说不可走也要否掉。
    /// 校验通过时顺便把点吸附到 navmesh 上（就地改写 <paramref name="pts"/>）。
    /// </summary>
    private static bool Accept(List<Vector3> pts, IReadOnlyList<DangerZone> zones, int selfIdx, MeshProbe? probe)
    {
        for (var i = 0; i < pts.Count; i++)
        {
            for (var z = 0; z < zones.Count; z++)
            {
                if (z == selfIdx)
                    continue;

                var zone = zones[z];
                if ((ToXZ(pts[i]) - zone.Center).LengthSquared() < zone.Radius * zone.Radius)
                    return false;
            }

            if (probe is null)
                continue;

            switch (probe(pts[i], out var projected))
            {
                case MeshCheck.Blocked:
                    return false;
                case MeshCheck.Walkable:
                    pts[i] = projected;
                    break;
            }
        }

        return true;
    }

    /// <summary>把角度差折算到指定绕行方向上：dir=+1 落在 [0, 2π)，dir=-1 落在 (-2π, 0]。</summary>
    private static float NormalizeSweep(float delta, int dir)
    {
        const float twoPi = MathF.PI * 2f;

        if (dir > 0)
        {
            while (delta < 0f) delta += twoPi;
            while (delta >= twoPi) delta -= twoPi;
        }
        else
        {
            while (delta > 0f) delta -= twoPi;
            while (delta <= -twoPi) delta += twoPi;
        }

        return delta;
    }

    private static float DistSqPointSegment(Vector2 p, Vector2 a, Vector2 b, out float t)
    {
        var ab = b - a;
        var lenSq = ab.LengthSquared();
        t = lenSq < 1e-6f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
        return (p - (a + ab * t)).LengthSquared();
    }

    private static Vector2 ToXZ(Vector3 v) => new(v.X, v.Z);
}
