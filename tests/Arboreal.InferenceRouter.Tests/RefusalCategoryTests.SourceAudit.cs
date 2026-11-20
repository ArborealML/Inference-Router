using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Arboreal.InferenceRouter.Tests;

public partial class RefusalCategoryTests
{
    private const string DirectExit = "return new(SwitchResult.Failed(SwitchReason.NoLifecycleOwner));";
    private const string RecordedExit = "? new ObservedSwitchResult(SwitchResult.FailedFromRecord(state.FailureReason)) : null;";
    private const string ProviderWrite = "state.FailureReason = SwitchReason.LaunchFailed;";
    private const string ReadinessWrite = "state.FailureReason = failure.Reason;";
    private const string ReadinessDefault = "_ => SwitchReason.HealthTimeout,";
    private const string RecordedDefault = "Failed(recorded is { Length: > 0 } why ? why : SwitchReason.SwitchError)";
    private const string FailedInitializer =
        """new SwitchResult(SwitchOutcome.Ready, null) { Outcome = SwitchOutcome.Failed, Detail = "free prose" }""";

    private static readonly ExpressionSyntax RecordedFallback =
        SyntaxFactory.ParseExpression(RecordedDefault);
    private static readonly ExpressionSyntax RecordedReason = SyntaxFactory.ParseExpression("state.FailureReason");
    private static readonly ExpressionSyntax MappedWrite = SyntaxFactory.ParseExpression("state.FailureReason = failure.Reason");
    private static readonly ExpressionSyntax FailedConstruction =
        SyntaxFactory.ParseExpression("new SwitchResult(SwitchOutcome.Failed, category)");

    private enum ViolationKind
    {
        ParseError, DirectCall, RecordedCall, RecordedDefault, RecordedWrite,
        CloneWrite, FailedConstructor, ReasonReference, ReadinessMapping,
    }

    private readonly record struct AuditViolation(ViolationKind Kind, int Line, string Expression);

    [Fact]
    public void SourceAuditCoversEveryFailedExit()
    {
        Assert.Empty(Audit(ReadEngineRuntime()));
        Assert.Equal(NamedMembers.Length, SwitchReason.All.Length);
        Assert.Equal(NamedMembers.Length, WireSet.Length);
    }

    [Theory]
    [InlineData(DirectExit, """return new(SwitchResult.Failed(SwitchReason.NoLifecycleOwner) with { Detail = "free prose" });""",
        nameof(ViolationKind.CloneWrite), "Detail = \"free prose\"")]
    [InlineData(DirectExit, "return new(SwitchResult.Failed(SwitchReason.NoLifecycleOwner) with { Outcome = SwitchOutcome.Ready });",
        nameof(ViolationKind.CloneWrite), "Outcome = SwitchOutcome.Ready")]
    [InlineData(DirectExit, """return new(canonical.Length == 0 ? SwitchResult.Failed(SwitchReason.NoLifecycleOwner) : SwitchResult.Failed("free prose"));""",
        nameof(ViolationKind.DirectCall), """SwitchResult.Failed("free prose")""")]
    [InlineData(DirectExit, """return new(SwitchResult.Failed("free prose")); // SwitchResult.Failed(SwitchReason.NoLifecycleOwner)""",
        nameof(ViolationKind.DirectCall), """SwitchResult.Failed("free prose")""")]
    [InlineData(DirectExit, "return new(SwitchResult.Failed(category: SwitchReason.NoLifecycleOwner));",
        nameof(ViolationKind.DirectCall), "SwitchResult.Failed(category: SwitchReason.NoLifecycleOwner)")]
    [InlineData(DirectExit, "return new(Failed(SwitchReason.NoLifecycleOwner));",
        nameof(ViolationKind.DirectCall), "Failed(SwitchReason.NoLifecycleOwner)")]
    [InlineData(RecordedExit, """? new ObservedSwitchResult(woke ? SwitchResult.FailedFromRecord(state.FailureReason) : SwitchResult.FailedFromRecord("free prose")) : null;""",
        nameof(ViolationKind.RecordedCall), """SwitchResult.FailedFromRecord("free prose")""")]
    [InlineData(RecordedExit, """? new ObservedSwitchResult(SwitchResult.FailedFromRecord("free prose")) : null; /* SwitchResult.FailedFromRecord(state.FailureReason) */""",
        nameof(ViolationKind.RecordedCall), """SwitchResult.FailedFromRecord("free prose")""")]
    [InlineData(ProviderWrite, "state.FailureReason += SwitchReason.LaunchFailed;",
        nameof(ViolationKind.RecordedWrite), "state.FailureReason += SwitchReason.LaunchFailed")]
    [InlineData(ProviderWrite, "state.FailureReason ??= SwitchReason.LaunchFailed;",
        nameof(ViolationKind.RecordedWrite), "state.FailureReason ??= SwitchReason.LaunchFailed")]
    [InlineData(ProviderWrite, """Interlocked.Exchange(ref state.FailureReason, SwitchReason.LaunchFailed + "!");""",
        nameof(ViolationKind.RecordedWrite), "ref state.FailureReason")]
    [InlineData(ProviderWrite, """_ = new Dictionary<int, string> { [0] = SwitchReason.LaunchFailed + "!" }.TryGetValue(0, out state.FailureReason);""",
        nameof(ViolationKind.RecordedWrite), "out state.FailureReason")]
    [InlineData(ProviderWrite, ReadinessWrite, nameof(ViolationKind.RecordedWrite), "state.FailureReason = failure.Reason")]
    [InlineData(ReadinessWrite, ReadinessWrite + " " + ReadinessWrite,
        nameof(ViolationKind.RecordedWrite), "state.FailureReason = failure.Reason")]
    [InlineData(DirectExit, """return new(new SwitchResult(SwitchOutcome.Failed, "free prose"));""",
        nameof(ViolationKind.FailedConstructor), """new SwitchResult(SwitchOutcome.Failed, "free prose")""")]
    [InlineData(DirectExit, """return new(new(SwitchOutcome.Failed, "free prose"));""",
        nameof(ViolationKind.FailedConstructor), """new(SwitchOutcome.Failed, "free prose")""")]
    [InlineData(DirectExit, "return new(canonical.Length == 0 ? " + FailedInitializer +
        " : SwitchResult.Failed(SwitchReason.NoLifecycleOwner));", nameof(ViolationKind.FailedConstructor), FailedInitializer)]
    [InlineData(DirectExit, "return new(SwitchResult.Failed(SwitchReason.Unknown));",
        nameof(ViolationKind.ReasonReference), "SwitchReason.Unknown")]
    [InlineData(RecordedDefault, "Failed(recorded is { Length: > 0 } why ? why : SwitchReason.HealthTimeout)",
        nameof(ViolationKind.RecordedDefault), "Failed(recorded is { Length: > 0 } why ? why : SwitchReason.HealthTimeout)")]
    [InlineData(ReadinessDefault, "_ => SwitchReason.SwitchError,",
        nameof(ViolationKind.ReadinessMapping), "_ => SwitchReason.SwitchError")]
    [InlineData(ReadinessDefault, "_ => SwitchReason.HealthTimeout, _ => SwitchReason.HealthTimeout,",
        nameof(ViolationKind.ReadinessMapping), "_ => SwitchReason.HealthTimeout")]
    [InlineData(ReadinessDefault, "ReadinessMiss.NeverHealthy => SwitchReason.SwitchError, _ => SwitchReason.HealthTimeout,",
        nameof(ViolationKind.ReadinessMapping), "ReadinessMiss.NeverHealthy => SwitchReason.SwitchError")]
    public void SourceAuditRejectsSpecificSyntaxValidMutations(string before, string after, string kind, string expression)
    {
        var (source, line) = MutateEngineRuntime(before, after);
        var violations = Audit(source);
        Assert.DoesNotContain(violations, violation => violation.Kind == ViolationKind.ParseError);
        var finding = Assert.Single(violations, violation =>
            violation.Kind.ToString() == kind && violation.Line == line);
        Assert.Equal(expression, finding.Expression);
    }

    [Theory]
    [InlineData(DirectExit, DirectExit + """ // SwitchReason.Unknown, SwitchResult.Failed("free prose"), FailureReason += "bad" """)]
    [InlineData(DirectExit, """{ _ = "SwitchOutcome.Failed SwitchReason.Unknown"; """ + DirectExit + " }")]
    [InlineData(DirectExit, "return new(canonical.Length == 0 ? SwitchResult.Failed(SwitchReason.NoLifecycleOwner) : " +
        "SwitchResult.Failed(SwitchReason.SwitchError));")]
    [InlineData(RecordedExit, "? new ObservedSwitchResult(woke ? SwitchResult.FailedFromRecord(state.FailureReason) : " +
        "SwitchResult.FailedFromRecord(state.FailureReason)) : null;")]
    [InlineData(RecordedDefault, "Failed( /* fallback */ recorded is { Length: > 0 } why ? why : SwitchReason.SwitchError )")]
    [InlineData(ReadinessWrite, "state.FailureReason  =  /* mapped category */ failure.Reason;")]
    public void SourceAuditAcceptsBenignSyntax(string before, string after)
    {
        var (source, _) = MutateEngineRuntime(before, after);
        Assert.Empty(Audit(source));
    }

    [Fact]
    public void SourceAuditReportsParseErrors()
    {
        var violation = Assert.Single(Audit("class Broken { int field = ; }"));
        Assert.Equal(ViolationKind.ParseError, violation.Kind);
        Assert.Equal(1, violation.Line);
    }

    private static (string Source, int Line) MutateEngineRuntime(string before, string after)
    {
        var source = ReadEngineRuntime();
        var start = source.IndexOf(before, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Mutation anchor was not found: {before}");
        Assert.Equal(start, source.LastIndexOf(before, StringComparison.Ordinal));
        var line = source.AsSpan(0, start).Count('\n') + 1;
        return (source[..start] + after + source[(start + before.Length)..], line);
    }

    // This audit recognizes syntax shapes in EngineRuntime.cs. It cannot resolve aliases,
    // method groups, shadowed variables or data flow. The caller-facing tests check actual exits.
    // The file-wide with-initializer policy also rejects Detail or Outcome on unrelated records.
    private static List<AuditViolation> Audit(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
        var violations = tree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => new AuditViolation(ViolationKind.ParseError,
                diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1, diagnostic.ToString()))
            .ToList();
        if (violations.Count != 0) return violations;

        var root = tree.GetRoot();
        var engine = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(type => type.Identifier.ValueText == "EngineRuntime");
        var switchResult = engine?.Members.OfType<RecordDeclarationSyntax>()
            .FirstOrDefault(type => type.Identifier.ValueText == "SwitchResult");
        var failed = switchResult?.Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(method => method.Identifier.ValueText == "Failed");
        var failedFromRecord = switchResult?.Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(method => method.Identifier.ValueText == "FailedFromRecord");
        var fallback = failedFromRecord?.ExpressionBody?.Expression;
        var commitReadiness = engine?.Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(method => method.Identifier.ValueText == "CommitLegacyReadiness");
        var reasonReferences = new HashSet<string>(StringComparer.Ordinal);
        var mappedWrites = 0;
        var failedConstructors = 0;

        void Reject(ViolationKind kind, SyntaxNode expression) => violations.Add(Violation(kind, expression));

        if (fallback is not InvocationExpressionSyntax)
            Reject(ViolationKind.RecordedDefault, fallback ?? (SyntaxNode?)failedFromRecord ?? root);
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax call when call.Span == fallback?.Span:
                    if (!SyntaxFactory.AreEquivalent(call, RecordedFallback, topLevel: false))
                        Reject(ViolationKind.RecordedDefault, call);
                    break;
                case InvocationExpressionSyntax call:
                    var violation = CheckFactoryCall(call);
                    if (violation is { } invalidCall) Reject(invalidCall, call);
                    break;
                case AssignmentExpressionSyntax assignment when
                    assignment.Parent is InitializerExpressionSyntax { Parent: WithExpressionSyntax } &&
                    SyntaxName(assignment.Left) is "Detail" or "Outcome":
                    Reject(ViolationKind.CloneWrite, assignment);
                    break;
                case ArgumentSyntax argument when
                    (argument.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword) &&
                    WritesRecordedReason(argument.Expression):
                    Reject(ViolationKind.RecordedWrite, argument);
                    break;
                case AssignmentExpressionSyntax assignment when WritesRecordedReason(assignment.Left):
                    if (SyntaxFactory.AreEquivalent(assignment, MappedWrite, topLevel: false) &&
                        assignment.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Span == commitReadiness?.Span &&
                        commitReadiness is not null)
                    {
                        if (++mappedWrites != 1) Reject(ViolationKind.RecordedWrite, assignment);
                    }
                    else if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
                        SyntaxName(assignment.Left) != "FailureReason" || !IsNamedReason(assignment.Right))
                    {
                        Reject(ViolationKind.RecordedWrite, assignment);
                    }
                    break;
                case BaseObjectCreationExpressionSyntax creation when ConstructsFailedResult(creation):
                    if (SyntaxFactory.AreEquivalent(creation, FailedConstruction, topLevel: false) &&
                        creation.Parent is ReturnStatementSyntax { Parent: BlockSyntax { Parent: MethodDeclarationSyntax method } } &&
                        method.Span == failed?.Span)
                    {
                        if (++failedConstructors != 1) Reject(ViolationKind.FailedConstructor, creation);
                    }
                    else
                    {
                        Reject(ViolationKind.FailedConstructor, creation);
                    }
                    break;
                case MemberAccessExpressionSyntax reference when MemberOf(reference, "SwitchReason") is { } name && name != "All":
                    if (NamedMembers.Contains(name, StringComparer.Ordinal)) reasonReferences.Add(name);
                    else Reject(ViolationKind.ReasonReference, reference);
                    break;
            }
        }

        if (mappedWrites == 0) Reject(ViolationKind.RecordedWrite, (SyntaxNode?)commitReadiness ?? root);
        if (failedConstructors == 0) Reject(ViolationKind.FailedConstructor, (SyntaxNode?)failed ?? root);
        foreach (var member in NamedMembers)
        {
            if (!reasonReferences.Contains(member))
                violations.Add(new AuditViolation(ViolationKind.ReasonReference, 1, $"Missing SwitchReason.{member} reference"));
        }
        CheckReadinessMapping(engine, root, violations);
        return violations;
    }

    private static ViolationKind? CheckFactoryCall(InvocationExpressionSyntax call)
    {
        var name = call.Expression switch
        {
            SimpleNameSyntax bare => bare.Identifier.ValueText,
            MemberAccessExpressionSyntax member when SyntaxName(member.Expression) == "SwitchResult" => member.Name.Identifier.ValueText,
            _ => null,
        };
        if (name is not ("Failed" or "FailedFromRecord")) return null;

        var argument = call.ArgumentList.Arguments is [{ NameColon: null, RefKindKeyword.RawKind: 0 } positional]
            ? positional.Expression : null;
        if (name == "Failed")
            return call.Expression is MemberAccessExpressionSyntax { Name: IdentifierNameSyntax } && IsNamedReason(argument)
                ? null : ViolationKind.DirectCall;
        return (call.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax { Name: IdentifierNameSyntax }) &&
            argument is not null && SyntaxFactory.AreEquivalent(argument, RecordedReason, topLevel: false)
            ? null : ViolationKind.RecordedCall;
    }

    private static bool ConstructsFailedResult(BaseObjectCreationExpressionSyntax creation)
    {
        if (creation is ObjectCreationExpressionSyntax explicitCreation && SyntaxName(explicitCreation.Type) != "SwitchResult")
            return false;
        return ContainsFailedOutcome(creation.ArgumentList) ||
            creation.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>()
                .Any(write => SyntaxName(write.Left) == "Outcome" && ContainsFailedOutcome(write.Right)) == true;
    }

    // Leave nested creations to their own visit, including when their type is implicit.
    private static bool ContainsFailedOutcome(SyntaxNode? expression) =>
        expression?.DescendantNodesAndSelf(node => node is not BaseObjectCreationExpressionSyntax)
            .OfType<MemberAccessExpressionSyntax>().Any(reference => MemberOf(reference, "SwitchOutcome") == "Failed") == true;

    private static bool WritesRecordedReason(ExpressionSyntax left) => left switch
    {
        TupleExpressionSyntax tuple => tuple.Arguments.Any(argument => WritesRecordedReason(argument.Expression)),
        ParenthesizedExpressionSyntax parentheses => WritesRecordedReason(parentheses.Expression),
        _ => SyntaxName(left) == "FailureReason",
    };

    private static void CheckReadinessMapping(ClassDeclarationSyntax? engine, SyntaxNode root, List<AuditViolation> violations)
    {
        var reason = engine?.Members.OfType<RecordDeclarationSyntax>()
            .FirstOrDefault(type => type.Identifier.ValueText == "ReadinessFailure")?.Members.OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(property => property.Identifier.ValueText == "Reason");
        var missKinds = engine?.Members.OfType<EnumDeclarationSyntax>()
            .FirstOrDefault(type => type.Identifier.ValueText == "ReadinessMiss");
        if (reason?.ExpressionBody?.Expression is not SwitchExpressionSyntax
            { GoverningExpression: IdentifierNameSyntax { Identifier.ValueText: "Miss" } } mapping ||
            missKinds is null)
        {
            violations.Add(Violation(ViolationKind.ReadinessMapping, (SyntaxNode?)reason ?? root));
            return;
        }

        // The behavior tests pin which category each named miss produces.
        var defaults = 0;
        foreach (var arm in mapping.Arms)
        {
            if (arm.Pattern is DiscardPatternSyntax) defaults++;
            var validPattern = arm.Pattern switch
            {
                DiscardPatternSyntax => defaults == 1 && MemberOf(arm.Expression, "SwitchReason") == "HealthTimeout",
                ConstantPatternSyntax constant => MemberOf(constant.Expression, "ReadinessMiss") is { } miss &&
                    miss != "NeverHealthy" && missKinds.Members.Any(member => member.Identifier.ValueText == miss),
                _ => false,
            };
            if (arm.WhenClause is not null || !validPattern || !IsNamedReason(arm.Expression))
                violations.Add(Violation(ViolationKind.ReadinessMapping, arm));
        }
        if (defaults == 0) violations.Add(Violation(ViolationKind.ReadinessMapping, mapping));
    }

    private static bool IsNamedReason(ExpressionSyntax? expression) =>
        MemberOf(expression, "SwitchReason") is { } member && NamedMembers.Contains(member, StringComparer.Ordinal);

    private static string? MemberOf(ExpressionSyntax? expression, string owner) =>
        expression is MemberAccessExpressionSyntax { Name: IdentifierNameSyntax name } member &&
        SyntaxName(member.Expression) == owner ? name.Identifier.ValueText : null;

    private static string? SyntaxName(SyntaxNode? node) => node switch
    {
        SimpleNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        ConditionalAccessExpressionSyntax conditional => SyntaxName(conditional.WhenNotNull),
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        _ => null,
    };

    private static AuditViolation Violation(ViolationKind kind, SyntaxNode expression) =>
        new(kind, expression.GetLocation().GetLineSpan().StartLinePosition.Line + 1, expression.ToString());

    private static string ReadEngineRuntime()
    {
        var path = RepoFile("src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs");
        Assert.NotNull(path);
        return File.ReadAllText(path);
    }
}
