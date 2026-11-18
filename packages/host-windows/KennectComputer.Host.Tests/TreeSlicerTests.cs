using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;

namespace KennectComputer.Host.Tests;

public sealed class TreeSlicerTests
{
    [Fact]
    public void CapsNodesByDepthAndCount()
    {
        var tree = TreeModel.Nested(depth: 20, childrenPerLevel: 3);

        var slice = TreeSlicer.Slice(tree, new SnapshotOptions { MaxDepth = 5, MaxNodes = 10 });

        Assert.True(slice.Truncated);
        Assert.True(slice.Nodes.Count <= 10);
    }

    [Fact]
    public void NodeIdsCarryTheSnapshotGeneration()
    {
        var id = NodeId.Create(7, 3);

        Assert.True(NodeId.TryParse(id, out var generation, out var index));
        Assert.Equal(7, generation);
        Assert.Equal(3, index);
    }

    [Theory]
    [InlineData("")]
    [InlineData("n")]
    [InlineData("x1:2")]
    [InlineData("n1")]
    [InlineData("n1:")]
    [InlineData("n:2")]
    [InlineData("n1:2:3")]
    [InlineData("n-1:2")]
    [InlineData("n1:-2")]
    [InlineData("n 1:2")]
    public void RejectsMalformedNodeIds(string nodeId)
    {
        Assert.False(NodeId.TryParse(nodeId, out _, out _));
    }

    [Fact]
    public void SnapshotOptionsCarryTheProtocolDefaults()
    {
        var options = new SnapshotOptions();

        Assert.Equal(12, options.MaxDepth);
        Assert.Equal(1500, options.MaxNodes);
        Assert.False(options.Image);
        Assert.Equal(1568, options.ImageLongEdge);
        Assert.Equal(2576, options.MaxImageLongEdge);
        Assert.Equal(SnapshotMode.Both, options.Mode);
    }

    [Fact]
    public void DropsOffscreenAndDisabledNodes()
    {
        var tree = Window(
            new TreeModel { Role = "button", Name = "Visible", Actions = ["press"] },
            new TreeModel { Role = "button", Name = "Hidden", Offscreen = true, Actions = ["press"] },
            new TreeModel { Role = "button", Name = "Disabled", Enabled = false, Actions = ["press"] });

        var slice = TreeSlicer.Slice(tree, new SnapshotOptions());

        var node = Assert.Single(slice.Nodes);
        Assert.Equal("Visible", node.Name);
    }

    [Fact]
    public void CollapsesUnnamedContainersAndHoistsTheirChildren()
    {
        var tree = Window(
            new TreeModel
            {
                Role = "group",
                Name = string.Empty,
                Children =
                [
                    new TreeModel { Role = "button", Name = "OK", Actions = ["press"] },
                ],
            },
            new TreeModel
            {
                Role = "group",
                Name = "Toolbar",
                Children =
                [
                    new TreeModel { Role = "button", Name = "Save", Actions = ["press"] },
                ],
            });

        var slice = TreeSlicer.Slice(tree, new SnapshotOptions());

        Assert.Equal(["OK", "Toolbar", "Save"], slice.Nodes.Select(node => node.Name));
    }

    [Fact]
    public void KeepsUnnamedButActionableGroupNodes()
    {
        // R8: Feishu exposes the composer as a Group with TextEdit/SelectionItem
        // patterns and the Send affordance as an unnamed Button. Dropping them
        // would make the M3 flow impossible, so actions make a node interactive.
        var tree = Window(
            new TreeModel { Role = "group", Name = string.Empty, Actions = ["setValue"] },
            new TreeModel { Role = "button", Name = string.Empty, Actions = ["press"] });

        var slice = TreeSlicer.Slice(tree, new SnapshotOptions());

        Assert.Equal(2, slice.Nodes.Count);
        Assert.Contains("focus", slice.Nodes[0].Actions);
        Assert.Contains("setValue", slice.Nodes[0].Actions);
        Assert.Contains("press", slice.Nodes[1].Actions);
    }

    [Fact]
    public void DropsUnnamedTextNodes()
    {
        var tree = Window(
            new TreeModel { Role = "text", Name = string.Empty },
            new TreeModel { Role = "text", Name = "Conversation" });

        var slice = TreeSlicer.Slice(tree, new SnapshotOptions());

        var node = Assert.Single(slice.Nodes);
        Assert.Equal("Conversation", node.Name);
    }

    [Fact]
    public void KeepsTextNodesWithContent()
    {
        var tree = Window(new TreeModel { Role = "text", Name = "Hello" });

        var slice = TreeSlicer.Slice(tree, new SnapshotOptions());

        var node = Assert.Single(slice.Nodes);
        Assert.Equal("text", node.Role);
        Assert.Empty(node.Actions);
    }

    [Fact]
    public void WalksDocumentSubtreesPastTheGenericDepthCap()
    {
        // R7/R11: a Document subtree carries the web-app content far below the
        // generic depth cap; the slicer must not re-truncate what the walker
        // deliberately descended into.
        var deepUnderDocument = Chain(
            levels: 8,
            leaf: new TreeModel { Role = "button", Name = "Send", Actions = ["press"] });
        var document = Chain(
            levels: 2,
            leaf: new TreeModel { Role = "document", Name = "Conversation", Children = [deepUnderDocument] });

        var deepOutsideDocument = Chain(
            levels: 9,
            leaf: new TreeModel { Role = "button", Name = "Buried" });

        var tree = Window(
            document,
            deepOutsideDocument);

        var slice = TreeSlicer.Slice(tree, new SnapshotOptions { MaxDepth = 5 });

        Assert.Contains(slice.Nodes, node => node.Name == "Send");
        Assert.DoesNotContain(slice.Nodes, node => node.Name == "Buried");
        Assert.True(slice.Truncated);
    }

    [Fact]
    public void SanitizesNamesAndDropsControlCharacters()
    {
        var tree = Window(
            new TreeModel { Role = "button", Name = "  Save\n\tfile\u0001  now  " });

        var slice = TreeSlicer.Slice(tree, new SnapshotOptions());

        Assert.Equal("Save file now", Assert.Single(slice.Nodes).Name);
    }

    [Fact]
    public void CapsSanitizedNames()
    {
        var longName = new string('a', TreeSlicer.MaxNameLength + 50);
        var tree = Window(new TreeModel { Role = "button", Name = longName });

        var slice = TreeSlicer.Slice(tree, new SnapshotOptions());

        Assert.Equal(TreeSlicer.MaxNameLength, Assert.Single(slice.Nodes).Name.Length);
    }

    [Fact]
    public void ClampsNegativeBoundsToTheProtocolDomain()
    {
        var tree = Window(new TreeModel
        {
            Role = "button",
            Name = "Odd",
            Bounds = new Rect { X = -5, Y = 10, Width = -1, Height = 20 },
        });

        var node = Assert.Single(TreeSlicer.Slice(tree, new SnapshotOptions()).Nodes);

        Assert.Equal(-5, node.Bounds.X);
        Assert.Equal(10, node.Bounds.Y);
        Assert.Equal(0, node.Bounds.Width);
        Assert.Equal(20, node.Bounds.Height);
    }

    [Fact]
    public void NodeMapMapsEachNodeIdToItsSourceNode()
    {
        var button = new TreeModel { Role = "button", Name = "OK", Actions = ["press"] };
        var tree = Window(button);

        var slice = TreeSlicer.Slice(tree, new SnapshotOptions(), generation: 3);

        var node = Assert.Single(slice.Nodes);
        Assert.Equal("n3:1", node.NodeId);
        Assert.Same(button, slice.NodeMap[node.NodeId]);
    }

    [Fact]
    public void NodeIdsStartAtOnePerGeneration()
    {
        var tree = Window(
            new TreeModel { Role = "button", Name = "One", Actions = ["press"] },
            new TreeModel { Role = "button", Name = "Two", Actions = ["press"] });

        var slice = TreeSlicer.Slice(tree, new SnapshotOptions(), generation: 1);

        Assert.Equal(["n1:1", "n1:2"], slice.Nodes.Select(node => node.NodeId));
    }

    private static TreeModel Window(params TreeModel[] children)
        => new() { Role = "window", Name = "Test window", Children = children };

    private static TreeModel Chain(int levels, TreeModel leaf)
    {
        var current = leaf;
        for (var level = 0; level < levels; level++)
        {
            current = new TreeModel
            {
                Role = "group",
                Name = string.Empty,
                Children = [current],
            };
        }

        return current;
    }
}
