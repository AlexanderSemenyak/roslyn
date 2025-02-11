﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.PooledObjects;

#if DEBUG
using System.Diagnostics;
#endif

namespace Microsoft.CodeAnalysis.Wrapping.BinaryExpression
{
    using Microsoft.CodeAnalysis.Indentation;
    using Microsoft.CodeAnalysis.Precedence;

    internal abstract partial class AbstractBinaryExpressionWrapper<TBinaryExpressionSyntax> : AbstractSyntaxWrapper
        where TBinaryExpressionSyntax : SyntaxNode
    {
        private readonly ISyntaxFacts _syntaxFacts;
        private readonly IPrecedenceService _precedenceService;

        protected AbstractBinaryExpressionWrapper(
            IIndentationService indentationService,
            ISyntaxFacts syntaxFacts,
            IPrecedenceService precedenceService) : base(indentationService)
        {
            _syntaxFacts = syntaxFacts;
            _precedenceService = precedenceService;
        }

        /// <summary>
        /// Get's the language specific trivia that should be inserted before an operator if the
        /// user wants to wrap the operator to the next line.  For C# this is a simple newline-trivia.
        /// For VB, this will be a line-continuation char (<c>_</c>), followed by a newline.
        /// </summary>
        protected abstract SyntaxTriviaList GetNewLineBeforeOperatorTrivia(SyntaxTriviaList newLine);

        public sealed override async Task<ICodeActionComputer?> TryCreateComputerAsync(
            Document document, int position, SyntaxNode node, SyntaxWrappingOptions options, bool containsSyntaxError, CancellationToken cancellationToken)
        {
            if (containsSyntaxError)
                return null;

            if (node is not TBinaryExpressionSyntax binaryExpr)
                return null;

            var precedence = _precedenceService.GetPrecedenceKind(binaryExpr);
            if (precedence == PrecedenceKind.Other)
                return null;

            // Don't process this binary expression if it's in a parent binary expr of the same or
            // lower precedence.  We'll just allow our caller to walk up to that and call back into
            // us to handle.  This way, we're always starting at the topmost binary expr of this
            // precedence.
            //
            // for example, if we have `if (a + b == c + d)` expectation is to wrap on the lower
            // precedence `==` op, not either of the `+` ops
            //
            // Note: we use `<=` when comparing precedence because lower precedence has a higher
            // value.
            if (binaryExpr.Parent is TBinaryExpressionSyntax parentBinary &&
                precedence <= _precedenceService.GetPrecedenceKind(parentBinary))
            {
                return null;
            }

            var exprsAndOperators = GetExpressionsAndOperators(precedence, binaryExpr);
#if DEBUG
            Debug.Assert(exprsAndOperators.Length >= 3);
            Debug.Assert(exprsAndOperators.Length % 2 == 1, "Should have odd number of exprs and operators");
            for (var i = 0; i < exprsAndOperators.Length; i++)
            {
                var item = exprsAndOperators[i];
                Debug.Assert(((i % 2) == 0 && item.IsNode) ||
                             ((i % 2) == 1 && item.IsToken));
            }
#endif

            var containsUnformattableContent = await ContainsUnformattableContentAsync(
                document, exprsAndOperators, cancellationToken).ConfigureAwait(false);

            if (containsUnformattableContent)
                return null;

            var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            return new BinaryExpressionCodeActionComputer(
                this, document, sourceText, options, binaryExpr,
                exprsAndOperators, cancellationToken);
        }

        private ImmutableArray<SyntaxNodeOrToken> GetExpressionsAndOperators(
            PrecedenceKind precedence, TBinaryExpressionSyntax binaryExpr)
        {
            using var _ = ArrayBuilder<SyntaxNodeOrToken>.GetInstance(out var result);
            AddExpressionsAndOperators(precedence, binaryExpr, result);
            return result.ToImmutable();
        }

        private void AddExpressionsAndOperators(
            PrecedenceKind precedence, SyntaxNode expr, ArrayBuilder<SyntaxNodeOrToken> result)
        {
            if (expr is TBinaryExpressionSyntax &&
                precedence == _precedenceService.GetPrecedenceKind(expr))
            {
                _syntaxFacts.GetPartsOfBinaryExpression(
                    expr, out var left, out var opToken, out var right);
                AddExpressionsAndOperators(precedence, left, result);
                result.Add(opToken);
                AddExpressionsAndOperators(precedence, right, result);
            }
            else
            {
                result.Add(expr);
            }
        }
    }
}
