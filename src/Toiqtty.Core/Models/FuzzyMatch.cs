namespace Toiqtty.Models;

public static class FuzzyMatch
{
    public static int Score(string query, string candidate)
    {
        if (query.Length == 0) return 0;
        int position = 0, score = 0, previous = -2;
        foreach (char character in query.Trim())
        {
            int found = candidate.IndexOf(character.ToString(), position, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return -1;
            score += 10 + (found == previous + 1 ? 12 : 0) + (found == 0 || !char.IsLetterOrDigit(candidate[found - 1]) ? 16 : 0);
            score -= Math.Min(8, found - position); previous = found; position = found + 1;
        }
        if (candidate.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase)) score += 1000;
        return score;
    }
}
