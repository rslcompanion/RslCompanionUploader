using System.Text.Json.Nodes;
using NewParserOpus.Il2Cpp;
using Xunit;

namespace RslCompanionUploader.Tests;

/// <summary>
/// KnownOffsets.RecordLearnedRvas and WriteAtomically: a learned class RVA fills a gap in the build's
/// local entry, replaces a stored value only when a run proves the stored one wrong and its own right,
/// and the file is never left torn or littered with temp files.
/// </summary>
public sealed class LearnedRvaTests : IDisposable
{
    private const string Hash = "A66241F0379D12FD8204084C03C3BF06292F2A0E3416FA5CB582889DDC40F414";
    private const ulong Right = 0x4FFFAD8, Wrong = 0x4FB50C0;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rva-tests-" + Guid.NewGuid().ToString("N"));
    private string CatalogPath => Path.Combine(_dir, "calibrated-offsets.json");

    public LearnedRvaTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteCatalog(ulong appModel) => File.WriteAllText(CatalogPath, $$"""
        { "builds": { "{{Hash}}": { "gameAssemblyHash": "{{Hash}}", "gameVersion": "11.80.0",
          "appModel_KlassTypeInfoRva": {{appModel}}, "userContext_KlassTypeInfoRva": 84049608 } } }
        """);

    private ulong StoredAppModel() =>
        JsonNode.Parse(File.ReadAllText(CatalogPath))!["builds"]![Hash]!["appModel_KlassTypeInfoRva"]!.GetValue<ulong>();

    private static OffsetDatabase Learned(ulong appModel) =>
        new() { GameAssemblyHash = Hash, AppModel_KlassTypeInfoRva = appModel, UserContext_KlassTypeInfoRva = 84049608 };

    /// <summary>A stand-in for Il2CppRuntime.KlassRvaResolvesFor: only <see cref="Right"/> resolves.</summary>
    private static bool OnlyRightResolves(string field, ulong rva) => rva == Right;

    [Fact]
    public void A_zero_is_filled_with_the_learned_value()
    {
        WriteCatalog(0);
        Assert.True(KnownOffsets.RecordLearnedRvas(Learned(Right), catalogPath: CatalogPath));
        Assert.Equal(Right, StoredAppModel());
    }

    [Fact]
    public void A_wrong_stored_value_is_replaced_once_the_run_proves_its_own()
    {
        WriteCatalog(Wrong);
        Assert.True(KnownOffsets.RecordLearnedRvas(Learned(Right), OnlyRightResolves, CatalogPath));
        Assert.Equal(Right, StoredAppModel());
    }

    [Fact]
    public void A_stored_value_that_resolves_is_never_replaced()
    {
        WriteCatalog(Right);
        Assert.False(KnownOffsets.RecordLearnedRvas(Learned(Wrong), (_, rva) => true, CatalogPath));
        Assert.Equal(Right, StoredAppModel());
    }

    [Fact]
    public void Without_a_verifier_only_zeros_are_filled()
    {
        WriteCatalog(Wrong);
        Assert.False(KnownOffsets.RecordLearnedRvas(Learned(Right), catalogPath: CatalogPath));
        Assert.Equal(Wrong, StoredAppModel());
    }

    [Fact]
    public void A_build_the_catalog_does_not_hold_is_never_added()
    {
        File.WriteAllText(CatalogPath, """{ "builds": {} }""");
        Assert.False(KnownOffsets.RecordLearnedRvas(Learned(Right), OnlyRightResolves, CatalogPath));
        Assert.DoesNotContain(Hash, File.ReadAllText(CatalogPath));
    }

    [Fact]
    public void An_atomic_write_replaces_the_file_and_leaves_no_temp_file()
    {
        File.WriteAllText(CatalogPath, "old");
        KnownOffsets.WriteAtomically(CatalogPath, "new");
        Assert.Equal("new", File.ReadAllText(CatalogPath));
        Assert.Single(Directory.GetFiles(_dir));
    }
}
