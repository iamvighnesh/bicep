// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Bicep.Core.Diagnostics;
using Bicep.Core.Extensions;
using Bicep.Core.Features;
using Bicep.Core.Semantics.Namespaces;
using Bicep.Core.Syntax;
using Bicep.Core.TypeSystem;
using Bicep.Core.Workspaces;

namespace Bicep.Core.Semantics
{
    public sealed class DeclarationVisitor : AstVisitor
    {
        private readonly INamespaceProvider namespaceProvider;
        private readonly IFeatureProvider features;
        private readonly ResourceScope targetScope;
        private readonly ISymbolContext context;
        private readonly BicepSourceFileKind sourceFileKind;
        private readonly IList<ScopeInfo> localScopes;

        private readonly Stack<ScopeInfo> activeScopes = new();

        private DeclarationVisitor(INamespaceProvider namespaceProvider, IFeatureProvider features, ResourceScope targetScope, ISymbolContext context, IList<ScopeInfo> localScopes, BicepSourceFileKind sourceFileKind)
        {
            this.namespaceProvider = namespaceProvider;
            this.features = features;
            this.targetScope = targetScope;
            this.context = context;
            this.localScopes = localScopes;
            this.sourceFileKind = sourceFileKind;
        }

        // Returns the list of top level declarations as well as top level scopes.
        public static LocalScope GetDeclarations(INamespaceProvider namespaceProvider, IFeatureProvider features, ResourceScope targetScope, BicepSourceFile sourceFile, ISymbolContext symbolContext)
        {
            // collect declarations
            var localScopes = new List<ScopeInfo>();
            var declarationVisitor = new DeclarationVisitor(namespaceProvider, features, targetScope, symbolContext, localScopes, sourceFile.FileKind);
            declarationVisitor.Visit(sourceFile.ProgramSyntax);

            return MakeImmutable(localScopes.Single());
        }

        public override void VisitProgramSyntax(ProgramSyntax syntax)
        {
            // create new scope without any descendants
            var scope = new LocalScope(string.Empty, syntax, syntax, ImmutableArray<DeclaredSymbol>.Empty, ImmutableArray<LocalScope>.Empty, ScopeResolution.GlobalsOnly);
            this.PushScope(scope);

            base.VisitProgramSyntax(syntax);

            this.PopScope();
        }

        public override void VisitMetadataDeclarationSyntax(MetadataDeclarationSyntax syntax)
        {
            base.VisitMetadataDeclarationSyntax(syntax);

            var symbol = new MetadataSymbol(this.context, syntax.Name.IdentifierName, syntax, syntax.Value);
            DeclareSymbol(symbol);
        }

        public override void VisitParameterDeclarationSyntax(ParameterDeclarationSyntax syntax)
        {
            base.VisitParameterDeclarationSyntax(syntax);

            var symbol = new ParameterSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitTypeDeclarationSyntax(TypeDeclarationSyntax syntax)
        {
            base.VisitTypeDeclarationSyntax(syntax);

            var symbol = new TypeAliasSymbol(this.context, syntax.Name.IdentifierName, syntax, syntax.Value);
            DeclareSymbol(symbol);
        }

        public override void VisitVariableDeclarationSyntax(VariableDeclarationSyntax syntax)
        {
            base.VisitVariableDeclarationSyntax(syntax);

            var symbol = new VariableSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitFunctionDeclarationSyntax(FunctionDeclarationSyntax syntax)
        {
            base.VisitFunctionDeclarationSyntax(syntax);

            var symbol = new DeclaredFunctionSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitResourceDeclarationSyntax(ResourceDeclarationSyntax syntax)
        {
            // Create a scope for each resource body - this ensures that nested resources
            // are contained within the appropriate scope.
            //
            // There may be additional scopes nested inside this between the resource declaration
            // and the actual object body (for-loop). That's OK, in that case, this scope will
            // be empty and we'll use the `for` scope for lookups.
            var bindingSyntax = syntax.Value is IfConditionSyntax ifConditionSyntax ? ifConditionSyntax.Body : syntax.Value;
            var scope = new LocalScope(string.Empty, syntax, bindingSyntax, ImmutableArray<DeclaredSymbol>.Empty, ImmutableArray<LocalScope>.Empty, ScopeResolution.InheritParent);
            this.PushScope(scope);

            base.VisitResourceDeclarationSyntax(syntax);

            this.PopScope();

            // The resource itself should be declared in the enclosing scope - it's accessible to nested
            // resource, but also siblings.
            var symbol = new ResourceSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitModuleDeclarationSyntax(ModuleDeclarationSyntax syntax)
        {
            base.VisitModuleDeclarationSyntax(syntax);

            var symbol = new ModuleSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitTestDeclarationSyntax(TestDeclarationSyntax syntax)
        {
            base.VisitTestDeclarationSyntax(syntax);

            var symbol = new TestSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitOutputDeclarationSyntax(OutputDeclarationSyntax syntax)
        {
            base.VisitOutputDeclarationSyntax(syntax);

            var symbol = new OutputSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitAssertDeclarationSyntax(AssertDeclarationSyntax syntax)
        {
            base.VisitAssertDeclarationSyntax(syntax);

            var symbol = new AssertSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitProviderDeclarationSyntax(ProviderDeclarationSyntax syntax)
        {
            base.VisitProviderDeclarationSyntax(syntax);

            TypeSymbol declaredType;
            if (!features.ExtensibilityEnabled)
            {
                declaredType = ErrorType.Create(DiagnosticBuilder.ForPosition(syntax).ImportsAreDisabled());
            }
            else if (syntax.SpecificationString is StringSyntax specificationString && specificationString.IsInterpolated())
            {
                declaredType = ErrorType.Create(DiagnosticBuilder.ForPosition(syntax.SpecificationString).ProviderSpecificationInterpolationUnsupported());
            }
            else if (!syntax.Specification.IsValid)
            {
                declaredType = syntax.SpecificationString is StringSyntax
                    ? ErrorType.Create(DiagnosticBuilder.ForPosition(syntax.Specification).InvalidProviderSpecification())
                    : ErrorType.Empty();
            }
            else if (namespaceProvider.TryGetNamespace(syntax.Specification.Name, syntax.Alias?.IdentifierName ?? syntax.Specification.Name, targetScope, features, sourceFileKind, syntax.Specification?.Version) is not { } namespaceType
                || (syntax.Specification?.Name == MicrosoftGraphNamespaceType.BuiltInName && !features.MicrosoftGraphPreviewEnabled))
            {
                declaredType = ErrorType.Create(DiagnosticBuilder.ForPosition(syntax).UnrecognizedImportProvider(syntax.Specification!.Name));
            }
            else
            {
                declaredType = namespaceType;
            }

            var symbol = new ProviderNamespaceSymbol(this.context, syntax, declaredType);
            DeclareSymbol(symbol);
        }

        public override void VisitParameterAssignmentSyntax(ParameterAssignmentSyntax syntax)
        {
            base.VisitParameterAssignmentSyntax(syntax);

            var symbol = new ParameterAssignmentSymbol(this.context, syntax.Name.IdentifierName, syntax);
            DeclareSymbol(symbol);
        }

        public override void VisitLambdaSyntax(LambdaSyntax syntax)
        {
            // create new scope without any descendants
            var scope = new LocalScope(string.Empty, syntax, syntax.Body, ImmutableArray<DeclaredSymbol>.Empty, ImmutableArray<LocalScope>.Empty, ScopeResolution.InheritParent);
            this.PushScope(scope);

            /*
             * We cannot add the local symbol to the list of declarations because it will
             * break name binding at the global namespace level
            */
            foreach (var variable in syntax.GetLocalVariables())
            {
                var itemVariableSymbol = new LocalVariableSymbol(this.context, variable.Name.IdentifierName, variable, LocalKind.LambdaItemVariable);
                DeclareSymbol(itemVariableSymbol);
            }

            // visit the children
            base.VisitLambdaSyntax(syntax);

            this.PopScope();
        }

        public override void VisitTypedLambdaSyntax(TypedLambdaSyntax syntax)
        {
            // create new scope without any descendants
            var scope = new LocalScope(string.Empty, syntax, syntax.Body, ImmutableArray<DeclaredSymbol>.Empty, ImmutableArray<LocalScope>.Empty, ScopeResolution.GlobalsOnly);
            this.PushScope(scope);

            /*
             * We cannot add the local symbol to the list of declarations because it will
             * break name binding at the global namespace level
            */
            foreach (var variable in syntax.GetLocalVariables())
            {
                var itemVariableSymbol = new LocalVariableSymbol(this.context, variable.Name.IdentifierName, variable, LocalKind.LambdaItemVariable);
                DeclareSymbol(itemVariableSymbol);
            }

            // visit the children
            base.VisitTypedLambdaSyntax(syntax);

            this.PopScope();
        }

        public override void VisitForSyntax(ForSyntax syntax)
        {
            // create new scope without any descendants
            var scope = new LocalScope(string.Empty, syntax, syntax.Body, ImmutableArray<DeclaredSymbol>.Empty, ImmutableArray<LocalScope>.Empty, ScopeResolution.InheritParent);
            this.PushScope(scope);

            /*
             * We cannot add the local symbol to the list of declarations because it will
             * break name binding at the global namespace level
             */
            var itemVariable = syntax.ItemVariable;
            if (itemVariable is not null)
            {
                var itemVariableSymbol = new LocalVariableSymbol(this.context, itemVariable.Name.IdentifierName, itemVariable, LocalKind.ForExpressionItemVariable);
                DeclareSymbol(itemVariableSymbol);
            }

            var indexVariable = syntax.IndexVariable;
            if (indexVariable is not null)
            {
                var indexVariableSymbol = new LocalVariableSymbol(this.context, indexVariable.Name.IdentifierName, indexVariable, LocalKind.ForExpressionIndexVariable);
                DeclareSymbol(indexVariableSymbol);
            }

            // visit the children
            base.VisitForSyntax(syntax);

            this.PopScope();
        }

        public override void VisitCompileTimeImportDeclarationSyntax(CompileTimeImportDeclarationSyntax syntax)
        {
            base.VisitCompileTimeImportDeclarationSyntax(syntax);

            switch (syntax.ImportExpression)
            {
                case WildcardImportSyntax wildcardImport:
                    DeclareSymbol(new WildcardImportSymbol(context, wildcardImport, syntax));
                    break;
                case ImportedSymbolsListSyntax importedSymbolsList:
                    foreach (var item in importedSymbolsList.ImportedSymbols)
                    {
                        DeclareSymbol(new ImportedTypeSymbol(context, item, syntax));
                    }
                    break;
            }
        }

        private void DeclareSymbol(DeclaredSymbol symbol)
        {
            if (this.activeScopes.TryPeek(out var current))
            {
                current.Locals.Add(symbol);
            }
        }

        private void PushScope(LocalScope scope)
        {
            var item = new ScopeInfo(scope);

            if (this.activeScopes.TryPeek(out var current))
            {
                if (object.ReferenceEquals(current.Scope.BindingSyntax, scope.BindingSyntax))
                {
                    throw new InvalidOperationException($"Attempting to redefine the scope for {current.Scope.BindingSyntax}");
                }

                // add this one to the parent
                current.Children.Add(item);
            }
            else
            {
                // add this to the root list
                this.localScopes.Add(item);
            }

            this.activeScopes.Push(item);
        }

        private void PopScope()
        {
            this.activeScopes.Pop();
        }

        private static LocalScope MakeImmutable(ScopeInfo info)
        {
            return info.Scope.ReplaceChildren(info.Children.Select(MakeImmutable)).ReplaceLocals(info.Locals);
        }

        /// <summary>
        /// Allows us to mutate child scopes without having to swap out items on the stack
        /// which is fragile.
        /// </summary>
        /// <remarks>This could be replaced with a record if we could target .net 5</remarks>
        private class ScopeInfo
        {
            public ScopeInfo(LocalScope scope)
            {
                this.Scope = scope;
            }

            public LocalScope Scope { get; }

            public IList<DeclaredSymbol> Locals { get; } = new List<DeclaredSymbol>();

            public IList<ScopeInfo> Children { get; } = new List<ScopeInfo>();
        }
    }
}
