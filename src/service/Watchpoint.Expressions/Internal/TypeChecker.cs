using System.Text.RegularExpressions;
using Watchpoint.Expressions.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;

namespace Watchpoint.Expressions.Internal;

internal sealed class TypeChecker
{
    private readonly ExpressionEnvironment _environment;
    private readonly ExpectedType _expected;
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly List<PropertyPath> _referencedProperties = [];
    private readonly List<string> _referencedRefs = [];
    private readonly List<string> _referencedCalendars = [];
    private bool _usesNow;

    public TypeChecker(ExpressionEnvironment environment, ExpectedType expected)
    {
        _environment = environment;
        _expected = expected;
    }

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;
    public IReadOnlyList<PropertyPath> ReferencedProperties => _referencedProperties;
    public IReadOnlyList<string> ReferencedRefs => _referencedRefs;
    public IReadOnlyList<string> ReferencedCalendars => _referencedCalendars;
    public bool UsesNow => _usesNow;

    public ExprType Check(AstNode node)
    {
        var resultType = CheckNode(node);

        if (_expected == ExpectedType.Bool && resultType.Kind != ExprTypeKind.Bool && resultType.Kind != ExprTypeKind.Unknown)
        {
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                ExpressionErrors.ResultNotBool,
                new Dictionary<string, object?> { ["actual"] = resultType.Kind.ToString().ToLowerInvariant() },
                node.Start,
                node.Length));
        }

        return resultType;
    }

    private ExprType CheckNode(AstNode node)
    {
        return node switch
        {
            LiteralNode lit => lit.Kind switch
            {
                WpValueKind.Bool => ExprType.Bool,
                WpValueKind.Int => ExprType.Int,
                WpValueKind.Decimal => ExprType.Decimal,
                WpValueKind.String => ExprType.String,
                WpValueKind.Null => ExprType.Null,
                _ => ExprType.Unknown,
            },

            IdentifierNode id => CheckIdentifier(id),

            MemberAccessNode member => CheckMemberAccess(member),

            IndexAccessNode idx => CheckIndexAccess(idx),

            CallNode call => CheckCall(call),

            UnaryNode unary => CheckUnary(unary),

            BinaryNode binary => CheckBinary(binary),

            ConditionalNode cond => CheckConditional(cond),

            ListNode list => CheckList(list),

            _ => ExprType.Unknown,
        };
    }

    private ExprType CheckIdentifier(IdentifierNode id)
    {
        switch (id.Name)
        {
            case "now":
                _usesNow = true;
                return ExprType.Timestamp;

            case "device":
                return ExprType.Object;

            case "event":
                return ExprType.Object;

            case "refs":
                return ExprType.Object;

            default:
                if (_environment.Variables.TryGetValue(id.Name, out var varType))
                {
                    return varType;
                }

                var candidates = new List<string> { "now", "device", "event", "refs" };
                candidates.AddRange(_environment.Variables.Keys);
                var suggestions = Suggestions.FindSuggestions(id.Name, candidates);

                _diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    ExpressionErrors.UnknownMember,
                    new Dictionary<string, object?>
                    {
                        ["name"] = id.Name,
                        ["on"] = "environment",
                        ["suggestions"] = string.Join(", ", suggestions),
                    },
                    id.Start,
                    id.Length));

                return ExprType.Unknown;
        }
    }

    private ExprType CheckMemberAccess(MemberAccessNode member)
    {
        // Check target first
        if (member.Target is IdentifierNode id)
        {
            if (id.Name == "device")
            {
                return CheckDeviceMember(member.Member, member.Start, member.Length);
            }

            if (id.Name == "event")
            {
                return CheckEventMember(member.Member, member.Start, member.Length);
            }

            if (id.Name == "refs")
            {
                _referencedRefs.Add(member.Member);
                if (_environment.Refs.TryGetValue(member.Member, out _))
                {
                    return ExprType.Object;
                }

                var suggestions = Suggestions.FindSuggestions(member.Member, _environment.Refs.Keys);
                _diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    ExpressionErrors.UnknownMember,
                    new Dictionary<string, object?>
                    {
                        ["name"] = member.Member,
                        ["on"] = "refs",
                        ["suggestions"] = string.Join(", ", suggestions),
                    },
                    member.Start,
                    member.Length));
                return ExprType.Unknown;
            }
        }
        else if (member.Target is MemberAccessNode parentMember)
        {
            // e.g. device.<cap>.<prop> or refs.<refName>.<cap>.<prop> or device.health.<field>
            var fullPath = BuildMemberPath(member);
            if (fullPath is not null)
            {
                return CheckPathAccess(fullPath, member.Start, member.Length);
            }
        }

        var targetType = CheckNode(member.Target);
        if (targetType.Kind == ExprTypeKind.Timestamp)
        {
            if (member.Member is "hour" or "dayOfWeek" or "minute")
            {
                return ExprType.Int;
            }
        }

        return ExprType.Unknown;
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

    private static bool TryMakePropertyPath(string cap, string member, out PropertyPath path)
    {
        if (CapabilityId.Parse(cap).TryGetValue(out var capId, out _) &&
            MemberId.Parse(member).TryGetValue(out var memberId, out _))
        {
            path = new PropertyPath(capId, memberId);
            return true;
        }

        path = default;
        return false;
    }

    private ExprType CheckPathAccess(List<string> path, int start, int length)
    {
        if (path.Count < 2)
        {
            return ExprType.Unknown;
        }

        if (path[0] == "device")
        {
            return CheckDevicePath(path.Skip(1).ToList(), start, length);
        }

        if (path[0] == "refs" && path.Count >= 2)
        {
            var refName = path[1];
            _referencedRefs.Add(refName);
            if (_environment.Refs.TryGetValue(refName, out var refTypes))
            {
                var remaining = path.Skip(2).ToList();
                return CheckDeviceTypesPath(refTypes, remaining, start, length, $"refs.{refName}");
            }

            return ExprType.Unknown;
        }

        if (path[0] == "event")
        {
            return CheckEventPath(path.Skip(1).ToList(), start, length);
        }

        return ExprType.Unknown;
    }

    private ExprType CheckDeviceMember(string member, int start, int length)
    {
        switch (member)
        {
            case "health":
                return ExprType.Object;
            case "overall":
                return ExprType.String;
            case "attributes":
                return ExprType.Object;
            case "tags":
                return ExprType.ListOf(ExprType.String);
            case "location":
                return ExprType.ListOf(ExprType.String);
            case "type":
                return ExprType.String;
            case "name":
                return ExprType.String;
            case "maintenance":
            case "enabled":
                return ExprType.Bool;
            default:
                // Check if member is a capability id across device types
                var capExists = _environment.DeviceTypes.Any(t => t.Capabilities.Any(c => c.Key.Value == member) || t.Properties.Any(p => p.Key.Capability.Value == member));
                if (capExists)
                {
                    return ExprType.Object;
                }

                // Collect candidates
                var candidates = new HashSet<string>(StringComparer.Ordinal)
                {
                    "health", "overall", "attributes", "tags", "location", "type", "name", "maintenance", "enabled",
                };
                foreach (var dt in _environment.DeviceTypes)
                {
                    foreach (var c in dt.Capabilities.Keys)
                    {
                        candidates.Add(c.Value);
                    }
                }

                var suggestions = Suggestions.FindSuggestions(member, candidates);
                _diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    ExpressionErrors.UnknownMember,
                    new Dictionary<string, object?>
                    {
                        ["name"] = member,
                        ["on"] = "device",
                        ["suggestions"] = string.Join(", ", suggestions),
                    },
                    start,
                    length));
                return ExprType.Unknown;
        }
    }

    private ExprType CheckDevicePath(List<string> path, int start, int length)
    {
        if (path.Count == 2 && path[0] == "health")
        {
            if (path[1] == "connectivity")
            {
                return ExprType.String;
            }

            if (path[1] == "faults")
            {
                return ExprType.ListOf(ExprType.String);
            }
        }

        if (path.Count >= 2 && path[0] == "attributes")
        {
            return ExprType.String;
        }

        return CheckDeviceTypesPath(_environment.DeviceTypes, path, start, length, "device");
    }

    private ExprType CheckDeviceTypesPath(IReadOnlyList<DeviceTypeDefinition> deviceTypes, List<string> path, int start, int length, string targetName)
    {
        if (deviceTypes.Count == 0)
        {
            // No device types specified; allow member access dynamically
            return ExprType.Unknown;
        }

        if (path.Count == 2)
        {
            var capId = path[0];
            var propId = path[1];
            if (!TryMakePropertyPath(capId, propId, out var propPath))
            {
                return ExprType.Unknown;
            }

            _referencedProperties.Add(propPath);

            var typesWithProp = new List<DeviceTypeDefinition>();
            var typesWithoutProp = new List<DeviceTypeDefinition>();
            PropertyDefinition? sampleProp = null;

            foreach (var dt in deviceTypes)
            {
                if (dt.Properties.TryGetValue(propPath, out var propDef))
                {
                    typesWithProp.Add(dt);
                    sampleProp ??= propDef;
                }
                else
                {
                    typesWithoutProp.Add(dt);
                }
            }

            if (typesWithProp.Count == 0)
            {
                var candidates = deviceTypes.SelectMany(dt => dt.Properties.Keys).Select(p => p.ToString()).Distinct(StringComparer.Ordinal);
                var suggestions = Suggestions.FindSuggestions(propPath.ToString(), candidates);

                _diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    ExpressionErrors.UnknownMember,
                    new Dictionary<string, object?>
                    {
                        ["name"] = propPath.ToString(),
                        ["on"] = targetName,
                        ["suggestions"] = string.Join(", ", suggestions),
                    },
                    start,
                    length));
                return ExprType.Unknown;
            }

            if (typesWithoutProp.Count > 0)
            {
                _diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    ExpressionErrors.MemberNotCommon,
                    new Dictionary<string, object?>
                    {
                        ["name"] = propPath.ToString(),
                        ["types"] = string.Join(", ", typesWithoutProp.Select(t => t.Ref.Id.ToString())),
                    },
                    start,
                    length));
            }

            return MapPropertyType(sampleProp!);
        }

        return ExprType.Object;
    }

    private static ExprType MapPropertyType(PropertyDefinition prop)
    {
        return prop.ValueType switch
        {
            "boolean" => ExprType.Bool,
            "integer" => ExprType.Int,
            "decimal" => ExprType.Decimal,
            "string" => ExprType.String,
            "enum" => ExprType.EnumString(prop.EnumValues.Select(e => e.Code).ToList()),
            "timestamp" => ExprType.Timestamp,
            "duration" => ExprType.Duration,
            "list" => ExprType.ListOf(MapItemType(prop.ItemType)),
            _ => ExprType.Unknown,
        };
    }

    private static ExprType MapItemType(string? itemType) => itemType switch
    {
        "boolean" => ExprType.Bool,
        "integer" => ExprType.Int,
        "decimal" => ExprType.Decimal,
        "string" => ExprType.String,
        _ => ExprType.String,
    };

    private static ExprType CheckEventMember(string member, int start, int length)
    {
        return member switch
        {
            "kind" => ExprType.String,
            "severity" => ExprType.String,
            "sourceTime" => ExprType.Timestamp,
            "payload" => ExprType.Object,
            _ => ExprType.Unknown,
        };
    }

    private static ExprType CheckEventPath(List<string> path, int start, int length)
    {
        if (path.Count == 2 && path[0] == "payload")
        {
            return ExprType.String;
        }

        return ExprType.Unknown;
    }

    private ExprType CheckIndexAccess(IndexAccessNode idx)
    {
        var targetType = CheckNode(idx.Target);
        var indexType = CheckNode(idx.Index);

        if (indexType.Kind != ExprTypeKind.Int && indexType.Kind != ExprTypeKind.Unknown)
        {
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                ExpressionErrors.TypeMismatch,
                new Dictionary<string, object?>
                {
                    ["op"] = "[]",
                    ["left"] = targetType.Kind.ToString().ToLowerInvariant(),
                    ["right"] = indexType.Kind.ToString().ToLowerInvariant(),
                },
                idx.Index.Start,
                idx.Index.Length));
        }

        if (targetType.Kind == ExprTypeKind.List)
        {
            return targetType.ItemType ?? ExprType.Unknown;
        }

        return ExprType.Unknown;
    }

    private ExprType CheckCall(CallNode call)
    {
        if (call.Method is not null)
        {
            // Method call: target.method(args)
            return CheckMethodCall(call.Target, call.Method, call.Arguments, call.Start, call.Length);
        }

        // Function call: ident(args)
        if (call.Target is IdentifierNode id)
        {
            return CheckFunctionCall(id.Name, call.Arguments, call.Start, call.Length);
        }

        return ExprType.Unknown;
    }

    private ExprType CheckMethodCall(AstNode target, string method, IReadOnlyList<AstNode> args, int start, int length)
    {
        var targetType = CheckNode(target);

        switch (method)
        {
            case "size":
                if (args.Count != 0)
                {
                    ReportArgCount(method, 0, start, length);
                }

                if (targetType.Kind is not ExprTypeKind.String and not ExprTypeKind.List and not ExprTypeKind.Unknown)
                {
                    ReportTypeMismatch(method, "string or list", targetType.Kind.ToString().ToLowerInvariant(), start, length);
                }

                return ExprType.Int;

            case "contains":
            case "startsWith":
            case "endsWith":
                if (args.Count != 1)
                {
                    ReportArgCount(method, 1, start, length);
                }
                else
                {
                    var argType = CheckNode(args[0]);
                    if (argType.Kind != ExprTypeKind.String && argType.Kind != ExprTypeKind.Unknown)
                    {
                        ReportTypeMismatch(method, "string", argType.Kind.ToString().ToLowerInvariant(), args[0].Start, args[0].Length);
                    }
                }

                return ExprType.Bool;

            case "lower":
            case "upper":
                if (args.Count != 0)
                {
                    ReportArgCount(method, 0, start, length);
                }

                return ExprType.String;

            case "matches":
                if (args.Count != 1)
                {
                    ReportArgCount(method, 1, start, length);
                }
                else
                {
                    CheckRegexArg(args[0]);
                }

                return ExprType.Bool;

            case "hour":
            case "dayOfWeek":
            case "minute":
                if (args.Count > 1)
                {
                    ReportArgCount(method, 1, start, length);
                }

                return ExprType.Int;

            case "quality":
            case "changedAgo":
                if (target is IdentifierNode targetId && targetId.Name == "device")
                {
                    if (args.Count != 1)
                    {
                        ReportArgCount(method, 1, start, length);
                    }
                    else if (args[0] is LiteralNode lit && lit.Kind == WpValueKind.String)
                    {
                        var propPathStr = (string)lit.Value!;
                        if (PropertyPath.Parse(propPathStr).TryGetValue(out var pPath, out _))
                        {
                            _referencedProperties.Add(pPath);
                        }
                    }

                    return method == "quality" ? ExprType.String : ExprType.Duration;
                }

                return ExprType.Unknown;

            case "isA":
                if (target is IdentifierNode devId && devId.Name == "device")
                {
                    if (args.Count != 1)
                    {
                        ReportArgCount(method, 1, start, length);
                    }

                    return ExprType.Bool;
                }

                return ExprType.Unknown;

            default:
                _diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    ExpressionErrors.UnknownFunction,
                    new Dictionary<string, object?> { ["name"] = method, ["suggestions"] = string.Empty },
                    start,
                    length));
                return ExprType.Unknown;
        }
    }

    private ExprType CheckFunctionCall(string name, IReadOnlyList<AstNode> args, int start, int length)
    {
        switch (name)
        {
            case "size":
                if (args.Count != 1)
                {
                    ReportArgCount(name, 1, start, length);
                    return ExprType.Int;
                }

                var sizeTarget = CheckNode(args[0]);
                if (sizeTarget.Kind is not ExprTypeKind.String and not ExprTypeKind.List and not ExprTypeKind.Unknown)
                {
                    ReportTypeMismatch(name, "string or list", sizeTarget.Kind.ToString().ToLowerInvariant(), args[0].Start, args[0].Length);
                }

                return ExprType.Int;

            case "has":
                return CheckHasCall(args, start, length);

            case "int":
                if (args.Count != 1)
                {
                    ReportArgCount(name, 1, start, length);
                }
                else
                {
                    CheckNode(args[0]);
                }

                return ExprType.Int;

            case "decimal":
                if (args.Count != 1)
                {
                    ReportArgCount(name, 1, start, length);
                }
                else
                {
                    CheckNode(args[0]);
                }

                return ExprType.Decimal;

            case "string":
                if (args.Count != 1)
                {
                    ReportArgCount(name, 1, start, length);
                }
                else
                {
                    CheckNode(args[0]);
                }

                return ExprType.String;

            case "duration":
                if (args.Count != 1 || args[0] is not LiteralNode { Kind: WpValueKind.String } durLit)
                {
                    _diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        ExpressionErrors.FunctionArguments,
                        new Dictionary<string, object?> { ["name"] = name, ["expected"] = "string literal" },
                        start,
                        length));
                    return ExprType.Duration;
                }

                return ExprType.Duration;

            case "timestamp":
                if (args.Count != 1 || args[0] is not LiteralNode { Kind: WpValueKind.String } tsLit)
                {
                    _diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        ExpressionErrors.FunctionArguments,
                        new Dictionary<string, object?> { ["name"] = name, ["expected"] = "string literal" },
                        start,
                        length));
                    return ExprType.Timestamp;
                }

                return ExprType.Timestamp;

            case "ago":
                _usesNow = true;
                if (args.Count != 1)
                {
                    ReportArgCount(name, 1, start, length);
                }
                else
                {
                    var agoArg = CheckNode(args[0]);
                    if (agoArg.Kind != ExprTypeKind.Duration && agoArg.Kind != ExprTypeKind.Unknown)
                    {
                        ReportTypeMismatch(name, "duration", agoArg.Kind.ToString().ToLowerInvariant(), args[0].Start, args[0].Length);
                    }
                }

                return ExprType.Timestamp;

            case "inSchedule":
                if (args.Count != 1 || args[0] is not LiteralNode { Kind: WpValueKind.String } calLit)
                {
                    _diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        ExpressionErrors.FunctionArguments,
                        new Dictionary<string, object?> { ["name"] = name, ["expected"] = "calendar key string literal" },
                        start,
                        length));
                    return ExprType.Bool;
                }

                var calKey = (string)calLit.Value!;
                _referencedCalendars.Add(calKey);

                if (_environment.KnownCalendars.Count > 0 && !_environment.KnownCalendars.Contains(calKey))
                {
                    _diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        ExpressionErrors.UnknownCalendar,
                        new Dictionary<string, object?> { ["key"] = calKey },
                        args[0].Start,
                        args[0].Length));
                }

                return ExprType.Bool;

            case "abs":
                if (args.Count != 1)
                {
                    ReportArgCount(name, 1, start, length);
                    return ExprType.Unknown;
                }

                var absType = CheckNode(args[0]);
                return absType.Kind == ExprTypeKind.Int ? ExprType.Int : ExprType.Decimal;

            case "min":
            case "max":
                if (args.Count != 2)
                {
                    ReportArgCount(name, 2, start, length);
                    return ExprType.Unknown;
                }

                var t1 = CheckNode(args[0]);
                var t2 = CheckNode(args[1]);
                if (t1.Kind == ExprTypeKind.Decimal || t2.Kind == ExprTypeKind.Decimal)
                {
                    return ExprType.Decimal;
                }

                return ExprType.Int;

            case "round":
                if (args.Count is not 1 and not 2)
                {
                    ReportArgCount(name, 2, start, length);
                    return ExprType.Decimal;
                }

                CheckNode(args[0]);
                if (args.Count == 2)
                {
                    CheckNode(args[1]);
                }

                return ExprType.Decimal;

            default:
                var candidates = new[] { "size", "has", "int", "decimal", "string", "duration", "timestamp", "ago", "inSchedule", "abs", "min", "max", "round" };
                var suggestions = Suggestions.FindSuggestions(name, candidates);
                _diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    ExpressionErrors.UnknownFunction,
                    new Dictionary<string, object?> { ["name"] = name, ["suggestions"] = string.Join(", ", suggestions) },
                    start,
                    length));
                return ExprType.Unknown;
        }
    }

    private ExprType CheckHasCall(IReadOnlyList<AstNode> args, int start, int length)
    {
        if (args.Count != 1)
        {
            ReportArgCount("has", 1, start, length);
            return ExprType.Bool;
        }

        var arg = args[0];
        var path = BuildMemberPath(arg);
        if (path is not null && path.Count >= 2 && path[0] == "device")
        {
            if (path.Count == 3)
            {
                // device.<cap>.<prop>
                if (TryMakePropertyPath(path[1], path[2], out var pPath))
                {
                    _referencedProperties.Add(pPath);
                }
            }

            return ExprType.Bool;
        }

        return ExprType.Bool;
    }

    private void CheckRegexArg(AstNode arg)
    {
        if (arg is not LiteralNode { Kind: WpValueKind.String } lit)
        {
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                ExpressionErrors.FunctionArguments,
                new Dictionary<string, object?> { ["name"] = "matches", ["expected"] = "string literal pattern" },
                arg.Start,
                arg.Length));
            return;
        }

        var pattern = (string)lit.Value!;

        // Validate portable regex subset: no lookaround (?=, (?!, (?<=, (?<!), no backreferences \1..\9
        if (Regex.IsMatch(pattern, @"\(\?<?[=!]", RegexOptions.None))
        {
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                ExpressionErrors.UnsupportedRegex,
                new Dictionary<string, object?> { ["feature"] = "lookaround" },
                arg.Start,
                arg.Length));
            return;
        }

        if (Regex.IsMatch(pattern, @"\\[1-9]", RegexOptions.None))
        {
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                ExpressionErrors.UnsupportedRegex,
                new Dictionary<string, object?> { ["feature"] = "backreference" },
                arg.Start,
                arg.Length));
            return;
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(10));
        }
        catch (Exception ex)
        {
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                ExpressionErrors.UnsupportedRegex,
                new Dictionary<string, object?> { ["feature"] = ex.Message },
                arg.Start,
                arg.Length));
        }
    }

    private ExprType CheckUnary(UnaryNode unary)
    {
        var operandType = CheckNode(unary.Operand);

        if (unary.Operator == "!")
        {
            if (operandType.Kind != ExprTypeKind.Bool && operandType.Kind != ExprTypeKind.Unknown)
            {
                ReportTypeMismatch("!", "bool", operandType.Kind.ToString().ToLowerInvariant(), unary.Start, unary.Length);
            }

            return ExprType.Bool;
        }

        if (unary.Operator == "-")
        {
            if (operandType.Kind is not ExprTypeKind.Int and not ExprTypeKind.Decimal and not ExprTypeKind.Unknown)
            {
                ReportTypeMismatch("-", "number", operandType.Kind.ToString().ToLowerInvariant(), unary.Start, unary.Length);
            }

            return operandType;
        }

        return ExprType.Unknown;
    }

    private ExprType CheckBinary(BinaryNode binary)
    {
        var left = CheckNode(binary.Left);
        var right = CheckNode(binary.Right);

        switch (binary.Operator)
        {
            case "||":
            case "&&":
                if (left.Kind != ExprTypeKind.Bool && left.Kind != ExprTypeKind.Unknown)
                {
                    ReportTypeMismatch(binary.Operator, "bool", left.Kind.ToString().ToLowerInvariant(), binary.Left.Start, binary.Left.Length);
                }

                if (right.Kind != ExprTypeKind.Bool && right.Kind != ExprTypeKind.Unknown)
                {
                    ReportTypeMismatch(binary.Operator, "bool", right.Kind.ToString().ToLowerInvariant(), binary.Right.Start, binary.Right.Length);
                }

                return ExprType.Bool;

            case "==":
            case "!=":
                return CheckEquality(binary.Operator, left, right, binary);

            case "<":
            case "<=":
            case ">":
            case ">=":
                return CheckOrdering(binary.Operator, left, right, binary);

            case "in":
                return CheckIn(left, right, binary);

            case "+":
            case "-":
            case "*":
            case "/":
            case "%":
                return CheckArithmetic(binary.Operator, left, right, binary);

            default:
                return ExprType.Unknown;
        }
    }

    private ExprType CheckEquality(string op, ExprType left, ExprType right, BinaryNode binary)
    {
        // Comparisons with null are always allowed
        if (left.Kind == ExprTypeKind.Null || right.Kind == ExprTypeKind.Null)
        {
            return ExprType.Bool;
        }

        // Unknown operand: ignore check-time error
        if (left.Kind == ExprTypeKind.Unknown || right.Kind == ExprTypeKind.Unknown)
        {
            return ExprType.Bool;
        }

        // Check enum validation
        if (left.IsEnum && binary.Right is LiteralNode { Kind: WpValueKind.String } rightLit)
        {
            var code = (string)rightLit.Value!;
            if (left.EnumValues is not null && !left.EnumValues.Contains(code))
            {
                _diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    ExpressionErrors.UnknownEnumValue,
                    new Dictionary<string, object?>
                    {
                        ["value"] = code,
                        ["allowed"] = string.Join(", ", left.EnumValues),
                    },
                    binary.Right.Start,
                    binary.Right.Length));
            }
        }
        else if (right.IsEnum && binary.Left is LiteralNode { Kind: WpValueKind.String } leftLit)
        {
            var code = (string)leftLit.Value!;
            if (right.EnumValues is not null && !right.EnumValues.Contains(code))
            {
                _diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    ExpressionErrors.UnknownEnumValue,
                    new Dictionary<string, object?>
                    {
                        ["value"] = code,
                        ["allowed"] = string.Join(", ", right.EnumValues),
                    },
                    binary.Left.Start,
                    binary.Left.Length));
            }
        }

        // Int and decimal can be mixed
        if (left.IsNumericWith(right))
        {
            return ExprType.Bool;
        }

        // Different types -> error
        if (left.Kind != right.Kind)
        {
            ReportTypeMismatch(op, left.Kind.ToString().ToLowerInvariant(), right.Kind.ToString().ToLowerInvariant(), binary.Start, binary.Length);
        }

        return ExprType.Bool;
    }

    private ExprType CheckOrdering(string op, ExprType left, ExprType right, BinaryNode binary)
    {
        if (left.Kind == ExprTypeKind.Null || right.Kind == ExprTypeKind.Null)
        {
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                ExpressionErrors.NullComparisonWarning,
                new Dictionary<string, object?> { ["name"] = binary.Left is MemberAccessNode m ? m.Member : "value" },
                binary.Start,
                binary.Length));
            return ExprType.Bool;
        }

        if (left.IsNumericWith(right))
        {
            return ExprType.Bool;
        }

        if (left.Kind == ExprTypeKind.Timestamp && right.Kind == ExprTypeKind.Timestamp)
        {
            return ExprType.Bool;
        }

        if (left.Kind == ExprTypeKind.Duration && right.Kind == ExprTypeKind.Duration)
        {
            return ExprType.Bool;
        }

        if (left.Kind == ExprTypeKind.String && right.Kind == ExprTypeKind.String)
        {
            return ExprType.Bool;
        }

        if (left.Kind != ExprTypeKind.Unknown && right.Kind != ExprTypeKind.Unknown)
        {
            ReportTypeMismatch(op, left.Kind.ToString().ToLowerInvariant(), right.Kind.ToString().ToLowerInvariant(), binary.Start, binary.Length);
        }

        return ExprType.Bool;
    }

    private ExprType CheckIn(ExprType left, ExprType right, BinaryNode binary)
    {
        if (right.Kind != ExprTypeKind.List && right.Kind != ExprTypeKind.Unknown)
        {
            ReportTypeMismatch("in", "list", right.Kind.ToString().ToLowerInvariant(), binary.Right.Start, binary.Right.Length);
        }

        return ExprType.Bool;
    }

    private ExprType CheckArithmetic(string op, ExprType left, ExprType right, BinaryNode binary)
    {
        if (op == "+" && left.Kind == ExprTypeKind.String && right.Kind == ExprTypeKind.String)
        {
            return ExprType.String;
        }

        if (op == "+" && left.Kind == ExprTypeKind.Timestamp && right.Kind == ExprTypeKind.Duration)
        {
            return ExprType.Timestamp;
        }

        if (op == "-" && left.Kind == ExprTypeKind.Timestamp && right.Kind == ExprTypeKind.Duration)
        {
            return ExprType.Timestamp;
        }

        if (op == "-" && left.Kind == ExprTypeKind.Timestamp && right.Kind == ExprTypeKind.Timestamp)
        {
            return ExprType.Duration;
        }

        if (left.IsNumericWith(right))
        {
            if (left.Kind == ExprTypeKind.Decimal || right.Kind == ExprTypeKind.Decimal)
            {
                return ExprType.Decimal;
            }

            return ExprType.Int;
        }

        if (left.Kind != ExprTypeKind.Unknown && right.Kind != ExprTypeKind.Unknown)
        {
            ReportTypeMismatch(op, left.Kind.ToString().ToLowerInvariant(), right.Kind.ToString().ToLowerInvariant(), binary.Start, binary.Length);
        }

        return ExprType.Unknown;
    }

    private ExprType CheckConditional(ConditionalNode cond)
    {
        var condType = CheckNode(cond.Condition);
        if (condType.Kind != ExprTypeKind.Bool && condType.Kind != ExprTypeKind.Unknown)
        {
            ReportTypeMismatch("?:", "bool", condType.Kind.ToString().ToLowerInvariant(), cond.Condition.Start, cond.Condition.Length);
        }

        var trueType = CheckNode(cond.TrueBranch);
        var falseType = CheckNode(cond.FalseBranch);

        if (trueType.IsNumericWith(falseType))
        {
            return trueType.Kind == ExprTypeKind.Decimal || falseType.Kind == ExprTypeKind.Decimal ? ExprType.Decimal : ExprType.Int;
        }

        return trueType;
    }

    private ExprType CheckList(ListNode list)
    {
        ExprType itemType = ExprType.Unknown;
        foreach (var item in list.Items)
        {
            var t = CheckNode(item);
            if (itemType.Kind == ExprTypeKind.Unknown)
            {
                itemType = t;
            }
        }

        return ExprType.ListOf(itemType);
    }

    private void ReportArgCount(string name, int expected, int start, int length)
    {
        _diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error,
            ExpressionErrors.FunctionArguments,
            new Dictionary<string, object?> { ["name"] = name, ["expected"] = expected },
            start,
            length));
    }

    private void ReportTypeMismatch(string op, string expected, string actual, int start, int length)
    {
        _diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error,
            ExpressionErrors.TypeMismatch,
            new Dictionary<string, object?> { ["op"] = op, ["left"] = expected, ["right"] = actual },
            start,
            length));
    }
}
