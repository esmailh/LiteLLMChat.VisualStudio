using System.Collections.Generic;
using System.Linq;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class McpAvailabilityTests
{
    private static McpServerConfiguration Server(string name, bool enabled = true, string solutionRoot = "")
    {
        return new McpServerConfiguration
        {
            Id = name,
            Name = name,
            Enabled = enabled,
            SolutionRoot = solutionRoot
        };
    }

    private static UserSettings Settings(params McpServerConfiguration[] servers)
    {
        return new UserSettings
        {
            EnableMcp = true,
            EnableToolCalling = true,
            McpServers = servers.ToList()
        };
    }

    [Fact]
    public void Off_WhenTheFeatureSwitchIsOff()
    {
        var settings = Settings(Server("files"));
        settings.EnableMcp = false;

        var availability = McpAvailability.For(settings, null);

        Assert.Equal(McpAvailabilityState.Off, availability.State);
        Assert.False(availability.IsEnabled);
        Assert.Empty(availability.EnabledServers);
    }

    [Fact]
    public void Off_WhenToolCallingIsOff()
    {
        var settings = Settings(Server("files"));
        settings.EnableToolCalling = false;

        var availability = McpAvailability.For(settings, null);

        Assert.Equal(McpAvailabilityState.Off, availability.State);
        Assert.Contains("tool calling", availability.Headline);
    }

    [Fact]
    public void NotConfigured_WhenNoServerExists()
    {
        var availability = McpAvailability.For(Settings(), null);

        Assert.Equal(McpAvailabilityState.NotConfigured, availability.State);
        Assert.False(availability.IsEnabled);
        Assert.Contains("No MCP servers", availability.Headline);
    }

    [Fact]
    public void AllDisabled_WhenEveryServerIsTurnedOff()
    {
        var availability = McpAvailability.For(Settings(Server("files", enabled: false), Server("db", enabled: false)), null);

        Assert.Equal(McpAvailabilityState.AllDisabled, availability.State);
        Assert.False(availability.IsEnabled);
        Assert.Equal(2, availability.ConfiguredCount);
        Assert.Contains("turned off", availability.Detail);
    }

    [Fact]
    public void AllDisabled_WhenEveryServerBelongsToAnotherSolution()
    {
        var availability = McpAvailability.For(
            Settings(Server("files", solutionRoot: @"C:\other")), @"C:\solution");

        Assert.Equal(McpAvailabilityState.AllDisabled, availability.State);
        Assert.Contains("limited to another solution", availability.Detail);
    }

    [Fact]
    public void Enabled_WhenAtLeastOneServerApplies()
    {
        var settings = Settings(
            Server("files", enabled: false),
            Server("db"),
            Server("report", solutionRoot: @"C:\other"));

        var availability = McpAvailability.For(settings, @"C:\solution");

        Assert.Equal(McpAvailabilityState.Enabled, availability.State);
        Assert.True(availability.IsEnabled);
        Assert.Equal(1, availability.EnabledCount);
        Assert.Equal("db", Assert.Single(availability.EnabledServers).Name);
    }

    [Fact]
    public void Enabled_EvenWhenTheOnlyEnabledServerIsNotConnected()
    {
        // Availability is configuration, not connectivity: the window draws a failed connection in the tooltip and
        // must not tell the user the server they enabled is off.
        var availability = McpAvailability.For(Settings(Server("files")), null);

        Assert.True(availability.IsEnabled);
    }

    [Fact]
    public void ReasonFor_BlamesTheFeatureSwitchBeforeTheServer()
    {
        var settings = Settings();
        settings.EnableMcp = false;

        var reason = McpAvailability.ReasonFor(settings, Server("files", enabled: false), null);

        Assert.Equal(McpInactiveReason.FeatureOff, reason);
    }

    [Fact]
    public void Fingerprint_ChangesWhenAServerIsToggled()
    {
        var server = Server("files");
        var settings = Settings(server);
        var before = McpAvailability.For(settings, null).Fingerprint;

        server.Enabled = false;
        var after = McpAvailability.For(settings, null).Fingerprint;

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Fingerprint_ChangesWhenTheSolutionChanges()
    {
        var settings = Settings(Server("files"));

        var first = McpAvailability.For(settings, @"C:\one").Fingerprint;
        var second = McpAvailability.For(settings, @"C:\two").Fingerprint;

        Assert.NotEqual(first, second);
    }
}
