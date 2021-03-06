﻿/*
 * SonarAnalyzer for .NET
 * Copyright (C) 2015-2017 SonarSource SA
 * mailto: contact AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SonarAnalyzer.Helpers;
using Microsoft.CodeAnalysis.Diagnostics;
using SonarAnalyzer.Common;
using System.Collections.Immutable;

namespace SonarAnalyzer.Rules.CSharp
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    [Rule(BugDiagnosticId)]
    [Rule(CodeSmellDiagnosticId)]
    public class StringFormatValidator : SonarDiagnosticAnalyzer
    {
        private const string BugDiagnosticId = "S2275";
        private const string CodeSmellDiagnosticId = "S3457";
        private const string MessageFormat = "{0}";

        private static readonly DiagnosticDescriptor bugRule =
          DiagnosticDescriptorBuilder.GetDescriptor(BugDiagnosticId, MessageFormat, RspecStrings.ResourceManager);

        private static readonly DiagnosticDescriptor codeSmellRule =
          DiagnosticDescriptorBuilder.GetDescriptor(CodeSmellDiagnosticId, MessageFormat, RspecStrings.ResourceManager);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(bugRule, codeSmellRule);

        private static readonly ISet<MethodSignature> HandledFormatMethods = new HashSet<MethodSignature>
        {
            new MethodSignature(KnownType.System_String, "Format"),
            new MethodSignature(KnownType.System_Console, "Write"),
            new MethodSignature(KnownType.System_Console, "WriteLine"),
            new MethodSignature(KnownType.System_Text_StringBuilder, "AppendFormat"),
            new MethodSignature(KnownType.System_IO_TextWriter, "Write"),
            new MethodSignature(KnownType.System_IO_TextWriter, "WriteLine"),
            new MethodSignature(KnownType.System_Diagnostics_Debug, "WriteLine"),
            new MethodSignature(KnownType.System_Diagnostics_Trace, "TraceError"),
            new MethodSignature(KnownType.System_Diagnostics_Trace, "TraceInformation"),
            new MethodSignature(KnownType.System_Diagnostics_Trace, "TraceWarning"),
            new MethodSignature(KnownType.System_Diagnostics_TraceSource, "TraceInformation")
        };

        private static readonly ISet<ValidationFailure> bugRelatedFailures =
            new HashSet<ValidationFailure>
            {
                ValidationFailure.UnknownError,
                ValidationFailure.NullFormatString,
                ValidationFailure.InvalidCharacterAfterOpenCurlyBrace,
                ValidationFailure.UnbalancedCurlyBraceCount,
                ValidationFailure.FormatItemMalformed,
                ValidationFailure.FormatItemIndexIsNaN,
                ValidationFailure.FormatItemAlignmentIsNaN,
                ValidationFailure.FormatItemIndexTooHigh
            };

        private static readonly ISet<ValidationFailure> codeSmellRelatedFailures =
            new HashSet<ValidationFailure>
            {
                ValidationFailure.SimpleString,
                ValidationFailure.MissingFormatItemIndex,
                ValidationFailure.UnusedFormatArguments
            };

        protected sealed override void Initialize(SonarAnalysisContext context)
        {
            context.RegisterSyntaxNodeActionInNonGenerated(CheckForFormatStringIssues, SyntaxKind.InvocationExpression);
        }

        private static void CheckForFormatStringIssues(SyntaxNodeAnalysisContext analysisContext)
        {
            var invocation = (InvocationExpressionSyntax)analysisContext.Node;
            var methodSymbol = analysisContext.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

            if (methodSymbol == null || !methodSymbol.Parameters.Any())
            {
                return;
            }

            var currentMethodSignature = HandledFormatMethods
                .Where(hfm => methodSymbol.ContainingType.Is(hfm.ContainingType))
                .FirstOrDefault(method => method.Name == methodSymbol.Name);
            if (currentMethodSignature == null)
            {
                return;
            }

            var formatArgumentIndex = methodSymbol.Parameters[0].IsType(KnownType.System_IFormatProvider)
                ? 1 : 0;
            var formatStringExpression = invocation.ArgumentList.Arguments[formatArgumentIndex];

            var constValue = analysisContext.SemanticModel.GetConstantValue(formatStringExpression.Expression);
            if (!constValue.HasValue)
            {
                // can't check non-constant format strings
                return;
            }

            var failure = TryParseAndValidate((string)constValue.Value, invocation.ArgumentList,
                formatArgumentIndex, analysisContext.SemanticModel);
            if (failure == null ||
                (failure == ValidationFailure.SimpleString &&
                !currentMethodSignature.Name.EndsWith("Format")))
            {
                // Don't report on no failure or on methods without Format in the name if the string is a simple
                // string. For example, Console.Write("foo") is valid string.Format("foo") is not.
                return;
            }

            if (bugRelatedFailures.Contains(failure))
            {
                analysisContext.ReportDiagnostic(Diagnostic.Create(bugRule, invocation.Expression.GetLocation(),
                    failure.ToString()));
            }

            if (codeSmellRelatedFailures.Contains(failure))
            {
                analysisContext.ReportDiagnostic(Diagnostic.Create(codeSmellRule, invocation.Expression.GetLocation(),
                    failure.ToString()));
            }
        }

        private static ValidationFailure TryParseAndValidate(string formatStringText, ArgumentListSyntax argumentList,
            int formatArgumentIndex, SemanticModel semanticModel)
        {
            if (formatStringText == null)
            {
                return ValidationFailure.NullFormatString;
            }

            List<FormatStringItem> formatStringItems;
            return ExtractFormatItems(formatStringText, out formatStringItems) ??
                TryValidateFormatString(formatStringText, formatStringItems, argumentList, formatArgumentIndex,
                    semanticModel);
        }

        private static ValidationFailure ExtractFormatItems(string formatString,
            out List<FormatStringItem> formatStringItems)
        {
            formatStringItems = new List<FormatStringItem>();
            var curlyBraceCount = 0;
            StringBuilder currentFormatItemBuilder = null;
            var isEscapingOpenCurlyBrace = false;
            var isEscapingCloseCurlyBrace = false;
            for (int i = 0; i < formatString.Length; i++)
            {
                var currentChar = formatString[i];
                var previousChar = i > 0 ? formatString[i - 1] : '\0';

                if (currentChar == '{')
                {
                    if (previousChar == '{' && !isEscapingOpenCurlyBrace)
                    {
                        curlyBraceCount--;
                        isEscapingOpenCurlyBrace = true;
                        currentFormatItemBuilder = null;
                        continue;
                    }

                    curlyBraceCount++;
                    isEscapingOpenCurlyBrace = false;
                    if (currentFormatItemBuilder == null)
                    {
                        currentFormatItemBuilder = new StringBuilder();
                    }
                    continue;
                }

                if (previousChar == '{' && !char.IsDigit(currentChar) && currentFormatItemBuilder != null)
                {
                    return ValidationFailure.InvalidCharacterAfterOpenCurlyBrace;
                }

                if (currentChar == '}')
                {
                    isEscapingCloseCurlyBrace = previousChar == '}' && !isEscapingCloseCurlyBrace;
                    curlyBraceCount = isEscapingCloseCurlyBrace
                        ? curlyBraceCount + 1
                        : curlyBraceCount - 1;

                    if (currentFormatItemBuilder != null)
                    {
                        FormatStringItem formatStringItem;
                        var failure = TryParseItem(currentFormatItemBuilder.ToString(), out formatStringItem);
                        if (failure != null)
                        {
                            return failure;
                        }

                        formatStringItems.Add(formatStringItem);
                        currentFormatItemBuilder = null;
                    }
                    continue;
                }

                currentFormatItemBuilder?.Append(currentChar);
            }

            if (curlyBraceCount != 0)
            {
                return ValidationFailure.UnbalancedCurlyBraceCount;
            }

            return null;
        }

        private static ValidationFailure TryParseItem(string formatItem, out FormatStringItem formatStringItem)
        {
            formatStringItem = null;
            var indexOfComma = formatItem.IndexOf(',');
            var indexOfColon = formatItem.IndexOf(':');
            var split = formatItem.Split(',', ':');

            if (indexOfComma >= 0 && indexOfColon >= 0 && indexOfColon < indexOfComma ||
                split.Length > 3)
            {
                return ValidationFailure.FormatItemMalformed;
            }

            int index;
            int? alignment = null;
            string formatString = null;

            if (!int.TryParse(split[0], out index))
            {
                return ValidationFailure.FormatItemIndexIsNaN;
            }

            if (indexOfComma >= 0)
            {
                int localAlignment;
                if (!int.TryParse(split[1], out localAlignment))
                {
                    return ValidationFailure.FormatItemAlignmentIsNaN;
                }
                alignment = localAlignment;
            }

            if (indexOfColon >= 0)
            {
                formatString = indexOfComma >= 0
                    ? split[2]
                    : split[1];
            }

            formatStringItem = new FormatStringItem(index, alignment, formatString);
            return null;
        }

        private static ValidationFailure TryValidateFormatString(string formatStringText,
            ICollection<FormatStringItem> formatStringItems, ArgumentListSyntax argumentList, int formatArgumentIndex,
            SemanticModel semanticModel)
        {
            var formatArguments = argumentList.Arguments
                .Skip(formatArgumentIndex + 1)
                .Select(arg => FormatStringArgument.Create(arg.Expression, semanticModel))
                .ToList();
            var maxFormatItemIndex = formatStringItems.Max(item => (int?)item.Index);

            var realArgumentsCount = formatArguments.Count;
            if (formatArguments.Count == 1 &&
                formatArguments[0].TypeSymbol.Is(TypeKind.Array))
            {
                realArgumentsCount = formatArguments[0].ArraySize;
                if (realArgumentsCount == -1)
                {
                    // can't statically check the override that supplies args in an array variable
                    return null;
                }
            }

            return IsFormatValidSafetyNet(formatStringText) ??
                IsSimpleString(formatStringItems.Count, realArgumentsCount) ??
                HasFormatItemIndexTooBig(maxFormatItemIndex, realArgumentsCount) ??
                HasMissingFormatItemIndex(formatStringItems, maxFormatItemIndex) ??
                HasUnusedArguments(formatArguments, maxFormatItemIndex);
        }


        private static ValidationFailure IsFormatValidSafetyNet(string formatString)
        {
            try
            {
                var _ = string.Format(formatString, new object[1000000]);
                return null;
            }
            catch (FormatException)
            {
                return ValidationFailure.UnknownError;
            }
        }

        private static ValidationFailure HasFormatItemIndexTooBig(int? maxFormatItemIndex, int argumentsCount)
        {
            if (maxFormatItemIndex.HasValue &&
                maxFormatItemIndex.Value + 1 > argumentsCount)
            {
                return ValidationFailure.FormatItemIndexTooHigh;
            }

            return null;
        }

        private static ValidationFailure IsSimpleString(int formatStringItemsCount, int argumentsCount)
        {
            if (formatStringItemsCount == 0 && argumentsCount == 0)
            {
                return ValidationFailure.SimpleString;
            }

            return null;
        }

        private static ValidationFailure HasMissingFormatItemIndex(IEnumerable<FormatStringItem> formatStringItems,
            int? maxFormatItemIndex)
        {
            if (!maxFormatItemIndex.HasValue)
            {
                return null;
            }

            var missingFormatItemIndexes = Enumerable.Range(0, maxFormatItemIndex.Value + 1)
                .Except(formatStringItems.Select(item => item.Index))
                .Select(i => i.ToString())
                .ToList();

            if (missingFormatItemIndexes.Count > 0)
            {
                var failure = ValidationFailure.MissingFormatItemIndex;
                failure.AdditionalData = missingFormatItemIndexes;
                return failure;
            }

            return null;
        }

        private static ValidationFailure HasUnusedArguments(List<FormatStringArgument> formatArguments,
            int? maxFormatItemIndex)
        {
            var unusedArgumentNames = formatArguments.Skip((maxFormatItemIndex ?? -1) + 1)
                .Select(arg => arg.Name)
                .ToList();

            if (unusedArgumentNames.Count > 0)
            {
                var failure = ValidationFailure.UnusedFormatArguments;
                failure.AdditionalData = unusedArgumentNames;
                return failure;
            }

            return null;
        }

        public class ValidationFailure
        {
            public static readonly ValidationFailure NullFormatString =
                new ValidationFailure("Invalid string format, the format string cannot be null.");
            public static readonly ValidationFailure InvalidCharacterAfterOpenCurlyBrace =
                new ValidationFailure("Invalid string format, opening curly brace can only be followed by a digit or an opening curly brace.");
            public static readonly ValidationFailure UnbalancedCurlyBraceCount =
                new ValidationFailure("Invalid string format, unbalanced curly brace count.");
            public static readonly ValidationFailure FormatItemMalformed =
                new ValidationFailure("Invalid string format, all format items should comply with the following pattern '{index[,alignment][:formatString]}'.");
            public static readonly ValidationFailure FormatItemIndexIsNaN =
                new ValidationFailure("Invalid string format, all format item indexes should be numbers.");
            public static readonly ValidationFailure FormatItemAlignmentIsNaN =
                new ValidationFailure("Invalid string format, all format item alignments should be numbers.");
            public static readonly ValidationFailure FormatItemIndexTooHigh =
                new ValidationFailure("Invalid string format, the highest string format item index should not be greater than the arguments count.");
            public static readonly ValidationFailure SimpleString =
                new ValidationFailure("Remove this formatting call and simply use the input string.");
            public static readonly ValidationFailure UnknownError =
                new ValidationFailure("Invalid string format, the format string is invalid and is likely to throw at runtime.");
            public static readonly ValidationFailure MissingFormatItemIndex =
                new ValidationFailure("The format string might be wrong, the following item indexes are missing: ");
            public static readonly ValidationFailure UnusedFormatArguments =
                new ValidationFailure("The format string might be wrong, the following arguments are unused: ");

            private string message;
            private ValidationFailure(string message)
            {
                this.message = message;
            }

            public IEnumerable<string> AdditionalData { get; set; }

            public override string ToString()
            {
                return AdditionalData == null
                    ? message
                    : string.Concat(message, DiagnosticReportHelper.CreateStringFromArgs(AdditionalData), ".");
            }
        }

        private sealed class FormatStringItem
        {
            public FormatStringItem(int index, int? alignment, string formatString)
            {
                Index = index;
                Alignment = alignment;
                FormatString = formatString;
            }

            public int Index { get; }
            public int? Alignment { get; }
            public string FormatString { get; }
        }

        private sealed class FormatStringArgument
        {
            public FormatStringArgument(string name, ITypeSymbol typeSymbol, int arraySize = -1)
            {
                Name = name;
                TypeSymbol = typeSymbol;
                ArraySize = arraySize;
            }

            public static FormatStringArgument Create(ExpressionSyntax expression, SemanticModel semanticModel)
            {
                var type = semanticModel.GetTypeInfo(expression).Type;
                var arraySize = -1;
                if (type.Is(TypeKind.Array))
                {
                    var implicitArray = expression as ImplicitArrayCreationExpressionSyntax;
                    if (implicitArray != null)
                    {
                        arraySize = implicitArray.Initializer.Expressions.Count;
                    }

                    var array = expression as ArrayCreationExpressionSyntax;
                    if (array != null)
                    {
                        arraySize = array.Initializer.Expressions.Count;
                    }
                }

                return new FormatStringArgument(expression.ToString(), type, arraySize);
            }

            public string Name { get; }
            public ITypeSymbol TypeSymbol { get; }
            public int ArraySize { get; }
        }
    }
}