using System;

namespace AutoPalExplorer.Services;

/// <summary>本层探索的目标类型。</summary>
public enum RoomExploreGoal
{
    /// <summary>没有可去的地方（房间图不可用 / 全部走完且已在传送装置房间）。</summary>
    None,

    /// <summary>还有没踩过的房间，去把它踩掉。</summary>
    Unrevealed,

    /// <summary>全层已踩完，去传送装置所在的房间等 / 打。</summary>
    Passage,

    /// <summary>用户在房间地图上手动点的目标，优先于自动选择。</summary>
    Manual
}

/// <summary>一层地图的位掩码快照。全部是纯数据，方便脱离游戏单独推演。</summary>
public readonly record struct RoomFloorSnapshot(
    int PlayerRoom,
    int PassageRoom,
    int ExistsMask,
    int RevealedMask,
    int ChestMask,
    int BlockedMask);

public readonly record struct RoomExplorePlan(RoomExploreGoal Goal, int TargetRoom)
{
    public static readonly RoomExplorePlan None = new(RoomExploreGoal.None, -1);
}

/// <summary>
/// 选下一个要去的房间。
///
/// 刻意写成不依赖游戏结构的纯函数：输入是位掩码快照 + 一份 BFS 步数表，输出是目标房间。
/// 这样“该往哪走”这件事可以脱离游戏单独验证，也不会和每帧的导航 / 交互逻辑纠缠在一起。
///
/// 排序规则：先按 BFS 步数近的优先；步数相同时，游戏记录里有宝箱的房间优先；
/// 再相同就按房间号，保证同样局面下每帧给出同一个答案，不会左右横跳。
/// </summary>
public static class RoomExplorePlanner
{
    /// <summary>步数相同时，有宝箱的房间相当于近这么多步。</summary>
    private const int ChestBonus = 1;

    public static RoomExplorePlan SelectTarget(in RoomFloorSnapshot snapshot, ReadOnlySpan<int> dist)
    {
        var player = snapshot.PlayerRoom;
        if ((uint)player >= RoomGraph.MaxRooms)
            return RoomExplorePlan.None;

        var bestRoom = -1;
        var bestScore = int.MaxValue;

        for (var room = 0; room < RoomGraph.MaxRooms; room++)
        {
            if ((snapshot.ExistsMask & (1 << room)) == 0)
                continue;

            if ((snapshot.RevealedMask & (1 << room)) != 0)
                continue;

            if ((snapshot.BlockedMask & (1 << room)) != 0)
                continue;

            var d = dist[room];
            if (d < 0)
                continue;

            var score = d * 4;
            if ((snapshot.ChestMask & (1 << room)) != 0)
                score -= ChestBonus * 4;

            if (score < bestScore)
            {
                bestScore = score;
                bestRoom = room;
            }
        }

        if (bestRoom >= 0)
            return new RoomExplorePlan(RoomExploreGoal.Unrevealed, bestRoom);

        // 全部踩完了：往传送装置所在房间靠，省得在原地贴墙乱转。
        var passage = snapshot.PassageRoom;
        if ((uint)passage < RoomGraph.MaxRooms &&
            passage != player &&
            (snapshot.BlockedMask & (1 << passage)) == 0 &&
            dist[passage] >= 0)
        {
            return new RoomExplorePlan(RoomExploreGoal.Passage, passage);
        }

        return RoomExplorePlan.None;
    }
}
