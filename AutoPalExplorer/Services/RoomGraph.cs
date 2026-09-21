using System;

using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

using RoomFlags = FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentDeepDungeon.RoomFlags;

namespace AutoPalExplorer.Services;

/// <summary>
/// 深层迷宫房间图。
///
/// 直接读游戏内 <see cref="InstanceContentDeepDungeon"/> 的 MapData（5x5 = 25 格），
/// 每格是一组 <see cref="RoomFlags"/>：四个方向的连通位 + Return / Passage / Home / Revealed。
/// 有了它就不用再靠“贴墙乱走”猜地形——可以直接知道：
///   · 这一层实际存在哪些房间（有连通位的格子）；
///   · 哪些房间已经踩过（Revealed）；
///   · 传送装置（Passage）/ 出生点（Home）在哪一格；
///   · 任意两个房间之间的最短路（BFS）。
///
/// 另外 InstanceContentDeepDungeon.Party 里游戏直接给了每个队员所在的 RoomIndex，
/// 所以“我在哪个房间”是游戏告诉我们的真值，不需要自己按坐标猜。
/// </summary>
public static unsafe class RoomGraph
{
    public const int MaxRooms = 25;
    public const int GridSize = 5;

    /// <summary>四个方向的连通位掩码。某格只要有任意一个连通位就说明这一层确实有这个房间。</summary>
    public const RoomFlags ConnectionMask =
        RoomFlags.ConnectionN | RoomFlags.ConnectionS | RoomFlags.ConnectionW | RoomFlags.ConnectionE;

    public static InstanceContentDeepDungeon* GetDeepDungeon()
    {
        var ef = EventFramework.Instance();
        if (ef == null)
            return null;

        return ef->GetInstanceContentDeepDungeon();
    }

    /// <summary>
    /// 传送装置（下一层入口）的激活进度，直接读游戏内 InstanceContentDeepDungeon.PassageProgress。
    ///
    /// 这是不依赖地图 UI 的实时真值：地图上那个传送装置图标的 PartId 就是按这个值画出来的，
    /// 所以“没有地图 UI 的层”也能靠它判断传送装置到底激活没有。
    /// 不在深层迷宫里（或数据还没刷出来）时返回 false。
    /// </summary>
    public static bool TryGetPassageProgress(out int progress)
    {
        progress = -1;

        var dd = GetDeepDungeon();
        if (dd == null)
            return false;

        progress = dd->PassageProgress;
        return true;
    }

    /// <summary>游戏内当前层数。切图 / 读条中拿不到时返回 false。</summary>
    public static bool TryGetFloor(out int floor)
    {
        floor = 0;

        var dd = GetDeepDungeon();
        if (dd == null)
            return false;

        floor = dd->Floor;
        return floor > 0;
    }

    public static RoomFlags GetFlags(InstanceContentDeepDungeon* dd, int roomIndex)
    {
        if (dd == null || (uint)roomIndex >= MaxRooms)
            return RoomFlags.None;

        return dd->MapData[roomIndex];
    }

    /// <summary>这一层的布局里是否真的存在这个房间。</summary>
    public static bool RoomExists(InstanceContentDeepDungeon* dd, int roomIndex)
        => (GetFlags(dd, roomIndex) & ConnectionMask) != 0;

    /// <summary>房间是否已被玩家踩过（游戏自己的“已探索”标记）。</summary>
    public static bool IsRevealed(InstanceContentDeepDungeon* dd, int roomIndex)
        => (GetFlags(dd, roomIndex) & RoomFlags.Revealed) != 0;

    public static int GetHomeRoomIndex(InstanceContentDeepDungeon* dd)
        => FindFirstWithFlag(dd, RoomFlags.Home);

    /// <summary>传送装置（下一层入口）所在房间。</summary>
    public static int GetPassageRoomIndex(InstanceContentDeepDungeon* dd)
        => FindFirstWithFlag(dd, RoomFlags.Passage);

    /// <summary>返回装置（回地面）所在房间。</summary>
    public static int GetReturnRoomIndex(InstanceContentDeepDungeon* dd)
        => FindFirstWithFlag(dd, RoomFlags.Return);

    private static int FindFirstWithFlag(InstanceContentDeepDungeon* dd, RoomFlags flag)
    {
        if (dd == null)
            return -1;

        var map = dd->MapData;
        for (var i = 0; i < MaxRooms && i < map.Length; i++)
        {
            if ((map[i] & flag) != 0)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// 游戏直接给出的“本地玩家所在房间”。找不到（不在本内容 / 数据还没刷）时返回 -1。
    /// </summary>
    public static int GetLocalPlayerRoomIndex(InstanceContentDeepDungeon* dd, uint localEntityId)
    {
        if (dd == null || localEntityId == 0)
            return -1;

        var party = dd->Party;
        for (var i = 0; i < party.Length; i++)
        {
            if (party[i].EntityId != localEntityId)
                continue;

            int room = party[i].RoomIndex;
            return (uint)room < MaxRooms ? room : -1;
        }

        return -1;
    }

    /// <summary>
    /// 枚举某房间实际连通的邻居。两边都要有对应方向的连通位才算连通，
    /// 避免只有单侧标记时走出一条不存在的路。
    /// </summary>
    public static void EnumerateNeighbors(InstanceContentDeepDungeon* dd, int roomIndex, Span<int> neighbors, out int count)
    {
        count = 0;
        if (dd == null || (uint)roomIndex >= MaxRooms)
            return;

        var map = dd->MapData;
        var row = roomIndex / GridSize;
        var col = roomIndex % GridSize;
        var flags = map[roomIndex];

        if ((flags & RoomFlags.ConnectionN) != 0 && row > 0)
        {
            var n = roomIndex - GridSize;
            if ((map[n] & RoomFlags.ConnectionS) != 0 && count < neighbors.Length)
                neighbors[count++] = n;
        }

        if ((flags & RoomFlags.ConnectionS) != 0 && row < GridSize - 1)
        {
            var s = roomIndex + GridSize;
            if ((map[s] & RoomFlags.ConnectionN) != 0 && count < neighbors.Length)
                neighbors[count++] = s;
        }

        if ((flags & RoomFlags.ConnectionW) != 0 && col > 0)
        {
            var w = roomIndex - 1;
            if ((map[w] & RoomFlags.ConnectionE) != 0 && count < neighbors.Length)
                neighbors[count++] = w;
        }

        if ((flags & RoomFlags.ConnectionE) != 0 && col < GridSize - 1)
        {
            var e = roomIndex + 1;
            if ((map[e] & RoomFlags.ConnectionW) != 0 && count < neighbors.Length)
                neighbors[count++] = e;
        }
    }

    /// <summary>
    /// 从 start 出发做 BFS。<paramref name="dist"/> 写入房间步数（不可达为 -1），
    /// <paramref name="parent"/> 写入最短路上的前驱（无为 -1）。两个 Span 至少 25 长。
    /// <paramref name="blockedMask"/> 里置位的房间会被当作不存在（用于拉黑走不进去的房间）。
    /// </summary>
    public static void Bfs(InstanceContentDeepDungeon* dd, int start, int blockedMask, Span<int> dist, Span<int> parent)
    {
        for (var i = 0; i < MaxRooms; i++)
        {
            dist[i] = -1;
            parent[i] = -1;
        }

        if (dd == null || (uint)start >= MaxRooms)
            return;

        Span<int> queue = stackalloc int[MaxRooms];
        Span<int> neighbors = stackalloc int[4];

        var head = 0;
        var tail = 0;
        dist[start] = 0;
        queue[tail++] = start;

        while (head < tail)
        {
            var cur = queue[head++];
            EnumerateNeighbors(dd, cur, neighbors, out var count);

            for (var i = 0; i < count; i++)
            {
                var next = neighbors[i];
                if (dist[next] >= 0)
                    continue;

                if ((blockedMask & (1 << next)) != 0)
                    continue;

                dist[next] = dist[cur] + 1;
                parent[next] = cur;
                queue[tail++] = next;
            }
        }
    }

    /// <summary>
    /// 沿 BFS 结果回溯出从 from 走向 to 的第一步房间。
    /// from == to 时返回 to；不可达返回 false。
    /// </summary>
    public static bool TryGetFirstHop(ReadOnlySpan<int> dist, ReadOnlySpan<int> parent, int from, int to, out int hop)
    {
        hop = -1;
        if ((uint)from >= MaxRooms || (uint)to >= MaxRooms)
            return false;

        if (from == to)
        {
            hop = to;
            return true;
        }

        if (dist[to] < 0)
            return false;

        var cur = to;
        // 回溯到 from 的直接后继；步数有限（最多 25），不会死循环。
        for (var guard = 0; guard < MaxRooms; guard++)
        {
            var prev = parent[cur];
            if (prev < 0)
                return false;

            if (prev == from)
            {
                hop = cur;
                return true;
            }

            cur = prev;
        }

        return false;
    }

    /// <summary>这一层布局里实际存在的房间数。</summary>
    public static int CountExistingRooms(InstanceContentDeepDungeon* dd)
    {
        var n = 0;
        for (var i = 0; i < MaxRooms; i++)
        {
            if (RoomExists(dd, i))
                n++;
        }

        return n;
    }

    public static int CountRevealedRooms(InstanceContentDeepDungeon* dd)
    {
        var n = 0;
        for (var i = 0; i < MaxRooms; i++)
        {
            if (RoomExists(dd, i) && IsRevealed(dd, i))
                n++;
        }

        return n;
    }

    /// <summary>
    /// 游戏记录的本层宝箱所在房间（bit i = 房间 i 有宝箱）。
    /// 只作为探索排序的加分项使用：即使游戏只记录已发现的宝箱也不会走错。
    /// </summary>
    public static int GetChestRoomMask(InstanceContentDeepDungeon* dd)
    {
        if (dd == null)
            return 0;

        var mask = 0;
        var chests = dd->Chests;
        for (var i = 0; i < chests.Length; i++)
        {
            var info = chests[i];
            if (info.ChestType == 0)
                continue;

            int room = info.RoomIndex;
            if ((uint)room < MaxRooms)
                mask |= 1 << room;
        }

        return mask;
    }
}
