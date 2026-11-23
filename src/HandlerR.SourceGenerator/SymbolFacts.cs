using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace HandlerR.SourceGenerator;

/// <summary>诊断描述符。</summary>
internal static class Diagnostics
{
    private const string Category = "HandlerR";

    public static readonly DiagnosticDescriptor MissingHandler = new(
        id: "CDI001",
        title: "Request has no handler",
        messageFormat: "Request '{0}' is declared in this compilation but no handler for it was found in the same compilation. "
                       + "If the handler lives in another assembly, ignore this warning; otherwise add an IRequestHandler implementation.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateHandler = new(
        id: "CDI002",
        title: "Duplicate handler",
        messageFormat: "Request '{0}' has more than one handler in this compilation: {1}. A request must have exactly one handler.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedHandlerShape = new(
        id: "CDI003",
        title: "Handler shape is not supported by the generator",
        messageFormat: "Handler '{0}' is an open generic or nested generic type and cannot be closed at compile time. "
                       + "Register it through HandlerRConfiguration (Mode A) instead.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}

/// <summary>从语法节点提取处理器 / 请求事实。</summary>
internal static class SymbolFacts
{
    private const string AbstractionsNamespace = "HandlerR.Abstractions";
    private const string ContractsNamespace = "HandlerR.Contracts";

    public static ImmutableArray<HandlerModel> CreateHandlers(GeneratorSyntaxContext context)
    {
        var symbol = context.SemanticModel.GetDeclaredSymbol(context.Node) as INamedTypeSymbol;
        if (symbol is null || symbol.IsAbstract || symbol.IsStatic || symbol.IsGenericType
            || symbol.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return [];
        }

        var handler = FullyQualified(symbol);
        var models = ImmutableArray.CreateBuilder<HandlerModel>();
        foreach (var @interface in symbol.AllInterfaces)
        {
            if (@interface.ContainingNamespace.ToDisplayString() != AbstractionsNamespace)
            {
                continue;
            }

            var kind = (@interface.Name, @interface.Arity) switch
            {
                ("IRequestHandler", 2) => HandlerKind.Request,
                ("IStreamRequestHandler", 2) => HandlerKind.Stream,
                ("INotificationHandler", 1) => HandlerKind.Notification,
                ("IRequestExceptionHandler", 3) => HandlerKind.ExceptionHandler,
                ("IRequestExceptionAction", 2) => HandlerKind.ExceptionAction,
                ("IRequestPreProcessor", 1) => HandlerKind.PreProcessor,
                ("IRequestPostProcessor", 2) => HandlerKind.PostProcessor,
                _ => (HandlerKind?)null,
            };

            if (kind is null)
            {
                continue;
            }

            var arguments = @interface.TypeArguments;
            var response = kind is HandlerKind.Request or HandlerKind.Stream or HandlerKind.ExceptionHandler or HandlerKind.PostProcessor
                ? FullyQualified(arguments[1])
                : string.Empty;
            var exception = kind switch
            {
                HandlerKind.ExceptionHandler => FullyQualified(arguments[2]),
                HandlerKind.ExceptionAction => FullyQualified(arguments[1]),
                _ => string.Empty,
            };
            models.Add(new HandlerModel(kind.Value, handler, FullyQualified(arguments[0]), response, exception));
        }

        return models.ToImmutable();
    }

    public static RequestModel? CreateRequest(GeneratorSyntaxContext context)
    {
        var symbol = context.SemanticModel.GetDeclaredSymbol(context.Node) as INamedTypeSymbol;
        if (symbol is null || symbol.IsAbstract || symbol.IsStatic || symbol.IsGenericType)
        {
            return null;
        }

        if (symbol.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return null;
        }

        var isStream = false;
        var isRequest = false;

        foreach (var @interface in symbol.AllInterfaces)
        {
            if (!@interface.IsGenericType)
            {
                if (Matches(@interface, AbstractionsNamespace, "ICommand", 0))
                {
                    isRequest = true;
                }

                continue;
            }

            if (Matches(@interface, ContractsNamespace, "IRequest", 1)
                || Matches(@interface, AbstractionsNamespace, "IQuery", 1))
            {
                isRequest = true;
            }
            else if (Matches(@interface, ContractsNamespace, "IStreamRequest", 1))
            {
                isStream = true;
                isRequest = true;
            }
        }

        if (!isRequest)
        {
            return null;
        }

        var location = context.Node.GetLocation().GetLineSpan();
        return new RequestModel(
            FullyQualified(symbol),
            isStream,
            location.Path,
            location.StartLinePosition.Line,
            location.StartLinePosition.Character);
    }

    public static Location ToLocation(RequestModel request)
        => Location.Create(
            request.FilePath,
            new TextSpan(0, 0),
            new LinePositionSpan(
                new LinePosition(request.StartLine, request.StartCharacter),
                new LinePosition(request.StartLine, request.StartCharacter)));

    private static bool Matches(INamedTypeSymbol symbol, string @namespace, string name, int arity)
        => symbol.Name == name
           && symbol.Arity == arity
           && symbol.ContainingNamespace?.ToDisplayString() == @namespace;

    private static string FullyQualified(ITypeSymbol symbol)
        => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}
