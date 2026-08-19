using System;
using System.Text.RegularExpressions;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Textures.TextureWraps;
using System.Numerics;
using System.Collections.Generic;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Chat;
using FFXIVClientStructs.FFXIV.Component.GUI;

using ECommons;

using AutoPalExplorer.Services;
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
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;

    private readonly Configuration config;
    private readonly AutoPalController controller;
    private readonly PomanderManager pomanderManager;
    private readonly bool isAllowed;
    // ⭐ 新增：UI 封装类
    private readonly ConfigWindow configWindow;
    private readonly RoomMapWindow roomMapWindow;

    public Plugin()
    {
        // 初始化 ECommons（Callback / TaskManager / Addon 辅助）
        ECommonsMain.Init(PluginInterface, this);

        // 白名单检查
        isAllowed = WhiteListCheck.IsPlayerAllowed(PlayerState);

        // 加载配置
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(PluginInterface);

        // 依赖
        var vnavmesh = new VNavmeshClient(PluginInterface, Log);
        var navigator = new Navigator(ObjectTable, vnavmesh, Log);
        var exitDetector = new ExitDetector(ObjectTable, Log);
        var wallFollower = new WallFollower(ObjectTable, navigator, Log);
        pomanderManager = new PomanderManager(ClientState, ObjectTable, Log, CommandManager, config, ChatGui, Condition);

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
            pomanderManager,
            KeyState,
            Framework,
            PartyList,
            TargetManager
        );
        // ⭐ 实例化配置窗口
        roomMapWindow = new RoomMapWindow(controller, config);
        configWindow = new ConfigWindow(config, controller, pomanderManager, PartyList, roomMapWindow);

        Log.Information("[AutoPalExplorer] Plugin loaded.");

        // 注册命令
        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Explorer. /autopal [start|stop|toggle|map]"
        });

        // 注册UI
        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;

        // 每帧更新
        Framework.Update += OnFrameworkUpdate;

        // 监听聊天：魔陶器 / 宝藏 / 层数等；传送装置激活以 UI 节点检测为主，这里的“传送装置启动了”只是兜底
        ChatGui.ChatMessage += OnChatMessage;

        // 可选：默认开启
        if (config.EnabledByDefault)
            controller.Start();
        
        // Log.Info($"DB full path: {Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName!, "palace-pal.data.sqlite3")}");

        if (config.devMode)
            Log.Information("[AutoPalExplorer] Loaded.");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        controller.Update();
    }

    private void OnCommand(string command, string args)
    {
        var arg = args.Trim().ToLowerInvariant();
        if (isAllowed)
        {
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

                case "map":
                    roomMapWindow.Toggle();
                    break;

                case "toggle":
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
                    ChatGui.Print("[AutoPalExplorer] Usage: /autopal [start|stop|toggle|map]");
                    break;
            }
        }
        
    }

    private void OnChatMessage(IHandleableChatMessage chatMessage)
    {
        var text = chatMessage.Message.TextValue;
        if (string.IsNullOrEmpty(text))
            return;

        pomanderManager.CalculateOnChat(text);
        pomanderManager.UsingOnChat(text);

        if (text.Contains("无法获得更多的魔陶器", StringComparison.Ordinal) || text.Contains("无法获得更多的杜松香", StringComparison.Ordinal))
        {
            if (config.devMode)
                Log.Information("[AutoPalExplorer] 检测到“无法获得更多的魔陶器/杜松香”提示，通知控制器忽略当前宝箱。");
            controller.NotifyChestPomanderOverflow();
        }

        if (text.Contains("传送装置启动了", StringComparison.OrdinalIgnoreCase))
        {
            if (config.devMode)
                Log.Information("激活");
            controller.NotifyExitActivated();
        }

        if (text.Contains("再生祭坛开始散发光辉", StringComparison.OrdinalIgnoreCase))
        {
            if (config.devMode)
                Log.Information("再生祭坛激活");
            controller.NotifyRegenerationActivated();
        }

        if (text.Contains("发现了埋藏的宝藏！", StringComparison.OrdinalIgnoreCase))
        {
            if (config.devMode)
                Log.Information("发现了埋藏的宝藏");
            controller.NotifyBuriedtActivated();
            // pomanderManager.NotifyBuriedtBuff();
        }

        if (text.Contains("点亮了光耀烛台", StringComparison.OrdinalIgnoreCase))
        {
            if (config.devMode)
                Log.Information("点亮了光耀烛台");
            controller.NotifyCandleLit();
        }

        if (text.Contains("可以感知到宝藏埋藏的位置了", StringComparison.OrdinalIgnoreCase))
        {
            if (config.devMode)
                Log.Information("可以感知到宝藏埋藏的位置了");
            // controller.NotifyBuriedtActivated();
            pomanderManager.NotifyBuriedtBuff();
        }

        if (text.Contains("获得了埋藏的宝藏！", StringComparison.OrdinalIgnoreCase))
        {
            if (config.devMode)
                Log.Information("发现了埋藏的宝藏");
            pomanderManager.ResetBuriedBuff();
        }


        var levelMatch = Regex.Match(text, @"第(100|[1-9]?[0-9])朝圣路");
        if (levelMatch.Success)
        {
            if (config.devMode)
                Log.Information("下一层");
            if (int.TryParse(levelMatch.Groups[1].Value, out var floor))
                controller.NotifyFloorNumber(floor);
            controller.nextLevelActivated();
        }

        // 整数层（10/20/…/90）是 Boss 房，另外第 99 层也是 Boss 房
        if (Regex.IsMatch(text, @"第([1-9]0|99)朝圣路"))
        {
            controller.NotifyBossFloor();
        }

        if (text.Contains("发送了参加申请"))
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

        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;

        controller.Stop();
        ECommonsMain.Dispose();
        if (config.devMode)
            Log.Information("[AutoPalExplorer] Disposed.");
    }

    private void ToggleConfigUi()
    {
        configWindow.Toggle();
    }

    private void DrawUi()
    {
        if (isAllowed)
        {
            configWindow.Draw();
            roomMapWindow.Draw();
        }
        else
        {
            // Log.Information("Test");
            configWindow.DrawSimple();
        }
    }
}
