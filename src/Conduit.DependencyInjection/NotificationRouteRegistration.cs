using System.Collections.Frozen;
using System.Reflection;
using Conduit.Abstractions;
using Conduit.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NotificationRoute = System.Func<Conduit.Abstractions.IDispatcher, Conduit.Contracts.INotification,
    System.Threading.CancellationToken, System.Threading.Tasks.ValueTask>;

namespace Conduit.DependencyInjection;

internal static class NotificationRouteRegistration
{
    internal static void Register(IServiceCollection services)
    {
        var serviceType = typeof(IReadOnlyDictionary<Type, NotificationRoute>);
        var generatedRoutes = services.LastOrDefault(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == serviceType);
        var routes = generatedRoutes?.ImplementationInstance as IReadOnlyDictionary<Type, NotificationRoute>;
        var additions = new Dictionary<Type, NotificationRoute>();
        MethodInfo? publishMethod = null;
        foreach (var descriptor in services)
        {
            var handlerType = descriptor.ServiceType;
            if (descriptor.IsKeyedService || !handlerType.IsConstructedGenericType || handlerType.ContainsGenericParameters
                || handlerType.GetGenericTypeDefinition() != typeof(INotificationHandler<>))
            {
                continue;
            }

            var notificationType = handlerType.GetGenericArguments()[0];
            if (!(routes?.ContainsKey(notificationType) ?? false) && !additions.ContainsKey(notificationType))
            {
                publishMethod ??= typeof(NotificationRouteRegistration)
                    .GetMethod(nameof(PublishAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
                additions.Add(notificationType,
                    publishMethod.MakeGenericMethod(notificationType).CreateDelegate<NotificationRoute>());
            }
        }

        if (additions.Count > 0)
        {
            if (routes is not null)
            {
                foreach (var route in routes)
                {
                    additions.Add(route.Key, route.Value);
                }
            }

            services.AddSingleton<IReadOnlyDictionary<Type, NotificationRoute>>(additions.ToFrozenDictionary());
        }
    }

    private static ValueTask PublishAsync<TNotification>(
        IDispatcher dispatcher, INotification notification, CancellationToken cancellationToken)
        where TNotification : INotification
        => dispatcher.PublishAsync((TNotification)notification, cancellationToken);
}
