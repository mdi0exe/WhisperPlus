using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace WhisperPlus.Services;

/// <summary>
/// DI-aware page provider for WPF-UI's <see cref="NavigationView"/>. Instead of letting
/// NavigationView new up pages with a parameterless constructor, this provider hands out
/// page instances resolved from the application's service container (so each page gets its
/// services injected). Instances are cached per type so a page keeps its state across
/// navigations — which is what we want for a settings UI.
/// </summary>
public sealed class DiPageProvider : INavigationViewPageProvider
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<Type, object> _cache = new();

    public DiPageProvider(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>
    /// Returns a cached page instance for <paramref name="pageType"/>, creating it via DI
    /// on first access. Returns null if the type isn't a known page.
    /// </summary>
    public object? GetPage(Type pageType)
    {
        if (_cache.TryGetValue(pageType, out var cached)) return cached;

        try
        {
            var page = ActivatorUtilities.CreateInstance(_services, pageType);
            _cache[pageType] = page;
            return page;
        }
        catch
        {
            return null;
        }
    }
}
