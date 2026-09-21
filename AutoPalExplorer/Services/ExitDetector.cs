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
/// ExitActivated 由控制器每帧写入（见 AutoExplorerController.ExitActivation.TickExitActivation）：
/// 主要来源是游戏内 InstanceContentDeepDungeon.PassageProgress（实时、不依赖地图 UI），
/// 地图图标 PartId==10 与聊天“传送装置启动了”作为兜底。
/// 注意这里是「当前状态」而不是「一次性锁存」：控制器判定未激活时会把它写回 false。
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
        SetExitActivated(false);
    }

    /// <summary>写入传送装置当前的激活状态（真值来自控制器，变化时才打日志）。</summary>
    public void SetExitActivated(bool activated)
    {
        if (ExitActivated == activated)
            return;

        ExitActivated = activated;
        log.Information("[AutoPalExplorer] Exit marked as {State}.", activated ? "activated" : "deactivated");
    }

    public void MarkExitActivated() => SetExitActivated(true);

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
