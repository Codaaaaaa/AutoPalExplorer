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

            DrawFortuneConfig();

            ImGui.Separator();

            ImGui.BeginDisabled(config.FortuneMode);

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

            ImGui.EndDisabled();

            // 存档槽在财运亨通模式下仍然要能选（要选中那个 1-10 层带感知宝藏的存档），
            // 所以 DrawRoundConfig 自己内部处理禁用，不放在上面的禁用块里。
            DrawRoundConfig();

            ImGui.BeginDisabled(config.FortuneMode);

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

            ImGui.EndDisabled();

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
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("只管「导航目标点」别贴着陷阱：目标太近就往外挪一点。\n路上会不会踩到由下面的「路线绕开陷阱」决定。");

                // AvoidTrapsOnPath
                bool avoidTrapsOnPath = config.AvoidTrapsOnPath;
                if (ImGui.Checkbox("路线绕开陷阱", ref avoidTrapsOnPath))
                {
                    config.AvoidTrapsOnPath = avoidTrapsOnPath;
                    config.Save();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        "把 vnav 算出来的整条路线读回来，穿过陷阱的那一段替换成绕行圆弧再走。\n" +
                        "只在普通模式生效，财运亨通模式全程用传送指令，不受影响。\n" +
                        "狂暴盲踩(排雷)模式里「目标点就是陷阱」的情况不会被绕开，仍然会去踩。");
                }

                if (config.AvoidTrapsOnPath)
                {
                    // TrapPathAvoidRadius
                    float trapPathRadius = config.TrapPathAvoidRadius;
                    if (ImGui.DragFloat("路线绕行半径", ref trapPathRadius, 0.1f, 0.5f, 10.0f, "%.1f"))
                    {
                        config.TrapPathAvoidRadius = MathF.Max(0.5f, trapPathRadius);
                        config.Save();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("绕行时给每个陷阱画的圈有多大（米）。\n调大更安全但更容易在窄通道里绕不过去（绕不过去时自动走原路）。");
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

                ImGui.Spacing();
                ImGui.Separator();

                DrawExitDetectDebug();
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

        /// <summary>
        /// 传送装置激活检测（DeepDungeonMap 图标 PartId）调试面板：
        /// 显示最近一次节点扫描卡在哪一步，并提供 dump 节点树 / Addon 列表的按钮。
        /// </summary>
        private void DrawExitDetectDebug()
        {
            ImGui.TextUnformatted("传送装置检测（UI 节点）：");

            var status = controller.ExitDetectStatus;
            var ok = status.StartsWith("√") || status.StartsWith("已激活");
            ImGui.PushStyleColor(ImGuiCol.Text, ok
                ? new Vector4(0.2f, 1.0f, 0.2f, 1.0f)
                : new Vector4(1.0f, 0.5f, 0.3f, 1.0f));
            ImGui.TextWrapped(status);
            ImGui.PopStyleColor();

            if (controller.ExitDetectStatusAt != DateTime.MinValue)
            {
                var ago = DateTime.Now - controller.ExitDetectStatusAt;
                ImGui.TextDisabled($"更新于 {controller.ExitDetectStatusAt:HH:mm:ss}（{ago.TotalSeconds:0.0}s 前）");
            }

            ImGui.TextDisabled("节点路径 DeepDungeonMap / Res 1 / Res 16 / Comp 18 / Image 2，PartId==10 视为已激活");
            ImGui.TextDisabled($"最近读到的 PartId：{(controller.ExitDetectPartId < 0 ? "(未读到)" : controller.ExitDetectPartId.ToString())}");

            if (ImGui.Button("立即检测一次"))
                controller.PollExitActivation();

            ImGui.SameLine();
            if (ImGui.Button("Dump 节点树"))
                controller.DumpExitMapNodeTree();

            ImGui.SameLine();
            if (ImGui.Button("列出已加载 Addon"))
                controller.DumpLoadedAddonNames();

            var dump = controller.ExitDebugDumpLines;
            if (dump.Count > 0)
            {
                ImGui.TextDisabled($"Dump 结果（{dump.Count} 行，同时也写进了 /xllog）：");
                if (ImGui.BeginChild("ExitNodeDump", new Vector2(0, 220), true,
                        ImGuiWindowFlags.HorizontalScrollbar))
                {
                    foreach (var line in dump)
                        ImGui.TextUnformatted(line);
                    ImGui.EndChild();
                }
            }
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

            ImGui.BeginDisabled(config.FortuneMode);

            bool enableRound = config.EnableRoundLimit;
            if (ImGui.Checkbox("启用「打到指定层停止 / 多轮」", ref enableRound))
            {
                config.EnableRoundLimit = enableRound;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("关闭时保持原来的“无限连续刷本”行为。\n开启后：从起始层进本，打到停止层出本删档，等待若干秒重新排本，重复设定轮数后自动停止。");

            ImGui.Indent();
            ImGui.BeginDisabled(!config.EnableRoundLimit || config.FortuneMode);

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
            ImGui.EndDisabled();

            if (config.FortuneMode)
                ImGui.TextDisabled("（财运亨通模式固定续打同一个存档，轮次设置不生效）");
        }

        /// <summary>
        /// 财运亨通模式：反复进本刷「埋藏的宝藏」。
        /// 队长用感知宝藏 -> 有宝藏就传送过去踩出来 -> 全队退本重进（不重置存档），一直循环。
        /// </summary>
        private void DrawFortuneConfig()
        {
            ImGui.Separator();
            ImGui.TextUnformatted("财运亨通模式:");

            bool fortune = config.FortuneMode;
            if (ImGui.Checkbox("启用财运亨通模式", ref fortune))
            {
                config.FortuneMode = fortune;
                config.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    @"专门发现宝藏的模式

使用前先人工准备：打一个 1-10 层的存档，存档里留着「魔陶器：感知宝藏」，
然后在上面的「使用存档」里选中这个存档（全队都要选自己的那个）。

开启后：队长续打存档进 11 层（不删存档），队员原地等着被带进来；
队长用感知宝藏 —— 没出现「这一朝圣路似乎有宝藏」就全队退本重进；
有宝藏就传送到宝藏点站定（宝藏不在视野里时先逐个房间传送去找），
等宝藏被踩出来后全队退本重进，一直循环到手动暂停。

开启后上面的探索 / 宝箱 / 房间图 / 轮次设置都不生效。");
            }

            if (!config.FortuneMode)
                return;

            ImGui.Indent();

            // ===== 战果计数（从按下 Start 起算）=====
            ImGui.TextUnformatted("累计获得宝藏：");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.2f, 1.0f), controller.FortuneTreasureCount.ToString());
            ImGui.SameLine();
            ImGui.TextDisabled($"（已进本 {controller.FortuneRunCount} 次）");
            ImGui.SameLine();
            if (ImGui.SmallButton("计数清零##fortune"))
                controller.ResetFortuneCounters();

            if (controller.IsRunning)
            {
                ImGui.TextUnformatted("当前阶段：");
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.3f, 0.9f, 1.0f, 1.0f), controller.FortuneStageText);
            }

            if (ImGui.CollapsingHeader("财运亨通参数"))
            {
                ImGui.PushItemWidth(140f);

                string tpCmd = config.FortuneTeleportCommand ?? string.Empty;
                if (ImGui.InputText("传送指令前缀", ref tpCmd, 64))
                {
                    config.FortuneTeleportCommand = tpCmd;
                    config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("最终发出的指令是「前缀 x y z」，默认 /vnav moveto。");

                string enterCmds = config.FortuneEnterCommands ?? string.Empty;
                if (ImGui.InputText("进本指令", ref enterCmds, 256))
                {
                    config.FortuneEnterCommands = enterCmds;
                    config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("进本读条结束、进本缓冲走完后发这些指令，多条用 ; 隔开。\n默认：/i-ching-commander y_adjust -7 true;/i-ching-commander speed 0.3");

                string leaveCmds = config.FortuneLeaveCommands ?? string.Empty;
                if (ImGui.InputText("退本指令", ref leaveCmds, 256))
                {
                    config.FortuneLeaveCommands = leaveCmds;
                    config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("请求退本之前发这些指令，多条用 ; 隔开。\n默认：/i-ching-commander y_adjust 0 true;/i-ching-commander speed 0\n手动按 Stop 时也会补发一次。");

                float yOffset = config.FortuneTeleportYOffset;
                if (ImGui.DragFloat("传送 Y 轴偏移", ref yOffset, 0.5f, 0.0f, 30.0f, "%.1f"))
                {
                    config.FortuneTeleportYOffset = yOffset;
                    config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("传送到「目标 y - 该值」，默认 0（传送指令需要 Y 修正时才改）。");

                int enterDelay = config.FortuneEnterDelaySeconds;
                if (ImGui.DragInt("进本缓冲 (s)", ref enterDelay, 1, 0, 60))
                {
                    config.FortuneEnterDelaySeconds = Math.Max(0, enterDelay);
                    config.Save();
                }

                int reenterDelay = config.FortuneReenterDelaySeconds;
                if (ImGui.DragInt("退本后重进缓冲 (s)", ref reenterDelay, 1, 0, 60))
                {
                    config.FortuneReenterDelaySeconds = Math.Max(0, reenterDelay);
                    config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("退本回到入口后等这么久再去点入口排本。\n填太小会在地宫菜单还没弹出来时就去点，反而更慢。");

                int detectWait = config.FortuneDetectWaitSeconds;
                if (ImGui.DragInt("宝藏检测等待 (s)", ref detectWait, 1, 1, 60))
                {
                    config.FortuneDetectWaitSeconds = Math.Max(1, detectWait);
                    config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("用完感知宝藏后等这么久还没出现宝藏点，就判定本层没宝藏，直接退本重进。");

                int memberWait = config.FortuneMemberExtraWaitSeconds;
                if (ImGui.DragInt("队员额外等待 (s)", ref memberWait, 1, 0, 60))
                {
                    config.FortuneMemberExtraWaitSeconds = Math.Max(0, memberWait);
                    config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("队员不用魔陶器，比队长多等这么久再退本，避免比队长先跑出去。");

                int treasureWait = config.FortuneTreasureWaitSeconds;
                if (ImGui.DragInt("等待宝藏超时 (s)", ref treasureWait, 1, 3, 300))
                {
                    config.FortuneTreasureWaitSeconds = Math.Max(3, treasureWait);
                    config.Save();
                }

                int roomScanWait = config.FortuneRoomScanWaitMs;
                if (ImGui.DragInt("每个房间停留 (ms)", ref roomScanWait, 100, 200, 10000))
                {
                    config.FortuneRoomScanWaitMs = Math.Max(200, roomScanWait);
                    config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("宝藏不在视野里时会逐个房间传送去找，\n每传到一个房间停留这么久等物件加载。");

                int searchTimeout = config.FortuneSearchTimeoutSeconds;
                if (ImGui.DragInt("逐房间搜索超时 (s)", ref searchTimeout, 5, 10, 600))
                {
                    config.FortuneSearchTimeoutSeconds = Math.Max(10, searchTimeout);
                    config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("找这么久还没找到宝藏就放弃这一趟，退本重进。");

                ImGui.PopItemWidth();
            }

            ImGui.Unindent();
        }

    }
}
