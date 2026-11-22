using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Conduit.Core;

/// <summary>
/// 内建可观测性入口：Activity 用于链路，Meter 用于指标。宿主无需注册日志提供程序即可工作。
/// </summary>
/// <remarks>
/// 语义约定采用 OpenTelemetry Messaging：
/// <list type="bullet">
///   <item><c>messaging.system</c> = conduit</item>
///   <item><c>messaging.operation</c> = send / publish / stream</item>
///   <item><c>messaging.message.type</c> = 请求类型全名</item>
///   <item><c>messaging.destination.name</c> = 处理器全名</item>
/// </list>
/// </remarks>
public static class ConduitDiagnostics
{
    /// <summary>ActivitySource / Meter 名称。宿主订阅时使用该名称。</summary>
    public const string SourceName = "Conduit";

    /// <summary>链路源。</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");

    /// <summary>指标源。</summary>
    public static readonly Meter Meter = new(SourceName, "1.0.0");

    /// <summary>请求发送次数。</summary>
    public static readonly Counter<long> SendCount = Meter.CreateCounter<long>("conduit.send.count");

    /// <summary>请求发送耗时（毫秒）。</summary>
    public static readonly Histogram<double> SendDuration = Meter.CreateHistogram<double>("conduit.send.duration", "ms");

    /// <summary>通知发布次数。</summary>
    public static readonly Counter<long> PublishCount = Meter.CreateCounter<long>("conduit.publish.count");

    /// <summary>处理失败次数。</summary>
    public static readonly Counter<long> FailureCount = Meter.CreateCounter<long>("conduit.handler.failures");

    internal static KeyValuePair<string, object?>[] BuildTags(Type messageType, string operation, Type? destinationType)
    {
        if (destinationType is null)
        {
            return
            [
                new("messaging.system", "conduit"),
                new("messaging.operation", operation),
                new("messaging.message.type", messageType.FullName),
            ];
        }

        return
        [
            new("messaging.system", "conduit"),
            new("messaging.operation", operation),
            new("messaging.message.type", messageType.FullName),
            new("messaging.destination.name", destinationType.FullName),
        ];
    }
}
