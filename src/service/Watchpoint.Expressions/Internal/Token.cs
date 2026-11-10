namespace Watchpoint.Expressions.Internal;

internal enum TokenKind
{
    // End of input
    Eof,

    // Literals
    IntegerLiteral,
    DecimalLiteral,
    StringLiteral,
    True,
    False,
    Null,

    // Identifiers & keywords
    Identifier,
    In,

    // Operators & Punctuation
    Question,       // ?
    Colon,          // :
    Or,             // ||
    And,            // &&
    Equal,          // ==
    NotEqual,       // !=
    Less,           // <
    LessOrEqual,    // <=
    Greater,        // >
    GreaterOrEqual, // >=
    Plus,           // +
    Minus,          // -
    Star,           // *
    Slash,          // /
    Percent,        // %
    Exclamation,    // !
    Dot,            // .
    OpenParen,      // (
    CloseParen,     // )
    OpenBracket,    // [
    CloseBracket,   // ]
    Comma,          // ,

    // Errors
    BadToken,
}

internal readonly record struct Token(
    TokenKind Kind,
    string Text,
    int Start,
    int Length,
    object? Value = null);
