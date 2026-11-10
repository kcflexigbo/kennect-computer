// Kennect Computer — M0b macOS Feishu accessibility spike (throwaway).
//
// Read-only for app content: this probe never calls AXUIElementPerformAction,
// never calls AXUIElementSetAttributeValue, and never synthesizes input. Its only
// writes are a screenshot file when KENNECT_SPIKE_CAPTURE=1 and optional diagnostic
// text previews when KENNECT_SPIKE_SHOW_TEXT=1 (unsafe; local validation only).
//
// Run from the repository root:
//   swift spikes/macos-feishu/main.swift
//
// Required macOS grants (see README.md):
//   Accessibility      — for the AX tree (AXIsProcessTrustedWithOptions)
//   Screen Recording   — only for the optional screenshot (CGPreflightScreenCaptureAccess)

import Foundation
import Cocoa
import ApplicationServices
import CoreGraphics
import ImageIO
import Darwin

// MARK: - Tunables and vocabulary

private let maxMainDepth = 20
private let maxDeepDepth = 60
private let maxNodes = 5000

/// Structural UI labels that may be printed verbatim. Everything else is redacted
/// to a length. Stored lowercased; matching lowercases the candidate string.
private let safeLabels: Set<String> = [
    "send", "send message", "发送", "feishu", "飞书", "lark", "search", "搜索",
    "chat", "chats", "消息", "contacts", "通讯录", "calendar", "日历", "docs", "文档",
    "drive", "云盘", "more", "更多", "emoji", "表情", "image", "图片", "file", "文件",
    "cancel", "取消", "close", "关闭", "minimize", "最小化", "maximize", "最大化",
    "restore", "还原", "settings", "设置", "reply", "回复", "forward", "转发",
    "copy", "复制", "edit", "编辑", "delete", "删除", "download", "下载", "upload", "上传",
    "new", "新建", "me", "我", "ok", "确定", "yes", "是", "no", "否", "back", "返回",
    "next", "下一步", "done", "完成", "add", "添加", "pin", "置顶", "mute", "静音",
    "read", "已读", "unread", "未读", "loading", "加载中", "attach", "附件",
    "voice", "语音", "video", "视频", "call", "通话", "info", "信息", "leave", "退出",
    "create", "创建", "select", "选择", "all", "全部", "notifications", "通知",
    "threads", "话题", "sticker", "贴纸", "save", "保存", "open", "打开", "menu", "菜单",
    "titlebar", "标题栏"
]

/// Interactive-role vocabulary used only for comparable counts in Q1. AXStaticText
/// is included so the count is comparable to the Windows Task 1 "Task 8 roles"
/// count; it is not actionable by itself.
private let interactiveRoles: Set<String> = [
    "AXButton", "AXTextField", "AXTextArea", "AXCheckBox", "AXRadioButton",
    "AXPopUpButton", "AXComboBox", "AXMenuItem", "AXMenuButton", "AXLink",
    "AXRow", "AXCell", "AXTab", "AXSlider", "AXIncrementor",
    "AXDisclosureTriangle", "AXStaticText"
]

/// AX attribute names. These are the documented string values of the kAX*Attribute
/// constants; using string literals avoids CFString/Unmanaged import differences
/// between Swift toolchains. The C API accepts any CFString attribute name.
private enum AXAttr {
    static let children = "AXChildren"
    static let windows = "AXWindows"
    static let role = "AXRole"
    static let subrole = "AXSubrole"
    static let title = "AXTitle"
    static let description = "AXDescription"
    static let identifier = "AXIdentifier"
    static let value = "AXValue"
    static let position = "AXPosition"
    static let size = "AXSize"
    static let enabled = "AXEnabled"
    static let focusedUIElement = "AXFocusedUIElement"
}

// MARK: - Types

private struct FeishuWindow {
    let number: CGWindowID
    let pid: pid_t
    let owner: String
    let title: String?
    let bounds: CGRect
    let onscreen: Bool
    let layer: Int
}

private struct AXNodeInfo {
    let element: AXUIElement
    let index: Int
    let parentIndex: Int
    let depth: Int
    let role: String
    let subrole: String
    let title: String
    let descriptionText: String
    let identifier: String
    let valueString: String?
    let valueKind: String
    let frame: CGRect?
}

// MARK: - AX attribute helpers

private func copyAttribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
    var value: CFTypeRef?
    let error = AXUIElementCopyAttributeValue(element, name as CFString, &value)
    guard error.rawValue == 0 else { return nil }
    return value
}

private func stringAttribute(_ element: AXUIElement, _ name: String) -> String? {
    guard let raw = copyAttribute(element, name), CFGetTypeID(raw) == CFStringGetTypeID() else {
        return nil
    }
    return raw as? String
}

private func boolAttribute(_ element: AXUIElement, _ name: String) -> Bool? {
    guard let raw = copyAttribute(element, name) else { return nil }
    if let number = raw as? NSNumber {
        return number.boolValue
    }
    return nil
}

private func pointAttribute(_ element: AXUIElement, _ name: String) -> CGPoint? {
    guard let raw = copyAttribute(element, name),
          CFGetTypeID(raw) == AXValueGetTypeID(),
          let axValue = raw as? AXValue,
          AXValueGetType(axValue) == .cgPoint else {
        return nil
    }
    var point = CGPoint.zero
    guard AXValueGetValue(axValue, .cgPoint, &point) else { return nil }
    return point
}

private func sizeAttribute(_ element: AXUIElement, _ name: String) -> CGSize? {
    guard let raw = copyAttribute(element, name),
          CFGetTypeID(raw) == AXValueGetTypeID(),
          let axValue = raw as? AXValue,
          AXValueGetType(axValue) == .cgSize else {
        return nil
    }
    var size = CGSize.zero
    guard AXValueGetValue(axValue, .cgSize, &size) else { return nil }
    return size
}

private func frameAttribute(_ element: AXUIElement) -> CGRect? {
    guard let origin = pointAttribute(element, AXAttr.position),
          let size = sizeAttribute(element, AXAttr.size) else {
        return nil
    }
    return CGRect(origin: origin, size: size)
}

private func childElements(_ element: AXUIElement) -> [AXUIElement] {
    guard let raw = copyAttribute(element, AXAttr.children) else { return [] }
    return raw as? [AXUIElement] ?? []
}

private func applicationWindows(_ app: AXUIElement) -> [AXUIElement] {
    guard let raw = copyAttribute(app, AXAttr.windows) else { return [] }
    return raw as? [AXUIElement] ?? []
}

private func actionNames(_ element: AXUIElement) -> [String] {
    var names: CFArray?
    let error = AXUIElementCopyActionNames(element, &names)
    guard error.rawValue == 0, let names = names else { return [] }
    return names as? [String] ?? []
}

private func attributeNames(_ element: AXUIElement) -> [String] {
    var names: CFArray?
    let error = AXUIElementCopyAttributeNames(element, &names)
    guard error.rawValue == 0, let names = names else { return [] }
    return names as? [String] ?? []
}

private func isValueSettable(_ element: AXUIElement) -> Bool {
    var settable = DarwinBoolean(false)
    let error = AXUIElementIsAttributeSettable(element, AXAttr.value as CFString, &settable)
    return error.rawValue == 0 && settable.boolValue
}

// MARK: - Tree walk

private func makeNode(_ element: AXUIElement, index: Int, parentIndex: Int, depth: Int) -> AXNodeInfo {
    let rawValue = copyAttribute(element, AXAttr.value)
    var valueString: String?
    var valueKind = "none"
    if let rawValue = rawValue {
        if CFGetTypeID(rawValue) == CFStringGetTypeID() {
            valueKind = "string"
            valueString = rawValue as? String
        } else if CFGetTypeID(rawValue) == CFNumberGetTypeID() ||
                    CFGetTypeID(rawValue) == CFBooleanGetTypeID() {
            valueKind = "number"
        } else {
            valueKind = "other"
        }
    }

    return AXNodeInfo(
        element: element,
        index: index,
        parentIndex: parentIndex,
        depth: depth,
        role: stringAttribute(element, AXAttr.role) ?? "<none>",
        subrole: stringAttribute(element, AXAttr.subrole) ?? "",
        title: stringAttribute(element, AXAttr.title) ?? "",
        descriptionText: stringAttribute(element, AXAttr.description) ?? "",
        identifier: stringAttribute(element, AXAttr.identifier) ?? "",
        valueString: valueString,
        valueKind: valueKind,
        frame: frameAttribute(element)
    )
}

private func walkTree(_ root: AXUIElement, maxDepth: Int, maxNodes: Int) -> [AXNodeInfo] {
    var nodes: [AXNodeInfo] = []
    nodes.reserveCapacity(min(maxNodes, 1024))

    func visit(_ element: AXUIElement, depth: Int, parentIndex: Int) {
        if nodes.count >= maxNodes { return }
        let node = makeNode(element, index: nodes.count, parentIndex: parentIndex, depth: depth)
        nodes.append(node)
        if depth >= maxDepth || nodes.count >= maxNodes { return }
        for child in childElements(element) {
            if nodes.count >= maxNodes { break }
            visit(child, depth: depth + 1, parentIndex: node.index)
        }
    }

    visit(root, depth: 0, parentIndex: -1)
    return nodes
}

private func descendantIndices(of rootIndex: Int, in nodes: [AXNodeInfo]) -> [Int] {
    var childrenByParent: [Int: [Int]] = [:]
    for node in nodes where node.parentIndex >= 0 {
        childrenByParent[node.parentIndex, default: []].append(node.index)
    }
    var result: [Int] = []
    var stack = childrenByParent[rootIndex] ?? []
    while let index = stack.popLast() {
        result.append(index)
        if let children = childrenByParent[index] {
            stack.append(contentsOf: children)
        }
    }
    return result
}

private func descendants(of rootIndex: Int, in nodes: [AXNodeInfo]) -> [AXNodeInfo] {
    let indices = Set(descendantIndices(of: rootIndex, in: nodes))
    return nodes.filter { indices.contains($0.index) }
}

// MARK: - Window enumeration

private func enumerateWindows() -> [FeishuWindow] {
    guard let windowInfo = CGWindowListCopyWindowInfo([.optionAll], kCGNullWindowID) as? [[String: Any]] else {
        return []
    }

    var result: [FeishuWindow] = []
    for info in windowInfo {
        guard let number = info[kCGWindowNumber as String] as? NSNumber,
              let pid = info[kCGWindowOwnerPID as String] as? NSNumber,
              let boundsDictionary = info[kCGWindowBounds as String] as? NSDictionary,
              let bounds = CGRect(dictionaryRepresentation: boundsDictionary as CFDictionary) else {
            continue
        }

        result.append(FeishuWindow(
            number: number.uint32Value,
            pid: pid_t(pid.int32Value),
            owner: info[kCGWindowOwnerName as String] as? String ?? "",
            title: info[kCGWindowName as String] as? String,
            bounds: bounds,
            onscreen: (info[kCGWindowIsOnscreen as String] as? NSNumber)?.boolValue ?? false,
            layer: (info[kCGWindowLayer as String] as? NSNumber)?.intValue ?? 0
        ))
    }
    return result
}

private func chooseMainAXWindow(
    _ windows: [(element: AXUIElement, role: String, title: String, frame: CGRect?)],
    matching target: CGRect
) -> (element: AXUIElement, role: String, title: String, frame: CGRect?)? {
    var best: (element: AXUIElement, role: String, title: String, frame: CGRect?)?
    var bestOverlap: CGFloat = 0
    for window in windows {
        guard let frame = window.frame else { continue }
        let overlap = intersectionArea(frame, target)
        if overlap > bestOverlap {
            bestOverlap = overlap
            best = window
        }
    }
    if let best = best, bestOverlap > 0 {
        return best
    }
    return windows.max { lhs, rhs in
        (lhs.frame?.width ?? 0) * (lhs.frame?.height ?? 0) <
            (rhs.frame?.width ?? 0) * (rhs.frame?.height ?? 0)
    }
}

private func intersectionArea(_ a: CGRect, _ b: CGRect) -> CGFloat {
    let intersection = a.intersection(b)
    if intersection.isNull || intersection.isEmpty { return 0 }
    return intersection.width * intersection.height
}

// MARK: - Candidate search

private func isLargeContainerRole(_ role: String) -> Bool {
    return ["AXScrollArea", "AXList", "AXTable", "AXOutline", "AXWebArea", "AXGroup"].contains(role)
}

private func chooseMessageContainer(_ nodes: [AXNodeInfo], windowBounds: CGRect) -> AXNodeInfo? {
    let leftLimit = windowBounds.minX + windowBounds.width * 0.20
    let topLimit = windowBounds.minY - 2
    let rightLimit = windowBounds.maxX + 2
    let bottomLimit = windowBounds.maxY + 2
    let minWidth = windowBounds.width * 0.20
    let minHeight = windowBounds.height * 0.20
    let rolePriority: [String: Int] = [
        "AXScrollArea": 0, "AXList": 0, "AXTable": 0, "AXOutline": 0,
        "AXWebArea": 1, "AXGroup": 2
    ]

    let candidates = nodes.filter { node in
        guard let frame = node.frame, rolePriority[node.role] != nil else { return false }
        return frame.width >= minWidth && frame.height >= minHeight &&
            frame.minX >= leftLimit && frame.minY >= topLimit &&
            frame.maxX <= rightLimit && frame.maxY <= bottomLimit
    }

    let sorted = candidates.sorted { lhs, rhs in
        let leftPriority = rolePriority[lhs.role] ?? 9
        let rightPriority = rolePriority[rhs.role] ?? 9
        if leftPriority != rightPriority { return leftPriority < rightPriority }
        let leftArea = (lhs.frame?.width ?? 0) * (lhs.frame?.height ?? 0)
        let rightArea = (rhs.frame?.width ?? 0) * (rhs.frame?.height ?? 0)
        return leftArea > rightArea
    }

    print("Message-region candidates (central-right, >=20% of window): \(sorted.count)")
    for node in sorted.prefix(10) {
        let descendants = descendantIndices(of: node.index, in: nodes).count
        print("  \(nodeLine(node, showText: false)) descendants=\(descendants)")
    }
    return sorted.first
}

// MARK: - Formatting and redaction

private func pad(_ value: Int, _ width: Int) -> String {
    let text = String(value)
    if text.count >= width { return text }
    return String(repeating: "0", count: width - text.count) + text
}

private func rectText(_ rect: CGRect?) -> String {
    guard let rect = rect else { return "<none>" }
    return "(\(Int(rect.origin.x.rounded())),\(Int(rect.origin.y.rounded())),\(Int(rect.width.rounded())),\(Int(rect.height.rounded())))"
}

/// Content roles whose titles/descriptions may be message text or chat titles.
/// Their labels are always redacted unless they are a known structural action label.
private let contentRoles: Set<String> = [
    "AXStaticText", "AXHeading", "AXWebArea", "AXGroup", "AXRow", "AXCell",
    "AXList", "AXTable", "AXOutline", "AXScrollArea", "AXDocument", "AXImage",
    "AXLink"
]

/// Narrow structural labels that are safe even on content nodes. A message whose
/// entire text is "Send" is implausible; a Send control rendered as static text is not.
private let alwaysSafeNodeLabels: Set<String> = ["send", "发送"]

/// Window titles are chat titles unless they are the generic app name.
private func redactedWindowTitle(_ value: String?) -> String {
    guard let value = value else { return "<unavailable>" }
    if value.isEmpty { return "<empty>" }
    let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
    if trimmed == "feishu" || trimmed == "飞书" || trimmed == "lark" {
        return value
    }
    return "<redacted: \(value.count) chars>"
}

/// Node titles/descriptions are redacted by role: interactive controls may keep a
/// small structural-label whitelist; content roles never do.
private func redactedNodeLabel(_ value: String, role: String) -> String {
    if value.isEmpty { return "<empty>" }
    let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
    let key = trimmed.lowercased()
    if alwaysSafeNodeLabels.contains(key) { return value }
    if contentRoles.contains(role) { return "<redacted: \(value.count) chars>" }
    if safeLabels.contains(key) { return value }
    return "<redacted: \(value.count) chars>"
}

private func redactedIdentifier(_ value: String) -> String {
    if value.isEmpty { return "<empty>" }
    if value.count <= 80 { return value }
    return String(value.prefix(80)) + "..."
}

private func valueDescription(_ node: AXNodeInfo, showText: Bool) -> String {
    if let value = node.valueString {
        if value.isEmpty { return "value=<empty>" }
        if showText {
            let preview = value.replacingOccurrences(of: "\n", with: "\\n").prefix(80)
            return "value=\"\(preview)\" (\(value.count) chars)"
        }
        return "value=<redacted: \(value.count) chars>"
    }
    switch node.valueKind {
    case "number": return "value=<number>"
    case "none": return "value=<none>"
    default: return "value=<\(node.valueKind)>"
    }
}

private func nodeLine(_ node: AXNodeInfo, showText: Bool) -> String {
    let subrole = node.subrole.isEmpty ? "<empty>" : node.subrole
    return "#\(pad(node.index, 4)) d\(pad(node.depth, 2)) role=\(node.role) subrole=\(subrole) " +
        "title=\(redactedNodeLabel(node.title, role: node.role)) " +
        "desc=\(redactedNodeLabel(node.descriptionText, role: node.role)) " +
        "id=\(redactedIdentifier(node.identifier)) \(valueDescription(node, showText: showText)) " +
        "bounds=\(rectText(node.frame))"
}

private func boolText(_ value: Bool?) -> String {
    guard let value = value else { return "<unknown>" }
    return value ? "true" : "false"
}

private func printRoleCounts(_ nodes: [AXNodeInfo], label: String) {
    let counts = Dictionary(grouping: nodes, by: { $0.role }).mapValues { $0.count }
    print("\(label) nodes by role:")
    let sorted = counts.sorted { lhs, rhs in
        if lhs.value != rhs.value { return lhs.value > rhs.value }
        return lhs.key < rhs.key
    }
    for entry in sorted {
        print("  \(entry.key): \(entry.value)")
    }
}

private func printLengthStats(_ lengths: [Int], label: String) {
    guard !lengths.isEmpty else {
        print("\(label): none")
        return
    }
    let total = lengths.reduce(0, +)
    let minimum = lengths.min() ?? 0
    let maximum = lengths.max() ?? 0
    let average = Double(total) / Double(lengths.count)
    print("\(label): count=\(lengths.count), min=\(minimum), max=\(maximum), " +
          "avg=\(String(format: "%.1f", average)), total=\(total) chars")
}

private func printTextStats(_ nodes: [AXNodeInfo], label: String, showText: Bool) {
    let staticTexts = nodes.filter { $0.role == "AXStaticText" }
    let valued = staticTexts.filter { !($0.valueString ?? "").isEmpty }
    let titled = staticTexts.filter { !$0.title.isEmpty }
    let described = staticTexts.filter { !$0.descriptionText.isEmpty }

    print("\(label): nodes=\(nodes.count), AXStaticText=\(staticTexts.count), " +
          "withAXValue=\(valued.count), withTitle=\(titled.count), withDescription=\(described.count)")

    let lengths = valued.compactMap { $0.valueString?.count }.filter { $0 > 0 }
    printLengthStats(lengths, label: "\(label) AXStaticText AXValue lengths")

    let otherValued = nodes.filter { $0.role != "AXStaticText" && !($0.valueString ?? "").isEmpty }
    print("\(label) other roles with non-empty AXValue: \(otherValued.count)")
    for node in otherValued.prefix(10) {
        print("  \(nodeLine(node, showText: showText))")
    }

    print("\(label) sample AXStaticText nodes with AXValue:")
    for node in valued.prefix(12) {
        print("  \(nodeLine(node, showText: showText))")
    }

    let titleOnly = titled.filter { ($0.valueString ?? "").isEmpty }
    print("\(label) sample AXStaticText nodes with a title but no AXValue: \(titleOnly.count)")
    for node in titleOnly.prefix(8) {
        print("  \(nodeLine(node, showText: showText))")
    }

    let descriptionOnly = described.filter { ($0.valueString ?? "").isEmpty && $0.title.isEmpty }
    print("\(label) sample AXStaticText nodes with a description only: \(descriptionOnly.count)")
    for node in descriptionOnly.prefix(8) {
        print("  \(nodeLine(node, showText: showText))")
    }
}

private func printComposerCandidate(_ node: AXNodeInfo, showText: Bool) {
    let actions = actionNames(node.element).sorted()
    let hasValueAttribute = attributeNames(node.element).contains(AXAttr.value)
    let settable = hasValueAttribute && isValueSettable(node.element)
    let enabled = boolAttribute(node.element, AXAttr.enabled)
    print("  \(nodeLine(node, showText: showText)) " +
          "actions=[\(actions.joined(separator: ","))] axPress=\(actions.contains("AXPress")) " +
          "axValueAttr=\(hasValueAttribute) axValueSettable=\(settable) enabled=\(boolText(enabled))")
}

private func printButtonCandidate(_ node: AXNodeInfo, showText: Bool) {
    let actions = actionNames(node.element).sorted()
    let hasValueAttribute = attributeNames(node.element).contains(AXAttr.value)
    let settable = hasValueAttribute && isValueSettable(node.element)
    let enabled = boolAttribute(node.element, AXAttr.enabled)
    print("  \(nodeLine(node, showText: showText)) " +
          "actions=[\(actions.joined(separator: ","))] axPress=\(actions.contains("AXPress")) " +
          "axValueAttr=\(hasValueAttribute) axValueSettable=\(settable) " +
          "enabled=\(boolText(enabled))")
}

private func isSendLabel(_ value: String) -> Bool {
    let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
    return trimmed.caseInsensitiveCompare("Send") == .orderedSame ||
        trimmed == "发送" ||
        trimmed.caseInsensitiveCompare("Send Message") == .orderedSame
}

private func containsCI(_ haystack: String, _ needle: String) -> Bool {
    guard !needle.isEmpty else { return false }
    return haystack.range(of: needle,
                          options: [.caseInsensitive, .diacriticInsensitive]) != nil
}

private func isGenericWindowTitle(_ title: String) -> Bool {
    let trimmed = title.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
    return trimmed.isEmpty || trimmed == "feishu" || trimmed == "飞书" || trimmed == "lark"
}

// MARK: - Capture and environment

private func pixelSize(of url: URL) -> CGSize? {
    guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
          let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [String: Any],
          let width = (properties[kCGImagePropertyPixelWidth as String] as? NSNumber)?.intValue,
          let height = (properties[kCGImagePropertyPixelHeight as String] as? NSNumber)?.intValue else {
        return nil
    }
    return CGSize(width: CGFloat(width), height: CGFloat(height))
}

private func captureWindow(_ window: FeishuWindow, to outputURL: URL) -> Bool {
    let fileManager = FileManager.default
    do {
        try fileManager.createDirectory(at: outputURL.deletingLastPathComponent(),
                                        withIntermediateDirectories: true)
    } catch {
        print("Screenshot failed: could not create output directory: \(error.localizedDescription)")
        return false
    }

    try? fileManager.removeItem(at: outputURL)

    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
    process.arguments = ["-x", "-l", String(window.number), outputURL.path]
    print("Running: /usr/sbin/screencapture -x -l \(window.number) '\(displayPath(outputURL.path))'")
    let stderrPipe = Pipe()
    process.standardError = stderrPipe

    do {
        try process.run()
    } catch {
        print("Screenshot failed: could not launch screencapture: \(error.localizedDescription)")
        return false
    }

    let stderrData = stderrPipe.fileHandleForReading.readDataToEndOfFile()
    process.waitUntilExit()
    let stderrText = String(data: stderrData, encoding: .utf8)?
        .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""

    print("screencapture exit=\(process.terminationStatus), path=\(displayPath(outputURL.path))")
    if !stderrText.isEmpty {
        print("screencapture stderr: \(redactHomePath(stderrText))")
    }

    guard fileManager.fileExists(atPath: outputURL.path) else {
        print("Screenshot failed: output file was not created.")
        return false
    }

    let attributes = try? fileManager.attributesOfItem(atPath: outputURL.path)
    let bytes = (attributes?[.size] as? NSNumber)?.int64Value ?? 0
    let dimensions = pixelSize(of: outputURL)
    print("Screenshot bytes: \(bytes)")
    if let dimensions = dimensions {
        print("Screenshot pixels: \(Int(dimensions.width))x\(Int(dimensions.height)) " +
              "(AX bounds are points; Retina captures are typically 2x)")
        if window.bounds.width > 0 {
            print("Screenshot/window scale factor: " +
                  String(format: "%.2f", Double(dimensions.width / window.bounds.width)))
        }
    } else {
        print("Screenshot pixel size: <unreadable>")
    }

    return process.terminationStatus == 0 && bytes > 0
}

private func findRepoRoot() -> URL {
    let fileManager = FileManager.default
    var url = URL(fileURLWithPath: fileManager.currentDirectoryPath)
    while true {
        if fileManager.fileExists(atPath: url.appendingPathComponent("DESIGN.md").path) ||
            fileManager.fileExists(atPath: url.appendingPathComponent(".git").path) {
            return url
        }
        let parent = url.deletingLastPathComponent()
        if parent.path == url.path {
            return URL(fileURLWithPath: fileManager.currentDirectoryPath)
        }
        url = parent
    }
}

/// Shortens paths under the user's home directory so committed transcripts do not
/// embed the Mac username.
private func displayPath(_ path: String) -> String {
    let home = FileManager.default.homeDirectoryForCurrentUser.path
    if path == home { return "~" }
    let homePrefix = home.hasSuffix("/") ? home : home + "/"
    if path.hasPrefix(homePrefix) {
        return "~/" + String(path.dropFirst(homePrefix.count))
    }
    return path
}

/// Replaces the user's home-directory prefix anywhere in free-form text (for
/// example, a tool's stderr) so committed output does not embed the Mac username.
private func redactHomePath(_ text: String) -> String {
    let home = FileManager.default.homeDirectoryForCurrentUser.path
    return text.replacingOccurrences(of: home, with: "~")
}

private func iso8601Now() -> String {
    return ISO8601DateFormatter().string(from: Date())
}

private func processArchitecture() -> String {
#if arch(arm64)
    return "arm64"
#elseif arch(x86_64)
    return "x86_64"
#else
    return "unknown"
#endif
}

private func printAccessibilityGuidance() {
    print("")
    print("ACCESSIBILITY IS REQUIRED for the AX tree measurement.")
    print("  1. Open System Settings > Privacy & Security > Accessibility.")
    print("  2. Enable the app running this command (Terminal, iTerm2, or Claude Code).")
    print("  3. If it is not listed, click + and add it. Quit and reopen that app if a grant does not take effect.")
    print("  4. Re-run: swift spikes/macos-feishu/main.swift")
    print("  Without this grant AX calls fail with kAXErrorAPIDisabled (-25211).")
}

private func printScreenRecordingGuidance() {
    print("")
    print("SCREEN RECORDING IS REQUIRED only for the optional screenshot.")
    print("  1. Open System Settings > Privacy & Security > Screen Recording.")
    print("  2. Enable the app running this command (Terminal, iTerm2, or Claude Code).")
    print("  3. Quit and reopen that app if the grant does not take effect.")
    print("  Without this grant, `screencapture -l <windowNumber>` fails or produces a blank/black image.")
}

// MARK: - Entry point

@MainActor
private func runSpike() -> Int32 {
    // Unbuffered stdout so `tee` shows progress and a long walk is not lost if the
    // process is interrupted.
    setbuf(stdout, nil)

    let environment = ProcessInfo.processInfo.environment
    let showText = environment["KENNECT_SPIKE_SHOW_TEXT"] == "1"
    let captureRequested = environment["KENNECT_SPIKE_CAPTURE"] == "1"
    let screenshotOverride = environment["KENNECT_SPIKE_SCREENSHOT"]
    let ownerFilter = environment["KENNECT_FEISHU_OWNER"] ?? "Feishu"
    let environmentConversation = environment["KENNECT_TEST_CONVERSATION"]
    let argumentConversation = CommandLine.arguments.dropFirst().first

    let repoRoot = findRepoRoot()
    let defaultScreenshotURL = repoRoot.appendingPathComponent("docs/spikes/macos-feishu.png")
    let screenshotURL = URL(fileURLWithPath: screenshotOverride ?? defaultScreenshotURL.path)

    print("=== Kennect Computer spike: macOS Feishu accessibility ===")
    print("Time (UTC): \(iso8601Now())")
    print("Host: \(ProcessInfo.processInfo.operatingSystemVersionString), arch=\(processArchitecture())")
    print("Working directory: \(displayPath(FileManager.default.currentDirectoryPath))")
    print("Repo root: \(displayPath(repoRoot.path))")
    print("Mode: read-only for app content. No AX actions are invoked and no attributes are set.")
    print("Redaction: message text, chat titles, descriptions, and values print as <redacted: N chars>; " +
          "only structural UI labels are kept.")
    if showText {
        print("WARNING: KENNECT_SPIKE_SHOW_TEXT=1 is set. Raw value previews may contain real " +
              "message text. Never save or commit this transcript.")
    }
    print("")

    print("=== Permissions ===")
    // The option key is the documented string value of kAXTrustedCheckOptionPrompt.
    // Using the literal avoids CFString/Unmanaged import differences across toolchains.
    let accessibilityOptions = ["AXTrustedCheckOptionPrompt": true] as CFDictionary
    let accessibilityTrusted = AXIsProcessTrustedWithOptions(accessibilityOptions)
    print("Accessibility (AXIsProcessTrustedWithOptions): \(accessibilityTrusted ? "granted" : "DENIED")")

    let screenRecordingGranted = CGPreflightScreenCaptureAccess()
    print("Screen Recording (CGPreflightScreenCaptureAccess): \(screenRecordingGranted ? "granted" : "DENIED")")
    if !screenRecordingGranted {
        let requestResult = CGRequestScreenCaptureAccess()
        print("CGRequestScreenCaptureAccess(): \(requestResult) " +
              "(a System Settings prompt may appear; a grant may require an app restart)")
    }
    if !accessibilityTrusted {
        printAccessibilityGuidance()
    }
    if !screenRecordingGranted {
        printScreenRecordingGuidance()
    }
    print("")

    print("=== CGWindowListCopyWindowInfo: Feishu windows ===")
    let allWindows = enumerateWindows()
    var feishuWindows = allWindows.filter {
        $0.owner.compare(ownerFilter, options: .caseInsensitive) == .orderedSame
    }
    if feishuWindows.isEmpty {
        let fallback = allWindows.filter {
            let owner = $0.owner.lowercased()
            return owner.contains("feishu") || owner.contains("lark") || $0.owner.contains("飞书")
        }
        if !fallback.isEmpty {
            feishuWindows = fallback
            print("Exact owner \"\(ownerFilter)\" not found; using owner-name fallback (Feishu/Lark/飞书).")
        }
    }

    let layerZeroWindows = feishuWindows.filter {
        $0.layer == 0 && $0.bounds.width > 0 && $0.bounds.height > 0
    }
    print("Feishu owner matches: \(feishuWindows.count); layer 0 with nonzero bounds: \(layerZeroWindows.count)")

    if feishuWindows.isEmpty {
        let owners = Set(allWindows.map { $0.owner }).sorted()
        print("No Feishu window found.")
        print("Owner names seen: \(owners.joined(separator: ", "))")
        print("Set KENNECT_FEISHU_OWNER if Feishu reports a different owner name.")
        return 2
    }

    for (index, window) in feishuWindows.enumerated() {
        print("  [\(index)] windowNumber=\(window.number) owner=\"\(window.owner)\" " +
              "title=\(redactedWindowTitle(window.title)) bounds=\(rectText(window.bounds)) " +
              "layer=\(window.layer) onscreen=\(window.onscreen) pid=\(window.pid)")
    }

    let windowCandidates = layerZeroWindows.isEmpty ? feishuWindows : layerZeroWindows
    let sortedWindows = windowCandidates.sorted { lhs, rhs in
        if lhs.onscreen != rhs.onscreen { return lhs.onscreen && !rhs.onscreen }
        let leftArea = lhs.bounds.width * lhs.bounds.height
        let rightArea = rhs.bounds.width * rhs.bounds.height
        return leftArea > rightArea
    }
    guard let mainWindow = sortedWindows.first else {
        print("No usable Feishu window after filtering.")
        return 2
    }

    print("")
    print("Main window: number=\(mainWindow.number) pid=\(mainWindow.pid) owner=\"\(mainWindow.owner)\" " +
          "title=\(redactedWindowTitle(mainWindow.title)) bounds=\(rectText(mainWindow.bounds)) " +
          "onscreen=\(mainWindow.onscreen)")
    if mainWindow.title == nil {
        print("Window title unavailable: macOS omits kCGWindowName without Screen Recording permission.")
    }
    if !mainWindow.onscreen {
        print("WARNING: the chosen Feishu window is not on screen; bring it forward before capturing.")
    }
    if let running = NSRunningApplication(processIdentifier: mainWindow.pid) {
        print("App: bundleIdentifier=\(running.bundleIdentifier ?? "<unknown>") " +
              "localizedName=\(running.localizedName ?? "<unknown>")")
        if let bundleURL = running.bundleURL, let bundle = Bundle(url: bundleURL) {
            let shortVersion = bundle.infoDictionary?["CFBundleShortVersionString"] as? String ?? "<unknown>"
            let buildVersion = bundle.infoDictionary?["CFBundleVersion"] as? String ?? "<unknown>"
            print("App version: CFBundleShortVersionString=\(shortVersion) CFBundleVersion=\(buildVersion)")
        }
    }

    if !accessibilityTrusted {
        print("")
        print("AX tree measurement skipped because Accessibility is not granted. Grant it and re-run.")
        return 3
    }

    let appElement = AXUIElementCreateApplication(mainWindow.pid)
    _ = AXUIElementSetMessagingTimeout(appElement, 5.0)
    print("")
    print("AX application element: pid=\(mainWindow.pid) " +
          "role=\(stringAttribute(appElement, AXAttr.role) ?? "<none>")")

    print("")
    print("=== AX tree: main walk (application root, max depth \(maxMainDepth), cap \(maxNodes)) ===")
    var mainNodes: [AXNodeInfo] = []
    var mainWalkAttempts = 0
    for attempt in 1...3 {
        mainWalkAttempts = attempt
        mainNodes = walkTree(appElement, maxDepth: maxMainDepth, maxNodes: maxNodes)
        let namedStaticText = mainNodes.filter {
            $0.role == "AXStaticText" &&
                (!($0.valueString ?? "").isEmpty || !$0.title.isEmpty)
        }.count
        let largeContainers = mainNodes.filter {
            isLargeContainerRole($0.role) &&
                ($0.frame?.width ?? 0) > 100 && ($0.frame?.height ?? 0) > 100
        }.count
        print("Walk attempt \(attempt): nodes=\(mainNodes.count) " +
              "capReached=\(mainNodes.count >= maxNodes) " +
              "namedStaticText=\(namedStaticText) largeContainers=\(largeContainers)")
        if mainNodes.count >= maxNodes { break }
        if namedStaticText >= 5 && largeContainers >= 1 { break }
        if attempt < 3 {
            print("Tree looks cold/sparse; waiting 1500 ms and re-walking " +
                  "(Electron enables accessibility on demand).")
            Thread.sleep(forTimeInterval: 1.5)
        }
    }
    print("Main walk attempts: \(mainWalkAttempts), nodes: \(mainNodes.count), " +
          "capReached: \(mainNodes.count >= maxNodes)")
    if mainNodes.isEmpty {
        print("WARNING: the main walk returned zero nodes. Check Accessibility, that Feishu is " +
              "running, and the window state; re-run before treating the counts as zero.")
    }

    print("")
    print("=== Q1: node counts and roles ===")
    printRoleCounts(mainNodes, label: "Main walk (app root)")
    let interactiveMain = mainNodes.filter { interactiveRoles.contains($0.role) }
    let valuedMain = mainNodes.filter { !($0.valueString ?? "").isEmpty }
    print("Interactive-role nodes: \(interactiveMain.count) of \(mainNodes.count)")
    print("Nodes with non-empty string AXValue: \(valuedMain.count)")
    print("AXWindow nodes in main walk: \(mainNodes.filter { $0.role == "AXWindow" }.count)")
    print("Interactive-role vocabulary: \(interactiveRoles.sorted().joined(separator: ", "))")

    print("")
    print("=== Main-walk node dump (role/title/description/identifier/value/bounds) ===")
    for node in mainNodes {
        print(nodeLine(node, showText: showText))
    }

    print("")
    print("=== AX windows ===")
    var axWindows = applicationWindows(appElement)
    if axWindows.isEmpty {
        axWindows = mainNodes.filter { $0.role == "AXWindow" }.map { $0.element }
        if !axWindows.isEmpty {
            print("AXWindows was empty; using \(axWindows.count) AXWindow node(s) from the main walk.")
        }
    }
    print("AXWindows count used: \(axWindows.count)")
    var axWindowDescriptions: [(element: AXUIElement, role: String, title: String, frame: CGRect?)] = []
    for element in axWindows {
        let role = stringAttribute(element, AXAttr.role) ?? "<none>"
        let title = stringAttribute(element, AXAttr.title) ?? ""
        let frame = frameAttribute(element)
        axWindowDescriptions.append((element: element, role: role, title: title, frame: frame))
        print("  role=\(role) title=\(redactedWindowTitle(title)) bounds=\(rectText(frame))")
    }
    let matchedAXWindow = chooseMainAXWindow(axWindowDescriptions, matching: mainWindow.bounds)
    if let matched = matchedAXWindow {
        print("Chosen AX window for deep walk: title=\(redactedWindowTitle(matched.title)) " +
              "bounds=\(rectText(matched.frame))")
    } else {
        print("No AXWindow matched; the deep walk will use the application element.")
    }

    let deepRoot: AXUIElement
    let deepRootLabel: String
    if let matched = matchedAXWindow {
        deepRoot = matched.element
        deepRootLabel = "AXWindow"
    } else {
        deepRoot = appElement
        deepRootLabel = "application root"
    }

    print("")
    print("=== AX tree: deep walk (\(deepRootLabel), max depth \(maxDeepDepth), cap \(maxNodes)) ===")
    let deepNodes = walkTree(deepRoot, maxDepth: maxDeepDepth, maxNodes: maxNodes)
    print("Deep walk nodes: \(deepNodes.count), capReached: \(deepNodes.count >= maxNodes)")
    if deepNodes.isEmpty {
        print("WARNING: the deep walk returned zero nodes; the Q2-Q4 results are inconclusive.")
    }
    printRoleCounts(deepNodes, label: "Deep walk")
    let interactiveDeep = deepNodes.filter { interactiveRoles.contains($0.role) }
    print("Deep walk interactive-role nodes: \(interactiveDeep.count) of \(deepNodes.count)")
    print("Deep walk nodes with non-empty string AXValue: " +
          "\(deepNodes.filter { !($0.valueString ?? "").isEmpty }.count)")

    print("")
    print("=== Q2: message text exposure ===")
    let messageListRoles: Set<String> = ["AXList", "AXTable", "AXOutline", "AXScrollArea", "AXWebArea"]
    let messageLists = deepNodes.filter { messageListRoles.contains($0.role) }
    print("Message-list element candidates " +
          "(AXList/AXTable/AXOutline/AXScrollArea/AXWebArea): \(messageLists.count)")
    for node in messageLists.prefix(10) {
        let descendants = descendantIndices(of: node.index, in: deepNodes).count
        print("  \(nodeLine(node, showText: showText)) descendants=\(descendants)")
    }

    let messageContainer = chooseMessageContainer(deepNodes, windowBounds: mainWindow.bounds)
    if let container = messageContainer {
        print("Chosen message-region container: \(nodeLine(container, showText: showText))")
        let regionNodes = descendants(of: container.index, in: deepNodes)
        printTextStats(regionNodes, label: "Message region", showText: showText)
    } else {
        print("No message-region container matched the geometric filter; reporting the whole deep walk.")
        printTextStats(deepNodes, label: "Whole deep walk", showText: showText)
    }

    print("")
    print("=== Q3: composer candidates ===")
    let composerRoles: Set<String> = ["AXTextArea", "AXTextField", "AXComboBox"]
    let composerCandidates = deepNodes.filter { composerRoles.contains($0.role) }
    print("AXTextArea/AXTextField/AXComboBox nodes in deep walk: \(composerCandidates.count)")
    print("Note: axValueSettable=true is the AXSetValue path " +
          "(AXUIElementSetAttributeValue on AXValue); the probe only reads the flag.")
    for node in composerCandidates.prefix(20) {
        printComposerCandidate(node, showText: showText)
    }

    print("Focused UI element:")
    if let rawFocused = copyAttribute(appElement, AXAttr.focusedUIElement),
       let focused = rawFocused as? AXUIElement {
        let focusedInfo = makeNode(focused, index: 0, parentIndex: -1, depth: 0)
        let focusedActions = actionNames(focused).sorted()
        print("  \(nodeLine(focusedInfo, showText: showText)) " +
              "actions=[\(focusedActions.joined(separator: ","))] " +
              "axPress=\(focusedActions.contains("AXPress")) " +
              "axValueSettable=\(isValueSettable(focused))")
    } else {
        print("  <unreadable or none>")
    }

    let bottomBand = deepNodes.filter { node in
        guard let frame = node.frame else { return false }
        return frame.minY >= mainWindow.bounds.minY + mainWindow.bounds.height * 0.60
    }
    let bottomSettable = bottomBand.filter { isValueSettable($0.element) }
    print("Bottom-band nodes (y >= 60% of window): \(bottomBand.count); " +
          "with settable AXValue: \(bottomSettable.count)")
    for node in bottomSettable.prefix(20) {
        printComposerCandidate(node, showText: showText)
    }

    print("")
    print("=== Q4: Send candidates ===")
    let sendNamed = deepNodes.filter { node in
        isSendLabel(node.title) || isSendLabel(node.descriptionText)
    }
    let sendByIdentifier = deepNodes.filter { $0.identifier.lowercased().contains("send") }
    print("Nodes titled/described Send or 发送: \(sendNamed.count)")
    for node in sendNamed.prefix(20) {
        printButtonCandidate(node, showText: showText)
    }
    print("Nodes with identifier containing \"send\": \(sendByIdentifier.count)")
    for node in sendByIdentifier.prefix(20) {
        printButtonCandidate(node, showText: showText)
    }

    let bottomButtons = deepNodes.filter { node in
        guard node.role == "AXButton", let frame = node.frame else { return false }
        return frame.minY >= mainWindow.bounds.minY + mainWindow.bounds.height * 0.60
    }
    print("Bottom-band AXButton nodes (y >= 60%): \(bottomButtons.count)")
    for node in bottomButtons.prefix(30) {
        printButtonCandidate(node, showText: showText)
    }

    print("")
    print("=== Test conversation search ===")
    var conversationTerm = environmentConversation ?? argumentConversation
    var conversationSource = environmentConversation != nil ? "KENNECT_TEST_CONVERSATION" : "argument"
    if conversationTerm?.isEmpty ?? true {
        if let title = mainWindow.title, !isGenericWindowTitle(title) {
            conversationTerm = title
            conversationSource = "window title"
        }
    }
    if let term = conversationTerm, !term.isEmpty {
        print("Test conversation: <redacted: \(term.count) chars> (source: \(conversationSource))")
        let matches = deepNodes.filter { node in
            containsCI(node.title, term) ||
                containsCI(node.descriptionText, term) ||
                containsCI(node.valueString ?? "", term)
        }
        print("Nodes matching the test conversation name (title/description/value): \(matches.count)")
        let matchRoles = Dictionary(grouping: matches, by: { $0.role }).mapValues { $0.count }
        let roleSummary = matchRoles.sorted { $0.key < $1.key }
            .map { "\($0.key)=\($0.value)" }
            .joined(separator: ", ")
        print("Match roles: \(roleSummary.isEmpty ? "<none>" : roleSummary)")
        for node in matches.prefix(20) {
            print("  \(nodeLine(node, showText: showText))")
        }
        if matches.isEmpty {
            print("No match. Confirm the conversation is open and use the exact name, " +
                  "or set KENNECT_TEST_CONVERSATION.")
        }
    } else {
        print("Test conversation not supplied and no usable window title; name-match search skipped.")
        print("Set KENNECT_TEST_CONVERSATION=\"<test chat name>\" to enable it.")
    }

    print("")
    print("=== Window capture ===")
    var captureFailed = false
    if captureRequested {
        if !screenRecordingGranted {
            print("Screenshot skipped: Screen Recording is not granted. Grant it and re-run.")
        } else if !captureWindow(mainWindow, to: screenshotURL) {
            captureFailed = true
        }
    } else {
        print("Screenshot skipped: KENNECT_SPIKE_CAPTURE is not 1.")
        print("To capture manually: /usr/sbin/screencapture -x -l " +
              "\(mainWindow.number) '\(displayPath(screenshotURL.path))'")
    }

    print("")
    print("=== Summary (raw counts; write the findings on the Mac) ===")
    print("Main walk: nodes=\(mainNodes.count), " +
          "capReached=\(mainNodes.count >= maxNodes), attempts=\(mainWalkAttempts)")
    print("Deep walk: nodes=\(deepNodes.count), " +
          "capReached=\(deepNodes.count >= maxNodes), root=\(deepRootLabel)")
    print("Interactive-role nodes (main walk): \(interactiveMain.count)")
    print("Message region container: \(messageContainer == nil ? "<none>" : "selected")")
    print("Composer candidates (AXTextArea/AXTextField/AXComboBox): \(composerCandidates.count)")
    print("Bottom-band settable-AXValue nodes: \(bottomSettable.count)")
    print("Send-titled/described nodes: \(sendNamed.count); " +
          "identifier contains send: \(sendByIdentifier.count)")
    print("Bottom-band AXButton nodes: \(bottomButtons.count)")
    print("")
    print("Done. Read-only for app content: no AX actions invoked, no attributes set.")
    return captureFailed ? 4 : 0
}

exit(runSpike())
