using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Conduit.SourceGenerator;

/// <summary>
/// Mode B 增量生成器：把调度表与 Handler 注册搬到编译期，运行时零反射。
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ConduitGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var handlers = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax { BaseList: not null },
                static (ctx, _) => SymbolFacts.CreateHandler(ctx))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect()
            .Select(static (models, _) => new EquatableList<HandlerModel>(models.ToArray()));

        var requests = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax { BaseList: not null },
                static (ctx, _) => SymbolFacts.CreateRequest(ctx))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect()
            .Select(static (models, _) => new EquatableList<RequestModel>(models.ToArray()));

        var hasDependencyInjection = context.CompilationProvider.Select(
            static (compilation, _) => compilation.ReferencedAssemblyNames
                .Any(static identity => identity.Name == "Conduit.DependencyInjection"));

        context.RegisterSourceOutput(
            handlers.Combine(requests).Combine(hasDependencyInjection),
            static (productionContext, input) => Emitter.Emit(
                productionContext,
                input.Left.Left,
                input.Left.Right,
                input.Right));
    }
}

/// <summary>源码与诊断产出。</summary>
internal static class Emitter
{
    public static void Emit(
        SourceProductionContext context,
        EquatableList<HandlerModel> handlers,
        EquatableList<RequestModel> requests,
        bool dependencyInjectionReferenced)
    {
        // 没有引用 DI 包时无法生成注册入口（契约项目只引 Contracts），静默跳过
        if (!dependencyInjectionReferenced)
        {
            return;
        }

        var all = handlers.Items;
        var requestHandlers = new Dictionary<string, List<HandlerModel>>(System.StringComparer.Ordinal);
        var streamHandlers = new Dictionary<string, List<HandlerModel>>(System.StringComparer.Ordinal);

        foreach (var handler in all)
        {
            var bucket = handler.Kind switch
            {
                HandlerKind.Request => requestHandlers,
                HandlerKind.Stream => streamHandlers,
                _ => null,
            };

            if (bucket is null)
            {
                continue;
            }

            if (!bucket.TryGetValue(handler.RequestType, out var list))
            {
                list = [];
                bucket[handler.RequestType] = list;
            }

            list.Add(handler);
        }

        var duplicates = new HashSet<string>(System.StringComparer.Ordinal);
        ReportDuplicates(context, requestHandlers, duplicates);
        ReportDuplicates(context, streamHandlers, duplicates);

        ReportMissingHandlers(context, requests.Items, requestHandlers, streamHandlers);

        var source = Build(handlers.Items, requestHandlers, streamHandlers, duplicates);
        context.AddSource("Conduit.Generated.g.cs", source);
    }

    private static void ReportDuplicates(
        SourceProductionContext context,
        Dictionary<string, List<HandlerModel>> bucket,
        HashSet<string> duplicates)
    {
        foreach (var pair in bucket)
        {
            if (pair.Value.Count <= 1)
            {
                continue;
            }

            duplicates.Add(pair.Key);
            var names = string.Join(", ", pair.Value.Select(static h => h.HandlerType));
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.DuplicateHandler, Location.None, pair.Key, names));
        }
    }

    private static void ReportMissingHandlers(
        SourceProductionContext context,
        RequestModel[] requests,
        Dictionary<string, List<HandlerModel>> requestHandlers,
        Dictionary<string, List<HandlerModel>> streamHandlers)
    {
        var covered = new HashSet<string>(System.StringComparer.Ordinal);
        covered.UnionWith(requestHandlers.Keys);
        covered.UnionWith(streamHandlers.Keys);

        foreach (var request in requests)
        {
            if (covered.Contains(request.RequestType))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.MissingHandler,
                SymbolFacts.ToLocation(request),
                request.RequestType));
        }
    }

    private static string Build(
        HandlerModel[] handlers,
        Dictionary<string, List<HandlerModel>> requestHandlers,
        Dictionary<string, List<HandlerModel>> streamHandlers,
        HashSet<string> duplicates)
    {
        var builder = new StringBuilder(4096);
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("#pragma warning disable");
        builder.AppendLine();
        builder.AppendLine("namespace Conduit.Generated");
        builder.AppendLine("{");
        builder.AppendLine("    internal static class ConduitGeneratedRegistration");
        builder.AppendLine("    {");
        builder.AppendLine("        public static global::Conduit.DependencyInjection.GeneratedRegistration Create()");
        builder.AppendLine("        {");
        builder.AppendLine("            var tables = new global::Conduit.Core.Dispatch.DispatchTableBuilder();");

        foreach (var pair in requestHandlers)
        {
            if (duplicates.Contains(pair.Key))
            {
                continue;
            }

            var handler = pair.Value[0];
            builder.Append("            tables.Add(new global::Conduit.Core.Dispatch.RequestHandlerEntry<")
                .Append(handler.RequestType).Append(", ")
                .Append(handler.ResponseType).AppendLine(">());");
        }

        foreach (var pair in streamHandlers)
        {
            if (duplicates.Contains(pair.Key))
            {
                continue;
            }

            var handler = pair.Value[0];
            builder.Append("            tables.AddStream(new global::Conduit.Core.Dispatch.StreamHandlerEntry<")
                .Append(handler.RequestType).Append(", ")
                .Append(handler.ResponseType).AppendLine(">());");
        }

        builder.AppendLine();
        builder.AppendLine("            return new global::Conduit.DependencyInjection.GeneratedRegistration");
        builder.AppendLine("            {");
        builder.AppendLine("                Tables = tables.Build(),");
        AppendServices(builder, handlers, duplicates);
        AppendExceptionHandlers(builder, handlers);
        AppendExceptionActions(builder, handlers);
        builder.AppendLine("            };");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        AppendExtension(builder);
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static void AppendServices(StringBuilder builder, HandlerModel[] handlers, HashSet<string> duplicates)
    {
        builder.AppendLine("                Services = new global::Conduit.DependencyInjection.GeneratedServiceRegistration[]");
        builder.AppendLine("                {");

        foreach (var handler in handlers)
        {
            if (handler.Kind is HandlerKind.Request or HandlerKind.Stream && duplicates.Contains(handler.RequestType))
            {
                continue;
            }

            var serviceType = handler.Kind switch
            {
                HandlerKind.Request =>
                    $"global::Conduit.Abstractions.IRequestHandler<{handler.RequestType}, {handler.ResponseType}>",
                HandlerKind.Stream =>
                    $"global::Conduit.Abstractions.IStreamRequestHandler<{handler.RequestType}, {handler.ResponseType}>",
                HandlerKind.Notification =>
                    $"global::Conduit.Abstractions.INotificationHandler<{handler.RequestType}>",
                HandlerKind.ExceptionHandler =>
                    $"global::Conduit.Abstractions.IRequestExceptionHandler<{handler.RequestType}, {handler.ResponseType}, {handler.ExceptionType}>",
                HandlerKind.ExceptionAction =>
                    $"global::Conduit.Abstractions.IRequestExceptionAction<{handler.RequestType}, {handler.ExceptionType}>",
                _ => null,
            };

            if (serviceType is null)
            {
                continue;
            }

            builder.Append("                    new(typeof(").Append(serviceType).Append("), typeof(")
                .Append(handler.HandlerType)
                .AppendLine("), global::Microsoft.Extensions.DependencyInjection.ServiceLifetime.Transient),");
        }

        builder.AppendLine("                },");
    }

    private static void AppendExceptionHandlers(StringBuilder builder, HandlerModel[] handlers)
    {
        builder.AppendLine("                ExceptionHandlers = new global::Conduit.DependencyInjection.GeneratedExceptionHandler[]");
        builder.AppendLine("                {");

        foreach (var handler in handlers)
        {
            if (handler.Kind != HandlerKind.ExceptionHandler)
            {
                continue;
            }

            builder.Append("                    new(typeof(").Append(handler.RequestType)
                .Append("), typeof(").Append(handler.ResponseType)
                .Append("), typeof(").Append(handler.ExceptionType)
                .AppendLine(")),");
        }

        builder.AppendLine("                },");
    }

    private static void AppendExceptionActions(StringBuilder builder, HandlerModel[] handlers)
    {
        builder.AppendLine("                ExceptionActions = new global::Conduit.DependencyInjection.GeneratedExceptionAction[]");
        builder.AppendLine("                {");

        foreach (var handler in handlers)
        {
            if (handler.Kind != HandlerKind.ExceptionAction)
            {
                continue;
            }

            builder.Append("                    new(typeof(").Append(handler.RequestType)
                .Append("), typeof(").Append(handler.ExceptionType)
                .AppendLine(")),");
        }

        builder.AppendLine("                },");
    }

    private static void AppendExtension(StringBuilder builder)
    {
        builder.AppendLine("    public static class ConduitGeneratedServiceCollectionExtensions");
        builder.AppendLine("    {");
        builder.AppendLine("        public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddGeneratedConduit(");
        builder.AppendLine("            this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services,");
        builder.AppendLine("            global::System.Action<global::Conduit.DependencyInjection.ConduitConfiguration>? configure = null)");
        builder.AppendLine("        {");
        builder.AppendLine("            global::System.ArgumentNullException.ThrowIfNull(services);");
        builder.AppendLine("            return global::Conduit.DependencyInjection.ConduitServiceCollectionExtensions.AddConduit(");
        builder.AppendLine("                services, ConduitGeneratedRegistration.Create(), configure);");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }
}
