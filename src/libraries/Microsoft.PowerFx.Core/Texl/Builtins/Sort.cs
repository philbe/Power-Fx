﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.PowerFx.Core.App.ErrorContainers;
using Microsoft.PowerFx.Core.Binding;
using Microsoft.PowerFx.Core.Binding.BindInfo;
using Microsoft.PowerFx.Core.Entities;
using Microsoft.PowerFx.Core.Functions;
using Microsoft.PowerFx.Core.Functions.Delegation;
using Microsoft.PowerFx.Core.Functions.Delegation.DelegationMetadata;
using Microsoft.PowerFx.Core.Functions.FunctionArgValidators;
using Microsoft.PowerFx.Core.Localization;
using Microsoft.PowerFx.Core.Logging.Trackers;
using Microsoft.PowerFx.Core.Syntax;
using Microsoft.PowerFx.Core.Syntax.Nodes;
using Microsoft.PowerFx.Core.Types;
using Microsoft.PowerFx.Core.Utils;

namespace Microsoft.PowerFx.Core.Texl.Builtins
{
    // Sort(source:*, valueFunc:b, [order:s])
    internal sealed class SortFunction : FunctionWithTableInput
    {
        private readonly SortOrderValidator _sortOrderValidator;
        public override bool IsSelfContained => true;
        public override bool SupportsParamCoercion => false;

        public SortFunction()
            : base("Sort", TexlStrings.AboutSort, FunctionCategories.Table, DType.EmptyTable, 0x02, 2, 3, DType.EmptyTable)
        {
            ScopeInfo = new FunctionScopeInfo(this);
            _sortOrderValidator = ArgValidators.SortOrderValidator;
        }

        public override IEnumerable<TexlStrings.StringGetter[]> GetSignatures()
        {
            yield return new [] { TexlStrings.SortArg1, TexlStrings.SortArg2 };
            yield return new [] { TexlStrings.SortArg1, TexlStrings.SortArg2, TexlStrings.SortArg3 };
        }

        public override bool CheckInvocation(TexlBinding binding, TexlNode[] args, DType[] argTypes, IErrorContainer errors, out DType returnType, out Dictionary<TexlNode, DType> nodeToCoercedTypeMap)
        {
            Contracts.AssertValue(args);
            Contracts.AssertAllValues(args);
            Contracts.AssertValue(argTypes);
            Contracts.Assert(args.Length == argTypes.Length);
            Contracts.AssertValue(errors);
            Contracts.Assert(MinArity <= args.Length && args.Length <= MaxArity);

            bool fValid = base.CheckInvocation(args, argTypes, errors, out returnType, out nodeToCoercedTypeMap);
            Contracts.Assert(returnType.IsTable);

            returnType = argTypes[0];

            DType exprType = argTypes[1];
            if (!exprType.IsPrimitive || exprType.IsOptionSet)
            {
                fValid = false;
                errors.EnsureError(args[1], TexlStrings.ErrSortWrongType);
            }

            if (args.Length == 3 && argTypes[2] != DType.String)
            {
                fValid = false;
                errors.EnsureError(args[2], TexlStrings.ErrSortIncorrectOrder);
            }

            return fValid;
        }

        // This method returns true if there are special suggestions for a particular parameter of the function.
        public override bool HasSuggestionsForParam(int argumentIndex)
        {
            Contracts.Assert(0 <= argumentIndex);

            return argumentIndex == 0 || argumentIndex == 2;
        }

        private bool IsValidSortOrderNode(TexlNode node, SortOpMetadata metadata, TexlBinding binding, DPath columnPath)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValue(metadata);
            Contracts.AssertValue(binding);
            Contracts.AssertValid(columnPath);

            if (binding.IsAsync(node))
            {
                AddSuggestionMessageToTelemetry("Async sortorder node.", node, binding);
                DelegationTrackerCore.SetDelegationTrackerStatus(DelegationStatus.AsyncSortOrder, node, binding, this, DelegationTelemetryInfo.CreateEmptyDelegationTelemetryInfo());
                return false;
            }

            string sortOrder;
            switch (node.Kind)
            {
            case NodeKind.FirstName:
            case NodeKind.StrLit:
                return _sortOrderValidator.TryGetValidValue(node, binding, out sortOrder) &&
                    IsSortOrderSuppportedByColumn(node, binding, sortOrder, metadata, columnPath);
            case NodeKind.DottedName:
            case NodeKind.Call:
                if (_sortOrderValidator.TryGetValidValue(node, binding, out sortOrder) &&
                    IsSortOrderSuppportedByColumn(node, binding, sortOrder, metadata, columnPath))
                {
                    return true;
                }

                // If both ascending and descending are supported then we can support this.
                return IsSortOrderSuppportedByColumn(node, binding, LanguageConstants.DescendingSortOrderString, metadata, columnPath) &&
                    IsSortOrderSuppportedByColumn(node, binding, LanguageConstants.AscendingSortOrderString, metadata, columnPath);
            default:
                AddSuggestionMessageToTelemetry("Unsupported sortorder node kind.", node, binding);
                return false;
            }
        }

        public override bool IsServerDelegatable(CallNode callNode, TexlBinding binding)
        {
            Contracts.AssertValue(callNode);
            Contracts.AssertValue(binding);

            if (!CheckArgsCount(callNode, binding))
                return false;

            SortOpMetadata metadata = null;
            IDelegationMetadata delegationMetadata = null;
            IExternalDataSource dataSource;
            if (TryGetEntityMetadata(callNode, binding, out delegationMetadata))
            {
                if (!binding.Document.Properties.EnabledFeatures.IsEnhancedDelegationEnabled ||
                    !TryGetValidDataSourceForDelegation(callNode, binding, DelegationCapability.ArrayLookup, out _))
                {
                    SuggestDelegationHint(callNode, binding);
                    return false;
                }

                metadata = delegationMetadata.SortDelegationMetadata.VerifyValue();
            }
            else
            {
                if (!TryGetValidDataSourceForDelegation(callNode, binding, DelegationCapability.Sort, out dataSource))
                    return false;

                metadata = dataSource.DelegationMetadata.SortDelegationMetadata;
            }

            TexlNode[] args = callNode.Args.Children.VerifyValue();
            TexlNode arg1 = args[1].VerifyValue();

            // For now, we are only supporting delegation for Sort operations where second argument is column name.
            // For example, Sort(CDS, Value)
            FirstNameNode firstName = arg1.AsFirstName();
            if (firstName == null)
            {
                SuggestDelegationHint(arg1, binding);
                AddSuggestionMessageToTelemetry("Arg1 is not a FirstName node.", arg1, binding);
                DelegationTrackerCore.SetDelegationTrackerStatus(DelegationStatus.UnSupportedSortArg, arg1, binding, this, DelegationTelemetryInfo.CreateEmptyDelegationTelemetryInfo());
                return false;
            }

            FirstNameInfo firstNameInfo = binding.GetInfo(firstName);
            if (firstNameInfo == null)
                return false;

            DPath columnName = DPath.Root.Append(firstNameInfo.Name);
            if (!metadata.IsDelegationSupportedByColumn(columnName, DelegationCapability.Sort))
            {
                SuggestDelegationHint(firstName, binding);
                DelegationTrackerCore.SetDelegationTrackerStatus(DelegationStatus.NoDelSupportByColumn, firstName, binding, this, DelegationTelemetryInfo.CreateNoDelSupportByColumnTelemetryInfo(firstNameInfo));
                return false;
            }

            const string defaultSortOrder = LanguageConstants.AscendingSortOrderString;
            int cargs = args.Count();

            // Verify that the third argument (If present) is an Enum or string literal.
            if (cargs < 3 && IsSortOrderSuppportedByColumn(callNode, binding, defaultSortOrder, metadata, columnName))
                return true;

            // TASK: 6237100 - Binder: Propagate errors in subtree of the callnode to the call node itself
            // Only FirstName, DottedName and StrLit non-async nodes are supported for arg2.
            TexlNode arg2 = args[2].VerifyValue();
            if (!IsValidSortOrderNode(arg2, metadata, binding, columnName))
            {
                SuggestDelegationHint(arg2, binding);
                return false;
            }

            return true;
        }

        public static string GetSortComparatorIdForType(DType type)
        {
            Contracts.AssertValid(type);

            switch (type.Kind)
            {
                case DKind.Boolean:
                    return "0";
                case DKind.Number:
                case DKind.Color:
                case DKind.Currency:
                case DKind.Date:
                case DKind.Time:
                case DKind.DateTime:
                    return "1";
                case DKind.String:
                case DKind.Image:
                case DKind.PenImage:
                case DKind.Hyperlink:
                case DKind.Media:
                case DKind.Blob:
                default:
                    Contracts.Assert(DType.String.Accepts(type, exact: false));
                    return "2";
            }
        }

        internal static string GenerateColumnNamesMappingForSortByColumns(DType sourceType)
        {
            Contracts.Assert(sourceType.IsTable);

            var allColumns = sourceType.GetNames(DPath.Root);
            string separator = string.Empty;

            var primitiveColumnsAndComparatorIds = new StringBuilder();
            primitiveColumnsAndComparatorIds.Append("{");

            foreach (var column in allColumns)
            {
                if (column.Type.IsPrimitive && !column.Type.IsOptionSet)
                {
                    primitiveColumnsAndComparatorIds.AppendFormat("{0}\"{1}\":{2}", separator, CharacterUtils.EscapeString(column.Name),
                        GetSortComparatorIdForType(column.Type));
                    separator = ",";
                }
            }
            primitiveColumnsAndComparatorIds.Append("}");

            return primitiveColumnsAndComparatorIds.ToString();
        }

        private bool IsSortOrderSuppportedByColumn(TexlNode node, TexlBinding binding, string order, SortOpMetadata metadata, DPath columnPath)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValue(binding);
            Contracts.AssertValue(order);
            Contracts.AssertValue(metadata);
            Contracts.AssertValid(columnPath);

            var result = IsSortOrderSuppportedByColumn(order, metadata, columnPath);
            if (!result)
                DelegationTrackerCore.SetDelegationTrackerStatus(DelegationStatus.SortOrderNotSupportedByColumn, node, binding, this, DelegationTelemetryInfo.CreateEmptyDelegationTelemetryInfo());

            return result;
        }

        private bool IsSortOrderSuppportedByColumn(string order, SortOpMetadata metadata, DPath columnPath)
        {
            Contracts.AssertValue(order);
            Contracts.AssertValue(metadata);
            Contracts.AssertValid(columnPath);

            order = order.ToLower();
            // If column is marked as ascending only then return false if order requested is descending.
            return order != LanguageConstants.DescendingSortOrderString || !metadata.IsColumnAscendingOnly(columnPath);
        }
    }
}
