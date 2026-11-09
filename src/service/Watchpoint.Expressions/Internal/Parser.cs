using Watchpoint.Expressions.Contracts;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Expressions.Internal;

internal sealed class Parser
{
    private const int MaxNodes = 500;
    private const int MaxDepth = 32;
    private const int MaxListItems = 1000;

    private readonly IReadOnlyList<Token> _tokens;
    private readonly List<Diagnostic> _diagnostics = [];
    private int _pos;
    private int _nodeCount;
    private int _currentDepth;

    public Parser(IReadOnlyList<Token> tokens)
    {
        _tokens = tokens ?? [];
    }

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    private Token Current => _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];

    private Token Peek(int offset)
    {
        var idx = _pos + offset;
        return idx < _tokens.Count ? _tokens[idx] : _tokens[^1];
    }

    private Token Advance()
    {
        var cur = Current;
        if (_pos < _tokens.Count - 1)
        {
            _pos++;
        }

        return cur;
    }

    private bool Match(TokenKind kind)
    {
        if (Current.Kind == kind)
        {
            Advance();
            return true;
        }

        return false;
    }

    private bool Check(TokenKind kind) => Current.Kind == kind;

    public AstNode? Parse()
    {
        if (Current.Kind == TokenKind.Eof)
        {
            return null;
        }

        var expr = ParseExpression();

        if (Current.Kind != TokenKind.Eof && _diagnostics.All(d => d.Severity != DiagnosticSeverity.Error))
        {
            ReportError(ExpressionErrors.UnexpectedToken, Current.Start, Current.Length,
                new Dictionary<string, object?> { ["found"] = Current.Text, ["expected"] = "end of expression" });
        }

        return expr;
    }

    private AstNode? ParseExpression()
    {
        _currentDepth++;
        if (_currentDepth > MaxDepth && !_diagnostics.Any(d => d.Code == ExpressionErrors.TooDeep))
        {
            ReportError(ExpressionErrors.TooDeep, Current.Start, Current.Length,
                new Dictionary<string, object?> { ["max"] = MaxDepth });
        }

        try
        {
            return ParseConditional();
        }
        finally
        {
            _currentDepth--;
        }
    }

    private AstNode? ParseConditional()
    {
        var condition = ParseOr();
        if (condition is null)
        {
            return null;
        }

        if (Match(TokenKind.Question))
        {
            var trueBranch = ParseExpression();
            if (trueBranch is null)
            {
                ReportError(ExpressionErrors.UnexpectedToken, Current.Start, Current.Length,
                    new Dictionary<string, object?> { ["found"] = Current.Text, ["expected"] = "expression" });
                return null;
            }

            if (!Match(TokenKind.Colon))
            {
                ReportError(ExpressionErrors.UnexpectedToken, Current.Start, Current.Length,
                    new Dictionary<string, object?> { ["found"] = Current.Text, ["expected"] = ":" });
                return null;
            }

            var falseBranch = ParseExpression();
            if (falseBranch is null)
            {
                ReportError(ExpressionErrors.UnexpectedToken, Current.Start, Current.Length,
                    new Dictionary<string, object?> { ["found"] = Current.Text, ["expected"] = "expression" });
                return null;
            }

            var start = condition.Start;
            var length = falseBranch.Start + falseBranch.Length - start;
            return RecordNode(new ConditionalNode(condition, trueBranch, falseBranch, start, length));
        }

        return condition;
    }

    private AstNode? ParseOr()
    {
        var left = ParseAnd();
        if (left is null)
        {
            return null;
        }

        while (Match(TokenKind.Or))
        {
            var right = ParseAnd();
            if (right is null)
            {
                ReportError(ExpressionErrors.UnexpectedToken, Current.Start, Current.Length,
                    new Dictionary<string, object?> { ["found"] = Current.Text, ["expected"] = "expression" });
                return null;
            }

            var start = left.Start;
            var length = right.Start + right.Length - start;
            left = RecordNode(new BinaryNode("||", left, right, start, length));
        }

        return left;
    }

    private AstNode? ParseAnd()
    {
        var left = ParseRelation();
        if (left is null)
        {
            return null;
        }

        while (Match(TokenKind.And))
        {
            var right = ParseRelation();
            if (right is null)
            {
                ReportError(ExpressionErrors.UnexpectedToken, Current.Start, Current.Length,
                    new Dictionary<string, object?> { ["found"] = Current.Text, ["expected"] = "expression" });
                return null;
            }

            var start = left.Start;
            var length = right.Start + right.Length - start;
            left = RecordNode(new BinaryNode("&&", left, right, start, length));
        }

        return left;
    }

    private AstNode? ParseRelation()
    {
        var left = ParseAddition();
        if (left is null)
        {
            return null;
        }

        if (IsRelationOp(Current.Kind))
        {
            var opToken = Advance();
            var op = opToken.Text;

            var right = ParseAddition();
            if (right is null)
            {
                ReportError(ExpressionErrors.UnexpectedToken, Current.Start, Current.Length,
                    new Dictionary<string, object?> { ["found"] = Current.Text, ["expected"] = "expression" });
                return null;
            }

            var start = left.Start;
            var length = right.Start + right.Length - start;
            var rel = RecordNode(new BinaryNode(op, left, right, start, length));

            // CEL relations are non-associative: a < b < c is a syntax error
            if (IsRelationOp(Current.Kind))
            {
                var chained = Current;
                ReportError(ExpressionErrors.ChainedComparison, chained.Start, chained.Length,
                    new Dictionary<string, object?>());
                return null;
            }

            return rel;
        }

        return left;
    }

    private static bool IsRelationOp(TokenKind kind) => kind switch
    {
        TokenKind.Equal or TokenKind.NotEqual or
        TokenKind.Less or TokenKind.LessOrEqual or
        TokenKind.Greater or TokenKind.GreaterOrEqual or
        TokenKind.In => true,
        _ => false,
    };

    private AstNode? ParseAddition()
    {
        var left = ParseMultiplication();
        if (left is null)
        {
            return null;
        }

        while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            var opToken = Advance();
            var right = ParseMultiplication();
            if (right is null)
            {
                ReportError(ExpressionErrors.UnexpectedToken, Current.Start, Current.Length,
                    new Dictionary<string, object?> { ["found"] = Current.Text, ["expected"] = "expression" });
                return null;
            }

            var start = left.Start;
            var length = right.Start + right.Length - start;
            left = RecordNode(new BinaryNode(opToken.Text, left, right, start, length));
        }

        return left;
    }

    private AstNode? ParseMultiplication()
    {
        var left = ParseUnary();
        if (left is null)
        {
            return null;
        }

        while (Current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
        {
            var opToken = Advance();
            var right = ParseUnary();
            if (right is null)
            {
                ReportError(ExpressionErrors.UnexpectedToken, Current.Start, Current.Length,
                    new Dictionary<string, object?> { ["found"] = Current.Text, ["expected"] = "expression" });
                return null;
            }

            var start = left.Start;
            var length = right.Start + right.Length - start;
            left = RecordNode(new BinaryNode(opToken.Text, left, right, start, length));
        }

        return left;
    }

    private AstNode? ParseUnary()
    {
        if (Current.Kind is TokenKind.Exclamation or TokenKind.Minus)
        {
            var opToken = Advance();
            var operand = ParseUnary();
            if (operand is null)
            {
                ReportError(ExpressionErrors.UnexpectedToken, Current.Start, Current.Length,
                    new Dictionary<string, object?> { ["found"] = Current.Text, ["expected"] = "expression" });
                return null;
            }

            var start = opToken.Start;
            var length = operand.Start + operand.Length - start;
            return RecordNode(new UnaryNode(opToken.Text, operand, start, length));
        }

        return ParseMember();
    }

    private AstNode? ParseMember()
    {
        var target = ParsePrimary();
        if (target is null)
        {
            return null;
        }

        while (true)
        {
            if (Match(TokenKind.Dot))
            {
                if (!Check(TokenKind.Identifier))
                {
                    ReportError(ExpressionErrors.UnexpectedToken, Current.Start, Current.Length,
                        new Dictionary<string, object?> { ["found"] = Current.Text, ["expected"] = "identifier" });
                    return null;
                }

                var identToken = Advance();
                var memberName = identToken.Text;

                if (Match(TokenKind.OpenParen))
                {
                    // Member method call: target.ident(args)
                    var args = ParseArguments(TokenKind.CloseParen);
                    if (args is null)
                    {
                        return null;
                    }

                    var end = Current.Start + Current.Length; // After close paren
                    var length = end - target.Start;
                    target = RecordNode(new CallNode(target, memberName, args, target.Start, length));
                }
                else
                {
                    // Member access: target.ident
                    var length = identToken.Start + identToken.Length - target.Start;
                    target = RecordNode(new MemberAccessNode(target, memberName, target.Start, length));
                }
            }
            else if (Match(TokenKind.OpenBracket))
            {
                // Index access: target[expr]
                var indexExpr = ParseExpression();
                if (indexExpr is null)
                {
                    ReportError(ExpressionErrors.UnexpectedToken, Current.Start, Current.Length,
                        new Dictionary<string, object?> { ["found"] = Current.Text, ["expected"] = "expression" });
                    return null;
                }

                if (!Match(TokenKind.CloseBracket))
                {
                    ReportError(ExpressionErrors.UnexpectedToken, Current.Start, Current.Length,
                        new Dictionary<string, object?> { ["found"] = Current.Text, ["expected"] = "]" });
                    return null;
                }

                var end = Current.Start;
                var length = end - target.Start;
                target = RecordNode(new IndexAccessNode(target, indexExpr, target.Start, length));
            }
            else
            {
                break;
            }
        }

        return target;
    }

    private AstNode? ParsePrimary()
    {
        var cur = Current;

        switch (cur.Kind)
        {
            case TokenKind.True:
                Advance();
                return RecordNode(new LiteralNode(WpValueKind.Bool, true, cur.Start, cur.Length));

            case TokenKind.False:
                Advance();
                return RecordNode(new LiteralNode(WpValueKind.Bool, false, cur.Start, cur.Length));

            case TokenKind.Null:
                Advance();
                return RecordNode(new LiteralNode(WpValueKind.Null, null, cur.Start, cur.Length));

            case TokenKind.IntegerLiteral:
                Advance();
                return RecordNode(new LiteralNode(WpValueKind.Int, (long)cur.Value!, cur.Start, cur.Length));

            case TokenKind.DecimalLiteral:
                Advance();
                return RecordNode(new LiteralNode(WpValueKind.Decimal, (decimal)cur.Value!, cur.Start, cur.Length));

            case TokenKind.StringLiteral:
                Advance();
                return RecordNode(new LiteralNode(WpValueKind.String, (string)cur.Value!, cur.Start, cur.Length));

            case TokenKind.Identifier:
                var identToken = Advance();
                if (Match(TokenKind.OpenParen))
                {
                    // Function call: ident(args)
                    var target = RecordNode(new IdentifierNode(identToken.Text, identToken.Start, identToken.Length));
                    var args = ParseArguments(TokenKind.CloseParen);
                    if (args is null)
                    {
                        return null;
                    }

                    var end = Current.Start + Current.Length;
                    var length = end - identToken.Start;
                    return RecordNode(new CallNode(target, null, args, identToken.Start, length));
                }

                return RecordNode(new IdentifierNode(identToken.Text, identToken.Start, identToken.Length));

            case TokenKind.OpenParen:
                Advance();
                var inner = ParseExpression();
                if (inner is null)
                {
                    ReportError(ExpressionErrors.UnexpectedToken, Current.Start, Current.Length,
                        new Dictionary<string, object?> { ["found"] = Current.Text, ["expected"] = "expression" });
                    return null;
                }

                if (!Match(TokenKind.CloseParen))
                {
                    ReportError(ExpressionErrors.UnexpectedToken, Current.Start, Current.Length,
                        new Dictionary<string, object?> { ["found"] = Current.Text, ["expected"] = ")" });
                    return null;
                }

                return inner;

            case TokenKind.OpenBracket:
                var bracketToken = Advance();
                var items = ParseArguments(TokenKind.CloseBracket);
                if (items is null)
                {
                    return null;
                }

                if (items.Count > MaxListItems)
                {
                    ReportError(ExpressionErrors.ListTooLarge, bracketToken.Start, Current.Start - bracketToken.Start,
                        new Dictionary<string, object?> { ["count"] = items.Count, ["max"] = MaxListItems });
                    return null;
                }

                var listLength = Current.Start - bracketToken.Start;
                return RecordNode(new ListNode(items, bracketToken.Start, listLength));

            default:
                ReportError(ExpressionErrors.UnexpectedToken, cur.Start, cur.Length > 0 ? cur.Length : 1,
                    new Dictionary<string, object?> { ["found"] = cur.Text, ["expected"] = "expression" });
                return null;
        }
    }

    private List<AstNode>? ParseArguments(TokenKind closeKind)
    {
        var list = new List<AstNode>();

        if (Check(closeKind))
        {
            Advance();
            return list;
        }

        while (true)
        {
            var arg = ParseExpression();
            if (arg is null)
            {
                ReportError(ExpressionErrors.UnexpectedToken, Current.Start, Current.Length,
                    new Dictionary<string, object?> { ["found"] = Current.Text, ["expected"] = "expression" });
                return null;
            }

            list.Add(arg);

            if (Match(TokenKind.Comma))
            {
                continue;
            }

            if (Match(closeKind))
            {
                break;
            }

            ReportError(ExpressionErrors.UnexpectedToken, Current.Start, Current.Length,
                new Dictionary<string, object?> { ["found"] = Current.Text, ["expected"] = closeKind == TokenKind.CloseParen ? ")" : "]" });
            return null;
        }

        return list;
    }

    private AstNode RecordNode(AstNode node)
    {
        _nodeCount++;
        if (_nodeCount > MaxNodes && !_diagnostics.Any(d => d.Code == ExpressionErrors.TooComplex))
        {
            ReportError(ExpressionErrors.TooComplex, node.Start, node.Length,
                new Dictionary<string, object?> { ["max"] = MaxNodes });
        }

        return node;
    }

    private void ReportError(string code, int start, int length, IReadOnlyDictionary<string, object?> args)
    {
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, code, args, start, length));
    }
}
