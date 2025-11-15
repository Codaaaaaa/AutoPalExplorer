using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Game.ClientState.Conditions;

using AutoPalExplorer.Helpers;

namespace AutoPalExplorer.Services;

public sealed class PomanderManager
{
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly IChatGui chatGui;
    private readonly ICommandManager commandManager;
    private readonly ICondition condition;
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
    private bool hasBuriedBuff = false;

    public PomanderManager(IClientState clientState, IPluginLog log, ICommandManager commandManager, Configuration config, IChatGui chatGui, ICondition condition)
    {
        this.clientState = clientState;
        this.log = log;
        this.commandManager = commandManager;
        this.config = config;
        this.chatGui = chatGui;
        this.condition = condition;

        pomanders.Add(new PomanderEntry("魔陶器：咒印解除",    "Safety", 1));
        pomanders.Add(new PomanderEntry("魔陶器：全景",        "Sight", 3));
        pomanders.Add(new PomanderEntry("魔陶器：强化自身",    "Strength", 3));
        pomanders.Add(new PomanderEntry("魔陶器：强化防御",    "Steel", 3));
        pomanders.Add(new PomanderEntry("魔陶器：宝箱增加",    "Affluence",    3));
        pomanders.Add(new PomanderEntry("魔陶器：减少敌人",    "Flight",    3));
        pomanders.Add(new PomanderEntry("魔陶器：改变敌人",    "Alteration",    3));
        pomanders.Add(new PomanderEntry("魔陶器：解咒",        "Purity",    3));
        pomanders.Add(new PomanderEntry("魔陶器：运气上升",    "Fortune",    3));
        pomanders.Add(new PomanderEntry("魔陶器：形态变化",    "Witching",    3));
        pomanders.Add(new PomanderEntry("魔陶器：魔法效果解除", "Serenity",    3));
        pomanders.Add(new PomanderEntry("魔陶器：净化护符",    "PurificationPomander", 3));
        pomanders.Add(new PomanderEntry("魔陶器：加速",        "HastePomander", 3));
        pomanders.Add(new PomanderEntry("魔陶器：朝圣的指引",   "DevotionPomander",    3));
        pomanders.Add(new PomanderEntry("魔陶器：重生", "Raising", 3));
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
            // Svc.Toasts.ShowNormal("检测到 Debuff【诅咒 1087】，自动使用魔陶器：解咒 (Purity)");
            chatGui.Print("[AutoPalExplorer] 检测到 Debuff【诅咒 1087】，自动使用魔陶器：解咒 (Purity)");
            TryUsePomander(purityEntry, "检测到 Debuff【诅咒 1087】，自动使用魔陶器：解咒 (Purity)");
        }

        // 1.2 其他负面魔法（1089/1090/1094/1097）-> 魔陶器：魔法效果解除 (Serenity)
        var serenityEntry = FindPomanderByType("Serenity");
        if (serenityEntry != null && serenityEntry.Count > 0 && HasAnyStatus(player, DebuffIds.DebuffsSerenity))
        {

            chatGui.Print("[AutoPalExplorer] 检测到 Debuff【最大体力减少/伤害降低/禁止使用道具/禁止体力自然恢复】，自动使用魔陶器：魔法效果解除 (Serenity)");
            // Svc.Toasts.ShowNormal("检测到 Debuff【最大体力减少/伤害降低/禁止使用道具/禁止体力自然恢复】，自动使用魔陶器：魔法效果解除 (Serenity)");
            TryUsePomander(serenityEntry, "检测到 Debuff【最大体力减少/伤害降低/禁止使用道具/禁止体力自然恢复】，自动使用魔陶器：魔法效果解除 (Serenity)");
        }

        // 1.3 魔陶器：自身强化 (Strength)
        var strengthEntry = FindPomanderByType("Strength");
        if (strengthEntry != null && strengthEntry.Count > 0 && !HasStatus(player, DebuffIds.StrengthBuff))
        {

            chatGui.Print("[AutoPalExplorer] 自动使用魔陶器：自身强化");
            // Svc.Toasts.ShowNormal("检测到 Debuff【最大体力减少/伤害降低/禁止使用道具/禁止体力自然恢复】，自动使用魔陶器：魔法效果解除 (Serenity)");
            TryUsePomander(strengthEntry, "自动使用魔陶器：自身强化");
        }

        // 1.4 魔陶器：自身防御 (Steel)
        var steelEntry = FindPomanderByType("Steel");
        if (steelEntry != null && steelEntry.Count > 0 && !HasStatus(player, DebuffIds.SteelBuff))
        {

            chatGui.Print("[AutoPalExplorer] 自动使用魔陶器：自身防御");
            // Svc.Toasts.ShowNormal("检测到 Debuff【最大体力减少/伤害降低/禁止使用道具/禁止体力自然恢复】，自动使用魔陶器：魔法效果解除 (Serenity)");
            TryUsePomander(steelEntry, "自动使用魔陶器：自身强化");
        }

        // 1.5 如果血量低于40%并且在战斗状态中，使用魔陶器：形态变化
        var witchingEntry = FindPomanderByType("Witching");
        if (witchingEntry != null && witchingEntry.Count > 0)
        {
            var currentHp = player.CurrentHp;
            var maxHp = player.MaxHp;

            if (maxHp > 0)
            {
                float hpPercent = (float)currentHp / maxHp;

                if (hpPercent <= 0.40f)
                {
                    var inCombat = condition[ConditionFlag.InCombat];
                    if (inCombat)
                    {
                        chatGui.Print($"[AutoPalExplorer] 当前血量 {hpPercent:P0}，战斗中，自动使用魔陶器：形态变化 (Witching)");
                        // Svc.Toasts.ShowWarning(
                        //     $"当前血量 {hpPercent:P0}，战斗中，自动使用魔陶器：形态变化 (Witching)");
                        TryUsePomander(
                            witchingEntry,
                            $"当前血量 {hpPercent:P0}，战斗中，自动使用魔陶器：形态变化 (Witching)");
                    }
                }
            }
        }
        // ---- 2. 阈值触发逻辑 ----
        // 比如 Affluence 阈值是 2，Count >= 2 就自动使用一次
        // 注意：这里不手动减 Count，等系统 chat 出“打碎了魔陶器：XXX”后 UsingOnChat 会减 1

        foreach (var p in pomanders)
        {
            if (p.Threshold <= 0)
                continue;

            // Purity / Serenity 已经由 debuff 控制，不走阈值逻辑，以免浪费
            // if (p.PomanderType == "Purity" || p.PomanderType == "Serenity")
            //     continue;

            if (p.Count >= p.Threshold)
            {
                // 如果身上已经有了埋藏的宝藏BUFF，不使用
                if (p.PomanderType == "Intuition" && hasBuriedBuff)
                    continue;

                chatGui.Print($"计数达到阈值 (Count={p.Count}, Threshold={p.Threshold})，自动使用 {p.Keyword} ({p.PomanderType})");
                // Svc.Toasts.ShowNormal($"计数达到阈值 (Count={p.Count}, Threshold={p.Threshold})，自动使用 {p.Keyword} ({p.PomanderType})");
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
            if (s.StatusId == statusId)
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
            if (s.StatusId == 0)
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

    public void NotifyBuriedtBuff()
    {
        hasBuriedBuff = true;
        if (config.devMode)
            log.Information("[AutoPalExplorer] 收到埋藏的宝藏Buff。");
    }

    public void ResetBuriedBuff()
    {
        hasBuriedBuff = false;
        if (config.devMode)
            log.Information("[AutoPalExplorer] 重置埋藏的宝藏Buff。");
    }

    public void ResetBuff()
    {
        // hasBuriedBuff = false;
        if (config.devMode)
            log.Information("[AutoPalExplorer] 重置Buff。");
    }

    public void SetPomanderCount(string pomanderType, int newCount)
{
    var entry = pomanders.Find(p => p.PomanderType == pomanderType);
    if (entry != null)
    {
        entry.Count = Math.Max(0, newCount);
    }
}
}
