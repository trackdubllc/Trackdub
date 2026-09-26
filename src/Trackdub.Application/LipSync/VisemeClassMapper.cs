using Trackdub.Contracts.Pipeline;

namespace Trackdub.Application.LipSync;

/// <summary>Shared mouth-shape classes for IPA, eSpeak IPA, and ARPAbet timings.</summary>
internal static class VisemeClassMapper
{
    public static string? Map(PhonemeTiming phoneme)
    {
        string symbol = phoneme.Symbol.Trim().ToLowerInvariant();
        string inventory = phoneme.Inventory.Trim().ToLowerInvariant();
        if (inventory is "arpabet" or "cmudict")
        {
            symbol = symbol.TrimEnd('0', '1', '2') switch
            {
                "aa" or "ah" or "ao" => "ɑ", "ae" => "æ", "eh" => "ɛ",
                "ih" or "iy" => "i", "uh" or "uw" => "u", "er" => "ɚ",
                "b" => "b", "p" => "p", "m" => "m", "f" => "f", "v" => "v",
                "th" => "θ", "dh" => "ð", "sh" => "ʃ", "ch" => "tʃ",
                "jh" => "dʒ", "ng" => "ŋ", "y" => "j", "w" => "w",
                _ => symbol,
            };
        }
        else if (inventory is not ("ipa" or "espeak-ipa"))
        {
            return null;
        }

        symbol = symbol.Trim('ˈ', 'ˌ', 'ː', 'ˑ');
        if (symbol.Length == 0) return null;
        if (symbol.StartsWith("tʃ", StringComparison.Ordinal) ||
            symbol.StartsWith("dʒ", StringComparison.Ordinal)) return "postalveolar";
        return symbol[0] switch
        {
            'p' or 'b' or 'm' => "bilabial",
            'f' or 'v' or 'ɱ' => "labiodental",
            'θ' or 'ð' => "dental",
            't' or 'd' or 's' or 'z' or 'n' or 'l' => "alveolar",
            'ʃ' or 'ʒ' => "postalveolar",
            'k' or 'g' or 'ŋ' or 'x' => "velar",
            'w' or 'ʍ' => "rounded",
            'i' or 'ɪ' or 'e' or 'ɛ' or 'æ' or 'y' or 'ø' or 'œ' => "vowel-front",
            'a' or 'ɑ' or 'ɐ' or 'ʌ' or 'ə' or 'ɚ' or 'ɜ' => "vowel-open",
            'o' or 'ɔ' or 'u' or 'ʊ' or 'ɒ' or 'ɯ' => "vowel-back",
            'h' or 'ɦ' or 'ʔ' => "glottal",
            'r' or 'ɹ' or 'ɾ' or 'j' => "approximant",
            _ => null,
        };
    }

    public static bool IsVowel(string viseme) => viseme.StartsWith("vowel-", StringComparison.Ordinal);
}
