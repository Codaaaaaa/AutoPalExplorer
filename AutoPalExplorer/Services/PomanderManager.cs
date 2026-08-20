using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Game.ClientState.Conditions;

using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using static ECommons.GenericHelpers;

using AutoPalExplorer.Helpers;

namespace AutoPalExplorer.Services;

public sealed class PomanderManager
{
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly IChatGui chatGui;
    private readonly ICommandManager commandManager;
    private readonly ICondition condition;
    private readonly Configuration config;
    public sealed class PomanderEntry
    {
        public PomanderEntry(string keyword, string pomanderType, int threshold, int pomanderId)
        {
            Keyword = keyword;
            PomanderType  = pomanderType;
            Threshold = threshold;
            PomanderId = pomanderId;
        }

        public string Keyword { get; }
        public string PomanderType { get; }
        public int Threshold { get; }
        // DeepDungeonItem RowId，用于 DeepDungeonStatus 回调（callback 11）
        public int PomanderId { get; }
        public int Count { get; set; }
    }

    private readonly List<PomanderEntry> pomanders = new();
    private long lastCheckTick = 0;
    private bool hasBuriedBuff = false;
    public bool HasBuriedBuff => hasBuriedBuff;

    /// <summary>财运亨通模式里「魔陶器：感知宝藏」的使用状态。</summary>
    public enum IntuitionRequestState
    {
        Idle,     // 本次进本还没请求过
        Pending,  // 已请求，等 DeepDungeonStatus 面板就绪
        Used,     // 已经真的点下去了
        Missing,  // 面板里没有这个魔陶器
    }

    private IntuitionRequestState intuitionState = IntuitionRequestState.Idle;
    public IntuitionRequestState IntuitionState => intuitionState;

    // 杜松香计数：满 3 个用一次（callback 12, 0），用后 -1
    private int juniperCount = 0;
    private const int JuniperUseThreshold = 3;
    public int JuniperCount => juniperCount;

    // 通过 DeepDungeonStatus 回调使用道具时的序列化任务
    private readonly TaskManager taskManager = new();
    public PomanderManager(IClientState clientState, IObjectTable objectTable, IPluginLog log, ICommandManager commandManager, Configuration config, IChatGui chatGui, ICondition condition)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.log = log;
        this.commandManager = commandManager;
        this.config = config;
        this.chatGui = chatGui;
        this.condition = condition;

        // 第四个参数 PomanderId = DeepDungeonItem RowId（对应 DeepDungeonStatus callback 11 的第二个值）
        pomanders.Add(new PomanderEntry("魔陶器：咒印解除",    "Safety",              1, 1));
        pomanders.Add(new PomanderEntry("魔陶器：全景",        "Sight",               3, 2));
        pomanders.Add(new PomanderEntry("魔陶器：强化自身",    "Strength",            3, 3));
        pomanders.Add(new PomanderEntry("魔陶器：强化防御",    "Steel",               3, 4));
        pomanders.Add(new PomanderEntry("魔陶器：宝箱增加",    "Affluence",           3, 5));
        pomanders.Add(new PomanderEntry("魔陶器：减少敌人",    "Flight",              3, 6));
        pomanders.Add(new PomanderEntry("魔陶器：改变敌人",    "Alteration",          3, 7));
        pomanders.Add(new PomanderEntry("魔陶器：解咒",        "Purity",              3, 8));
        pomanders.Add(new PomanderEntry("魔陶器：运气上升",    "Fortune",             3, 9));
        pomanders.Add(new PomanderEntry("魔陶器：形态变化",    "Witching",            3, 10));
        pomanders.Add(new PomanderEntry("魔陶器：魔法效果解除", "Serenity",            3, 11));
        pomanders.Add(new PomanderEntry("魔陶器：加速",        "HastePomander",       3, 36));
        pomanders.Add(new PomanderEntry("魔陶器：净化护符",    "PurificationPomander", 3, 37));
        pomanders.Add(new PomanderEntry("魔陶器：感知宝藏",    "Intuition",           1, 14));
        pomanders.Add(new PomanderEntry("魔陶器：重生",        "Raising",             3, 15));
        pomanders.Add(new PomanderEntry("魔陶器：朝圣的指引",   "DevotionPomander",    3, 38));
    }
    
    public void Reset()
    {
        foreach (var p in pomanders)
            p.Count = 0;
        juniperCount = 0;
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

        // 杜松香是全队共享池：任何人获得都 +1，任何人点燃（使用）都 -1。

        // 获得：计数 +1（这条消息对全队可见，队友获得也会收到）
        if (text.Contains("获得了杜松香", StringComparison.Ordinal))
        {
            juniperCount++;
            log.Information($"[AutoPalExplorer][Juniper] 获得杜松香 -> {juniperCount}");
            return;
        }

        // 点燃/使用：计数 -1（自己或队友点燃都会收到，例如“xxx点燃杜松香·敏慧召唤了妖灵王的分身！”）
        // 各种变体都含“点燃杜松香”，用子串匹配即可覆盖。
        if (text.Contains("点燃杜松香", StringComparison.Ordinal))
        {
            juniperCount = Math.Max(0, juniperCount - 1);
            log.Information($"[AutoPalExplorer][Juniper] 点燃杜松香 -> {juniperCount}");
            return;
        }

        // 魔陶器数量不再依赖聊天累加，改为每次决策前直接从 DeepDungeonStatus 面板读取。
    }

    /// <summary>
    /// 保留给 Plugin 调用；魔陶器数量现在从 UI 读取，无需再根据聊天扣减。
    /// </summary>
    public void UsingOnChat(string text)
    {
        // no-op：数量以 DeepDungeonStatus 面板为准
    }

    /// <summary>
    /// 每 tick 调用，但内部只会每 5 秒真正执行一次逻辑
    /// 1. 检测 debuff 用 Purity / Serenity
    /// 2. 根据阈值自动使用其他 pomander
    /// </summary>
    public unsafe void UsingPomander()
    {
        // 节流：每 5 秒检测一次
        var now = Environment.TickCount64;
        if (now - lastCheckTick < config.PomanderIntervalSeconds)
            return;
        lastCheckTick = now;

        // 不在目标地图就不处理
        if (!MapIds.IsPilgrimsTraverse(clientState.TerritoryType))
            return;

        if (objectTable.LocalPlayer is not IPlayerCharacter)
            return;

        // 打开 DeepDungeonStatus 面板；就绪后从 UI 读取魔陶器数量再决策使用。
        if (!TryGetAddonByName<AtkUnitBase>("DeepDungeonStatus", out _))
        {
            var agent = AgentDeepDungeonStatus.Instance();
            if (agent != null)
                agent->AgentInterface.Show();
        }

        taskManager.Enqueue(() =>
            TryGetAddonByName<AtkUnitBase>("DeepDungeonStatus", out var a) && IsAddonReady(a));

        taskManager.Enqueue(() =>
        {
            if (!TryGetAddonByName<AtkUnitBase>("DeepDungeonStatus", out var addon) || !IsAddonReady(addon))
                return;
            if (objectTable.LocalPlayer is not IPlayerCharacter player)
                return;

            // 先从面板读取所有魔陶器数量（覆盖旧值），再决策
            ReadPomanderCountsFromAddon(addon);
            RunPomanderDecisions(player);
        });
    }

    /// <summary>
    /// 根据当前数量 / debuff / 阈值决定使用哪些魔陶器和杜松香。
    /// 数量已由 <see cref="ReadPomanderCountsFromAddon"/> 从 UI 读取。
    /// </summary>
    private void RunPomanderDecisions(IPlayerCharacter player)
    {
        // 使用范围：All = 全部魔陶器 + 杜松香；SelfBuffOnly = 仅强化自身(Strength)和强化防御(Steel)
        var useAll = config.PomanderMode == PomanderUsageMode.All;

        // ---- 0. 杜松香：满 3 个用一次（DeepDungeonStatus callback 12, 0）----
        // 注意：不在这里立即 -1。杜松香是全队共享池，使用后游戏会广播“点燃杜松香”消息，
        // 由 CalculateOnChat 统一 -1（自己/队友一视同仁），避免和消息重复扣减。
        // 仅在“使用全部魔陶器和杜松香”模式下才用。
        if (useAll && juniperCount >= JuniperUseThreshold)
        {
            chatGui.Print($"[AutoPalExplorer] 杜松香数量达到 {juniperCount}，自动使用杜松香。");
            UseJuniper();
        }

        // ---- 1. debuff 检测（仅“使用全部”模式）----
        if (useAll)
        {
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

        // 以下（形态变化 + 阈值触发的其余魔陶器）仅在“使用全部魔陶器和杜松香”模式下执行。
        if (!useAll)
            return;

        // 1.5 如果血量低于40%并且在战斗状态中，使用魔陶器：形态变化
        var witchingEntry = FindPomanderByType("Witching");
        if (witchingEntry != null && witchingEntry.Count > 0)
        {
            var currentHp = player.CurrentHp;
            var maxHp = player.MaxHp;

            if (maxHp > 0)
            {
                float hpPercent = (float)currentHp / maxHp;

                if (hpPercent <= 0.50f)
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
        log.Information($"[AutoPalExplorer][Pomander] {reason}，使用魔陶器 {entry.PomanderType} (Id={entry.PomanderId})。");
        FireDeepDungeonStatusCallback(11, entry.PomanderId);
    }

    /// <summary>
    /// 使用杜松香：DeepDungeonStatus callback (12, 0)。
    /// </summary>
    private void UseJuniper()
    {
        log.Information("[AutoPalExplorer][Juniper] 使用杜松香 (callback 12, 0)。");
        FireDeepDungeonStatusCallback(12, 0);
    }

    /// <summary>
    /// 打开（若未打开）DeepDungeonStatus 面板，等待其就绪后触发回调。
    /// 用于使用魔陶器 (11, id) 或杜松香 (12, 0)。
    /// </summary>
    private unsafe void FireDeepDungeonStatusCallback(int category, int value)
    {
        if (!TryGetAddonByName<AtkUnitBase>("DeepDungeonStatus", out _))
        {
            var agent = AgentDeepDungeonStatus.Instance();
            if (agent != null)
                agent->AgentInterface.Show();
        }

        taskManager.Enqueue(() =>
            TryGetAddonByName<AtkUnitBase>("DeepDungeonStatus", out var addon) && IsAddonReady(addon));

        taskManager.Enqueue(() =>
        {
            if (TryGetAddonByName<AtkUnitBase>("DeepDungeonStatus", out var addon))
                Callback.Fire(addon, true, category, value);
        });
    }

    /// <summary>
    /// 从 DeepDungeonStatus 面板读取每个魔陶器的数量并写回 Count。
    /// 面板结构：容器节点 18 里的槽位节点 19~34（顺序与 <see cref="pomanders"/> 一致，19=咒印解除）。
    /// 每个槽位内：Button Component Node(id 2) 不可见 => 0；
    /// 否则读取按钮内 Text Node(id 3) 的文本：空 => 1，数字 => 该数量。
    /// </summary>
    private unsafe void ReadPomanderCountsFromAddon(AtkUnitBase* addon)
    {
        if (addon == null)
            return;

        for (var i = 0; i < pomanders.Count; i++)
        {
            var count = ReadOnePomanderCount(addon, (uint)(19 + i));
            pomanders[i].Count = count;

            if (config.devMode)
                log.Information($"[AutoPalExplorer][Pomander][Read] nodeId={19 + i} {pomanders[i].Keyword} -> {count}");
        }
    }

    private unsafe int ReadOnePomanderCount(AtkUnitBase* addon, uint slotNodeId)
    {
        // 优先直接按 id 取；取不到再从容器节点 18 里找
        var slotNode = addon->GetNodeById(slotNodeId);
        if (slotNode == null)
        {
            var container = addon->GetNodeById(18);
            slotNode = GetComponentChildById(container, slotNodeId);
        }
        if (slotNode == null)
            return 0;

        // (2) Button Component Node
        var buttonNode = GetComponentChildById(slotNode, 2);
        if (buttonNode == null)
            return 0;

        // 不可见 / 不可用 => 0
        if ((buttonNode->NodeFlags & NodeFlags.Visible) == 0)
            return 0;

        // (3) 按钮里的 Text Node
        var textResNode = GetComponentChildById(buttonNode, 3);
        if (textResNode == null)
            return 1; // 有可见按钮但取不到文本，保守视为 1

        var textNode = (AtkTextNode*)textResNode;
        var text = textNode->NodeText.GetText();
        if (string.IsNullOrWhiteSpace(text))
            return 1; // 空 => 1

        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (string.IsNullOrEmpty(digits))
            return 1;

        return int.TryParse(digits, out var n) ? n : 1;
    }

    /// <summary>
    /// 从一个组件节点的内部节点列表中按 id 取子节点。
    /// </summary>
    private static unsafe AtkResNode* GetComponentChildById(AtkResNode* node, uint id)
    {
        if (node == null)
            return null;

        var compNode = node->GetAsAtkComponentNode();
        if (compNode == null || compNode->Component == null)
            return null;

        return compNode->Component->UldManager.SearchNodeById(id);
    }

    /// <summary>
    /// 财运亨通模式：使用「魔陶器：感知宝藏」。
    /// 打开 DeepDungeonStatus 面板 -> 读一次数量 -> 有货才点，结果写在 <see cref="IntuitionState"/> 里。
    /// </summary>
    public unsafe void UseIntuitionPomander()
    {
        intuitionState = IntuitionRequestState.Pending;

        if (!TryGetAddonByName<AtkUnitBase>("DeepDungeonStatus", out _))
        {
            var agent = AgentDeepDungeonStatus.Instance();
            if (agent != null)
                agent->AgentInterface.Show();
        }

        taskManager.Enqueue(() =>
            TryGetAddonByName<AtkUnitBase>("DeepDungeonStatus", out var a) && IsAddonReady(a));

        taskManager.Enqueue(() =>
        {
            if (!TryGetAddonByName<AtkUnitBase>("DeepDungeonStatus", out var addon) || !IsAddonReady(addon))
                return;

            ReadPomanderCountsFromAddon(addon);

            var entry = FindPomanderByType("Intuition");
            if (entry is null || entry.Count <= 0)
            {
                intuitionState = IntuitionRequestState.Missing;
                log.Warning("[AutoPalExplorer][财运亨通] 面板里没有「魔陶器：感知宝藏」。");
                return;
            }

            Callback.Fire(addon, true, 11, entry.PomanderId);
            intuitionState = IntuitionRequestState.Used;
            log.Information("[AutoPalExplorer][财运亨通] 已使用「魔陶器：感知宝藏」（使用前剩余 {Count} 个）。", entry.Count);
        });
    }

    /// <summary>每次重新进本时清掉上一次的感知宝藏使用状态。</summary>
    public void ResetIntuitionState()
    {
        intuitionState = IntuitionRequestState.Idle;
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
