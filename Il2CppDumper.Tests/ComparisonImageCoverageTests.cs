using System.Text.Json;
using Il2CppDumper.Diff;
using Xunit;

namespace Il2CppDumper.Tests;

public sealed class ComparisonImageCoverageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_FullVersusScoped_ReturnsInputErrorAndPreservesReport(bool drift)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var left = Directory.CreateDirectory(Path.Combine(root, "left")).FullName;
            var right = Directory.CreateDirectory(Path.Combine(root, "right")).FullName;
            var image = new SelectedImage(3, "Game.dll", 100, 1);
            var type = TypeIn(image, "Game.Player");
            var leftPath = Path.Combine(left, "dump.json");
            var rightPath = Path.Combine(right, "dump.json");
            File.WriteAllText(leftPath, JsonSerializer.Serialize(new JsonDumpModel { types = { type } }));
            File.WriteAllText(rightPath, JsonSerializer.Serialize(Scoped(4, new[] { image }, type)));
            File.WriteAllText(Path.Combine(left, "script.json"), "unread optional input");
            var report = Path.Combine(root, "report.json");
            File.WriteAllText(report, "previous report");
            using var log = new StringWriter();

            var code = drift ? FieldOffsetDiff.Execute(left, rightPath, report, log)
                : DumpDiffer.Execute(left, rightPath, report, log);

            Assert.Equal(2, code);
            Assert.Contains("only the right dump is scoped", log.ToString());
            Assert.Equal("previous report", File.ReadAllText(report));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Compare_DifferentRecordedImageNames_RejectsEvenWhenIdsMatch()
    {
        var game = new SelectedImage(1, "Game.dll", 100, 1);
        var other = game with { ImageName = "Other.dll" };
        var left = Scoped(4, new[] { game }, TypeIn(game, "Game.Player"));
        var right = Scoped(4, new[] { other }, TypeIn(other, "Game.Player"));

        AssertIncompatible(left, right, "Left-only images: [Game.dll]. Right-only images: [Other.dll].");
    }

    [Fact]
    public void Compare_SameImagesWithDifferentIdsRangesAndOrder_MatchesDeclarations()
    {
        var oldGame = new SelectedImage(1, "Game.dll", 100, 1);
        var newGame = new SelectedImage(6, "Game.dll", 400, 1);
        var oldEmpty = new SelectedImage(3, "Empty.dll", -1, 0);
        var newEmpty = new SelectedImage(2, "Empty.dll", 300, 0);
        var left = Scoped(4, new[] { oldGame, oldEmpty }, TypeIn(oldGame, "Game.Player"));
        var right = Scoped(9, new[] { newEmpty, newGame }, TypeIn(newGame, "Game.Player"));

        Assert.False(DumpDiffer.Compare(left, right).HasDifferences);
        Assert.False(FieldOffsetDiff.Compare(left, right).HasDrift);
    }

    [Fact]
    public void Compare_EquivalentScopes_ReportRealRemovedNestedTypeAndItsField()
    {
        var oldGame = new SelectedImage(1, "Game.dll", 100, 2);
        var newGame = new SelectedImage(6, "Game.dll", 400, 1);
        var parent = TypeIn(oldGame, "Game.Player");
        var child = TypeIn(oldGame, "Game.Player.State", relativeIndex: 1);
        child.fields.Add(new JsonDumpField { name = "Health", type = "int", offset = "0x10" });
        parent.nested.Add(child);
        var left = Scoped(4, new[] { oldGame }, parent);
        var right = Scoped(9, new[] { newGame }, TypeIn(newGame, "Game.Player"));

        Assert.Equal("Game.dll!Game.Player.State", Assert.Single(DumpDiffer.Compare(left, right).types.removed));
        var drift = Assert.Single(FieldOffsetDiff.Compare(left, right).structs);
        Assert.Equal("Game.dll!Game.Player.State", drift.@struct);
        Assert.Equal(1, drift.verdict.removed);
    }

    [Fact]
    public void Compare_EquivalentScopes_RejectsMissingTypeRows()
    {
        var game = new SelectedImage(1, "Game.dll", 100, 2);
        var parent = TypeIn(game, "Game.Player");
        var left = Scoped(4, new[] { game }, parent, TypeIn(game, "Game.Inventory", relativeIndex: 1));
        var right = Scoped(4, new[] { game }, parent);

        AssertIncompatible(left, right, "Incomplete right image coverage for 'Game.dll': expected 2 type declarations, found 1");
    }

    [Theory]
    [InlineData("wrong image", 2, 101)]
    [InlineData("out of range", 1, 102)]
    [InlineData("duplicate index", 1, 100)]
    public void Compare_InvalidCoordinates_RejectsDespiteMatchingCounts(string name, int imageId, int index)
    {
        var game = new SelectedImage(1, "Game.dll", 100, 2);
        var parent = TypeIn(game, "Game.Player");
        var left = Scoped(4, new[] { game }, parent, TypeIn(game, "Game.Inventory", relativeIndex: 1));
        var invalid = TypeIn(game, name);
        invalid.imageId = imageId;
        invalid.typeDefIndex = index;
        var right = Scoped(4, new[] { game }, parent, invalid);

        AssertIncompatible(left, right, "Invalid right image coverage");
    }

    [Fact]
    public void Compare_DuplicateResolvedNames_AreAmbiguousAcrossBuilds()
    {
        var first = new SelectedImage(0, "Duplicate.dll", 0, 0);
        var second = first with { ImageId = 1 };
        var model = Scoped(2, new[] { first, second });

        AssertIncompatible(model, model, "duplicate image name 'Duplicate.dll'");
    }

    private static void AssertIncompatible(JsonDumpModel left, JsonDumpModel right, string message)
    {
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => DumpDiffer.Compare(left, right)).Message);
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => FieldOffsetDiff.Compare(left, right)).Message);
    }

    private static JsonDumpModel Scoped(int sourceCount, SelectedImage[] images, params JsonDumpType[] types) => new()
    {
        ImageSelection = new ImageSelection(images.Select(image => $"id:{image.ImageId}").ToArray(), sourceCount, images),
        types = types.ToList()
    };

    private static JsonDumpType TypeIn(SelectedImage image, string fullName, int relativeIndex = 0) => new()
    {
        image = image.ImageName,
        imageId = image.ImageId,
        typeDefIndex = image.TypeStart + relativeIndex,
        fullName = fullName
    };
}
