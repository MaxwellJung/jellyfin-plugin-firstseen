using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.FixDateAdded;

/// <summary>
/// Keeps the earliest date added of each movie and episode, and restores it when an upgrade creates a new item.
/// </summary>
internal sealed class DateKeeper : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly string _path;
    private readonly Dictionary<string, DateTime> _dates;

    public DateKeeper(ILibraryManager libraryManager, IApplicationPaths paths)
    {
        _libraryManager = libraryManager;
        _path = Path.Combine(paths.PluginConfigurationsPath, "Jellyfin.Plugin.FixDateAdded.json");
        _dates = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_path)) ?? new()
            : new();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemUpdated += OnItemUpdated;
        _libraryManager.ItemRemoved += OnItemRemoved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        Save();
        return Task.CompletedTask;
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e) => Remember(e.Item);

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        var item = e.Item;
        var keys = Remember(item);
        if (keys.Count == 0)
        {
            return;
        }

        var firstAdded = keys.Min(k => _dates[k]);
        if (firstAdded >= item.DateCreated)
        {
            return;
        }

        item.DateCreated = firstAdded;
        item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();
    }

    private List<string> Remember(BaseItem item)
    {
        if (item is not (Movie or Episode))
        {
            return [];
        }

        // Last user-data key is the item GUID (changes on upgrade).
        var keys = item.GetUserDataKeys().SkipLast(1)
            .Append(item.GetProviderId(MetadataProvider.Imdb))
            .Append(item.GetProviderId(MetadataProvider.Tmdb))
            .Where(static k => !string.IsNullOrEmpty(k))
            .Select(static k => k!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (keys.Count == 0)
        {
            return keys;
        }

        var earliest = item.DateCreated;
        foreach (var key in keys)
        {
            if (_dates.TryGetValue(key, out var existing) && existing < earliest)
            {
                earliest = existing;
            }
        }

        foreach (var key in keys)
        {
            _dates[key] = earliest;
        }

        Save();
        return keys;
    }

    private void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_dates));
}
