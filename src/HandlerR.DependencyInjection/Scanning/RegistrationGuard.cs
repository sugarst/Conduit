using System.Diagnostics;
using HandlerR.Abstractions;

namespace HandlerR.DependencyInjection.Scanning;

/// <summary>
/// 四类泛型注册护栏。语义对齐基准 15.0：阈值为 <c>0</c> 表示禁用该限制（而不是立即失败）。
/// </summary>
internal sealed class RegistrationGuard(HandlerRConfiguration configuration)
{
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private int _typesClosing;
    private int _registrations;
    private int _maxTypeParameters;

    public void IncrementRegistrations(int count = 1)
    {
        _registrations += count;
        CheckGenericRegistrations();
    }

    public void IncrementTypesClosing()
    {
        _typesClosing++;
        if (configuration.MaxTypesClosing > 0 && _typesClosing > configuration.MaxTypesClosing)
        {
            throw new HandlerRConfigurationException(
                $"Generic registration guard tripped: more than {configuration.MaxTypesClosing} closed types "
                + "were created. Raise HandlerRConfiguration.MaxTypesClosing or set it to 0 to disable the limit.");
        }
    }

    public void AddTypeParameters(int count)
    {
        _maxTypeParameters = Math.Max(_maxTypeParameters, count);
        if (configuration.MaxGenericTypeParameters > 0 && count > configuration.MaxGenericTypeParameters)
        {
            throw new HandlerRConfigurationException(
                $"Generic registration guard tripped: a type declares {count} generic parameters, above the "
                + $"configured MaxGenericTypeParameters ({configuration.MaxGenericTypeParameters}). "
                + "Raise the limit or set it to 0 to disable it.");
        }
    }

    public void CheckTimeout()
    {
        if (configuration.RegistrationTimeout <= 0)
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_startTimestamp);
        if (elapsed.TotalMilliseconds > configuration.RegistrationTimeout)
        {
            throw new HandlerRConfigurationException(
                $"Registration exceeded the configured RegistrationTimeout of {configuration.RegistrationTimeout} ms "
                + $"(elapsed {elapsed.TotalMilliseconds:F0} ms). Reduce scanned assemblies or set the timeout to 0 to disable it.");
        }
    }

    private void CheckGenericRegistrations()
    {
        if (configuration.MaxGenericTypeRegistrations > 0
            && _registrations > configuration.MaxGenericTypeRegistrations)
        {
            throw new HandlerRConfigurationException(
                $"Generic registration guard tripped: more than {configuration.MaxGenericTypeRegistrations} registrations "
                + "were produced. Raise HandlerRConfiguration.MaxGenericTypeRegistrations or set it to 0 to disable the limit.");
        }
    }
}
