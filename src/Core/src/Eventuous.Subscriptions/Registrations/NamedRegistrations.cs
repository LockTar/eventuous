// Copyright (C) Ubiquitous AS. All rights reserved
// Licensed under the Apache License, Version 2.0.

using Eventuous.Subscriptions;
using Eventuous.Subscriptions.Registrations;

// ReSharper disable CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

static class NamedRegistrationExtensions {
    public static IServiceCollection AddSubscriptionBuilder<T, TOptions>(
        this IServiceCollection          services,
        SubscriptionBuilder<T, TOptions> builder
    ) where T : EventSubscription<TOptions> where TOptions : SubscriptionOptions {
        // if (services.Any(x => x is NamedDescriptor named && named.Name == builder.SubscriptionId)) {
        //     throw new InvalidOperationException(
        //         $"Existing subscription builder with id {builder.SubscriptionId} already registered"
        //     );
        // }

        // var descriptor = new NamedDescriptor(builder.SubscriptionId, typeof(SubscriptionBuilder<T, TOptions>), builder);

        services.AddKeyedSingleton(builder.SubscriptionId, builder);
        // services.Add(descriptor);
        services.Configure(builder.SubscriptionId, builder.ConfigureOptions);
        return services;
    }

    public static SubscriptionBuilder<T, TOptions> GetSubscriptionBuilder<T, TOptions>(
        this IServiceProvider provider,
        string                subscriptionId
    ) where T : EventSubscription<TOptions> where TOptions : SubscriptionOptions {
        return provider.GetRequiredKeyedService<SubscriptionBuilder<T, TOptions>>(subscriptionId);
        // var services = provider.GetServices<SubscriptionBuilder<T, TOptions>>();
        // return services.Single(x => x.SubscriptionId == subscriptionId);
    }
}

// class NamedDescriptor(string name, Type serviceType, object instance) : ServiceDescriptor(serviceType, instance) {
    // public string Name { get; } = name;
// }