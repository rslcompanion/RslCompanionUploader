using System.Text.Json;
using System.Text.Json.Nodes;
using NewParserOpus.Extractors;
using NewParserOpus.Models;
using Xunit;

namespace RslCompanionUploader.Tests;

/// <summary>
/// <c>grimForest.difficulties[].goldenGoblins</c> (schema 50): the account's slot → tiers record joined to the
/// static map's slot → zone, and the names it goes on the wire under (RaidTools binds them as they are).
/// The numbers are the live Hard map of rotation 12 (2026-11-19).
/// </summary>
public sealed class GrimForestGoblinsTests
{
    private const int Hard = 2;

    private static readonly Dictionary<int, int> Zones = new()
    {
        [4] = 1401, [77] = 1402, [133] = 1402, [202] = 1402, [252] = 1402, [342] = 1403, [5] = 1401,
    };

    private static Dictionary<int, List<int>> LiveHard() => new()
    {
        [342] = [12_000_000, 8_000_000],                                // claim order; exported ascending
        [4] = [2_000_000, 4_000_000, 7_000_000, 10_000_000],
        [202] = [4_000_000, 7_000_000, 13_000_000, 20_000_000],
        [77] = [4_000_000, 7_000_000, 13_000_000, 20_000_000],
        [133] = [4_000_000, 7_000_000, 13_000_000, 20_000_000],
    };

    private static readonly int[] LiveHardWon = [14012201, 14022201, 14022202, 14022204, 14032201, 14012006];

    [Fact]
    public void Each_goblin_fought_is_listed_by_zone_then_slot_with_its_tiers_ascending()
    {
        var goblins = GrimForestGoblins.Build(Hard, LiveHard(), Zones, LiveHardWon)!;

        Assert.Equal([(1401, 4), (1402, 77), (1402, 133), (1402, 202), (1403, 342)],
            goblins.Select(g => (g.Zone, g.SlotId ?? 0)));
        Assert.Equal([8_000_000, 12_000_000], goblins[^1].PrizesTaken);
    }

    [Fact]
    public void Nothing_fought_is_an_empty_list_and_an_unread_input_is_absent()
    {
        Assert.Empty(GrimForestGoblins.Build(1, new Dictionary<int, List<int>>(), Zones, [14011001])!);
        Assert.Null(GrimForestGoblins.Build(Hard, null, Zones, LiveHardWon));
        Assert.Null(GrimForestGoblins.Build(Hard, LiveHard(), null, LiveHardWon));
    }

    [Fact]
    public void A_goblin_slot_with_no_zone_drops_the_whole_list_rather_than_one_goblin()
    {
        var prizes = LiveHard();
        prizes[999] = [4_000_000];
        Assert.Null(GrimForestGoblins.Build(Hard, prizes, Zones, LiveHardWon));
    }

    [Fact]
    public void A_goblin_stage_won_with_no_tier_list_in_its_zone_is_still_listed()
    {
        // Zone 1402's fourth goblin (stage 14022203) won with nothing paid, if the game keeps no list for that.
        int[] won = [.. LiveHardWon, 14022203];
        var goblins = GrimForestGoblins.Build(Hard, LiveHard(), Zones, won)!;

        var extra = Assert.Single(goblins, g => g.SlotId is null);
        Assert.Equal(1402, extra.Zone);
        Assert.Empty(extra.PrizesTaken);
        Assert.Equal(6, goblins.Count);
    }

    [Fact]
    public void Won_goblin_stages_of_the_other_difficulty_or_other_kinds_add_nothing()
    {
        // 14021201 is Normal's goblin; 14022101 a Hard side boss (1xx); 14022301 a skull (3xx).
        int[] won = [.. LiveHardWon, 14021201, 14022101, 14022301];
        Assert.Equal(5, GrimForestGoblins.Build(Hard, LiveHard(), Zones, won)!.Count);
    }

    [Fact]
    public void The_wire_names_are_zone_slotId_and_prizesTaken_and_an_unread_list_is_omitted()
    {
        var read = new GrimForestDifficultyProgress { DifficultyId = Hard, GoldenGoblins = GrimForestGoblins.Build(Hard, LiveHard(), Zones, null) };
        var unread = new GrimForestDifficultyProgress { DifficultyId = 1 };

        var first = JsonNode.Parse(JsonSerializer.Serialize(read))!["goldenGoblins"]![0]!.AsObject();
        Assert.Equal(["zone", "slotId", "prizesTaken"], first.Select(p => p.Key));
        Assert.False(JsonNode.Parse(JsonSerializer.Serialize(unread))!.AsObject().ContainsKey("goldenGoblins"));
        Assert.False(JsonNode.Parse(JsonSerializer.Serialize(unread))!.AsObject().ContainsKey("goblinPrizesBySlot"));
    }
}
