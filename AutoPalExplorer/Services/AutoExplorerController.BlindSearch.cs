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
    private void ResetBlindWalkState()
    {
        ignoredBlindLocations.Clear();
        blindLocations.Clear();
        allBlindLocations.Clear();
        blindLocationsTerritory = 0;
        currentBlindTarget = null;
        blindArrivedAt = DateTime.MinValue;
        blindLastProgressPos = Vector3.Zero;
        blindLastProgressCheckAt = DateTime.MinValue;

        // 丢弃任何进行中/未处理的预定结果，避免跨层残留。
        // 自增世代号：让已在路上的后台请求完成时校验失败，从而不再写回状态位。
        reserveGeneration++;
        reservePendingCandidate = null;
        reserveHasResult = false;
        reserveInFlight = false;
        reserveSucceeded = false;

        if (IsOnlineMode)
        {
            OnlineClearIgnoredOnServer();
        }
    }

    private void EnsureBlindLocationsLoaded()
    {
        var territory = clientState.TerritoryType;

        // 如果当前缓存的就是这个 Territory，而且已经有数据，就不用重复查
        if (blindLocationsTerritory == territory && blindLocations.Count > 0 && allBlindLocations.Count > 0)
            return;

        blindLocations.Clear();
        allBlindLocations.Clear();
        blindLocationsTerritory = territory;
        currentBlindTarget = null;
        blindArrivedAt = DateTime.MinValue;
        ignoredBlindLocations.Clear();
        blindLastProgressPos = Vector3.Zero;
        blindLastProgressCheckAt = DateTime.MinValue;

        if (string.IsNullOrEmpty(config.PalacePalDbPath))
        {
            if (config.devMode)
                log.Warning("[AutoPalExplorer] PalacePalDbPath 未配置，跳过盲踩坐标加载。");
            return;
        }

        var filePath = Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName!, config.PalacePalDbPath);
        if (config.devMode)
            log.Information(filePath.ToString());
        if (!Path.IsPathRooted(config.PalacePalDbPath) && !File.Exists(filePath))
        {
            // 如果你允许直接填绝对路径，也可以再试一次绝对路径
            if (File.Exists(config.PalacePalDbPath))
            {
                filePath = config.PalacePalDbPath;
            }
            else
            {
                if (config.devMode)
                    log.Warning("[AutoPalExplorer] PalacePal 数据库不存在：{Path}", filePath);
                return;
            }
        }

        try
        {
            EnsureSQLiteProvider();

            sqlite3 db;
            var rc = raw.sqlite3_open(filePath, out db);
            if (rc != raw.SQLITE_OK)
            {
                if (config.devMode)
                    log.Warning("[AutoPalExplorer] 打开 PalacePal DB 失败，rc={Rc}", rc);
                // 如果 open 失败，db 可能是非 null，保险起见关一下
                try { raw.sqlite3_close(db); } catch { }
                return;
            }

            try
            {
                // -------- 第一条查询：Type = 2 -> blindLocations --------
                {
                    log.Warning("[AutoPalExplorer] blindLocations开始读取。", rc);
                    var sql = $"SELECT X, Y, Z FROM Locations WHERE Type = 2 AND TerritoryType = {territory}";
                    sqlite3_stmt stmt;
                    rc = raw.sqlite3_prepare_v2(db, sql, out stmt);
                    if (rc != raw.SQLITE_OK)
                    {
                        if (config.devMode)
                            log.Warning("[AutoPalExplorer] 准备查询 Type=2 盲踩坐标失败，rc={Rc}", rc);
                    }
                    else
                    {
                        try
                        {
                            while ((rc = raw.sqlite3_step(stmt)) == raw.SQLITE_ROW)
                            {
                                var x = (float)raw.sqlite3_column_double(stmt, 0);
                                var y = (float)raw.sqlite3_column_double(stmt, 1);
                                var z = (float)raw.sqlite3_column_double(stmt, 2);
                                blindLocations.Add(new Vector3(x, y, z));
                            }

                            if (rc != raw.SQLITE_DONE && config.devMode)
                            {
                                log.Warning("[AutoPalExplorer] 读取 Type=2 盲踩坐标时返回 rc={Rc}（非 SQLITE_DONE）。", rc);
                            }
                        }
                        finally
                        {
                            raw.sqlite3_finalize(stmt);
                        }
                    }
                }

                // -------- 第二条查询：所有点 -> allBlindLocations --------
                {
                    var sql = $"SELECT X, Y, Z FROM Locations WHERE TerritoryType = {territory}";
                    sqlite3_stmt stmt;
                    rc = raw.sqlite3_prepare_v2(db, sql, out stmt);
                    if (rc != raw.SQLITE_OK)
                    {
                        if (config.devMode)
                            log.Warning("[AutoPalExplorer] 准备查询全部盲踩坐标失败，rc={Rc}", rc);
                    }
                    else
                    {
                        try
                        {
                            while ((rc = raw.sqlite3_step(stmt)) == raw.SQLITE_ROW)
                            {
                                var x = (float)raw.sqlite3_column_double(stmt, 0);
                                var y = (float)raw.sqlite3_column_double(stmt, 1);
                                var z = (float)raw.sqlite3_column_double(stmt, 2);
                                allBlindLocations.Add(new Vector3(x, y, z));
                            }

                            if (rc != raw.SQLITE_DONE && config.devMode)
                            {
                                log.Warning("[AutoPalExplorer] 读取全部盲踩坐标时返回 rc={Rc}（非 SQLITE_DONE）。", rc);
                            }
                        }
                        finally
                        {
                            raw.sqlite3_finalize(stmt);
                        }
                    }
                }
            }
            finally
            {
                // ✅ 只在这里关一次
                raw.sqlite3_close(db);
            }

            if (config.devMode)
            {
                log.Information(
                    "[AutoPalExplorer] 盲踩：加载完成，Territory={Territory}, Type=2 点位={CountType2}, 全部点位={CountAll}",
                    territory, blindLocations.Count, allBlindLocations.Count);
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] 加载 PalacePal 盲踩坐标失败：{ex}");
        }
    }

    private Vector3? GetNextBlindLocation(Vector3 from, float maxDistance)
    {
        Vector3? best = null;
        var maxDistSq = maxDistance * maxDistance;
        var bestDistSq = maxDistSq;
        var locationList = config.BlindChestsWithTrap ? allBlindLocations : blindLocations;

        // log.Warning($"[AutoPalExplorer] location数量：{locationList.Count.ToString()}");
        foreach (var p in locationList)
        {
            if (IsBlindLocationIgnored(p))
                continue;

            var dx = p.X - from.X;
            var dz = p.Z - from.Z;
            var distSq = dx * dx + dz * dz;

            if (distSq > maxDistSq)
                continue;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = p;
            }
        }

        return best;
    }

    private long PackBlindKey(Vector3 p)
    {
        // 把坐标粗略量化一下，避免浮点误差导致同一点重复
        var x = (int)MathF.Round(p.X * 10); // 0.1 精度
        var z = (int)MathF.Round(p.Z * 10);
        return ((long)x << 32) | (uint)z;
    }

    private bool IsBlindLocationIgnored(Vector3 p)
    {
        var key = PackBlindKey(p);
        lock (ignoredBlindLocations)
        {
            return ignoredBlindLocations.Contains(key);
        }
    }

    private void IgnoreBlindLocation(Vector3 p)
    {
        var key = PackBlindKey(p);
        var added = false;

        lock (ignoredBlindLocations)
        {
            if (!ignoredBlindLocations.Contains(key))
            {
                ignoredBlindLocations.Add(key);
                added = true;
            }
        }

        if (added && config.devMode)
        {
            log.Information("[AutoPalExplorer] 盲踩：忽略点位 ({X:0.00}, {Y:0.00}, {Z:0.00})。", p.X, p.Y, p.Z);
        }

        // 联机模式：把新忽略的点推到服务器
        // if (added && IsOnlineMode)
        // {
        //     PushIgnoredKeyToServer(key);
        // }
    }

    /// <summary>
    /// 联机模式下非阻塞地选定盲踩目标点。只在主线程调用。
    /// 状态机：
    /// - 有请求在路上 (reserveInFlight)：本帧什么都不做，返回 false（还没拿到目标）。
    /// - 有已完成结果 (reserveHasResult)：
    ///     成功  -> 设为 currentBlindTarget，返回 true；
    ///     失败  -> 本地忽略该点，继续本帧发起下一个候选点的预定，返回 false。
    /// - 空闲：挑一个候选点，发起后台预定，返回 false。
    /// 返回 true 表示已经拿到一个可用的 currentBlindTarget。
    /// </summary>
    private bool TryPickBlindTargetOnline(Vector3 playerPos)
    {
        // 1) 处理已完成的预定结果
        if (reserveHasResult)
        {
            reserveHasResult = false;
            var candidate = reservePendingCandidate;
            reservePendingCandidate = null;

            if (reserveSucceeded && candidate is { } okPos)
            {
                currentBlindTarget = okPos;
                blindArrivedAt = DateTime.MinValue;
                blindLastProgressPos = playerPos;
                blindLastProgressCheckAt = DateTime.UtcNow;

                if (config.devMode)
                {
                    log.Information("[AutoPalExplorer] 盲踩：预定成功，选择新目标 ({X:0.00}, {Y:0.00}, {Z:0.00})，Key={Key}。",
                        okPos.X, okPos.Y, okPos.Z, PackBlindKey(okPos));
                }
                return true;
            }

            // 抢不到：本地也忽略这个点，稍后（下面）尝试下一个候选点
            if (candidate is { } failPos)
            {
                IgnoreBlindLocation(failPos);
                if (config.devMode)
                {
                    log.Information("[AutoPalExplorer] 盲踩：目标 ({X:0.00}, {Y:0.00}, {Z:0.00}) 已被占用，尝试下一个。",
                        failPos.X, failPos.Y, failPos.Z);
                }
            }
        }

        // 2) 已经有请求在路上：等结果，本帧不再发起、也不阻塞
        if (reserveInFlight)
            return false;

        // 3) 空闲：挑下一个候选点并发起后台预定
        var next = GetNextBlindLocation(playerPos, config.BlindMaxDistance);
        if (next is null)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] 盲踩：没有更多可踩的坐标。");
            return false;
        }

        StartReserveKeyOnServer(next.Value);
        return false;
    }

    /// <summary>
    /// 盲踩埋藏宝藏逻辑：
    /// - pomanderManager.HasBuriedBuff 为 false 时生效；
    /// - 从 PalacePal 数据库中取出当前 Territory 的 Type=2 坐标；
    /// - 选最近一个没被忽略的点，引导玩家走过去；
    /// - 到点后停 5 秒，然后标记该点为忽略；
    /// - 如果前往途中 4 秒几乎没移动，则判定“卡住”，也忽略该点。
    /// 返回 true 表示本帧由盲踩逻辑接管。
    /// </summary>
    private bool TryHandleBlindBuriedSearch(Vector3 playerPos)
    {
        // 没配置 DB 就不跑
        if (string.IsNullOrEmpty(config.PalacePalDbPath))
            return false;

        // 如果途中已经拿到埋藏宝藏 Buff 或已经开过本层埋藏宝藏，停止盲踩
        if (pomanderManager.HasBuriedBuff || hasOpenBurinedChest)
            return false;

        EnsureBlindLocationsLoaded();

        if (blindLocations.Count == 0 && !config.BlindChestsWithTrap)
            return false;

        if (allBlindLocations.Count == 0 && config.BlindChestsWithTrap)
            return false;

        // ====== 关键：当前没有目标时，先抢点 ======
        if (currentBlindTarget is null)
        {
            if (IsOnlineMode)
            {
                // 联机模式：预定要走网络，绝不能在主线程上同步等待（高延迟会卡帧）。
                // 改为非阻塞状态机：本帧最多发起一个后台预定请求，由后续帧轮询结果。
                // 关键：只有真正“抢到点”（TryPickBlindTargetOnline 返回 true）才继续往下导航；
                // 否则（请求在路上 / 已没有更多点）返回 false，把本帧交回给探索逻辑，
                // 让角色继续贴墙探索而不是站桩不动——预定完成后下一帧再切到盲踩目标。
                if (!TryPickBlindTargetOnline(playerPos))
                {
                    return false;
                }
            }
            else
            {
                // 本地模式：没有网络，直接同步选点即可。
                var next = GetNextBlindLocation(playerPos, config.BlindMaxDistance);
                if (next is null)
                {
                    if (config.devMode)
                        log.Information("[AutoPalExplorer] 盲踩：没有更多可踩的坐标。");
                    return false;
                }

                var candidate = next.Value;
                currentBlindTarget = candidate;
                blindArrivedAt = DateTime.MinValue;
                blindLastProgressPos = playerPos;
                blindLastProgressCheckAt = DateTime.UtcNow;

                if (config.devMode)
                {
                    log.Information("[AutoPalExplorer] 盲踩：选择新目标 ({X:0.00}, {Y:0.00}, {Z:0.00})，Key={Key}。",
                        candidate.X, candidate.Y, candidate.Z, PackBlindKey(candidate));
                }
            }
        }

        // ====== 后面的逻辑保持不变：走路、到点等待、卡住检测 ======

        var target = currentBlindTarget.Value;
        var dx = target.X - playerPos.X;
        var dz = target.Z - playerPos.Z;
        var distSq = dx * dx + dz * dz;

        if (distSq <= BlindArriveRadius * BlindArriveRadius)
        {
            if (blindArrivedAt == DateTime.MinValue)
            {
                blindArrivedAt = DateTime.UtcNow;
                navigator.Stop();

                if (config.devMode)
                    log.Information("[AutoPalExplorer] 盲踩：已到盲踩目标点，开始原地等待 {Seconds}s。", BlindWaitDuration.TotalSeconds);
            }
            else
            {
                var elapsed = DateTime.UtcNow - blindArrivedAt;
                if (elapsed >= BlindWaitDuration)
                {
                    IgnoreBlindLocation(target);
                    TickOnlineIgnoredSync();
                    currentBlindTarget = null;
                    blindArrivedAt = DateTime.MinValue;

                    if (config.devMode)
                        log.Information("[AutoPalExplorer] 盲踩：在目标点停留 {Elapsed:0.0}s，标记为已踩过并忽略。", elapsed.TotalSeconds);
                }
            }

            return true;
        }

        if (!navigator.IsBusy || IsDifferentTarget(navigator.CurrentTarget, target, 0.5f))
        {
            navigator.Stop();
            navigator.TryMoveTo(target);

            if (config.devMode)
                log.Information("[AutoPalExplorer] 盲踩：导航前往目标点。");
        }
        else
        {
            var now = DateTime.UtcNow;
            if ((now - blindLastProgressCheckAt) >= BlindStuckTimeout)
            {
                var mdx = playerPos.X - blindLastProgressPos.X;
                var mdz = playerPos.Z - blindLastProgressPos.Z;
                var moveSq = mdx * mdx + mdz * mdz;

                if (moveSq < BlindStuckMoveThreshold * BlindStuckMoveThreshold)
                {
                    if (config.devMode)
                        log.Information("[AutoPalExplorer] 盲踩：前往目标途中疑似卡住，忽略该盲踩点位。");

                    IgnoreBlindLocation(target);
                    currentBlindTarget = null;
                    blindArrivedAt = DateTime.MinValue;
                    navigator.Stop();
                }
                else
                {
                    blindLastProgressPos = playerPos;
                }

                blindLastProgressCheckAt = now;
            }
        }

        return true;
    }
}
