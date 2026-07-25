namespace Trackdub.Contracts.Pipeline;

public interface IGraphemeToPhoneme
{
    string Phonemize(string text, string languageCode);
}
