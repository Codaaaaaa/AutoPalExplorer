using System;
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
    [PluginService] internal static IGameGui GameGui { get; private set; } = null;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    private bool configWindowVisible = false;
    // private bool autoExploreAdvancedOpen = false;

    private readonly Configuration config;
    private readonly AutoPalController controller;
    private readonly ObjectIdOverlay objectIdOverlay;
    private readonly PomanderManager pomanderManager;
    private PomanderDebuggerEx? pomanderDebuggerEx;

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
        pomanderManager = new PomanderManager(ClientState, Log, CommandManager);
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
            config
        );
        objectIdOverlay = new ObjectIdOverlay(ObjectTable, GameGui, config);

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
                configWindowVisible = true;
                break;
            case "toggle":
            case "debug":
                PomanderDebugger.DumpPomanderSheets(DataManager, Log);
                // PomanderDebugger.DumpItems(DataManager, Log);
                // PomanderDebugger.DumpActions(DataManager, Log);
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

    // 必须精确匹配 IChatGui.OnMessageDelegate:
    // (XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
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

        pomanderManager.OnChat(text);
        // 这里匹配你游戏里的实际提示文本
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
        }

        if (text.Contains("成功进行了传送！", StringComparison.OrdinalIgnoreCase))
        {
            if (config.devMode)
                Log.Information("下一层");
            controller.nextLevelActivated();
        }

        if (text.Contains("第10朝圣路") || text.Contains("第20朝圣路") || text.Contains("第30朝圣路"))
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
        Framework.Update -= OnFrameworkUpdate;
        CommandManager.RemoveHandler(Command);
        ChatGui.ChatMessage -= OnChatMessage;
        pomanderManager.Reset();

        // UI
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;

        controller.Stop();
        if (config.devMode)
            Log.Information("[AutoPalExplorer] Disposed.");
    }
    private void ToggleConfigUi()
    {
        configWindowVisible = !configWindowVisible;
    }

    private void DrawUi()
    {
        objectIdOverlay.Draw();
        if (!configWindowVisible)
            return;

        ImGui.SetNextWindowSize(new Vector2(420, 260), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("AutoPalExplorer", ref configWindowVisible,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        // 标题
        ImGui.TextUnformatted("Auto Palace Explorer");

        // Start / Stop 按钮
        if (controller.IsRunning)
        {
            if (ImGui.Button("Stop##autopal"))
                controller.Stop();
        }
        else
        {
            if (ImGui.Button("Start##autopal"))
                controller.Start();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("战斗设置:");

        // BMRAI 控制
        bool useBmrai = config.UseBmrai;
        if (ImGui.Checkbox("用BossMod和Rotation来自动打怪", ref useBmrai))
        {
            config.UseBmrai = useBmrai;
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("箱子设置:");

        bool openBronze = config.OpenBronzeChests;
        if (ImGui.Checkbox("开启铜箱子", ref openBronze))
        {
            config.OpenBronzeChests = openBronze;
            config.Save();
        }

        bool openSilver = config.OpenSilverChests;
        if (ImGui.Checkbox("开启银箱子", ref openSilver))
        {
            config.OpenSilverChests = openSilver;
            config.Save();
        }

        bool openGold = config.OpenGoldChests;
        if (ImGui.Checkbox("开启金箱子", ref openGold))
        {
            config.OpenGoldChests = openGold;
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("其他:");
        bool devModeStatus = config.devMode;
        if (ImGui.Checkbox("开发者(拉屎)模式", ref devModeStatus))
        {
            config.devMode = devModeStatus;
            config.Save();
        }

        ImGui.Separator();
        if (ImGui.CollapsingHeader("自动探索参数",
                ImGuiTreeNodeFlags.DefaultOpen)) // 想默认收起就去掉 DefaultOpen
        {
            ImGui.PushItemWidth(100f);
            ImGui.TextUnformatted("如果你不知道你在干什么，不要修改这里的内容");
            // ExitStopRadius
            float exitStop = config.ExitStopRadius;
            if (ImGui.DragFloat("激活门停步距离", ref exitStop, 0.1f, 0.1f, 10.0f, "%.1f"))
            {
                config.ExitStopRadius = MathF.Max(0.1f, exitStop);
                config.Save();
            }

            // ChestDoneRadius
            float chestDone = config.ChestDoneRadius;
            if (ImGui.DragFloat("宝箱交互距离", ref chestDone, 0.1f, 0.5f, 10.0f, "%.1f"))
            {
                config.ChestDoneRadius = MathF.Max(0.1f, chestDone);
                config.Save();
            }

            // BuriedChestDoneRadius
            float buriedDone = config.BuriedChestDoneRadius;
            if (ImGui.DragFloat("埋藏宝藏触发半径", ref buriedDone, 0.1f, 0.5f, 10.0f, "%.1f"))
            {
                config.BuriedChestDoneRadius = MathF.Max(0.1f, buriedDone);
                config.Save();
            }

            // InactiveExitNearRadius
            float inactiveNear = config.InactiveExitNearRadius;
            if (ImGui.DragFloat("未激活门附近范围", ref inactiveNear, 0.5f, 1.0f, 30.0f, "%.1f"))
            {
                config.InactiveExitNearRadius = MathF.Max(0.1f, inactiveNear);
                config.Save();
            }

            // EnemySearchRadius
            float enemyRange = config.EnemySearchRadius;
            if (ImGui.DragFloat("找怪范围", ref enemyRange, 10.0f, 10.0f, 1000.0f, "%.0f"))
            {
                config.EnemySearchRadius = MathF.Max(1.0f, enemyRange);
                config.Save();
            }

            // TrapAvoidRadius
            float trapRadius = config.TrapAvoidRadius;
            if (ImGui.DragFloat("陷阱避让半径", ref trapRadius, 0.1f, 0.3f, 10.0f, "%.1f"))
            {
                config.TrapAvoidRadius = MathF.Max(0.1f, trapRadius);
                config.Save();
            }

            // ChestInteractIntervalMs
            int chestInterval = config.ChestInteractIntervalMs;
            if (ImGui.DragInt("开箱节流间隔 (ms)", ref chestInterval, 50, 50, 5000))
            {
                config.ChestInteractIntervalMs = Math.Max(50, chestInterval);
                config.Save();
            }

            ImGui.PopItemWidth();
        }

        ImGui.End();
    }
}
