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
    private void OnlineClearIgnoredOnServer()
    {
        var apiKey = config.OnlineApiKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        var territory = clientState.TerritoryType;
        var url = $"{OnlineServerUrl}/ignored/clear";

        var payload = new OnlineIgnoredClearRequest
        {
            ApiKey = apiKey,
            Territory = territory
        };

        _ = Task.Run(async () =>
        {
            try
            {
                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await httpClient.PostAsync(url, content).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode && config.devMode)
                {
                    log.Warning("[AutoPalExplorer] 联机盲踩：清空 ignored 失败 HTTP {Code}", resp.StatusCode);
                }
            }
            catch (Exception ex)
            {
                if (config.devMode)
                    log.Warning($"[AutoPalExplorer] 联机盲踩：清空 ignored 异常：{ex.Message}");
            }
        });
    }

    private void PushIgnoredKeyToServer(long key)
    {
        var apiKey = config.OnlineApiKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        var territory = clientState.TerritoryType;
        var url = $"{OnlineServerUrl}/ignored/add";

        var payload = new OnlineIgnoredAddRequest
        {
            ApiKey = apiKey,
            Territory = territory,
            Keys = new[] { key }
        };

        _ = Task.Run(async () =>
        {
            try
            {
                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await httpClient.PostAsync(url, content).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode && config.devMode)
                {
                    log.Warning("[AutoPalExplorer] 联机盲踩：上报 ignored 点位失败 HTTP {Code}", resp.StatusCode);
                    // log.Warning(url);
                }
            }
            catch (Exception ex)
            {
                if (config.devMode)
                    log.Warning($"[AutoPalExplorer] 联机盲踩：上报 ignored 点位异常：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 在后台线程向服务器发起“预定点位”请求，不阻塞主线程。
    /// 完成后写回 reserveSucceeded / reserveHasResult，并最后清 reserveInFlight。
    /// </summary>
    private void StartReserveKeyOnServer(Vector3 candidate)
    {
        var key = PackBlindKey(candidate);
        reservePendingCandidate = candidate;
        reserveInFlight = true;
        reserveHasResult = false;
        var gen = reserveGeneration; // 捕获本次请求的世代，完成时校验是否已被换层作废

        var apiKey = config.OnlineApiKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // 没配置 api_key：视为预定失败，交回主线程处理（会本地忽略后继续）
            reserveSucceeded = false;
            reserveHasResult = true;
            reserveInFlight = false;
            return;
        }

        var territory = clientState.TerritoryType;
        var url = $"{OnlineServerUrl}/ignored/reserve";
        var payload = new OnlineIgnoredAddRequest
        {
            ApiKey = apiKey,
            Territory = territory,
            Keys = new[] { key }
        };

        _ = Task.Run(async () =>
        {
            var ok = false;
            try
            {
                // 给预定请求一个较短的超时，避免服务器不可达时把 reserveInFlight 卡住到 HttpClient 默认 100s，
                // 那会让整层盲踩长时间不再预定新点。
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await httpClient.PostAsync(url, content, cts.Token).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    if (config.devMode)
                        log.Warning("[AutoPalExplorer] 联机盲踩：reserve HTTP {Code}", resp.StatusCode);
                }
                else
                {
                    var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    var dto = JsonSerializer.Deserialize<OnlineIgnoredReserveResponse>(text);
                    if (dto is null)
                    {
                        if (config.devMode)
                            log.Warning("[AutoPalExplorer] 联机盲踩：reserve 解析失败，返回为空。");
                    }
                    else if (!dto.Ok)
                    {
                        if (config.devMode)
                            log.Information("[AutoPalExplorer] 联机盲踩：reserve 被拒绝，Taken={TakenCount}，Key={Key}",
                                dto.Taken?.Length ?? 0, key);
                    }
                    else
                    {
                        ok = true;
                        // 成功抢到：本地也加入 ignoredBlindLocations，避免后面再选到
                        lock (ignoredBlindLocations)
                        {
                            ignoredBlindLocations.Add(key);
                        }

                        if (config.devMode)
                            log.Information("[AutoPalExplorer] 联机盲踩：reserve 成功，Key={Key}，服务器总数={Count}",
                                key, dto.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                if (config.devMode)
                    log.Warning($"[AutoPalExplorer] 联机盲踩：reserve 异常：{ex.Message}");
            }
            finally
            {
                // 换层/重置已经作废了这次请求：丢弃结果，也不要动状态位
                // （否则可能覆盖掉换层后新发起的请求）
                if (gen == reserveGeneration)
                {
                    // 顺序很重要：先写结果，最后放开 inflight（volatile release 保证主线程可见性）
                    reserveSucceeded = ok;
                    reserveHasResult = true;
                    reserveInFlight = false;
                }
            }
        });
    }

    private void TickOnlineIgnoredSync()
    {
        if (!IsOnlineMode)
            return;

        // 没填 api_key 就不走联机逻辑
        var apiKey = config.OnlineApiKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        // 防止刷屏请求：每 2 秒一次，同时只允许一个请求在路上
        // var now = DateTime.UtcNow;
        // if (onlineSyncInProgress || now < nextOnlineFetchAt)
        //     return;

        // onlineSyncInProgress = true;
        // nextOnlineFetchAt = now.AddSeconds(2);

        var territory = clientState.TerritoryType;
        var url = $"{OnlineServerUrl}/ignored?territory={territory}&api_key={Uri.EscapeDataString(apiKey)}";

        _ = Task.Run(async () =>
        {
            try
            {
                using var resp = await httpClient.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    if (config.devMode)
                        log.Warning("[AutoPalExplorer] 联机盲踩：GET ignored 返回 {Code}", resp.StatusCode);
                    return;
                }

                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                // log.Warning(text);
                var dto = JsonSerializer.Deserialize<OnlineIgnoredGetResponse>(text);
                // log.Warning(dto.Keys.Length.ToString());
                if (dto == null)
                {
                    log.Warning("dto null");
                    return;
                }

                lock (ignoredBlindLocations)
                {
                    ignoredBlindLocations.Clear();
                    foreach (var k in dto.Keys)
                        ignoredBlindLocations.Add(k);
                }

                if (config.devMode)
                    log.Information("[AutoPalExplorer] 联机盲踩：从服务器同步 {Count} 个 ignoredBlindLocations。", dto.Keys.Length);
            }
            catch (Exception ex)
            {
                if (config.devMode)
                    log.Warning($"[AutoPalExplorer] 联机盲踩：同步 ignored 失败：{ex.Message}");
            }
            finally
            {
                onlineSyncInProgress = false;
            }
        });
    }
}
