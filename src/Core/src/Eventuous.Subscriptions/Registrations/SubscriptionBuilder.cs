using System.Reflection;
using Eventuous.Subscriptions.Consumers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Eventuous.Subscriptions.Registrations;

public abstract class SubscriptionBuilder {
    public string             SubscriptionId { get; }
    public IServiceCollection Services       { get; }

    protected SubscriptionBuilder(IServiceCollection services, string subscriptionId) {
        SubscriptionId = subscriptionId;
        Services       = services;
    }

    readonly List<ResolveHandler> _handlers = new();

    protected ResolveConsumer ResolveConsumer { get; set; } = null!;

    protected IEventHandler[] ResolveHandlers(IServiceProvider sp)
        => _handlers.Select(x => x(sp)).ToArray();

    /// <summary>
    /// Adds an event handler to the subscription
    /// </summary>
    /// <typeparam name="THandler">Event handler type</typeparam>
    /// <returns></returns>
    [PublicAPI]
    public SubscriptionBuilder AddEventHandler<THandler>()
        where THandler : class, IEventHandler {
        Services.TryAddSingleton<THandler>();
        AddHandlerResolve(sp => sp.GetRequiredService<THandler>());
        return this;
    }

    /// <summary>
    /// Adds an event handler to the subscription
    /// </summary>
    /// <param name="getHandler">A function to resolve event handler using the service provider</param>
    /// <typeparam name="THandler"></typeparam>
    /// <returns></returns>
    [PublicAPI]
    public SubscriptionBuilder AddEventHandler<THandler>(
        Func<IServiceProvider, THandler> getHandler
    ) where THandler : class, IEventHandler {
        Services.TryAddSingleton(getHandler);
        AddHandlerResolve(sp => sp.GetRequiredService<THandler>());
        return this;
    }

    public SubscriptionBuilder AddCompositionEventHandler<THandler, TWrappingHandler>(
        Func<THandler, TWrappingHandler> getWrappingHandler
    ) where THandler : class, IEventHandler where TWrappingHandler : class, IEventHandler {
        Services.TryAddSingleton<THandler>();
        AddHandlerResolve(sp => getWrappingHandler(sp.GetRequiredService<THandler>()));
        return this;
    }

    public SubscriptionBuilder AddCompositionEventHandler<THandler, TWrappingHandler>(
        Func<IServiceProvider, THandler> getInnerHandler,
        Func<THandler, TWrappingHandler> getWrappingHandler
    ) where THandler : class, IEventHandler where TWrappingHandler : class, IEventHandler {
        Services.TryAddSingleton(getInnerHandler);
        AddHandlerResolve(sp => getWrappingHandler(sp.GetRequiredService<THandler>()));
        return this;
    }

    /// <summary>
    /// Allows using a custom consumer instead of the <see cref="DefaultConsumer"/> one.
    /// Can also be used to change the default consumer instantiation.
    /// </summary>
    /// <param name="getConsumer">A function to resolve the consumer using the service provider</param>
    /// <returns></returns>
    [PublicAPI]
    public SubscriptionBuilder UseConsumer(
        Func<IServiceProvider, IEventHandler[], IMessageConsumer> getConsumer
    ) {
        Ensure.NotNull(getConsumer, nameof(getConsumer));
        ResolveConsumer = sp => getConsumer(sp, ResolveHandlers(sp));
        return this;
    }

    void AddHandlerResolve(ResolveHandler resolveHandler)
        => _handlers.Add(sp => new TracedEventHandler(resolveHandler(sp)));
}

public class SubscriptionBuilder<T, TOptions> : SubscriptionBuilder
    where T : EventSubscription<TOptions>
    where TOptions : SubscriptionOptions {
    public SubscriptionBuilder(IServiceCollection services, string subscriptionId)
        : base(services, subscriptionId) {
        ResolveConsumer  = ResolveDefaultConsumer;
        ConfigureOptions = options => options.SubscriptionId = subscriptionId;
    }

    T?                _resolvedSubscription;
    IMessageConsumer? _resolvedConsumer;

    internal Action<TOptions>? ConfigureOptions { get; private set; }

    /// <summary>
    /// Configure subscription options
    /// </summary>
    /// <param name="configureOptions">Subscription options configuration function</param>
    /// <returns></returns>
    [PublicAPI]
    public SubscriptionBuilder<T, TOptions> Configure(Action<TOptions>? configureOptions) {
        ConfigureOptions = Cfg;
        return this;

        void Cfg(TOptions options) {
            options.SubscriptionId = SubscriptionId;
            configureOptions?.Invoke(options);
        }
    }

    IMessageConsumer GetConsumer(IServiceProvider sp) {
        if (_resolvedConsumer != null) return _resolvedConsumer;

        _resolvedConsumer = new TracedConsumer(ResolveConsumer(sp));
        return _resolvedConsumer;
    }

    IMessageConsumer ResolveDefaultConsumer(IServiceProvider sp) {
        var options = sp.GetService<IOptionsMonitor<TOptions>>();

        _resolvedConsumer = new DefaultConsumer(
            ResolveHandlers(sp),
            options?.Get(SubscriptionId)?.ThrowOnError == true
        );

        return _resolvedConsumer;
    }

    internal T ResolveSubscription(IServiceProvider sp) {
        const string subscriptionIdParameterName = "subscriptionId";

        if (_resolvedSubscription != null) return _resolvedSubscription;

        var constructors = typeof(T).GetConstructors<TOptions>();

        switch (constructors.Length) {
            case > 1:
                throw new ArgumentOutOfRangeException(
                    typeof(T).Name,
                    "Subscription type must have only one constructor with options argument"
                );
            case 0:
                constructors = typeof(T).GetConstructors<string>(subscriptionIdParameterName);
                break;
        }

        if (constructors.Length == 0) {
            throw new ArgumentOutOfRangeException(
                typeof(T).Name,
                "Subscription type must have at least one constructor with options or subscription id argument"
            );
        }

        var (ctor, parameter) = constructors[0];

        var args = ctor.GetParameters().Select(CreateArg).ToArray();

        if (ctor.Invoke(args) is not T instance)
            throw new InvalidOperationException($"Unable to instantiate {typeof(T)}");

        _resolvedSubscription = instance;
        return instance;

        object? CreateArg(ParameterInfo parameterInfo) {
            if (parameterInfo == parameter) {
                if (parameter.Name == subscriptionIdParameterName) {
                    return SubscriptionId;
                }

                var options = Ensure.NotNull(
                    sp.GetService<IOptionsMonitor<TOptions>>(),
                    typeof(TOptions).Name
                );

                return options.Get(SubscriptionId);
            }

            // ReSharper disable once ConvertIfStatementToReturnStatement
            if (parameterInfo.ParameterType == typeof(IMessageConsumer))
                return new TracedConsumer(GetConsumer(sp));

            return sp.GetService(parameterInfo.ParameterType);
        }
    }
}

static class TypeExtensionsForRegistrations {
    public static (ConstructorInfo Ctor, ParameterInfo Param)[] GetConstructors<T>(
        this Type type,
        string?   name = null
    )
        => type
            .GetConstructors()
            .Select(
                x => (
                    Ctor: x,
                    Options: x.GetParameters()
                        .SingleOrDefault(
                            y => y.ParameterType == typeof(T) && (name == null || y.Name == name)
                        )
                )
            ).Where(x => x.Options != null).ToArray()!;
}

public delegate IEventHandler ResolveHandler(IServiceProvider sp);

public delegate IMessageConsumer ResolveConsumer(IServiceProvider sp);