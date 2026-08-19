using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

using AutoPalExplorer.Services;

using RoomFlags = FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentDeepDungeon.RoomFlags;

namespace AutoPalExplorer;

/// <summary>
/// 房间地图窗口。
///
/// 把 <see cref="RoomGraph"/> 读到的 5x5 房间网格画出来：连通道路、已探索 / 未探索、
/// 出生点 / 传送装置 / 返回装置、每个房间的宝箱、剩余盲踩点位、玩家朝向，
/// 以及插件自己的探索目标和路径。
///
/// 左键点房间 = 指定下一个要去的房间（走到后自动回到自动探索）；
/// 右键点房间 = 拉黑 / 解除拉黑（拉黑的房间探索和寻路都会绕开）。
/// </summary>
public sealed class RoomMapWindow
{
    private const float TileSize = 52f;
    private const float Gap = 8f;
    private const int Grid = RoomGraph.GridSize;

    private readonly AutoPalController controller;
    private readonly Configuration config;

    private bool isVisible;

    private readonly int[] blindRemaining = new int[RoomGraph.MaxRooms];
    private readonly int[] blindTotal = new int[RoomGraph.MaxRooms];

    public RoomMapWindow(AutoPalController controller, Configuration config)
    {
        this.controller = controller;
        this.config = config;
    }

    public bool IsVisible => isVisible;
    public void Toggle() => isVisible = !isVisible;
    public void Open() => isVisible = true;
    public void Close() => isVisible = false;

    public unsafe void Draw()
    {
        if (!isVisible)
            return;

        if (!ImGui.Begin("房间地图##AutoPalRoomMap", ref isVisible, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var info = controller.GetRoomMapInfo();

        if (!config.UseRoomGraph)
        {
            ImGui.TextDisabled("房间图已在设置里关闭。");
            ImGui.End();
            return;
        }

        if (!info.InDeepDungeon)
        {
            ImGui.TextDisabled("当前不在深层迷宫里。");
            ImGui.End();
            return;
        }

        var dd = RoomGraph.GetDeepDungeon();
        if (dd == null)
        {
            ImGui.TextDisabled("读不到迷宫数据。");
            ImGui.End();
            return;
        }

        DrawHeader(info);
        ImGui.Separator();

        controller.GetBlindRoomCounts(blindRemaining, blindTotal);
        DrawGrid(dd, info);

        ImGui.Separator();
        DrawLegend();

        ImGui.End();
    }

    private void DrawHeader(AutoPalController.RoomMapInfo info)
    {
        ImGui.TextUnformatted($"第 {info.Floor} 层 · 布局 {info.LayoutIndex} · 已探索 {info.RevealedCount}/{info.ExistsCount}");

        if (info.Calibrated)
        {
            var pitchNote = info.PitchSolved ? "本层实测" : "沿用先验";
            ImGui.TextDisabled($"房间间距 X={info.PitchX:0.0} Z={info.PitchZ:0.0}（{pitchNote}，{info.ObservedRooms} 个房间样本）");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), "房间中心尚未标定，暂时无法导航到房间（走几步就好）。");
        }

        var goalText = info.Goal switch
        {
            RoomExploreGoal.Unrevealed => $"探索房间 {info.TargetRoom}",
            RoomExploreGoal.Passage => $"前往传送装置房间 {info.TargetRoom}",
            RoomExploreGoal.Manual => $"手动指定房间 {info.TargetRoom}",
            _ => "无"
        };
        ImGui.TextUnformatted($"当前目标：{goalText}");
    }

    private unsafe void DrawGrid(
        FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentDeepDungeon* dd,
        AutoPalController.RoomMapInfo info)
    {
        var drawList = ImGui.GetWindowDrawList();
        var topLeft = ImGui.GetCursorScreenPos();
        // 下面循环里为了做点击热区会反复移动光标，先把起点记下来，画完再恢复，
        // 否则最后那个 Dummy 会被放到最后一格下面，把窗口撑得很高。
        var startCursorPos = ImGui.GetCursorPos();
        var totalSize = new Vector2(Grid * TileSize + (Grid - 1) * Gap, Grid * TileSize + (Grid - 1) * Gap);

        // 颜色
        var colNoRoom = ImGui.GetColorU32(new Vector4(0.10f, 0.10f, 0.11f, 0.55f));
        var colHidden = ImGui.GetColorU32(new Vector4(0.15f, 0.18f, 0.24f, 1.0f));
        var colRevealed = ImGui.GetColorU32(new Vector4(0.34f, 0.36f, 0.40f, 1.0f));
        var colHatch = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.10f));
        var colBlocked = ImGui.GetColorU32(new Vector4(0.65f, 0.15f, 0.15f, 0.45f));
        var colBorder = ImGui.GetColorU32(new Vector4(0.55f, 0.57f, 0.60f, 1f));
        var colBorderPath = ImGui.GetColorU32(new Vector4(0.35f, 0.65f, 1.00f, 1f));
        var colBorderTarget = ImGui.GetColorU32(new Vector4(1.00f, 0.85f, 0.20f, 1f));
        var colRoad = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f));
        var colRoadHidden = ImGui.GetColorU32(new Vector4(0.78f, 0.78f, 0.78f, 0.45f));
        var colRoadShadow = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.55f));
        var colPlayer = ImGui.GetColorU32(new Vector4(1.00f, 0.95f, 0.20f, 1f));
        var colBlind = ImGui.GetColorU32(new Vector4(0.55f, 0.90f, 0.60f, 1f));
        var colBlindDone = ImGui.GetColorU32(new Vector4(0.45f, 0.45f, 0.45f, 1f));

        var map = dd->MapData;

        // 先画道路，让它只留在格子之间的缝隙里，不盖住格子内容
        for (var i = 0; i < RoomGraph.MaxRooms; i++)
        {
            var center = TileTopLeft(topLeft, i) + new Vector2(TileSize * 0.5f, TileSize * 0.5f);
            var flags = map[i];
            var roadCol = (flags & RoomFlags.Revealed) != 0 ? colRoad : colRoadHidden;

            DrawRoad(drawList, center, flags, RoomFlags.ConnectionN, new Vector2(0f, -1f), roadCol, colRoadShadow);
            DrawRoad(drawList, center, flags, RoomFlags.ConnectionS, new Vector2(0f, 1f), roadCol, colRoadShadow);
            DrawRoad(drawList, center, flags, RoomFlags.ConnectionW, new Vector2(-1f, 0f), roadCol, colRoadShadow);
            DrawRoad(drawList, center, flags, RoomFlags.ConnectionE, new Vector2(1f, 0f), roadCol, colRoadShadow);
        }

        // 再画格子
        for (var i = 0; i < RoomGraph.MaxRooms; i++)
        {
            var tl = TileTopLeft(topLeft, i);
            var br = tl + new Vector2(TileSize, TileSize);
            var center = (tl + br) * 0.5f;

            var flags = map[i];
            var exists = (flags & RoomGraph.ConnectionMask) != 0;
            var revealed = (flags & RoomFlags.Revealed) != 0;
            var blocked = (info.BlockedMask & (1 << i)) != 0;

            var fill = !exists ? colNoRoom : revealed ? colRevealed : colHidden;
            drawList.AddRectFilled(tl, br, fill, 4f);

            if (exists && !revealed)
                DrawHatch(drawList, tl, br, colHatch, 6f, 1.2f);

            if (blocked)
                drawList.AddRectFilled(tl, br, colBlocked, 4f);

            var borderCol = colBorder;
            var borderThick = 1.5f;
            if (exists && i == info.TargetRoom)
            {
                borderCol = colBorderTarget;
                borderThick = 2.5f;
            }
            else if ((info.PathMask & (1 << i)) != 0)
            {
                borderCol = colBorderPath;
                borderThick = 2f;
            }

            drawList.AddRect(tl, br, borderCol, 4f, ImDrawFlags.RoundCornersAll, borderThick);

            if (exists)
            {
                DrawChests(drawList, dd, i, tl);
                DrawLandmark(drawList, flags, tl, br);
                DrawBlindCount(drawList, i, br, colBlind, colBlindDone);
            }

            if (i == info.PlayerRoom)
                DrawPlayerArrow(drawList, center, colPlayer);

            HandleTileInput(drawList, i, tl, br, exists, info);
        }

        ImGui.SetCursorPos(startCursorPos);
        ImGui.Dummy(totalSize);
    }

    private static Vector2 TileTopLeft(Vector2 origin, int roomIndex)
    {
        var row = roomIndex / Grid;
        var col = roomIndex % Grid;
        return origin + new Vector2(col * (TileSize + Gap), row * (TileSize + Gap));
    }

    private static void DrawRoad(
        ImDrawListPtr dl, Vector2 center, RoomFlags flags, RoomFlags connection,
        Vector2 direction, uint col, uint shadow)
    {
        if ((flags & connection) == 0)
            return;

        var half = TileSize * 0.5f;
        var from = center + direction * half;
        var to = center + direction * (half + Gap);
        dl.AddLine(from, to, shadow, 4f);
        dl.AddLine(from, to, col, 3f);
    }

    /// <summary>未探索房间上打斜线，一眼能和已探索区分开。</summary>
    private static void DrawHatch(ImDrawListPtr dl, Vector2 tl, Vector2 br, uint col, float spacing, float thickness)
    {
        var size = br - tl;
        for (var o = -size.Y; o <= size.X; o += spacing)
        {
            var start = new Vector2(tl.X + MathF.Max(o, 0f), tl.Y + MathF.Max(-o, 0f));
            var ex = start.X + size.Y;
            var ey = start.Y + size.Y;

            if (ex > br.X)
            {
                var dx = br.X - start.X;
                ex = br.X;
                ey = start.Y + dx;
            }

            if (ey > br.Y)
            {
                var dy = br.Y - start.Y;
                ey = br.Y;
                ex = start.X + dy;
            }

            dl.AddLine(start, new Vector2(ex, ey), col, thickness);
        }
    }

    /// <summary>宝箱：游戏自己记录了每个宝箱在哪个房间，直接照抄到格子左上角。</summary>
    private static unsafe void DrawChests(
        ImDrawListPtr dl,
        FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentDeepDungeon* dd,
        int room, Vector2 tl)
    {
        var colBronze = ImGui.GetColorU32(new Vector4(0.80f, 0.50f, 0.20f, 1f));
        var colSilver = ImGui.GetColorU32(new Vector4(0.75f, 0.75f, 0.80f, 1f));
        var colGold = ImGui.GetColorU32(new Vector4(1.00f, 0.84f, 0.00f, 1f));
        var colOutline = ImGui.GetColorU32(new Vector4(0.1f, 0.1f, 0.1f, 0.9f));

        const float s = 9f;
        var drawn = 0;
        var chests = dd->Chests;

        for (var i = 0; i < chests.Length && drawn < 3; i++)
        {
            var info = chests[i];
            if (info.ChestType == 0 || info.RoomIndex != room)
                continue;

            var cTL = tl + new Vector2(3f + drawn * (s + 2f), 3f);
            var cBR = cTL + new Vector2(s, s);
            var col = info.ChestType switch
            {
                1 => colBronze,
                2 => colSilver,
                3 => colGold,
                _ => colBronze
            };

            dl.AddRectFilled(cTL, cBR, col, 2f);
            dl.AddRect(cTL, cBR, colOutline, 2f);
            drawn++;
        }
    }

    /// <summary>出生点 / 传送装置 / 返回装置。</summary>
    private static void DrawLandmark(ImDrawListPtr dl, RoomFlags flags, Vector2 tl, Vector2 br)
    {
        string? label = null;
        Vector4 color = default;

        if ((flags & RoomFlags.Passage) != 0)
        {
            label = "通";
            color = new Vector4(0.40f, 1.00f, 0.55f, 1f);
        }
        else if ((flags & RoomFlags.Return) != 0)
        {
            label = "回";
            color = new Vector4(0.55f, 0.75f, 1.00f, 1f);
        }
        else if ((flags & RoomFlags.Home) != 0)
        {
            label = "起";
            color = new Vector4(0.90f, 0.90f, 0.90f, 1f);
        }

        if (label == null)
            return;

        var size = ImGui.CalcTextSize(label);
        var pos = new Vector2(tl.X + 3f, br.Y - size.Y - 2f);
        dl.AddText(pos + new Vector2(1, 1), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.85f)), label);
        dl.AddText(pos, ImGui.GetColorU32(color), label);
    }

    /// <summary>该房间还剩几个没踩的 PalacePal 点位（右上角），全踩完就变灰。</summary>
    private void DrawBlindCount(ImDrawListPtr dl, int room, Vector2 br, uint colActive, uint colDone)
    {
        if (!config.BlindChests || blindTotal[room] == 0)
            return;

        var text = $"{blindRemaining[room]}/{blindTotal[room]}";
        var size = ImGui.CalcTextSize(text);
        var pos = new Vector2(br.X - size.X - 3f, br.Y - size.Y - 2f);
        dl.AddText(pos + new Vector2(1, 1), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.85f)), text);
        dl.AddText(pos, blindRemaining[room] > 0 ? colActive : colDone, text);
    }

    /// <summary>
    /// 玩家箭头。地图里 +X 向右、+Z 向下，而游戏里 Rotation=0 表示朝 +Z，
    /// 所以屏幕方向直接就是 (sin, cos)。
    /// </summary>
    private static void DrawPlayerArrow(ImDrawListPtr dl, Vector2 center, uint col)
    {
        var rot = 0f;
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player != null)
            rot = player.Rotation;

        var dir = new Vector2(MathF.Sin(rot), MathF.Cos(rot));
        var perp = new Vector2(-dir.Y, dir.X);

        const float r = 9f;
        var tip = center + dir * r;
        var left = center - dir * (r * 0.55f) + perp * (r * 0.6f);
        var right = center - dir * (r * 0.55f) - perp * (r * 0.6f);

        dl.AddTriangleFilled(tip, left, right, col);
        dl.AddTriangle(tip, left, right, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.85f)), 1.2f);
    }

    private void HandleTileInput(
        ImDrawListPtr dl, int room, Vector2 tl, Vector2 br, bool exists, AutoPalController.RoomMapInfo info)
    {
        ImGui.SetCursorScreenPos(tl);
        ImGui.InvisibleButton($"apal_room_{room}", new Vector2(TileSize, TileSize));

        if (!ImGui.IsItemHovered())
            return;

        var canTarget = exists && info.Calibrated;
        var hl = ImGui.GetColorU32(canTarget
            ? new Vector4(0.25f, 0.65f, 0.35f, 0.25f)
            : new Vector4(0.5f, 0.5f, 0.5f, 0.18f));
        dl.AddRectFilled(tl, br, hl, 4f);

        var blocked = (info.BlockedMask & (1 << room)) != 0;
        if (!exists)
        {
            ImGui.SetTooltip($"房间 {room}：本层布局里没有这个房间。");
        }
        else if (!info.Calibrated)
        {
            ImGui.SetTooltip($"房间 {room}：房间中心还没标定，暂时不能指定目标。");
        }
        else
        {
            var manual = info.ManualTarget == room ? "（当前手动目标，再点一次取消）" : string.Empty;
            ImGui.SetTooltip(
                $"房间 {room}（行 {room / Grid} 列 {room % Grid}）\n" +
                $"左键：指定为下一个要去的房间{manual}\n" +
                (blocked ? "右键：解除拉黑" : "右键：拉黑此房间（探索和寻路都会绕开）"));
        }

        if (canTarget && ImGui.IsItemClicked(ImGuiMouseButton.Left))
            controller.RequestRoomTarget(room);

        if (exists && ImGui.IsItemClicked(ImGuiMouseButton.Right))
            controller.ToggleRoomBlocked(room);
    }

    private static void DrawLegend()
    {
        Swatch(new Vector4(0.34f, 0.36f, 0.40f, 1f), "已探索");
        ImGui.SameLine();
        Swatch(new Vector4(0.15f, 0.18f, 0.24f, 1f), "未探索");
        ImGui.SameLine();
        Swatch(new Vector4(0.65f, 0.25f, 0.25f, 1f), "已拉黑");

        Swatch(new Vector4(1.00f, 0.85f, 0.20f, 1f), "目标");
        ImGui.SameLine();
        Swatch(new Vector4(0.35f, 0.65f, 1.00f, 1f), "路径");
        ImGui.SameLine();
        Swatch(new Vector4(0.55f, 0.90f, 0.60f, 1f), "剩余盲踩点");

        ImGui.TextDisabled("起=出生点  通=传送装置  回=返回装置；左上角小方块是宝箱。");
    }

    private static void Swatch(Vector4 color, string label)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var h = ImGui.GetTextLineHeight();
        dl.AddRectFilled(pos, pos + new Vector2(h, h), ImGui.GetColorU32(color), 2f);
        ImGui.Dummy(new Vector2(h, h));
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
    }
}
