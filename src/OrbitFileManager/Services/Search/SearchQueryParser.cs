using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace OrbitFileManager.Services.Search;

public sealed record SearchCandidate(
    string Name,
    string Extension,
    string FullPath,
    bool IsDirectory,
    bool IsHidden,
    long SizeBytes,
    DateTime CreatedUtc,
    DateTime ModifiedUtc);

public abstract class SearchNode
{
    public abstract bool Matches(in SearchCandidate item, bool caseSensitive, DateTime utcNow);
}

internal sealed class TrueNode : SearchNode
{
    public override bool Matches(in SearchCandidate item, bool caseSensitive, DateTime utcNow) => true;
}

internal sealed class AndNode(SearchNode left, SearchNode right) : SearchNode
{
    public override bool Matches(in SearchCandidate item, bool caseSensitive, DateTime utcNow) =>
        left.Matches(in item, caseSensitive, utcNow) && right.Matches(in item, caseSensitive, utcNow);
}

internal sealed class OrNode(SearchNode left, SearchNode right) : SearchNode
{
    public override bool Matches(in SearchCandidate item, bool caseSensitive, DateTime utcNow) =>
        left.Matches(in item, caseSensitive, utcNow) || right.Matches(in item, caseSensitive, utcNow);
}

internal sealed class NotNode(SearchNode inner) : SearchNode
{
    public override bool Matches(in SearchCandidate item, bool caseSensitive, DateTime utcNow) =>
        !inner.Matches(in item, caseSensitive, utcNow);
}

internal sealed class NamePatternNode : SearchNode
{
    private readonly string _rawPattern;
    private readonly Regex? _wildcardRegexCaseInsensitive;
    private readonly Regex? _wildcardRegexCaseSensitive;

    public NamePatternNode(string pattern, TimeSpan timeout)
    {
        _rawPattern = pattern;
        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            string rxBody = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            _wildcardRegexCaseInsensitive = new Regex(rxBody, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, timeout);
            _wildcardRegexCaseSensitive = new Regex(rxBody, RegexOptions.CultureInvariant, timeout);
        }
    }

    public override bool Matches(in SearchCandidate item, bool caseSensitive, DateTime utcNow)
    {
        if (_wildcardRegexCaseInsensitive is not null && _wildcardRegexCaseSensitive is not null)
        {
            var rx = caseSensitive ? _wildcardRegexCaseSensitive : _wildcardRegexCaseInsensitive;
            return rx.IsMatch(item.Name);
        }

        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return item.Name.Contains(_rawPattern, cmp);
    }
}

internal sealed class ExtensionNode : SearchNode
{
    private readonly string _normalizedExt;

    public ExtensionNode(string ext)
    {
        string trimmed = ext.Trim();
        _normalizedExt = trimmed.StartsWith('.') ? trimmed : "." + trimmed;
    }

    public override bool Matches(in SearchCandidate item, bool caseSensitive, DateTime utcNow)
    {
        if (item.IsDirectory)
            return false;

        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(item.Extension, _normalizedExt, cmp);
    }
}

internal sealed class RegexNameNode : SearchNode
{
    private readonly Regex _rxInsensitive;
    private readonly Regex _rxSensitive;

    public RegexNameNode(string pattern, TimeSpan timeout)
    {
        _rxInsensitive = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, timeout);
        _rxSensitive = new Regex(pattern, RegexOptions.CultureInvariant, timeout);
    }

    public override bool Matches(in SearchCandidate item, bool caseSensitive, DateTime utcNow)
    {
        var rx = caseSensitive ? _rxSensitive : _rxInsensitive;
        return rx.IsMatch(item.Name);
    }
}

internal enum CompareOp { Gt, Gte, Lt, Lte, Eq }

internal sealed class SizeNode(CompareOp op, long targetBytes, long? upperRangeBytes = null) : SearchNode
{
    public override bool Matches(in SearchCandidate item, bool caseSensitive, DateTime utcNow)
    {
        if (item.IsDirectory)
            return false;

        if (upperRangeBytes.HasValue)
            return item.SizeBytes >= targetBytes && item.SizeBytes <= upperRangeBytes.Value;

        return op switch
        {
            CompareOp.Gt => item.SizeBytes > targetBytes,
            CompareOp.Gte => item.SizeBytes >= targetBytes,
            CompareOp.Lt => item.SizeBytes < targetBytes,
            CompareOp.Lte => item.SizeBytes <= targetBytes,
            _ => item.SizeBytes == targetBytes
        };
    }
}

internal sealed class DateFilterNode(bool isModified, CompareOp op, TimeSpan? relativeAge, DateTime? absoluteUtc) : SearchNode
{
    public override bool Matches(in SearchCandidate item, bool caseSensitive, DateTime utcNow)
    {
        DateTime stamp = isModified ? item.ModifiedUtc : item.CreatedUtc;
        if (stamp == DateTime.MinValue)
            return false;

        if (relativeAge.HasValue)
        {
            // Example: modified:<7d means age is less than 7 days (modified within the last 7 days)
            TimeSpan age = utcNow - stamp;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;

            return op switch
            {
                CompareOp.Lt => age < relativeAge.Value,
                CompareOp.Lte => age <= relativeAge.Value,
                CompareOp.Gt => age > relativeAge.Value,
                CompareOp.Gte => age >= relativeAge.Value,
                _ => Math.Abs((age - relativeAge.Value).TotalHours) < 24
            };
        }

        if (absoluteUtc.HasValue)
        {
            return op switch
            {
                CompareOp.Gt => stamp > absoluteUtc.Value,
                CompareOp.Gte => stamp >= absoluteUtc.Value,
                CompareOp.Lt => stamp < absoluteUtc.Value,
                CompareOp.Lte => stamp <= absoluteUtc.Value,
                _ => stamp.Date == absoluteUtc.Value.Date
            };
        }

        return false;
    }
}

public static class SearchQueryParser
{
    public static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromMilliseconds(250);

    public static (SearchNode? Root, string? ErrorMessage) TryParse(string? queryText, TimeSpan? regexTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(queryText))
            return (new TrueNode(), null);

        TimeSpan timeout = regexTimeout ?? DefaultRegexTimeout;

        try
        {
            List<string> tokens = Tokenize(queryText);
            if (tokens.Count == 0)
                return (new TrueNode(), null);

            int index = 0;
            SearchNode root = ParseOr(tokens, ref index, timeout);
            if (index < tokens.Count)
                return (null, $"Unexpected token '{tokens[index]}' at position {index + 1}.");

            return (root, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static SearchNode ParseOr(List<string> tokens, ref int index, TimeSpan timeout)
    {
        SearchNode left = ParseAnd(tokens, ref index, timeout);
        while (index < tokens.Count && string.Equals(tokens[index], "OR", StringComparison.OrdinalIgnoreCase))
        {
            index++;
            SearchNode right = ParseAnd(tokens, ref index, timeout);
            left = new OrNode(left, right);
        }
        return left;
    }

    private static SearchNode ParseAnd(List<string> tokens, ref int index, TimeSpan timeout)
    {
        SearchNode left = ParseUnary(tokens, ref index, timeout);
        while (index < tokens.Count)
        {
            string peek = tokens[index];
            if (peek == ")" || string.Equals(peek, "OR", StringComparison.OrdinalIgnoreCase))
                break;

            if (string.Equals(peek, "AND", StringComparison.OrdinalIgnoreCase))
                index++;

            SearchNode right = ParseUnary(tokens, ref index, timeout);
            left = new AndNode(left, right);
        }
        return left;
    }

    private static SearchNode ParseUnary(List<string> tokens, ref int index, TimeSpan timeout)
    {
        if (index < tokens.Count && string.Equals(tokens[index], "NOT", StringComparison.OrdinalIgnoreCase))
        {
            index++;
            SearchNode operand = ParseUnary(tokens, ref index, timeout);
            return new NotNode(operand);
        }

        return ParsePrimary(tokens, ref index, timeout);
    }

    private static SearchNode ParsePrimary(List<string> tokens, ref int index, TimeSpan timeout)
    {
        if (index >= tokens.Count)
            throw new FormatException("Unexpected end of search query.");

        string token = tokens[index++];
        if (token == "(")
        {
            SearchNode inner = ParseOr(tokens, ref index, timeout);
            if (index >= tokens.Count || tokens[index] != ")")
                throw new FormatException("Missing closing parenthesis ')'.");
            index++;
            return inner;
        }

        int colonIdx = token.IndexOf(':');
        if (colonIdx > 0 && colonIdx < token.Length - 1)
        {
            string field = token[..colonIdx].ToLowerInvariant();
            string val = StripQuotes(token[(colonIdx + 1)..]);

            return field switch
            {
                "name" => new NamePatternNode(val, timeout),
                "ext" => new ExtensionNode(val),
                "regex" => new RegexNameNode(val, timeout),
                "size" => ParseSizeNode(val),
                "modified" => ParseDateNode(isModified: true, val),
                "created" => ParseDateNode(isModified: false, val),
                _ => new NamePatternNode(StripQuotes(token), timeout)
            };
        }

        return new NamePatternNode(StripQuotes(token), timeout);
    }

    private static SizeNode ParseSizeNode(string expr)
    {
        int rangeIdx = expr.IndexOf("..", StringComparison.Ordinal);
        if (rangeIdx > 0)
        {
            long min = ParseByteQuantity(expr[..rangeIdx]);
            long max = ParseByteQuantity(expr[(rangeIdx + 2)..]);
            return new SizeNode(CompareOp.Eq, min, max);
        }

        var (op, rest) = ExtractOperator(expr);
        long bytes = ParseByteQuantity(rest);
        return new SizeNode(op, bytes);
    }

    private static long ParseByteQuantity(string text)
    {
        string s = text.Trim();
        int numLen = 0;
        while (numLen < s.Length && (char.IsDigit(s[numLen]) || s[numLen] == '.'))
            numLen++;

        if (numLen == 0 || !double.TryParse(s[..numLen], NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            throw new FormatException($"Invalid size expression '{text}'. Expected e.g. size:>100MB.");

        string unit = s[numLen..].Trim().ToUpperInvariant();
        double mult = unit switch
        {
            "" or "B" => 1d,
            "K" or "KB" => 1024d,
            "M" or "MB" => 1024d * 1024d,
            "G" or "GB" => 1024d * 1024d * 1024d,
            "T" or "TB" => 1024d * 1024d * 1024d * 1024d,
            _ => throw new FormatException($"Unknown size unit '{unit}'. Use B, KB, MB, GB, or TB.")
        };

        return (long)(num * mult);
    }

    private static DateFilterNode ParseDateNode(bool isModified, string expr)
    {
        var (op, rest) = ExtractOperator(expr);
        string s = rest.Trim();

        // Check relative age like 7d, 24h, 2w, 6m, 1y
        int digits = 0;
        while (digits < s.Length && char.IsDigit(s[digits]))
            digits++;

        if (digits > 0 && digits < s.Length && int.TryParse(s[..digits], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
        {
            string unit = s[digits..].Trim().ToLowerInvariant();
            TimeSpan? span = unit switch
            {
                "h" => TimeSpan.FromHours(amount),
                "d" => TimeSpan.FromDays(amount),
                "w" => TimeSpan.FromDays(amount * 7),
                "m" => TimeSpan.FromDays(amount * 30),
                "y" => TimeSpan.FromDays(amount * 365),
                _ => null
            };

            if (span.HasValue)
                return new DateFilterNode(isModified, op, span.Value, null);
        }

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime parsed))
            return new DateFilterNode(isModified, op, null, parsed.ToUniversalTime());

        throw new FormatException($"Invalid date expression '{expr}'. Expected e.g. modified:<7d or modified:>2026-01-01.");
    }

    private static (CompareOp Op, string Remainder) ExtractOperator(string input)
    {
        string s = input.Trim();
        if (s.StartsWith(">=", StringComparison.Ordinal)) return (CompareOp.Gte, s[2..]);
        if (s.StartsWith("<=", StringComparison.Ordinal)) return (CompareOp.Lte, s[2..]);
        if (s.StartsWith(">", StringComparison.Ordinal)) return (CompareOp.Gt, s[1..]);
        if (s.StartsWith("<", StringComparison.Ordinal)) return (CompareOp.Lt, s[1..]);
        if (s.StartsWith("=", StringComparison.Ordinal)) return (CompareOp.Eq, s[1..]);
        return (CompareOp.Eq, s);
    }

    private static string StripQuotes(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s[1..^1];
        return s;
    }

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < input.Length)
        {
            while (i < input.Length && char.IsWhiteSpace(input[i]))
                i++;

            if (i >= input.Length)
                break;

            if (input[i] is '(' or ')')
            {
                tokens.Add(input[i].ToString());
                i++;
                continue;
            }

            int start = i;
            bool inQuotes = false;
            bool isRegexField = i + 6 <= input.Length &&
                                input.AsSpan(i, 6).Equals("regex:", StringComparison.OrdinalIgnoreCase);
            while (i < input.Length)
            {
                char c = input[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    i++;
                    continue;
                }
                if (!inQuotes && (char.IsWhiteSpace(c) || (!isRegexField && c is '(' or ')')))
                    break;
                i++;
            }

            tokens.Add(input[start..i]);
        }

        return tokens;
    }
}
