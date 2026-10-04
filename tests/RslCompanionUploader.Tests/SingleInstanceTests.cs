using System.Collections.Concurrent;
using Xunit;

namespace RslCompanionUploader.Tests;

/// <summary>
/// The second-launch → running-instance path: a <c>sync</c> URI started by the browser has to arrive
/// intact at the primary, which then parses it exactly like a fresh launch. Runs the real named pipe
/// under a throwaway name, so it never touches an installed copy that happens to be running.
/// </summary>
public class SingleInstanceTests
{
    private const string SiteLaunch =
        "rslcompanion-extractor://sync?code=Zx9_-abc123&api=https%3A%2F%2Fapi.rslcompanion.com";

    private static string UniquePipe() => $"RslCompanionUploader.Tests.{Guid.NewGuid():N}";

    [Fact]
    public void A_forwarded_sync_launch_arrives_whole_and_parses_like_a_fresh_one()
    {
        var pipe = UniquePipe();
        var received = new BlockingCollection<string[]>();
        using var channel = LaunchChannel.Listen(pipe, received.Add);

        string[] sent = [SiteLaunch];
        Assert.True(LaunchChannel.TrySend(pipe, sent, TimeSpan.FromSeconds(5)));
        Assert.True(received.TryTake(out var args, TimeSpan.FromSeconds(5)));

        Assert.Equal(sent, args);
        var launch = ProtocolHandler.TryGetHandoff(args)!;
        Assert.Equal("Zx9_-abc123", launch.Code);
        Assert.Same(ApiTarget.Production, launch.ResolveTarget(new AppConfig()));
    }

    [Fact]
    public void Successive_launches_each_get_through()
    {
        // The site's "Update Data" can be pressed again after a failure; the listener must still be
        // there for the second and third.
        var pipe = UniquePipe();
        var received = new BlockingCollection<string[]>();
        using var channel = LaunchChannel.Listen(pipe, received.Add);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(LaunchChannel.TrySend(pipe, [$"rslcompanion-extractor://sync?code=c{i}"], TimeSpan.FromSeconds(5)));
            Assert.True(received.TryTake(out var args, TimeSpan.FromSeconds(5)));
            Assert.Equal($"c{i}", ProtocolHandler.TryGetHandoff(args)!.Code);
        }
    }

    [Fact]
    public void Arguments_survive_spaces_newlines_and_non_ascii()
    {
        // The old wire format joined on '\n', so an argument containing one split in two.
        var pipe = UniquePipe();
        var received = new BlockingCollection<string[]>();
        using var channel = LaunchChannel.Listen(pipe, received.Add);

        string[] sent = ["--flag", "a b\nc", "ünïcødé ✓", "", SiteLaunch];
        Assert.True(LaunchChannel.TrySend(pipe, sent, TimeSpan.FromSeconds(5)));
        Assert.True(received.TryTake(out var args, TimeSpan.FromSeconds(5)));
        Assert.Equal(sent, args);
    }

    [Fact]
    public void With_nobody_listening_the_send_reports_failure_instead_of_success()
    {
        // A false here is what makes the second process wait for a closing primary and take over,
        // rather than exiting with the only copy of a single-use code.
        Assert.False(LaunchChannel.TrySend(UniquePipe(), [SiteLaunch], TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void A_launch_that_arrives_before_the_window_is_ready_is_held_not_dropped()
    {
        var delivered = new List<string[]>();
        try
        {
            SingleInstance.SetHandler(null);
            SingleInstance.Deliver([SiteLaunch]);

            SingleInstance.SetHandler(delivered.Add);
            Assert.Single(delivered);
            Assert.Equal(SiteLaunch, delivered[0][0]);

            SingleInstance.Deliver(["rslcompanion-extractor://ping"]);
            Assert.Equal(2, delivered.Count);
        }
        finally
        {
            SingleInstance.SetHandler(null);
        }
    }
}
