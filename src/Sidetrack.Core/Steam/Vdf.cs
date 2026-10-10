using System.Text;

namespace Sidetrack.Core.Steam;

/// <summary>
/// One node of Valve's KeyValues text format (<c>.vdf</c>, <c>.acf</c>): a
/// quoted key with either a quoted string value or a braced block of children.
/// Keys may repeat; lookups are case-insensitive, as Steam treats them.
/// </summary>
public sealed class VdfNode
{
    private static readonly List<VdfNode> NoChildren = [];

    public VdfNode(string key, string? value, List<VdfNode>? children)
    {
        Key = key;
        Value = value;
        Children = children ?? NoChildren;
    }

    public string Key { get; }
    public string? Value { get; }
    public List<VdfNode> Children { get; }

    public VdfNode? Child(string key) => Children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    public string? this[string key] => Child(key)?.Value;

    /// <summary>The first block named <paramref name="key"/> anywhere below this node, depth-first.</summary>
    public VdfNode? Find(string key)
    {
        foreach (var c in Children)
        {
            if (string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)) return c;
            if (c.Find(key) is { } deeper) return deeper;
        }
        return null;
    }
}

public static class Vdf
{
    /// <summary>Parses a whole file into a root node whose children are the top-level keys.</summary>
    public static VdfNode Parse(string text)
    {
        int i = 0;
        var children = ParseBlock(text, ref i, topLevel: true);
        return new VdfNode("", null, children);
    }

    private static List<VdfNode> ParseBlock(string s, ref int i, bool topLevel)
    {
        var nodes = new List<VdfNode>();
        while (true)
        {
            SkipTrivia(s, ref i);
            if (i >= s.Length)
            {
                if (topLevel) return nodes;
                throw new FormatException("Unterminated block in VDF.");
            }
            if (s[i] == '}')
            {
                if (topLevel) throw new FormatException($"Unexpected '}}' at {i} in VDF.");
                i++;
                return nodes;
            }
            var key = ReadToken(s, ref i);
            SkipTrivia(s, ref i);
            if (i < s.Length && s[i] == '{')
            {
                i++;
                nodes.Add(new VdfNode(key, null, ParseBlock(s, ref i, topLevel: false)));
            }
            else
            {
                nodes.Add(new VdfNode(key, ReadToken(s, ref i), null));
                SkipConditional(s, ref i);
            }
        }
    }

    private static string ReadToken(string s, ref int i)
    {
        if (i >= s.Length) throw new FormatException("Unexpected end of VDF.");
        var sb = new StringBuilder();
        if (s[i] == '"')
        {
            i++;
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] });
                }
                else sb.Append(s[i]);
                i++;
            }
            if (i >= s.Length) throw new FormatException("Unterminated string in VDF.");
            i++;
            return sb.ToString();
        }
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('{' or '}' or '"')) sb.Append(s[i++]);
        if (sb.Length == 0) throw new FormatException($"Unexpected '{s[i]}' at {i} in VDF.");
        return sb.ToString();
    }

    /// <summary>Skips a trailing platform conditional such as <c>[$WIN32]</c>.</summary>
    private static void SkipConditional(string s, ref int i)
    {
        int j = i;
        while (j < s.Length && s[j] is ' ' or '\t') j++;
        if (j < s.Length && s[j] == '[')
        {
            int close = s.IndexOf(']', j);
            if (close > 0) i = close + 1;
        }
    }

    private static void SkipTrivia(string s, ref int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i]) || s[i] == '﻿') i++;
            else if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
            }
            else return;
        }
    }
}
