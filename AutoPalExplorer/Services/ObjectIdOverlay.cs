using System.Numerics;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui; // 提供 ImGui 包装

namespace AutoPalExplorer.Services;

public sealed class ObjectIdOverlay
{
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly Configuration config;

    public ObjectIdOverlay(IObjectTable objectTable, IGameGui gameGui, Configuration config)
    {
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        this.config = config;
    }

    public void Draw()
    {
        // 只在开发者模式下画，避免平时太吵
        if (!this.config.devMode)
            return;

        var drawList = ImGui.GetForegroundDrawList();

        foreach (var obj in this.objectTable)
        {
            if (obj is null || obj.BaseId == 0)
                continue;

            // 可以按需要过滤：比如只看 EventObj / Treasure / 传送装置
            // if (obj.ObjectKind is not ObjectKind.EventObj)
            //     continue;

            var yOffset = GetYOffset(obj);
            var worldPos = obj.Position + new Vector3(0, yOffset, 0);

            if (!this.gameGui.WorldToScreen(worldPos, out var screenPos))
                continue;

            // 显示 BaseId，如果你想看 DataId 就写 BaseId / Id 的实际字段
            var text = obj.BaseId.ToString() + obj.ObjectKind.ToString() + obj.GameObjectId.ToString();

            // 白色文字：0xAARRGGBB，这里全白不透明
            const uint color = 0xFFFFFFFF;

            drawList.AddText(new Vector2(screenPos.X, screenPos.Y), color, text);
        }
    }

    private static float GetYOffset(IGameObject obj)
    {
        // 粗略头顶偏移，够 debug 用了
        return obj switch
        {
            IBattleNpc => 2.2f,
            _ => 1.5f,
        };
    }
}
