using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FixDateAdded;

/// <summary>
/// Keeps the earliest date added of each movie and episode, and restores it when an upgrade creates a new item.
/// </summary>
internal sealed class DateKeeper : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DateKeeper> _logger;
    private readonly string _path;
    private readonly ConcurrentDictionary<string, DateTime> _dates;
    private readonly Timer _saveTimer;

    public DateKeeper(ILibraryManager libraryManager, IApplicationPaths paths, ILogger<DateKeeper> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _path = Path.Combine(paths.PluginConfigurationsPath, "Jellyfin.Plugin.FixDateAdded.json");
        _dates = File.Exists(_path)
            ? JsonSerializer.Deserialize<ConcurrentDictionary<string, DateTime>>(File.ReadAllText(_path)) ?? new()
            : new();
        _saveTimer = new Timer(_ => Save());
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

    public void Dispose() => _saveTimer.Dispose();

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        // Upgrades delete the old item first; record its date so the replacement can restore it.
        Remember(e.Item);
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        var item = e.Item;
        var key = Remember(item);
        if (key is null)
        {
            return;
        }

        var firstAdded = _dates[key];
        if (firstAdded == item.DateCreated)
        {
            return;
        }

        _logger.LogInformation("Setting date added of {Name} ({Key}) from {Current} to {FirstAdded}", item.Name, key, item.DateCreated, firstAdded);
        item.DateCreated = firstAdded;
        item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();
    }

    private string? Remember(BaseItem item)
    {
        var keys = item.GetUserDataKeys();
        // The last key is the item's own ID, which changes on upgrade.
        if (item is not (Movie or Episode) || keys.Count < 2)
        {
            return null;
        }

        var key = keys[0];
        _dates.AddOrUpdate(key, item.DateCreated, (_, d) => d < item.DateCreated ? d : item.DateCreated);
        _saveTimer.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        return key;
    }

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_dates));
            File.Move(tmp, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save {Path}", _path);
        }
    }
}
