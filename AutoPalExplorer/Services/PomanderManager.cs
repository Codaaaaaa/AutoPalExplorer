using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using AutoPalExplorer.Helpers;

namespace AutoPalExplorer.Services;

public sealed class PomanderManager
{
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly ICommandManager commandManager;
    private sealed class PomanderEntry
    {
        public PomanderEntry(string keyword, ActionType type, string pomanderType, int threshold, uint id)
        {
            Keyword = keyword;
            Type = type;
            PomanderType  = pomanderType;
            Threshold = threshold;
            Id = id;
        }

        public string Keyword { get; }
        public ActionType Type { get; }
        public string PomanderType { get; }
        public int Threshold { get; }
        public int Count { get; set; }
        public uint Id { get; set; }
    }

    private readonly List<PomanderEntry> pomanders = new();

    public PomanderManager(IClientState clientState, IPluginLog log, ICommandManager commandManager)
    {
        this.clientState = clientState;
        this.log = log;
        this.commandManager = commandManager;

        // 这里按你的需求：
        // - 所有魔陶器监听
        // - 默认攒到 3 层时自动使用
        // - 感知宝藏只要有 1 个就用
        //
        // 注意：除了你已经确认的感知宝藏 6870，其它 ActionId 我在当前环境下拿不到可靠数据，
        // 不想给你乱编。请你用自己现有的方法（如 Excel 表 / SaintCoinach / 已有插件）把 ID 补上。

        pomanders.Add(new PomanderEntry("魔陶器：咒印解除", ActionType.Action,    "Safety", 2, 1u));
        pomanders.Add(new PomanderEntry("魔陶器：全景", ActionType.Action,        "Sight", 2, 1u));
        pomanders.Add(new PomanderEntry("魔陶器：强化自身", ActionType.Action,    "Strength", 2, 1u));
        pomanders.Add(new PomanderEntry("魔陶器：强化防御", ActionType.Action,    "Steel", 2, 1u));
        pomanders.Add(new PomanderEntry("魔陶器：宝箱增加", ActionType.Action,    "Affluence",    2, 1u));
        pomanders.Add(new PomanderEntry("魔陶器：减少敌人", ActionType.Action,    "Flight",    2, 1u));
        pomanders.Add(new PomanderEntry("魔陶器：解咒", ActionType.Action,        "Purity",    2, 1u));
        pomanders.Add(new PomanderEntry("魔陶器：运气上升", ActionType.Action,    "Fortune",    2, 1u));
        pomanders.Add(new PomanderEntry("魔陶器：形态变化", ActionType.Action,    "Witching",    2, 1u));
        pomanders.Add(new PomanderEntry("魔陶器：魔法效果解除", ActionType.Action, "Serenity",    2, 1u));
        pomanders.Add(new PomanderEntry("魔陶器：净化护符", ActionType.Action,    "PurificationPomander", 2, 1u));
        pomanders.Add(new PomanderEntry("魔陶器：加速", ActionType.Action,        "HastePomander", 2, 1u));
        pomanders.Add(new PomanderEntry("魔陶器：朝圣的指引", ActionType.Action,   "DevotionPomander",    2, 1u));
        pomanders.Add(new PomanderEntry("魔陶器：重生", ActionType.Action, "Raising", 2, 1u));
        
        // 感知宝藏：一层就用。你之前已经确认是 Action #6870，就直接写死。
        pomanders.Add(new PomanderEntry("魔陶器：感知宝藏", ActionType.Action, "Intuition", 1, 1u));
    }

    public void Reset()
    {
        foreach (var p in pomanders)
            p.Count = 0;
    }

    /// <summary>
    /// 每条聊天消息从 Plugin.OnChatMessage 进来。
    /// </summary>
    public void OnChat(string text)
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

            if (p.Id == 0)
            {
                // 没配 ActionId 就只计数，不自动用，避免误炸
                log.Warning($"[AutoPalExplorer][Pomander] {p.Keyword} 未配置 ActionId，跳过自动使用。");
                continue;
            }

            // 阈值到了就用；用一次扣掉对应层数（例如 3 层消耗 3）
            while (p.Count >= p.Threshold)
            {
                log.Information("用用用");
                // PomanderHelper.TryUsePomander(p.PomanderType);
                TryCommand("/pomander " + p.PomanderType);

                p.Count -= p.Threshold;
                log.Debug($"[AutoPalExplorer][Pomander] 使用 {p.Keyword} (ActionId={p.Id})，剩余计数 {p.Count}");
            }
        }
    }

    /// <summary>
    /// 通过 FFXIVClientStructs 调 ActionManager.UseAction
    /// </summary>
    /// 
    private unsafe bool TryUse(PomanderEntry p)
    {
        var am = ActionManager.Instance();
        if (am == null)
        {
            log.Error("[Pomander] ActionManager null");
            return false;
        }

        var status = am->GetActionStatus(p.Type, p.Id);
        log.Information($"[Pomander] {p.Keyword} ({p.Type},{p.Id}) status={status}");

        if (status != 0)
            return false;

        bool area = false;

        var ok = am->UseAction(
                        p.Type,   // 来自 type=Action
                        p.Id,                // 来自 id=6870（就是你说的 ActionId）
                        0,          // 你角色的 GameObjectId
                        0,                   // extraParam (a4)
                        ActionManager.UseActionMode.None,  // mode=None
                        0,                   // comboRouteId
                        &area
                    );
        log.Information($"[Pomander] UseAction({p.Type},{p.Id}) -> {ok}");

        return ok;
    }

    public void TryCommand(string command)
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
