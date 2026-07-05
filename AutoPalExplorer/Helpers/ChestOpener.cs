using System;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace YourPlugin.Helpers
{
    internal static class ChestOpener
    {
        // 距离阈值（游戏里 3 左右就很稳，你可以按自己需求调）
        private const float MaxInteractDistance = 3.0f;

        // 需要从外面注入的 Dalamud 服务
        internal static IObjectTable ObjectTable { get; set; } = null!;
        internal static IClientState ClientState { get; set; } = null!;

        /// <summary>
        /// 在你的 OnFrameworkUpdate / 每帧调用这个方法。
        /// 满足条件时，会自动对最近的宝箱调用交互。
        /// </summary>
        internal static void Tick(Func<bool>? extraCondition = null)
        {
            if (!IsPlayerReady())
                return;

            if (extraCondition != null && !extraCondition())
                return;

            var chest = GetNearestTreasureCofferWithin(MaxInteractDistance);
            if (chest == null)
                return;

            TryInteract(chest);
        }

        private static bool IsPlayerReady()
        {
            if (!ClientState.IsLoggedIn || ObjectTable.LocalPlayer == null)
                return false;

            // 这里可以按需要加战斗中 / 载入中 / 强制移动中等限制
            return true;
        }

        private static IGameObject? GetNearestTreasureCofferWithin(float maxDistance)
        {
            var player = ObjectTable.LocalPlayer!;
            IGameObject? target = null;
            var bestDist = maxDistance * maxDistance; // 用平方避免开根号

            foreach (var obj in ObjectTable)
            {
                if (obj is not IGameObject go)
                    continue;

                if (go.ObjectKind != ObjectKind.Treasure)
                    continue;

                if (!go.IsTargetable)
                    continue;

                var dx = go.Position.X - player.Position.X;
                var dy = go.Position.Y - player.Position.Y;
                var dz = go.Position.Z - player.Position.Z;
                var distSq = dx * dx + dy * dy + dz * dz;

                if (distSq < bestDist)
                {
                    bestDist = distSq;
                    target = go;
                }
            }

            return target;
        }

        private static unsafe void TryInteract(IGameObject chest)
        {
            try
            {
                // Dalamud GameObject -> 原生指针
                var ptr = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)chest.Address;
                if (ptr == null)
                    return;

                var targetSystem = TargetSystem.Instance();
                if (targetSystem == null)
                    return;

                // false = 不强制使用 ground targeting 那一套
                targetSystem->InteractWithObject(ptr, false);
            }
            catch
            {
                // 这里按需要打日志，不要炸插件
            }
        }
    }
}
