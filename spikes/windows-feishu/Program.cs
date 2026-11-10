using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace KennectComputer.Spike.Windows;

/// <summary>
/// Throwaway M0a spike: enumerate Feishu windows, read its UIA tree, and capture the window.
/// Read-only for app content: it never types, clicks, invokes a pattern, or sets a value.
/// The brief mandates ShowWindow(SW_RESTORE)/SetForegroundWindow; the probe captures the
/// original WINDOWPLACEMENT and restores it after the screenshot (ruling R12).
/// Message text, chat names, and drafts are redacted before anything is printed.
/// </summary>
internal static class Program
{
    private const int MaxDepth = 20;
    private const int MaxNodes = 5000;
    private const uint PwRenderFullContent = 0x00000002;
    private const int SwRestore = 9;

    private static readonly HashSet<string> SafeUiLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Send", "发送", "Feishu", "飞书", "Search", "搜索", "Chat", "Chats", "消息", "Contacts", "通讯录",
        "Calendar", "日历", "Docs", "文档", "Drive", "云盘", "More", "更多", "Emoji", "表情", "Image", "图片",
        "File", "文件", "Cancel", "取消", "Close", "关闭", "Minimize", "最小化", "Maximize", "最大化",
        "Restore", "还原", "Settings", "设置", "Reply", "回复", "Forward", "转发", "Copy", "复制",
        "Edit", "编辑", "Delete", "删除", "Download", "下载", "Upload", "上传", "New", "新建", "Me", "我",
        "OK", "确定", "Yes", "是", "No", "否", "Back", "返回", "Next", "下一步", "Done", "完成",
        "Add", "添加", "Pin", "置顶", "Mute", "静音", "Read", "已读", "Unread", "未读", "Loading", "加载中",
        "Attach", "附件", "Voice", "语音", "Video", "视频", "Call", "通话", "Info", "信息", "Leave", "退出",
        "Create", "创建", "Select", "选择", "All", "全部", "Notifications", "通知", "Threads", "话题",
        "Sticker", "贴纸", "GIF", "Save", "保存", "Open", "打开", "Menu", "菜单", "TitleBar", "标题栏",
        "Hold Right Alt to talk"
    };

    // Task 8's interactive-role vocabulary. Used only to report comparable counts.
    private static readonly HashSet<string> InteractiveRoles = new(StringComparer.Ordinal)
    {
        "Button", "Edit", "Document", "Hyperlink", "CheckBox", "RadioButton", "ComboBox",
        "ListItem", "MenuItem", "TabItem", "TreeItem", "Slider", "Spinner", "Text"
    };

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        EnablePerMonitorDpiAwareness();

        var repoRoot = FindRepoRoot();
        var screenshotPath = Environment.GetEnvironmentVariable("KENNECT_SPIKE_SCREENSHOT")
                             ?? Path.Combine(repoRoot, "docs", "spikes", "windows-feishu.png");
        var suppliedConversation = args.Length > 0
            ? args[0]
            : Environment.GetEnvironmentVariable("KENNECT_TEST_CONVERSATION");

        Console.WriteLine("=== Kennect Computer spike: Windows Feishu accessibility ===");
        Console.WriteLine($"Time (UTC): {DateTime.UtcNow:O}");
        Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
        Console.WriteLine("Redaction: message text, chat titles, and draft values print as <redacted: N chars>; " +
                          "only structural UI labels (Send, Chats, Feishu, ...) are kept.");
        Console.WriteLine("Mode: read-only for app content. No patterns invoked, no values set, no input synthesized.");
        Console.WriteLine("Window restore/activation is mandated by the brief; the original WINDOWPLACEMENT is " +
                          "restored after capture (ruling R12).");
        Console.WriteLine();

        var windows = EnumerateFeishuWindows();
        Console.WriteLine($"Feishu.exe top-level windows (EnumWindows): {windows.Count}");
        for (var i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            Console.WriteLine($"  [{i}] hwnd=0x{w.Handle.ToInt64():X} title=\"{RedactName(w.Title)}\" " +
                              $"class=\"{w.ClassName}\" pid={w.ProcessId} bounds=({w.Bounds.Left},{w.Bounds.Top}) " +
                              $"{w.Bounds.Width}x{w.Bounds.Height} visible={w.Visible} iconic={w.Iconic}");
        }

        var main = PickMainWindow(windows);
        if (main is null)
        {
            Console.WriteLine("ERROR: no Feishu.exe top-level window found.");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine($"Main window: hwnd=0x{main.Handle.ToInt64():X} pid={main.ProcessId} " +
                          $"class=\"{main.ClassName}\" title=\"{RedactName(main.Title)}\" " +
                          $"bounds=({main.Bounds.Left},{main.Bounds.Top}) {main.Bounds.Width}x{main.Bounds.Height}");
        Console.WriteLine($"Feishu version: {GetFileVersion(main.ImagePath)}");
        Console.WriteLine($"Image: {Path.GetFileName(main.ImagePath)}");

        var children = EnumerateChildWindows(main.Handle);
        Console.WriteLine();
        Console.WriteLine($"Child HWNDs of main window (EnumChildWindows): {children.Count}");
        foreach (var child in children)
        {
            Console.WriteLine($"  hwnd=0x{child.Handle.ToInt64():X} class=\"{child.ClassName}\" " +
                              $"title=\"{RedactName(child.Title)}\" bounds=({child.Bounds.Left},{child.Bounds.Top}) " +
                              $"{child.Bounds.Width}x{child.Bounds.Height} visible={child.Visible} pid={child.ProcessId}");
        }

        // The brief mandates SW_RESTORE + SetForegroundWindow. Capture the original
        // placement first so the geometry can be put back after the screenshot (R12).
        var originalPlacement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        var hasOriginalPlacement = GetWindowPlacement(main.Handle, ref originalPlacement);
        Console.WriteLine($"Original WINDOWPLACEMENT: captured={hasOriginalPlacement}, " +
                          $"showCmd={originalPlacement.ShowCmd}, " +
                          $"normal=({originalPlacement.NormalPosition.Left},{originalPlacement.NormalPosition.Top}) " +
                          $"{originalPlacement.NormalPosition.Right - originalPlacement.NormalPosition.Left}x" +
                          $"{originalPlacement.NormalPosition.Bottom - originalPlacement.NormalPosition.Top}");

        // Restore and foreground the main window. No input is synthesized.
        ShowWindow(main.Handle, SwRestore);
        var foregrounded = SetForegroundWindow(main.Handle);
        Console.WriteLine($"ShowWindow(SW_RESTORE) + SetForegroundWindow -> {foregrounded}; waiting 1000 ms");
        Thread.Sleep(1000);

        // GetWindowRect returned the minimized rect during enumeration. Refresh it after
        // SW_RESTORE so geometry filters and the capture use the measured window size.
        GetWindowRect(main.Handle, out var restoredRect);
        var windowBounds = Rectangle.FromLTRB(restoredRect.Left, restoredRect.Top, restoredRect.Right, restoredRect.Bottom);
        if (windowBounds.Width <= 100 || windowBounds.Height <= 100)
        {
            var normal = originalPlacement.NormalPosition;
            windowBounds = Rectangle.FromLTRB(normal.Left, normal.Top, normal.Right, normal.Bottom);
            Console.WriteLine("Refresh still returned a minimized/tiny rect; using WINDOWPLACEMENT normal position.");
        }

        Console.WriteLine($"Measured window bounds after restore: ({windowBounds.Left},{windowBounds.Top}) " +
                          $"{windowBounds.Width}x{windowBounds.Height}, iconic={IsIconic(main.Handle)}");

        using var automation = new UIA3Automation
        {
            ConnectionTimeout = TimeSpan.FromSeconds(10),
            TransactionTimeout = TimeSpan.FromSeconds(10)
        };

        var root = automation.FromHandle(main.Handle);
        var nodes = new List<Node>();
        var walkedTwice = false;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            nodes = WalkTree(automation, root);
            var namedText = nodes.Count(n => n.ControlType == "Text" && n.Name.Length > 0);
            var sizedDocuments = nodes.Count(n => n.ControlType == "Document" && n.Bounds.Width > 100 && n.Bounds.Height > 100);
            Console.WriteLine($"Walk attempt {attempt}: nodes={nodes.Count}, namedText={namedText}, sizedDocuments={sizedDocuments}");
            if (namedText >= 5 && sizedDocuments >= 1)
            {
                break;
            }

            if (attempt < 3)
            {
                // Chromium enables its renderer accessibility tree on demand. A cold tree
                // looks sparse for the first second or two after the first UIA query.
                Console.WriteLine("Tree looks cold/sparse; waiting 1500 ms and re-walking.");
                Thread.Sleep(1500);
                root = automation.FromHandle(main.Handle);
                walkedTwice = true;
            }
        }

        var truncated = nodes.Count >= MaxNodes;
        Console.WriteLine();
        Console.WriteLine("=== UIA tree ===");
        Console.WriteLine($"Nodes walked: {nodes.Count} (raw view, max depth {MaxDepth}, cap {MaxNodes}), " +
                          $"truncated={truncated}, retriedAfterSparseTree={walkedTwice}");

        var byControlType = nodes
            .GroupBy(n => n.ControlType)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine("Nodes by ControlType:");
        foreach (var group in byControlType)
        {
            var interactive = InteractiveRoles.Contains(group.Key) ? " (interactive role)" : string.Empty;
            Console.WriteLine($"  {group.Key}: {group.Count()}{interactive}");
        }

        var interactiveNodes = nodes.Where(n => InteractiveRoles.Contains(n.ControlType)).ToList();
        var interactiveEnabled = interactiveNodes.Count(n => n.IsEnabled);
        var interactiveOnScreen = interactiveNodes.Count(n => !n.IsOffscreen);
        Console.WriteLine();
        Console.WriteLine($"Interactive nodes (Task 8 vocabulary): {interactiveNodes.Count} total, " +
                          $"{interactiveEnabled} enabled, {interactiveOnScreen} on-screen");
        Console.WriteLine($"Enabled + on-screen interactive nodes: {interactiveNodes.Count(n => n.IsEnabled && !n.IsOffscreen)}");

        Console.WriteLine();
        Console.WriteLine("=== Full-window deep walk (supplemental: depth 60, cap 5000) ===");
        var fullDeep = WalkTree(automation, root, 60, 5000);
        Console.WriteLine($"Full-window deep nodes: {fullDeep.Count}, truncated={fullDeep.Count >= 5000}");
        foreach (var group in fullDeep.GroupBy(n => n.ControlType).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {group.Key}: {group.Count()}");
        }

        var fullInteractive = fullDeep.Where(n => InteractiveRoles.Contains(n.ControlType)).ToList();
        Console.WriteLine($"Full-window interactive nodes: {fullInteractive.Count}, " +
                          $"enabled={fullInteractive.Count(n => n.IsEnabled)}, " +
                          $"on-screen={fullInteractive.Count(n => !n.IsOffscreen)}");

        Console.WriteLine();
        Console.WriteLine("=== Task 8 default walk (supplemental: depth 12, cap 1500) ===");
        var defaultWalk = WalkTree(automation, root, 12, 1500);
        var defaultInteractive = defaultWalk.Where(n => InteractiveRoles.Contains(n.ControlType)).ToList();
        Console.WriteLine($"Task 8 default nodes: {defaultWalk.Count}, truncated={defaultWalk.Count >= 1500}, " +
                          $"interactive={defaultInteractive.Count}, " +
                          $"enabled={defaultInteractive.Count(n => n.IsEnabled)}, " +
                          $"on-screen={defaultInteractive.Count(n => !n.IsOffscreen)}");

        Console.WriteLine();
        Console.WriteLine("=== First 50 nodes (raw view) ===");
        foreach (var node in nodes.Take(50))
        {
            Console.WriteLine($"  {Describe(node)} {ReadValueSummary(node.Element)}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Candidate nodes ===");

        var conversationName = suppliedConversation;
        var conversationSource = "argument/env";
        if (string.IsNullOrEmpty(conversationName))
        {
            var selectedChat = FindSelectedChatItem(nodes, windowBounds);
            if (selectedChat is not null)
            {
                conversationName = selectedChat.Name;
                conversationSource = $"selected {selectedChat.ControlType} in left pane";
            }
        }

        if (string.IsNullOrEmpty(conversationName))
        {
            var header = FindHeaderText(nodes, windowBounds);
            if (header is not null)
            {
                conversationName = header.Name;
                conversationSource = $"header {header.ControlType} in top band";
            }
        }

        if (string.IsNullOrEmpty(conversationName))
        {
            // The largest web-area Document usually is the open conversation. Its text's
            // first non-empty line is the chat title. Used only for structural matching;
            // it is never printed (only its length is).
            var document = nodes
                .Where(n => n.ControlType == "Document" && n.Bounds.Width > 100 && n.Bounds.Height > 100)
                .OrderByDescending(n => (long)n.Bounds.Width * n.Bounds.Height)
                .FirstOrDefault();
            if (document is not null)
            {
                var firstLine = FirstNonEmptyLine(GetTextPatternText(document.Element) ?? GetValuePatternText(document.Element));
                if (firstLine is { Length: > 1 and <= 80 })
                {
                    conversationName = firstLine;
                    conversationSource = $"first line of {document.ControlType} text";
                }
            }
        }

        if (string.IsNullOrEmpty(conversationName))
        {
            Console.WriteLine("Test conversation: not supplied and not detected; name-match search skipped.");
        }
        else
        {
            Console.WriteLine($"Test conversation: {RedactName(conversationName)} (source: {conversationSource})");
            var matches = nodes
                .Where(n => n.Name.Contains(conversationName, StringComparison.Ordinal))
                .ToList();
            Console.WriteLine($"Nodes whose Name contains the test conversation name: {matches.Count}");
            foreach (var node in matches.Take(20))
            {
                Console.WriteLine($"  {Describe(node)} patterns={PatternSummary(node.Element)} {ReadValueSummary(node.Element)}");
            }
        }

        Console.WriteLine();
        var editOrDocument = nodes.Where(n => n.ControlType is "Edit" or "Document").ToList();
        Console.WriteLine($"Edit/Document nodes: {editOrDocument.Count}");
        foreach (var node in editOrDocument.Take(30))
        {
            Console.WriteLine($"  {Describe(node)} patterns={PatternSummary(node.Element)} {ReadValueSummary(node.Element)} " +
                              $"{TextPatternSummary(node.Element)} {LegacySummary(node.Element)} " +
                              $"bottomBand={IsInBottomBand(node, windowBounds)}");
            var preview = TextPreviewIfEnabled(node.Element);
            if (preview is not null)
            {
                Console.WriteLine($"    {preview}");
            }
        }

        Console.WriteLine();
        var sendNodes = nodes
            .Where(n => n.Name.Trim().Equals("Send", StringComparison.OrdinalIgnoreCase)
                        || n.Name.Trim().Equals("发送", StringComparison.Ordinal))
            .ToList();
        Console.WriteLine($"Nodes whose Name is Send/发送: {sendNodes.Count}");
        foreach (var node in sendNodes.Take(20))
        {
            var patterns = PatternSummary(node.Element);
            Console.WriteLine($"  {Describe(node, showName: true)} patterns={patterns} " +
                              $"invokePattern={HasPattern(node.Element, "Invoke")} {ReadValueSummary(node.Element)}");
        }

        var automationIdSend = nodes
            .Where(n => n.AutomationId.Contains("send", StringComparison.OrdinalIgnoreCase)
                        && !sendNodes.Contains(n))
            .ToList();
        Console.WriteLine($"Additional nodes with AutomationId containing \"send\": {automationIdSend.Count}");
        foreach (var node in automationIdSend.Take(10))
        {
            Console.WriteLine($"  {Describe(node)} patterns={PatternSummary(node.Element)}");
        }

        Console.WriteLine();
        var chatItems = nodes
            .Where(n => n.ControlType is "ListItem" or "TreeItem" or "Text" ||
                        (n.ControlType == "Group" && n.Name.Length > 0 && n.Bounds.Height >= 20))
            .Where(n => n.Name.Length > 0)
            .Where(n => IsInLeftPane(n, windowBounds))
            .ToList();
        Console.WriteLine($"Chat-list candidates (ListItem/TreeItem/Text/named Group in left pane): {chatItems.Count}");
        foreach (var node in chatItems.Take(25))
        {
            Console.WriteLine($"  {Describe(node)} selected={IsSelected(node.Element)} " +
                              $"patterns={PatternSummary(node.Element)}");
        }

        Console.WriteLine();
        var region = AnalyzeMessageRegion(nodes, windowBounds);
        DeepProbe(automation, nodes, region, windowBounds);

        Console.WriteLine();
        CaptureWindow(main, windowBounds, screenshotPath);

        Console.WriteLine();
        if (hasOriginalPlacement)
        {
            var restored = SetWindowPlacement(main.Handle, ref originalPlacement);
            Console.WriteLine($"Restored original WINDOWPLACEMENT after capture: {restored} " +
                              $"(showCmd={originalPlacement.ShowCmd}).");
        }
        else
        {
            Console.WriteLine("Original WINDOWPLACEMENT was not captured; window geometry left as measured.");
        }

        Console.WriteLine();
        Console.WriteLine("Done. Read-only for app content: no patterns invoked, no values set, no input synthesized. " +
                          "Window placement restored where captured (R12).");
        return 0;
    }

    private static List<Node> WalkTree(UIA3Automation automation, AutomationElement root, int maxDepth = MaxDepth,
        int maxNodes = MaxNodes)
    {
        var nodes = new List<Node>(maxNodes);
        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();

        void Walk(AutomationElement element, int depth, int parentIndex)
        {
            if (nodes.Count >= maxNodes)
            {
                return;
            }

            var node = new Node
            {
                Element = element,
                Index = nodes.Count,
                ParentIndex = parentIndex,
                Depth = depth,
                ControlType = SafeGet(() => element.ControlType.ToString(), "Unknown"),
                Name = SafeGet(() => element.Name ?? string.Empty, string.Empty),
                AutomationId = SafeGet(() => element.AutomationId ?? string.Empty, string.Empty),
                Bounds = SafeGet(() => element.BoundingRectangle, Rectangle.Empty),
                IsEnabled = SafeGet(() => element.IsEnabled, false),
                IsOffscreen = SafeGet(() => element.IsOffscreen, true)
            };
            nodes.Add(node);

            if (depth >= maxDepth || nodes.Count >= maxNodes)
            {
                return;
            }

            var child = SafeGet(() => rawWalker.GetFirstChild(element), null);
            while (child is not null && nodes.Count < maxNodes)
            {
                Walk(child, depth + 1, node.Index);
                child = SafeGet(() => rawWalker.GetNextSibling(child), null);
            }
        }

        Walk(root, 0, -1);
        return nodes;
    }

    private static Node? AnalyzeMessageRegion(List<Node> nodes, Rectangle windowBounds)
    {
        // Prefer the largest scrollable List/Document/Pane in the central-right area.
        var regionCandidates = nodes
            .Where(n => n.ControlType is "List" or "Document" or "Pane" or "Group")
            .Where(n => n.Bounds.Width > windowBounds.Width * 0.25 && n.Bounds.Height > windowBounds.Height * 0.25)
            .Where(n => n.Bounds.Left >= windowBounds.Left + windowBounds.Width * 0.25)
            .Where(n => n.Bounds.Right <= windowBounds.Right + 2 && n.Bounds.Top >= windowBounds.Top - 2 &&
                        n.Bounds.Bottom <= windowBounds.Bottom + 2)
            .OrderByDescending(n => (long)n.Bounds.Width * n.Bounds.Height)
            .ToList();

        Console.WriteLine($"Message-region container candidates (central-right, >25% width/height): {regionCandidates.Count}");
        foreach (var candidate in regionCandidates.Take(8))
        {
            Console.WriteLine($"  #{candidate.Index} d{candidate.Depth} ct={candidate.ControlType} " +
                              $"aid={RedactId(candidate.AutomationId)} bounds=({candidate.Bounds.Left},{candidate.Bounds.Top}," +
                              $"{candidate.Bounds.Width},{candidate.Bounds.Height}) patterns={PatternSummary(candidate.Element)}");
        }

        var region = regionCandidates
            .OrderBy(n => n.ControlType == "List" ? 0 : n.ControlType == "Document" ? 1 : n.ControlType == "Pane" ? 2 : 3)
            .ThenByDescending(n => (long)n.Bounds.Width * n.Bounds.Height)
            .FirstOrDefault();

        List<Node> regionNodes;
        string regionLabel;
        if (region is not null)
        {
            regionNodes = nodes.Where(n => n.Index != region.Index && IsDescendant(nodes, n, region.Index)).ToList();
            regionLabel = $"#{region.Index} {region.ControlType}";
            Console.WriteLine($"Chosen message region: {regionLabel} bounds=({region.Bounds.Left},{region.Bounds.Top}," +
                              $"{region.Bounds.Width},{region.Bounds.Height})");
        }
        else
        {
            // Geometric fallback: middle band, right of the chat list.
            var left = windowBounds.Left + (int)(windowBounds.Width * 0.25);
            var right = windowBounds.Right;
            var top = windowBounds.Top + (int)(windowBounds.Height * 0.08);
            var bottom = windowBounds.Bottom - (int)(windowBounds.Height * 0.25);
            regionNodes = nodes
                .Where(n => n.Bounds.Width > 0 && n.Bounds.Height > 0)
                .Where(n => n.Bounds.Left >= left && n.Bounds.Right <= right + 2 &&
                            n.Bounds.Top >= top && n.Bounds.Bottom <= bottom + 2)
                .ToList();
            regionLabel = $"geometric band x=[{left},{right}] y=[{top},{bottom}]";
            Console.WriteLine($"Chosen message region: geometric fallback ({regionLabel})");
        }

        var texts = regionNodes.Where(n => n.ControlType == "Text").ToList();
        var listItems = regionNodes.Where(n => n.ControlType == "ListItem").ToList();
        var namedNodes = regionNodes.Where(n => n.Name.Length > 0).ToList();
        var textLengths = texts.Select(n => n.Name.Length).Where(len => len > 0).ToList();
        Console.WriteLine($"Message region {regionLabel}: descendants={regionNodes.Count}, Text={texts.Count}, " +
                          $"ListItem={listItems.Count}, namedNodes={namedNodes.Count}, namedText={textLengths.Count}");
        if (textLengths.Count > 0)
        {
            Console.WriteLine($"Text Name lengths: min={textLengths.Min()}, max={textLengths.Max()}, " +
                              $"avg={textLengths.Average():F1}, total={textLengths.Sum()} chars");
        }

        Console.WriteLine("Sample Text nodes in message region:");
        foreach (var node in texts.Take(12))
        {
            Console.WriteLine($"  {Describe(node)} patterns={PatternSummary(node.Element)} {ReadValueSummary(node.Element)}");
        }

        Console.WriteLine("Sample ListItem nodes in message region:");
        foreach (var node in listItems.Take(8))
        {
            Console.WriteLine($"  {Describe(node)} patterns={PatternSummary(node.Element)} {ReadValueSummary(node.Element)}");
        }

        Console.WriteLine("Sample named non-Text nodes in message region (message text may hide in Name):");
        foreach (var node in namedNodes.Where(n => n.ControlType != "Text").Take(15))
        {
            Console.WriteLine($"  {Describe(node)} patterns={PatternSummary(node.Element)} {ReadValueSummary(node.Element)}");
        }

        // A message item is typically a ListItem; fall back to named Groups/Documents in the region.
        var messageItemCandidates = listItems.Count > 0
            ? listItems
            : regionNodes.Where(n => n.ControlType is "Group" or "Document" && n.Name.Length > 0).ToList();
        Console.WriteLine($"Message-item candidates: {messageItemCandidates.Count}");
        foreach (var node in messageItemCandidates.Take(8))
        {
            Console.WriteLine($"  {Describe(node)} patterns={PatternSummary(node.Element)}");
        }

        return region;
    }

    private static void DeepProbe(UIA3Automation automation, List<Node> nodes, Node? region, Rectangle windowBounds)
    {
        if (region is null)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"=== Deep walk under message region #{region.Index} (supplemental: depth 60, cap 5000) ===");
        var deep = WalkTree(automation, region.Element, 60, 5000);
        Console.WriteLine($"Deep nodes: {deep.Count}, truncated={deep.Count >= 5000}");
        foreach (var group in deep.GroupBy(n => n.ControlType).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {group.Key}: {group.Count()}");
        }

        var named = deep.Where(n => n.Name.Length > 0).ToList();
        Console.WriteLine($"Named nodes in deep walk: {named.Count}");
        foreach (var node in named.Take(40))
        {
            Console.WriteLine($"  {Describe(node)} patterns={PatternSummary(node.Element)} {ReadValueSummary(node.Element)}");
        }

        var edits = deep.Where(n => n.ControlType is "Edit" or "Document").ToList();
        Console.WriteLine($"Deep Edit/Document nodes: {edits.Count}");
        foreach (var node in edits.Take(20))
        {
            Console.WriteLine($"  {Describe(node)} patterns={PatternSummary(node.Element)} {ReadValueSummary(node.Element)} " +
                              $"{TextPatternSummary(node.Element)} {LegacySummary(node.Element)}");
        }

        var sends = deep.Where(n => n.Name.Trim().Equals("Send", StringComparison.OrdinalIgnoreCase)
                                    || n.Name.Trim().Equals("发送", StringComparison.Ordinal))
            .ToList();
        Console.WriteLine($"Deep Send/发送 nodes: {sends.Count}");
        foreach (var node in sends.Take(10))
        {
            Console.WriteLine($"  {Describe(node, showName: true)} patterns={PatternSummary(node.Element)}");
        }

        var texts = deep.Where(n => n.ControlType == "Text").ToList();
        var lengths = texts.Select(n => n.Name.Length).Where(len => len > 0).ToList();
        Console.WriteLine($"Deep Text nodes: {texts.Count}, named={lengths.Count}" +
                          (lengths.Count > 0
                              ? $", lengths min={lengths.Min()} max={lengths.Max()} total={lengths.Sum()}"
                              : string.Empty));
        foreach (var node in texts.Take(20))
        {
            Console.WriteLine($"  {Describe(node)} patterns={PatternSummary(node.Element)}");
        }

        var textEditNodes = deep.Where(n => HasPattern(n.Element, "TextEdit")).ToList();
        Console.WriteLine($"Deep nodes supporting TextEdit pattern: {textEditNodes.Count}");
        foreach (var node in textEditNodes.Take(20))
        {
            Console.WriteLine($"  {Describe(node)} patterns={PatternSummary(node.Element)} {ReadValueSummary(node.Element)}");
        }

        var bottomBand = deep
            .Where(n => n.Bounds.Height > 0 && n.Bounds.Top > windowBounds.Top + windowBounds.Height * 0.72)
            .OrderBy(n => n.Bounds.Top)
            .ThenBy(n => n.Bounds.Left)
            .ToList();
        Console.WriteLine($"Deep bottom-band nodes (y > 72% of window): {bottomBand.Count}");
        foreach (var node in bottomBand.Take(80))
        {
            Console.WriteLine($"  {Describe(node)} patterns={PatternSummary(node.Element)} {ReadValueSummary(node.Element)}");
        }

        var bottomInvoke = bottomBand
            .Where(n => n.ControlType == "Button" && HasPattern(n.Element, "Invoke"))
            .ToList();
        Console.WriteLine($"Bottom-band Buttons with InvokePattern: {bottomInvoke.Count}");
        foreach (var node in bottomInvoke.Take(10))
        {
            Console.WriteLine($"  {Describe(node)} patterns={PatternSummary(node.Element)}");
        }

        var bottomTextEdit = bottomBand
            .Where(n => n.ControlType is not ("Text" or "Document") && HasPattern(n.Element, "TextEdit"))
            .ToList();
        Console.WriteLine($"Bottom-band non-Text/Document nodes supporting TextEdit: {bottomTextEdit.Count}");
        foreach (var node in bottomTextEdit.Take(10))
        {
            Console.WriteLine($"  {Describe(node)} patterns={PatternSummary(node.Element)} {ReadValueSummary(node.Element)} " +
                              $"{TextPatternSummary(node.Element)}");
        }

        var bottomValue = bottomBand
            .Where(n => HasPattern(n.Element, "Value"))
            .ToList();
        Console.WriteLine($"Bottom-band nodes supporting ValuePattern: {bottomValue.Count}");
        foreach (var node in bottomValue.Take(10))
        {
            Console.WriteLine($"  {Describe(node)} patterns={PatternSummary(node.Element)} {ReadValueSummary(node.Element)}");
        }

        // Summarize the other web-area Document (normally the chat list) without dumping it.
        var otherDocument = nodes
            .Where(n => n.ControlType == "Document" && n.Index != region.Index && n.Bounds.Width > 50 && n.Bounds.Height > 50)
            .FirstOrDefault();
        if (otherDocument is not null)
        {
            var otherDeep = WalkTree(automation, otherDocument.Element, 60, 5000);
            var otherTexts = otherDeep.Where(n => n.ControlType == "Text").ToList();
            var otherLengths = otherTexts.Select(n => n.Name.Length).Where(len => len > 0).ToList();
            Console.WriteLine();
            Console.WriteLine($"Deep summary for other Document #{otherDocument.Index}: nodes={otherDeep.Count}, " +
                              $"Text={otherTexts.Count}, namedText={otherLengths.Count}" +
                              (otherLengths.Count > 0
                                  ? $", lengths min={otherLengths.Min()} max={otherLengths.Max()} total={otherLengths.Sum()}"
                                  : string.Empty) +
                              $", Button={otherDeep.Count(n => n.ControlType == "Button")}, " +
                              $"Edit={otherDeep.Count(n => n.ControlType == "Edit")}, " +
                              $"Send={otherDeep.Count(n => n.Name.Trim() is "Send" or "发送")}");
        }
    }

    private static bool HasPattern(AutomationElement element, string patternName)
    {
        try
        {
            return element.GetSupportedPatterns().Any(p => p.Name == patternName);
        }
        catch
        {
            return false;
        }
    }

    private static void CaptureWindow(WindowInfo main, Rectangle windowBounds, string outputPath)
    {
        var width = windowBounds.Width;
        var height = windowBounds.Height;
        if (width <= 0 || height <= 0)
        {
            Console.WriteLine($"Screenshot skipped: invalid window bounds {width}x{height}.");
            return;
        }

        try
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                bool ok;
                try
                {
                    ok = PrintWindow(main.Handle, hdc, PwRenderFullContent);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }

                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                bitmap.Save(outputPath, ImageFormat.Png);
                var relative = Path.GetRelativePath(FindRepoRoot(), outputPath);
                Console.WriteLine($"Screenshot: PrintWindow(PW_RENDERFULLCONTENT)={ok}, size={width}x{height}, " +
                                  $"mostlyBlack={IsMostlyBlack(bitmap)}, path={relative}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Screenshot failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsMostlyBlack(Bitmap bitmap)
    {
        var stepX = Math.Max(1, bitmap.Width / 32);
        var stepY = Math.Max(1, bitmap.Height / 32);
        var dark = 0;
        var total = 0;
        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.R < 12 && c.G < 12 && c.B < 12)
                {
                    dark++;
                }

                total++;
            }
        }

        return total > 0 && dark > total * 0.9;
    }

    private static Node? FindSelectedChatItem(List<Node> nodes, Rectangle windowBounds)
    {
        return nodes
            .Where(n => n.ControlType is "ListItem" or "TreeItem")
            .Where(n => IsInLeftPane(n, windowBounds))
            .Where(n => n.Name.Length > 0)
            .Where(n => IsSelected(n.Element))
            .OrderByDescending(n => (long)n.Bounds.Width * n.Bounds.Height)
            .FirstOrDefault();
    }

    private static Node? FindHeaderText(List<Node> nodes, Rectangle windowBounds)
    {
        return nodes
            .Where(n => n.ControlType == "Text")
            .Where(n => n.Name.Length > 2 && !SafeUiLabels.Contains(n.Name.Trim()))
            .Where(n => n.Bounds.Height > 0)
            .Where(n => n.Bounds.Top < windowBounds.Top + windowBounds.Height * 0.10)
            .Where(n => n.Bounds.Left > windowBounds.Left + windowBounds.Width * 0.25)
            .OrderByDescending(n => n.Bounds.Width)
            .FirstOrDefault();
    }

    private static bool IsInLeftPane(Node node, Rectangle windowBounds)
    {
        return node.Bounds.Width > 0 && node.Bounds.Height > 0 &&
               node.Bounds.Left < windowBounds.Left + windowBounds.Width * 0.35 &&
               node.Bounds.Width < windowBounds.Width * 0.45;
    }

    private static bool IsInBottomBand(Node node, Rectangle windowBounds)
    {
        return node.Bounds.Height > 0 && node.Bounds.Top > windowBounds.Top + windowBounds.Height * 0.60;
    }

    private static bool IsDescendant(List<Node> nodes, Node node, int ancestorIndex)
    {
        var parent = node.ParentIndex;
        while (parent >= 0)
        {
            if (parent == ancestorIndex)
            {
                return true;
            }

            parent = nodes[parent].ParentIndex;
        }

        return false;
    }

    private static bool IsSelected(AutomationElement element)
    {
        try
        {
            var pattern = element.Patterns.SelectionItem.PatternOrDefault;
            return pattern is not null && SafeGet(() => pattern.IsSelected.ValueOrDefault, false);
        }
        catch
        {
            return false;
        }
    }

    private static string TextPatternSummary(AutomationElement element)
    {
        try
        {
            var pattern = element.Patterns.Text.PatternOrDefault;
            if (pattern is null)
            {
                return "textPattern=no";
            }

            var text = pattern.DocumentRange.GetText(-1) ?? string.Empty;
            var lineCount = text.Count(c => c == '\n') + (text.Length > 0 ? 1 : 0);
            return $"textPattern=yes textLen={text.Length} textLines={lineCount}";
        }
        catch (Exception ex)
        {
            return $"textPattern=error:{ex.GetType().Name}";
        }
    }

    private static string LegacySummary(AutomationElement element)
    {
        try
        {
            var pattern = element.Patterns.LegacyIAccessible.PatternOrDefault;
            if (pattern is null)
            {
                return "legacy=no";
            }

            var name = SafeGet(() => pattern.Name.ValueOrDefault ?? string.Empty, string.Empty);
            var value = SafeGet(() => pattern.Value.ValueOrDefault ?? string.Empty, string.Empty);
            return $"legacy=yes legacyNameLen={name.Length} legacyValueLen={value.Length}";
        }
        catch (Exception ex)
        {
            return $"legacy=error:{ex.GetType().Name}";
        }
    }

    private static string? GetTextPatternText(AutomationElement element)
    {
        try
        {
            return element.Patterns.Text.PatternOrDefault?.DocumentRange.GetText(-1);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetValuePatternText(AutomationElement element)
    {
        try
        {
            return element.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstNonEmptyLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text
            .Split(['\r', '\n', '\u2028', '\u2029'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))
            ?.Trim();
    }

    /// <summary>
    /// Private diagnostics escape hatch, never enabled for the committed transcript.
    /// It exists only to confirm that a Document's text property really contains
    /// message bodies; the report records the structural result, not the text.
    /// </summary>
    private static string? TextPreviewIfEnabled(AutomationElement element)
    {
        if (Environment.GetEnvironmentVariable("KENNECT_SPIKE_SHOW_TEXT") != "1")
        {
            return null;
        }

        var text = GetTextPatternText(element) ?? GetValuePatternText(element);
        if (string.IsNullOrEmpty(text))
        {
            return "preview=<empty>";
        }

        var normalized = text.Replace("\r", "\\r").Replace("\n", "\\n");
        var preview = normalized[..Math.Min(120, normalized.Length)];
        return $"preview=\"{preview}\"";
    }

    private static string PatternSummary(AutomationElement element)
    {
        try
        {
            var patterns = element.GetSupportedPatterns();
            if (patterns.Length == 0)
            {
                return "<none>";
            }

            return string.Join("|", patterns.Select(p => p.ToString()).OrderBy(s => s, StringComparer.Ordinal));
        }
        catch (Exception ex)
        {
            return $"<error:{ex.GetType().Name}>";
        }
    }

    private static string ReadValueSummary(AutomationElement element)
    {
        try
        {
            var valuePattern = element.Patterns.Value.PatternOrDefault;
            if (valuePattern is null)
            {
                return "valuePattern=no";
            }

            var readOnly = SafeGet(() => valuePattern.IsReadOnly.ValueOrDefault, false);
            string? value = null;
            try
            {
                value = valuePattern.Value.ValueOrDefault;
            }
            catch
            {
                // Value may be unavailable on this element.
            }

            return $"valuePattern=yes readOnly={readOnly} value={RedactName(value ?? string.Empty)}";
        }
        catch (Exception ex)
        {
            return $"valuePattern=error:{ex.GetType().Name}";
        }
    }

    private static string Describe(Node node, bool showName = false)
    {
        var nameIsSafe = SafeUiLabels.Contains(node.Name.Trim()) ||
                         node.Name is "Send" or "发送";
        var renderedName = showName && nameIsSafe ? node.Name : RedactName(node.Name);
        return $"#{node.Index:D4} d{node.Depth:D2} ct={node.ControlType} " +
               $"name={renderedName} " +
               $"aid={RedactId(node.AutomationId)} bounds=({node.Bounds.Left},{node.Bounds.Top}," +
               $"{node.Bounds.Width},{node.Bounds.Height}) enabled={node.IsEnabled} offscreen={node.IsOffscreen}";
    }

    private static string RedactName(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "<empty>";
        }

        return SafeUiLabels.Contains(value.Trim()) ? value : $"<redacted: {value.Length} chars>";
    }

    private static string RedactId(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "<empty>";
        }

        return value.Length <= 80 ? value : value[..80] + "...";
    }

    private static T SafeGet<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }

    private static List<WindowInfo> EnumerateFeishuWindows()
    {
        var result = new List<WindowInfo>();
        var imageByPid = new Dictionary<uint, string>();

        string GetImage(uint pid)
        {
            if (imageByPid.TryGetValue(pid, out var cached))
            {
                return cached;
            }

            var image = string.Empty;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                image = process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                // Fall back to the process name below.
            }

            if (string.IsNullOrEmpty(image))
            {
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    image = process.ProcessName + ".exe";
                }
                catch
                {
                    // Leave empty; it will not match.
                }
            }

            imageByPid[pid] = image;
            return image;
        }

        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var pid);
            var image = GetImage(pid);
            if (!string.Equals(Path.GetFileName(image), "Feishu.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            GetWindowRect(handle, out var rect);
            var titleBuilder = new StringBuilder(512);
            GetWindowText(handle, titleBuilder, titleBuilder.Capacity);
            var classBuilder = new StringBuilder(256);
            GetClassName(handle, classBuilder, classBuilder.Capacity);

            result.Add(new WindowInfo
            {
                Handle = handle,
                ProcessId = pid,
                Title = titleBuilder.ToString(),
                ClassName = classBuilder.ToString(),
                Bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom),
                Visible = IsWindowVisible(handle),
                Iconic = IsIconic(handle),
                ImagePath = image
            });
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static List<WindowInfo> EnumerateChildWindows(IntPtr parent)
    {
        var result = new List<WindowInfo>();
        EnumChildWindows(parent, (handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var pid);
            GetWindowRect(handle, out var rect);
            var titleBuilder = new StringBuilder(512);
            GetWindowText(handle, titleBuilder, titleBuilder.Capacity);
            var classBuilder = new StringBuilder(256);
            GetClassName(handle, classBuilder, classBuilder.Capacity);
            result.Add(new WindowInfo
            {
                Handle = handle,
                ProcessId = pid,
                Title = titleBuilder.ToString(),
                ClassName = classBuilder.ToString(),
                Bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom),
                Visible = IsWindowVisible(handle),
                Iconic = IsIconic(handle),
                ImagePath = string.Empty
            });
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static WindowInfo? PickMainWindow(List<WindowInfo> windows)
    {
        // The Feishu main window keeps title "Feishu" and stays Visible=True when
        // minimized, but GetWindowRect then returns an off-screen 237x39 rect. Prefer
        // the visible titled Chrome_WidgetWin_1 window over hidden helper windows.
        var titled = windows
            .Where(w => w.Visible && w.Title.Length > 0)
            .OrderByDescending(w => w.ClassName.Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w => (long)w.Bounds.Width * w.Bounds.Height)
            .FirstOrDefault();
        if (titled is not null)
        {
            return titled;
        }

        var visibleMain = windows
            .Where(w => w.Visible && w.ClassName.Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => (long)w.Bounds.Width * w.Bounds.Height)
            .FirstOrDefault();
        if (visibleMain is not null)
        {
            return visibleMain;
        }

        var visible = windows
            .Where(w => w.Visible && w.Bounds.Width > 100 && w.Bounds.Height > 100)
            .OrderByDescending(w => (long)w.Bounds.Width * w.Bounds.Height)
            .FirstOrDefault();

        return visible ?? windows
            .Where(w => w.Bounds.Width > 100 && w.Bounds.Height > 100)
            .OrderByDescending(w => (long)w.Bounds.Width * w.Bounds.Height)
            .FirstOrDefault();
    }

    private static string GetFileVersion(string imagePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(imagePath).FileVersion ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DESIGN.md")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static void EnablePerMonitorDpiAwareness()
    {
        try
        {
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            SetProcessDpiAwarenessContext(new IntPtr(-4));
        }
        catch
        {
            // Older Windows builds; the spike still works at system DPI.
        }
    }

    private sealed class Node
    {
        public required AutomationElement Element { get; init; }
        public int Index { get; init; }
        public int ParentIndex { get; init; }
        public int Depth { get; init; }
        public string ControlType { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string AutomationId { get; init; } = string.Empty;
        public Rectangle Bounds { get; init; }
        public bool IsEnabled { get; init; }
        public bool IsOffscreen { get; init; }
    }

    private sealed class WindowInfo
    {
        public IntPtr Handle { get; init; }
        public uint ProcessId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string ClassName { get; init; } = string.Empty;
        public Rectangle Bounds { get; init; }
        public bool Visible { get; init; }
        public bool Iconic { get; init; }
        public string ImagePath { get; init; } = string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hDc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);
}
