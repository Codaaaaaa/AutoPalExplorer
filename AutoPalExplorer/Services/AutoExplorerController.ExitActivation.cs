using System;
using System.Collections.Generic;
using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;

using static ECommons.GenericHelpers;

namespace AutoPalExplorer.Services;

/// <summary>
/// 传送装置激活检测（UI 节点版）。
///
/// 以前只靠聊天日志“传送装置启动了”判断，队友触发时聊天可能收不到 / 被过滤。
/// 现改为轮询 DeepDungeonMap 里的传送装置图标：
///   Res Node 1 -> Res Node 16 -> Component Node 18 -> Image Node 2
/// 该 Image Node 的 texture PartId == 10 即表示传送装置已激活。
/// 聊天检测保留为兜底（见 Plugin.OnChatMessage -> NotifyExitActivated）。
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

    // ===== 调试用（配置窗口「内部变量」里显示）=====

    /// <summary>最近一次节点扫描的结果 / 失败原因。</summary>
    public string ExitDetectStatus { get; private set; } = "(尚未检测)";

    /// <summary>最近一次节点扫描的时间。</summary>
    public DateTime ExitDetectStatusAt { get; private set; } = DateTime.MinValue;

    /// <summary>最近一次成功读到的 PartId，-1 表示没读到。</summary>
    public int ExitDetectPartId { get; private set; } = -1;

    /// <summary>手动 dump 出来的节点树 / Addon 列表。</summary>
    public IReadOnlyList<string> ExitDebugDumpLines => exitDebugDumpLines;
    private readonly List<string> exitDebugDumpLines = new();

    /// <summary>每帧调用：轮询 DeepDungeonMap 的传送装置图标，PartId==10 则标记已激活。</summary>
    private void TickExitActivationFromAddon()
    {
        // 已经激活过就不用再读了（换层由 ResetStaticObjectsState / ExitDetector.Reset 清掉）
        if (exitActivatedByChat && exitDetector.ExitActivated)
        {
            SetExitDetectStatus("已激活（本层不再轮询）");
            return;
        }

        var now = DateTime.Now;
        if (now < nextExitAddonPollAt)
            return;
        nextExitAddonPollAt = now.AddSeconds(ExitAddonPollSeconds);

        PollExitActivation();
    }

    /// <summary>
    /// 立即执行一次节点扫描（配置窗口的「立即检测一次」按钮也走这里）。
    /// 命中 PartId==10 时同样会标记激活。
    /// </summary>
    public void PollExitActivation()
    {
        if (!TryReadExitIconPartId(out var partId))
            return;

        ExitDetectPartId = (int)partId;
        SetExitDetectStatus($"√ 读到 PartId={partId}（激活阈值 {ExitActivePartId}）");

        if (config.devMode && partId != lastLoggedExitPartId)
        {
            lastLoggedExitPartId = (int)partId;
            log.Information("[AutoPalExplorer] [传送检测] {Addon} 图标 PartId={PartId}。", ExitMapAddonName, partId);
        }

        if (partId != ExitActivePartId)
            return;

        if (config.devMode)
            log.Information("[AutoPalExplorer] [传送检测] PartId={PartId}，判定传送装置已激活。", partId);

        NotifyExitActivated();
    }

    private void SetExitDetectStatus(string status)
    {
        ExitDetectStatus = status;
        ExitDetectStatusAt = DateTime.Now;
    }

    /// <summary>
    /// 按节点路径读出传送装置图标当前的 texture PartId。
    /// 读不到时返回 false，并把卡住的那一步写进 <see cref="ExitDetectStatus"/>。
    /// </summary>
    private unsafe bool TryReadExitIconPartId(out uint partId)
    {
        partId = 0;
        ExitDetectPartId = -1;

        if (!TryGetAddonByName<AtkUnitBase>(ExitMapAddonName, out var addon) || addon == null)
        {
            SetExitDetectStatus($"× 找不到 Addon「{ExitMapAddonName}」（未加载）");
            return false;
        }

        if (!IsAddonReady(addon))
        {
            SetExitDetectStatus($"× Addon「{ExitMapAddonName}」已加载但未就绪（IsVisible={addon->IsVisible}）");
            return false;
        }

        // Res Node 1：addon 自己的 uld 里应该直接有
        var root = addon->GetNodeById(ExitMapRootNodeId);
        if (root == null)
        {
            SetExitDetectStatus($"× 找不到 ResNode {ExitMapRootNodeId}"
                + $"（addon 顶层节点数={addon->UldManager.NodeListCount}）");
            return false;
        }

        // Res Node 16：普通 ResNode，子节点走 ChildNode 链表；addon uld 里一般也能直接查到
        var node16 = addon->GetNodeById(ExitMapNode16Id);
        if (node16 == null)
            node16 = FindDescendantById(root, ExitMapNode16Id);

        if (node16 == null)
        {
            SetExitDetectStatus($"× ResNode {ExitMapRootNodeId} 下找不到节点 {ExitMapNode16Id}"
                + $"（{DescribeNode(root)}）");
            return false;
        }

        // Component Node 18
        var node18 = FindDescendantById(node16, ExitMapNode18Id);
        if (node18 == null)
        {
            SetExitDetectStatus($"× 节点 {ExitMapNode16Id} 下找不到节点 {ExitMapNode18Id}"
                + $"（{DescribeNode(node16)}）");
            return false;
        }

        // Image Node 2：在组件 18 内部
        var imageNode = FindDescendantById(node18, ExitMapImageNodeId);
        if (imageNode == null)
        {
            SetExitDetectStatus($"× 节点 {ExitMapNode18Id} 下找不到节点 {ExitMapImageNodeId}"
                + $"（{DescribeNode(node18)}）");
            return false;
        }

        if (imageNode->Type != NodeType.Image)
        {
            SetExitDetectStatus($"× 节点 {ExitMapImageNodeId} 不是 Image 节点（Type={imageNode->Type}）");
            return false;
        }

        partId = ((AtkImageNode*)imageNode)->PartId;
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
