using KennectComputer.Host.Config;
using KennectComputer.Host.Policy;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Tests;

public sealed class ConfigTests
{
    [Fact]
    public void ExcludedProcessesAreFilteredFromTheWindowList()
    {
        // R34: RawWindow is (Hwnd, Pid, Title, ProcessName, Bounds); the process
        // exclusion must be read from ProcessName.
        var policy = new ExclusionPolicy(
            new AppConfig { Privacy = new PrivacyConfig { ExcludedProcesses = ["Feishu.exe"] } });
        var api = FakeWindowApi.With(
            normal: new RawWindow(1, 100, "Feishu", "Feishu.exe", new Rect(0, 0, 10, 10)));
        var registry = new WindowRegistry(api);

        var list = new WindowEnumerator(api, registry, policy).List();

        Assert.Empty(list);
        // Filtered before registration: the excluded window never gets an ID.
        Assert.False(registry.TryResolve("w_1", out _));
    }

    [Fact]
    public void ExcludedTitlePatternsAreFiltered()
    {
        var policy = new ExclusionPolicy(
            new AppConfig { Privacy = new PrivacyConfig { ExcludedTitles = ["*password*"] } });
        var api = FakeWindowApi.With(
            normal: new RawWindow(1, 100, "My password vault", "Chrome", new Rect(0, 0, 10, 10)));
        var list = new WindowEnumerator(api, new WindowRegistry(api), policy).List();

        Assert.Empty(list);
    }

    [Fact]
    public void ProcessExclusionsIgnoreCaseAndTheExeSuffix()
    {
        var policy = new ExclusionPolicy(
            new AppConfig { Privacy = new PrivacyConfig { ExcludedProcesses = ["KeePass.exe"] } });

        Assert.True(policy.IsExcluded("keepass", "Anything"));
        Assert.True(policy.IsExcluded("KEEPASS.EXE", "Anything"));
        Assert.False(policy.IsExcluded("notepad", "Anything"));
    }

    [Fact]
    public void TitleExclusionsAreCaseInsensitiveGlobs()
    {
        var policy = new ExclusionPolicy(
            new AppConfig
            {
                Privacy = new PrivacyConfig { ExcludedTitles = ["*password*", "*bank*"] },
            });

        Assert.True(policy.IsExcluded("chrome", "My PASSWORD vault"));
        Assert.True(policy.IsExcluded("chrome", "Bank of America"));
        Assert.True(policy.IsExcluded("chrome", "password"));
        Assert.False(policy.IsExcluded("chrome", "Untitled - Notepad"));
    }

    [Fact]
    public void NonExcludedWindowsStayListable()
    {
        var policy = new ExclusionPolicy(
            new AppConfig { Privacy = new PrivacyConfig { ExcludedProcesses = ["Feishu.exe"] } });
        var api = FakeWindowApi.With(
            normal: new RawWindow(1, 100, "Notepad", "Notepad", new Rect(0, 0, 10, 10)));
        var list = new WindowEnumerator(api, new WindowRegistry(api), policy).List();

        Assert.Single(list);
    }

    [Fact]
    public void TheDefaultConfigMatchesTheDesign()
    {
        var config = ConfigStore.LoadFrom(ConfigStore.FindDefaultPath(), userConfigPath: null);

        Assert.True(config.Host.StartOnDemand);
        Assert.Equal("Ctrl+Alt+Shift+Esc", config.Control.StopHotkey.Windows);
        Assert.Equal("Ctrl+Alt+Shift+Esc", config.Control.StopHotkey.Macos);
        Assert.Equal("per-app", config.Control.AuthorizationMode);
        Assert.Equal("off", config.Control.ConsequentialGate);
        Assert.Equal(3000, config.Control.ConsequentialCountdownMs);
        Assert.Equal("both", config.Perception.DefaultMode);
        // The JSON defaults and the code defaults must not drift apart: the
        // dispatcher falls back to these values when a request omits them.
        Assert.Equal(SnapshotOptions.DefaultMaxDepth, config.Perception.MaxDepth);
        Assert.Equal(SnapshotOptions.DefaultMaxNodes, config.Perception.MaxNodes);
        Assert.Equal(SnapshotOptions.DefaultImageLongEdge, config.Perception.ImageLongEdge);
        Assert.Equal(SnapshotOptions.DefaultMaxImageLongEdge, config.Perception.MaxImageLongEdge);
        Assert.Contains("KeePass.exe", config.Privacy.ExcludedProcesses);
        Assert.Contains("1Password.exe", config.Privacy.ExcludedProcesses);
        Assert.Contains("LogonUI.exe", config.Privacy.ExcludedProcesses);
        Assert.Contains("*password*", config.Privacy.ExcludedTitles);
        Assert.Contains("*bank*", config.Privacy.ExcludedTitles);
        Assert.Equal(7, config.Privacy.AuditRetentionDays);
        Assert.True(config.Apps["Feishu"].PreAuthorized);
        Assert.Equal("off", config.Apps["Feishu"].ConsequentialGate);
        Assert.True(config.Apps.ContainsKey("feishu"));
    }

    [Fact]
    public void TheCompiledConfigDefaultsMatchTheSnapshotDefaults()
    {
        // AppConfig is also used when a user config omits a section; it must
        // agree with SnapshotOptions, which supplies the record defaults.
        var perception = new PerceptionConfig();

        Assert.Equal(SnapshotOptions.DefaultMaxDepth, perception.MaxDepth);
        Assert.Equal(SnapshotOptions.DefaultMaxNodes, perception.MaxNodes);
        Assert.Equal(SnapshotOptions.DefaultImageLongEdge, perception.ImageLongEdge);
        Assert.Equal(SnapshotOptions.DefaultMaxImageLongEdge, perception.MaxImageLongEdge);
        Assert.Equal("both", perception.DefaultMode);
        Assert.Equal(7, new PrivacyConfig().AuditRetentionDays);
    }

    [Fact]
    public void AUserConfigOverlaysTheDefaults()
    {
        var directory = NewTempDir();
        try
        {
            var defaults = Path.Combine(directory, "default.json");
            var user = Path.Combine(directory, "user.json");
            File.WriteAllText(defaults, """
                {
                  "host": { "startOnDemand": true },
                  "perception": { "maxDepth": 12, "maxNodes": 1500 },
                  "privacy": { "excludedTitles": ["*password*"], "auditRetentionDays": 7 }
                }
                """);
            File.WriteAllText(user, """
                {
                  "perception": { "maxNodes": 500 },
                  "privacy": { "excludedTitles": ["*secret*"] }
                }
                """);

            var config = ConfigStore.LoadFrom(defaults, user);

            Assert.Equal(12, config.Perception.MaxDepth);
            Assert.Equal(500, config.Perception.MaxNodes);
            // Arrays replace rather than concatenate.
            Assert.Equal(["*secret*"], config.Privacy.ExcludedTitles);
            Assert.Equal(7, config.Privacy.AuditRetentionDays);
            Assert.True(config.Host.StartOnDemand);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AMissingUserConfigIsFine()
    {
        var directory = NewTempDir();
        try
        {
            var defaults = Path.Combine(directory, "default.json");
            File.WriteAllText(defaults, """{ "perception": { "maxDepth": 20 } }""");

            var config = ConfigStore.LoadFrom(
                defaults,
                Path.Combine(directory, "does-not-exist.json"));

            Assert.Equal(20, config.Perception.MaxDepth);
            Assert.Equal(1500, config.Perception.MaxNodes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheEnvironmentVariableOverridesTheDefaultConfigPath()
    {
        var previous = Environment.GetEnvironmentVariable(ConfigStore.OverrideEnvironmentVariable);
        try
        {
            var overridePath = Path.Combine(Path.GetTempPath(), "kennect-override.json");
            Environment.SetEnvironmentVariable(
                ConfigStore.OverrideEnvironmentVariable,
                overridePath);

            Assert.Equal(
                Path.GetFullPath(overridePath),
                ConfigStore.ResolveDefaultPath(@"C:\repo\config\default.json"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                ConfigStore.OverrideEnvironmentVariable,
                previous);
        }
    }

    [Fact]
    public void AMalformedConfigFailsClearly()
    {
        var directory = NewTempDir();
        try
        {
            var malformed = Path.Combine(directory, "config.json");
            File.WriteAllText(malformed, """{ "perception": """);

            var exception = Assert.Throws<InvalidDataException>(
                () => ConfigStore.LoadFrom(malformed, userConfigPath: null));

            Assert.Contains(malformed, exception.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AMissingDefaultConfigFailsClearly()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "default.json");

        var exception = Assert.Throws<FileNotFoundException>(
            () => ConfigStore.LoadFrom(missing, userConfigPath: null));

        Assert.Contains(missing, exception.Message);
    }

    private static string NewTempDir()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "kennect-config-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
