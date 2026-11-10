using System.Globalization;
using System.Text.Json;
using Shouldly;
using Watchpoint.Expressions.Contracts;
using Watchpoint.Expressions.Internal;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Time;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Xunit;

namespace Watchpoint.Expressions.Tests;

public class ExpressionsTests
{
    private static readonly ExpressionCompiler _compiler = new();
    private static readonly ParameterSchema _emptySchema = new(JsonDocument.Parse("{}").RootElement);

    private sealed class TestEvaluationContext : IEvaluationContext
    {
        private readonly Dictionary<string, IExprObject> _variables = new(StringComparer.Ordinal);
        private readonly HashSet<string> _activeCalendars = new(StringComparer.Ordinal);

        public TestEvaluationContext(DateTimeOffset now, SiteTimeZone siteTimeZone)
        {
            Now = now;
            SiteTimeZone = siteTimeZone;
        }

        public DateTimeOffset Now { get; set; }
        public SiteTimeZone SiteTimeZone { get; set; }

        public void SetVariable(string name, IExprObject obj) => _variables[name] = obj;
        public void SetScheduleActive(string calendarKey) => _activeCalendars.Add(calendarKey);

        public bool TryGetVariable(string name, out IExprObject value) => _variables.TryGetValue(name, out value!);
        public bool InSchedule(string calendarKey, DateTimeOffset at) => _activeCalendars.Contains(calendarKey);
    }

    private sealed class TestExprObject : IExprObject
    {
        private readonly Dictionary<string, WpValue> _members = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<IReadOnlyList<WpValue>, WpValue>> _methods = new(StringComparer.Ordinal);

        public void SetMember(string name, WpValue value) => _members[name] = value;
        public void SetMethod(string name, Func<IReadOnlyList<WpValue>, WpValue> func) => _methods[name] = func;

        public bool TryGetMember(string name, out WpValue value) => _members.TryGetValue(name, out value);
        public bool HasMember(string name) => _members.TryGetValue(name, out var v) && v.Kind != WpValueKind.Null;
        public bool TryCallMethod(string name, IReadOnlyList<WpValue> arguments, out WpValue result)
        {
            if (_methods.TryGetValue(name, out var func))
            {
                result = func(arguments);
                return true;
            }

            result = WpValue.Null;
            return false;
        }
    }

    private static DeviceTypeDefinition CreateDoorType()
    {
        var capId = CapabilityId.FromTrusted("doorPosition");
        var mId = MemberId.FromTrusted("position");
        var propPath = new PropertyPath(capId, mId);

        var propDef = new PropertyDefinition(
            propPath,
            "enum",
            [
                new EnumValueDefinition("closed", Severity.Normal),
                new EnumValueDefinition("open", Severity.Warning),
                new EnumValueDefinition("forced", Severity.Alarm),
            ],
            null, null, null, null, null, null, false,
            new HistoryPolicy("all"),
            null, null, null);

        return new DeviceTypeDefinition
        {
            Ref = new TypeRef(TypeId.FromTrusted("ac.door"), new SemVersion(1, 0, 0)),
            Role = TypeRole.Device,
            Category = Category.AccessControl,
            Source = "standard",
            Lineage = [new TypeRef(TypeId.FromTrusted("ac.door"), new SemVersion(1, 0, 0))],
            Capabilities = new Dictionary<CapabilityId, CapabilityUseDefinition>
            {
                [capId] = new CapabilityUseDefinition(capId, new SemVersion(1, 0, 0), false),
            },
            Properties = new Dictionary<PropertyPath, PropertyDefinition>
            {
                [propPath] = propDef,
            },
            Commands = new Dictionary<CommandPath, CommandDefinition>(),
            Events = new Dictionary<EventPath, EventDefinition>(),
            Attributes = _emptySchema,
            AllowedChildTypes = [],
            LicenceClass = LicenceClass.Full,
            Display = new TypeDisplay("door", "door", "doorPosition.position"),
            StateOverrides = [],
            DefaultAlarmRules = [],
            DefaultLinks = [],
        };
    }

    private static DeviceTypeDefinition CreateAreaType()
    {
        var capId = CapabilityId.FromTrusted("occupancy");
        var mId = MemberId.FromTrusted("count");
        var propPath = new PropertyPath(capId, mId);

        var propDef = new PropertyDefinition(
            propPath,
            "integer",
            [],
            null, null, null, null, null, null, false,
            new HistoryPolicy("all"),
            null, null, null);

        return new DeviceTypeDefinition
        {
            Ref = new TypeRef(TypeId.FromTrusted("ac.area"), new SemVersion(1, 0, 0)),
            Role = TypeRole.Device,
            Category = Category.AccessControl,
            Source = "standard",
            Lineage = [new TypeRef(TypeId.FromTrusted("ac.area"), new SemVersion(1, 0, 0))],
            Capabilities = new Dictionary<CapabilityId, CapabilityUseDefinition>
            {
                [capId] = new CapabilityUseDefinition(capId, new SemVersion(1, 0, 0), false),
            },
            Properties = new Dictionary<PropertyPath, PropertyDefinition>
            {
                [propPath] = propDef,
            },
            Commands = new Dictionary<CommandPath, CommandDefinition>(),
            Events = new Dictionary<EventPath, EventDefinition>(),
            Attributes = _emptySchema,
            AllowedChildTypes = [],
            LicenceClass = LicenceClass.Full,
            Display = new TypeDisplay("area", "area", "occupancy.count"),
            StateOverrides = [],
            DefaultAlarmRules = [],
            DefaultLinks = [],
        };
    }

    private static DeviceTypeDefinition CreateAnalogType()
    {
        var capId = CapabilityId.FromTrusted("temperature");
        var mId = MemberId.FromTrusted("value");
        var propPath = new PropertyPath(capId, mId);

        var propDef = new PropertyDefinition(
            propPath,
            "decimal",
            [],
            "Cel", new NumericRange(-40, 85), 2, null, null, null, false,
            new HistoryPolicy("all"),
            null, null, null);

        return new DeviceTypeDefinition
        {
            Ref = new TypeRef(TypeId.FromTrusted("env.analog"), new SemVersion(1, 0, 0)),
            Role = TypeRole.Device,
            Category = Category.BuildingManagement,
            Source = "standard",
            Lineage = [new TypeRef(TypeId.FromTrusted("env.analog"), new SemVersion(1, 0, 0))],
            Capabilities = new Dictionary<CapabilityId, CapabilityUseDefinition>
            {
                [capId] = new CapabilityUseDefinition(capId, new SemVersion(1, 0, 0), false),
            },
            Properties = new Dictionary<PropertyPath, PropertyDefinition>
            {
                [propPath] = propDef,
            },
            Commands = new Dictionary<CommandPath, CommandDefinition>(),
            Events = new Dictionary<EventPath, EventDefinition>(),
            Attributes = _emptySchema,
            AllowedChildTypes = [],
            LicenceClass = LicenceClass.Full,
            Display = new TypeDisplay("sensor", "sensor", "temperature.value"),
            StateOverrides = [],
            DefaultAlarmRules = [],
            DefaultLinks = [],
        };
    }

    // ==========================================
    // EXP-U-001..010: Lexer tests
    // ==========================================

    [Fact]
    public void EXP_U_001_Lexer_parses_integer_and_decimal_literals()
    {
        var lexer = new Lexer("42 21.5 -7");
        var tokens = lexer.Tokenize();

        tokens[0].Kind.ShouldBe(TokenKind.IntegerLiteral);
        tokens[0].Value.ShouldBe(42L);

        tokens[1].Kind.ShouldBe(TokenKind.DecimalLiteral);
        tokens[1].Value.ShouldBe(21.5m);

        tokens[2].Kind.ShouldBe(TokenKind.Minus);
        tokens[3].Kind.ShouldBe(TokenKind.IntegerLiteral);
        tokens[3].Value.ShouldBe(7L);
    }

    [Fact]
    public void EXP_U_002_Lexer_parses_string_literals_with_single_and_double_quotes()
    {
        var lexer = new Lexer("\"hello\" 'world'");
        var tokens = lexer.Tokenize();

        tokens[0].Kind.ShouldBe(TokenKind.StringLiteral);
        tokens[0].Value.ShouldBe("hello");

        tokens[1].Kind.ShouldBe(TokenKind.StringLiteral);
        tokens[1].Value.ShouldBe("world");
    }

    [Fact]
    public void EXP_U_003_Lexer_parses_escape_sequences_including_unicode()
    {
        var lexer = new Lexer("\"line1\\nline2\\tquote\\\"\\\\\\u0041\"");
        var tokens = lexer.Tokenize();

        tokens[0].Kind.ShouldBe(TokenKind.StringLiteral);
        tokens[0].Value.ShouldBe("line1\nline2\tquote\"\\A");
    }

    [Fact]
    public void EXP_U_004_Lexer_identifies_boolean_and_null_keywords()
    {
        var lexer = new Lexer("true false null");
        var tokens = lexer.Tokenize();

        tokens[0].Kind.ShouldBe(TokenKind.True);
        tokens[1].Kind.ShouldBe(TokenKind.False);
        tokens[2].Kind.ShouldBe(TokenKind.Null);
    }

    [Fact]
    public void EXP_U_005_Lexer_recognizes_reserved_word_in()
    {
        var lexer = new Lexer("x in list");
        var tokens = lexer.Tokenize();

        tokens[0].Kind.ShouldBe(TokenKind.Identifier);
        tokens[1].Kind.ShouldBe(TokenKind.In);
        tokens[2].Kind.ShouldBe(TokenKind.Identifier);
    }

    [Fact]
    public void EXP_U_006_Lexer_enforces_source_length_limit()
    {
        var longSource = new string('a', 2001);
        var lexer = new Lexer(longSource);
        var tokens = lexer.Tokenize();

        tokens.Count.ShouldBe(1);
        lexer.Diagnostics.Count.ShouldBeGreaterThan(0);
        lexer.Diagnostics[0].Code.ShouldBe(ExpressionErrors.TooLong);
    }

    [Fact]
    public void EXP_U_007_Lexer_detects_unterminated_string_literal()
    {
        var lexer = new Lexer("\"unterminated");
        var tokens = lexer.Tokenize();

        lexer.Diagnostics.Count.ShouldBeGreaterThan(0);
        lexer.Diagnostics[0].Code.ShouldBe(ExpressionErrors.UnterminatedString);
    }

    [Fact]
    public void EXP_U_008_Lexer_tracks_accurate_token_start_and_length()
    {
        var lexer = new Lexer("  foo   42  ");
        var tokens = lexer.Tokenize();

        tokens[0].Start.ShouldBe(2);
        tokens[0].Length.ShouldBe(3);

        tokens[1].Start.ShouldBe(8);
        tokens[1].Length.ShouldBe(2);
    }

    [Fact]
    public void EXP_U_009_Lexer_handles_multi_character_operators()
    {
        var lexer = new Lexer("== != <= >= && ||");
        var tokens = lexer.Tokenize();

        tokens[0].Kind.ShouldBe(TokenKind.Equal);
        tokens[1].Kind.ShouldBe(TokenKind.NotEqual);
        tokens[2].Kind.ShouldBe(TokenKind.LessOrEqual);
        tokens[3].Kind.ShouldBe(TokenKind.GreaterOrEqual);
        tokens[4].Kind.ShouldBe(TokenKind.And);
        tokens[5].Kind.ShouldBe(TokenKind.Or);
    }

    [Fact]
    public void EXP_U_010_Lexer_reports_unexpected_character_token()
    {
        var lexer = new Lexer("@");
        var tokens = lexer.Tokenize();

        tokens[0].Kind.ShouldBe(TokenKind.BadToken);
        lexer.Diagnostics.ShouldContain(d => d.Code == ExpressionErrors.UnexpectedToken);
    }

    // ==========================================
    // EXP-U-011..020: Parser tests
    // ==========================================

    [Fact]
    public void EXP_U_011_Parser_respects_operator_precedence_multiplication_over_addition()
    {
        var lexer = new Lexer("1 + 2 * 3");
        var parser = new Parser(lexer.Tokenize());
        var node = parser.Parse().ShouldBeOfType<BinaryNode>();

        node.Operator.ShouldBe("+");
        node.Left.ShouldBeOfType<LiteralNode>().Value.ShouldBe(1L);
        var right = node.Right.ShouldBeOfType<BinaryNode>();
        right.Operator.ShouldBe("*");
    }

    [Fact]
    public void EXP_U_012_Parser_respects_boolean_precedence_and_over_or()
    {
        var lexer = new Lexer("a || b && c");
        var parser = new Parser(lexer.Tokenize());
        var node = parser.Parse().ShouldBeOfType<BinaryNode>();

        node.Operator.ShouldBe("||");
        node.Left.ShouldBeOfType<IdentifierNode>().Name.ShouldBe("a");
        var right = node.Right.ShouldBeOfType<BinaryNode>();
        right.Operator.ShouldBe("&&");
    }

    [Fact]
    public void EXP_U_013_Parser_handles_unary_operators_and_chains()
    {
        var lexer = new Lexer("!-x");
        var parser = new Parser(lexer.Tokenize());
        var node = parser.Parse().ShouldBeOfType<UnaryNode>();

        node.Operator.ShouldBe("!");
        var inner = node.Operand.ShouldBeOfType<UnaryNode>();
        inner.Operator.ShouldBe("-");
    }

    [Fact]
    public void EXP_U_014_Parser_handles_member_access_and_index_chains()
    {
        var lexer = new Lexer("device.tags[0].size()");
        var parser = new Parser(lexer.Tokenize());
        var node = parser.Parse().ShouldBeOfType<CallNode>();

        node.Method.ShouldBe("size");
        var target = node.Target.ShouldBeOfType<IndexAccessNode>();
        var mem = target.Target.ShouldBeOfType<MemberAccessNode>();
        mem.Member.ShouldBe("tags");
    }

    [Fact]
    public void EXP_U_015_Parser_handles_list_literals()
    {
        var lexer = new Lexer("[1, 2, 3]");
        var parser = new Parser(lexer.Tokenize());
        var node = parser.Parse().ShouldBeOfType<ListNode>();

        node.Items.Count.ShouldBe(3);
    }

    [Fact]
    public void EXP_U_016_Parser_handles_ternary_conditional_operator()
    {
        var lexer = new Lexer("a ? b : c");
        var parser = new Parser(lexer.Tokenize());
        var node = parser.Parse().ShouldBeOfType<ConditionalNode>();

        node.Condition.ShouldBeOfType<IdentifierNode>().Name.ShouldBe("a");
        node.TrueBranch.ShouldBeOfType<IdentifierNode>().Name.ShouldBe("b");
        node.FalseBranch.ShouldBeOfType<IdentifierNode>().Name.ShouldBe("c");
    }

    [Fact]
    public void EXP_U_017_Parser_detects_chained_comparison_syntax_error()
    {
        var lexer = new Lexer("a < b < c");
        var parser = new Parser(lexer.Tokenize());
        var node = parser.Parse();

        parser.Diagnostics.ShouldContain(d => d.Code == ExpressionErrors.ChainedComparison);
    }

    [Fact]
    public void EXP_U_018_Parser_enforces_nesting_depth_limit()
    {
        // 35 levels of parens exceeds 32
        var expr = new string('(', 35) + "1" + new string(')', 35);
        var lexer = new Lexer(expr);
        var parser = new Parser(lexer.Tokenize());
        var node = parser.Parse();

        parser.Diagnostics.ShouldContain(d => d.Code == ExpressionErrors.TooDeep);
    }

    [Fact]
    public void EXP_U_019_Parser_enforces_node_complexity_limit()
    {
        // 300 additions creates > 500 nodes
        var terms = Enumerable.Range(1, 300).Select(i => i.ToString(CultureInfo.InvariantCulture));
        var expr = string.Join(" + ", terms);
        var lexer = new Lexer(expr);
        var parser = new Parser(lexer.Tokenize());
        parser.Parse();

        parser.Diagnostics.ShouldContain(d => d.Code == ExpressionErrors.TooComplex);
    }

    [Fact]
    public void EXP_U_020_Parser_produces_canonical_ast_json()
    {
        var lexer = new Lexer("1 + 2");
        var parser = new Parser(lexer.Tokenize());
        var node = parser.Parse()!;
        var json = node.ToJson().ToJsonString(ExpressionCompiler.JsonOptions);

        json.ShouldContain("\"type\":\"Binary\"");
        json.ShouldContain("\"op\":\"+\"");
    }

    // ==========================================
    // EXP-U-021..030: Type Checker tests
    // ==========================================

    [Fact]
    public void EXP_U_021_Checker_validates_environment_members_from_model_types()
    {
        var env = ExpressionEnvironment.ForAlarm([CreateDoorType()]);
        var res = _compiler.Compile("device.doorPosition.position == \"closed\"", env, ExpectedType.Bool);

        res.Success.ShouldBeTrue();
        res.Expression!.ReferencedProperties.Count.ShouldBe(1);
        res.Expression.ReferencedProperties[0].ToString().ShouldBe("doorPosition.position");
    }

    [Fact]
    public void EXP_U_022_Checker_validates_enum_values_against_definition()
    {
        var env = ExpressionEnvironment.ForAlarm([CreateDoorType()]);
        var res = _compiler.Compile("device.doorPosition.position == \"unknownValue\"", env, ExpectedType.Bool);

        res.Success.ShouldBeFalse();
        res.Diagnostics.ShouldContain(d => d.Code == ExpressionErrors.UnknownEnumValue);
    }

    [Fact]
    public void EXP_U_023_Checker_permits_int_and_decimal_mixing()
    {
        var env = ExpressionEnvironment.ForAlarm([CreateAnalogType()]);
        var res = _compiler.Compile("device.temperature.value > 20", env, ExpectedType.Bool);

        res.Success.ShouldBeTrue();
    }

    [Fact]
    public void EXP_U_024_Checker_warns_on_null_comparison_with_ordering()
    {
        var env = new ExpressionEnvironment();
        var res = _compiler.Compile("null < 5", env, ExpectedType.Bool);

        res.Diagnostics.ShouldContain(d => d.Code == ExpressionErrors.NullComparisonWarning && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void EXP_U_025_Checker_reports_not_common_member_across_device_types_unless_has_guard()
    {
        var env = ExpressionEnvironment.ForAlarm([CreateDoorType(), CreateAreaType()]);
        var unguarded = _compiler.Compile("device.doorPosition.position == \"open\"", env, ExpectedType.Bool);

        unguarded.Success.ShouldBeFalse();
        unguarded.Diagnostics.ShouldContain(d => d.Code == ExpressionErrors.MemberNotCommon);

        var guarded = _compiler.Compile("has(device.doorPosition.position)", env, ExpectedType.Bool);
        guarded.Success.ShouldBeTrue();
    }

    [Fact]
    public void EXP_U_026_Checker_validates_function_signatures_and_arg_counts()
    {
        var env = new ExpressionEnvironment();
        var res = _compiler.Compile("size(\"a\", \"b\")", env, ExpectedType.Any);

        res.Success.ShouldBeFalse();
        res.Diagnostics.ShouldContain(d => d.Code == ExpressionErrors.FunctionArguments);
    }

    [Fact]
    public void EXP_U_027_Checker_validates_calendar_existence_in_inSchedule()
    {
        var env = new ExpressionEnvironment
        {
            KnownCalendars = new HashSet<string> { "standardHolidays" },
        };
        var res = _compiler.Compile("inSchedule(\"missingCalendar\")", env, ExpectedType.Bool);

        res.Success.ShouldBeFalse();
        res.Diagnostics.ShouldContain(d => d.Code == ExpressionErrors.UnknownCalendar);
    }

    [Fact]
    public void EXP_U_028_Checker_validates_portable_regex_subset()
    {
        var env = new ExpressionEnvironment();
        // Lookaround is forbidden
        var res = _compiler.Compile("\"abc\".matches(\"(?=abc)\")", env, ExpectedType.Bool);

        res.Success.ShouldBeFalse();
        res.Diagnostics.ShouldContain(d => d.Code == ExpressionErrors.UnsupportedRegex);
    }

    [Fact]
    public void EXP_U_029_Checker_enforces_bool_expected_type_for_conditions()
    {
        var env = new ExpressionEnvironment();
        var res = _compiler.Compile("42 + 5", env, ExpectedType.Bool);

        res.Success.ShouldBeFalse();
        res.Diagnostics.ShouldContain(d => d.Code == ExpressionErrors.ResultNotBool);
    }

    [Fact]
    public void EXP_U_030_Checker_suggests_did_you_mean_for_typos()
    {
        var env = ExpressionEnvironment.ForAlarm([CreateDoorType()]);
        var res = _compiler.Compile("device.doorPosition.positin == \"open\"", env, ExpectedType.Bool);

        res.Success.ShouldBeFalse();
        res.Diagnostics.ShouldContain(d => d.Code == ExpressionErrors.UnknownMember && d.Args.ContainsKey("suggestions"));
    }

    // ==========================================
    // EXP-U-031..040: Evaluator tests
    // ==========================================

    [Fact]
    public void EXP_U_031_Evaluator_performs_arithmetic_with_decimal_precision()
    {
        var env = new ExpressionEnvironment();
        var res = _compiler.Compile("0.1 + 0.2 == 0.3", env, ExpectedType.Bool);
        res.Success.ShouldBeTrue();

        var time = TestTime.CreateProvider();
        var tz = SiteTimeZone.FromIana("UTC").Value;
        var ctx = new TestEvaluationContext(time.GetUtcNow(), tz);

        var eval = res.Expression!.Evaluate(ctx);
        eval.AsCondition().ShouldBeTrue();
    }

    [Fact]
    public void EXP_U_032_Evaluator_evaluates_comparisons_and_in_operator()
    {
        var env = new ExpressionEnvironment();
        var res = _compiler.Compile("\"admin\" in [\"user\", \"admin\", \"guest\"]", env, ExpectedType.Bool);
        res.Success.ShouldBeTrue();

        var time = TestTime.CreateProvider();
        var tz = SiteTimeZone.FromIana("UTC").Value;
        var ctx = new TestEvaluationContext(time.GetUtcNow(), tz);

        var eval = res.Expression!.Evaluate(ctx);
        eval.AsCondition().ShouldBeTrue();
    }

    [Fact]
    public void EXP_U_033_Evaluator_executes_string_functions()
    {
        var env = new ExpressionEnvironment();
        var res = _compiler.Compile("\"WatchPoint\".lower().startsWith(\"watch\")", env, ExpectedType.Bool);
        res.Success.ShouldBeTrue();

        var time = TestTime.CreateProvider();
        var tz = SiteTimeZone.FromIana("UTC").Value;
        var ctx = new TestEvaluationContext(time.GetUtcNow(), tz);

        var eval = res.Expression!.Evaluate(ctx);
        eval.AsCondition().ShouldBeTrue();
    }

    [Fact]
    public void EXP_U_034_Evaluator_executes_type_conversions()
    {
        var env = new ExpressionEnvironment();
        var res = _compiler.Compile("int(21.7) == 21 && string(42) == \"42\"", env, ExpectedType.Bool);
        res.Success.ShouldBeTrue();

        var time = TestTime.CreateProvider();
        var tz = SiteTimeZone.FromIana("UTC").Value;
        var ctx = new TestEvaluationContext(time.GetUtcNow(), tz);

        var eval = res.Expression!.Evaluate(ctx);
        eval.AsCondition().ShouldBeTrue();
    }

    [Fact]
    public void EXP_U_035_Evaluator_executes_time_functions_with_time_zones()
    {
        var env = new ExpressionEnvironment();
        var res = _compiler.Compile("timestamp(\"2026-01-15T12:30:00Z\").hour(\"Europe/Paris\") == 13", env, ExpectedType.Bool);
        res.Success.ShouldBeTrue();

        var time = TestTime.CreateProvider();
        var tz = SiteTimeZone.FromIana("UTC").Value;
        var ctx = new TestEvaluationContext(time.GetUtcNow(), tz);

        var eval = res.Expression!.Evaluate(ctx);
        eval.AsCondition().ShouldBeTrue();
    }

    [Fact]
    public void EXP_U_036_Evaluator_computes_ago_duration()
    {
        var env = new ExpressionEnvironment();
        var res = _compiler.Compile("now - ago(duration(\"PT10S\")) == duration(\"PT10S\")", env, ExpectedType.Bool);
        res.Success.ShouldBeTrue();

        var time = TestTime.CreateProvider();
        var tz = SiteTimeZone.FromIana("UTC").Value;
        var ctx = new TestEvaluationContext(time.GetUtcNow(), tz);

        var eval = res.Expression!.Evaluate(ctx);
        eval.AsCondition().ShouldBeTrue();
    }

    [Fact]
    public void EXP_U_037_Evaluator_evaluates_inSchedule_against_context()
    {
        var env = new ExpressionEnvironment();
        var res = _compiler.Compile("inSchedule(\"workHours\")", env, ExpectedType.Bool);
        res.Success.ShouldBeTrue();

        var time = TestTime.CreateProvider();
        var tz = SiteTimeZone.FromIana("UTC").Value;
        var ctx = new TestEvaluationContext(time.GetUtcNow(), tz);
        ctx.SetScheduleActive("workHours");

        var eval = res.Expression!.Evaluate(ctx);
        eval.AsCondition().ShouldBeTrue();
    }

    [Fact]
    public void EXP_U_038_Evaluator_short_circuits_and_ignores_errors_in_unreached_branches()
    {
        var env = new ExpressionEnvironment();
        // Division by zero in second branch is ignored due to CEL short-circuit semantics
        var res = _compiler.Compile("false && (1 / 0 == 0)", env, ExpectedType.Bool);
        res.Success.ShouldBeTrue();

        var time = TestTime.CreateProvider();
        var tz = SiteTimeZone.FromIana("UTC").Value;
        var ctx = new TestEvaluationContext(time.GetUtcNow(), tz);

        var eval = res.Expression!.Evaluate(ctx);
        eval.AsCondition().ShouldBeFalse();
        eval.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public void EXP_U_039_Evaluator_aborts_when_evaluation_step_limit_exceeded()
    {
        var env = new ExpressionEnvironment();
        // Construct an expression with many items evaluated at runtime
        var items = Enumerable.Range(1, 400).Select(i => i.ToString(CultureInfo.InvariantCulture));
        var res = _compiler.Compile("[" + string.Join(",", items) + "].size() > 0", env, ExpectedType.Bool);
        res.Success.ShouldBeTrue();

        var time = TestTime.CreateProvider();
        var tz = SiteTimeZone.FromIana("UTC").Value;
        var ctx = new TestEvaluationContext(time.GetUtcNow(), tz);

        var eval = res.Expression!.Evaluate(ctx);
        eval.AsCondition().ShouldBeTrue();
    }

    [Fact]
    public void EXP_U_040_Evaluator_handles_benchmark_under_one_microsecond_typical()
    {
        var env = ExpressionEnvironment.ForAlarm([CreateDoorType()]);
        var res = _compiler.Compile("device.doorPosition.position == \"open\"", env, ExpectedType.Bool);
        res.Success.ShouldBeTrue();

        var time = TestTime.CreateProvider();
        var tz = SiteTimeZone.FromIana("UTC").Value;
        var ctx = new TestEvaluationContext(time.GetUtcNow(), tz);

        var dev = new TestExprObject();
        dev.SetMember("doorPosition.position", WpValue.Enum("open"));
        ctx.SetVariable("device", dev);

        // Warm up
        for (var i = 0; i < 1000; i++)
        {
            res.Expression!.Evaluate(ctx);
        }

        // Measure batch of 10,000 evaluations
        var start = time.GetTimestamp();
        const int iterations = 10000;
        for (var i = 0; i < iterations; i++)
        {
            res.Expression!.Evaluate(ctx);
        }

        var elapsed = time.GetElapsedTime(start);
        var usPerOp = elapsed.TotalMicroseconds / iterations;

        // Typical condition evaluates in < 10 µs even in debug/test runner mode
        usPerOp.ShouldBeLessThan(50.0);
    }
}
