using System.Reflection;

namespace Conduit.DependencyInjection.Scanning;

/// <summary>关闭尝试的结果。</summary>
internal enum CloseResult
{
    /// <summary>成功关闭。</summary>
    Closed = 0,

    /// <summary>类型结构不匹配或约束不满足：该请求不适用此 Behavior（静默跳过）。</summary>
    NotApplicable = 1,

    /// <summary>结构匹配但无法推断类型参数：属于配置错误，需诊断。</summary>
    Failed = 2,
}

/// <summary>
/// 开放泛型 Behavior 关闭器：支持"直接参数"与"嵌套泛型参数"两种形态，例如
/// <c>Behavior&lt;TRequest, TItem&gt; : IPipelineBehavior&lt;TRequest, List&lt;TItem&gt;&gt;</c>。
/// </summary>
internal static class OpenBehaviorCloser
{
    public static CloseResult TryClose(
        Type openBehavior,
        Type requestType,
        Type responseType,
        Type pipelineInterfaceOpen,
        out Type closedBehavior,
        out string? failure)
    {
        closedBehavior = openBehavior;
        failure = null;

        var pattern = openBehavior.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == pipelineInterfaceOpen);

        if (pattern is null)
        {
            failure = $"Type '{openBehavior.FullName}' does not implement '{pipelineInterfaceOpen.Name}'.";
            return CloseResult.Failed;
        }

        var parameters = openBehavior.GetGenericArguments();
        var patternArguments = pattern.GetGenericArguments();
        var bindings = new Dictionary<Type, Type>(parameters.Length);

        if (!TryUnify(patternArguments[0], requestType, bindings, out failure)
            || !TryUnify(patternArguments[1], responseType, bindings, out failure))
        {
            return CloseResult.NotApplicable;
        }

        var arguments = new Type[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            if (!bindings.TryGetValue(parameters[i], out var bound))
            {
                failure = $"Cannot infer generic parameter '{parameters[i].Name}' of '{openBehavior.FullName}' "
                          + $"for request '{requestType.Name}' / response '{responseType.Name}'. "
                          + "Register this behavior explicitly (closed type) instead of as an open generic.";
                return CloseResult.Failed;
            }

            arguments[i] = bound;
        }

        if (!SatisfiesConstraints(parameters, arguments))
        {
            return CloseResult.NotApplicable;
        }

        closedBehavior = openBehavior.MakeGenericType(arguments);
        return CloseResult.Closed;
    }

    private static bool TryUnify(Type pattern, Type target, Dictionary<Type, Type> bindings, out string? failure)
    {
        failure = null;

        if (pattern.IsGenericParameter)
        {
            if (bindings.TryGetValue(pattern, out var bound))
            {
                if (bound == target)
                {
                    return true;
                }

                failure = $"Conflicting inference for '{pattern.Name}': '{bound.Name}' vs '{target.Name}'.";
                return false;
            }

            bindings[pattern] = target;
            return true;
        }

        if (pattern.IsArray)
        {
            if (!target.IsArray || pattern.GetArrayRank() != target.GetArrayRank())
            {
                failure = $"Array pattern mismatch: '{pattern}' vs '{target}'.";
                return false;
            }

            return TryUnify(pattern.GetElementType()!, target.GetElementType()!, bindings, out failure);
        }

        if (pattern.IsGenericType)
        {
            if (!target.IsGenericType || target.GetGenericTypeDefinition() != pattern.GetGenericTypeDefinition())
            {
                failure = $"Generic pattern mismatch: '{pattern}' vs '{target}'.";
                return false;
            }

            var patternArguments = pattern.GetGenericArguments();
            var targetArguments = target.GetGenericArguments();
            for (var i = 0; i < patternArguments.Length; i++)
            {
                if (!TryUnify(patternArguments[i], targetArguments[i], bindings, out failure))
                {
                    return false;
                }
            }

            return true;
        }

        if (pattern != target)
        {
            failure = $"Pattern '{pattern}' does not match '{target}'.";
            return false;
        }

        return true;
    }

    private static bool SatisfiesConstraints(Type[] parameters, Type[] arguments)
    {
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var argument = arguments[i];

            var attributes = parameter.GenericParameterAttributes;
            if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0 && argument.IsValueType)
            {
                return false;
            }

            if ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0
                && (!argument.IsValueType || Nullable.GetUnderlyingType(argument) is not null))
            {
                return false;
            }

            if ((attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0
                && !argument.IsValueType
                && argument.GetConstructor(Type.EmptyTypes) is null)
            {
                return false;
            }

            foreach (var constraint in parameter.GetGenericParameterConstraints())
            {
                var substituted = Substitute(constraint, parameters, arguments);
                if (!substituted.IsAssignableFrom(argument))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static Type Substitute(Type type, Type[] parameters, Type[] arguments)
    {
        if (type.IsGenericParameter)
        {
            var index = Array.IndexOf(parameters, type);
            return index >= 0 ? arguments[index] : type;
        }

        if (type.IsArray)
        {
            var element = Substitute(type.GetElementType()!, parameters, arguments);
            return type.GetArrayRank() == 1
                ? element.MakeArrayType()
                : element.MakeArrayType(type.GetArrayRank());
        }

        if (type.IsGenericType && type.ContainsGenericParameters)
        {
            var definition = type.GetGenericTypeDefinition();
            var typeArguments = type.GetGenericArguments();
            for (var i = 0; i < typeArguments.Length; i++)
            {
                typeArguments[i] = Substitute(typeArguments[i], parameters, arguments);
            }

            return definition.MakeGenericType(typeArguments);
        }

        return type;
    }
}
