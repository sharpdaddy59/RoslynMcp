using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

/// <summary>One parameter to add in a change_signature call.</summary>
public sealed class AddParameterSpec
{
    [JsonPropertyName("name")]
    [Description("Parameter name (a valid C# identifier)")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    [Description("Parameter type as C# source text, e.g. 'bool', 'string?', 'IReadOnlyList<int>'")]
    public string Type { get; set; } = "";

    [JsonPropertyName("defaultValue")]
    [Description("Optional default value expression (C# source), making the parameter optional. If given without callSiteValue, call sites are left unchanged.")]
    public string? DefaultValue { get; set; }

    [JsonPropertyName("callSiteValue")]
    [Description("Optional argument expression (C# source) inserted at EVERY call site. Required unless defaultValue is given.")]
    public string? CallSiteValue { get; set; }

    [JsonPropertyName("index")]
    [Description("Optional 0-based position in the parameter list after removals. Default: before the first optional/params parameter when the new parameter is required, otherwise before any params parameter (i.e. at the end).")]
    public int? Index { get; set; }
}

[McpServerToolType]
public static class SignatureTools
{
    private const int MaxChangedDocuments = 100;

    [McpServerTool]
    [Description("Solution-wide signature change for a method or constructor: add, remove, and/or reorder parameters across the WHOLE override/implementation group (the definition, every override, every interface implementation, and the interface member itself) and rewrite every call site (positional and named arguments, base.Method(...) calls, cross-project). Operations apply in the order remove -> add -> reorder. Unrelated overloads with the same name are never touched. Usages that cannot be rewritten safely (method groups / delegate conversions, collection-initializer Add calls, attributes) make the tool refuse and list the locations; nothing is written. Compiles the result first and only writes to disk if no new compile errors appear - a removed parameter that is still read in a body comes back as a CS0103 conflict, not a broken build.")]
    public static Task<string> ChangeSignature(
        [Description("Absolute path of the file containing the method (use with line+column)")] string? filePath = null,
        [Description("1-based line number of the method name (use with filePath)")] int? line = null,
        [Description("1-based column of the method name (use with filePath)")] int? column = null,
        [Description("Fully-qualified metadata name of the method instead of a position, e.g. 'FixtureLib.Shipper.Quote' or, to pick an overload, 'FixtureLib.Shipper.Quote(string, int)' (parameter types as C# display names)")] string? fullyQualifiedName = null,
        [Description("Parameters to add: [{ name, type, defaultValue?, callSiteValue?, index? }]. Each needs callSiteValue (inserted at every call site) and/or defaultValue (call sites untouched).")] AddParameterSpec[]? addParameters = null,
        [Description("Names of parameters (as declared on the targeted method) to remove from every declaration and every call site")] string[]? removeParameters = null,
        [Description("Optional: the complete parameter name list in the desired final order - must be a permutation of the parameters left after removals plus additions")] string[]? newOrder = null)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var solution = WorkspaceHost.Current;
            var symbol = !string.IsNullOrEmpty(fullyQualifiedName)
                ? await RefactorTools.ResolveByFullyQualifiedNameAsync(solution, fullyQualifiedName)
                : await NavigationTools.ResolveSymbolAsync(
                    filePath ?? throw new ArgumentException("Provide either filePath+line+column or fullyQualifiedName."),
                    line ?? throw new ArgumentException("line is required when filePath is given."),
                    column ?? throw new ArgumentException("column is required when filePath is given."));

            var method = AsChangeableMethod(symbol);
            var plan = SignaturePlan.Build(method, addParameters, removeParameters, newOrder);

            var cascade = await CollectCascadeAsync(method, solution);
            var cascadeSet = new HashSet<ISymbol>(cascade, SymbolEqualityComparer.Default);
            var cascadeNames = cascade.Select(m => m.ToDisplayString()).ToList();
            var operations = plan.Describe(method);

            // --- Declarations: one ParameterListSyntax per declaring syntax of every cascade member ---
            var edits = new Dictionary<DocumentId, List<PlannedEdit>>();
            foreach (var member in cascade)
            {
                foreach (var reference in member.DeclaringSyntaxReferences)
                {
                    var declaration = await reference.GetSyntaxAsync();
                    var document = solution.GetDocument(reference.SyntaxTree)
                        ?? throw new InvalidOperationException($"Declaration of '{member.ToDisplayString()}' is not in a loaded document.");

                    var parameterList = declaration switch
                    {
                        MethodDeclarationSyntax m => m.ParameterList,
                        ConstructorDeclarationSyntax c => c.ParameterList,
                        TypeDeclarationSyntax => throw new InvalidOperationException(
                            $"'{member.ToDisplayString()}' is a primary constructor; change_signature does not support primary constructors yet."),
                        _ => throw new InvalidOperationException(
                            $"Unsupported declaration kind {declaration.Kind()} for '{member.ToDisplayString()}'.")
                    };

                    Add(edits, document.Id, new PlannedEdit(parameterList, rewritten =>
                        RewriteParameterList((ParameterListSyntax)rewritten, plan)));
                }
            }

            // --- Call sites: every reference to any cascade member, solution-wide ---
            var unsupported = new List<object>();
            var seenLocations = new HashSet<(string Path, int Start)>();
            var semanticModels = new Dictionary<DocumentId, SemanticModel>();
            var callSiteCount = 0;

            foreach (var member in cascade)
            {
                var referencedSymbols = await SymbolFinder.FindReferencesAsync(member, solution);
                foreach (var referenced in referencedSymbols)
                {
                    if (!cascadeSet.Contains(Canonical(referenced.Definition))) continue;

                    foreach (var location in referenced.Locations)
                    {
                        if (!location.Location.IsInSource) continue;
                        var path = location.Location.SourceTree?.FilePath ?? "";
                        if (!seenLocations.Add((path, location.Location.SourceSpan.Start))) continue;

                        var document = location.Document;
                        var root = await document.GetSyntaxRootAsync()
                            ?? throw new InvalidOperationException($"No syntax tree available for {document.FilePath}.");
                        var token = root.FindToken(location.Location.SourceSpan.Start, findInsideTrivia: true);
                        var node = token.Parent ?? root;

                        // XML doc crefs are never rewritten (a stale cref is a warning, not an error).
                        if (node.FirstAncestorOrSelf<DocumentationCommentTriviaSyntax>() is not null) continue;
                        // nameof(Method) stays valid whatever the signature becomes.
                        if (IsNameOfArgument(node)) continue;

                        if (location.IsImplicit)
                        {
                            unsupported.Add(DescribeUnsupported(location.Location, "implicit reference (collection initializer, pattern-based call, or similar) - no argument list to rewrite"));
                            continue;
                        }
                        if (location.CandidateReason != CandidateReason.None)
                        {
                            unsupported.Add(DescribeUnsupported(location.Location, $"reference does not bind cleanly ({location.CandidateReason}) - fix the existing error first"));
                            continue;
                        }

                        var call = FindCallSite(node);
                        if (call is null)
                        {
                            unsupported.Add(DescribeUnsupported(location.Location, "method used as a method group / delegate conversion (or another non-invocation usage) - rewrite it by hand first"));
                            continue;
                        }
                        if (call.Value.Kind == CallSiteKind.Attribute)
                        {
                            unsupported.Add(DescribeUnsupported(location.Location, "attribute usage - attribute argument lists are not rewritten"));
                            continue;
                        }

                        if (!semanticModels.TryGetValue(document.Id, out var semanticModel))
                        {
                            semanticModel = await document.GetSemanticModelAsync()
                                ?? throw new InvalidOperationException($"No semantic model for {document.FilePath}.");
                            semanticModels[document.Id] = semanticModel;
                        }

                        var bound = semanticModel.GetSymbolInfo(call.Value.CallNode).Symbol as IMethodSymbol
                            ?? referenced.Definition as IMethodSymbol
                            ?? member;
                        var receiverOffset = bound.MethodKind == MethodKind.ReducedExtension ? 1 : 0;
                        var boundDefinition = Canonical(bound) as IMethodSymbol ?? member;
                        if (!cascadeSet.Contains(boundDefinition))
                        {
                            unsupported.Add(DescribeUnsupported(location.Location, $"binds to '{bound.ToDisplayString()}', which is outside the override/implementation group"));
                            continue;
                        }

                        var existingArguments = call.Value.ArgumentList?.Arguments ?? default;
                        var argumentPlan = ArgumentPlan.TryBuild(existingArguments, boundDefinition.Parameters, receiverOffset, plan, out var reason);
                        if (argumentPlan is null)
                        {
                            unsupported.Add(DescribeUnsupported(location.Location, reason!));
                            continue;
                        }

                        callSiteCount++;
                        var capturedPlan = argumentPlan;
                        if (call.Value.ArgumentList is { } argumentList)
                        {
                            Add(edits, document.Id, new PlannedEdit(argumentList, rewritten =>
                                RewriteArgumentList((ArgumentListSyntax)rewritten, capturedPlan)));
                        }
                        else
                        {
                            // `new Foo { ... }` without parentheses: attach a fresh argument list to the creation expression.
                            var creation = (ObjectCreationExpressionSyntax)call.Value.CallNode;
                            Add(edits, document.Id, new PlannedEdit(creation, rewritten =>
                            {
                                var oc = (ObjectCreationExpressionSyntax)rewritten;
                                var typeTrivia = oc.Type.GetTrailingTrivia();
                                return oc
                                    .WithType(oc.Type.WithoutTrailingTrivia())
                                    .WithArgumentList(RewriteArgumentList(SyntaxFactory.ArgumentList(), capturedPlan).WithTrailingTrivia(typeTrivia));
                            }));
                        }
                    }
                }
            }

            if (unsupported.Count > 0)
            {
                return new
                {
                    symbol = NavigationTools.Describe(method),
                    cascade = cascadeNames,
                    operations,
                    applied = false,
                    changedFileCount = 0,
                    changes = Array.Empty<object>(),
                    unsupportedUsages = unsupported,
                    message = $"Not applied: {unsupported.Count} usage(s) of the method group cannot be rewritten safely (see unsupportedUsages). Nothing was written."
                };
            }

            // --- Build the candidate solution ---
            var candidateSolution = solution;
            foreach (var (documentId, documentEdits) in edits)
            {
                var document = candidateSolution.GetDocument(documentId)!;
                var root = (await document.GetSyntaxRootAsync())!;
                var byNode = documentEdits.ToDictionary(e => e.Target, e => e.Rewrite);
                var newRoot = root.ReplaceNodes(byNode.Keys, (original, rewritten) => byNode[original](rewritten));
                candidateSolution = candidateSolution.WithDocumentSyntaxRoot(documentId, newRoot);
            }

            var changes = await RefactorTools.DescribeDocumentChangesAsync(solution, candidateSolution, MaxChangedDocuments);
            var result = await MutationApplier.TryApplyAsync(candidateSolution);

            return new
            {
                symbol = NavigationTools.Describe(method),
                cascade = cascadeNames,
                operations,
                callSiteCount,
                applied = result.Applied,
                changedFileCount = result.Applied ? changes.Count : 0,
                changes = result.Applied ? changes : [],
                conflicts = result.Applied || result.NewErrors.Count == 0 ? null : result.NewErrors,
                staleFile = result.StaleFile,
                message = result.Message
            };
        });
    }

    // ----------------------------------------------------------------------------------
    // Target resolution and cascade
    // ----------------------------------------------------------------------------------

    private static IMethodSymbol AsChangeableMethod(ISymbol symbol)
    {
        if (symbol is IParameterSymbol { ContainingSymbol: IMethodSymbol owner }) symbol = owner;

        if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Delegate } || symbol is IMethodSymbol { MethodKind: MethodKind.DelegateInvoke })
        {
            throw new InvalidOperationException(
                $"'{symbol.ToDisplayString()}' is a delegate; change_signature supports ordinary methods and constructors only.");
        }

        if (symbol is not IMethodSymbol method)
        {
            throw new InvalidOperationException(
                $"'{symbol.ToDisplayString()}' is a {symbol.Kind}, not a method. Position the coordinates on a method or constructor name.");
        }

        switch (method.MethodKind)
        {
            case MethodKind.Ordinary:
            case MethodKind.Constructor:
            case MethodKind.ExplicitInterfaceImplementation:
                break;
            case MethodKind.LocalFunction:
                throw new InvalidOperationException(
                    $"'{method.ToDisplayString()}' is a local function; change_signature supports ordinary methods and constructors only.");
            default:
                throw new InvalidOperationException(
                    $"'{method.ToDisplayString()}' is a {method.MethodKind}; change_signature supports ordinary methods and constructors only.");
        }

        return (IMethodSymbol)Canonical(method);
    }

    /// <summary>Strips generic construction and extension-method reduction so group membership compares by definition.</summary>
    private static ISymbol Canonical(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { ReducedFrom: { } reducedFrom }) symbol = reducedFrom;
        return symbol.OriginalDefinition;
    }

    private static async Task<List<IMethodSymbol>> CollectCascadeAsync(IMethodSymbol seed, Solution solution)
    {
        var group = new List<IMethodSymbol>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<IMethodSymbol>();

        void Enqueue(ISymbol? candidate)
        {
            if (candidate is null || Canonical(candidate) is not IMethodSymbol method) return;
            if (seen.Add(method)) queue.Enqueue(method);
        }

        Enqueue(seed);
        while (queue.Count > 0)
        {
            var method = queue.Dequeue();
            group.Add(method);

            Enqueue(method.OverriddenMethod);
            Enqueue(method.PartialDefinitionPart);
            Enqueue(method.PartialImplementationPart);
            foreach (var explicitImplementation in method.ExplicitInterfaceImplementations) Enqueue(explicitImplementation);
            foreach (var interfaceMember in await SymbolFinder.FindImplementedInterfaceMembersAsync(method, solution)) Enqueue(interfaceMember);
            foreach (var @override in await SymbolFinder.FindOverridesAsync(method, solution)) Enqueue(@override);
            foreach (var implementation in await SymbolFinder.FindImplementationsAsync(method, solution)) Enqueue(implementation);
        }

        var fromMetadata = group.Where(m => m.DeclaringSyntaxReferences.Length == 0).ToList();
        if (fromMetadata.Count > 0)
        {
            throw new InvalidOperationException(
                "The override/implementation group includes members defined outside the loaded solution, whose signatures cannot be changed: " +
                string.Join(", ", fromMetadata.Select(m => m.ToDisplayString())));
        }

        return group;
    }

    // ----------------------------------------------------------------------------------
    // Call-site classification
    // ----------------------------------------------------------------------------------

    private enum CallSiteKind { Invocation, ObjectCreation, ImplicitObjectCreation, ConstructorInitializer, PrimaryConstructorBase, Attribute }

    private readonly record struct CallSite(CallSiteKind Kind, SyntaxNode CallNode, ArgumentListSyntax? ArgumentList);

    private static bool IsNameOfArgument(SyntaxNode node)
    {
        var top = ClimbName(node);
        return top.Parent is ArgumentSyntax
        {
            Parent: ArgumentListSyntax
            {
                Parent: InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } }
            }
        };
    }

    /// <summary>From the referenced identifier up through qualified names and member accesses to the expression a call would own.</summary>
    private static SyntaxNode ClimbName(SyntaxNode node)
    {
        var top = node;
        while (top.Parent is QualifiedNameSyntax or AliasQualifiedNameSyntax) top = top.Parent;
        if (top.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == top) top = memberAccess;
        else if (top.Parent is MemberBindingExpressionSyntax memberBinding && memberBinding.Name == top) top = memberBinding;
        return top;
    }

    private static CallSite? FindCallSite(SyntaxNode node)
    {
        switch (node)
        {
            case ConstructorInitializerSyntax initializer:
                return new CallSite(CallSiteKind.ConstructorInitializer, initializer, initializer.ArgumentList);
            case ImplicitObjectCreationExpressionSyntax implicitCreation:
                return new CallSite(CallSiteKind.ImplicitObjectCreation, implicitCreation, implicitCreation.ArgumentList);
        }

        var top = ClimbName(node);
        return top.Parent switch
        {
            InvocationExpressionSyntax invocation when invocation.Expression == top =>
                new CallSite(CallSiteKind.Invocation, invocation, invocation.ArgumentList),
            ObjectCreationExpressionSyntax creation when creation.Type == top =>
                new CallSite(CallSiteKind.ObjectCreation, creation, creation.ArgumentList),
            PrimaryConstructorBaseTypeSyntax primaryBase when primaryBase.Type == top =>
                new CallSite(CallSiteKind.PrimaryConstructorBase, primaryBase, primaryBase.ArgumentList),
            AttributeSyntax attribute when attribute.Name == top =>
                new CallSite(CallSiteKind.Attribute, attribute, null),
            _ => null
        };
    }

    private static object DescribeUnsupported(Location location, string reason)
    {
        var span = location.GetLineSpan();
        var text = location.SourceTree?.GetText();
        var lineIndex = span.StartLinePosition.Line;
        return new
        {
            file = span.Path,
            line = lineIndex + 1,
            column = span.StartLinePosition.Character + 1,
            snippet = text is not null && lineIndex < text.Lines.Count ? text.Lines[lineIndex].ToString().Trim() : null,
            reason
        };
    }

    // ----------------------------------------------------------------------------------
    // Rewriting
    // ----------------------------------------------------------------------------------

    private sealed record PlannedEdit(SyntaxNode Target, Func<SyntaxNode, SyntaxNode> Rewrite);

    private static void Add(Dictionary<DocumentId, List<PlannedEdit>> edits, DocumentId documentId, PlannedEdit edit)
    {
        if (!edits.TryGetValue(documentId, out var list)) edits[documentId] = list = [];
        list.Add(edit);
    }

    private static ParameterListSyntax RewriteParameterList(ParameterListSyntax list, SignaturePlan plan)
    {
        var original = list.Parameters;
        var newParameters = new List<ParameterSyntax>();
        foreach (var slot in plan.Slots)
        {
            newParameters.Add(slot.IsNew
                ? slot.New!.MakeParameter()
                : original[slot.OriginalIndex]);
        }
        return list.WithParameters(BuildSeparatedList(original, newParameters, isNew: p => !original.Contains(p)));
    }

    private static ArgumentListSyntax RewriteArgumentList(ArgumentListSyntax list, ArgumentPlan plan)
    {
        var original = list.Arguments;
        var newArguments = plan.Emit(original);
        return list.WithArguments(BuildSeparatedList(original, newArguments, isNew: a => !original.Contains(a)));
    }

    /// <summary>
    /// Rebuilds a comma-separated list from reordered/added/removed elements while keeping
    /// the original separators (so multi-line lists keep their line breaks) and giving new
    /// elements the leading trivia of whatever originally sat at their position.
    /// </summary>
    private static SeparatedSyntaxList<T> BuildSeparatedList<T>(
        SeparatedSyntaxList<T> original, IReadOnlyList<T> elements, Func<T, bool> isNew) where T : SyntaxNode
    {
        if (elements.Count == 0) return SyntaxFactory.SeparatedList<T>();

        var originalNodes = original.ToList();
        var separators = original.GetSeparators().ToList();
        var defaultSeparator = separators.Count > 0
            ? separators[^1]
            : SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space);

        var parts = new List<SyntaxNodeOrToken>();
        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            if (isNew(element))
            {
                var template = originalNodes.Count > 0 ? originalNodes[Math.Min(i, originalNodes.Count - 1)] : null;
                element = element.WithLeadingTrivia(template?.GetLeadingTrivia() ?? SyntaxTriviaList.Empty).WithoutTrailingTrivia();
            }
            else if (i == elements.Count - 1 && originalNodes.Count > 0 && !ReferenceEquals(element, originalNodes[^1]))
            {
                // An element that moved into last place must not drag a separator-side trailing newline with it.
                element = element.WithTrailingTrivia(originalNodes[^1].GetTrailingTrivia());
            }

            parts.Add(element);
            if (i < elements.Count - 1)
            {
                parts.Add(i < separators.Count ? separators[i] : defaultSeparator);
            }
        }

        return SyntaxFactory.SeparatedList<T>(parts);
    }

    // ----------------------------------------------------------------------------------
    // Plans
    // ----------------------------------------------------------------------------------

    /// <summary>A position in the final parameter list: an existing parameter (by original index) or a newly added one.</summary>
    private sealed record Slot(int OriginalIndex, NewParameter? New)
    {
        public bool IsNew => New is not null;
    }

    private sealed record NewParameter(string Name, string Type, string? DefaultValue, string? CallSiteValue)
    {
        public ParameterSyntax MakeParameter()
        {
            var text = DefaultValue is null ? $"({Type} {Name})" : $"({Type} {Name} = {DefaultValue})";
            return SyntaxFactory.ParseParameterList(text).Parameters[0];
        }

        public ExpressionSyntax MakeCallSiteExpression() => SyntaxFactory.ParseExpression(CallSiteValue!);
    }

    private sealed class SignaturePlan
    {
        public required IReadOnlyList<Slot> Slots { get; init; }
        public required IReadOnlyList<string> Removed { get; init; }
        public required IReadOnlyList<NewParameter> Added { get; init; }
        public required IReadOnlyList<string>? NewOrder { get; init; }
        public required HashSet<int> RemovedIndices { get; init; }

        public static SignaturePlan Build(
            IMethodSymbol method, AddParameterSpec[]? addParameters, string[]? removeParameters, string[]? newOrder)
        {
            var originalParameters = method.Parameters;
            var removes = removeParameters ?? [];
            var adds = addParameters ?? [];

            if (removes.Length == 0 && adds.Length == 0 && newOrder is not { Length: > 0 })
            {
                throw new ArgumentException("Nothing to do: give at least one of addParameters, removeParameters, newOrder.");
            }

            // 1. remove
            var removedIndices = new HashSet<int>();
            foreach (var name in removes)
            {
                var index = originalParameters.ToList().FindIndex(p => p.Name == name);
                if (index < 0)
                {
                    throw new ArgumentException(
                        $"removeParameters: '{method.ToDisplayString()}' has no parameter named '{name}'. Parameters: {ParameterNames(originalParameters)}.");
                }
                if (!removedIndices.Add(index))
                {
                    throw new ArgumentException($"removeParameters: '{name}' is listed more than once.");
                }
            }

            var slots = new List<Slot>();
            for (var i = 0; i < originalParameters.Length; i++)
            {
                if (!removedIndices.Contains(i)) slots.Add(new Slot(i, null));
            }

            // 2. add
            var added = new List<NewParameter>();
            foreach (var spec in adds)
            {
                if (string.IsNullOrWhiteSpace(spec.Name) || !SyntaxFacts.IsValidIdentifier(spec.Name))
                {
                    throw new ArgumentException($"addParameters: '{spec.Name}' is not a valid C# identifier.");
                }
                if (string.IsNullOrWhiteSpace(spec.Type) || SyntaxFactory.ParseTypeName(spec.Type).ContainsDiagnostics)
                {
                    throw new ArgumentException($"addParameters: type '{spec.Type}' for '{spec.Name}' is not valid C# type syntax.");
                }
                if (spec.CallSiteValue is null && spec.DefaultValue is null)
                {
                    throw new ArgumentException(
                        $"addParameters: '{spec.Name}' needs a callSiteValue (inserted at every call site) and/or a defaultValue (call sites left unchanged). Nothing was changed.");
                }
                if (spec.CallSiteValue is not null && SyntaxFactory.ParseExpression(spec.CallSiteValue).ContainsDiagnostics)
                {
                    throw new ArgumentException($"addParameters: callSiteValue '{spec.CallSiteValue}' for '{spec.Name}' is not a valid C# expression.");
                }
                if (spec.DefaultValue is not null && SyntaxFactory.ParseExpression(spec.DefaultValue).ContainsDiagnostics)
                {
                    throw new ArgumentException($"addParameters: defaultValue '{spec.DefaultValue}' for '{spec.Name}' is not a valid C# expression.");
                }
                if (slots.Any(s => SlotName(s, originalParameters) == spec.Name))
                {
                    throw new ArgumentException($"addParameters: a parameter named '{spec.Name}' already exists on '{method.ToDisplayString()}'.");
                }

                var parameter = new NewParameter(spec.Name, spec.Type.Trim(), spec.DefaultValue?.Trim(), spec.CallSiteValue?.Trim());
                var index = spec.Index ?? DefaultInsertIndex(slots, originalParameters, parameter.DefaultValue is not null);
                if (index < 0 || index > slots.Count)
                {
                    throw new ArgumentException($"addParameters: index {index} for '{spec.Name}' is out of range (0..{slots.Count}).");
                }
                slots.Insert(index, new Slot(-1, parameter));
                added.Add(parameter);
            }

            // 3. reorder
            if (newOrder is { Length: > 0 })
            {
                var currentNames = slots.Select(s => SlotName(s, originalParameters)).ToList();
                var sortedCurrent = currentNames.Order(StringComparer.Ordinal).ToList();
                var sortedWanted = newOrder.Order(StringComparer.Ordinal).ToList();
                if (!sortedCurrent.SequenceEqual(sortedWanted))
                {
                    throw new ArgumentException(
                        $"newOrder must be a permutation of the parameters after removals and additions: [{string.Join(", ", currentNames)}]; got [{string.Join(", ", newOrder)}].");
                }
                slots = newOrder.Select(name => slots[currentNames.IndexOf(name)]).ToList();
            }

            return new SignaturePlan
            {
                Slots = slots,
                Removed = removes,
                Added = added,
                NewOrder = newOrder is { Length: > 0 } ? newOrder : null,
                RemovedIndices = removedIndices
            };
        }

        private static int DefaultInsertIndex(List<Slot> slots, IReadOnlyList<IParameterSymbol> originalParameters, bool isOptional)
        {
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (slot.IsNew)
                {
                    if (!isOptional && slot.New!.DefaultValue is not null) return i;
                    continue;
                }
                var parameter = originalParameters[slot.OriginalIndex];
                if (parameter.IsParams) return i;
                if (!isOptional && parameter.IsOptional) return i;
            }
            return slots.Count;
        }

        private static string SlotName(Slot slot, IReadOnlyList<IParameterSymbol> originalParameters) =>
            slot.IsNew ? slot.New!.Name : originalParameters[slot.OriginalIndex].Name;

        private static string ParameterNames(IEnumerable<IParameterSymbol> parameters) =>
            string.Join(", ", parameters.Select(p => p.Name));

        public object Describe(IMethodSymbol method)
        {
            var parameters = method.Parameters;
            var after = string.Join(", ", Slots.Select(s => s.IsNew
                ? $"{s.New!.Type} {s.New.Name}"
                : $"{parameters[s.OriginalIndex].Type.ToDisplayString()} {parameters[s.OriginalIndex].Name}"));

            return new
            {
                removed = Removed,
                added = Added.Select(a => new { name = a.Name, type = a.Type, defaultValue = a.DefaultValue, callSiteValue = a.CallSiteValue }).ToList(),
                newOrder = NewOrder,
                before = method.ToDisplayString(),
                after = $"{method.ContainingType.ToDisplayString()}.{method.Name}({after})"
            };
        }
    }

    /// <summary>
    /// How one call site's argument list maps onto the new signature. Built against the
    /// ORIGINAL arguments (indices only), applied to the possibly already-rewritten node
    /// (nested calls of the same group), which has the same argument count and order.
    /// </summary>
    private sealed class ArgumentPlan
    {
        private readonly SignaturePlan _plan;
        private readonly IReadOnlyList<IParameterSymbol> _parameters;
        private readonly int[] _argumentToParameter;   // per original argument: original parameter index
        private readonly bool[] _argumentIsNamed;

        private ArgumentPlan(SignaturePlan plan, IReadOnlyList<IParameterSymbol> parameters, int[] argumentToParameter, bool[] argumentIsNamed)
        {
            _plan = plan;
            _parameters = parameters;
            _argumentToParameter = argumentToParameter;
            _argumentIsNamed = argumentIsNamed;
        }

        public static ArgumentPlan? TryBuild(
            SeparatedSyntaxList<ArgumentSyntax> arguments, IReadOnlyList<IParameterSymbol> parameters,
            int receiverOffset, SignaturePlan plan, out string? reason)
        {
            reason = null;
            var paramsIndex = parameters.Count > 0 && parameters[^1].IsParams ? parameters.Count - 1 : -1;
            var map = new int[arguments.Count];
            var named = new bool[arguments.Count];

            for (var i = 0; i < arguments.Count; i++)
            {
                var argument = arguments[i];
                if (argument.NameColon is { } nameColon)
                {
                    var name = nameColon.Name.Identifier.ValueText;
                    var index = parameters.ToList().FindIndex(p => p.Name == name);
                    if (index < 0)
                    {
                        reason = $"named argument '{name}:' does not match any parameter";
                        return null;
                    }
                    map[i] = index;
                    named[i] = true;
                }
                else
                {
                    var index = i + receiverOffset;
                    if (paramsIndex >= 0 && index >= paramsIndex) index = paramsIndex;
                    if (index >= parameters.Count)
                    {
                        reason = "more positional arguments than parameters";
                        return null;
                    }
                    map[i] = index;
                }
            }

            // A params parameter given in expanded form can't be expressed as a named argument,
            // which it would have to be if it no longer sits at the end behind only positional args.
            if (paramsIndex >= 0 && !plan.RemovedIndices.Contains(paramsIndex))
            {
                var expandedCount = map.Count(p => p == paramsIndex);
                var paramsSlotPosition = plan.Slots.ToList().FindIndex(s => !s.IsNew && s.OriginalIndex == paramsIndex);
                var afterParams = plan.Slots.Skip(paramsSlotPosition + 1).Any(s => s.IsNew ? s.New!.CallSiteValue is not null : map.Contains(s.OriginalIndex));
                if (expandedCount > 1 && (paramsSlotPosition != plan.Slots.Count - 1 || afterParams))
                {
                    reason = "expanded params arguments would have to move behind a named argument";
                    return null;
                }
            }

            return new ArgumentPlan(plan, parameters, map, named);
        }

        public List<ArgumentSyntax> Emit(SeparatedSyntaxList<ArgumentSyntax> arguments)
        {
            var positionalPrefix = new List<ArgumentSyntax>();
            var remainder = new List<(int OriginalPosition, ArgumentSyntax Argument)>();
            var positionalAllowed = true;
            var nextRemainderPosition = arguments.Count;

            foreach (var slot in _plan.Slots)
            {
                if (slot.IsNew)
                {
                    if (slot.New!.CallSiteValue is null)
                    {
                        positionalAllowed = false; // optional parameter omitted at this call site
                        continue;
                    }
                    var expression = slot.New.MakeCallSiteExpression();
                    if (positionalAllowed)
                    {
                        positionalPrefix.Add(SyntaxFactory.Argument(expression));
                    }
                    else
                    {
                        remainder.Add((nextRemainderPosition++, SyntaxFactory.Argument(
                            SyntaxFactory.NameColon(slot.New.Name).WithTrailingTrivia(SyntaxFactory.Space), default, expression)));
                    }
                    continue;
                }

                var indices = Enumerable.Range(0, arguments.Count).Where(i => _argumentToParameter[i] == slot.OriginalIndex).ToList();
                if (indices.Count == 0)
                {
                    positionalAllowed = false; // optional parameter omitted at this call site
                    continue;
                }

                foreach (var i in indices)
                {
                    var argument = arguments[i];
                    if (_argumentIsNamed[i])
                    {
                        positionalAllowed = false;
                        remainder.Add((i, argument));
                    }
                    else if (positionalAllowed)
                    {
                        positionalPrefix.Add(argument);
                    }
                    else
                    {
                        var name = _parameters[slot.OriginalIndex].Name;
                        remainder.Add((i, argument
                            .WithNameColon(SyntaxFactory.NameColon(name).WithTrailingTrivia(SyntaxFactory.Space))
                            .WithLeadingTrivia(argument.GetLeadingTrivia())));
                    }
                }
            }

            // Existing arguments keep their textual order; new named arguments go last.
            return positionalPrefix
                .Concat(remainder.OrderBy(r => r.OriginalPosition).Select(r => r.Argument))
                .ToList();
        }
    }
}
