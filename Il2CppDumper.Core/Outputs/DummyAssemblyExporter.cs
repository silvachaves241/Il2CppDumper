using System.Collections.Generic;
using System.IO;
using Mono.Cecil;

namespace Il2CppDumper
{
    public static class DummyAssemblyExporter
    {
        public static List<string> Export(Il2CppExecutor il2CppExecutor, string outputDir, bool addToken, ICollection<string> writtenFiles = null)
        {
            var written = new List<string>();
            var dummyDllDirectory = Path.Combine(outputDir, "DummyDll");
            if (Directory.Exists(dummyDllDirectory))
                Directory.Delete(dummyDllDirectory, true);
            Directory.CreateDirectory(dummyDllDirectory);
            var dummy = new DummyAssemblyGenerator(il2CppExecutor, addToken);
            foreach (var assembly in dummy.Assemblies)
            {
                SanitizeConstants(assembly);
                SanitizeMemberReferences(assembly);
                SanitizeCustomAttributes(assembly);
                var targetFile = Path.Combine(dummyDllDirectory, assembly.MainModule.Name);
                File.WriteAllBytes(targetFile, WriteAssembly(assembly, WriteAssemblyToBytes, customAttributesSanitized: true));
                written.Add(targetFile);
                writtenFiles?.Add(targetFile);
            }
            return written;
        }

        internal const int MaxCustomAttributeWriteProbeAttempts = 64;

        internal static byte[] WriteAssembly(AssemblyDefinition assembly)
        {
            return WriteAssembly(assembly, WriteAssemblyToBytes, customAttributesSanitized: false);
        }

        internal static byte[] WriteAssembly(AssemblyDefinition assembly, System.Func<AssemblyDefinition, byte[]> writer, bool customAttributesSanitized = false)
        {
            try
            {
                return writer(assembly);
            }
            catch (System.Exception ex) when (IsCustomAttributeWriteException(ex))
            {
                return WriteAssemblyIsolatingFailingCustomAttributes(assembly, ex.Message, writer, customAttributesSanitized);
            }
        }

        private static byte[] WriteAssemblyToBytes(AssemblyDefinition assembly)
        {
            using var stream = new MemoryStream();
            assembly.Write(stream);
            return stream.ToArray();
        }

        private static bool IsCustomAttributeWriteException(System.Exception ex)
        {
            return ex.StackTrace?.Contains("WriteCustomAttribute") == true
                || ex.StackTrace?.Contains("GetCustomAttributeSignature") == true
                || ex.StackTrace?.Contains("AddCustomAttributes") == true;
        }

        private readonly struct CustomAttributeEntry
        {
            public CustomAttributeEntry(string owner, Mono.Collections.Generic.Collection<CustomAttribute> collection, CustomAttribute attribute)
            {
                Owner = owner;
                Collection = collection;
                Attribute = attribute;
            }

            public string Owner { get; }
            public Mono.Collections.Generic.Collection<CustomAttribute> Collection { get; }
            public CustomAttribute Attribute { get; }
        }

        private static byte[] WriteAssemblyIsolatingFailingCustomAttributes(
            AssemblyDefinition assembly,
            string initialReason,
            System.Func<AssemblyDefinition, byte[]> writer,
            bool customAttributesSanitized)
        {
            if (!customAttributesSanitized)
            {
                SanitizeCustomAttributes(assembly);
                try
                {
                    return writer(assembly);
                }
                catch (System.Exception ex) when (IsCustomAttributeWriteException(ex))
                {
                    initialReason = ex.Message;
                }
            }

            var assemblyName = GetAssemblyName(assembly.MainModule);
            var entries = new List<CustomAttributeEntry>();
            foreach (var (owner, collection) in EnumerateCustomAttributeCollections(assembly))
            {
                for (var i = 0; i < collection.Count; i++)
                {
                    var attr = collection[i];
                    if (!IsIl2CppDumperAttribute(attr))
                    {
                        entries.Add(new CustomAttributeEntry(owner, collection, attr));
                    }
                }
                StripCustomAttributes(collection);
            }

            // Verify that the base assembly (without restored custom attributes) is writable before attributing failures to candidates.
            var baselineBytes = writer(assembly);
            if (entries.Count == 0)
            {
                return baselineBytes;
            }

            var remainingBudget = MaxCustomAttributeWriteProbeAttempts;
            byte[] lastSuccessfulBytes = baselineBytes;
            var pendingRanges = new Queue<(int Start, int Count, string Reason)>();
            pendingRanges.Enqueue((0, entries.Count, initialReason));

            while (pendingRanges.Count > 0)
            {
                if (remainingBudget <= 0)
                {
                    var pendingAttributeCount = 0;
                    var pendingRangeCount = pendingRanges.Count;
                    var lastReason = initialReason;
                    while (pendingRanges.Count > 0)
                    {
                        var pending = pendingRanges.Dequeue();
                        pendingAttributeCount += pending.Count;
                        if (!string.IsNullOrWhiteSpace(pending.Reason))
                        {
                            lastReason = pending.Reason;
                        }
                    }

                    throw new System.InvalidOperationException(
                        $"Inconclusive custom attribute write recovery for assembly '{assemblyName}': probe limit ({MaxCustomAttributeWriteProbeAttempts}) reached with {pendingAttributeCount} pending custom attribute(s) across {pendingRangeCount} batch(es). Last writer failure: {lastReason}");
                }

                var (start, count, fallbackReason) = pendingRanges.Dequeue();
                if (count <= 0)
                {
                    continue;
                }

                for (var i = 0; i < count; i++)
                {
                    var entry = entries[start + i];
                    entry.Collection.Add(entry.Attribute);
                }

                remainingBudget--;
                try
                {
                    lastSuccessfulBytes = writer(assembly);
                }
                catch (System.Exception batchEx) when (IsCustomAttributeWriteException(batchEx))
                {
                    for (var i = count - 1; i >= 0; i--)
                    {
                        var collection = entries[start + i].Collection;
                        collection.RemoveAt(collection.Count - 1);
                    }

                    var reason = !string.IsNullOrWhiteSpace(batchEx.Message) ? batchEx.Message : fallbackReason;
                    if (count == 1)
                    {
                        var failing = entries[start];
                        ReportOmittedCustomAttribute(assemblyName, failing.Owner, GetCustomAttributeTypeName(failing.Attribute), reason);
                        continue;
                    }

                    var mid = count / 2;
                    pendingRanges.Enqueue((start, mid, reason));
                    pendingRanges.Enqueue((start + mid, count - mid, reason));
                }
            }

            return lastSuccessfulBytes;
        }

        private static IEnumerable<(string Owner, Mono.Collections.Generic.Collection<CustomAttribute> Attributes)> EnumerateCustomAttributeCollections(AssemblyDefinition assembly)
        {
            var module = assembly.MainModule;
            var assemblyName = GetAssemblyName(module);
            yield return (assemblyName, assembly.CustomAttributes);
            yield return (module.Name ?? assemblyName, module.CustomAttributes);
            foreach (var type in GetAllTypes(module.Types))
            {
                yield return (type.FullName, type.CustomAttributes);
                foreach (var (gpOwner, gpAttributes) in EnumerateGenericParameterCustomAttributeCollections(type.FullName, type.GenericParameters))
                {
                    yield return (gpOwner, gpAttributes);
                }
                foreach (var field in type.Fields)
                {
                    yield return ($"{type.FullName}.{field.Name}", field.CustomAttributes);
                }
                foreach (var method in type.Methods)
                {
                    var methodOwner = $"{type.FullName}.{method.Name}";
                    yield return (methodOwner, method.CustomAttributes);
                    yield return ($"{methodOwner}:return", method.MethodReturnType.CustomAttributes);
                    foreach (var (gpOwner, gpAttributes) in EnumerateGenericParameterCustomAttributeCollections(methodOwner, method.GenericParameters))
                    {
                        yield return (gpOwner, gpAttributes);
                    }
                    foreach (var parameter in method.Parameters)
                    {
                        yield return ($"{methodOwner}({parameter.Name})", parameter.CustomAttributes);
                    }
                }
                foreach (var property in type.Properties)
                {
                    yield return ($"{type.FullName}.{property.Name}", property.CustomAttributes);
                }
                foreach (var @event in type.Events)
                {
                    yield return ($"{type.FullName}.{@event.Name}", @event.CustomAttributes);
                }
            }
        }

        private static IEnumerable<(string Owner, Mono.Collections.Generic.Collection<CustomAttribute> Attributes)> EnumerateGenericParameterCustomAttributeCollections(
            string parentOwner,
            Mono.Collections.Generic.Collection<GenericParameter> genericParameters)
        {
            foreach (var genericParameter in genericParameters)
            {
                var gpOwner = $"{parentOwner}.<{genericParameter.Name}>";
                yield return (gpOwner, genericParameter.CustomAttributes);
                foreach (var constraint in genericParameter.Constraints)
                {
                    yield return (gpOwner, constraint.CustomAttributes);
                }
            }
        }

        private static void StripCustomAttributes(Mono.Collections.Generic.Collection<CustomAttribute> customAttributes)
        {
            for (var i = customAttributes.Count - 1; i >= 0; i--)
            {
                if (!IsIl2CppDumperAttribute(customAttributes[i]))
                {
                    customAttributes.RemoveAt(i);
                }
            }
        }

        private static bool IsIl2CppDumperAttribute(CustomAttribute customAttribute)
        {
            var declaringType = customAttribute.Constructor?.DeclaringType;
            return declaringType?.Namespace == "Il2CppDummyDll"
                || declaringType?.Scope?.Name == "Il2CppDummyDll.dll";
        }

        internal static void SanitizeMemberReferences(AssemblyDefinition assembly)
        {
            var module = assembly.MainModule;
            foreach (var type in GetAllTypes(module.Types))
            {
                type.BaseType = ImportTypeReference(module, type.BaseType);
                SanitizeGenericParameterConstraints(module, type.GenericParameters);
                foreach (var @interface in type.Interfaces)
                {
                    @interface.InterfaceType = ImportTypeReference(module, @interface.InterfaceType);
                }
                foreach (var field in type.Fields)
                {
                    field.FieldType = ImportTypeReference(module, field.FieldType);
                }
                foreach (var method in type.Methods)
                {
                    method.ReturnType = ImportTypeReference(module, method.ReturnType);
                    SanitizeGenericParameterConstraints(module, method.GenericParameters);
                    for (var i = 0; i < method.Overrides.Count; i++)
                    {
                        method.Overrides[i] = ImportMethodReference(module, method.Overrides[i]);
                    }
                    foreach (var parameter in method.Parameters)
                    {
                        parameter.ParameterType = ImportTypeReference(module, parameter.ParameterType);
                    }
                    if (method.HasBody)
                    {
                        foreach (var variable in method.Body.Variables)
                        {
                            variable.VariableType = ImportTypeReference(module, variable.VariableType);
                        }
                        foreach (var instruction in method.Body.Instructions)
                        {
                            switch (instruction.Operand)
                            {
                                case TypeReference typeReference:
                                    instruction.Operand = ImportTypeReference(module, typeReference);
                                    break;
                                case MethodReference methodReference:
                                    instruction.Operand = ImportMethodReference(module, methodReference);
                                    break;
                                case FieldReference fieldReference:
                                    instruction.Operand = ImportFieldReference(module, fieldReference);
                                    break;
                                case CallSite callSite:
                                    SanitizeCallSite(module, callSite);
                                    break;
                            }
                        }
                    }
                }
                foreach (var property in type.Properties)
                {
                    property.PropertyType = ImportTypeReference(module, property.PropertyType);
                    foreach (var parameter in property.Parameters)
                    {
                        parameter.ParameterType = ImportTypeReference(module, parameter.ParameterType);
                    }
                }
                foreach (var @event in type.Events)
                {
                    @event.EventType = ImportTypeReference(module, @event.EventType);
                }
            }
        }

        private static void SanitizeGenericParameterConstraints(ModuleDefinition module, Mono.Collections.Generic.Collection<GenericParameter> genericParameters)
        {
            foreach (var genericParameter in genericParameters)
            {
                foreach (var constraint in genericParameter.Constraints)
                {
                    constraint.ConstraintType = ImportTypeReference(module, constraint.ConstraintType);
                }
            }
        }

        private static void SanitizeCallSite(ModuleDefinition module, CallSite callSite)
        {
            callSite.ReturnType = ImportTypeReference(module, callSite.ReturnType);
            foreach (var parameter in callSite.Parameters)
            {
                parameter.ParameterType = ImportTypeReference(module, parameter.ParameterType);
            }
        }

        internal static void SanitizeCustomAttributes(AssemblyDefinition assembly)
        {
            var module = assembly.MainModule;
            foreach (var type in GetAllTypes(module.Types))
            {
                SanitizeGenericParameterConstraints(module, type.GenericParameters);
                foreach (var method in type.Methods)
                {
                    SanitizeGenericParameterConstraints(module, method.GenericParameters);
                }
            }
            foreach (var (owner, attributes) in EnumerateCustomAttributeCollections(assembly))
            {
                ImportCustomAttributes(module, attributes, owner);
            }
        }

        private static void ImportCustomAttributes(ModuleDefinition module, Mono.Collections.Generic.Collection<CustomAttribute> customAttributes, string owner)
        {
            if (module == null)
            {
                return;
            }
            var assemblyName = GetAssemblyName(module);
            for (var attributeIndex = customAttributes.Count - 1; attributeIndex >= 0; attributeIndex--)
            {
                var customAttribute = customAttributes[attributeIndex];
                try
                {
                    if (customAttribute.Constructor == null)
                    {
                        throw new System.InvalidOperationException("Custom attribute constructor cannot be null.");
                    }
                    customAttribute.Constructor = ImportMethodReference(module, customAttribute.Constructor);
                    if (customAttribute.Constructor.Parameters.Count != customAttribute.ConstructorArguments.Count)
                    {
                        throw new System.InvalidOperationException(
                            $"Custom attribute constructor argument count ({customAttribute.ConstructorArguments.Count}) does not match parameter count ({customAttribute.Constructor.Parameters.Count}).");
                    }
                    for (var i = 0; i < customAttribute.ConstructorArguments.Count; i++)
                    {
                        var expectedParameterType = ImportTypeReference(module, customAttribute.Constructor.Parameters[i].ParameterType);
                        customAttribute.ConstructorArguments[i] = ImportCustomAttributeArgument(module, customAttribute.ConstructorArguments[i], expectedParameterType);
                    }
                    for (var i = 0; i < customAttribute.Fields.Count; i++)
                    {
                        var field = customAttribute.Fields[i];
                        if (string.IsNullOrWhiteSpace(field.Name))
                        {
                            throw new System.InvalidOperationException("Custom attribute named field argument name cannot be null or empty.");
                        }
                        customAttribute.Fields[i] = new CustomAttributeNamedArgument(field.Name, ImportCustomAttributeArgument(module, field.Argument));
                    }
                    for (var i = 0; i < customAttribute.Properties.Count; i++)
                    {
                        var property = customAttribute.Properties[i];
                        if (string.IsNullOrWhiteSpace(property.Name))
                        {
                            throw new System.InvalidOperationException("Custom attribute named property argument name cannot be null or empty.");
                        }
                        customAttribute.Properties[i] = new CustomAttributeNamedArgument(property.Name, ImportCustomAttributeArgument(module, property.Argument));
                    }
                }
                catch (System.Exception ex)
                {
                    customAttributes.RemoveAt(attributeIndex);
                    ReportOmittedCustomAttribute(assemblyName, owner, GetCustomAttributeTypeName(customAttribute), ex.Message);
                }
            }
        }

        internal static void ReportOmittedCustomAttribute(string assemblyName, string owner, string attributeType, string reason)
        {
            CoreLogger.LogWarning($"WARNING: Omitted custom attribute '{attributeType}' on '{owner}' in assembly '{assemblyName}': {reason}");
        }

        private static string GetAssemblyName(ModuleDefinition module)
        {
            return module?.Assembly?.Name?.Name
                ?? (!string.IsNullOrWhiteSpace(module?.Name) ? module.Name : "UnknownAssembly");
        }

        private static string GetCustomAttributeTypeName(CustomAttribute customAttribute)
        {
            return customAttribute?.AttributeType?.FullName
                ?? customAttribute?.Constructor?.DeclaringType?.FullName
                ?? "UnknownAttribute";
        }

        private static CustomAttributeArgument ImportCustomAttributeArgument(
            ModuleDefinition module,
            CustomAttributeArgument argument,
            TypeReference expectedType = null)
        {
            var argumentType = ImportTypeReference(module, argument.Type);
            if (expectedType != null && argumentType != null && expectedType.FullName != argumentType.FullName)
            {
                throw new System.InvalidOperationException(
                    $"Custom attribute argument type '{argumentType.FullName}' does not match expected constructor parameter type '{expectedType.FullName}'.");
            }

            var type = expectedType ?? argumentType;
            var value = argument.Value;
            switch (value)
            {
                case TypeReference typeReference:
                    value = ImportTypeReference(module, typeReference);
                    break;
                case CustomAttributeArgument customAttributeArgument:
                    value = ImportCustomAttributeArgument(module, customAttributeArgument);
                    break;
                case CustomAttributeArgument[] customAttributeArguments:
                    var expectedElementType = (type as ArrayType)?.ElementType;
                    var importedArguments = new CustomAttributeArgument[customAttributeArguments.Length];
                    for (var i = 0; i < customAttributeArguments.Length; i++)
                    {
                        var elementExpectedType = expectedElementType != null && expectedElementType.MetadataType != MetadataType.Object && expectedElementType.FullName != "System.Object"
                            ? expectedElementType
                            : null;
                        importedArguments[i] = ImportCustomAttributeArgument(module, customAttributeArguments[i], elementExpectedType);
                    }
                    value = importedArguments;
                    break;
            }
            value = NormalizeCustomAttributeValue(type, value);
            return new CustomAttributeArgument(type, value);
        }

        private static object NormalizeCustomAttributeValue(TypeReference type, object value)
        {
            if (type == null)
            {
                throw new System.InvalidOperationException("Custom attribute argument type cannot be null.");
            }
            if (type is ArrayType)
            {
                if (value != null && value is not CustomAttributeArgument[])
                {
                    throw new System.InvalidOperationException($"Invalid array custom attribute value for type '{type.FullName}'.");
                }
                return value;
            }
            if (type.FullName == "System.Type")
            {
                if (value != null && value is not TypeReference)
                {
                    throw new System.InvalidOperationException($"Invalid System.Type custom attribute argument value '{value}'.");
                }
                return value;
            }
            if (type.MetadataType == MetadataType.Object || type.FullName == "System.Object")
            {
                if (value is not CustomAttributeArgument)
                {
                    throw new System.InvalidOperationException($"Boxed custom attribute argument for '{type.FullName}' must wrap a CustomAttributeArgument.");
                }
                return value;
            }
            if (DummyAssemblyGenerator.TryGetEnumUnderlyingType(type, out var underlyingType))
            {
                if (!DummyAssemblyGenerator.TryNormalizeConstant(type, value, out var normalizedEnum))
                {
                    throw new System.InvalidOperationException(
                        $"Invalid enum custom attribute argument value '{value ?? "null"}' for enum '{type.FullName}' (underlying type '{underlyingType.FullName}').");
                }
                return normalizedEnum;
            }
            if (DummyAssemblyGenerator.CanWriteConstant(type))
            {
                if (!DummyAssemblyGenerator.TryNormalizeConstant(type, value, out var normalizedPrimitive))
                {
                    throw new System.InvalidOperationException(
                        $"Invalid custom attribute argument value '{value ?? "null"}' for type '{type.FullName}'.");
                }
                return normalizedPrimitive;
            }
            throw new System.InvalidOperationException(
                $"Unable to resolve valid enum underlying type for custom attribute argument type '{type.FullName}'.");
        }

        private static TypeReference ImportTypeReference(ModuleDefinition module, TypeReference typeReference)
        {
            if (typeReference == null || typeReference.IsGenericParameter)
            {
                return typeReference;
            }
            switch (typeReference)
            {
                case GenericInstanceType genericInstanceType:
                    var importedGenericInstanceType = new GenericInstanceType(ImportTypeReference(module, genericInstanceType.ElementType));
                    CopyIsValueType(importedGenericInstanceType, genericInstanceType);
                    foreach (var genericArgument in genericInstanceType.GenericArguments)
                    {
                        importedGenericInstanceType.GenericArguments.Add(ImportTypeReference(module, genericArgument));
                    }
                    return importedGenericInstanceType;
                case ArrayType arrayType:
                    var importedArrayType = new ArrayType(ImportTypeReference(module, arrayType.ElementType), arrayType.Rank);
                    CopyIsValueType(importedArrayType, arrayType);
                    importedArrayType.Dimensions.Clear();
                    foreach (var dimension in arrayType.Dimensions)
                    {
                        importedArrayType.Dimensions.Add(new ArrayDimension(dimension.LowerBound, dimension.UpperBound));
                    }
                    return importedArrayType;
                case ByReferenceType byReferenceType:
                    var importedByReferenceType = new ByReferenceType(ImportTypeReference(module, byReferenceType.ElementType));
                    CopyIsValueType(importedByReferenceType, byReferenceType);
                    return importedByReferenceType;
                case PointerType pointerType:
                    var importedPointerType = new PointerType(ImportTypeReference(module, pointerType.ElementType));
                    CopyIsValueType(importedPointerType, pointerType);
                    return importedPointerType;
                case PinnedType pinnedType:
                    var importedPinnedType = new PinnedType(ImportTypeReference(module, pinnedType.ElementType));
                    CopyIsValueType(importedPinnedType, pinnedType);
                    return importedPinnedType;
                case OptionalModifierType optionalModifierType:
                    var importedOptionalModifierType = new OptionalModifierType(
                        ImportTypeReference(module, optionalModifierType.ModifierType),
                        ImportTypeReference(module, optionalModifierType.ElementType));
                    CopyIsValueType(importedOptionalModifierType, optionalModifierType);
                    return importedOptionalModifierType;
                case RequiredModifierType requiredModifierType:
                    var importedRequiredModifierType = new RequiredModifierType(
                        ImportTypeReference(module, requiredModifierType.ModifierType),
                        ImportTypeReference(module, requiredModifierType.ElementType));
                    CopyIsValueType(importedRequiredModifierType, requiredModifierType);
                    return importedRequiredModifierType;
                case SentinelType sentinelType:
                    var importedSentinelType = new SentinelType(ImportTypeReference(module, sentinelType.ElementType));
                    CopyIsValueType(importedSentinelType, sentinelType);
                    return importedSentinelType;
                case FunctionPointerType functionPointerType:
                    var importedFunctionPointerType = new FunctionPointerType
                    {
                        HasThis = functionPointerType.HasThis,
                        ExplicitThis = functionPointerType.ExplicitThis,
                        CallingConvention = functionPointerType.CallingConvention,
                        ReturnType = ImportTypeReference(module, functionPointerType.ReturnType)
                    };
                    CopyIsValueType(importedFunctionPointerType, functionPointerType);
                    foreach (var parameter in functionPointerType.Parameters)
                    {
                        importedFunctionPointerType.Parameters.Add(new ParameterDefinition(
                            parameter.Name,
                            parameter.Attributes,
                            ImportTypeReference(module, parameter.ParameterType)));
                    }
                    return importedFunctionPointerType;
            }
            return typeReference.Module == module ? typeReference : module.ImportReference(typeReference);
        }

        private static void CopyIsValueType(TypeReference target, TypeReference source)
        {
            try
            {
                target.IsValueType = source.IsValueType;
            }
            catch (System.InvalidOperationException)
            {
            }
            catch (System.NotSupportedException)
            {
            }
        }

        private static MethodReference ImportMethodReference(ModuleDefinition module, MethodReference methodReference)
        {
            if (methodReference == null)
            {
                return null;
            }
            return methodReference.Module == module && (methodReference.DeclaringType == null || methodReference.DeclaringType.Module == module)
                ? methodReference
                : module.ImportReference(methodReference);
        }

        private static FieldReference ImportFieldReference(ModuleDefinition module, FieldReference fieldReference)
        {
            if (fieldReference == null)
            {
                return null;
            }
            return fieldReference.Module == module && (fieldReference.DeclaringType == null || fieldReference.DeclaringType.Module == module)
                ? fieldReference
                : module.ImportReference(fieldReference);
        }

        internal static void SanitizeConstants(AssemblyDefinition assembly)
        {
            foreach (var type in GetAllTypes(assembly.MainModule.Types))
            {
                foreach (var field in type.Fields)
                {
                    if (field.HasConstant)
                    {
                        if (DummyAssemblyGenerator.TryNormalizeConstant(field.FieldType, field.Constant, out var normalized))
                        {
                            field.Constant = normalized;
                        }
                        else
                        {
                            field.Constant = null;
                            field.HasConstant = false;
                            field.Attributes &= ~FieldAttributes.HasDefault;
                        }
                    }
                    else
                    {
                        field.Attributes &= ~FieldAttributes.HasDefault;
                    }
                }
                foreach (var method in type.Methods)
                {
                    foreach (var parameter in method.Parameters)
                    {
                        if (parameter.HasConstant)
                        {
                            if (DummyAssemblyGenerator.TryNormalizeConstant(parameter.ParameterType, parameter.Constant, out var normalized))
                            {
                                parameter.Constant = normalized;
                            }
                            else
                            {
                                parameter.Constant = null;
                                parameter.HasConstant = false;
                                parameter.Attributes &= ~ParameterAttributes.HasDefault;
                            }
                        }
                        else
                        {
                            parameter.Attributes &= ~ParameterAttributes.HasDefault;
                        }
                    }
                }
            }
        }

        private static IEnumerable<TypeDefinition> GetAllTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (var type in types)
            {
                yield return type;
                foreach (var nestedType in GetAllTypes(type.NestedTypes))
                {
                    yield return nestedType;
                }
            }
        }

        internal static bool CanWriteConstant(TypeReference typeReference)
        {
            return DummyAssemblyGenerator.CanWriteConstant(typeReference);
        }
    }
}
