using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FixDateAdded;

/// <summary>
/// Keeps the earliest date added of each movie and episode, and restores it when an upgrade creates a new item.
/// </summary>
internal sealed partial class DateKeeper : IHostedService, IDisposable
{
    // How long an ItemRemoved keeps a key eligible for DateCreated restore.
    private static readonly TimeSpan PendingWindow = TimeSpan.FromMinutes(15);

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DateKeeper> _logger;
    private readonly string _path;
    private readonly ConcurrentDictionary<string, DateTime> _dates;
    private readonly ConcurrentDictionary<string, DateTime> _pendingRemovals = new(StringComparer.Ordinal);
    private readonly Timer _saveTimer;

    public DateKeeper(ILibraryManager libraryManager, IApplicationPaths paths, ILogger<DateKeeper> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _path = Path.Combine(paths.PluginConfigurationsPath, "Jellyfin.Plugin.FixDateAdded.json");
        _dates = File.Exists(_path)
            ? JsonSerializer.Deserialize<ConcurrentDictionary<string, DateTime>>(File.ReadAllText(_path)) ?? new(StringComparer.Ordinal)
            : new(StringComparer.Ordinal);
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
        var keys = Remember(e.Item);
        if (keys.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var key in keys)
        {
            _pendingRemovals[key] = now;
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        var item = e.Item;
        var keys = Remember(item);
        if (keys.Count == 0)
        {
            return;
        }

        // Only restore after a recent remove (upgrade). Avoid rewriting dates during full library refreshes.
        if (!TryGetPendingFirstAdded(keys, out var firstAdded))
        {
            return;
        }

        if (firstAdded >= item.DateCreated)
        {
            return;
        }

        _logger.LogInformation("Setting date added of {Name} ({Key}) from {Current} to {FirstAdded}", item.Name, keys[0], item.DateCreated, firstAdded);
        item.DateCreated = firstAdded;
        item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();

        foreach (var key in keys)
        {
            _pendingRemovals.TryRemove(key, out _);
        }
    }

    private bool TryGetPendingFirstAdded(IReadOnlyList<string> keys, out DateTime firstAdded)
    {
        firstAdded = DateTime.MaxValue;
        var cutoff = DateTime.UtcNow - PendingWindow;
        var pending = false;

        foreach (var key in keys)
        {
            if (_pendingRemovals.TryGetValue(key, out var removedAt))
            {
                if (removedAt < cutoff)
                {
                    _pendingRemovals.TryRemove(key, out _);
                }
                else
                {
                    pending = true;
                }
            }

            if (_dates.TryGetValue(key, out var date) && date < firstAdded)
            {
                firstAdded = date;
            }
        }

        return pending && firstAdded != DateTime.MaxValue;
    }

    private IReadOnlyList<string> Remember(BaseItem item)
    {
        if (item is not (Movie or Episode))
        {
            return [];
        }

        var keys = GetStableKeys(item);
        if (keys.Count == 0)
        {
            _logger.LogWarning(
                "Skipping {Type} {Name}: no stable key. Path={Path} ProviderIds={ProviderIds} UserDataKeys={UserDataKeys}",
                item.GetType().Name,
                item.Name,
                item.Path,
                FormatProviderIds(item),
                string.Join(',', item.GetUserDataKeys()));
            return [];
        }

        foreach (var key in keys)
        {
            _dates.AddOrUpdate(key, item.DateCreated, (_, d) => d < item.DateCreated ? d : item.DateCreated);
        }

        // Keep every key for this title on the earliest date we know.
        var earliest = keys.Select(k => _dates[k]).Min();
        foreach (var key in keys)
        {
            _dates[key] = earliest;
        }

        _saveTimer.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        return keys;
    }

    private static List<string> GetStableKeys(BaseItem item)
    {
        var keys = new List<string>();
        var userDataKeys = item.GetUserDataKeys();
        // The last key is the item's own ID, which changes on upgrade.
        for (var i = 0; i < userDataKeys.Count - 1; i++)
        {
            AddKey(keys, userDataKeys[i]);
        }

        AddKey(keys, item.GetProviderId(MetadataProvider.Imdb));
        AddKey(keys, item.GetProviderId(MetadataProvider.Tmdb));

        // Movie folder tags ({imdb-…} / [imdbid-…]). Do not parse episode paths: the series id
        // would collide across every episode of that show.
        if (item is Movie)
        {
            AddKeysFromPath(keys, item.Path);
        }

        return keys;
    }

    private static void AddKeysFromPath(List<string> keys, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        foreach (Match match in PathIdRegex().Matches(path))
        {
            var id = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            AddKey(keys, id);
        }
    }

    private static void AddKey(List<string> keys, string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (!keys.Contains(key, StringComparer.Ordinal))
        {
            keys.Add(key);
        }
    }

    private static string FormatProviderIds(BaseItem item)
        => item.ProviderIds is null || item.ProviderIds.Count == 0
            ? "(none)"
            : string.Join(',', item.ProviderIds.Select(kv => kv.Key + '=' + kv.Value));

    // {imdb-tt…}, [imdbid-…], {tmdb-…}, [tmdbid-…], and the same for tvdb.
    [GeneratedRegex(@"\{(?:imdb|tmdb|tvdb)-([^}/]+)\}|\[(?:imdb|tmdb|tvdb)id-([^\]/]+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathIdRegex();

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
