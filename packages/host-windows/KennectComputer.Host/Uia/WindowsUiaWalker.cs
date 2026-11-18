using System.Drawing;

using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Patterns;
using FlaUI.UIA3;

using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Uia;

/// <summary>
/// Reads a window's raw-view UI Automation tree through FlaUI.UIA3.
///
/// A <c>CacheRequest</c> fetches the properties and patterns the slicer needs
/// in one cross-process call per element, which matters for Chromium apps whose
/// tree is both deep and slow. Control types are mapped to the generic,
/// platform-neutral role vocabulary and supported patterns to generic actions
/// so the protocol is not tied to Windows.
/// </summary>
public sealed class WindowsUiaWalker : IUiaWalker
{
    public TreeModel? Walk(nint hwnd, SnapshotOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (hwnd == 0)
        {
            return null;
        }

        using var automation = new UIA3Automation();
        using var cacheActivation = CreateCacheRequest(automation).Activate();

        AutomationElement root;
        try
        {
            root = automation.FromHandle(hwnd);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // UIA can throw a variety of COM exceptions for a dead or
            // unresponsive window; the snapshotter warms/retries and the
            // dispatcher re-checks the window afterwards.
            return null;
        }

        if (root is null)
        {
            return null;
        }

        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
        var state = new WalkState(options, cancellationToken);
        var tree = Build(rawWalker, root, depth: 0, insideDocument: false, state);
        return tree is null ? null : tree with { Truncated = state.Truncated };
    }

    private static TreeModel? Build(
        ITreeWalker walker,
        AutomationElement element,
        int depth,
        bool insideDocument,
        WalkState state)
    {
        state.CancellationToken.ThrowIfCancellationRequested();

        if (state.Count >= state.Options.MaxNodes)
        {
            state.Truncated = true;
            return null;
        }

        var controlType = Read(() => element.Properties.ControlType.ValueOrDefault, ControlType.Unknown);
        var isDocument = controlType == ControlType.Document;

        // R7/R11: a Document subtree carries the app's content far below the
        // generic depth cap; it may be walked to DocumentMaxDepth instead.
        var inDocument = insideDocument || isDocument;
        var depthLimit = inDocument
            ? Math.Max(state.Options.MaxDepth, SnapshotOptions.DocumentMaxDepth)
            : state.Options.MaxDepth;

        if (depth > depthLimit)
        {
            state.Truncated = true;
            return null;
        }

        state.Count++;

        var name = Read(() => element.Properties.Name.ValueOrDefault, string.Empty);
        var identifier = Read(() => element.Properties.AutomationId.ValueOrDefault, string.Empty);
        var bounds = Read(() => element.Properties.BoundingRectangle.ValueOrDefault, Rectangle.Empty);
        var enabled = Read(() => element.Properties.IsEnabled.ValueOrDefault, true);
        var offscreen = Read(() => element.Properties.IsOffscreen.ValueOrDefault, false);
        var focused = Read(() => element.Properties.HasKeyboardFocus.ValueOrDefault, false);

        // DESIGN §8 hard rule 4: a password field's value must never be
        // captured. The flag is cached with the element, and the value read is
        // skipped entirely for a password element.
        var isPassword = Read(() => element.Properties.IsPassword.ValueOrDefault, false);
        var value = isPassword ? null : ReadValue(element);

        var children = new List<TreeModel>();
        AutomationElement? child = Read<AutomationElement?>(
            () => walker.GetFirstChild(element),
            null);
        while (child is not null)
        {
            if (state.Count >= state.Options.MaxNodes)
            {
                state.Truncated = true;
                break;
            }

            var built = Build(walker, child, depth + 1, inDocument, state);
            if (built is not null)
            {
                children.Add(built);
            }

            child = Read<AutomationElement?>(() => walker.GetNextSibling(child), null);
        }

        return new TreeModel
        {
            Role = MapRole(controlType),
            Name = name ?? string.Empty,
            Identifier = string.IsNullOrEmpty(identifier) ? null : identifier,
            Value = value,
            IsPassword = isPassword,
            Bounds = ToRect(bounds),
            Enabled = enabled,
            Offscreen = offscreen,
            Focused = focused,
            IsModal = !offscreen && IsModal(element),
            Actions = ReadActions(element),
            Children = children,
        };
    }

    private static CacheRequest CreateCacheRequest(UIA3Automation automation)
    {
        // TreeScope.None is rejected by ElementFromHandleBuildCache; Element
        // asks for the properties of one element at a time, which is exactly
        // what the manual raw-view walk needs (subtree caching would defeat the
        // node cap). Full mode keeps a native reference so traversal works.
        var cache = new CacheRequest
        {
            TreeScope = TreeScope.Element,
            AutomationElementMode = AutomationElementMode.Full,
        };

        var properties = automation.PropertyLibrary.Element;
        cache.Add(properties.Name);
        cache.Add(properties.AutomationId);
        cache.Add(properties.ControlType);
        cache.Add(properties.BoundingRectangle);
        cache.Add(properties.IsEnabled);
        cache.Add(properties.IsOffscreen);
        cache.Add(properties.HasKeyboardFocus);
        cache.Add(properties.IsPassword);

        // The value is read through the value pattern for edit-like controls;
        // the pattern is cached here so the read stays inside the element's
        // single cross-process snapshot.
        cache.Add(automation.PropertyLibrary.Value.Value);

        var patterns = automation.PatternLibrary;
        cache.Add(patterns.InvokePattern);
        cache.Add(patterns.TogglePattern);
        cache.Add(patterns.SelectionItemPattern);
        cache.Add(patterns.ExpandCollapsePattern);
        cache.Add(patterns.ValuePattern);
        cache.Add(patterns.RangeValuePattern);
        cache.Add(patterns.TextEditPattern);
        cache.Add(patterns.TextPattern);
        cache.Add(patterns.ScrollPattern);
        cache.Add(patterns.ScrollItemPattern);
        cache.Add(patterns.WindowPattern);
        return cache;
    }

    /// <summary>
    /// Captures the element's value or text for host-internal content matching
    /// (expect, find, wait). The value pattern covers edit-like controls
    /// cheaply; the text pattern carries document content. The pattern lookup
    /// and value property are cached with the element, so the only extra call
    /// is the text read itself, and only for elements that expose text.
    /// Empty values stay null so the model does not carry noise.
    /// </summary>
    private static string? ReadValue(AutomationElement element)
    {
        var valuePattern = Read<IValuePattern?>(
            () => element.Patterns.Value.PatternOrDefault,
            null);
        if (valuePattern is not null)
        {
            var value = Read<string?>(
                () => valuePattern.Value.ValueOrDefault,
                null);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        var textPattern = Read<ITextPattern?>(
            () => element.Patterns.Text.PatternOrDefault,
            null);
        if (textPattern is not null)
        {
            var text = Read<string?>(
                () => textPattern.DocumentRange.GetText(-1),
                null);
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return null;
    }

    internal static List<string> ReadActions(AutomationElement element)
    {
        var actions = new List<string>();

        if (Supports(() => element.Patterns.Invoke.IsSupported))
        {
            actions.Add("press");
        }

        if (Supports(() => element.Patterns.Toggle.IsSupported))
        {
            actions.Add("toggle");
        }

        if (Supports(() => element.Patterns.SelectionItem.IsSupported))
        {
            actions.Add("select");
        }

        if (Supports(() => element.Patterns.ExpandCollapse.IsSupported))
        {
            actions.Add("expand");
        }

        if (Supports(() => element.Patterns.Value.IsSupported)
            || Supports(() => element.Patterns.RangeValue.IsSupported)
            || Supports(() => element.Patterns.TextEdit.IsSupported))
        {
            actions.Add("setValue");
        }

        if (Supports(() => element.Patterns.Scroll.IsSupported))
        {
            actions.Add("scroll");
        }

        if (Supports(() => element.Patterns.ScrollItem.IsSupported))
        {
            actions.Add("scrollIntoView");
        }

        return actions;
    }

    private static bool IsModal(AutomationElement element)
    {
        try
        {
            if (!element.Patterns.Window.IsSupported)
            {
                return false;
            }

            var window = element.Patterns.Window.PatternOrDefault;
            return window is not null && window.IsModal.ValueOrDefault;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Supports(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static T Read<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    internal static Rect ToRect(Rectangle bounds) => new()
    {
        X = bounds.X,
        Y = bounds.Y,
        Width = Math.Max(0, bounds.Width),
        Height = Math.Max(0, bounds.Height),
    };

    internal static string MapRole(ControlType controlType) => controlType switch
    {
        ControlType.Button => "button",
        ControlType.Edit => "edit",
        ControlType.Document => "document",
        ControlType.Text => "text",
        ControlType.Group => "group",
        ControlType.List => "list",
        ControlType.ListItem => "listitem",
        ControlType.Menu => "menu",
        ControlType.MenuBar => "menubar",
        ControlType.MenuItem => "menuitem",
        ControlType.Tab => "tab",
        ControlType.TabItem => "tabitem",
        ControlType.Tree => "tree",
        ControlType.TreeItem => "treeitem",
        ControlType.CheckBox => "checkbox",
        ControlType.RadioButton => "radiobutton",
        ControlType.ComboBox => "combobox",
        ControlType.Hyperlink => "hyperlink",
        ControlType.Slider => "slider",
        ControlType.Spinner => "spinner",
        ControlType.ProgressBar => "progressbar",
        ControlType.ScrollBar => "scrollbar",
        ControlType.Image => "image",
        ControlType.Pane => "pane",
        ControlType.Window => "window",
        ControlType.TitleBar => "titlebar",
        ControlType.ToolBar => "toolbar",
        ControlType.ToolTip => "tooltip",
        ControlType.StatusBar => "statusbar",
        ControlType.SplitButton => "splitbutton",
        ControlType.DataGrid => "datagrid",
        ControlType.DataItem => "dataitem",
        ControlType.Table => "table",
        ControlType.Calendar => "calendar",
        ControlType.Custom => "custom",
        ControlType.Header => "header",
        ControlType.HeaderItem => "headeritem",
        ControlType.Separator => "separator",
        ControlType.Thumb => "thumb",
        ControlType.AppBar => "appbar",
        ControlType.SemanticZoom => "semanticzoom",
        _ => controlType.ToString().ToLowerInvariant(),
    };

    private sealed class WalkState
    {
        public WalkState(SnapshotOptions options, CancellationToken cancellationToken)
        {
            Options = options;
            CancellationToken = cancellationToken;
        }

        public SnapshotOptions Options { get; }

        public CancellationToken CancellationToken { get; }

        public int Count { get; set; }

        public bool Truncated { get; set; }
    }
}
