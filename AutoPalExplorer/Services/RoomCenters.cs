using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoPalExplorer.Services;

/// <summary>
/// 房间中心标定。
///
/// 房间图（<see cref="RoomGraph"/>）只给出“第几格”，导航需要的是世界坐标，
/// 所以要把 5x5 的格子对上世界坐标。深层迷宫每层的房间是等间距网格摆放的：
///
///     center(room) = Origin + (col * PitchX, row * PitchZ)      col = idx % 5, row = idx / 5
///
/// 标定完全在线完成，不依赖任何写死的坐标表：
///   · 游戏在 InstanceContentDeepDungeon.Party 里直接告诉我们玩家当前在哪一格；
///   · 于是每一帧都能拿到一组真值样本 (roomIndex, 玩家世界坐标)；
///   · 对这些样本按列做 x 的最小二乘、按行做 z 的最小二乘，就同时解出 Origin 和 Pitch。
///
/// 只走过一个房间时列/行都只有一个取值，解不出斜率，这时用上一次学到的 Pitch
/// （按 Territory 记忆，见 Configuration.RoomGridPitchX / RoomGridPitchZ）先把 Origin 定下来，
/// 走到第二个不同列 / 行的房间后自动收敛到本层真实值。
/// </summary>
public sealed class RoomCenters
{
    /// <summary>没有任何先验时的房间间距（米）。朝圣路实测在 64 附近，走两个房间后会被真实值覆盖。</summary>
    public const float FallbackPitch = 64f;

    private const float MinPitch = 20f;
    private const float MaxPitch = 200f;

    // 每个房间记玩家走过位置的包围盒。
    // 用包围盒中点而不是平均值：玩家横穿一个房间只要一次，中点就已经很接近真实房间中心；
    // 平均值则会被“在门口来回蹭”这种停留严重带偏。
    private readonly float[] minX = new float[RoomGraph.MaxRooms];
    private readonly float[] maxX = new float[RoomGraph.MaxRooms];
    private readonly float[] minZ = new float[RoomGraph.MaxRooms];
    private readonly float[] maxZ = new float[RoomGraph.MaxRooms];
    private readonly float[] firstX = new float[RoomGraph.MaxRooms];
    private readonly float[] firstZ = new float[RoomGraph.MaxRooms];
    private readonly float[] sumY = new float[RoomGraph.MaxRooms];
    private readonly int[] ySamples = new int[RoomGraph.MaxRooms];
    private readonly int[] samples = new int[RoomGraph.MaxRooms];

    private float originX;
    private float originZ;
    private float originY;
    private float pitchX = FallbackPitch;
    private float pitchZ = FallbackPitch;

    private bool solved;
    private bool pitchXSolved;
    private bool pitchZSolved;

    /// <summary>是否已经能给出房间中心坐标。</summary>
    public bool IsCalibrated => solved;

    /// <summary>本层已经采到样本的房间数（越多标定越准）。</summary>
    public int ObservedRoomCount { get; private set; }

    /// <summary>X / Z 两个方向的房间间距都是从本层真实样本解出来的（而不是沿用先验）。</summary>
    public bool PitchFullySolved => pitchXSolved && pitchZSolved;

    public float PitchX => pitchX;
    public float PitchZ => pitchZ;

    /// <summary>用于判定“这个坐标属于哪个房间”的半径，取间距的一半略收一点。</summary>
    public float RoomRadius => MathF.Min(pitchX, pitchZ) * 0.5f;

    /// <summary>换层 / 换布局时调用：本层的标定全部作废，但保留 Pitch 作为下一层的先验。</summary>
    public void ResetFloor()
    {
        Array.Clear(minX);
        Array.Clear(maxX);
        Array.Clear(minZ);
        Array.Clear(maxZ);
        Array.Clear(firstX);
        Array.Clear(firstZ);
        Array.Clear(sumY);
        Array.Clear(ySamples);
        Array.Clear(samples);
        ObservedRoomCount = 0;
        solved = false;
        pitchXSolved = false;
        pitchZSolved = false;
    }

    /// <summary>设置 Pitch 先验（从配置里记住的上一次结果恢复）。</summary>
    public void SeedPitch(float seedX, float seedZ)
    {
        if (IsSanePitch(seedX))
            pitchX = seedX;

        if (IsSanePitch(seedZ))
            pitchZ = seedZ;
    }

    /// <summary>
    /// 喂入一组真值样本：玩家此刻在 <paramref name="roomIndex"/> 号房间，世界坐标是 <paramref name="pos"/>。
    /// 每帧调用即可，内部按房间累积包围盒，不会因为在某个房间某处待久了就把结果压偏。
    /// </summary>
    public void Observe(int roomIndex, Vector3 pos)
    {
        if ((uint)roomIndex >= RoomGraph.MaxRooms)
            return;

        if (samples[roomIndex] == 0)
        {
            ObservedRoomCount++;
            firstX[roomIndex] = pos.X;
            firstZ[roomIndex] = pos.Z;
            minX[roomIndex] = maxX[roomIndex] = pos.X;
            minZ[roomIndex] = maxZ[roomIndex] = pos.Z;
        }
        else
        {
            // 离该房间第一个样本太远的点多半是刚换房间时游戏还没刷新的脏数据，丢掉，
            // 否则一个离群点就能把包围盒撑到隔壁房间去。
            var outlierLimit = MathF.Max(pitchX, pitchZ) * 1.5f;
            if (MathF.Abs(pos.X - firstX[roomIndex]) > outlierLimit ||
                MathF.Abs(pos.Z - firstZ[roomIndex]) > outlierLimit)
            {
                return;
            }

            minX[roomIndex] = MathF.Min(minX[roomIndex], pos.X);
            maxX[roomIndex] = MathF.Max(maxX[roomIndex], pos.X);
            minZ[roomIndex] = MathF.Min(minZ[roomIndex], pos.Z);
            maxZ[roomIndex] = MathF.Max(maxZ[roomIndex], pos.Z);
        }

        // Y 只用来给导航点一个大致高度，取均值即可；累计到上限后就不再变，避免长时间挂机丢精度。
        const int yCap = 600;
        if (ySamples[roomIndex] < yCap)
        {
            sumY[roomIndex] += pos.Y;
            ySamples[roomIndex]++;
        }

        samples[roomIndex]++;

        Solve();
    }

    /// <summary>房间中心的世界坐标。未标定时返回 false。</summary>
    public bool TryGetCenter(int roomIndex, out Vector3 center)
    {
        center = default;
        if (!solved || (uint)roomIndex >= RoomGraph.MaxRooms)
            return false;

        var col = roomIndex % RoomGraph.GridSize;
        var row = roomIndex / RoomGraph.GridSize;
        center = new Vector3(originX + col * pitchX, originY, originZ + row * pitchZ);
        return true;
    }

    /// <summary>
    /// 反查：这个世界坐标落在哪个房间格。超出 5x5 网格范围（例如另一份镜像布局的坐标）返回 false。
    /// </summary>
    public bool TryGetRoomAt(Vector3 pos, out int roomIndex)
    {
        roomIndex = -1;
        if (!solved)
            return false;

        var col = (int)MathF.Round((pos.X - originX) / pitchX);
        var row = (int)MathF.Round((pos.Z - originZ) / pitchZ);

        if ((uint)col >= RoomGraph.GridSize || (uint)row >= RoomGraph.GridSize)
            return false;

        roomIndex = row * RoomGraph.GridSize + col;
        return true;
    }

    /// <summary>
    /// 按当前样本重新解一遍 Origin / Pitch。
    /// x 只跟 col 有关、z 只跟 row 有关，所以拆成两个一元线性回归。
    /// </summary>
    private void Solve()
    {
        Span<float> midX = stackalloc float[RoomGraph.MaxRooms];
        Span<float> midZ = stackalloc float[RoomGraph.MaxRooms];

        var ySum = 0f;
        var yCount = 0;

        for (var i = 0; i < RoomGraph.MaxRooms; i++)
        {
            if (samples[i] == 0)
                continue;

            midX[i] = (minX[i] + maxX[i]) * 0.5f;
            midZ[i] = (minZ[i] + maxZ[i]) * 0.5f;

            if (ySamples[i] > 0)
            {
                ySum += sumY[i] / ySamples[i];
                yCount++;
            }
        }

        if (yCount == 0)
            return;

        originY = ySum / yCount;

        pitchXSolved = TryFitAxis(midX, axisIsColumn: true, ref pitchX, out originX);
        pitchZSolved = TryFitAxis(midZ, axisIsColumn: false, ref pitchZ, out originZ);
        solved = true;
    }

    /// <summary>
    /// 对一个轴做最小二乘：value ≈ origin + pitch * index。
    /// 只有一个 index 取值时解不出斜率，此时沿用现有 pitch 只解截距，返回 false。
    /// </summary>
    private bool TryFitAxis(ReadOnlySpan<float> roomCentersOnAxis, bool axisIsColumn, ref float pitch, out float origin)
    {
        // 同一列（或同一行）里可能有多个房间，先按 index 聚合成一个点，
        // 免得某一列房间多就把回归拉过去。
        Span<float> bucketSum = stackalloc float[RoomGraph.GridSize];
        Span<int> bucketCount = stackalloc int[RoomGraph.GridSize];

        for (var i = 0; i < RoomGraph.MaxRooms; i++)
        {
            if (samples[i] == 0)
                continue;

            var idx = axisIsColumn ? i % RoomGraph.GridSize : i / RoomGraph.GridSize;
            bucketSum[idx] += roomCentersOnAxis[i];
            bucketCount[idx]++;
        }

        var n = 0;
        var sumIdx = 0f;
        var sumVal = 0f;
        for (var k = 0; k < RoomGraph.GridSize; k++)
        {
            if (bucketCount[k] == 0)
                continue;

            n++;
            sumIdx += k;
            sumVal += bucketSum[k] / bucketCount[k];
        }

        var meanIdx = sumIdx / n;
        var meanVal = sumVal / n;

        if (n >= 2)
        {
            var num = 0f;
            var den = 0f;
            for (var k = 0; k < RoomGraph.GridSize; k++)
            {
                if (bucketCount[k] == 0)
                    continue;

                var dv = (bucketSum[k] / bucketCount[k]) - meanVal;
                var di = k - meanIdx;
                num += di * dv;
                den += di * di;
            }

            if (den > 0.0001f)
            {
                var slope = num / den;
                // 只接受量级合理的斜率；异常值（例如刚换层时的脏数据）沿用先验。
                if (IsSanePitch(MathF.Abs(slope)) && slope > 0f)
                {
                    pitch = slope;
                    origin = meanVal - pitch * meanIdx;
                    return true;
                }
            }
        }

        origin = meanVal - pitch * meanIdx;
        return false;
    }

    private static bool IsSanePitch(float value)
        => value >= MinPitch && value <= MaxPitch && !float.IsNaN(value) && !float.IsInfinity(value);

    /// <summary>
    /// 把当前层所有 PalacePal 点位按房间分桶，用于“按房间顺序盲踩”。
    /// 落在网格外的点（另一份镜像布局）会被自然排除。
    /// </summary>
    public void GroupByRoom(IReadOnlyList<Vector3> points, Dictionary<int, List<Vector3>> result)
    {
        result.Clear();
        if (!solved)
            return;

        for (var i = 0; i < points.Count; i++)
        {
            if (!TryGetRoomAt(points[i], out var room))
                continue;

            if (!result.TryGetValue(room, out var list))
            {
                list = new List<Vector3>();
                result[room] = list;
            }

            list.Add(points[i]);
        }
    }
}
