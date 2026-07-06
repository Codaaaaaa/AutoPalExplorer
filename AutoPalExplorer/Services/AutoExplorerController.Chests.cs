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
    private bool ShouldOpenChest(uint baseId)
    {
        if (ObjectIds.IsBronzeChest(baseId))
            return config.OpenBronzeChests;

        if (ObjectIds.IsSilverChest(baseId))
            return config.OpenSilverChests;

        if (ObjectIds.IsGoldChest(baseId))
            return config.OpenGoldChests;
        
        if (ObjectIds.IsBuriedChest(baseId))
            return true;

        return false;
    }

    private unsafe void TryOpenChest(IGameObject chest)
    {
        if (!ShouldOpenChest(chest.BaseId))
            return;
        
        var player = objectTable.LocalPlayer;
        if (player == null)
            return;

        foreach (var status in player.StatusList)
        {
            if (status.StatusId == DebuffIds.changeBuff)
            {
                log.Debug($"Player has change buff {status.StatusId}, skip chest.");
                return;
            }
        }

        if (chest == null)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] TryOpenChest 防呆：chest 为 null。");
            return;
        }

        if (!chest.IsTargetable)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] TryOpenChest：宝箱不可交互（可能已开/动画中），跳过。");
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - lastChestInteractAt).TotalMilliseconds < ChestInteractIntervalMs)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] TryOpenChest：节流中，跳过本帧开箱请求。");
            return;
        }

        try
        {
            var ptr = (GameObject*)chest.Address;
            if (ptr == null)
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] TryOpenChest：GameObject 指针为空。");
                return;
            }

            var ts = TargetSystem.Instance();
            if (ts == null)
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] TryOpenChest：TargetSystem 实例为空。");
                return;
            }

            lastChestInteractObjectId = chest.GameObjectId;

            ts->InteractWithObject(ptr, false);
            lastChestInteractAt = now;

            if (config.devMode)
            {
                log.Information("[AutoPalExplorer] 已尝试与宝箱交互，位置=({X:0.00}, {Y:0.00}, {Z:0.00})。",
                    chest.Position.X, chest.Position.Y, chest.Position.Z);
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] TryOpenChest 异常：{ex.Message}");
        }
    }

    private void ClearLockedChest()
    {
        if (!hasLockedChest)
            return;

        if (config.devMode)
            log.Information("[AutoPalExplorer] 锁定宝箱已清除：GameObjectId={Id}。", lockedChestId);

        hasLockedChest = false;
        lockedChestId = 0;
        lockedChestPos = Vector3.Zero;
    }

    /// <summary>
    /// 在一定范围内寻找最近的埋藏宝藏（EventObj + BuriedChestIds）。
    /// </summary>
    private IGameObject? FindNearestBuriedChest(Vector3 from, float maxDistance)
    {
        IGameObject? best = null;
        var bestDistSq = maxDistance * maxDistance;

        foreach (var obj in objectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)
                continue;

            if (!ObjectIds.BuriedChestIds.Contains(obj.BaseId))
                continue;

            var dx = obj.Position.X - from.X;
            var dz = obj.Position.Z - from.Z;
            var distSq = dx * dx + dz * dz;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = obj;
            }
        }

        return best;
    }

    private bool IsIgnoredChest(IGameObject chest)
        => ignoredChestIds.Contains(chest.GameObjectId);

    private bool HandleChest(Vector3 playerPos, IGameObject chest, Vector3? currentTarget)
    {
        if (IsIgnoredChest(chest))
            return false;

        if (!ShouldOpenChest(chest.BaseId))
            return false;

        if (!chest.IsTargetable)
            return false;

        var dx = chest.Position.X - playerPos.X;
        var dz = chest.Position.Z - playerPos.Z;
        var distSq = dx * dx + dz * dz;

        if (distSq > ChestDoneRadius * ChestDoneRadius)
        {
            // ✅ 新增：在决定走向这个宝箱时上锁，记录 ID 和坐标
            if (!hasLockedChest || lockedChestId != chest.GameObjectId)
            {
                hasLockedChest = true;
                lockedChestId = chest.GameObjectId;
                lockedChestPos = chest.Position;

                if (config.devMode)
                {
                    log.Information("[AutoPalExplorer] 锁定宝箱：GameObjectId={Id}, Pos=({X:0.00}, {Y:0.00}, {Z:0.00})。",
                        lockedChestId, lockedChestPos.X, lockedChestPos.Y, lockedChestPos.Z);
                }
            }

            if (!navigator.IsBusy || IsDifferentTarget(currentTarget, chest.Position, 1.0f))
            {
                navigator.Stop();
                navigator.TryMoveTo(chest.Position);
            }
            return true; // 本帧由宝箱逻辑接管
        }

        // 已在开箱半径内
        // ✅ 新增：到点后可以把锁清掉（不再需要防抖）
        if (hasLockedChest && lockedChestId == chest.GameObjectId)
        {
            ClearLockedChest();
        }

        TryOpenChest(chest);
        return true; // 建议这里也直接 true，后面不再处理门
    }

    private bool HandleLockedChest(Vector3 playerPos, Vector3? currentTarget)
    {
        if (!hasLockedChest)
            return false;

        // 尝试在 objectTable 中找到这个 GameObjectId
        IGameObject? lockedChestObj = null;
        foreach (var obj in objectTable)
        {
            if (obj.GameObjectId == lockedChestId)
            {
                lockedChestObj = obj;
                break;
            }
        }

        // 1) 找到了真实对象
        if (lockedChestObj is not null)
        {
            var dx = lockedChestObj.Position.X - playerPos.X;
            var dz = lockedChestObj.Position.Z - playerPos.Z;
            var distSq = dx * dx + dz * dz;

            // 如果现在配置里已经不打算开这个箱子，或者已经被标记 ignore，直接解锁
            if (!ShouldOpenChest(lockedChestObj.BaseId) || IsIgnoredChest(lockedChestObj))
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] 锁定宝箱已被配置忽略或在忽略列表中，解除锁定。");
                ClearLockedChest();
                return false;
            }

            // 如果已经不可交互且在 ChestDoneRadius 范围内，认为已经被开过，加入 ignore 并解锁
            if (!lockedChestObj.IsTargetable && distSq <= ChestDoneRadius * ChestDoneRadius && !ObjectIds.IsBuriedChest(lockedChestObj.BaseId))
            {
                if (ignoredChestIds.Add(lockedChestObj.GameObjectId) && config.devMode)
                {
                    log.Information("[AutoPalExplorer] 锁定宝箱在 ChestDoneRadius 内且不可交互，视为已开，加入忽略列表并解锁。");
                }
                ClearLockedChest();
                return false;
            }

            // 正常情况：交给现有 HandleChest 处理（移动 / 开箱），同时保持锁定
            return HandleChest(playerPos, lockedChestObj, currentTarget);
        }

        // 2) 在 objectTable 中找不到这个箱子（被裁剪掉 / despawn）
        var dx2 = lockedChestPos.X - playerPos.X;
        var dz2 = lockedChestPos.Z - playerPos.Z;
        var distSq2 = dx2 * dx2 + dz2 * dz2;

        var giveUpDistSq = LockedChestGiveUpDistance * LockedChestGiveUpDistance;

        if (distSq2 > giveUpDistSq)
        {
            // 距离原始宝箱位置 > 100：即使暂时不可见，也继续朝 lockedChestPos 走，不切换到门/怪
            if (!navigator.IsBusy || IsDifferentTarget(currentTarget, lockedChestPos, 1.0f))
            {
                if (config.devMode)
                {
                    var dist = MathF.Sqrt(distSq2);
                    log.Information("[AutoPalExplorer] 锁定宝箱暂时不可见，距离={Dist:0.0} > {Limit}，继续导航到记录位置。",
                        dist, LockedChestGiveUpDistance);
                }

                TrySafeMoveTo(lockedChestPos, TrapAvoidRadiusCfg);
            }

            // 本帧由锁定宝箱逻辑接管
            return true;
        }
        else
        {
            // 距离原始宝箱位置 <= 100 且仍然看不到这个箱子：按你说的，当作已经开过
            if (lockedChestId != 0 && ignoredChestIds.Add(lockedChestId))
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] 锁定宝箱在100范围内仍不可见，视为已开，加入忽略列表。");
            }

            ClearLockedChest();
            // 返回 false，让后面的门/怪逻辑可以接管
            return false;
        }
    }

    private IGameObject? FindNextChestToOpen(Vector3 playerPos)
    {
        IGameObject? best = null;
        var bestDistSq = float.MaxValue;

        foreach (var obj in objectTable)
        {
            // 只看事件物件（宝藏和EventObj）
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj && obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure)
                continue;

            // 根据配置决定开不打开这种箱子
            if (!ShouldOpenChest(obj.BaseId))
                continue;

            var dx = obj.Position.X - playerPos.X;
            var dz = obj.Position.Z - playerPos.Z;
            var distSq = dx * dx + dz * dz;

            // ✅ 情况一：在开箱半径内，但已经不可交互
            // 说明 99% 是刚开完的箱子（或者被队友开完），直接加入 ignore，避免一直把它当目标。
            if (!obj.IsTargetable && distSq <= ChestDoneRadius * ChestDoneRadius && !ObjectIds.IsBuriedChest(obj.BaseId))
            {
                if (ignoredChestIds.Add(obj.GameObjectId) && config.devMode)
                {
                    log.Information(
                        "[AutoPalExplorer] 宝箱 BaseId={BaseId} 在 ChestDoneRadius 内且不可交互，视为已开，加入 ignore 列表。",
                        obj.BaseId
                    );
                }
                continue;
            }

            // 不可交互而且距离很远：可能是别层/奇怪残影，一律不当成候选
            if (!obj.IsTargetable)
                continue;

            // 忽略列表里的箱子直接跳过
            if (IsIgnoredChest(obj))
                continue;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = obj;
            }
        }

        return best;
    }
}
