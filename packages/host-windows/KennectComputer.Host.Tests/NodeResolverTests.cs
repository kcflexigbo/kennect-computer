using System.Reflection;

using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;

namespace KennectComputer.Host.Tests;

public sealed class NodeResolverTests
{
    [Fact]
    public void ResolverReturnsTheLiveNodeWhoseFingerprintMatches()
    {
        var target = SnapshotButton();
        var live = FakeUiaNode.From(target);
        var resolver = new FakeUiaNodeResolver(live);

        var resolved = resolver.Resolve((nint)42, target, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Same(live, resolved);
        AssertIdentityMatches(target, resolved);
        Assert.Equal((nint)42, resolver.LastHwnd);
        Assert.Same(target, resolver.LastTarget);
    }

    [Fact]
    public void ResolvedNodeExposesTheIdentityTheFingerprintVerified()
    {
        var target = SnapshotButton();
        var live = FakeUiaNode.From(target);

        var resolved = new FakeUiaNodeResolver(live)
            .Resolve((nint)42, target, CancellationToken.None);

        Assert.NotNull(resolved);
        AssertIdentityMatches(target, resolved);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("name")]
    [InlineData("identifier")]
    [InlineData("x")]
    [InlineData("y")]
    [InlineData("width")]
    [InlineData("height")]
    public void FingerprintRejectsAChangedIdentityField(string field)
    {
        var target = SnapshotButton();

        var fingerprint = NodeFingerprint.From(target);

        Assert.True(fingerprint.Matches(target));
        Assert.False(fingerprint.Matches(WithFieldChanged(target, field)));
    }

    [Theory]
    [InlineData("role")]
    [InlineData("name")]
    [InlineData("identifier")]
    [InlineData("x")]
    [InlineData("y")]
    [InlineData("width")]
    [InlineData("height")]
    public void ResolverReturnsNullWhenTheLiveIdentityChanged(string field)
    {
        var target = SnapshotButton();
        var live = FakeUiaNode.From(WithFieldChanged(target, field));
        var resolver = new FakeUiaNodeResolver(live);

        var resolved = resolver.Resolve((nint)42, target, CancellationToken.None);

        Assert.Null(resolved);
    }

    [Fact]
    public void FingerprintIgnoresTheCapturedTextValue()
    {
        // Typing changes the value but not the identity: a later action on the
        // same field must still resolve. The fingerprint is role, name,
        // identifier, and bounds only.
        var target = SnapshotButton() with { Value = "before" };

        var afterTyping = target with { Value = "after" };

        Assert.True(NodeFingerprint.From(target).Matches(afterTyping));
    }

    [Fact]
    public void TargetWithoutIdentifierMatchesOnRoleNameAndBounds()
    {
        var target = SnapshotButton() with { Identifier = null };
        var live = FakeUiaNode.From(target);
        var liveModel = new TreeModel
        {
            Role = live.Role,
            Name = live.Name,
            Identifier = live.Identifier,
            Bounds = live.Bounds,
        };
        var resolver = new FakeUiaNodeResolver(live);

        Assert.Null(live.Identifier);
        Assert.True(NodeFingerprint.From(target).Matches(liveModel));
        Assert.Same(live, resolver.Resolve((nint)7, target, CancellationToken.None));
    }

    [Fact]
    public void FakeNodeReturnsTheConfiguredText()
    {
        // Later action tests read text through this fake, so the seam must
        // return exactly what the test configured, control characters and
        // non-ASCII included.
        var node = new FakeUiaNode { Text = "こんにちは world" };

        Assert.True(node.TryGetText(out var text));
        Assert.Equal("こんにちは world", text);
    }

    [Fact]
    public void FakeNodeExposesTheValueSetByLaterActions()
    {
        var node = new FakeUiaNode { Value = "before" };

        Assert.True(node.TrySetValue("after"));
        Assert.True(node.TryGetValue(out var value));
        Assert.Equal("after", value);
        Assert.Contains("TrySetValue:after", node.Calls);
    }

    [Fact]
    public void FakeNodeReportsUnsupportedPatternsAsFalse()
    {
        var node = new FakeUiaNode { SupportsSetValue = false, SupportsGetText = false };

        Assert.False(node.TrySetValue("ignored"));
        Assert.False(node.TryGetText(out var text));
        Assert.Null(text);
    }

    [Fact]
    public void TreeModelValueRoundTripsWithoutSerialization()
    {
        var model = new TreeModel { Role = "edit", Name = "Editor", Value = "draft text" };

        Assert.Equal("draft text", model.Value);
        Assert.Equal("draft text", (model with { Name = "Renamed" }).Value);
    }

    [Fact]
    public void ProtocolNodeShapeStaysFrozen()
    {
        // Value is host-internal state for expect/find/wait; it must never be
        // added to the protocol Node shape.
        Assert.Null(typeof(Node).GetProperty("Value", BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void ResolvesAndReadsAnOwnedWindowThroughFlaUi()
    {
        // Integration without launching an external app: the test owns this
        // non-activating window and closes it. It covers the real walker's
        // value capture, the FlaUI resolver lookup, and the live node wrapper.
        using var window = OwnedTestWindow.Start();

        var capture = new UiaSnapshotter(new WindowsUiaWalker(), _ => { }, _ => { })
            .Capture(
                window.Handle,
                window.Bounds,
                new SnapshotOptions { Mode = SnapshotMode.A11y },
                CancellationToken.None);

        var buttonNode = capture.Snapshot.Nodes.Single(
            node => node.Role == "button" && node.Name == "Action 0");
        var editorNode = capture.Snapshot.Nodes.Single(
            node => node.Role == "edit" && node.Name == "Editor");
        var buttonTarget = capture.NodeMap[buttonNode.NodeId];
        var editorTarget = capture.NodeMap[editorNode.NodeId];

        // The walker keeps the textbox value for host-side matching; the
        // protocol node still omits it.
        Assert.Equal("Text", editorTarget.Value);

        var resolver = new WindowsUiaNodeResolver();

        var button = resolver.Resolve(window.Handle, buttonTarget, CancellationToken.None);
        Assert.NotNull(button);
        Assert.Equal("button", button.Role);
        Assert.Equal("Action 0", button.Name);
        AssertIdentityMatches(buttonTarget, button);
        Assert.True(button.TryInvoke());

        var editor = resolver.Resolve(window.Handle, editorTarget, CancellationToken.None);
        Assert.NotNull(editor);
        Assert.Equal("edit", editor.Role);
        AssertIdentityMatches(editorTarget, editor);
        Assert.True(editor.TryGetValue(out var value));
        Assert.Equal("Text", value);
        Assert.True(editor.TrySetValue("Changed"));
        Assert.True(editor.TryGetValue(out value));
        Assert.Equal("Changed", value);

        // The editor is still found by name and role, but its moved bounds make
        // the fingerprint fail, which the resolver reports as a stale node.
        Assert.Null(resolver.Resolve(
            window.Handle,
            editorTarget with
            {
                Bounds = new Rect(
                    editorTarget.Bounds.X + 500,
                    editorTarget.Bounds.Y,
                    editorTarget.Bounds.Width,
                    editorTarget.Bounds.Height),
            },
            CancellationToken.None));
    }

    /// <summary>
    /// The node handed to callers must expose exactly the identity the
    /// resolver's fingerprint check accepted.
    /// </summary>
    private static void AssertIdentityMatches(TreeModel target, IUiaNode node)
    {
        Assert.Equal(target.Role, node.Role);
        Assert.Equal(target.Name, node.Name);
        Assert.Equal(target.Identifier, node.Identifier);
        Assert.Equal(target.Bounds.X, node.Bounds.X);
        Assert.Equal(target.Bounds.Y, node.Bounds.Y);
        Assert.Equal(target.Bounds.Width, node.Bounds.Width);
        Assert.Equal(target.Bounds.Height, node.Bounds.Height);
        Assert.Equal(
            NodeFingerprint.From(target),
            NodeFingerprint.From(new TreeModel
            {
                Role = node.Role,
                Name = node.Name,
                Identifier = node.Identifier,
                Bounds = node.Bounds,
            }));
    }

    private static TreeModel SnapshotButton() => new()
    {
        Role = "button",
        Name = "Send",
        Identifier = "send",
        Bounds = new Rect(10, 20, 120, 32),
        Actions = ["press"],
    };

    private static TreeModel WithFieldChanged(TreeModel model, string field) => field switch
    {
        "role" => model with { Role = "link" },
        "name" => model with { Name = "Send now" },
        "identifier" => model with { Identifier = "send-2" },
        "x" => model with
        {
            Bounds = new Rect(model.Bounds.X + 1, model.Bounds.Y, model.Bounds.Width, model.Bounds.Height),
        },
        "y" => model with
        {
            Bounds = new Rect(model.Bounds.X, model.Bounds.Y + 1, model.Bounds.Width, model.Bounds.Height),
        },
        "width" => model with
        {
            Bounds = new Rect(model.Bounds.X, model.Bounds.Y, model.Bounds.Width + 1, model.Bounds.Height),
        },
        "height" => model with
        {
            Bounds = new Rect(model.Bounds.X, model.Bounds.Y, model.Bounds.Width, model.Bounds.Height + 1),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown identity field."),
    };
}

/// <summary>
/// A deterministic live node for action tests. Identity properties are init
/// only; the behavioral flags and text/value storage let each test configure
/// the pattern results it needs. Every semantic call is recorded in
/// <see cref="Calls"/> so action tests can assert ordering.
/// </summary>
internal sealed record FakeUiaNode : IUiaNode
{
    public string Role { get; init; } = "button";

    public string Name { get; init; } = string.Empty;

    public string? Identifier { get; init; }

    public Rect Bounds { get; init; } = new();

    public bool Enabled { get; init; } = true;

    public bool Focused { get; init; }

    public IReadOnlyList<string> Actions { get; init; } = [];

    public string? Text { get; set; }

    public string? Value { get; set; }

    public bool SupportsInvoke { get; init; } = true;

    public bool SupportsToggle { get; init; } = true;

    public bool SupportsSelect { get; init; } = true;

    public bool SupportsExpand { get; init; } = true;

    public bool SupportsFocus { get; init; } = true;

    public bool SupportsSetValue { get; init; } = true;

    public bool SupportsGetText { get; init; } = true;

    public bool SupportsGetValue { get; init; } = true;

    public List<string> Calls { get; } = [];

    public static FakeUiaNode From(TreeModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return new FakeUiaNode
        {
            Role = model.Role,
            Name = model.Name,
            Identifier = model.Identifier,
            Bounds = new Rect(
                model.Bounds.X,
                model.Bounds.Y,
                model.Bounds.Width,
                model.Bounds.Height),
            Actions = model.Actions,
        };
    }

    public bool TryInvoke() => Record(nameof(TryInvoke), SupportsInvoke);

    public bool TryToggle() => Record(nameof(TryToggle), SupportsToggle);

    public bool TrySelect() => Record(nameof(TrySelect), SupportsSelect);

    public bool TryExpand() => Record(nameof(TryExpand), SupportsExpand);

    public bool TryFocus() => Record(nameof(TryFocus), SupportsFocus);

    public bool TrySetValue(string value)
    {
        Calls.Add($"{nameof(TrySetValue)}:{value}");

        if (!SupportsSetValue)
        {
            return false;
        }

        Value = value;
        return true;
    }

    public bool TryGetText(out string? text)
    {
        Calls.Add(nameof(TryGetText));
        text = SupportsGetText ? Text : null;
        return SupportsGetText;
    }

    public bool TryGetValue(out string? value)
    {
        Calls.Add(nameof(TryGetValue));
        value = SupportsGetValue ? Value : null;
        return SupportsGetValue;
    }

    private bool Record(string call, bool supported)
    {
        Calls.Add(call);
        return supported;
    }
}

/// <summary>
/// Mirrors the production resolution contract without touching the desktop:
/// the fake live node is returned only when its identity matches the target's
/// fingerprint. Later action tests use this to drive stale-snapshot paths.
/// </summary>
internal sealed class FakeUiaNodeResolver : IUiaNodeResolver
{
    private readonly IUiaNode? _node;

    public FakeUiaNodeResolver(IUiaNode? node)
    {
        _node = node;
    }

    public nint? LastHwnd { get; private set; }

    public TreeModel? LastTarget { get; private set; }

    public IUiaNode? Resolve(nint hwnd, TreeModel target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        LastHwnd = hwnd;
        LastTarget = target;

        if (_node is null)
        {
            return null;
        }

        var live = new TreeModel
        {
            Role = _node.Role,
            Name = _node.Name,
            Identifier = _node.Identifier,
            Bounds = _node.Bounds,
        };

        return NodeFingerprint.From(target).Matches(live) ? _node : null;
    }
}
