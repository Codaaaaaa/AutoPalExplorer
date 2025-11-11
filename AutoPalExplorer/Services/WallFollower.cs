using System;
using System.Numerics;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;

namespace AutoPalExplorer.Services;

/// <summary>
/// 左手贴墙探索器：
/// - 只在没有当前路径时选一个新目标点
/// - 优先：左 -> 前 -> 右 -> 后
/// - 用 Navigator.TryMoveTo 测试有没有路
/// </summary>
public sealed class WallFollower
{
    private readonly IClientState clientState;
    private readonly Navigator navigator;
    private readonly IPluginLog log;

    private bool initialized;
    private Vector2 dir2D; // XZ 平面方向

    private const float Step = 150.0f;
    private const float MaxStep = 150.0f;
    private const float MinMoveSq = 0.25f;

    public WallFollower(IClientState clientState, Navigator navigator, IPluginLog log)
    {
        this.clientState = clientState;
        this.navigator = navigator;
        this.log = log;
    }

    public void Reset()
    {
        initialized = false;
    }

    public bool TryStep()
    {
        var player = clientState.LocalPlayer;
        if (player is null)
            return false;

        var me = player.Position;

        if (!initialized)
            InitDirectionFromPlayer(player);

        var forward = dir2D;
        var left = new Vector2(-forward.Y, forward.X);
        var right = -left;
        var back = -forward;

        if (TryDirection(me, left, ref dir2D)) return true;
        if (TryDirection(me, forward, ref dir2D)) return true;
        if (TryDirection(me, right, ref dir2D)) return true;
        if (TryDirection(me, back, ref dir2D)) return true;

        log.Warning("[AutoPalExplorer] WallFollower: no reachable direction from current position.");
        return false;
    }

    private void InitDirectionFromPlayer(IGameObject player)
    {
        var rot = player.Rotation; // 0 = +Z
        var f = new Vector2(MathF.Sin(rot), MathF.Cos(rot));
        if (f.LengthSquared() < 1e-4f)
            f = new Vector2(0, 1);

        dir2D = Vector2.Normalize(f);
        initialized = true;
        log.Debug($"[AutoPalExplorer] WallFollower init dir = {dir2D}.");
    }

    private bool TryDirection(Vector3 from, Vector2 dir, ref Vector2 dirState)
    {
        dir = Normalize(dir);
        if (dir == Vector2.Zero)
            return false;

        var step = Step;
        while (step <= MaxStep)
        {
            var candidate = new Vector3(
                from.X + dir.X * step,
                from.Y,
                from.Z + dir.Y * step
            );

            if ((candidate - from).LengthSquared() < MinMoveSq)
            {
                step += Step;
                continue;
            }

            if (navigator.TryMoveTo(candidate))
            {
                dirState = dir;
                log.Debug($"[AutoPalExplorer] WallFollower: move dir={dir}, step={step}, target={candidate}");
                return true;
            }

            step += Step;
        }

        return false;
    }

    private static Vector2 Normalize(Vector2 v)
    {
        var len = v.Length();
        return len > 1e-4f ? v / len : Vector2.Zero;
    }
}
