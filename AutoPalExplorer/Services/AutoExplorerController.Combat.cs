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

using FFXIVClientStructs.FFXIV.Client.Game;
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
    /// <summary>
    /// 前往一个敌人并开怪：
    /// - 若配置了远程开怪且已进入 PullRange：停下、锁定目标、按职业远程技能开怪；
    /// - 否则：导航到敌人身边（进战后交给 BMRAI）。
    /// 从“找怪”与“支援队友”两处复用。
    /// </summary>
    private void EngageEnemy(IBattleChara enemy, Vector3 pos, IPlayerCharacter? player, Vector3? currentTarget)
    {
        var ex = enemy.Position.X - pos.X;
        var ez = enemy.Position.Z - pos.Z;
        var edist = MathF.Sqrt(ex * ex + ez * ez);

        // 远程开怪：走进 PullRange 内就停下、锁定目标、用当前职业的远程技能开怪，避免脸开
        var pullCommand = ResolvePullCommand(player);
        if (config.PullRange > 0f && pullCommand is not null && edist <= PullRange)
        {
            SetIntent($"发现怪物：{edist:0.0}m 内远程开怪");

            if (navigator.IsBusy)
                navigator.Stop();

            // 锁定最近的怪作为技能目标
            if (targetManager.Target?.GameObjectId != enemy.GameObjectId)
                targetManager.Target = enemy;

            // 读条技能必须站定才放，否则移动会打断读条：还在移动（减速中）就本帧只停不放，等站稳
            if (IsPlayerMoving)
            {
                SetIntent($"发现怪物：{edist:0.0}m 内，等待站定后开怪");
                if (config.devMode)
                    log.Information("[AutoPalExplorer] 远程开怪：仍在移动(speed={Speed:0.00} m/s)，等待站定后再放技能。", currentSpeedMps);
                return;
            }

            // 节流发开怪指令，避免每帧狂点（进战后由顶部战斗分支交给 BMRAI）
            var now = DateTime.UtcNow;
            if (now >= nextPullActionAt)
            {
                // /ac 是游戏原生指令，必须走聊天框而不是 ProcessCommand
                SendGameChatCommand(pullCommand);
                nextPullActionAt = now.AddMilliseconds(PullActionIntervalMs);

                if (config.devMode)
                    log.Information("[AutoPalExplorer] 远程开怪：目标={Name}, 距离={Dist:0.00}, 执行指令 {Cmd}。",
                        enemy.Name.TextValue, edist, pullCommand);
            }

            return;
        }

        SetIntent("发现怪物：前往并交给 BMRAI");

        if (config.devMode)
            log.Information("[AutoPalExplorer] 找到最近敌人 Name={Name}, 距离={Dist:0.00}，发送导航到敌人位置。",
                enemy.Name.TextValue, edist);

        if (!navigator.IsBusy || IsDifferentTarget(currentTarget, enemy.Position, 1.0f))
        {
            navigator.Stop();
            navigator.TryMoveTo(enemy.Position);
        }
    }

    /// <summary>
    /// 光耀 buff（身上带 status 4708）：只要有这个 buff 就直接使用读条 GCD 44492。
    /// - 不开 BMRAI / rotation（若开着则关掉）；
    /// - 不需要目标、不需要靠近怪物，站定后直接对自身使用 44492；
    /// - 读条会被移动打断，所以先停下、等站稳再放。
    /// 返回 true 表示本帧由该逻辑接管。
    /// </summary>
    private bool HandleRadiantBuff(IPlayerCharacter player)
    {
        if (!PlayerHasStatus(player, RadiantStatusId))
            return false;

        // 不交给自动循环：若之前开了 BMRAI/Rotation，这里一并关掉（有 bmraiOn 守卫，只会触发一次）
        if (bmraiOn)
        {
            EnsureBmraiOff();
            EnsureRotationOff();
        }

        // 停下再放；读条 GCD 被移动打断，还在移动（减速中）就本帧只停不放，等站稳
        if (navigator.IsBusy)
            navigator.Stop();

        if (IsPlayerMoving)
        {
            SetIntent("光耀buff(4708)：等待站定后使用 44492");
            return true;
        }

        // 节流使用 44492
        var now = DateTime.UtcNow;
        if (now >= nextRadiantActionAt)
        {
            SetIntent("光耀buff(4708)：使用 44492");
            UseActionById(RadiantActionId);
            nextRadiantActionAt = now.AddMilliseconds(PullActionIntervalMs);

            if (config.devMode)
                log.Information("[AutoPalExplorer] 光耀buff(4708)：直接使用 Action 44492。");
        }

        return true;
    }

    /// <summary>检查玩家是否带有指定 statusId。</summary>
    private static bool PlayerHasStatus(IPlayerCharacter player, ushort statusId)
    {
        foreach (var s in player.StatusList)
        {
            if (s.StatusId == statusId)
                return true;
        }
        return false;
    }

    /// <summary>按 ActionId 对自身使用技能（用于光耀 buff 的 44492，无需目标）。</summary>
    private unsafe void UseActionById(uint actionId)
    {
        try
        {
            var am = ActionManager.Instance();
            if (am == null)
            {
                if (config.devMode)
                    log.Information("[AutoPalExplorer] UseActionById：ActionManager 实例为空。");
                return;
            }

            am->UseAction(ActionType.Action, actionId);
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoPalExplorer] UseActionById(Action={actionId}) 异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 车头模式支援：如果有队友进入战斗状态，停止当前探索，前去支援。
    /// - 优先直接前往“队友正在交战的一只怪”的位置开打（复用 EngageEnemy，而不是走到队友脚下）；
    /// - 若一时看不到队友在打的怪（可能还没进视野）：先靠近队友把怪带进视野；
    /// - 到队友身边仍找不到可打的怪：待命在旁，不跑去开箱/贴墙。
    /// 返回 true 表示本帧由支援逻辑接管。
    /// </summary>
    private bool TryHelpPartyInCombat(Vector3 pos, IPlayerCharacter? player, Vector3? currentTarget)
    {
        if (!config.HelpPartyInCombat)
            return false;

        var mate = FindNearestInCombatPartyMember(pos);
        if (mate is null)
            return false;

        // 优先：直接去打队友正在交战的怪（去怪的位置，而不是队友的位置）
        var enemy = FindEnemyFightingMember(mate);
        if (enemy is not null)
        {
            SetIntent("车头：支援队友，前往其交战的怪");

            if (config.devMode)
                log.Information("[AutoPalExplorer] [支援] 前往队友 {Mate} 交战的怪 {Enemy}。",
                    mate.Name.TextValue, enemy.Name.TextValue);

            EngageEnemy(enemy, pos, player, currentTarget);
            return true;
        }

        // 看不到队友在打的怪（可能还没进视野/加载）：先靠近队友把怪带进视野
        var dx = mate.Position.X - pos.X;
        var dz = mate.Position.Z - pos.Z;
        var dist = MathF.Sqrt(dx * dx + dz * dz);

        if (dist > HelpPartyArriveRadius)
        {
            SetIntent($"车头：队友进战，前去支援（{dist:0.0}m）");

            if (!navigator.IsBusy || IsDifferentTarget(currentTarget, mate.Position, 1.5f))
            {
                navigator.Stop();
                navigator.TryMoveTo(mate.Position);
            }

            if (config.devMode)
                log.Information("[AutoPalExplorer] [支援] 队友 {Name} 进战，暂未见到其目标怪，先靠近队友，距离={Dist:0.00}。",
                    mate.Name.TextValue, dist);

            return true;
        }

        // 已到队友身边但仍找不到可打的怪：待命在旁，别跑去开箱
        SetIntent("车头：队友进战，待命在旁");
        if (navigator.IsBusy)
            navigator.Stop();

        return true;
    }

    /// <summary>
    /// 找一只“队友正在交战的怪”：
    /// - 优先：正在把队友当目标的怪（谁在打这个队友），取离队友最近的一只；
    /// - 兜底：队友当前锁定的目标怪。
    /// </summary>
    private IBattleChara? FindEnemyFightingMember(IBattleChara mate)
    {
        var mateId = mate.GameObjectId;
        var mateTargetId = mate.TargetObjectId;

        IBattleChara? attackingMate = null;
        var bestDistSq = float.MaxValue;
        IBattleChara? mateTarget = null;

        foreach (var obj in objectTable)
        {
            if (obj is not IBattleChara bc)
                continue;

            if (bc.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)
                continue;

            if (bc is not IBattleNpc bn)
                continue;

            if (bn.BattleNpcKind != Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant)
                continue;

            if (!bc.IsTargetable || bc.CurrentHp <= 0)
                continue;

            // 队友锁定的目标怪（兜底用）
            if (mateTargetId != 0 && bc.GameObjectId == mateTargetId)
                mateTarget = bc;

            // 正在打这个队友的怪：取离队友最近的一只
            if (bc.TargetObjectId == mateId)
            {
                var dx = bc.Position.X - mate.Position.X;
                var dz = bc.Position.Z - mate.Position.Z;
                var distSq = dx * dx + dz * dz;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    attackingMate = bc;
                }
            }
        }

        return attackingMate ?? mateTarget;
    }

    /// <summary>
    /// 找最近的、处于战斗状态且未阵亡的队友（排除自己）。
    /// </summary>
    private IBattleChara? FindNearestInCombatPartyMember(Vector3 from)
    {
        var localId = objectTable.LocalPlayer?.GameObjectId ?? 0;

        IBattleChara? best = null;
        var bestDistSq = float.MaxValue;

        foreach (var member in partyList)
        {
            var obj = member.GameObject;
            if (obj is null)
                continue;

            if (obj.GameObjectId == localId)
                continue;

            if (obj is not IBattleChara bc)
                continue;

            if (bc.IsDead || bc.CurrentHp <= 0)
                continue;

            if (!bc.StatusFlags.HasFlag(StatusFlags.InCombat))
                continue;

            var dx = bc.Position.X - from.X;
            var dz = bc.Position.Z - from.Z;
            var distSq = dx * dx + dz * dz;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = bc;
            }
        }

        return best;
    }

    private IBattleChara? FindNearestEnemy(Vector3 from, float maxDistance)
    {
        IBattleChara? best = null;
        var bestDistSq = maxDistance * maxDistance;

        foreach (var obj in objectTable)
        {
            if (obj is not IBattleChara bc)
                continue;

            if (bc.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)
                continue;

            if (bc is not IBattleNpc bn)
                continue;

            if (bn.BattleNpcKind != Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant)
                continue;

            if (!bc.IsTargetable || bc.CurrentHp <= 0)
                continue;

            var dx = bc.Position.X - from.X;
            var dz = bc.Position.Z - from.Z;
            var distSq = dx * dx + dz * dz;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = bc;
            }
        }

        if (config.devMode)
        {
            if (best is null)
            {
                log.Information("[AutoPalExplorer] FindNearestEnemy：范围内未找到可攻击敌人。");
            }
            else
            {
                var dx = best.Position.X - from.X;
                var dz = best.Position.Z - from.Z;
                var dist = MathF.Sqrt(dx * dx + dz * dz);
                log.Information("[AutoPalExplorer] FindNearestEnemy：最近敌人 Name={Name}, 距离={Dist:0.00}。",
                    best.Name.TextValue, dist);
            }
        }

        return best;
    }

    private bool HasDeadOtherPlayer()
    {
        var localId = objectTable.LocalPlayer?.GameObjectId ?? 0;

        foreach (var ch in objectTable.PlayerObjects)
        {
            if (ch.GameObjectId == localId)
                continue; // 自己死了也没法走过去，就不算在这里

            if (ch.IsDead)
                return true;
        }

        return false;
    }
}
