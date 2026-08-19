using System;
using System.Numerics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using Dalamud.Interface;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Net.Http;
using System.Threading.Tasks;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Game.ClientState.Party;
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
        private readonly IPartyList partyList;
        private readonly RoomMapWindow roomMapWindow;
        private bool isVisible = false;
        private IDalamudTextureWrap? logoTexture;
        private bool logoLoadStarted = false;   
        private string? logoError;
        private bool testingOnline = false;
        private string onlineTestResult = string.Empty;
        public ConfigWindow(Configuration config, AutoPalController controller, PomanderManager pomanderManager, IPartyList partyList, RoomMapWindow roomMapWindow)
        {
            this.config = config;
            this.controller = controller;
            this.pomanderManager = pomanderManager;
            this.partyList = partyList;
            this.roomMapWindow = roomMapWindow;
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

            bool usingPomander = config.UsingPomander;
            if (ImGui.Checkbox("使用魔陶器", ref usingPomander))
            {
                config.UsingPomander = usingPomander;
                config.Save();
            }

            // 魔陶器使用范围：只有勾选“使用魔陶器”时才可选，否则禁用
            ImGui.Indent();
            ImGui.BeginDisabled(!config.UsingPomander);
            string[] pomanderModeLabels = { "仅使用强化自身和防御魔陶器", "使用全部魔陶器和杜松香" };
            int pomanderModeIndex = (int)config.PomanderMode;
            if (ImGui.Combo("魔陶器使用范围", ref pomanderModeIndex, pomanderModeLabels, pomanderModeLabels.Length))
            {
                config.PomanderMode = (PomanderUsageMode)pomanderModeIndex;
                config.Save();
            }
            ImGui.EndDisabled();
            ImGui.Unindent();

            DrawRoundConfig();

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
            bool blindChests = config.BlindChests;
            if (ImGui.Checkbox("盲踩模式", ref blindChests))
            {
                config.BlindChests = blindChests;
                config.Save();
            }
            bool blindChestsWithTrap = config.BlindChestsWithTrap;
            ImGui.Indent();
            ImGui.BeginDisabled(!blindChests);
            if (ImGui.Checkbox("狂暴盲踩(排雷)模式 慎选", ref blindChestsWithTrap))
            {
                config.BlindChestsWithTrap = blindChestsWithTrap;
                config.Save();
            }
            ImGui.EndDisabled();
            ImGui.Unindent();

            ImGui.Separator();
            ImGui.TextUnformatted("房间图（读游戏内地图数据，5x5 房间网格）:");

            bool useRoomGraph = config.UseRoomGraph;
            if (ImGui.Checkbox("启用房间图", ref useRoomGraph))
            {
                config.UseRoomGraph = useRoomGraph;
                config.Save();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton(roomMapWindow.IsVisible ? "关闭房间地图" : "打开房间地图"))
                roomMapWindow.Toggle();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("关掉之后所有房间相关功能都回退到原来的贴墙 / 最近点行为。");

            ImGui.Indent();
            ImGui.BeginDisabled(!useRoomGraph);

            bool roomExplore = config.RoomExplore;
            if (ImGui.Checkbox("用房间图探索（替代贴墙）", ref roomExplore))
            {
                config.RoomExplore = roomExplore;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("按最短路去最近的没踩过的房间，优先有宝箱的房间；\n全层踩完后直接去传送装置所在房间。\n房间中心还没标定好时会自动退回贴墙。");

            bool blindRoomOrder = config.BlindRoomOrder;
            if (ImGui.Checkbox("盲踩按房间顺序推进", ref blindRoomOrder))
            {
                config.BlindRoomOrder = blindRoomOrder;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("先把当前房间的候选点搜完，再按最短路去下一个房间。\n启用时不再受「盲踩目标最大允许距离」限制。");

            ImGui.EndDisabled();
            ImGui.Unindent();

            ImGui.Separator();
            ImGui.TextUnformatted("盲踩共享模式:");

            string[] syncModeLabels = { "本地模式（只自己）", "联机模式（小队共享）" };
            int syncModeIndex = (int)config.BlindSyncMode;
            if (ImGui.Combo("盲踩同步模式", ref syncModeIndex, syncModeLabels, syncModeLabels.Length))
            {
                config.BlindSyncMode = (BlindSyncMode)syncModeIndex;
                config.Save();
            }

            // 只有联机模式下才显示服务器配置
            if (config.BlindSyncMode == BlindSyncMode.Online)
            {
                string serverUrl = config.OnlineServerUrl ?? string.Empty;
                if (ImGui.InputText("服务器地址", ref serverUrl, 256))
                {
                    config.OnlineServerUrl = serverUrl;
                    config.Save();
                }

                string apiKey = config.OnlineApiKey ?? string.Empty;
                if (ImGui.InputText("API Key", ref apiKey, 64))
                {
                    config.OnlineApiKey = apiKey;
                    config.Save();
                }

                ImGui.SameLine();
                if (ImGui.SmallButton("测试联机"))
                {
                    _ = TestOnlineConnectivityAsync();
                }

                if (!string.IsNullOrEmpty(onlineTestResult))
                {
                    ImGui.SameLine();
                    ImGui.TextUnformatted(onlineTestResult);
                }
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

                // RadiantCandlestandNearRange
                float radiantNear = config.RadiantCandlestandNearRange;
                if (ImGui.DragFloat("光耀烛台优先距离", ref radiantNear, 1.0f, 0.0f, 200.0f, "%.0f"))
                {
                    config.RadiantCandlestandNearRange = MathF.Max(0.0f, radiantNear);
                    config.Save();
                }

                // BuriedChestDoneRadius
                float blindWaitDuration = config.BlindWaitDuration;
                if (ImGui.DragFloat("盲踩宝藏等待时间", ref blindWaitDuration, 2.0f, 0.5f, 10.0f, "%.1f"))
                {
                    config.BlindWaitDuration = MathF.Max(2.0f, blindWaitDuration);
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

                // ===== 车头：队友进战支援 =====
                bool helpParty = config.HelpPartyInCombat;
                if (ImGui.Checkbox("队友进战时前去支援", ref helpParty))
                {
                    config.HelpPartyInCombat = helpParty;
                    config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("车头(探索)模式：检测到队友进入战斗就停止探索，跑到队友身边帮忙打怪。");

                if (config.HelpPartyInCombat)
                {
                    float helpRadius = config.HelpPartyArriveRadius;
                    if (ImGui.DragFloat("支援到位距离 (m)", ref helpRadius, 0.5f, 0.5f, 30.0f, "%.1f"))
                    {
                        config.HelpPartyArriveRadius = MathF.Max(0.5f, helpRadius);
                        config.Save();
                    }
                }

                // ===== 远程开怪 =====
                // PullRange
                float pullRange = config.PullRange;
                if (ImGui.DragFloat("远程开怪距离 (m)", ref pullRange, 0.5f, 0.0f, 30.0f, "%.1f"))
                {
                    config.PullRange = pullRange;
                    config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("走到怪物这个距离内就停下用远程技能开怪；填 0 表示沿用旧的走到脸上行为。");

                // PullActionCommand（留空则按当前职业自动选开怪技能）
                string pullCmd = config.PullActionCommand ?? string.Empty;
                if (ImGui.InputText("开怪指令(可选,覆盖职业表)", ref pullCmd, 128))
                {
                    config.PullActionCommand = pullCmd;
                    config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("留空则按当前职业自动选开怪技能（骑士投盾/诗人强力射击…）。\n填了则强制用这个指令，例如 /ac \"炽热光辉\" 或某个宏。\n该职业无远程技能（如武僧/龙骑等）时退回走到脸上交给 BMRAI。");

                // PullActionIntervalMs
                int pullInterval = config.PullActionIntervalMs;
                if (ImGui.DragInt("开怪指令间隔 (ms)", ref pullInterval, 50, 200, 5000))
                {
                    config.PullActionIntervalMs = Math.Max(200, pullInterval);
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

                // 盲踩目标最大允许距离
                float blindMaxDistance = config.BlindMaxDistance;
                if (ImGui.DragFloat("盲踩目标最大允许距离", ref blindMaxDistance, 0.1f, 0.3f, 100.0f, "%.0f"))
                {
                    config.BlindMaxDistance = Math.Max(10, blindMaxDistance);
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
                
                ImGui.Spacing();
                ImGui.Separator();
                
                bool buried = pomanderManager.HasBuriedBuff;
                ImGui.TextUnformatted("埋藏宝藏 Buff 状态：");

                if (buried)
                    ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), "已获得");
                else
                    ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "未获得");
            }

            if (ImGui.CollapsingHeader("内部变量", ImGuiTreeNodeFlags.DefaultOpen))
            {
                // === 最近一次 AI 意图 ===
                ImGui.TextUnformatted("最近一次 AI 意图：");
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 0.9f, 1.0f, 1.0f));
                ImGui.TextWrapped(string.IsNullOrEmpty(controller.LastIntent) ? "(无)" : controller.LastIntent);
                ImGui.PopStyleColor();

                if (controller.LastIntentAt != DateTime.MinValue)
                {
                    var ago = DateTime.Now - controller.LastIntentAt;
                    ImGui.TextDisabled($"更新于 {controller.LastIntentAt:HH:mm:ss}（{ago.TotalSeconds:0.0}s 前）");
                }

                ImGui.Spacing();
                ImGui.Separator();

                // === 房间图状态 ===
                ImGui.TextUnformatted("房间图：");
                if (!config.UseRoomGraph)
                {
                    ImGui.TextDisabled("(已关闭)");
                }
                else if (!controller.RoomCentersCalibrated)
                {
                    ImGui.TextDisabled("(未标定 / 不在深层迷宫)");
                }
                else
                {
                    var progress = controller.RoomProgress;
                    ImGui.TextUnformatted($"当前房间 {controller.CurrentRoomIndex}"
                        + (controller.CurrentRoomIndex >= 0
                            ? $"（行 {controller.CurrentRoomIndex / 5} 列 {controller.CurrentRoomIndex % 5}）"
                            : string.Empty));
                    ImGui.TextUnformatted($"已探索 {progress.Revealed} / {progress.Exists} 个房间");
                    ImGui.TextUnformatted($"目标房间 {controller.RoomExploreTarget}（{controller.RoomGoal}）");
                    ImGui.TextUnformatted($"房间间距 X={controller.RoomPitchX:0.0} Z={controller.RoomPitchZ:0.0}"
                        + (controller.RoomPitchSolved ? "（本层实测）" : "（沿用先验）"));
                    ImGui.TextDisabled($"标定样本房间数 {controller.RoomObservedCount}");
                }

                ImGui.Spacing();
                ImGui.Separator();

                bool buried = pomanderManager.HasBuriedBuff;
                ImGui.TextUnformatted("宝藏数量");
                ImGui.TextUnformatted(controller.blindLocations.Count.ToString());
                ImGui.TextUnformatted("所有数量");
                ImGui.TextUnformatted(controller.allBlindLocations.Count.ToString());

                // === ignoredChestIds ===
                ImGui.TextUnformatted($"忽略的宝箱 ID 列表 ({controller.ignoredChestIds.Count}):");

                if (controller.ignoredChestIds.Count == 0)
                {
                    ImGui.TextDisabled("(空)");
                }
                else
                {
                    // 可以加滚动区域防止太多 ID 撑满窗口
                    if (ImGui.BeginChild("IgnoredChestList", new Vector2(0, 150), true))
                    {
                        int index = 1;
                        foreach (var id in controller.ignoredChestIds)
                        {
                            ImGui.TextUnformatted($"{index}. {id}");
                            index++;
                        }
                        ImGui.EndChild();
                    }
                }

                ImGui.Spacing();
                ImGui.Separator();

                // === savedExitPos ===
                var exitPos = controller.savedExitPos;
                if (exitPos.HasValue)
                {
                    ImGui.TextUnformatted("传送点位置:");
                    ImGui.TextUnformatted(FormatVector3(exitPos.Value));
                }
                else
                {
                    ImGui.TextUnformatted("传送点位置: (未记录)");
                }

                // === savedRegenerationPos ===
                var regenPos = controller.savedRegenerationPos;
                if (regenPos.HasValue)
                {
                    ImGui.TextUnformatted("再生位置:");
                    ImGui.TextUnformatted(FormatVector3(regenPos.Value));
                }
                else
                {
                    ImGui.TextUnformatted("再生位置: (未记录)");
                }
            }

            ImGui.End();
        }

        public void DrawSimple()
        {
            // 如果没打开，不画
            if (!isVisible)
                return;

            // 构造一个更小的窗口大小
            ImGui.SetNextWindowSize(new Vector2(300, 120), ImGuiCond.FirstUseEver);

            if (!ImGui.Begin("dadongbei", ref isVisible))
            {
                ImGui.End();
                return;
            }

            if (!logoLoadStarted)
            {
                logoLoadStarted = true;
                _ = LoadLogoAsync(); // fire-and-forget，不阻塞游戏线程
            }
            if (logoTexture != null)
            {
                // 按宽度缩放一下，保持比例
                var size = logoTexture.Size;
                const float maxWidth = 260f;
                if (size.X > maxWidth)
                {
                    float scale = maxWidth / size.X;
                    size *= scale;
                }

                ImGui.Image(logoTexture.Handle, size);
                ImGui.Separator();
            }
            else if (!string.IsNullOrEmpty(logoError))
            {
                ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), logoError);
                ImGui.Separator();
            }
            else
            {
                ImGui.TextUnformatted("正在加载...");
                ImGui.Separator();
            }

            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null)
            {
                ImGui.TextUnformatted("玩家未加载");
                ImGui.End();
                return;
            }

            // 获取 LocalContentId
            var cid = Plugin.PlayerState.ContentId;

            ImGui.TextUnformatted("当前角色 LocalContentId:");
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), cid.ToString());

            ImGui.End();
        }
        private void DrawPlayerStatusList()
        {
            var player = Plugin.ObjectTable.LocalPlayer;
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

        private async Task LoadLogoAsync()
        {
            try
            {
                using var http = new HttpClient();
                var bytes = await http
                    .GetByteArrayAsync("https://raw.githubusercontent.com/Codaaaaaa/Pal/main/AutoPalaceExplorer/logo.png")
                    .ConfigureAwait(false);

                // CreateFromImageAsync 支持 png/jpg/tex 等常见格式
                logoTexture = await Plugin.TextureProvider
                    .CreateFromImageAsync(bytes, "AutoPalExplorer Logo")
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Failed to load logo texture.");
                logoError = "加载 logo 失败 (看 console 详情)";
            }
        }

        private static string FormatVector3(Vector3 v)
        {
            return $"{v.X:F2}, {v.Y:F2}, {v.Z:F2}";
        }
        
        private async Task TestOnlineConnectivityAsync()
        {
            if (testingOnline)
                return;

            testingOnline = true;
            onlineTestResult = "测试中...";

            try
            {
                var urlBase = string.IsNullOrWhiteSpace(config.OnlineServerUrl)
                    ? "http://127.0.0.1:8080"
                    : config.OnlineServerUrl.TrimEnd('/');

                var apiKey = config.OnlineApiKey ?? string.Empty;

                using var http = new HttpClient();
                var url = $"{urlBase}/health?api_key={Uri.EscapeDataString(apiKey)}";

                using var resp = await http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    onlineTestResult = $"失败：HTTP {(int)resp.StatusCode}";
                    return;
                }

                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                // 简单判断一下
                onlineTestResult = text.Contains("ok", StringComparison.OrdinalIgnoreCase)
                    ? "联机正常"
                    : "响应异常";
            }
            catch (Exception ex)
            {
                onlineTestResult = $"异常：{ex.Message}";
            }
            finally
            {
                testingOnline = false;
            }
        }

        private void DrawRoundConfig()
        {
            ImGui.Separator();
            ImGui.TextUnformatted("存档 / 轮次设置:");

            // 使用几号存档（始终生效）
            string[] slotLabels = { "1号存档", "2号存档" };
            int slotIndex = config.SaveSlot == 1 ? 1 : 0;
            if (ImGui.Combo("使用存档", ref slotIndex, slotLabels, slotLabels.Length))
            {
                config.SaveSlot = slotIndex;
                config.Save();
            }

            bool enableRound = config.EnableRoundLimit;
            if (ImGui.Checkbox("启用「打到指定层停止 / 多轮」", ref enableRound))
            {
                config.EnableRoundLimit = enableRound;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("关闭时保持原来的“无限连续刷本”行为。\n开启后：从起始层进本，打到停止层出本删档，等待若干秒重新排本，重复设定轮数后自动停止。");

            ImGui.Indent();
            ImGui.BeginDisabled(!config.EnableRoundLimit);

            // 起始层：1 / 21 / 31 / 51 / 71
            int[] startFloors = { 1, 21, 31, 51, 71 };
            string[] startFloorLabels = { "第1层", "第21层", "第31层", "第51层", "第71层" };
            int startIdx = Array.IndexOf(startFloors, config.StartFloor);
            if (startIdx < 0) startIdx = 0;
            if (ImGui.Combo("起始层", ref startIdx, startFloorLabels, startFloorLabels.Length))
            {
                config.StartFloor = startFloors[startIdx];
                config.Save();
            }

            // 停止层：30 / 50 / 70 / 100，只保留比起始层高的选项
            int[] allStopFloors = { 30, 50, 70, 100 };
            var stopFloorList = new List<int>();
            var stopFloorLabelList = new List<string>();
            foreach (var f in allStopFloors)
            {
                if (f <= config.StartFloor)
                    continue;
                stopFloorList.Add(f);
                stopFloorLabelList.Add($"第{f}层");
            }

            var stopFloors = stopFloorList.ToArray();
            var stopFloorLabels = stopFloorLabelList.ToArray();

            // 当前停止层若不在可选范围（低于/等于起始层）则自动纠正为最低的合法层
            int stopIdx = Array.IndexOf(stopFloors, config.StopFloor);
            if (stopIdx < 0)
            {
                stopIdx = 0;
                config.StopFloor = stopFloors[0];
                config.Save();
            }
            if (ImGui.Combo("停止层", ref stopIdx, stopFloorLabels, stopFloorLabels.Length))
            {
                config.StopFloor = stopFloors[stopIdx];
                config.Save();
            }

            int roundCount = config.RoundCount;
            if (ImGui.DragInt("轮数", ref roundCount, 1, 1, 999))
            {
                config.RoundCount = Math.Max(1, roundCount);
                config.Save();
            }

            int roundWait = config.RoundWaitSeconds;
            if (ImGui.DragInt("每轮等待 (s)", ref roundWait, 1, 0, 120))
            {
                config.RoundWaitSeconds = Math.Max(0, roundWait);
                config.Save();
            }

            ImGui.EndDisabled();
            ImGui.Unindent();
        }

    }
}
