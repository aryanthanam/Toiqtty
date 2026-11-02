namespace Toiqtty.Models;

public static class FuzzyMatch
{
    public static int Score(string query, string candidate)
    {
        var trimmed = query.AsSpan().Trim();
        if (trimmed.IsEmpty) return 0;
        var text = candidate.AsSpan();
        int position = 0, score = 0, previous = -2;
        for (int index = 0; index < trimmed.Length; index++)
        {
            int found = text[position..].IndexOf(trimmed.Slice(index, 1), StringComparison.OrdinalIgnoreCase);
            if (found < 0) return -1;
            found += position;
            score += 10 + (found == previous + 1 ? 12 : 0) + (found == 0 || !char.IsLetterOrDigit(candidate[found - 1]) ? 16 : 0);
            score -= Math.Min(8, found - position); previous = found; position = found + 1;
        }
        if (text.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) score += 1000;
        return score;
    }
}
