using System;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AutoPalExplorer.Services;
using AutoPalExplorer.Helpers;

namespace AutoPalExplorer
{
    /// <summary>
    /// 负责 AutoPalExplorer 的配置窗口 UI。
    /// </summary>
    public class ConfigWindow
    {
        private readonly Configuration config;
        private readonly AutoPalController controller;
        private readonly PomanderManager pomanderManager;

        private bool isVisible = false;

        public ConfigWindow(Configuration config, AutoPalController controller, PomanderManager pomanderManager)
        {
            this.config = config;
            this.controller = controller;
            this.pomanderManager = pomanderManager;
        }

        public void Toggle()
        {
            isVisible = !isVisible;
        }

        public void Open()
        {
            isVisible = true;
        }

        public void Close()
        {
            isVisible = false;
        }

        public void Draw()
        {
            if (!isVisible)
                return;

            // 初始大小，只在第一次使用时生效
            ImGui.SetNextWindowSize(new Vector2(420, 260), ImGuiCond.FirstUseEver);

            // ⭐ 移除了 ImGuiWindowFlags.AlwaysAutoResize，让窗口可以拖动改变大小
            if (!ImGui.Begin("AutoPalExplorer", ref isVisible))
            {
                ImGui.End();
                return;
            }

            // ===== 标题 =====
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
            ImGui.TextUnformatted("模式设置:");

            int modeIndex = (int)config.Mode;
            string[] modeLabels = { "自动探索模式", "跟车模式" };
            if (ImGui.Combo("自动化模式", ref modeIndex, modeLabels, modeLabels.Length))
            {
                config.Mode = (AutoMode)modeIndex;
                config.Save();
            }

            ImGui.Separator();
            ImGui.TextUnformatted("战斗设置:");

            // BMRAI 控制
            bool useBmrai = config.UseBmrai;
            if (ImGui.Checkbox("自动使用BossMod/Rotation", ref useBmrai))
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
            ImGui.TextUnformatted("测试:");

            if (ImGui.CollapsingHeader("自动探索参数", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.PushItemWidth(100f);
                ImGui.TextUnformatted("如果你不知道参数的含义，不要修改这里的内容");

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

                // 下一层间隔
                int challengeIntervalSeconds = config.ChallengeIntervalSeconds;
                if (ImGui.DragInt("点击下一层间隔 (s)", ref challengeIntervalSeconds, 1, 1, 10))
                {
                    config.ChallengeIntervalSeconds = Math.Max(1, challengeIntervalSeconds);
                    config.Save();
                }

                // 魔陶器使用间隔
                int pomanderIntervalSeconds = config.PomanderIntervalSeconds;
                if (ImGui.DragInt("魔陶器使用间隔 (ms)", ref pomanderIntervalSeconds, 50, 50, 5000))
                {
                    config.PomanderIntervalSeconds = Math.Max(50, pomanderIntervalSeconds);
                    config.Save();
                }

                ImGui.PopItemWidth();
            }

            if (ImGui.CollapsingHeader("魔陶器数量", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.TextUnformatted("魔陶器数量：");
                ImGui.Spacing();

                if (ImGui.BeginTable("PomanderTable", 2,
                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.WidthStretch, 4.0f);
                    ImGui.TableSetupColumn("数量", ImGuiTableColumnFlags.WidthStretch, 0.7f);

                    ImGui.TableHeadersRow();

                    foreach (var p in pomanderManager.Pomanders)
                    {
                        ImGui.TableNextRow();

                        ImGui.TableSetColumnIndex(0);
                        ImGui.TextUnformatted(p.Keyword);
                        
                        ImGui.TableSetColumnIndex(1);
                        int count = p.Count;
                        if (ImGui.DragInt($"##pomander_count_{p.PomanderType}", ref count, 1, 0, 99))
                        {
                            if (count < 0)
                                count = 0;

                            pomanderManager.SetPomanderCount(p.PomanderType, count);
                        }
                    }

                    ImGui.EndTable();
                }
            }
            
            if (ImGui.CollapsingHeader("玩家 Buff 状态（只读）", ImGuiTreeNodeFlags.DefaultOpen))
            {
                DrawPlayerStatusList();
            }

            ImGui.End();
        }

        private void DrawPlayerStatusList()
        {
            var player = Plugin.ClientState.LocalPlayer;
            if (player == null)
            {
                ImGui.TextUnformatted("玩家不存在");
                return;
            }

            ImGui.TextUnformatted("当前 Buff 列表（StatusId / 名称 / 剩余时间）:");
            ImGui.Spacing();

            if (ImGui.BeginTable("PlayerStatusTable", 3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 60f);
                ImGui.TableSetupColumn("Buff 名称", ImGuiTableColumnFlags.WidthStretch, 3f);
                ImGui.TableSetupColumn("剩余时间 (s)", ImGuiTableColumnFlags.WidthFixed, 120f);

                ImGui.TableHeadersRow();

                foreach (var status in player.StatusList)
                {
                    if (status.StatusId == 0)
                        continue;

                    string name = GetStatusName((ushort)status.StatusId);

                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(status.StatusId.ToString());

                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(name);

                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextUnformatted($"{status.RemainingTime:0.0}");
                }

                ImGui.EndTable();
            }
        }

        private string GetStatusName(ushort statusId)
        {
            try
            {
                var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>();
                var row = sheet?.GetRow(statusId);   // row 是 Status?

                if (row == null)
                    return "(Unknown)";

                // row.Value 才是真正的 struct，可以访问 Name 字段
                return row.Value.Name.ToString() ?? "(Unknown)";
            }
            catch
            {
                return "(Unknown)";
            }
        }


    }
}
