using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Game.ClientState.Objects.SubKinds;

namespace AutoPalExplorer.Services;

public sealed partial class AutoPalController
{
    // ==== 房间图状态 ====
    private readonly RoomCenters roomCenters = new();

    /// <summary>游戏直接给出的“玩家当前所在房间”，-1 表示拿不到。</summary>
    private int currentRoomIndex = -1;

    /// <summary>正在前往的最终目标房间；-1 表示没有。</summary>
    private int roomExploreTarget = -1;

    /// <summary>用户在房间地图上手动点的目标房间；走到 / 不可达就自动作废。-1 表示没有。</summary>
    private int roomManualTarget = -1;

    /// <summary>本帧要走的下一跳房间（目标房间的最短路第一步）。</summary>
    private int roomExploreHop = -1;

    private RoomExploreGoal roomExploreGoal = RoomExploreGoal.None;

    /// <summary>走不进去的房间（拉黑到换层为止），按位存。</summary>
    private int blockedRoomMask;

    /// <summary>用来判断“换层 / 换布局”的标识：territory | floor | layout。</summary>
    private long roomFloorStamp = -1;

    /// <summary>当前这份标定属于哪个 Territory（回写学到的房间间距时用）。</summary>
    private uint roomPitchTerritory;

    /// <summary>“读不到玩家房间号”只提示一次，避免刷屏。</summary>
    private bool roomIndexMissWarned;

    private DateTime lastRoomTickAt = DateTime.MinValue;
    private double roomHopActiveSeconds;

    /// <summary>已经给 Navigator 下过指令的那一跳；用它判断要不要重发，避免每帧重算路径。</summary>
    private int roomNavIssuedHop = -1;

    /// <summary>房间导航重发节流：房间中心可能落在墙里导致寻路失败，不能每帧硬怼 vnavmesh。</summary>
    private DateTime nextRoomNavIssueAt = DateTime.MinValue;

    /// <summary>PalacePal 点位按房间分桶的结果，用于按房间顺序盲踩。</summary>
    private readonly Dictionary<int, List<Vector3>> blindByRoom = new();
    private bool blindByRoomDirty = true;
    private int blindByRoomSourceCount = -1;
    private bool blindByRoomUsedTrapSet;

    // 房间探索：卡在同一个房间超过这个时间（只统计真正在跑探索的帧）就拉黑下一跳
    private const double RoomHopStuckSeconds = 25.0;

    // 分桶用的 PalacePal 点位数量上限保护（防止异常数据把每帧开销拉高）
    private const int MaxBlindGroupPoints = 20000;

    public int CurrentRoomIndex => currentRoomIndex;
    public int RoomExploreTarget => roomExploreTarget;
    public RoomExploreGoal RoomGoal => roomExploreGoal;
    public bool RoomCentersCalibrated => roomCenters.IsCalibrated;
    public bool RoomPitchSolved => roomCenters.PitchFullySolved;
    public float RoomPitchX => roomCenters.PitchX;
    public float RoomPitchZ => roomCenters.PitchZ;
    public int RoomObservedCount => roomCenters.ObservedRoomCount;

    /// <summary>本层存在的房间数 / 已踩过的房间数，供 UI 显示。</summary>
    public unsafe (int Exists, int Revealed) RoomProgress
    {
        get
        {
            var dd = RoomGraph.GetDeepDungeon();
            if (dd == null)
                return (0, 0);

            return (RoomGraph.CountExistingRooms(dd), RoomGraph.CountRevealedRooms(dd));
        }
    }

    /// <summary>换层 / 启停时清空本层的房间探索状态（Pitch 会作为下一层的先验保留）。</summary>
    private void ResetRoomState()
    {
        PersistLearnedPitch();

        roomCenters.ResetFloor();
        SeedRoomPitchFromConfig();

        currentRoomIndex = -1;
        roomExploreTarget = -1;
        roomManualTarget = -1;
        roomExploreHop = -1;
        roomExploreGoal = RoomExploreGoal.None;
        blockedRoomMask = 0;
        roomFloorStamp = -1;
        lastRoomTickAt = DateTime.MinValue;
        roomHopActiveSeconds = 0;
        roomNavIssuedHop = -1;
        nextRoomNavIssueAt = DateTime.MinValue;
        roomIndexMissWarned = false;

        blindByRoom.Clear();
        blindByRoomDirty = true;
        blindByRoomSourceCount = -1;
    }

    private void SeedRoomPitchFromConfig()
    {
        var territory = clientState.TerritoryType;
        roomPitchTerritory = territory;

        var seedX = config.RoomGridPitchX.TryGetValue(territory, out var px) ? px : RoomCenters.FallbackPitch;
        var seedZ = config.RoomGridPitchZ.TryGetValue(territory, out var pz) ? pz : RoomCenters.FallbackPitch;
        roomCenters.SeedPitch(seedX, seedZ);
    }

    /// <summary>把这一层真正解出来的房间间距记到配置里，下一层 / 下一趟可以一进门就用。</summary>
    private void PersistLearnedPitch()
    {
        if (!roomCenters.PitchFullySolved)
            return;

        // 用标定开始时记下的 Territory，而不是当前的：
        // 跨 10 层换图时 clientState.TerritoryType 已经先变成新地图了，直接用会把结果记到错的地图上。
        var territory = roomPitchTerritory;
        if (territory == 0)
            return;

        var changed = false;
        if (!config.RoomGridPitchX.TryGetValue(territory, out var oldX) || MathF.Abs(oldX - roomCenters.PitchX) > 0.5f)
        {
            config.RoomGridPitchX[territory] = roomCenters.PitchX;
            changed = true;
        }

        if (!config.RoomGridPitchZ.TryGetValue(territory, out var oldZ) || MathF.Abs(oldZ - roomCenters.PitchZ) > 0.5f)
        {
            config.RoomGridPitchZ[territory] = roomCenters.PitchZ;
            changed = true;
        }

        if (changed)
            config.Save();
    }

    /// <summary>
    /// 每帧维护房间图状态：判断有没有换层、把“玩家所在房间 + 世界坐标”喂给标定器。
    /// 只有副作用，不接管任何一帧。
    /// </summary>
    private unsafe void TickRoomState(IPlayerCharacter player, Vector3 pos, bool force = false)
    {
        // force：财运亨通模式要靠房间中心传送找宝藏，即使用户关了房间图也得标定
        if (!config.UseRoomGraph && !force)
        {
            currentRoomIndex = -1;
            return;
        }

        var dd = RoomGraph.GetDeepDungeon();
        if (dd == null)
        {
            currentRoomIndex = -1;
            return;
        }

        // Boss 房是单独的一张 arena（LayoutInitializationType = 6），没有 5x5 房间网格。
        if (dd->LayoutInitializationType == 6)
        {
            currentRoomIndex = -1;
            return;
        }

        var stamp = ((long)clientState.TerritoryType << 16) | ((long)dd->Floor << 8) | dd->ActiveLayoutIndex;
        if (stamp != roomFloorStamp)
        {
            // 换层 / 换镜像布局：上一层的 Origin 完全作废。
            PersistLearnedPitch();
            roomCenters.ResetFloor();
            SeedRoomPitchFromConfig();

            roomFloorStamp = stamp;
            roomExploreTarget = -1;
            roomManualTarget = -1;
            roomExploreHop = -1;
            roomExploreGoal = RoomExploreGoal.None;
            blockedRoomMask = 0;
            roomHopActiveSeconds = 0;
            lastRoomTickAt = DateTime.MinValue;
            roomNavIssuedHop = -1;
            nextRoomNavIssueAt = DateTime.MinValue;
            blindByRoomDirty = true;

            if (config.devMode)
                log.Information("[AutoPalExplorer][房间图] 换层：Floor={Floor}, Layout={Layout}", dd->Floor, dd->ActiveLayoutIndex);
        }

        var room = RoomGraph.GetLocalPlayerRoomIndex(dd, player.EntityId);
        if (room < 0)
        {
            // 拿不到房间号（游戏结构变了 / 刚进本还没刷）时整套房间功能自动退回贴墙，
            // 这里只提示一次，方便排查是不是 EntityId 对不上。
            if (!roomIndexMissWarned && dd->Floor > 0)
            {
                roomIndexMissWarned = true;
                log.Warning("[AutoPalExplorer][房间图] 读不到本地玩家房间号（EntityId={Id}），本层退回贴墙探索。", player.EntityId);
            }

            return;
        }

        roomIndexMissWarned = false;

        if (room != currentRoomIndex)
        {
            currentRoomIndex = room;
            roomHopActiveSeconds = 0;
            blindByRoomDirty = true; // 换房间要重排盲踩顺序

            if (config.devMode)
                log.Information("[AutoPalExplorer][房间图] 进入房间 {Room}（行 {Row} 列 {Col}）。",
                    room, room / RoomGraph.GridSize, room % RoomGraph.GridSize);
        }

        // 游戏给的房间号 + 玩家真实坐标 = 一组标定真值，每帧喂一次即可自动收敛。
        var before = roomCenters.PitchFullySolved;
        roomCenters.Observe(room, pos);
        if (!before && roomCenters.PitchFullySolved)
        {
            blindByRoomDirty = true;
            if (config.devMode)
                log.Information("[AutoPalExplorer][房间图] 房间间距已解出：X={PitchX:0.0}, Z={PitchZ:0.0}（{Count} 个房间样本）。",
                    roomCenters.PitchX, roomCenters.PitchZ, roomCenters.ObservedRoomCount);
        }
    }

    /// <summary>
    /// 3.4a 房间图探索：按房间图挑最近的没踩过的房间，一跳一跳地走过去。
    /// 比贴墙好在三点——不会绕回已探索区域、优先去有宝箱的房间、全层踩完能直接去传送装置房。
    /// 房间图不可用（未标定 / 拿不到 dd）时返回 false，交回贴墙逻辑兜底。
    /// </summary>
    private unsafe bool HandleRoomExplore(Vector3 pos)
    {
        if (!config.UseRoomGraph || !config.RoomExplore)
            return false;

        if (currentRoomIndex < 0 || !roomCenters.IsCalibrated)
            return false;

        var dd = RoomGraph.GetDeepDungeon();
        if (dd == null)
            return false;

        // 只有真正在跑探索的帧才累加“卡住”计时，避免打怪 / 开箱期间被误判。
        var now = DateTime.UtcNow;
        if (lastRoomTickAt != DateTime.MinValue)
            roomHopActiveSeconds += Math.Min((now - lastRoomTickAt).TotalSeconds, 0.5);
        lastRoomTickAt = now;

        Span<int> dist = stackalloc int[RoomGraph.MaxRooms];
        Span<int> parent = stackalloc int[RoomGraph.MaxRooms];
        RoomGraph.Bfs(dd, currentRoomIndex, blockedRoomMask, dist, parent);

        var snapshot = BuildFloorSnapshot(dd);

        // 手动目标（房间地图上左键点的）优先于自动选择；走到了或走不通就自动作废。
        if (roomManualTarget >= 0)
        {
            if (roomManualTarget == currentRoomIndex || dist[roomManualTarget] < 0)
            {
                roomManualTarget = -1;
            }
            else if (roomExploreGoal != RoomExploreGoal.Manual || roomExploreTarget != roomManualTarget)
            {
                roomExploreTarget = roomManualTarget;
                roomExploreGoal = RoomExploreGoal.Manual;
                roomHopActiveSeconds = 0;
            }
        }

        // 目标保持粘性：只要还没踩到、还可达、没被拉黑，就不换目标，免得两个房间之间来回横跳。
        if (!IsRoomTargetStillValid(snapshot, dist))
        {
            var plan = RoomExplorePlanner.SelectTarget(snapshot, dist);
            if (plan.Goal == RoomExploreGoal.None)
            {
                roomExploreTarget = -1;
                roomExploreHop = -1;
                roomExploreGoal = RoomExploreGoal.None;
                return false;
            }

            roomExploreTarget = plan.TargetRoom;
            roomExploreGoal = plan.Goal;
            roomHopActiveSeconds = 0;

            if (config.devMode)
                log.Information("[AutoPalExplorer][房间图] 新目标房间 {Room}（{Goal}），步数 {Dist}。",
                    plan.TargetRoom, plan.Goal, dist[plan.TargetRoom]);
        }

        if (!RoomGraph.TryGetFirstHop(dist, parent, currentRoomIndex, roomExploreTarget, out var hop))
        {
            // 目标突然不可达（多半是刚被拉黑的房间挡住了路），下一帧重选。
            roomExploreTarget = -1;
            roomExploreHop = -1;
            return false;
        }

        if (hop != roomExploreHop)
        {
            roomExploreHop = hop;
            roomHopActiveSeconds = 0;
        }

        if (!roomCenters.TryGetCenter(hop, out var hopCenter))
            return false;

        // 长时间没能从当前房间挪到下一跳：拉黑那个房间，改走别的路。
        if (roomHopActiveSeconds >= RoomHopStuckSeconds && hop != currentRoomIndex)
        {
            blockedRoomMask |= 1 << hop;
            roomExploreTarget = -1;
            roomExploreHop = -1;
            roomNavIssuedHop = -1;
            nextRoomNavIssueAt = DateTime.MinValue;
            roomHopActiveSeconds = 0;
            navigator.Stop();

            log.Warning("[AutoPalExplorer][房间图] 房间 {Room} 走不进去，本层拉黑。", hop);
            return false;
        }

        // 只在“换了下一跳”或“导航已经停下”时才重新发路径。
        // 不能拿 navigator.CurrentTarget 和房间中心比：TrySafeMoveTo 遇到陷阱会把目标点偏移，
        // 那样每帧都会判定成“目标不同”从而反复重算路径。
        if (!navigator.IsBusy || roomNavIssuedHop != hop)
        {
            // 标定出来的房间中心可能落在墙 / 柱子里导致寻路直接失败，
            // 失败时 Navigator 不会置忙，下一帧又会走到这里，所以再加一层节流。
            if (now < nextRoomNavIssueAt && roomNavIssuedHop == hop)
            {
                SetIntent($"房间图：等待重试前往房间 {roomExploreTarget}");
                return true;
            }

            roomNavIssuedHop = hop;
            nextRoomNavIssueAt = now.AddSeconds(0.5);

            SetIntent(roomExploreGoal switch
            {
                RoomExploreGoal.Passage => $"房间图：全层已探索，前往传送装置房间 {roomExploreTarget}",
                RoomExploreGoal.Manual => $"房间图：前往手动指定的房间 {roomExploreTarget}（下一跳 {hop}）",
                _ => $"房间图：前往房间 {roomExploreTarget}（下一跳 {hop}）"
            });

            TrySafeMoveTo(hopCenter, TrapAvoidRadiusCfg);
        }
        else
        {
            SetIntent($"房间图：前往房间 {roomExploreTarget}（移动中）");
        }

        return true;
    }

    private unsafe RoomFloorSnapshot BuildFloorSnapshot(
        FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentDeepDungeon* dd)
    {
        var existsMask = 0;
        var revealedMask = 0;

        for (var i = 0; i < RoomGraph.MaxRooms; i++)
        {
            if (!RoomGraph.RoomExists(dd, i))
                continue;

            existsMask |= 1 << i;
            if (RoomGraph.IsRevealed(dd, i))
                revealedMask |= 1 << i;
        }

        return new RoomFloorSnapshot(
            currentRoomIndex,
            RoomGraph.GetPassageRoomIndex(dd),
            existsMask,
            revealedMask,
            RoomGraph.GetChestRoomMask(dd),
            blockedRoomMask);
    }

    private bool IsRoomTargetStillValid(in RoomFloorSnapshot snapshot, ReadOnlySpan<int> dist)
    {
        var target = roomExploreTarget;
        if ((uint)target >= RoomGraph.MaxRooms)
            return false;

        if ((snapshot.ExistsMask & (1 << target)) == 0)
            return false;

        if ((snapshot.BlockedMask & (1 << target)) != 0)
            return false;

        if (dist[target] < 0)
            return false;

        // 手动目标：只要用户没撤销、人还没走到，就一直保持。
        if (roomExploreGoal == RoomExploreGoal.Manual)
            return roomManualTarget == target && target != snapshot.PlayerRoom;

        // 去传送装置房间的目标，走到了就算完成。
        if (roomExploreGoal == RoomExploreGoal.Passage)
            return target != snapshot.PlayerRoom;

        // 探索目标：已经被踩亮就该换下一个。
        return (snapshot.RevealedMask & (1 << target)) == 0;
    }

    // ===================== 按房间顺序盲踩 =====================

    /// <summary>
    /// 按房间图挑下一个盲踩点：先把当前房间的候选点踩完，再按 BFS 步数去下一个还有候选点的房间。
    /// 相比“全图找最近的没踩过的点”，好处是不会在两个房间之间反复横跳，
    /// 而且一个房间是一次性搜完的，命中埋藏宝藏的期望步数明显更低。
    /// 房间图不可用时返回 false，交回原来的“最近点”策略。
    /// </summary>
    private unsafe bool TryGetNextBlindLocationByRoom(Vector3 from, out Vector3 result)
    {
        result = default;

        if (!config.UseRoomGraph || !config.BlindRoomOrder)
            return false;

        if (currentRoomIndex < 0 || !roomCenters.IsCalibrated)
            return false;

        var dd = RoomGraph.GetDeepDungeon();
        if (dd == null)
            return false;

        EnsureBlindRoomGroups();
        if (blindByRoom.Count == 0)
            return false;

        Span<int> dist = stackalloc int[RoomGraph.MaxRooms];
        Span<int> parent = stackalloc int[RoomGraph.MaxRooms];
        RoomGraph.Bfs(dd, currentRoomIndex, blockedRoomMask, dist, parent);

        var bestScore = int.MaxValue;
        var bestPointDistSq = float.MaxValue;
        var found = false;

        foreach (var kv in blindByRoom)
        {
            var room = kv.Key;
            var d = dist[room];
            if (d < 0)
                continue;

            // 房间评分：先看最短路步数；步数相同时，还没踩过的房间优先——
            // 这样盲踩顺手把楼层探索也推进了，不用等盲踩结束再单独去开图。
            var score = d * 2;
            if (!RoomGraph.IsRevealed(dd, room))
                score -= 1;

            if (found && score > bestScore)
                continue;

            foreach (var p in kv.Value)
            {
                if (IsBlindLocationIgnored(p))
                    continue;

                var dx = p.X - from.X;
                var dz = p.Z - from.Z;
                var distSq = dx * dx + dz * dz;

                var better = !found
                             || score < bestScore
                             || (score == bestScore && distSq < bestPointDistSq);

                if (!better)
                    continue;

                bestScore = score;
                bestPointDistSq = distSq;
                result = p;
                found = true;
            }
        }

        return found;
    }

    // ===================== 给房间地图窗口用的只读接口 =====================

    /// <summary>房间地图窗口一次性要用的全部状态，避免 UI 每帧反复去戳 controller 内部。</summary>
    public readonly record struct RoomMapInfo(
        bool InDeepDungeon,
        int Floor,
        int LayoutIndex,
        int PlayerRoom,
        int TargetRoom,
        RoomExploreGoal Goal,
        int ManualTarget,
        int BlockedMask,
        int PathMask,
        bool Calibrated,
        bool PitchSolved,
        float PitchX,
        float PitchZ,
        int ObservedRooms,
        int ExistsCount,
        int RevealedCount);

    public unsafe RoomMapInfo GetRoomMapInfo()
    {
        var dd = RoomGraph.GetDeepDungeon();
        if (dd == null)
            return new RoomMapInfo(false, 0, 0, -1, -1, RoomExploreGoal.None, -1, 0, 0, false, false, 0, 0, 0, 0, 0);

        var pathMask = 0;
        if (currentRoomIndex >= 0 && roomExploreTarget >= 0 && roomExploreTarget != currentRoomIndex)
        {
            Span<int> dist = stackalloc int[RoomGraph.MaxRooms];
            Span<int> parent = stackalloc int[RoomGraph.MaxRooms];
            RoomGraph.Bfs(dd, currentRoomIndex, blockedRoomMask, dist, parent);

            if (dist[roomExploreTarget] >= 0)
            {
                var cur = roomExploreTarget;
                for (var guard = 0; guard < RoomGraph.MaxRooms && cur >= 0 && cur != currentRoomIndex; guard++)
                {
                    pathMask |= 1 << cur;
                    cur = parent[cur];
                }
            }
        }

        return new RoomMapInfo(
            true,
            dd->Floor,
            dd->ActiveLayoutIndex,
            currentRoomIndex,
            roomExploreTarget,
            roomExploreGoal,
            roomManualTarget,
            blockedRoomMask,
            pathMask,
            roomCenters.IsCalibrated,
            roomCenters.PitchFullySolved,
            roomCenters.PitchX,
            roomCenters.PitchZ,
            roomCenters.ObservedRoomCount,
            RoomGraph.CountExistingRooms(dd),
            RoomGraph.CountRevealedRooms(dd));
    }

    /// <summary>
    /// 每个房间还剩多少个没踩过的 PalacePal 点位 / 一共多少个。
    /// 两个数组长度必须是 25；房间图没标定好时全部返回 0。
    /// </summary>
    public void GetBlindRoomCounts(Span<int> remaining, Span<int> total)
    {
        remaining.Clear();
        total.Clear();

        if (!config.UseRoomGraph || !roomCenters.IsCalibrated)
            return;

        EnsureBlindRoomGroups();

        foreach (var kv in blindByRoom)
        {
            if ((uint)kv.Key >= RoomGraph.MaxRooms)
                continue;

            total[kv.Key] = kv.Value.Count;

            var left = 0;
            foreach (var p in kv.Value)
            {
                if (!IsBlindLocationIgnored(p))
                    left++;
            }

            remaining[kv.Key] = left;
        }
    }

    /// <summary>房间地图右键：把房间拉黑 / 解除拉黑（拉黑后探索和寻路都会绕开它）。</summary>
    public void ToggleRoomBlocked(int room)
    {
        if ((uint)room >= RoomGraph.MaxRooms)
            return;

        var bit = 1 << room;
        if ((blockedRoomMask & bit) != 0)
        {
            blockedRoomMask &= ~bit;
        }
        else
        {
            blockedRoomMask |= bit;
            if (roomExploreTarget == room)
            {
                roomExploreTarget = -1;
                roomExploreHop = -1;
                roomNavIssuedHop = -1;
            }

            if (roomManualTarget == room)
                roomManualTarget = -1;
        }
    }

    /// <summary>房间地图左键：指定下一个要去的房间，走到后自动回到自动探索。传 -1 取消。</summary>
    public void RequestRoomTarget(int room)
    {
        if (room >= 0 && (uint)room >= RoomGraph.MaxRooms)
            return;

        if (room >= 0 && (blockedRoomMask & (1 << room)) != 0)
            return;

        roomManualTarget = room == roomManualTarget ? -1 : room;

        if (roomManualTarget < 0 && roomExploreGoal == RoomExploreGoal.Manual)
        {
            roomExploreTarget = -1;
            roomExploreHop = -1;
            roomNavIssuedHop = -1;
            roomExploreGoal = RoomExploreGoal.None;
        }
    }

    /// <summary>把当前使用的 PalacePal 点位集合按房间重新分桶（只在需要时重建）。</summary>
    private void EnsureBlindRoomGroups()
    {
        var useTrapSet = config.BlindChestsWithTrap;
        var source = useTrapSet ? allBlindLocations : blindLocations;

        if (!blindByRoomDirty && blindByRoomSourceCount == source.Count && blindByRoomUsedTrapSet == useTrapSet)
            return;

        blindByRoomDirty = false;
        blindByRoomSourceCount = source.Count;
        blindByRoomUsedTrapSet = useTrapSet;

        if (source.Count > MaxBlindGroupPoints)
        {
            blindByRoom.Clear();
            log.Warning("[AutoPalExplorer][房间图] 盲踩点位过多（{Count}），跳过按房间分桶。", source.Count);
            return;
        }

        roomCenters.GroupByRoom(source, blindByRoom);

        if (config.devMode)
        {
            log.Information("[AutoPalExplorer][房间图] 盲踩点位分桶完成：{Rooms} 个房间 / {Points} 个点。",
                blindByRoom.Count, source.Count);
        }
    }
}
