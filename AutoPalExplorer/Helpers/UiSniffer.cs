namespace AutoPalExplorer.Helpers;

using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

public sealed class UiSniffer : IDisposable
{
    private readonly IAddonLifecycle lifecycle;
    private readonly IPluginLog log;
    private bool enabled;

    public UiSniffer(IAddonLifecycle lifecycle, IPluginLog log)
    {
        this.lifecycle = lifecycle;
        this.log = log;
    }

    public void Enable()
    {
        if (enabled) return;
        enabled = true;
        lifecycle.RegisterListener(AddonEvent.PostSetup,  OnAny);
        lifecycle.RegisterListener(AddonEvent.PostUpdate, OnAny);
        lifecycle.RegisterListener(AddonEvent.PostDraw,   OnAny);
        log.Information("[UiSniffer] enabled. Logging addon names…");
    }

    public void Disable()
    {
        if (!enabled) return;
        enabled = false;
        lifecycle.UnregisterListener(OnAny); // 一次性卸载所有事件
        log.Information("[UiSniffer] disabled.");
    }

    private void OnAny(AddonEvent type, AddonArgs args)
    {
        // 这里就能看到“窗口类型/名字”，例如 SelectString / SelectYesno / Talk 等
        log.Information($"[UI] {type} -> {args.AddonName}");
    }

    public void Dispose() => Disable();
}
