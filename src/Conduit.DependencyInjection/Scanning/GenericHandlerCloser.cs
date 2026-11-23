using System.Reflection;

namespace Conduit.DependencyInjection.Scanning;

/// <summary>在启动期使用扫描到的候选类型关闭开放处理器。</summary>
internal static class GenericHandlerCloser
{
    internal static IEnumerable<Type> Close(Type openHandler, IReadOnlyCollection<Type> candidates, RegistrationGuard guard)
    {
        var parameters = openHandler.GetGenericArguments();
        var arguments = new Type[parameters.Length];
        var candidatesByParameter = parameters.Select(parameter => FilterCandidates(parameter, candidates)).ToArray();
        return CloseAt(0);

        IEnumerable<Type> CloseAt(int index)
        {
            guard.CheckTimeout();
            if (index < arguments.Length)
            {
                foreach (var candidate in candidatesByParameter[index])
                {
                    arguments[index] = candidate;
                    foreach (var closed in CloseAt(index + 1))
                    {
                        yield return closed;
                    }
                }

                yield break;
            }

            Type closedHandler;
            try
            {
                // CLR 检查包含其他泛型参数的约束。
                closedHandler = openHandler.MakeGenericType(arguments);
            }
            catch (ArgumentException)
            {
                yield break;
            }

            guard.IncrementTypesClosing();
            yield return closedHandler;
        }
    }

    private static Type[] FilterCandidates(Type parameter, IReadOnlyCollection<Type> candidates)
    {
        var attributes = parameter.GenericParameterAttributes;
        var constraints = parameter.GetGenericParameterConstraints()
            .Where(static constraint => !constraint.ContainsGenericParameters)
            .ToArray();
        return candidates.Where(candidate =>
            ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) == 0 || !candidate.IsValueType)
            && ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) == 0
                || candidate.IsValueType && Nullable.GetUnderlyingType(candidate) is null)
            && ((attributes & GenericParameterAttributes.DefaultConstructorConstraint) == 0
                || candidate.IsValueType || !candidate.IsAbstract && candidate.GetConstructor(Type.EmptyTypes) is not null)
            && constraints.All(constraint => constraint.IsAssignableFrom(candidate)))
            .ToArray();
    }
}
