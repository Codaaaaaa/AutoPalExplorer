using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game;
using AutoPalExplorer.Helpers;
using ECommons.DalamudServices;

namespace AutoPalExplorer.Services;

public sealed class PomanderManager
{
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly ICommandManager commandManager;
    private readonly Configuration config;
    public sealed class PomanderEntry
    {
        public PomanderEntry(string keyword, string pomanderType, int threshold)
        {
            Keyword = keyword;
            PomanderType  = pomanderType;
            Threshold = threshold;
        }

        public string Keyword { get; }
        public string PomanderType { get; }
        public int Threshold { get; }
        public int Count { get; set; }
    }

    private readonly List<PomanderEntry> pomanders = new();
    private long lastCheckTick = 0;

    public PomanderManager(IClientState clientState, IPluginLog log, ICommandManager commandManager, Configuration config)
    {
        this.clientState = clientState;
        this.log = log;
        this.commandManager = commandManager;
        this.config = config;

        pomanders.Add(new PomanderEntry("魔陶器：咒印解除",    "Safety", 1));
        pomanders.Add(new PomanderEntry("魔陶器：全景",        "Sight", 2));
        pomanders.Add(new PomanderEntry("魔陶器：强化自身",    "Strength", 2));
        pomanders.Add(new PomanderEntry("魔陶器：强化防御",    "Steel", 2));
        pomanders.Add(new PomanderEntry("魔陶器：宝箱增加",    "Affluence",    2));
        pomanders.Add(new PomanderEntry("魔陶器：减少敌人",    "Flight",    2));
        pomanders.Add(new PomanderEntry("魔陶器：解咒",        "Purity",    2));
        pomanders.Add(new PomanderEntry("魔陶器：运气上升",    "Fortune",    2));
        pomanders.Add(new PomanderEntry("魔陶器：形态变化",    "Witching",    2));
        pomanders.Add(new PomanderEntry("魔陶器：魔法效果解除", "Serenity",    2));
        pomanders.Add(new PomanderEntry("魔陶器：净化护符",    "PurificationPomander", 2));
        pomanders.Add(new PomanderEntry("魔陶器：加速",        "HastePomander", 2));
        pomanders.Add(new PomanderEntry("魔陶器：朝圣的指引",   "DevotionPomander",    2));
        pomanders.Add(new PomanderEntry("魔陶器：重生", "Raising", 2));
        pomanders.Add(new PomanderEntry("魔陶器：感知宝藏", "Intuition", 1));
    }

    public void Reset()
    {
        foreach (var p in pomanders)
            p.Count = 0;
    }
    public IReadOnlyList<PomanderEntry> Pomanders => pomanders;

    /// <summary>
    /// 每条聊天消息从 Plugin.OnChatMessage 进来。
    /// </summary>
    public void CalculateOnChat(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        // 不在地宫里就清空计数；防止带出
        if (!MapIds.IsPilgrimsTraverse(clientState.TerritoryType))
        {
            Reset();
            return;
        }

        // 我们只关心“获得了魔陶器：XXX！”这种提示
        if (!text.Contains("获得了魔陶器：", StringComparison.Ordinal))
            return;

        foreach (var p in pomanders)
        {
            if (!text.Contains(p.Keyword, StringComparison.Ordinal))
                continue;

            p.Count++;
            log.Information($"[AutoPalExplorer][Pomander] {p.Keyword} -> {p.Count}");

            // 阈值到了就用；用一次扣掉对应层数（例如 3 层消耗 3）
            // while (p.Count >= p.Threshold)
            // {
            //     log.Information("用用用");
            //     // PomanderHelper.TryUsePomander(p.PomanderType);
            //     TryCommand("/pomander " + p.PomanderType);

            //     p.Count -= p.Threshold;
            //     log.Debug($"[AutoPalExplorer][Pomander] 使用 {p.Keyword} (ActionId={p.Id})，剩余计数 {p.Count}");
            // }
        }
    }

    /// <summary>
    /// 何时使用pomander
    /// </summary>
    public void UsingOnChat(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (!text.Contains("打碎了魔陶器：", StringComparison.Ordinal))
            return;
        
        foreach (var p in pomanders)
        {
            if (!text.Contains(p.Keyword, StringComparison.Ordinal))
                continue;

            p.Count--;
            log.Information($"[AutoPalExplorer][Pomander] {p.Keyword} -> {p.Count}");
        }
    }

    /// <summary>
    /// 每 tick 调用，但内部只会每 5 秒真正执行一次逻辑
    /// 1. 检测 debuff 用 Purity / Serenity
    /// 2. 根据阈值自动使用其他 pomander
    /// </summary>
    public void UsingPomander()
    {
        // 节流：每 5 秒检测一次
        var now = Environment.TickCount64;
        if (now - lastCheckTick < config.PomanderIntervalSeconds)
            return;
        lastCheckTick = now;

        // 不在目标地图就不处理
        if (!MapIds.IsPilgrimsTraverse(clientState.TerritoryType))
            return;

        if (clientState.LocalPlayer is not IPlayerCharacter player)
            return;

        // ---- 1. debuff 检测 ----

        // 1.1 诅咒（1087） -> 魔陶器：解咒 (Purity)
        var purityEntry = FindPomanderByType("Purity");
        if (purityEntry != null && purityEntry.Count > 0 && HasStatus(player, DebuffIds.DebuffCurse))
        {
            Svc.Toasts.ShowNormal("检测到 Debuff【诅咒 1087】，自动使用魔陶器：解咒 (Purity)");
            TryUsePomander(purityEntry, "检测到 Debuff【诅咒 1087】，自动使用魔陶器：解咒 (Purity)");
        }

        // 1.2 其他负面魔法（1089/1090/1094/1097）-> 魔陶器：魔法效果解除 (Serenity)
        var serenityEntry = FindPomanderByType("Serenity");
        if (serenityEntry != null && serenityEntry.Count > 0 && HasAnyStatus(player, DebuffIds.DebuffsSerenity))
        {
            Svc.Toasts.ShowNormal("检测到 Debuff【最大体力减少/伤害降低/禁止使用道具/禁止体力自然恢复】，自动使用魔陶器：魔法效果解除 (Serenity)");
            TryUsePomander(serenityEntry, "检测到 Debuff【最大体力减少/伤害降低/禁止使用道具/禁止体力自然恢复】，自动使用魔陶器：魔法效果解除 (Serenity)");
        }

        // ---- 2. 阈值触发逻辑 ----
        // 比如 Affluence 阈值是 2，Count >= 2 就自动使用一次
        // 注意：这里不手动减 Count，等系统 chat 出“打碎了魔陶器：XXX”后 UsingOnChat 会减 1

        foreach (var p in pomanders)
        {
            if (p.Threshold <= 0)
                continue;

            // Purity / Serenity 已经由 debuff 控制，不走阈值逻辑，以免浪费
            if (p.PomanderType == "Purity" || p.PomanderType == "Serenity")
                continue;

            if (p.Count >= p.Threshold)
            {
                Svc.Toasts.ShowNormal($"计数达到阈值 (Count={p.Count}, Threshold={p.Threshold})，自动使用 {p.Keyword} ({p.PomanderType})");
                TryUsePomander(p,
                    $"计数达到阈值 (Count={p.Count}, Threshold={p.Threshold})，自动使用 {p.Keyword} ({p.PomanderType})");

                // 一次检测只用一个，避免一口气连发多个
                break;
            }
        }
    }

    // --- 辅助函数 ---

    private PomanderEntry? FindPomanderByType(string pomanderType)
        => pomanders.Find(p => p.PomanderType == pomanderType);

    /// <summary>
    /// 检查角色是否有指定 statusId
    /// </summary>
    private static bool HasStatus(IPlayerCharacter player, ushort statusId)
    {
        foreach (var s in player.StatusList)
        {
            if (s.StatusId == statusId && s.RemainingTime > 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 检查角色是否命中任意一个 statusId 列表
    /// </summary>
    private static bool HasAnyStatus(IPlayerCharacter player, params ushort[] statusIds)
    {
        foreach (var s in player.StatusList)
        {
            if (s.StatusId == 0 || s.RemainingTime <= 0)
                continue;

            foreach (var id in statusIds)
            {
                if (s.StatusId == id)
                    return true;
            }
        }
        return false;
    }
    private void TryUsePomander(PomanderEntry entry, string reason)
    {
        var command = "/pomander " + entry.PomanderType;
        log.Information($"[AutoPalExplorer][Pomander] {reason}，执行命令：{command}");
        TryCommand(command);
    }
    private void TryCommand(string command)
    {
        try
        {
            commandManager.ProcessCommand(command);
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] Failed to execute command '{command}': {ex.Message}");
        }
    }
}
