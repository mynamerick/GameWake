using System.Globalization;
using System.Text;

namespace GameWake.Providers.Steam;

public sealed class VdfParseException(string message) : FormatException(message);

/// <summary>A node in Valve's KeyValues text format: either a string value or a list of children.</summary>
public sealed class VdfNode
{
    private readonly List<VdfNode> _children = [];

    public VdfNode(string key, string? value = null)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }

    public string? Value { get; }

    public bool IsObject => Value is null;

    public IReadOnlyList<VdfNode> Children => _children;

    public VdfNode? this[string key] => _children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    public string? GetString(string key) => this[key]?.Value;

    public long? GetInt64(string key) =>
        long.TryParse(GetString(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    internal void Add(VdfNode child) => _children.Add(child);
}

/// <summary>
/// Parser for the text KeyValues format used by <c>libraryfolders.vdf</c> and <c>appmanifest_*.acf</c>.
/// It is deliberately strict about structure (unbalanced braces or unterminated strings throw
/// <see cref="VdfParseException"/>) and bounded in depth and size, so corrupted files fail cleanly.
/// </summary>
public static class VdfParser
{
    public const int MaxDepth = 32;
    public const int MaxLength = 16 * 1024 * 1024;

    /// <summary>Parses the document into a synthetic root whose children are the top-level keys.</summary>
    public static VdfNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxLength)
        {
            throw new VdfParseException("Document is too large");
        }

        var reader = new Reader(text);
        var root = new VdfNode(string.Empty);
        ParseChildren(ref reader, root, depth: 0, expectClosingBrace: false);
        return root;
    }

    public static bool TryParse(string text, out VdfNode? root, out string? error)
    {
        try
        {
            root = Parse(text);
            error = null;
            return true;
        }
        catch (VdfParseException ex)
        {
            root = null;
            error = ex.Message;
            return false;
        }
    }

    private static void ParseChildren(ref Reader reader, VdfNode parent, int depth, bool expectClosingBrace)
    {
        if (depth > MaxDepth)
        {
            throw new VdfParseException("Nesting is too deep");
        }

        while (true)
        {
            var token = reader.Next();
            switch (token.Kind)
            {
                case TokenKind.End:
                    if (expectClosingBrace)
                    {
                        throw new VdfParseException($"Unexpected end of file inside \"{parent.Key}\"");
                    }

                    return;
                case TokenKind.CloseBrace:
                    if (!expectClosingBrace)
                    {
                        throw new VdfParseException($"Unexpected '}}' at offset {token.Offset}");
                    }

                    return;
                case TokenKind.OpenBrace:
                    throw new VdfParseException($"Unexpected '{{' at offset {token.Offset}");
                case TokenKind.Condition:
                    continue;
            }

            var key = token.Text!;
            var next = reader.Next();
            while (next.Kind == TokenKind.Condition)
            {
                next = reader.Next();
            }

            switch (next.Kind)
            {
                case TokenKind.OpenBrace:
                    var child = new VdfNode(key);
                    ParseChildren(ref reader, child, depth + 1, expectClosingBrace: true);
                    parent.Add(child);
                    break;
                case TokenKind.String:
                    parent.Add(new VdfNode(key, next.Text));
                    break;
                default:
                    throw new VdfParseException($"Key \"{key}\" has no value");
            }
        }
    }

    private enum TokenKind
    {
        End,
        String,
        OpenBrace,
        CloseBrace,
        Condition,
    }

    private readonly record struct Token(TokenKind Kind, string? Text, int Offset);

    private ref struct Reader(string text)
    {
        private readonly string _text = text;
        private int _position;

        public Token Next()
        {
            SkipWhitespaceAndComments();
            if (_position >= _text.Length)
            {
                return new Token(TokenKind.End, null, _position);
            }

            var start = _position;
            var c = _text[_position];
            switch (c)
            {
                case '{':
                    _position++;
                    return new Token(TokenKind.OpenBrace, null, start);
                case '}':
                    _position++;
                    return new Token(TokenKind.CloseBrace, null, start);
                case '"':
                    return new Token(TokenKind.String, ReadQuoted(), start);
                case '[':
                    var close = _text.IndexOf(']', _position);
                    if (close < 0)
                    {
                        throw new VdfParseException($"Unterminated condition at offset {start}");
                    }

                    _position = close + 1;
                    return new Token(TokenKind.Condition, null, start);
                default:
                    return new Token(TokenKind.String, ReadUnquoted(), start);
            }
        }

        private void SkipWhitespaceAndComments()
        {
            while (_position < _text.Length)
            {
                var c = _text[_position];
                if (char.IsWhiteSpace(c) || c == '\uFEFF' || c == '\0')
                {
                    _position++;
                    continue;
                }

                if (c == '/' && _position + 1 < _text.Length && _text[_position + 1] == '/')
                {
                    var newline = _text.IndexOf('\n', _position);
                    _position = newline < 0 ? _text.Length : newline + 1;
                    continue;
                }

                break;
            }
        }

        private string ReadQuoted()
        {
            var start = _position;
            _position++;
            var builder = new StringBuilder();
            while (_position < _text.Length)
            {
                var c = _text[_position++];
                if (c == '"')
                {
                    return builder.ToString();
                }

                if (c == '\\' && _position < _text.Length)
                {
                    var escaped = _text[_position++];
                    builder.Append(escaped switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        _ => escaped,
                    });
                    continue;
                }

                builder.Append(c);
            }

            throw new VdfParseException($"Unterminated string starting at offset {start}");
        }

        private string ReadUnquoted()
        {
            var start = _position;
            while (_position < _text.Length)
            {
                var c = _text[_position];
                if (char.IsWhiteSpace(c) || c is '{' or '}' or '"')
                {
                    break;
                }

                _position++;
            }

            return _text[start.._position];
        }
    }
}
