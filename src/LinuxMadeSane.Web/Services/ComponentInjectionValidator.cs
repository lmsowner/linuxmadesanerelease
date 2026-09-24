// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace LinuxMadeSane.Web.Services;

internal static class ComponentInjectionValidator
{
    public static void Validate(IServiceProvider services, Assembly componentAssembly)
        => Validate(
            services,
            componentAssembly
                .GetTypes()
                .Where(type => !type.IsAbstract && typeof(IComponent).IsAssignableFrom(type)));

    internal static void Validate(IServiceProvider services, IEnumerable<Type> componentTypes)
    {
        var serviceLookup = services.GetRequiredService<IServiceProviderIsService>();
        var missingRegistrations = componentTypes
            .SelectMany(GetInjectedProperties)
            .Where(item => !serviceLookup.IsService(item.Property.PropertyType))
            .Select(item => $"{item.Component.FullName}.{item.Property.Name}: {item.Property.PropertyType.FullName}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (missingRegistrations.Length > 0)
        {
            throw new InvalidOperationException(
                "Blazor component services are missing from dependency injection:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, missingRegistrations));
        }
    }

    private static IEnumerable<(Type Component, PropertyInfo Property)> GetInjectedProperties(Type component)
    {
        for (var current = component; current is not null; current = current.BaseType)
        {
            foreach (var property in current.GetProperties(
                         BindingFlags.Instance |
                         BindingFlags.Public |
                         BindingFlags.NonPublic |
                         BindingFlags.DeclaredOnly))
            {
                if (property.IsDefined(typeof(InjectAttribute), inherit: false))
                {
                    yield return (component, property);
                }
            }
        }
    }
}
