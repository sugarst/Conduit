using Microsoft.Extensions.DependencyInjection;

namespace HandlerR.DependencyInjection;

/// <summary>
/// 开放泛型 Behavior 的关闭策略。
/// </summary>
public enum BehaviorClosingStrategy
{
    /// <summary>
    /// 启动期对每个已扫描的请求 / 响应组合逐一关闭并显式注册（默认）。
    /// </summary>
    /// <remarks>行为可预测、启动期即可校验，且不依赖容器对开放泛型的支持程度。</remarks>
    CloseForScannedRequests = 0,

    /// <summary>
    /// 注册为开放泛型，交由容器在解析时关闭。
    /// </summary>
    /// <remarks>适用于容器对开放泛型有增强（如编译期泛型注册），代价是启动期无法校验关闭结果。</remarks>
    CloseInContainer = 1,
}

/// <summary>
/// 一条开放 Behavior 注册（类型 + 生命周期）。
/// </summary>
/// <param name="BehaviorType">Behavior 类型（开放泛型或已封闭类型）。</param>
/// <param name="Lifetime">生命周期。</param>
public sealed record HandlerROpenBehavior(Type BehaviorType, ServiceLifetime Lifetime);
