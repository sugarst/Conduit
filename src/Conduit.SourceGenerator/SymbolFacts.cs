using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Conduit.SourceGenerator;

/// <summary>诊断描述符。</summary>
internal static class Diagnostics
{
    private const string Category = "Conduit";

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
                       + "Register it through ConduitConfiguration (Mode A) instead.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}

/// <summary>从语法节点提取处理器 / 请求事实。</summary>
internal static class SymbolFacts
{
    private const string AbstractionsNamespace = "Conduit.Abstractions";
    private const string ContractsNamespace = "Conduit.Contracts";

    public static HandlerModel? CreateHandler(GeneratorSyntaxContext context)
    {
        var symbol = context.SemanticModel.GetDeclaredSymbol(context.Node) as INamedTypeSymbol;
        if (symbol is null || symbol.IsAbstract || symbol.IsStatic || symbol.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return null;
        }

        if (symbol.IsGenericType)
        {
            return null;
        }

        HandlerModel? model = null;
        var handler = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        foreach (var @interface in symbol.AllInterfaces)
        {
            if (!@interface.IsGenericType)
            {
                continue;
            }

            var arguments = @interface.TypeArguments;

            if (Matches(@interface, AbstractionsNamespace, "IRequestHandler", 2))
            {
                model ??= new HandlerModel(
                    HandlerKind.Request,
                    handler,
                    FullyQualified(arguments[0]),
                    FullyQualified(arguments[1]),
                    string.Empty);
            }
            else if (Matches(@interface, AbstractionsNamespace, "IStreamRequestHandler", 2))
            {
                model ??= new HandlerModel(
                    HandlerKind.Stream,
                    handler,
                    FullyQualified(arguments[0]),
                    FullyQualified(arguments[1]),
                    string.Empty);
            }
            else if (Matches(@interface, AbstractionsNamespace, "INotificationHandler", 1))
            {
                model ??= new HandlerModel(
                    HandlerKind.Notification,
                    handler,
                    FullyQualified(arguments[0]),
                    string.Empty,
                    string.Empty);
            }
            else if (Matches(@interface, AbstractionsNamespace, "IRequestExceptionHandler", 3))
            {
                model ??= new HandlerModel(
                    HandlerKind.ExceptionHandler,
                    handler,
                    FullyQualified(arguments[0]),
                    FullyQualified(arguments[1]),
                    FullyQualified(arguments[2]));
            }
            else if (Matches(@interface, AbstractionsNamespace, "IRequestExceptionAction", 2))
            {
                model ??= new HandlerModel(
                    HandlerKind.ExceptionAction,
                    handler,
                    FullyQualified(arguments[0]),
                    string.Empty,
                    FullyQualified(arguments[1]));
            }
        }

        return model;
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
