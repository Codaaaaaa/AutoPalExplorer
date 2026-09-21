using System;
using System.Collections.Generic;
using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;

using static ECommons.GenericHelpers;

namespace AutoPalExplorer.Services;

/// <summary>
/// 传送装置激活检测（实时判定）。
///
/// 真值来源按可靠度排：
///   1. 游戏内 InstanceContentDeepDungeon.PassageProgress —— 每帧可读、不依赖任何 UI，
///      地图上那个传送装置图标本来就是按这个值画的，所以这是最靠谱的一手数据；
///   2. DeepDungeonMap 里的传送装置图标（PartId==10 视为满格）：
///      Res Node 1 -> Res Node 16 -> Component Node 18 -> Image Node 2；
///   3. 聊天“传送装置启动了”（见 Plugin.OnChatMessage -> NotifyExitActivated），队友触发时可能收不到。
///
/// ⚠ 这里是「实时状态」而不是「一次性锁存」：以前判定一次激活后就再也不轮询、也永远不会写回未激活，
/// 只能靠聊天“第N朝圣路”触发的换层重置来清。打完 Boss 排队进 11/21/31… 层时那行聊天不一定收得到，
/// 于是上一层“已激活”的结论被带进新一层，开完宝箱就直接跑去传送装置——本文件连同
/// TickFloorChangeWatchdog（换层看门狗）一起修掉这个问题。
///
/// 扫不到节点时，配置窗口「内部变量 -> 传送装置检测」里会显示卡在哪一步，
/// 并可以一键 dump 当前 Addon 的完整节点树 / 已加载 Addon 列表来核对 id。
/// </summary>
public sealed partial class AutoPalController
{
    private const string ExitMapAddonName = "DeepDungeonMap";

    // 节点路径：ResNode 1 -> ResNode 16 -> ComponentNode 18 -> ImageNode 2
    // 注意 16 是普通 ResNode（子节点走 ChildNode 链表），18 才是组件节点（子节点在组件 uld 里），
    // 所以每一跳都用 FindDescendantById 自适应，不能一律按组件查。
    private const uint ExitMapRootNodeId = 1;
    private const uint ExitMapNode16Id = 16;
    private const uint ExitMapNode18Id = 18;
    private const uint ExitMapImageNodeId = 2;

    /// <summary>图标 PartId == 10 表示传送装置已激活。</summary>
    private const uint ExitActivePartId = 10;

    private const double ExitAddonPollSeconds = 0.25;
    private DateTime nextExitAddonPollAt = DateTime.MinValue;

    // devMode 下只在 PartId 变化时打日志，避免刷屏
    private int lastLoggedExitPartId = -1;
    private int lastLoggedPassageProgress = -1;

    /// <summary>
    /// PassageProgress 的满值。不同版本这个字段的量程可能是 0-10 或 0-100，
    /// 默认按 100 算；只要某一帧同时读到了地图图标满格（PartId==10），
    /// 就用当时的 progress 把满值标定成真实量程。
    /// </summary>
    private int passageFullProgress = 100;

    /// <summary>地图图标最近一次给出的结论（null = 一次都没读到，读不到时沿用上一次）。</summary>
    private bool? exitIconActive;

    /// <summary>地图图标读取失败时卡在哪一步（null = 上一次读成功了）。</summary>
    private string? exitIconFailReason;

    // ===== 调试用（配置窗口「内部变量」里显示）=====

    /// <summary>最近一次检测的结果 / 失败原因。</summary>
    public string ExitDetectStatus { get; private set; } = "(尚未检测)";

    /// <summary>最近一次检测的时间。</summary>
    public DateTime ExitDetectStatusAt { get; private set; } = DateTime.MinValue;

    /// <summary>最近一次成功读到的 PartId，-1 表示没读到。</summary>
    public int ExitDetectPartId { get; private set; } = -1;

    /// <summary>最近一次读到的 PassageProgress，-1 表示没读到。</summary>
    public int ExitPassageProgress { get; private set; } = -1;

    /// <summary>当前认定的 PassageProgress 满值（激活阈值）。</summary>
    public int ExitPassageFullProgress => passageFullProgress;

    /// <summary>传送装置当前是否已激活（实时）。</summary>
    public bool ExitActivatedNow => exitDetector.ExitActivated;

    /// <summary>手动 dump 出来的节点树 / Addon 列表。</summary>
    public IReadOnlyList<string> ExitDebugDumpLines => exitDebugDumpLines;
    private readonly List<string> exitDebugDumpLines = new();

    /// <summary>换层时清掉本层的检测结论（由 ResetStaticObjectsState 调用）。</summary>
    private void ResetExitActivationState()
    {
        exitIconActive = null;
        exitIconFailReason = null;
        ExitDetectPartId = -1;
        ExitPassageProgress = -1;
        lastLoggedExitPartId = -1;
        lastLoggedPassageProgress = -1;
        nextExitAddonPollAt = DateTime.MinValue;
        SetExitDetectStatus("(换层，等待重新检测)");
    }

    /// <summary>
    /// 每帧调用：重新判定一次传送装置是否已激活，并把结论实时写回 ExitDetector。
    /// 三个来源取「或」——任何一个说激活就算激活，全都说没激活（或全都读不到）才算没激活。
    /// </summary>
    private void TickExitActivation()
    {
        var now = DateTime.Now;

        // 1) 游戏数据：便宜，每帧都读
        var hasProgress = RoomGraph.TryGetPassageProgress(out var progress);
        ExitPassageProgress = hasProgress ? progress : -1;

        // 2) 地图 UI 图标：要遍历节点树，按 0.25s 节流；读不到时沿用上一次结论
        var hasPartId = false;
        uint partId = 0;
        if (now >= nextExitAddonPollAt)
        {
            nextExitAddonPollAt = now.AddSeconds(ExitAddonPollSeconds);
            hasPartId = TryReadExitIconPartId(out partId);
            if (hasPartId)
            {
                ExitDetectPartId = (int)partId;
                exitIconActive = partId == ExitActivePartId;

                if (config.devMode && (int)partId != lastLoggedExitPartId)
                {
                    lastLoggedExitPartId = (int)partId;
                    log.Information("[AutoPalExplorer] [传送检测] {Addon} 图标 PartId={PartId}。", ExitMapAddonName, partId);
                }
            }
        }

        // 两个数据源同时可读、且图标是满格时，顺便把 progress 的满值标定出来
        if (hasProgress && hasPartId && partId == ExitActivePartId && progress > 0 && progress != passageFullProgress)
        {
            log.Information("[AutoPalExplorer] [传送检测] 标定 PassageProgress 满值：{Old} -> {New}（图标 PartId=10）。",
                passageFullProgress, progress);
            passageFullProgress = progress;
        }

        var progressActive = hasProgress && progress >= passageFullProgress;
        var active = progressActive || exitIconActive == true || exitActivatedByChat;

        if (config.devMode && hasProgress && progress != lastLoggedPassageProgress)
        {
            lastLoggedPassageProgress = progress;
            log.Information("[AutoPalExplorer] [传送检测] PassageProgress={Progress}/{Full}。", progress, passageFullProgress);
        }

        SetExitDetectStatus(BuildExitDetectStatus(hasProgress, progress, active));
        exitDetector.SetExitActivated(active);
    }

    private string BuildExitDetectStatus(bool hasProgress, int progress, bool active)
    {
        var sb = new StringBuilder();
        sb.Append(active ? "√ 已激活" : "× 未激活");
        sb.Append("｜游戏数据 PassageProgress=");
        sb.Append(hasProgress ? $"{progress}/{passageFullProgress}" : "(读不到)");
        sb.Append("｜地图图标=");
        sb.Append(exitIconActive is null
            ? "(读不到)"
            : $"PartId={ExitDetectPartId}（{(exitIconActive == true ? "满格" : "未满")}）");
        if (exitIconFailReason is not null)
            sb.Append($"｜{exitIconFailReason}");
        if (exitActivatedByChat)
            sb.Append("｜聊天已报激活");
        return sb.ToString();
    }

    /// <summary>
    /// 立即执行一次判定（配置窗口的「立即检测一次」按钮走这里）。
    /// </summary>
    public void PollExitActivation()
    {
        nextExitAddonPollAt = DateTime.MinValue;
        TickExitActivation();
    }

    private void SetExitDetectStatus(string status)
    {
        ExitDetectStatus = status;
        ExitDetectStatusAt = DateTime.Now;
    }

    /// <summary>
    /// 按节点路径读出传送装置图标当前的 texture PartId。
    /// 读不到时返回 false，并把卡住的那一步写进 <see cref="exitIconFailReason"/>（会拼进 ExitDetectStatus）。
    /// </summary>
    private unsafe bool TryReadExitIconPartId(out uint partId)
    {
        partId = 0;

        if (!TryGetAddonByName<AtkUnitBase>(ExitMapAddonName, out var addon) || addon == null)
        {
            exitIconFailReason = $"× 找不到 Addon「{ExitMapAddonName}」（未加载）";
            return false;
        }

        if (!IsAddonReady(addon))
        {
            exitIconFailReason = $"× Addon「{ExitMapAddonName}」已加载但未就绪（IsVisible={addon->IsVisible}）";
            return false;
        }

        // Res Node 1：addon 自己的 uld 里应该直接有
        var root = addon->GetNodeById(ExitMapRootNodeId);
        if (root == null)
        {
            exitIconFailReason = $"× 找不到 ResNode {ExitMapRootNodeId}"
                + $"（addon 顶层节点数={addon->UldManager.NodeListCount}）";
            return false;
        }

        // Res Node 16：普通 ResNode，子节点走 ChildNode 链表；addon uld 里一般也能直接查到
        var node16 = addon->GetNodeById(ExitMapNode16Id);
        if (node16 == null)
            node16 = FindDescendantById(root, ExitMapNode16Id);

        if (node16 == null)
        {
            exitIconFailReason = $"× ResNode {ExitMapRootNodeId} 下找不到节点 {ExitMapNode16Id}"
                + $"（{DescribeNode(root)}）";
            return false;
        }

        // Component Node 18
        var node18 = FindDescendantById(node16, ExitMapNode18Id);
        if (node18 == null)
        {
            exitIconFailReason = $"× 节点 {ExitMapNode16Id} 下找不到节点 {ExitMapNode18Id}"
                + $"（{DescribeNode(node16)}）";
            return false;
        }

        // Image Node 2：在组件 18 内部
        var imageNode = FindDescendantById(node18, ExitMapImageNodeId);
        if (imageNode == null)
        {
            exitIconFailReason = $"× 节点 {ExitMapNode18Id} 下找不到节点 {ExitMapImageNodeId}"
                + $"（{DescribeNode(node18)}）";
            return false;
        }

        if (imageNode->Type != NodeType.Image)
        {
            exitIconFailReason = $"× 节点 {ExitMapImageNodeId} 不是 Image 节点（Type={imageNode->Type}）";
            return false;
        }

        partId = ((AtkImageNode*)imageNode)->PartId;
        exitIconFailReason = null;
        return true;
    }

    /// <summary>
    /// 在一个节点下按 NodeId 找子节点，自适应两种情况：
    /// - 组件节点（AtkComponentNode）：子节点在组件自己的 uld 里，走 GetComponentNodeById；
    /// - 普通 ResNode：子节点是 ChildNode / PrevSiblingNode 链表，逐层递归。
    /// </summary>
    private static unsafe AtkResNode* FindDescendantById(AtkResNode* parent, uint id)
    {
        if (parent == null)
            return null;

        // 组件节点：先按组件内部查
        if (parent->GetAsAtkComponentNode() != null)
        {
            var inComponent = GetComponentNodeById(parent, id);
            if (inComponent != null)
                return inComponent;
        }

        // 普通子节点链表：先扫同层，再往下递归（同层优先，避免抓到深处同 id 的节点）
        for (var child = parent->ChildNode; child != null; child = child->PrevSiblingNode)
        {
            if (child->NodeId == id)
                return child;
        }

        for (var child = parent->ChildNode; child != null; child = child->PrevSiblingNode)
        {
            var found = FindDescendantById(child, id);
            if (found != null)
                return found;
        }

        return null;
    }

    /// <summary>失败信息里描述一个节点：类型 + 子节点数，方便对着 dump 排查。</summary>
    private static unsafe string DescribeNode(AtkResNode* node)
    {
        if (node == null)
            return "节点为空";

        var childCount = 0;
        for (var child = node->ChildNode; child != null; child = child->PrevSiblingNode)
            childCount++;

        var comp = node->GetAsAtkComponentNode();
        var componentCount = comp != null && comp->Component != null
            ? comp->Component->UldManager.NodeListCount
            : -1;

        return componentCount >= 0
            ? $"Type={node->Type} 子节点={childCount} 组件内节点={componentCount}"
            : $"Type={node->Type} 子节点={childCount} 非组件节点";
    }

    // ===================== 调试 dump =====================

    /// <summary>把目标 Addon 的完整节点树 dump 到 <see cref="ExitDebugDumpLines"/> 和日志里。</summary>
    public unsafe void DumpExitMapNodeTree()
    {
        exitDebugDumpLines.Clear();

        if (!TryGetAddonByName<AtkUnitBase>(ExitMapAddonName, out var addon) || addon == null)
        {
            exitDebugDumpLines.Add($"找不到 Addon「{ExitMapAddonName}」，先点「列出已加载 Addon」核对名字。");
            LogDump();
            return;
        }

        exitDebugDumpLines.Add($"Addon {ExitMapAddonName}: Ready={IsAddonReady(addon)} Visible={addon->IsVisible} 顶层节点数={addon->UldManager.NodeListCount}");

        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            DumpNode(addon->UldManager.NodeList[i], 1);

        LogDump();
    }

    /// <summary>递归 dump 一个节点（组件节点会进到组件内部），最多 4 层。</summary>
    private unsafe void DumpNode(AtkResNode* node, int depth)
    {
        if (node == null || depth > 4)
            return;

        var indent = new string(' ', depth * 2);
        var extra = string.Empty;
        if (node->Type == NodeType.Image)
            extra = $" PartId={((AtkImageNode*)node)->PartId}";
        else if (node->Type == NodeType.Text)
            extra = $" Text=\"{((AtkTextNode*)node)->NodeText.GetText()}\"";

        exitDebugDumpLines.Add($"{indent}NodeId={node->NodeId} Type={node->Type} Visible={node->IsVisible()}{extra}");

        var comp = node->GetAsAtkComponentNode();
        if (comp == null || comp->Component == null)
            return;

        var count = comp->Component->UldManager.NodeListCount;
        exitDebugDumpLines.Add($"{indent}  -> 组件内节点数={count}");
        for (var i = 0; i < count; i++)
            DumpNode(comp->Component->UldManager.NodeList[i], depth + 1);
    }

    /// <summary>列出当前已加载的所有 Addon 名字，用来核对目标 Addon 到底叫什么。</summary>
    public unsafe void DumpLoadedAddonNames()
    {
        exitDebugDumpLines.Clear();

        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null)
        {
            exitDebugDumpLines.Add("拿不到 RaptureAtkUnitManager。");
            LogDump();
            return;
        }

        var list = &stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
        exitDebugDumpLines.Add($"已加载 Addon 数={list->Count}：");

        for (var i = 0; i < list->Count; i++)
        {
            var unit = list->Entries[i].Value;
            if (unit == null)
                continue;

            exitDebugDumpLines.Add($"  {unit->NameString}  Visible={unit->IsVisible} Ready={IsAddonReady(unit)}");
        }

        LogDump();
    }

    private void LogDump()
    {
        var sb = new StringBuilder();
        foreach (var line in exitDebugDumpLines)
            sb.AppendLine(line);

        log.Information("[AutoPalExplorer] [传送检测][Dump]\n{Dump}", sb.ToString());
    }
}
