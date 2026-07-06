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
    private void EnsureBmraiOn()
    {
        if (!config.UseBmrai)
        {
            if (config.devMode)
                log.Information("[AutoPalExplorer] 配置未启用 BMRAI，跳过开启。");
            return;
        }

        if (bmraiOn)
            return;

        bmraiOn = true;
        TryCommand("/bmrai on");
        TryCommand("/rotation Auto");

        if (config.devMode)
            log.Information("[AutoPalExplorer] 已发送 BMRAI 开启指令。");
    }

    private void EnsureBmraiOff()
    {
        if (!bmraiOn)
            return;

        bmraiOn = false;
        TryCommand("/bmrai off");
        // TryCommand("/rotation Off");

        if (config.devMode)
            log.Information("[AutoPalExplorer] 已发送 BMRAI 关闭指令。");
    }

    private void EnsureRotationOff()
    {
        TryCommand("/rotation Off");

        if (config.devMode)
            log.Information("[AutoPalExplorer] 已发送 Rotation 关闭指令。");
    }

    public void TryCommand(string command)
    {
        try
        {
            commandManager.ProcessCommand(command);
            if (config.devMode)
                log.Information("[AutoPalExplorer] 执行指令：{Cmd}", command);
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] 执行指令失败 '{command}': {ex.Message}");
        }
    }

    /// <summary>
    /// 通过游戏原生聊天框发送指令。
    /// 注意：ICommandManager.ProcessCommand 只会执行 Dalamud/插件注册的指令（/bmrai、/vnav…），
    /// 不会执行游戏原生指令（/ac、/action、/merror 等）。开怪用的 /ac 必须走这里。
    /// </summary>
    private void SendGameChatCommand(string command)
    {
        try
        {
            Chat.SendMessage(command);
            if (config.devMode)
                log.Information("[AutoPalExplorer] 发送游戏指令：{Cmd}", command);
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] 发送游戏指令失败 '{command}': {ex.Message}");
        }
    }

    private void StartFollowLoop()
    {
        // 1. 先从配置里拿到要跟随的槽位
        var index = config.FollowPartyIndex;

        if (partyList.Length == 0)
        {
            log.Warning("[AutoPalExplorer] 跟车模式：当前不在队伍中，无法跟随。");
            Stop();
            return;
        }

        if (index < 0 || index >= partyList.Length)
        {
            log.Warning("[AutoPalExplorer] 跟车模式：FollowPartyIndex={Index} 无效（队伍人数={Count}），停止。", index, partyList.Length);
            Stop();
            return;
        }

        var member = partyList[index];
        var actor = member.GameObject;

        if (actor is null)
        {
            log.Warning("[AutoPalExplorer] 跟车模式：选中目标队友 GameObject 为空（可能还没加载），停止。");
            Stop();
            return;
        }

        // 2. 尝试把他设为当前目标（等价于你手动点人）
        try
        {
            targetManager.Target = actor;
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] 跟车模式：设置 Target 失败：{ex}");
            Stop();
            return;
        }

        // 3. 再读一遍当前 Target，确认确实选中了这个人
        if (targetManager.Target is not IGameObject currentTarget ||
            currentTarget.GameObjectId != actor.GameObjectId)
        {
            log.Warning("[AutoPalExplorer] 跟车模式：尝试选中队友失败（Target 不一致），停止。");
            Stop();
            return;
        }

        // 4. 选中成功，发送 /pdr follow
        TryCommand("/pdrfollow");

        if (config.UseBmrai)
        {
            EnsureBmraiOn();
        }

        if (config.devMode)
        {
            log.Information(
                "[AutoPalExplorer] 跟车模式：已选中队友 {Name} 并发送 /pdr follow。",
                member.Name.TextValue
            );
        }
    }

    private void TryChatCommand(string text)
    {
        try
        {
            Plugin.ChatGui.Print(text);

            if (config.devMode)
                log.Information("[AutoPalExplorer] 发送聊天命令：{Cmd}", text);
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] 发送聊天命令失败 '{text}': {ex.Message}");
        }
    }

    private void BreakActWithShift()
    {
        try
        {
            // 按下 Shift
            keyState[VirtualKey.SHIFT] = true;

            // 1 帧后抬起（可以根据需要改成 delayTicks: 2 或 TimeSpan）
            _ = framework.RunOnTick(
                () =>
                {
                    keyState[VirtualKey.SHIFT] = false;
                    if (config.devMode)
                        log.Information("[AutoPalExplorer] 已抬起 Shift，用于打断 ACT/E。");
                },
                delayTicks: 1
            );

            if (config.devMode)
                log.Information("[AutoPalExplorer] 按下 Shift 用于打断 ACT/E。");
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] BreakActWithShift 异常：{ex}");
        }
    }
}
