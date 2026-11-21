using RslCompanionUploader.Api;
using Xunit;

namespace RslCompanionUploader.Tests;

/// <summary>
/// An upload of a game account another profile held moves it here (RaidTools: one record per game
/// account), and the response says until when the previous owner can take it back.
/// </summary>
public class UploadMigrationResponseTests
{
    [Fact]
    public void A_response_that_opened_a_move_carries_its_deadline()
    {
        var body = "{\"userId\":1,\"name\":\"X\",\"message\":\"Imported successfully\","
                 + "\"migration\":{\"migratedAt\":\"2026-11-21T12:00:00Z\",\"deletableFrom\":\"2026-11-28T12:00:00Z\"}}";
        Assert.Equal(new DateTimeOffset(2026, 11, 28, 12, 0, 0, TimeSpan.Zero), RslCompanionApiClient.MigrationDeadline(body));
    }

    [Theory]
    [InlineData("{\"userId\":1,\"message\":\"Imported successfully\"}")]
    [InlineData("{\"userId\":1,\"migration\":null}")]
    [InlineData("not json")]
    [InlineData("")]
    public void An_ordinary_response_has_no_deadline(string body)
    {
        Assert.Null(RslCompanionApiClient.MigrationDeadline(body));
    }

    [Fact]
    public void A_response_that_updated_another_profiles_copy_says_so()
    {
        Assert.True(RslCompanionApiClient.UpdatedAnotherProfile("{\"userId\":1,\"updatedAnotherProfile\":true}"));
        Assert.False(RslCompanionApiClient.UpdatedAnotherProfile("{\"userId\":1}"));
        Assert.False(RslCompanionApiClient.UpdatedAnotherProfile("not json"));
    }

    [Fact]
    public void The_holder_answer_reads_both_flags()
    {
        var holder = System.Text.Json.JsonSerializer.Deserialize<AccountHolder>("{\"heldElsewhere\":true,\"canMove\":false}");
        Assert.Equal(new AccountHolder(true, false), holder);
    }
}
