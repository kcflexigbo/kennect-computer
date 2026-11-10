using System.Text.Json;
using System.Text.Json.Nodes;
using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Tests;

public sealed class DtoConformanceTests
{
    [Fact]
    public void HostStatusRoundTripsTheSharedFixture()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "status.json");
        Assert.True(File.Exists(fixturePath), $"Shared fixture was not copied to the test output: {fixturePath}");
        var fixtureJson = File.ReadAllText(fixturePath);

        var status = JsonSerializer.Deserialize<HostStatus>(fixtureJson, ProtocolJson.Options);
        Assert.NotNull(status);
        Assert.Equal("0.1.0", status.HostVersion);
        Assert.Equal(1, status.ProtocolVersion);
        Assert.Equal("windows", status.Platform);
        Assert.False(status.Locked);

        var monitor = Assert.Single(status.Monitors);
        Assert.Equal("mon_primary", monitor.Id);
        Assert.Equal("Primary", monitor.Name);
        Assert.Equal(0, monitor.Bounds.X);
        Assert.Equal(0, monitor.Bounds.Y);
        Assert.Equal(2560, monitor.Bounds.Width);
        Assert.Equal(1440, monitor.Bounds.Height);
        Assert.Equal(1.5, monitor.Scale);
        Assert.True(monitor.Primary);

        Assert.True(status.Permissions.Accessibility);
        Assert.True(status.Permissions.ScreenRecording);

        var roundTripped = JsonSerializer.SerializeToNode(status, ProtocolJson.Options);
        var original = JsonNode.Parse(fixtureJson);
        Assert.True(
            JsonNode.DeepEquals(original, roundTripped),
            $"HostStatus did not round-trip the shared fixture.\noriginal: {original}\nround-trip: {roundTripped}");

        var root = roundTripped!.AsObject();
        Assert.Equal(6, root.Count);
        foreach (var key in new[] { "hostVersion", "protocolVersion", "platform", "locked", "monitors", "permissions" })
        {
            Assert.True(root.ContainsKey(key), $"Missing status key: {key}");
        }

        var monitorObject = root["monitors"]!.AsArray()[0]!.AsObject();
        Assert.Equal(5, monitorObject.Count);
        foreach (var key in new[] { "id", "name", "bounds", "scale", "primary" })
        {
            Assert.True(monitorObject.ContainsKey(key), $"Missing monitor key: {key}");
        }

        var boundsObject = monitorObject["bounds"]!.AsObject();
        Assert.Equal(4, boundsObject.Count);
        foreach (var key in new[] { "x", "y", "width", "height" })
        {
            Assert.True(boundsObject.ContainsKey(key), $"Missing bounds key: {key}");
        }

        var permissionsObject = root["permissions"]!.AsObject();
        Assert.Equal(2, permissionsObject.Count);
        Assert.True(permissionsObject.ContainsKey("accessibility"));
        Assert.True(permissionsObject.ContainsKey("screenRecording"));
    }
}
