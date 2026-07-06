using System;
using System.Numerics;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json.Serialization;

using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;

using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using static ECommons.GenericHelpers;

using SQLitePCL;

using AutoPalExplorer.Helpers;

namespace AutoPalExplorer.Services;

public sealed partial class AutoPalController
{
    private void UpdateStaticObjectPositions()
    {
        foreach (var obj in objectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)
                continue;

            // 传送装置
            if (ObjectIds.exitIds.Contains(obj.BaseId))
            {
                savedExitPos = obj.Position;
            }
            // 再生祭坛
            else if (ObjectIds.regenerationIds.Contains(obj.BaseId))
            {
                savedRegenerationPos = obj.Position;
            }
        }
    }

    private static bool IsDifferentTarget(Vector3? current, Vector3 desired, float threshold)
    {
        if (current is null)
            return true;

        var v = current.Value;
        var dx = v.X - desired.X;
        var dz = v.Z - desired.Z;
        return dx * dx + dz * dz > threshold * threshold;
    }

    private unsafe void TryInteractWithObject(IGameObject obj, string purpose)
    {
        if (obj == null)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] TryInteractWithObject({Purpose})：目标为 null。", purpose);
            return;
        }

        if (!obj.IsTargetable)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] TryInteractWithObject({Purpose})：目标不可交互，BaseId={BaseId}。", purpose, obj.BaseId);
            return;
        }

        try
        {
            var ptr = (GameObject*)obj.Address;
            if (ptr == null)
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] TryInteractWithObject({Purpose})：GameObject 指针为空。", purpose);
                return;
            }

            var ts = TargetSystem.Instance();
            if (ts == null)
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] TryInteractWithObject({Purpose})：TargetSystem 实例为空。", purpose);
                return;
            }

            ts->InteractWithObject(ptr, false);

            if (config.devMode)
                log.Information("[AutoPalExplorer] 已尝试与 {Purpose} 交互，BaseId={BaseId}。", purpose, obj.BaseId);
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] TryInteractWithObject({purpose}) 异常：{ex.Message}");
        }
    }

    private IGameObject? FindObjectByBaseId(uint baseId)
    {
        foreach (var obj in objectTable)
        {
            if (obj.BaseId == baseId)
                return obj;
        }
        return null;
    }

    /// <summary>
    /// 检查某个点附近是否有陷阱（EventObj + TrapIds）。
    /// 返回是否危险，以及最近陷阱的位置。
    /// </summary>
    private bool IsNearTrap(Vector3 point, float radius, out Vector3 nearestTrapPos)
    {
        var radiusSq = radius * radius;
        nearestTrapPos = default;
        var found = false;
        var bestSq = float.MaxValue;

        foreach (var obj in objectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)
                continue;

            if (!ObjectIds.TrapIds.Contains(obj.BaseId))
                continue;

            var dx = obj.Position.X - point.X;
            var dz = obj.Position.Z - point.Z;
            var distSq = dx * dx + dz * dz;

            if (distSq < radiusSq && distSq < bestSq)
            {
                bestSq = distSq;
                nearestTrapPos = obj.Position;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// 包一层导航：如果目标点太靠近陷阱，尝试往远离陷阱方向偏移；如果仍然不安全则放弃该移动。
    /// </summary>
    private bool TrySafeMoveTo(Vector3 destination, float avoidRadius)
    {
        if (IsNearTrap(destination, avoidRadius, out var trapPos))
        {
            // 计算一个从陷阱往外偏移的新目标点
            var offset = destination - trapPos;
            offset.Y = 0;

            if (offset.LengthSquared() < 0.0001f)
            {
                // 和陷阱几乎重合，随便给个方向
                offset = new Vector3(1, 0, 0);
            }

            offset = Vector3.Normalize(offset) * (avoidRadius + 0.5f);
            var newDest = trapPos + offset;

            // 再检查一次新目标是否仍然贴陷阱
            if (IsNearTrap(newDest, avoidRadius, out _))
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] TrySafeMoveTo: 目标及偏移点均过近陷阱，放弃该导航目标。");
                return false;
            }

            if (config.devMode)
            {
                log.Information("[AutoPalExplorer] TrySafeMoveTo: 目标靠近陷阱，调整至安全点 ({X:0.00}, {Y:0.00}, {Z:0.00})。",
                    newDest.X, newDest.Y, newDest.Z);
            }

            navigator.Stop();
            navigator.TryMoveTo(newDest);
            return true;
        }

        navigator.Stop();
        navigator.TryMoveTo(destination);
        return true;
    }

    private void ResetStaticObjectsState()
    {
        savedExitPos = null;
        savedRegenerationPos = null;
        exitActivatedByChat = false;
        regenerationActivated = false;
    }
}
