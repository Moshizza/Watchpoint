using System.Globalization;
using System.Text.RegularExpressions;
using Watchpoint.Expressions.Contracts;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Expressions.Internal;

/// <summary>Evaluates an AST. A struct so that an evaluation allocates nothing (EXP §5).</summary>
internal struct Evaluator
{
    private const int MaxSteps = 10000;
    private readonly IEvaluationContext _context;
    private int _stepCount;

    public Evaluator(IEvaluationContext context)
    {
        _context = context;
    }

    public EvalResult Evaluate(AstNode node)
    {
        _stepCount = 0;
        try
        {
            return Eval(node);
        }
        catch (LimitExceededException)
        {
            return new EvalResult(WpValue.Null, ExpressionErrors.EvalLimitExceeded);
        }
        catch
        {
            return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
        }
    }

    private void Step()
    {
        _stepCount++;
        if (_stepCount > MaxSteps)
        {
            throw new LimitExceededException();
        }
    }

    private EvalResult Eval(AstNode node)
    {
        Step();

        return node switch
        {
            LiteralNode lit => EvalLiteral(lit),
            IdentifierNode id => EvalIdentifier(id),
            MemberAccessNode member => EvalMemberAccess(member),
            IndexAccessNode idx => EvalIndexAccess(idx),
            CallNode call => EvalCall(call),
            UnaryNode unary => EvalUnary(unary),
            BinaryNode binary => EvalBinary(binary),
            ConditionalNode cond => EvalConditional(cond),
            ListNode list => EvalList(list),
            _ => new EvalResult(WpValue.Null, ExpressionErrors.EvalError),
        };
    }

    private static EvalResult EvalLiteral(LiteralNode lit)
    {
        return lit.Kind switch
        {
            WpValueKind.Bool => new EvalResult(WpValue.Bool((bool)lit.Value!), null),
            WpValueKind.Int => new EvalResult(WpValue.Int((long)lit.Value!), null),
            WpValueKind.Decimal => new EvalResult(WpValue.Decimal((decimal)lit.Value!), null),
            WpValueKind.String => new EvalResult(WpValue.String((string)lit.Value!), null),
            WpValueKind.Null => new EvalResult(WpValue.Null, null),
            _ => new EvalResult(WpValue.Null, null),
        };
    }

    private EvalResult EvalIdentifier(IdentifierNode id)
    {
        if (id.Name == "now")
        {
            return new EvalResult(WpValue.Timestamp(_context.Now), null);
        }

        if (_context.TryGetVariable(id.Name, out var exprObj))
        {
            return new EvalResult(WpValue.Null, null);
        }

        return new EvalResult(WpValue.Null, null);
    }

    private EvalResult EvalMemberAccess(MemberAccessNode member)
    {
        if (member is { RootVariable: { } root, VariableMemberPath: { } remaining })
        {
            if (_context.TryGetVariable(root, out var obj))
            {
                if (obj.TryGetMember(remaining, out var val))
                {
                    return new EvalResult(val, null);
                }

                return new EvalResult(WpValue.Null, null);
            }
        }

        var targetRes = Eval(member.Target);
        if (targetRes.ErrorCode is not null)
        {
            return targetRes;
        }

        if (targetRes.Value.Kind == WpValueKind.Timestamp)
        {
            return EvalTimestampMember(targetRes.Value.AsTimestamp(), member.Member, null);
        }

        return new EvalResult(WpValue.Null, null);
    }

    private static List<string>? BuildMemberPath(AstNode node)
    {
        if (node is IdentifierNode id)
        {
            return [id.Name];
        }

        if (node is MemberAccessNode mem)
        {
            var p = BuildMemberPath(mem.Target);
            if (p is null)
            {
                return null;
            }

            p.Add(mem.Member);
            return p;
        }

        return null;
    }

    private EvalResult EvalIndexAccess(IndexAccessNode idx)
    {
        var targetRes = Eval(idx.Target);
        if (targetRes.ErrorCode is not null)
        {
            return targetRes;
        }

        var indexRes = Eval(idx.Index);
        if (indexRes.ErrorCode is not null)
        {
            return indexRes;
        }

        if (targetRes.Value.Kind == WpValueKind.List && indexRes.Value.Kind == WpValueKind.Int)
        {
            var list = targetRes.Value.AsList();
            var index = (int)indexRes.Value.AsInt();
            if (index < 0 || index >= list.Count)
            {
                return new EvalResult(WpValue.Null, ExpressionErrors.EvalIndexOutOfRange);
            }

            return new EvalResult(list[index], null);
        }

        return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
    }

    private EvalResult EvalCall(CallNode call)
    {
        if (call.Method is not null)
        {
            return EvalMethodCall(call.Target, call.Method, call.Arguments);
        }

        if (call.Target is IdentifierNode id)
        {
            return EvalFunctionCall(id.Name, call.Arguments);
        }

        return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
    }

    private EvalResult EvalMethodCall(AstNode target, string method, IReadOnlyList<AstNode> args)
    {
        // Special case: device methods
        if (target is IdentifierNode { Name: "device" })
        {
            if (_context.TryGetVariable("device", out var devObj))
            {
                var evaluatedArgs = new List<WpValue>();
                foreach (var arg in args)
                {
                    var r = Eval(arg);
                    if (r.ErrorCode is not null)
                    {
                        return r;
                    }

                    evaluatedArgs.Add(r.Value);
                }

                if (devObj.TryCallMethod(method, evaluatedArgs, out var resVal))
                {
                    return new EvalResult(resVal, null);
                }
            }

            return new EvalResult(WpValue.Null, null);
        }

        var targetRes = Eval(target);
        if (targetRes.ErrorCode is not null)
        {
            return targetRes;
        }

        switch (method)
        {
            case "size":
                if (targetRes.Value.Kind == WpValueKind.String)
                {
                    return new EvalResult(WpValue.Int(targetRes.Value.AsString().Length), null);
                }

                if (targetRes.Value.Kind == WpValueKind.List)
                {
                    return new EvalResult(WpValue.Int(targetRes.Value.AsList().Count), null);
                }

                return new EvalResult(WpValue.Int(0), null);

            case "contains":
            case "startsWith":
            case "endsWith":
                if (args.Count != 1)
                {
                    return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
                }

                var subRes = Eval(args[0]);
                if (subRes.ErrorCode is not null)
                {
                    return subRes;
                }

                if (targetRes.Value.Kind == WpValueKind.String && subRes.Value.Kind == WpValueKind.String)
                {
                    var s = targetRes.Value.AsString();
                    var sub = subRes.Value.AsString();
                    var b = method switch
                    {
                        "contains" => s.Contains(sub, StringComparison.Ordinal),
                        "startsWith" => s.StartsWith(sub, StringComparison.Ordinal),
                        "endsWith" => s.EndsWith(sub, StringComparison.Ordinal),
                        _ => false,
                    };
                    return new EvalResult(WpValue.Bool(b), null);
                }

                return new EvalResult(WpValue.Bool(false), null);

            case "lower":
            case "upper":
                if (targetRes.Value.Kind == WpValueKind.String)
                {
                    var s = targetRes.Value.AsString();
                    var transformed = method == "lower" ? s.ToLowerInvariant() : s.ToUpperInvariant();
                    return new EvalResult(WpValue.String(transformed), null);
                }

                return new EvalResult(WpValue.Null, null);

            case "matches":
                if (args.Count != 1)
                {
                    return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
                }

                var reRes = Eval(args[0]);
                if (reRes.ErrorCode is not null)
                {
                    return reRes;
                }

                if (targetRes.Value.Kind == WpValueKind.String && reRes.Value.Kind == WpValueKind.String)
                {
                    try
                    {
                        var isMatch = Regex.IsMatch(
                            targetRes.Value.AsString(),
                            reRes.Value.AsString(),
                            RegexOptions.NonBacktracking,
                            TimeSpan.FromMilliseconds(10));
                        return new EvalResult(WpValue.Bool(isMatch), null);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return new EvalResult(WpValue.Bool(false), ExpressionErrors.EvalRegexTimeout);
                    }
                    catch
                    {
                        return new EvalResult(WpValue.Bool(false), ExpressionErrors.EvalError);
                    }
                }

                return new EvalResult(WpValue.Bool(false), null);

            case "hour":
            case "dayOfWeek":
            case "minute":
                if (targetRes.Value.Kind == WpValueKind.Timestamp)
                {
                    string? tz = null;
                    if (args.Count > 0)
                    {
                        var tzRes = Eval(args[0]);
                        if (tzRes.ErrorCode is not null)
                        {
                            return tzRes;
                        }

                        if (tzRes.Value.Kind == WpValueKind.String)
                        {
                            tz = tzRes.Value.AsString();
                        }
                    }

                    return EvalTimestampMember(targetRes.Value.AsTimestamp(), method, tz);
                }

                return new EvalResult(WpValue.Null, null);

            default:
                return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
        }
    }

    private EvalResult EvalFunctionCall(string name, IReadOnlyList<AstNode> args)
    {
        switch (name)
        {
            case "size":
                if (args.Count != 1)
                {
                    return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
                }

                return EvalMethodCall(args[0], "size", []);

            case "has":
                return EvalHas(args);

            case "int":
                if (args.Count != 1)
                {
                    return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
                }

                var intArg = Eval(args[0]);
                if (intArg.ErrorCode is not null)
                {
                    return intArg;
                }

                if (intArg.Value.Kind == WpValueKind.Int)
                {
                    return intArg;
                }

                if (intArg.Value.Kind == WpValueKind.Decimal)
                {
                    return new EvalResult(WpValue.Int((long)decimal.Truncate(intArg.Value.AsDecimal())), null);
                }

                if (intArg.Value.Kind == WpValueKind.String && long.TryParse(intArg.Value.AsString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedInt))
                {
                    return new EvalResult(WpValue.Int(parsedInt), null);
                }

                return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);

            case "decimal":
                if (args.Count != 1)
                {
                    return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
                }

                var decArg = Eval(args[0]);
                if (decArg.ErrorCode is not null)
                {
                    return decArg;
                }

                if (decArg.Value.Kind == WpValueKind.Decimal)
                {
                    return decArg;
                }

                if (decArg.Value.Kind == WpValueKind.Int)
                {
                    return new EvalResult(WpValue.Decimal((decimal)decArg.Value.AsInt()), null);
                }

                if (decArg.Value.Kind == WpValueKind.String && decimal.TryParse(decArg.Value.AsString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsedDec))
                {
                    return new EvalResult(WpValue.Decimal(parsedDec), null);
                }

                return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);

            case "string":
                if (args.Count != 1)
                {
                    return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
                }

                var strArg = Eval(args[0]);
                if (strArg.ErrorCode is not null)
                {
                    return strArg;
                }

                if (strArg.Value.Kind is WpValueKind.String or WpValueKind.Enum)
                {
                    var text = strArg.Value.Kind == WpValueKind.Enum ? strArg.Value.AsEnum() : strArg.Value.AsString();
                    return new EvalResult(WpValue.String(text), null);
                }

                if (strArg.Value.Kind == WpValueKind.Int)
                {
                    return new EvalResult(WpValue.String(strArg.Value.AsInt().ToString(CultureInfo.InvariantCulture)), null);
                }

                if (strArg.Value.Kind == WpValueKind.Decimal)
                {
                    return new EvalResult(WpValue.String(strArg.Value.AsDecimal().ToString(CultureInfo.InvariantCulture)), null);
                }

                if (strArg.Value.Kind == WpValueKind.Bool)
                {
                    return new EvalResult(WpValue.String(strArg.Value.AsBool() ? "true" : "false"), null);
                }

                return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);

            case "duration":
                if (args.Count == 1 && args[0] is LiteralNode { Kind: WpValueKind.String } durLit)
                {
                    var durStr = (string)durLit.Value!;
                    try
                    {
                        var ts = System.Xml.XmlConvert.ToTimeSpan(durStr);
                        return new EvalResult(WpValue.Duration(ts), null);
                    }
                    catch
                    {
                        return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
                    }
                }

                return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);

            case "timestamp":
                if (args.Count == 1 && args[0] is LiteralNode { Kind: WpValueKind.String } tsLit)
                {
                    var tsStr = (string)tsLit.Value!;
                    if (DateTimeOffset.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                    {
                        return new EvalResult(WpValue.Timestamp(dto), null);
                    }
                }

                return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);

            case "ago":
                if (args.Count != 1)
                {
                    return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
                }

                var agoArg = Eval(args[0]);
                if (agoArg.ErrorCode is not null)
                {
                    return agoArg;
                }

                if (agoArg.Value.Kind == WpValueKind.Duration)
                {
                    var past = _context.Now - agoArg.Value.AsDuration();
                    return new EvalResult(WpValue.Timestamp(past), null);
                }

                return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);

            case "inSchedule":
                if (args.Count == 1 && args[0] is LiteralNode { Kind: WpValueKind.String } calLit)
                {
                    var calKey = (string)calLit.Value!;
                    var active = _context.InSchedule(calKey, _context.Now);
                    return new EvalResult(WpValue.Bool(active), null);
                }

                return new EvalResult(WpValue.Bool(false), null);

            case "abs":
                if (args.Count != 1)
                {
                    return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
                }

                var absArg = Eval(args[0]);
                if (absArg.ErrorCode is not null)
                {
                    return absArg;
                }

                if (absArg.Value.Kind == WpValueKind.Int)
                {
                    return new EvalResult(WpValue.Int(Math.Abs(absArg.Value.AsInt())), null);
                }

                if (absArg.Value.Kind == WpValueKind.Decimal)
                {
                    return new EvalResult(WpValue.Decimal(Math.Abs(absArg.Value.AsDecimal())), null);
                }

                return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);

            case "min":
            case "max":
                if (args.Count != 2)
                {
                    return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
                }

                var a1 = Eval(args[0]);
                if (a1.ErrorCode is not null)
                {
                    return a1;
                }

                var a2 = Eval(args[1]);
                if (a2.ErrorCode is not null)
                {
                    return a2;
                }

                if (a1.Value.Kind == WpValueKind.Decimal || a2.Value.Kind == WpValueKind.Decimal)
                {
                    var d1 = a1.Value.Kind == WpValueKind.Decimal ? a1.Value.AsDecimal() : a1.Value.AsInt();
                    var d2 = a2.Value.Kind == WpValueKind.Decimal ? a2.Value.AsDecimal() : a2.Value.AsInt();
                    var res = name == "min" ? Math.Min(d1, d2) : Math.Max(d1, d2);
                    return new EvalResult(WpValue.Decimal(res), null);
                }

                if (a1.Value.Kind == WpValueKind.Int && a2.Value.Kind == WpValueKind.Int)
                {
                    var i1 = a1.Value.AsInt();
                    var i2 = a2.Value.AsInt();
                    var res = name == "min" ? Math.Min(i1, i2) : Math.Max(i1, i2);
                    return new EvalResult(WpValue.Int(res), null);
                }

                return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);

            case "round":
                if (args.Count is not 1 and not 2)
                {
                    return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
                }

                var roundArg = Eval(args[0]);
                if (roundArg.ErrorCode is not null)
                {
                    return roundArg;
                }

                var digits = 0;
                if (args.Count == 2)
                {
                    var digitsArg = Eval(args[1]);
                    if (digitsArg.ErrorCode is not null)
                    {
                        return digitsArg;
                    }

                    if (digitsArg.Value.Kind == WpValueKind.Int)
                    {
                        digits = (int)digitsArg.Value.AsInt();
                    }
                }

                var dec = roundArg.Value.Kind == WpValueKind.Decimal ? roundArg.Value.AsDecimal() : roundArg.Value.AsInt();
                var rounded = Math.Round(dec, digits, MidpointRounding.AwayFromZero);
                return new EvalResult(WpValue.Decimal(rounded), null);

            default:
                return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
        }
    }

    private EvalResult EvalHas(IReadOnlyList<AstNode> args)
    {
        if (args.Count != 1)
        {
            return new EvalResult(WpValue.Bool(false), ExpressionErrors.EvalError);
        }

        var path = BuildMemberPath(args[0]);
        if (path is not null && path.Count >= 2 && path[0] == "device")
        {
            if (_context.TryGetVariable("device", out var devObj))
            {
                var member = string.Join(".", path.Skip(1));
                return new EvalResult(WpValue.Bool(devObj.HasMember(member)), null);
            }
        }

        return new EvalResult(WpValue.Bool(false), null);
    }

    private EvalResult EvalTimestampMember(DateTimeOffset ts, string member, string? tzId)
    {
        var zone = _context.SiteTimeZone;
        if (!string.IsNullOrEmpty(tzId))
        {
            var res = Watchpoint.Kernel.Time.SiteTimeZone.FromIana(tzId);
            if (res.TryGetValue(out var parsedZone, out _))
            {
                zone = parsedZone;
            }
        }

        var local = zone.ToLocal(ts);
        return member switch
        {
            "hour" => new EvalResult(WpValue.Int(local.Hour), null),
            "minute" => new EvalResult(WpValue.Int(local.Minute), null),
            "dayOfWeek" => new EvalResult(WpValue.Int((int)local.DayOfWeek), null),
            _ => new EvalResult(WpValue.Null, ExpressionErrors.EvalError),
        };
    }

    private EvalResult EvalUnary(UnaryNode unary)
    {
        var res = Eval(unary.Operand);
        if (res.ErrorCode is not null)
        {
            return res;
        }

        if (unary.Operator == "!")
        {
            if (res.Value.Kind == WpValueKind.Bool)
            {
                return new EvalResult(WpValue.Bool(!res.Value.AsBool()), null);
            }

            return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
        }

        if (unary.Operator == "-")
        {
            if (res.Value.Kind == WpValueKind.Int)
            {
                return new EvalResult(WpValue.Int(-res.Value.AsInt()), null);
            }

            if (res.Value.Kind == WpValueKind.Decimal)
            {
                return new EvalResult(WpValue.Decimal(-res.Value.AsDecimal()), null);
            }

            return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
        }

        return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
    }

    private EvalResult EvalBinary(BinaryNode binary)
    {
        // Short-circuit logic for && and ||
        if (binary.Operator == "&&")
        {
            var left = Eval(binary.Left);
            if (left.ErrorCode is null && left.Value.Kind == WpValueKind.Bool && !left.Value.AsBool())
            {
                // false && <anything> = false (even if right errors)
                return new EvalResult(WpValue.Bool(false), null);
            }

            var right = Eval(binary.Right);
            if (right.ErrorCode is null && right.Value.Kind == WpValueKind.Bool && !right.Value.AsBool())
            {
                // <error> && false = false
                return new EvalResult(WpValue.Bool(false), null);
            }

            if (left.ErrorCode is not null)
            {
                return left;
            }

            if (right.ErrorCode is not null)
            {
                return right;
            }

            if (left.Value.Kind == WpValueKind.Bool && right.Value.Kind == WpValueKind.Bool)
            {
                return new EvalResult(WpValue.Bool(left.Value.AsBool() && right.Value.AsBool()), null);
            }

            return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
        }

        if (binary.Operator == "||")
        {
            var left = Eval(binary.Left);
            if (left.ErrorCode is null && left.Value.Kind == WpValueKind.Bool && left.Value.AsBool())
            {
                // true || <anything> = true (even if right errors)
                return new EvalResult(WpValue.Bool(true), null);
            }

            var right = Eval(binary.Right);
            if (right.ErrorCode is null && right.Value.Kind == WpValueKind.Bool && right.Value.AsBool())
            {
                // <error> || true = true
                return new EvalResult(WpValue.Bool(true), null);
            }

            if (left.ErrorCode is not null)
            {
                return left;
            }

            if (right.ErrorCode is not null)
            {
                return right;
            }

            if (left.Value.Kind == WpValueKind.Bool && right.Value.Kind == WpValueKind.Bool)
            {
                return new EvalResult(WpValue.Bool(left.Value.AsBool() || right.Value.AsBool()), null);
            }

            return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
        }

        var l = Eval(binary.Left);
        if (l.ErrorCode is not null)
        {
            return l;
        }

        var r = Eval(binary.Right);
        if (r.ErrorCode is not null)
        {
            return r;
        }

        return binary.Operator switch
        {
            "==" => EvalEqual(l.Value, r.Value),
            "!=" => EvalNotEqual(l.Value, r.Value),
            "<" => EvalCompare(l.Value, r.Value, c => c < 0),
            "<=" => EvalCompare(l.Value, r.Value, c => c <= 0),
            ">" => EvalCompare(l.Value, r.Value, c => c > 0),
            ">=" => EvalCompare(l.Value, r.Value, c => c >= 0),
            "in" => EvalIn(l.Value, r.Value),
            "+" => EvalAdd(l.Value, r.Value),
            "-" => EvalSubtract(l.Value, r.Value),
            "*" => EvalMultiply(l.Value, r.Value),
            "/" => EvalDivide(l.Value, r.Value),
            "%" => EvalModulo(l.Value, r.Value),
            _ => new EvalResult(WpValue.Null, ExpressionErrors.EvalError),
        };
    }

    private static EvalResult EvalEqual(WpValue left, WpValue right)
    {
        if (left.Kind == WpValueKind.Null && right.Kind == WpValueKind.Null)
        {
            return new EvalResult(WpValue.Bool(true), null);
        }

        if (left.Kind == WpValueKind.Null || right.Kind == WpValueKind.Null)
        {
            return new EvalResult(WpValue.Bool(false), null);
        }

        if (left.Kind is WpValueKind.Int or WpValueKind.Decimal && right.Kind is WpValueKind.Int or WpValueKind.Decimal)
        {
            var d1 = left.Kind == WpValueKind.Decimal ? left.AsDecimal() : left.AsInt();
            var d2 = right.Kind == WpValueKind.Decimal ? right.AsDecimal() : right.AsInt();
            return new EvalResult(WpValue.Bool(d1 == d2), null);
        }

        if (left.Kind is WpValueKind.String or WpValueKind.Enum && right.Kind is WpValueKind.String or WpValueKind.Enum)
        {
            var s1 = left.Kind == WpValueKind.Enum ? left.AsEnum() : left.AsString();
            var s2 = right.Kind == WpValueKind.Enum ? right.AsEnum() : right.AsString();
            return new EvalResult(WpValue.Bool(string.Equals(s1, s2, StringComparison.Ordinal)), null);
        }

        return new EvalResult(WpValue.Bool(left == right), null);
    }

    private static EvalResult EvalNotEqual(WpValue left, WpValue right)
    {
        var eq = EvalEqual(left, right);
        return new EvalResult(WpValue.Bool(!eq.Value.AsBool()), eq.ErrorCode);
    }

    private static EvalResult EvalCompare(WpValue left, WpValue right, Func<int, bool> predicate)
    {
        if (left.Kind == WpValueKind.Null || right.Kind == WpValueKind.Null)
        {
            // Spec EXP §4.3: comparisons with null other than ==/!= evaluate to false (runtime)
            return new EvalResult(WpValue.Bool(false), null);
        }

        if (left.Kind is WpValueKind.Int or WpValueKind.Decimal && right.Kind is WpValueKind.Int or WpValueKind.Decimal)
        {
            var d1 = left.Kind == WpValueKind.Decimal ? left.AsDecimal() : left.AsInt();
            var d2 = right.Kind == WpValueKind.Decimal ? right.AsDecimal() : right.AsInt();
            return new EvalResult(WpValue.Bool(predicate(d1.CompareTo(d2))), null);
        }

        if (left.Kind == WpValueKind.Timestamp && right.Kind == WpValueKind.Timestamp)
        {
            return new EvalResult(WpValue.Bool(predicate(left.AsTimestamp().CompareTo(right.AsTimestamp()))), null);
        }

        if (left.Kind == WpValueKind.Duration && right.Kind == WpValueKind.Duration)
        {
            return new EvalResult(WpValue.Bool(predicate(left.AsDuration().CompareTo(right.AsDuration()))), null);
        }

        if (left.Kind == WpValueKind.String && right.Kind == WpValueKind.String)
        {
            return new EvalResult(WpValue.Bool(predicate(string.CompareOrdinal(left.AsString(), right.AsString()))), null);
        }

        return new EvalResult(WpValue.Bool(false), null);
    }

    private static EvalResult EvalIn(WpValue left, WpValue right)
    {
        if (right.Kind == WpValueKind.List)
        {
            var list = right.AsList();
            foreach (var item in list)
            {
                var eq = EvalEqual(left, item);
                if (eq.Value.AsBool())
                {
                    return new EvalResult(WpValue.Bool(true), null);
                }
            }

            return new EvalResult(WpValue.Bool(false), null);
        }

        return new EvalResult(WpValue.Bool(false), null);
    }

    private static EvalResult EvalAdd(WpValue left, WpValue right)
    {
        if (left.Kind == WpValueKind.String && right.Kind == WpValueKind.String)
        {
            return new EvalResult(WpValue.String(left.AsString() + right.AsString()), null);
        }

        if (left.Kind == WpValueKind.Timestamp && right.Kind == WpValueKind.Duration)
        {
            return new EvalResult(WpValue.Timestamp(left.AsTimestamp() + right.AsDuration()), null);
        }

        if (left.Kind is WpValueKind.Int or WpValueKind.Decimal && right.Kind is WpValueKind.Int or WpValueKind.Decimal)
        {
            if (left.Kind == WpValueKind.Decimal || right.Kind == WpValueKind.Decimal)
            {
                var d1 = left.Kind == WpValueKind.Decimal ? left.AsDecimal() : left.AsInt();
                var d2 = right.Kind == WpValueKind.Decimal ? right.AsDecimal() : right.AsInt();
                return new EvalResult(WpValue.Decimal(d1 + d2), null);
            }

            return new EvalResult(WpValue.Int(left.AsInt() + right.AsInt()), null);
        }

        return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
    }

    private static EvalResult EvalSubtract(WpValue left, WpValue right)
    {
        if (left.Kind == WpValueKind.Timestamp && right.Kind == WpValueKind.Duration)
        {
            return new EvalResult(WpValue.Timestamp(left.AsTimestamp() - right.AsDuration()), null);
        }

        if (left.Kind == WpValueKind.Timestamp && right.Kind == WpValueKind.Timestamp)
        {
            return new EvalResult(WpValue.Duration(left.AsTimestamp() - right.AsTimestamp()), null);
        }

        if (left.Kind is WpValueKind.Int or WpValueKind.Decimal && right.Kind is WpValueKind.Int or WpValueKind.Decimal)
        {
            if (left.Kind == WpValueKind.Decimal || right.Kind == WpValueKind.Decimal)
            {
                var d1 = left.Kind == WpValueKind.Decimal ? left.AsDecimal() : left.AsInt();
                var d2 = right.Kind == WpValueKind.Decimal ? right.AsDecimal() : right.AsInt();
                return new EvalResult(WpValue.Decimal(d1 - d2), null);
            }

            return new EvalResult(WpValue.Int(left.AsInt() - right.AsInt()), null);
        }

        return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
    }

    private static EvalResult EvalMultiply(WpValue left, WpValue right)
    {
        if (left.Kind is WpValueKind.Int or WpValueKind.Decimal && right.Kind is WpValueKind.Int or WpValueKind.Decimal)
        {
            if (left.Kind == WpValueKind.Decimal || right.Kind == WpValueKind.Decimal)
            {
                var d1 = left.Kind == WpValueKind.Decimal ? left.AsDecimal() : left.AsInt();
                var d2 = right.Kind == WpValueKind.Decimal ? right.AsDecimal() : right.AsInt();
                return new EvalResult(WpValue.Decimal(d1 * d2), null);
            }

            return new EvalResult(WpValue.Int(left.AsInt() * right.AsInt()), null);
        }

        return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
    }

    private static EvalResult EvalDivide(WpValue left, WpValue right)
    {
        if (left.Kind is WpValueKind.Int or WpValueKind.Decimal && right.Kind is WpValueKind.Int or WpValueKind.Decimal)
        {
            if (left.Kind == WpValueKind.Decimal || right.Kind == WpValueKind.Decimal)
            {
                var d1 = left.Kind == WpValueKind.Decimal ? left.AsDecimal() : left.AsInt();
                var d2 = right.Kind == WpValueKind.Decimal ? right.AsDecimal() : right.AsInt();
                if (d2 == 0)
                {
                    return new EvalResult(WpValue.Null, ExpressionErrors.EvalDivisionByZero);
                }

                return new EvalResult(WpValue.Decimal(d1 / d2), null);
            }

            var i1 = left.AsInt();
            var i2 = right.AsInt();
            if (i2 == 0)
            {
                return new EvalResult(WpValue.Null, ExpressionErrors.EvalDivisionByZero);
            }

            return new EvalResult(WpValue.Int(i1 / i2), null);
        }

        return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
    }

    private static EvalResult EvalModulo(WpValue left, WpValue right)
    {
        if (left.Kind is WpValueKind.Int or WpValueKind.Decimal && right.Kind is WpValueKind.Int or WpValueKind.Decimal)
        {
            if (left.Kind == WpValueKind.Decimal || right.Kind == WpValueKind.Decimal)
            {
                var d1 = left.Kind == WpValueKind.Decimal ? left.AsDecimal() : left.AsInt();
                var d2 = right.Kind == WpValueKind.Decimal ? right.AsDecimal() : right.AsInt();
                if (d2 == 0)
                {
                    return new EvalResult(WpValue.Null, ExpressionErrors.EvalDivisionByZero);
                }

                return new EvalResult(WpValue.Decimal(d1 % d2), null);
            }

            var i1 = left.AsInt();
            var i2 = right.AsInt();
            if (i2 == 0)
            {
                return new EvalResult(WpValue.Null, ExpressionErrors.EvalDivisionByZero);
            }

            return new EvalResult(WpValue.Int(i1 % i2), null);
        }

        return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
    }

    private EvalResult EvalConditional(ConditionalNode cond)
    {
        var condition = Eval(cond.Condition);
        if (condition.ErrorCode is not null)
        {
            return condition;
        }

        if (condition.Value.Kind == WpValueKind.Bool)
        {
            return condition.Value.AsBool() ? Eval(cond.TrueBranch) : Eval(cond.FalseBranch);
        }

        return new EvalResult(WpValue.Null, ExpressionErrors.EvalError);
    }

    private EvalResult EvalList(ListNode list)
    {
        var items = new List<WpValue>();
        foreach (var item in list.Items)
        {
            var r = Eval(item);
            if (r.ErrorCode is not null)
            {
                return r;
            }

            items.Add(r.Value);
        }

        return new EvalResult(WpValue.List(items), null);
    }

    private sealed class LimitExceededException : Exception;
}
