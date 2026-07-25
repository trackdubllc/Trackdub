using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

public sealed class FakePhonemizer(string fixedPhonemes = "h@l@U") : IGraphemeToPhoneme
{
    public string Phonemize(string text, string languageCode) => fixedPhonemes;
}
