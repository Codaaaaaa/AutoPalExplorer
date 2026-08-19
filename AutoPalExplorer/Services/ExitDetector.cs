using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

using AutoPalExplorer.Helpers;

namespace AutoPalExplorer.Services;

/// <summary>
/// 识别：
/// - 传送装置（同一个 DataId，由 ExitActivated 区分状态）
/// - 宝箱
/// ExitActivated 由控制器标记：主要来源是 DeepDungeonMap 图标 PartId==10（见 AutoExplorerController.ExitActivation），
/// 聊天“传送装置启动了”作为兜底，两者都走 MarkExitActivated()。
/// </summary>
public sealed class ExitDetector
{
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    public IGameObject? Exit { get; private set; }
    public IGameObject? Chest { get; private set; }

    public bool ExitActivated { get; private set; }

    public bool HasExit => Exit is not null;
    public bool HasChest => Chest is not null;

    public bool HasActiveExit => ExitActivated && Exit is not null;
    public bool HasInactiveExit => !ExitActivated && Exit is not null;

    public ExitDetector(IObjectTable objectTable, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.log = log;
    }

    public void Reset()
    {
        Exit = null;
        Chest = null;
        ExitActivated = false;
        log.Information("[AutoPalExplorer] Exit marked deactivated.");
    }

    public void MarkExitActivated()
    {
        if (!ExitActivated)
        {
            ExitActivated = true;
            log.Information("[AutoPalExplorer] Exit marked as activated.");
        }
    }

    /// <summary>
    /// 每帧按玩家位置刷新最近 Exit 和 Chest。
    /// </summary>
    public void Update(Vector3 playerPos)
    {
        Exit = null;
        Chest = null;

        float exitBest = float.MaxValue;
        float chestBest = float.MaxValue;

        foreach (var obj in objectTable)
        {
            if (obj is null || obj.BaseId == 0)
                continue;

            var pos = obj.Position;
            var dx = pos.X - playerPos.X;
            var dz = pos.Z - playerPos.Z;
            var distSq = dx * dx + dz * dz;
            
            if (ObjectIds.exitIds.Contains(obj.BaseId))
            {
                if (distSq < exitBest)
                {
                    exitBest = distSq;
                    Exit = obj;
                }
            }
            else if (ObjectIds.IsAnyChest(obj.BaseId))
            {
                if (distSq < chestBest)
                {
                    chestBest = distSq;
                    Chest = obj;
                }
            }
        }
    }
}
