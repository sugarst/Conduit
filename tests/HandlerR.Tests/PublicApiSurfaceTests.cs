using System.Reflection;
using HandlerR.Abstractions;
using HandlerR.Contracts;
using HandlerR.Core;
using HandlerR.DependencyInjection;
using HandlerR.FluentValidation;

namespace HandlerR.Tests;

/// <summary>
/// 公开 API 表面审批测试。
/// </summary>
/// <remarks>
/// 技术方案把"公开 API 变更需显式批准"列为工程约束。这里用反射快照实现同等门禁：
/// 任何新增/删除/改签名的公开成员都会让本测试失败，审查者必须同步更新
/// <c>ApiSurface.approved.txt</c>，从而让 API 变更进入代码评审视野。
/// 更新基线：设置环境变量 <c>HANDLERR_APPROVE_API=1</c> 后重跑本测试。
/// </remarks>
public class PublicApiSurfaceTests
{
    private const string ApproveEnvironmentVariable = "HANDLERR_APPROVE_API";

    private static readonly Assembly[] ProductAssemblies =
    [
        typeof(Unit).Assembly,                                  // HandlerR.Contracts
        typeof(ISender).Assembly,                               // HandlerR.Abstractions
        typeof(HandlerRDispatcher).Assembly,                     // HandlerR.Core
        typeof(HandlerRServiceCollectionExtensions).Assembly,    // HandlerR.DependencyInjection
        typeof(FluentValidationHandlerRExtensions).Assembly,     // HandlerR.FluentValidation
    ];

    private static string ApprovedFilePath => Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..",
        "ApiSurface.approved.txt");

    [Test]
    public async Task Public_api_surface_is_stable()
    {
        var actual = BuildSurface();

        if (Environment.GetEnvironmentVariable(ApproveEnvironmentVariable) == "1")
        {
            await File.WriteAllTextAsync(ApprovedFilePath, actual);
            return;
        }

        await Assert.That(File.Exists(ApprovedFilePath))
            .IsTrue()
            .Because($"缺少 API 基线文件 {ApprovedFilePath}；设置 {ApproveEnvironmentVariable}=1 后重跑以生成。");

        var approved = await File.ReadAllTextAsync(ApprovedFilePath);

        await Assert.That(actual)
            .IsEqualTo(approved)
            .Because($"公开 API 表面发生变化。确认变更合理后，设置 {ApproveEnvironmentVariable}=1 重跑以更新基线。");
    }

    [Test]
    public async Task Contracts_exposes_exactly_six_public_types()
    {
        var exported = typeof(Unit).Assembly.GetExportedTypes()
            .Select(type => type.FullName!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(exported.Length).IsEqualTo(6);
        await Assert.That(string.Join(", ", exported)).IsNotEmpty();
    }

    private static string BuildSurface()
    {
        var lines = new List<string>();

        foreach (var assembly in ProductAssemblies)
        {
            lines.Add($"# {assembly.GetName().Name}");

            foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                lines.Add($"T {type.FullName}{TypeSuffix(type)}");

                const BindingFlags flags = BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly;

                foreach (var member in type.GetMembers(flags).OrderBy(Format, StringComparer.Ordinal))
                {
                    // 属性/事件访问器（get_X / set_X / add_X / remove_X）由属性与事件行覆盖
                    if (member is MethodInfo method && method.IsSpecialName)
                    {
                        continue;
                    }

                    lines.Add(Format(member));
                }
            }

            lines.Add(string.Empty);
        }

        return string.Join('\n', lines).ReplaceLineEndings("\n").TrimEnd() + "\n";
    }

    private static string TypeSuffix(Type type)
    {
        var parts = new List<string>();

        if (type.IsInterface)
        {
            parts.Add("interface");
        }
        else if (type.IsAbstract && type.IsSealed)
        {
            parts.Add("static");
        }

        if (type.IsSealed && !type.IsAbstract)
        {
            parts.Add("sealed");
        }

        if (!type.IsEnum && type.BaseType is not null && type.BaseType != typeof(object) && !type.IsValueType)
        {
            parts.Add($"base:{type.BaseType.FullName}");
        }

        var interfaces = type.GetInterfaces()
            // 构造泛型接口的 FullName 为 null，退化为 ToString() 以保持确定性
            .Select(i => i.FullName ?? i.ToString())
            .Where(name => name.StartsWith("HandlerR.", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (interfaces.Length > 0)
        {
            parts.Add($"implements:{string.Join("+", interfaces)}");
        }

        return parts.Count == 0 ? string.Empty : $" [{string.Join("; ", parts)}]";
    }

    private static string Format(MemberInfo member) => member switch
    {
        MethodInfo method =>
            $"  M {(method.IsStatic ? "static " : string.Empty)}{method.DeclaringType!.FullName}.{method.Name}"
            + $"`{method.GetGenericArguments().Length}"
            + $"({string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.ToString()))})"
            + $" -> {method.ReturnType}",

        ConstructorInfo constructor =>
            $"  C {constructor.DeclaringType!.FullName}"
            + $"({string.Join(", ", constructor.GetParameters().Select(parameter => parameter.ParameterType.ToString()))})",

        PropertyInfo property =>
            $"  P {property.DeclaringType!.FullName}.{property.Name} : {property.PropertyType}"
            + $" ({(property.CanRead ? "get" : string.Empty)}{(property.CanWrite ? "set" : string.Empty)})",

        FieldInfo field =>
            $"  F {(field.IsStatic ? "static " : string.Empty)}{field.DeclaringType!.FullName}.{field.Name} : {field.FieldType}",

        EventInfo @event =>
            $"  E {@event.DeclaringType!.FullName}.{@event.Name} : {@event.EventHandlerType}",

        _ => $"  O {member.DeclaringType!.FullName}.{member.Name}",
    };
}
