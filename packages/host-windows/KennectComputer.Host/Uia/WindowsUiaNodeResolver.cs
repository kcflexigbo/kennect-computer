using System.Drawing;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Uia;

/// <summary>
/// Resolves snapshot nodes against the live window through FlaUI.UIA3.
///
/// The live tree is searched by platform identifier when the node has one,
/// otherwise by name and generic role, and a candidate is accepted only when
/// its <see cref="NodeFingerprint"/> matches exactly. Any mismatch returns
/// null (the caller reports a stale snapshot); provider failures while reading
/// an element are treated the same way instead of escaping as internal errors.
/// A fresh automation is created per call because pipe requests may arrive on
/// different threads; the node keeps the element it needs for its patterns.
/// </summary>
public sealed class WindowsUiaNodeResolver : IUiaNodeResolver
{
    public IUiaNode? Resolve(nint hwnd, TreeModel target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (hwnd == 0)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var fingerprint = NodeFingerprint.From(target);

        using var automation = new UIA3Automation();

        AutomationElement? window;
        try
        {
            window = automation.FromHandle(hwnd);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A dead or unresponsive window fails the attach; that is a stale
            // snapshot, not an internal failure.
            return null;
        }

        if (window is null)
        {
            return null;
        }

        try
        {
            // Snapshot node maps hold descendants, but a find or a future
            // caller may target the window element itself.
            if (Matches(fingerprint, window))
            {
                return new WindowsUiaNode(window);
            }

            foreach (var candidate in FindCandidates(window, target))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Matches(fingerprint, candidate))
                {
                    return new WindowsUiaNode(candidate);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Providers can throw while enumerating or reading a disappearing
            // element. The element cannot be acted on safely, so report stale.
            return null;
        }

        return null;
    }

    private static AutomationElement[] FindCandidates(AutomationElement window, TreeModel target)
    {
        var condition = BuildCondition(window.ConditionFactory, target);

        return condition is null
            ? window.FindAllDescendants()
            : window.FindAllDescendants(condition);
    }

    /// <summary>
    /// Builds the cheapest lookup that the fingerprint can still verify:
    /// identifier when present, otherwise name and role when available. A node
    /// with neither a name nor a mappable role falls back to scanning the
    /// window's descendants, where the fingerprint disambiguates by bounds.
    /// </summary>
    private static ConditionBase? BuildCondition(ConditionFactory factory, TreeModel target)
    {
        if (!string.IsNullOrEmpty(target.Identifier))
        {
            return factory.ByAutomationId(target.Identifier);
        }

        ConditionBase? condition = string.IsNullOrEmpty(target.Name)
            ? null
            : factory.ByName(target.Name);

        if (TryMapRole(target.Role, out var controlType))
        {
            var roleCondition = factory.ByControlType(controlType);
            condition = condition is null ? roleCondition : condition.And(roleCondition);
        }

        return condition;
    }

    /// <summary>
    /// Maps the generic role vocabulary back to a UIA control type. The walker
    /// emits lower-case control-type names, so the reverse parse covers every
    /// role it can produce.
    /// </summary>
    private static bool TryMapRole(string? role, out ControlType controlType)
    {
        controlType = ControlType.Unknown;

        return !string.IsNullOrWhiteSpace(role)
            && Enum.TryParse(role, ignoreCase: true, out controlType);
    }

    private static bool Matches(NodeFingerprint fingerprint, AutomationElement element)
        => fingerprint.Matches(IdentityOf(element));

    private static TreeModel IdentityOf(AutomationElement element)
    {
        var controlType = WindowsUiaWalker.Read(
            () => element.Properties.ControlType.ValueOrDefault,
            ControlType.Unknown);
        var name = WindowsUiaWalker.Read(
            () => element.Properties.Name.ValueOrDefault,
            string.Empty);
        var identifier = WindowsUiaWalker.Read(
            () => element.Properties.AutomationId.ValueOrDefault,
            string.Empty);
        var bounds = WindowsUiaWalker.Read(
            () => element.Properties.BoundingRectangle.ValueOrDefault,
            Rectangle.Empty);

        return new TreeModel
        {
            Role = WindowsUiaWalker.MapRole(controlType),
            Name = name ?? string.Empty,
            Identifier = string.IsNullOrEmpty(identifier) ? null : identifier,
            Bounds = WindowsUiaWalker.ToRect(bounds),
        };
    }
}

/// <summary>
/// The FlaUI-backed <see cref="IUiaNode"/>. Identity, state, and supported
/// actions are captured when the resolver accepts the element; pattern
/// operations are fetched from the live element on demand so a vanished
/// element reports <c>false</c> rather than acting on a replacement control.
/// </summary>
internal sealed class WindowsUiaNode : IUiaNode
{
    private readonly AutomationElement _element;

    internal WindowsUiaNode(AutomationElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        _element = element;

        var controlType = WindowsUiaWalker.Read(
            () => element.Properties.ControlType.ValueOrDefault,
            ControlType.Unknown);
        var name = WindowsUiaWalker.Read(
            () => element.Properties.Name.ValueOrDefault,
            string.Empty);
        var identifier = WindowsUiaWalker.Read(
            () => element.Properties.AutomationId.ValueOrDefault,
            string.Empty);
        var bounds = WindowsUiaWalker.Read(
            () => element.Properties.BoundingRectangle.ValueOrDefault,
            Rectangle.Empty);

        Role = WindowsUiaWalker.MapRole(controlType);
        Name = name ?? string.Empty;
        Identifier = string.IsNullOrEmpty(identifier) ? null : identifier;
        Bounds = WindowsUiaWalker.ToRect(bounds);
        Enabled = WindowsUiaWalker.Read(
            () => element.Properties.IsEnabled.ValueOrDefault,
            true);
        Focused = WindowsUiaWalker.Read(
            () => element.Properties.HasKeyboardFocus.ValueOrDefault,
            false);
        Actions = WindowsUiaWalker.ReadActions(element);
    }

    public string Role { get; }

    public string Name { get; }

    public string? Identifier { get; }

    public Rect Bounds { get; }

    public bool Enabled { get; }

    public bool Focused { get; }

    public IReadOnlyList<string> Actions { get; }

    public bool TryInvoke()
        => TryPattern(
            () => _element.Patterns.Invoke.PatternOrDefault,
            pattern => pattern.Invoke());

    public bool TryToggle()
        => TryPattern(
            () => _element.Patterns.Toggle.PatternOrDefault,
            pattern => pattern.Toggle());

    public bool TrySelect()
        => TryPattern(
            () => _element.Patterns.SelectionItem.PatternOrDefault,
            pattern => pattern.Select());

    public bool TryExpand()
        => TryPattern(
            () => _element.Patterns.ExpandCollapse.PatternOrDefault,
            pattern => pattern.Expand());

    public bool TryFocus()
    {
        try
        {
            _element.Focus();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TrySetValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            var valuePattern = _element.Patterns.Value.PatternOrDefault;
            if (valuePattern is not null)
            {
                if (valuePattern.IsReadOnly.ValueOrDefault)
                {
                    return false;
                }

                valuePattern.SetValue(value);
                return true;
            }

            var legacy = _element.Patterns.LegacyIAccessible.PatternOrDefault;
            if (legacy is not null)
            {
                legacy.SetValue(value);
                return true;
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryGetText(out string? text)
    {
        text = null;

        try
        {
            var textPattern = _element.Patterns.Text.PatternOrDefault;
            if (textPattern is null)
            {
                return false;
            }

            text = textPattern.DocumentRange.GetText(-1);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryGetValue(out string? value)
    {
        value = null;

        try
        {
            var valuePattern = _element.Patterns.Value.PatternOrDefault;
            if (valuePattern is not null)
            {
                value = valuePattern.Value.ValueOrDefault;
                return true;
            }

            var legacy = _element.Patterns.LegacyIAccessible.PatternOrDefault;
            if (legacy is not null)
            {
                value = legacy.Value.ValueOrDefault;
                return true;
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryPattern<T>(Func<T?> getPattern, Action<T> action)
        where T : class
    {
        try
        {
            var pattern = getPattern();
            if (pattern is null)
            {
                return false;
            }

            action(pattern);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
