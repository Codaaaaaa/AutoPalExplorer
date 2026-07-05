using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoPalExplorer.Debug;

/// <summary>
/// Pomander 调试器：
/// - Hook ActionManager.UseAction，抓 Pomander 的真实调用参数；
/// - 提供 UsePomander(actionId) 复用这些参数。
/// 放在 Debug/ 里，独立于正式 PomanderManager。
/// </summary>
public unsafe sealed class PomanderDebuggerEx : IDisposable
{
    private readonly IPluginLog log;
    private readonly IObjectTable objectTable;
    private readonly Hook<UseActionDelegate>? hook;
    private readonly Dictionary<uint, CapturedUse> captured = new();

    // 你目前关心的 Pomander ActionId；先加 6870，后面需要可以再加。
    private static readonly HashSet<uint> PomanderActions = new()
    {
        6870, // 你确认的 Action Id
    };

    private delegate bool UseActionDelegate(
        ActionManager* self,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint a4,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        bool* outOptAreaTargeted
    );

    private readonly struct CapturedUse
    {
        public readonly ActionType Type;
        public readonly ulong TargetId;
        public readonly uint A4;
        public readonly ActionManager.UseActionMode Mode;

        public CapturedUse(ActionType type, ulong targetId, uint a4, ActionManager.UseActionMode mode)
        {
            Type = type;
            TargetId = targetId;
            A4 = a4;
            Mode = mode;
        }
    }

    public PomanderDebuggerEx(IPluginLog log, IGameInteropProvider interop, IObjectTable objectTable)
    {
        this.log = log;
        this.objectTable = objectTable;

        var ptr = (nint)ActionManager.Addresses.UseAction.Value;
        if (ptr == nint.Zero)
        {
            log.Error("[PomanderDebuggerEx] ActionManager.UseAction address is 0. Hook not created.");
            return;
        }

        hook = interop.HookFromAddress<UseActionDelegate>(ptr, Detour);
        hook.Enable();

        log.Information("[PomanderDebuggerEx] Hook enabled on ActionManager.UseAction.");
    }

    private bool Detour(
        ActionManager* self,
        ActionType type,
        uint id,
        ulong targetId,
        uint a4,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        bool* outOptAreaTargeted)
    {
        if (PomanderActions.Contains(id))
        {
            log.Information(
                $"[PomanderDebuggerEx][Captured] id={id}, type={type}, target=0x{targetId:X}, a4={a4}, mode={mode}, combo={comboRouteId}");

            captured[id] = new CapturedUse(type, targetId, a4, mode);
        }

        return hook!.Original(self, type, id, targetId, a4, mode, comboRouteId, outOptAreaTargeted);
    }

    /// <summary>
    /// 使用指定 Pomander（通过 ActionId）。
    /// 优先用抓到的真实参数；没有的话用保守兜底。
    /// </summary>
    public bool UsePomander(uint actionId)
    {
        var am = ActionManager.Instance();
        if (am == null)
        {
            log.Warning("[PomanderDebuggerEx] ActionManager.Instance() is null.");
            return false;
        }

        CapturedUse info;
        if (!captured.TryGetValue(actionId, out info))
        {
            // 兜底参数（仅在你还没手动按过时用）：
            // - type: Action（因为 6870 在 Action 表）
            // - target: 自己（如果拿不到就 0）
            // - mode: Standard
            var targetId = objectTable.LocalPlayer?.GameObjectId ?? 0UL;

            info = new CapturedUse(
                ActionType.Action,
                targetId,
                0,
                ActionManager.UseActionMode.Queue // 注意：是 Standard，不是 Normal
            );

            log.Warning(
                $"[PomanderDebuggerEx] No captured params for {actionId}, using fallback: " +
                $"type={info.Type}, target=0x{info.TargetId:X}, a4={info.A4}, mode={info.Mode}");
        }

        bool areaTargeted = false;

        var result = am->UseAction(
            info.Type,
            actionId,
            info.TargetId,
            info.A4,
            info.Mode,
            0,
            &areaTargeted
        );

        log.Information($"[PomanderDebuggerEx] UseAction({info.Type}, {actionId}) => {result}, area={areaTargeted}");
        return result;
    }

    public void Dispose()
    {
        if (hook != null)
        {
            hook.Disable();
            hook.Dispose();
            log.Information("[PomanderDebuggerEx] Hook disposed.");
        }
    }
}
