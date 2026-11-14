using System.Globalization;
using System.Text.Json.Nodes;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Expressions.Internal;

internal abstract record AstNode(int Start, int Length)
{
    public abstract JsonNode ToJson();
}

internal sealed record LiteralNode(WpValueKind Kind, object? Value, int Start, int Length) : AstNode(Start, Length)
{
    public override JsonNode ToJson()
    {
        var obj = new JsonObject
        {
            ["type"] = "Literal",
            ["kind"] = Kind switch
            {
                WpValueKind.Bool => "bool",
                WpValueKind.Int => "int",
                WpValueKind.Decimal => "decimal",
                WpValueKind.String => "string",
                WpValueKind.Null => "null",
                _ => Kind.ToString().ToLowerInvariant(),
            },
        };

        switch (Kind)
        {
            case WpValueKind.Bool:
                obj["value"] = (bool)Value!;
                break;
            case WpValueKind.Int:
                obj["value"] = (long)Value!;
                break;
            case WpValueKind.Decimal:
                obj["value"] = ((decimal)Value!).ToString(CultureInfo.InvariantCulture);
                break;
            case WpValueKind.String:
                obj["value"] = (string)Value!;
                break;
            case WpValueKind.Null:
                obj["value"] = null;
                break;
        }

        return obj;
    }
}

internal sealed record IdentifierNode(string Name, int Start, int Length) : AstNode(Start, Length)
{
    public override JsonNode ToJson() => new JsonObject
    {
        ["type"] = "Identifier",
        ["name"] = Name,
    };
}

internal sealed record MemberAccessNode(AstNode Target, string Member, int Start, int Length) : AstNode(Start, Length)
{
    /// <summary>
    /// Gets the root variable name when the chain is <c>variable.member[.member…]</c>, else null.
    /// Computed once at parse time so evaluation does not allocate (EXP §5).
    /// </summary>
    public string? RootVariable { get; } = Target switch
    {
        IdentifierNode id => id.Name,
        MemberAccessNode { RootVariable: { } root } => root,
        _ => null,
    };

    /// <summary>Gets the dotted member path below <see cref="RootVariable"/> (e.g. <c>doorPosition.position</c>), else null.</summary>
    public string? VariableMemberPath { get; } = Target switch
    {
        IdentifierNode => Member,
        MemberAccessNode { VariableMemberPath: { } inner } => inner + "." + Member,
        _ => null,
    };

    public override JsonNode ToJson() => new JsonObject
    {
        ["type"] = "MemberAccess",
        ["target"] = Target.ToJson(),
        ["member"] = Member,
    };
}

internal sealed record IndexAccessNode(AstNode Target, AstNode Index, int Start, int Length) : AstNode(Start, Length)
{
    public override JsonNode ToJson() => new JsonObject
    {
        ["type"] = "IndexAccess",
        ["target"] = Target.ToJson(),
        ["index"] = Index.ToJson(),
    };
}

internal sealed record CallNode(AstNode Target, string? Method, IReadOnlyList<AstNode> Arguments, int Start, int Length) : AstNode(Start, Length)
{
    public override JsonNode ToJson()
    {
        var args = new JsonArray();
        foreach (var arg in Arguments)
        {
            args.Add(arg.ToJson());
        }

        var obj = new JsonObject
        {
            ["type"] = "Call",
            ["target"] = Target.ToJson(),
            ["args"] = args,
        };
        if (Method is not null)
        {
            obj["method"] = Method;
        }

        return obj;
    }
}

internal sealed record UnaryNode(string Operator, AstNode Operand, int Start, int Length) : AstNode(Start, Length)
{
    public override JsonNode ToJson() => new JsonObject
    {
        ["type"] = "Unary",
        ["op"] = Operator,
        ["operand"] = Operand.ToJson(),
    };
}

internal sealed record BinaryNode(string Operator, AstNode Left, AstNode Right, int Start, int Length) : AstNode(Start, Length)
{
    public override JsonNode ToJson() => new JsonObject
    {
        ["type"] = "Binary",
        ["op"] = Operator,
        ["left"] = Left.ToJson(),
        ["right"] = Right.ToJson(),
    };
}

internal sealed record ConditionalNode(AstNode Condition, AstNode TrueBranch, AstNode FalseBranch, int Start, int Length) : AstNode(Start, Length)
{
    public override JsonNode ToJson() => new JsonObject
    {
        ["type"] = "Conditional",
        ["condition"] = Condition.ToJson(),
        ["trueBranch"] = TrueBranch.ToJson(),
        ["falseBranch"] = FalseBranch.ToJson(),
    };
}

internal sealed record ListNode(IReadOnlyList<AstNode> Items, int Start, int Length) : AstNode(Start, Length)
{
    public override JsonNode ToJson()
    {
        var arr = new JsonArray();
        foreach (var item in Items)
        {
            arr.Add(item.ToJson());
        }

        return new JsonObject
        {
            ["type"] = "List",
            ["items"] = arr,
        };
    }
}
