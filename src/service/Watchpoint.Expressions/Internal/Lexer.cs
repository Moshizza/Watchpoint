using System.Globalization;
using System.Text;
using Watchpoint.Expressions.Contracts;

namespace Watchpoint.Expressions.Internal;

internal sealed class Lexer
{
    private readonly string _source;
    private readonly List<Diagnostic> _diagnostics = [];
    private int _pos;

    public Lexer(string source)
    {
        _source = source ?? string.Empty;
    }

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();

        if (_source.Length > 2000)
        {
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                ExpressionErrors.TooLong,
                new Dictionary<string, object?> { ["max"] = 2000, ["length"] = _source.Length },
                0,
                _source.Length));
            tokens.Add(new Token(TokenKind.Eof, string.Empty, _source.Length, 0));
            return tokens;
        }

        while (_pos < _source.Length)
        {
            var ch = _source[_pos];

            if (char.IsWhiteSpace(ch))
            {
                _pos++;
                continue;
            }

            var start = _pos;

            switch (ch)
            {
                case '?':
                    _pos++;
                    tokens.Add(new Token(TokenKind.Question, "?", start, 1));
                    break;
                case ':':
                    _pos++;
                    tokens.Add(new Token(TokenKind.Colon, ":", start, 1));
                    break;
                case '|':
                    if (Peek(1) == '|')
                    {
                        _pos += 2;
                        tokens.Add(new Token(TokenKind.Or, "||", start, 2));
                    }
                    else
                    {
                        _pos++;
                        ReportUnexpectedToken("|", start, 1, "||");
                        tokens.Add(new Token(TokenKind.BadToken, "|", start, 1));
                    }
                    break;
                case '&':
                    if (Peek(1) == '&')
                    {
                        _pos += 2;
                        tokens.Add(new Token(TokenKind.And, "&&", start, 2));
                    }
                    else
                    {
                        _pos++;
                        ReportUnexpectedToken("&", start, 1, "&&");
                        tokens.Add(new Token(TokenKind.BadToken, "&", start, 1));
                    }
                    break;
                case '=':
                    if (Peek(1) == '=')
                    {
                        _pos += 2;
                        tokens.Add(new Token(TokenKind.Equal, "==", start, 2));
                    }
                    else
                    {
                        _pos++;
                        ReportUnexpectedToken("=", start, 1, "==");
                        tokens.Add(new Token(TokenKind.BadToken, "=", start, 1));
                    }
                    break;
                case '!':
                    if (Peek(1) == '=')
                    {
                        _pos += 2;
                        tokens.Add(new Token(TokenKind.NotEqual, "!=", start, 2));
                    }
                    else
                    {
                        _pos++;
                        tokens.Add(new Token(TokenKind.Exclamation, "!", start, 1));
                    }
                    break;
                case '<':
                    if (Peek(1) == '=')
                    {
                        _pos += 2;
                        tokens.Add(new Token(TokenKind.LessOrEqual, "<=", start, 2));
                    }
                    else
                    {
                        _pos++;
                        tokens.Add(new Token(TokenKind.Less, "<", start, 1));
                    }
                    break;
                case '>':
                    if (Peek(1) == '=')
                    {
                        _pos += 2;
                        tokens.Add(new Token(TokenKind.GreaterOrEqual, ">=", start, 2));
                    }
                    else
                    {
                        _pos++;
                        tokens.Add(new Token(TokenKind.Greater, ">", start, 1));
                    }
                    break;
                case '+':
                    _pos++;
                    tokens.Add(new Token(TokenKind.Plus, "+", start, 1));
                    break;
                case '-':
                    _pos++;
                    tokens.Add(new Token(TokenKind.Minus, "-", start, 1));
                    break;
                case '*':
                    _pos++;
                    tokens.Add(new Token(TokenKind.Star, "*", start, 1));
                    break;
                case '/':
                    _pos++;
                    tokens.Add(new Token(TokenKind.Slash, "/", start, 1));
                    break;
                case '%':
                    _pos++;
                    tokens.Add(new Token(TokenKind.Percent, "%", start, 1));
                    break;
                case '.':
                    _pos++;
                    tokens.Add(new Token(TokenKind.Dot, ".", start, 1));
                    break;
                case '(':
                    _pos++;
                    tokens.Add(new Token(TokenKind.OpenParen, "(", start, 1));
                    break;
                case ')':
                    _pos++;
                    tokens.Add(new Token(TokenKind.CloseParen, ")", start, 1));
                    break;
                case '[':
                    _pos++;
                    tokens.Add(new Token(TokenKind.OpenBracket, "[", start, 1));
                    break;
                case ']':
                    _pos++;
                    tokens.Add(new Token(TokenKind.CloseBracket, "]", start, 1));
                    break;
                case ',':
                    _pos++;
                    tokens.Add(new Token(TokenKind.Comma, ",", start, 1));
                    break;
                case '"':
                case '\'':
                    tokens.Add(ScanString(ch, start));
                    break;
                default:
                    if (char.IsAsciiDigit(ch))
                    {
                        tokens.Add(ScanNumber(start));
                    }
                    else if (IsIdentStart(ch))
                    {
                        tokens.Add(ScanIdentifierOrKeyword(start));
                    }
                    else
                    {
                        _pos++;
                        ReportUnexpectedToken(ch.ToString(), start, 1, "valid token");
                        tokens.Add(new Token(TokenKind.BadToken, ch.ToString(), start, 1));
                    }
                    break;
            }
        }

        tokens.Add(new Token(TokenKind.Eof, string.Empty, _source.Length, 0));
        return tokens;
    }

    private char Peek(int offset)
    {
        var target = _pos + offset;
        return target < _source.Length ? _source[target] : '\0';
    }

    private static bool IsIdentStart(char ch) =>
        (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || ch == '_';

    private static bool IsIdentPart(char ch) =>
        (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_';

    private Token ScanIdentifierOrKeyword(int start)
    {
        while (_pos < _source.Length && IsIdentPart(_source[_pos]))
        {
            _pos++;
        }

        var text = _source[start.._pos];
        return text switch
        {
            "true" => new Token(TokenKind.True, text, start, text.Length, true),
            "false" => new Token(TokenKind.False, text, start, text.Length, false),
            "null" => new Token(TokenKind.Null, text, start, text.Length, null),
            "in" => new Token(TokenKind.In, text, start, text.Length),
            _ => new Token(TokenKind.Identifier, text, start, text.Length),
        };
    }

    private Token ScanNumber(int start)
    {
        var isDecimal = false;

        while (_pos < _source.Length && char.IsAsciiDigit(_source[_pos]))
        {
            _pos++;
        }

        if (_pos < _source.Length && _source[_pos] == '.' && char.IsAsciiDigit(Peek(1)))
        {
            isDecimal = true;
            _pos++; // skip '.'
            while (_pos < _source.Length && char.IsAsciiDigit(_source[_pos]))
            {
                _pos++;
            }
        }

        var text = _source[start.._pos];

        if (isDecimal)
        {
            if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var decVal))
            {
                return new Token(TokenKind.DecimalLiteral, text, start, text.Length, decVal);
            }

            ReportUnexpectedToken(text, start, text.Length, "decimal literal");
            return new Token(TokenKind.BadToken, text, start, text.Length);
        }

        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var intVal))
        {
            return new Token(TokenKind.IntegerLiteral, text, start, text.Length, intVal);
        }

        // Out of 64-bit int range, fall back to decimal
        if (decimal.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var decFallback))
        {
            return new Token(TokenKind.DecimalLiteral, text, start, text.Length, decFallback);
        }

        ReportUnexpectedToken(text, start, text.Length, "integer literal");
        return new Token(TokenKind.BadToken, text, start, text.Length);
    }

    private Token ScanString(char quote, int start)
    {
        _pos++; // skip opening quote
        var sb = new StringBuilder();
        var isClosed = false;

        while (_pos < _source.Length)
        {
            var ch = _source[_pos];
            if (ch == quote)
            {
                _pos++;
                isClosed = true;
                break;
            }

            if (ch == '\\')
            {
                _pos++;
                if (_pos >= _source.Length)
                {
                    break;
                }

                var esc = _source[_pos];
                _pos++;

                switch (esc)
                {
                    case '"':
                        sb.Append('"');
                        break;
                    case '\'':
                        sb.Append('\'');
                        break;
                    case '\\':
                        sb.Append('\\');
                        break;
                    case 'n':
                        sb.Append('\n');
                        break;
                    case 't':
                        sb.Append('\t');
                        break;
                    case 'r':
                        sb.Append('\r');
                        break;
                    case 'u':
                        if (_pos + 4 <= _source.Length)
                        {
                            var hex = _source.Substring(_pos, 4);
                            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint))
                            {
                                sb.Append((char)codePoint);
                                _pos += 4;
                            }
                            else
                            {
                                sb.Append("\\u").Append(hex);
                                _pos += 4;
                            }
                        }
                        else
                        {
                            sb.Append("\\u");
                        }
                        break;
                    default:
                        sb.Append(esc);
                        break;
                }
            }
            else
            {
                sb.Append(ch);
                _pos++;
            }
        }

        var length = _pos - start;
        if (!isClosed)
        {
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                ExpressionErrors.UnterminatedString,
                new Dictionary<string, object?>(),
                start,
                length));
            return new Token(TokenKind.BadToken, _source[start.._pos], start, length);
        }

        return new Token(TokenKind.StringLiteral, _source[start.._pos], start, length, sb.ToString());
    }

    private void ReportUnexpectedToken(string found, int start, int length, string expected)
    {
        _diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error,
            ExpressionErrors.UnexpectedToken,
            new Dictionary<string, object?> { ["found"] = found, ["expected"] = expected },
            start,
            length));
    }
}
