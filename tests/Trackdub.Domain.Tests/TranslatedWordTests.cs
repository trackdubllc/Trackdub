using Trackdub.Domain.Translation;

namespace Trackdub.Domain.Tests;

public sealed class TranslatedWordTests
{
    [Fact]
    public void Create_ValidWord_PreservesValues()
    {
        TranslatedWord word = TranslatedWord.Create(2, 1.25d, 1.75d, " hola ");

        Assert.Equal(2, word.WordIndex);
        Assert.Equal(1.25d, word.StartSeconds);
        Assert.Equal(1.75d, word.EndSeconds);
        Assert.Equal("hola", word.Text);
    }

    [Fact]
    public void Create_NegativeIndex_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TranslatedWord.Create(-1, 0d, 1d, "hola"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.01d)]
    public void Create_InvalidStart_Throws(double startSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TranslatedWord.Create(0, startSeconds, 1d, "hola"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.49d)]
    public void Create_InvalidEnd_Throws(double endSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TranslatedWord.Create(0, 0.5d, endSeconds, "hola"));
    }

    [Fact]
    public void SegmentCreate_NormalizesAndReindexesWords()
    {
        TranslatedSegment segment = TranslatedSegment.Create(
            Guid.NewGuid(),
            0,
            0d,
            2d,
            "hola mundo",
            words:
            [
                TranslatedWord.Create(5, 1d, 2d, "mundo"),
                TranslatedWord.Create(2, 0d, 1d, "hola")
            ]);

        Assert.Collection(
            segment.Words,
            first =>
            {
                Assert.Equal(0, first.WordIndex);
                Assert.Equal(0d, first.StartSeconds);
                Assert.Equal(1d, first.EndSeconds);
                Assert.Equal("hola", first.Text);
            },
            second =>
            {
                Assert.Equal(1, second.WordIndex);
                Assert.Equal(1d, second.StartSeconds);
                Assert.Equal(2d, second.EndSeconds);
                Assert.Equal("mundo", second.Text);
            });
    }
}
