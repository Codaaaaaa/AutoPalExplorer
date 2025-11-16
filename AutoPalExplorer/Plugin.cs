using System;
using System.Text.RegularExpressions;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Numerics;
using System.Collections.Generic;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AutoPalExplorer.Services;
using AutoPalExplorer.Debug;
using AutoPalExplorer.Helpers;

namespace AutoPalExplorer;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "AutoPalExplorer";
    private const string Command = "/autopal";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    private readonly Configuration config;
    private readonly AutoPalController controller;
    private readonly ObjectIdOverlay objectIdOverlay;
    private readonly PomanderManager pomanderManager;
    private PomanderDebuggerEx? pomanderDebuggerEx;
    private UiSniffer? uiSniffer;

    // ⭐ 新增：UI 封装类
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        // 加载配置
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(PluginInterface);

        // 依赖
        var vnavmesh = new VNavmeshClient(PluginInterface, Log);
        var navigator = new Navigator(ClientState, vnavmesh, Log);
        var exitDetector = new ExitDetector(ObjectTable, Log);
        var wallFollower = new WallFollower(ClientState, navigator, Log);
        pomanderManager = new PomanderManager(ClientState, Log, CommandManager, config, ChatGui, Condition);
        pomanderDebuggerEx = new PomanderDebuggerEx(Log, GameInteropProvider, ClientState);

        controller = new AutoPalController(
            ClientState,
            navigator,
            exitDetector,
            wallFollower,
            ObjectTable,
            CommandManager,
            Condition,
            Log,
            config,
            pomanderManager
        );
        objectIdOverlay = new ObjectIdOverlay(ObjectTable, GameGui, config);
        uiSniffer = new UiSniffer(AddonLifecycle, Log);

        // ⭐ 实例化配置窗口
        configWindow = new ConfigWindow(config, controller, pomanderManager);

        CommandManager.AddHandler("/uiwatch", new CommandInfo(OnUiWatch)
        {
            HelpMessage = "Log addon names/types (usage: /uiwatch on | off)"
        });

        Log.Information("[AutoPalExplorer] Plugin loaded.");

        // 注册命令
        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Auto Palace explorer. /autopal [start|stop|toggle]"
        });

        // 注册UI
        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;

        // 每帧更新
        Framework.Update += OnFrameworkUpdate;

        // 监听聊天，用于“传送装置已激活”
        ChatGui.ChatMessage += OnChatMessage;

        // 可选：默认开启
        if (config.EnabledByDefault)
            controller.Start();

        if (config.devMode)
            Log.Information("[AutoPalExplorer] Loaded.");
    }

    private void OnUiWatch(string cmd, string args)
    {
        if (string.Equals(args, "on", StringComparison.OrdinalIgnoreCase))
            uiSniffer?.Enable();
        else if (string.Equals(args, "off", StringComparison.OrdinalIgnoreCase))
            uiSniffer?.Disable();
        else
            Log.Information("Usage: /uiwatch on | off");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // pomanderDebuggerEx?.UsePomander(6268u);
        controller.Update();
    }

    private void OnCommand(string command, string args)
    {
        var arg = args.Trim().ToLowerInvariant();

        switch (arg)
        {
            case "start":
                controller.Start();
                ChatGui.Print("[AutoPalExplorer] Started.");
                break;

            case "stop":
                controller.Stop();
                ChatGui.Print("[AutoPalExplorer] Stopped.");
                break;

            case "config":
                // ⭐ 打开配置窗口
                configWindow.Open();
                break;

            case "toggle":
            case "debug":
                PomanderDebugger.DumpPomanderSheets(DataManager, Log);
                break;

            case "":
                if (controller.IsRunning)
                {
                    controller.Stop();
                    ChatGui.Print("[AutoPalExplorer] Stopped.");
                }
                else
                {
                    controller.Start();
                    ChatGui.Print("[AutoPalExplorer] Started.");
                }
                break;

            default:
                ChatGui.Print("[AutoPalExplorer] Usage: /autopal [start|stop|toggle]");
                break;
        }
    }

    private void OnChatMessage(
        XivChatType type,
        int timestamp,
        ref SeString sender,
        ref SeString message,
        ref bool isHandled)
    {
        var text = message.TextValue;
        if (string.IsNullOrEmpty(text))
            return;

        pomanderManager.CalculateOnChat(text);
        pomanderManager.UsingOnChat(text);

        if (text.Contains("无法获得更多的魔陶器", StringComparison.Ordinal))
        {
            if (config.devMode)
                Log.Information("[AutoPalExplorer] 检测到“无法获得更多的魔陶器”提示，通知控制器忽略当前宝箱。");
            controller.NotifyChestPomanderOverflow();
        }
        
        if (text.Contains("传送装置启动了", StringComparison.OrdinalIgnoreCase))
        {
            if (config.devMode)
                Log.Information("激活");
            controller.NotifyExitActivated();
        }

        if (text.Contains("发现了埋藏的宝藏！", StringComparison.OrdinalIgnoreCase))
        {
            if (config.devMode)
                Log.Information("发现了埋藏的宝藏");
            controller.NotifyBuriedtActivated();
            pomanderManager.NotifyBuriedtBuff();
        }

        if (text.Contains("获得了埋藏的宝藏！", StringComparison.OrdinalIgnoreCase))
        {
            if (config.devMode)
                Log.Information("发现了埋藏的宝藏");
            pomanderManager.ResetBuriedBuff();
        }


        if (text.Contains("成功进行了传送！", StringComparison.OrdinalIgnoreCase))
        {
            if (config.devMode)
                Log.Information("下一层");
            controller.nextLevelActivated();
        }

        if (Regex.IsMatch(text, @"第([1-9]0)朝圣路"))
        {
            controller.NotifyBossFloor();
        }

        if (text.Contains("成功发送了参加申请"))
        {
            controller.NotifyChallengeRequestSent();
        }
    }

    public void Dispose()
    {
        uiSniffer?.Dispose();
        Framework.Update -= OnFrameworkUpdate;
        CommandManager.RemoveHandler(Command);
        ChatGui.ChatMessage -= OnChatMessage;
        pomanderManager.Reset();

        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;

        controller.Stop();
        if (config.devMode)
            Log.Information("[AutoPalExplorer] Disposed.");
    }

    private void ToggleConfigUi()
    {
        configWindow.Toggle();
    }

    private void DrawUi()
    {
        objectIdOverlay.Draw();
        configWindow.Draw();
    }
}
