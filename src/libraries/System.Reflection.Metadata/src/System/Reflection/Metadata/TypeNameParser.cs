﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;

#if !SYSTEM_PRIVATE_CORELIB
using System.Collections.Immutable;
#endif

using static System.Reflection.Metadata.TypeNameParserHelpers;

namespace System.Reflection.Metadata
{
    [DebuggerDisplay("{_inputString}")]
    internal ref struct TypeNameParser
    {
        private static readonly TypeNameParseOptions s_defaults = new();

        private readonly bool _throwOnError;
        private readonly TypeNameParseOptions _parseOptions;
        private ReadOnlySpan<char> _inputString;

        private TypeNameParser(ReadOnlySpan<char> name, bool throwOnError, TypeNameParseOptions? options) : this()
        {
            _inputString = name;
            _throwOnError = throwOnError;
            _parseOptions = options ?? s_defaults;
        }

        internal static TypeName? Parse(ReadOnlySpan<char> typeName, bool throwOnError, TypeNameParseOptions? options = default)
        {
            ReadOnlySpan<char> trimmedName = typeName.TrimStart(); // whitespaces at beginning are always OK
            if (trimmedName.IsEmpty)
            {
                if (throwOnError)
                {
                    ThrowArgumentException_InvalidTypeName(errorIndex: 0); // whitespace input needs to report the error index as 0
                }

                return null;
            }

            int recursiveDepth = 0;
            TypeNameParser parser = new(trimmedName, throwOnError, options);
            TypeName? parsedName = parser.ParseNextTypeName(allowFullyQualifiedName: true, ref recursiveDepth);

            if (parsedName is null || !parser._inputString.IsEmpty) // unconsumed input == error
            {
                if (throwOnError)
                {
                    if (IsMaxDepthExceeded(parser._parseOptions, recursiveDepth))
                    {
                        ThrowInvalidOperation_MaxNodesExceeded(parser._parseOptions.MaxNodes);
                    }

                    int errorIndex = typeName.Length - parser._inputString.Length;
                    ThrowArgumentException_InvalidTypeName(errorIndex);
                }

                return null;
            }

            Debug.Assert(parsedName.GetNodeCount() == recursiveDepth, $"Node count mismatch for '{typeName.ToString()}'");

            return parsedName;
        }

        // this method should return null instead of throwing, so the caller can get errorIndex and include it in error msg
        private TypeName? ParseNextTypeName(bool allowFullyQualifiedName, ref int recursiveDepth)
        {
            if (!TryDive(_parseOptions, ref recursiveDepth))
            {
                return null;
            }

            List<int>? nestedNameLengths = null;
            if (!TryGetTypeNameInfo(_parseOptions, ref _inputString, ref nestedNameLengths, ref recursiveDepth, out int fullTypeNameLength))
            {
                return null;
            }

            // At this point, we have performed O(fullTypeNameLength) total work.

            ReadOnlySpan<char> fullTypeName = _inputString.Slice(0, fullTypeNameLength);
            _inputString = _inputString.Slice(fullTypeNameLength);

            // Don't allocate now, as it may be an open generic type like "Name`1"
#if SYSTEM_PRIVATE_CORELIB
            List<TypeName>? genericArgs = null;
#else
            ImmutableArray<TypeName>.Builder? genericArgs = null;
#endif

            // Are there any captured generic args? We'll look for "[[" and "[".
            // There are no spaces allowed before the first '[', but spaces are allowed
            // after that. The check slices _inputString, so we'll capture it into
            // a local so we can restore it later if needed.
            ReadOnlySpan<char> capturedBeforeProcessing = _inputString;
            if (IsBeginningOfGenericArgs(ref _inputString, out bool doubleBrackets))
            {
            ParseAnotherGenericArg:

                // Namespace.Type`2[[GenericArgument1, AssemblyName1],[GenericArgument2, AssemblyName2]] - double square bracket syntax allows for fully qualified type names
                // Namespace.Type`2[GenericArgument1,GenericArgument2] - single square bracket syntax is legal only for non-fully qualified type names
                // Namespace.Type`2[[GenericArgument1, AssemblyName1], GenericArgument2] - mixed mode
                // Namespace.Type`2[GenericArgument1, [GenericArgument2, AssemblyName2]] - mixed mode
                TypeName? genericArg = ParseNextTypeName(allowFullyQualifiedName: doubleBrackets, ref recursiveDepth);
                if (genericArg is null) // parsing failed
                {
                    return null;
                }

                // For [[, there had better be a ']' after the type name.
                if (doubleBrackets && !TryStripFirstCharAndTrailingSpaces(ref _inputString, ']'))
                {
                    return null;
                }

                if (genericArgs is null)
                {
#if SYSTEM_PRIVATE_CORELIB
                    genericArgs = new List<TypeName>(2);
#else
                    genericArgs = ImmutableArray.CreateBuilder<TypeName>(2);
#endif
                }
                genericArgs.Add(genericArg);

                // Is there a ',[' indicating fully qualified generic type arg?
                // Is there a ',' indicating non-fully qualified generic type arg?
                if (TryStripFirstCharAndTrailingSpaces(ref _inputString, ','))
                {
                    doubleBrackets = TryStripFirstCharAndTrailingSpaces(ref _inputString, '[');

                    goto ParseAnotherGenericArg;
                }

                // The only other allowable character is ']', indicating the end of
                // the generic type arg list.
                if (!TryStripFirstCharAndTrailingSpaces(ref _inputString, ']'))
                {
                    return null;
                }
            }

            // At this point, we may have performed O(fullTypeNameLength + _inputString.Length) total work.
            // This will be the case if there was whitespace after the full type name in the original input
            // string. We could end up looking at these same whitespace chars again later in this method,
            // such as when parsing decorators. We rely on the TryDive routine to limit the total number
            // of times we might inspect the same character.

            // If there was an error stripping the generic args, back up to
            // before we started processing them, and let the decorator
            // parser try handling it.
            if (genericArgs is null)
            {
                _inputString = capturedBeforeProcessing;
            }
            else
            {
                // Every constructed generic type needs the generic type definition.
                if (!TryDive(_parseOptions, ref recursiveDepth))
                {
                    return null;
                }
                // If that generic type is a nested type, we don't increase the recursiveDepth any further,
                // as generic type definition uses exactly the same declaring type as the constructed generic type.
            }

            int previousDecorator = default;
            // capture the current state so we can reprocess it again once we know the AssemblyName
            capturedBeforeProcessing = _inputString;
            // iterate over the decorators to ensure there are no illegal combinations
            while (TryParseNextDecorator(ref _inputString, out int parsedDecorator))
            {
                if (!TryDive(_parseOptions, ref recursiveDepth))
                {
                    return null;
                }

                // Currently it's illegal for managed reference to be followed by any other decorator,
                // but this is a runtime-specific behavior and the parser is not enforcing that rule.
                previousDecorator = parsedDecorator;
            }

            AssemblyNameInfo? assemblyName = null;
            if (allowFullyQualifiedName && !TryParseAssemblyName(ref assemblyName))
            {
#if SYSTEM_PRIVATE_CORELIB
                // Backward compatibility: throw for non-empty invalid assembly names.
                if (!_inputString.TrimStart().StartsWith(","))
                {
                    // Reject attempt to provide top-level assembly name to Assembly.GetType
                    if (_parseOptions.IsAssemblyGetType)
                    {
                        if (_throwOnError)
                        {
                            throw new ArgumentException(SR.Argument_AssemblyGetTypeCannotSpecifyAssembly);
                        }
                        return null;
                    }

                    // Otherwise, no matter what throwOnError is set to, we throw FileLoadException for invalid assembly names.
                    throw new IO.FileLoadException(SR.InvalidAssemblyName, _inputString.ToString());
                }
#endif
                return null;
            }

            // No matter what was parsed, the full name string is allocated only once.
            // In case of generic, nested, array, pointer and byref types the full name is allocated
            // when needed for the first time .
            string fullName = fullTypeName.ToString();

            TypeName? declaringType = GetDeclaringType(fullName, nestedNameLengths, assemblyName);
            TypeName result = new(fullName, assemblyName, declaringType: declaringType);
            if (genericArgs is not null)
            {
                result = new(fullName: null, assemblyName, elementOrGenericType: result, declaringType, genericArgs);
            }

            // The loop below is protected by the dive check during the first decorator pass prior
            // to assembly name parsing above.

            if (previousDecorator != default) // some decorators were recognized
            {
                while (TryParseNextDecorator(ref capturedBeforeProcessing, out int parsedModifier))
                {
                    result = new(fullName: null, assemblyName, elementOrGenericType: result, rankOrModifier: parsedModifier);
                }
            }

            return result;
        }

        /// <returns>false means the input was invalid and parsing has failed. Empty input is valid and returns true.</returns>
        private bool TryParseAssemblyName(ref AssemblyNameInfo? assemblyName)
        {
            ReadOnlySpan<char> capturedBeforeProcessing = _inputString;
            if (TryStripFirstCharAndTrailingSpaces(ref _inputString, ','))
            {
                if (_inputString.IsEmpty)
                {
                    _inputString = capturedBeforeProcessing; // restore the state
                    return false;
                }

                ReadOnlySpan<char> candidate = GetAssemblyNameCandidate(_inputString);
                if (!AssemblyNameInfo.TryParse(candidate, out assemblyName))
                {
                    return false;
                }

                _inputString = _inputString.Slice(candidate.Length);
                return true;
            }

            return true;
        }

        private static TypeName? GetDeclaringType(string fullTypeName, List<int>? nestedNameLengths, AssemblyNameInfo? assemblyName)
        {
            if (nestedNameLengths is null)
            {
                return null;
            }

            // The loop below is protected by the dive check in GetFullTypeNameLength.

            TypeName? declaringType = null;
            int nameOffset = 0;
            foreach (int nestedNameLength in nestedNameLengths)
            {
                Debug.Assert(nestedNameLength > 0, "TryGetTypeNameInfo should return error on zero lengths");
                int fullNameLength = nameOffset + nestedNameLength;
                declaringType = new(fullTypeName, assemblyName, declaringType: declaringType, nestedNameLength: fullNameLength);
                nameOffset += nestedNameLength + 1; // include the '+' that was skipped in name
            }

            return declaringType;
        }
    }
}
