using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using ImmichFrame.Core.Api;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic.Pool;
using ImmichFrame.Core.Logic.Rotation;

namespace ImmichFrame.Core.Logic;

public class PooledImmichFrameLogic : IAccountImmichFrameLogic
{
    private readonly IGeneralSettings _generalSettings;
    private readonly IApiCache _apiCache;
    private readonly IAssetPool _pool;
    private readonly ImmichApi _immichApi;
    private readonly ILogger<PooledImmichFrameLogic>? _logger;
    private readonly ExhaustiveRotationStrategy<AssetResponseDto> _exhaustiveStrategy;
    private readonly string _rotationKey;
    private readonly string _downloadLocation = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImageCache");

    public PooledImmichFrameLogic(
        IAccountSettings accountSettings,
        IGeneralSettings generalSettings,
        IHttpClientFactory httpClientFactory,
        ILogger<PooledImmichFrameLogic>? logger = null)
    {
        _logger = logger;
        _generalSettings = generalSettings;

        var httpClient = httpClientFactory.CreateClient("ImmichApiAccountClient");
        AccountSettings = accountSettings;

        httpClient.UseApiKey(accountSettings.ApiKey);
        _immichApi = new ImmichApi(accountSettings.ImmichServerUrl, httpClient);

        _apiCache = new ApiCache(RefreshInterval(generalSettings.RefreshAlbumPeopleInterval));
        _pool = BuildPool(accountSettings);
        _rotationKey = AccountSettings.ImmichServerUrl ?? "default";

        IEnumerable<AssetResponseDto> CandidatesProvider()
        {
            var total = _pool.GetAssetCount().GetAwaiter().GetResult();
            if (total <= 0) return Enumerable.Empty<AssetResponseDto>();
            var list = _pool.GetAssets((int)total).GetAwaiter().GetResult();
            return list ?? Enumerable.Empty<AssetResponseDto>();
        }

        _exhaustiveStrategy = new ExhaustiveRotationStrategy<AssetResponseDto>(CandidatesProvider);

        _logger?.LogDebug("PooledImmichFrameLogic initialized. ExhaustiveShuffle={Flag}, Pool={Pool}",
            _generalSettings.ExhaustiveShuffle, _pool.GetType().Name);
    }

    private static TimeSpan RefreshInterval(int hours)
        => hours > 0 ? TimeSpan.FromHours(hours) : TimeSpan.FromMilliseconds(1);

    public IAccountSettings AccountSettings { get; }

    private IAssetPool BuildPool(IAccountSettings accountSettings)
    {
        if (!accountSettings.ShowFavorites && !accountSettings.ShowMemories && !accountSettings.Albums.Any() && !accountSettings.People.Any())
        {
            return new AllAssetsPool(_apiCache, _immichApi, accountSettings);
        }

        var pools = new List<IAssetPool>();

        if (accountSettings.ShowFavorites)
            pools.Add(new FavoriteAssetsPool(_apiCache, _immichApi, accountSettings));

        if (accountSettings.ShowMemories)
            pools.Add(new MemoryAssetsPool(_immichApi, accountSettings));

        if (accountSettings.Albums.Any())
            pools.Add(new AlbumAssetsPool(_apiCache, _immichApi, accountSettings));

        if (accountSettings.People.Any())
            pools.Add(new PersonAssetsPool(_apiCache, _immichApi, accountSettings));

        return new MultiAssetPool(pools);
    }

    public async Task<AssetResponseDto?> GetNextAsset()
    {
        var isAllAssets = _pool.GetType().Name.Contains("AllAssetsPool", StringComparison.OrdinalIgnoreCase);

        if (_generalSettings.ExhaustiveShuffle && !isAllAssets)
        {
            try
            {
                var asset = _exhaustiveStrategy.Next(_rotationKey);
                _logger?.LogDebug("ExhaustiveShuffle selected asset {AssetId}", asset.Id);
                return asset;
            }
            catch (InvalidOperationException)
            {
                _logger?.LogDebug("ExhaustiveShuffle exhausted, falling back to pool random.");
                return (await _pool.GetAssets(1)).FirstOrDefault();
            }
        }

        if (_generalSettings.ExhaustiveShuffle && isAllAssets)
        {
            _logger?.LogDebug("ExhaustiveShuffle requested, but AllAssetsPool delivers SearchRandom → falling back.");
        }

        var random = (await _pool.GetAssets(1)).FirstOrDefault();
        _logger?.LogDebug("Default shuffle selected asset {AssetId}", random?.Id);
        return random;
    }

    // Fisher-Yates in-place shuffle
    private static void ShuffleInPlace<T>(IList<T> list, Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // Holt robuste, eindeutige Kandidaten (Pool kann random sein → mehrfach ziehen, bis wir alle haben)
    private async Task<List<AssetResponseDto>> LoadUniqueCandidatesAsync()
    {
        var total = await _pool.GetAssetCount();
        if (total <= 0) return new List<AssetResponseDto>();

        var byId = new Dictionary<string, AssetResponseDto>();
        int guard = 0;

        // Ziehe wiederholt Batches, bis nahezu alle eindeutigen Kandidaten gesammelt sind
        while (byId.Count < total && guard++ < 200)
        {
            var batchSize = (int)Math.Min(25, Math.Max(1, total));
            var batch = await _pool.GetAssets(batchSize);

            foreach (var a in batch)
            {
                if (!byId.ContainsKey(a.Id))
                    byId[a.Id] = a;
            }

            if (total <= 25 && byId.Count == total) break;
        }

        _logger?.LogDebug("Collected {Collected}/{Total} unique candidates", byId.Count, total);
        return byId.Values.ToList();
    }

    public async Task<IEnumerable<AssetResponseDto>> GetAssets()
    {
        if (_generalSettings.ExhaustiveShuffle)
        {
            // 1) Eindeutige Kandidaten robust einsammeln (unabhängig von Pool-Randomness)
            var candidates = await LoadUniqueCandidatesAsync();

            // Fallback, falls der Pool wirklich nichts liefert
            if (candidates.Count == 0)
            {
                var fallback = await _pool.GetAssets(25);
                _logger?.LogDebug("Returning random fallback batch of {Count} assets", fallback.Count());
                return fallback;
            }

            // 2) Auf 25 auffüllen – jede Runde neu mischen, dann anhängen
            var rng = new Random();
            var batch = new List<AssetResponseDto>(25);

            while (batch.Count < 25)
            {
                ShuffleInPlace(candidates, rng);
                foreach (var a in candidates)
                {
                    batch.Add(a);
                    _logger?.LogDebug("Batch ExhaustiveShuffle selected asset {AssetId}", a.Id);
                    if (batch.Count >= 25) break;
                }
            }

            _logger?.LogDebug("Returning batch with {Count} items (unique base {Unique})", batch.Count, candidates.Count);
            return batch;
        }
        else
        {
            var randomBatch = await _pool.GetAssets(25);
            _logger?.LogDebug("Returning random batch of {Count} assets", randomBatch.Count());
            return randomBatch;
        }
    }

    public Task<AssetResponseDto> GetAssetInfoById(Guid assetId) => _immichApi.GetAssetInfoAsync(assetId, null);

    public async Task<IEnumerable<AlbumResponseDto>> GetAlbumInfoById(Guid assetId) => await _immichApi.GetAllAlbumsAsync(assetId, null);

    public Task<long> GetTotalAssets() => _pool.GetAssetCount();

    public async Task<(string fileName, string ContentType, Stream fileStream)> GetImage(Guid id)
    {
        if (_generalSettings.DownloadImages)
        {
            if (!Directory.Exists(_downloadLocation))
            {
                Directory.CreateDirectory(_downloadLocation);
            }

            var file = Directory.GetFiles(_downloadLocation)
                .FirstOrDefault(x => Path.GetFileNameWithoutExtension(x) == id.ToString());

            if (!string.IsNullOrWhiteSpace(file))
            {
                if (_generalSettings.RenewImagesDuration > (DateTime.UtcNow - File.GetCreationTimeUtc(file)).Days)
                {
                    var fs = File.OpenRead(file);
                    var ex = Path.GetExtension(file);
                    _logger?.LogDebug("Serving cached image for {AssetId}", id);
                    return (Path.GetFileName(file), $"image/{ex}", fs);
                }

                _logger?.LogDebug("Cached image expired for {AssetId}, deleting.", id);
                File.Delete(file);
            }
        }

        var data = await _immichApi.ViewAssetAsync(id, string.Empty, AssetMediaSize.Preview);

        if (data == null)
            throw new AssetNotFoundException($"Asset {id} was not found!");

        var contentType = "";
        if (data.Headers.ContainsKey("Content-Type"))
        {
            contentType = data.Headers["Content-Type"].FirstOrDefault() ?? "";
        }

        var ext = contentType.ToLower() == "image/webp" ? "webp" : "jpeg";
        var fileName = $"{id}.{ext}";

        if (_generalSettings.DownloadImages)
        {
            var stream = data.Stream;
            var filePath = Path.Combine(_downloadLocation, fileName);

            var fs = File.Create(filePath);
            await stream.CopyToAsync(fs);
            fs.Position = 0;
            _logger?.LogDebug("Downloaded and cached new image {AssetId}", id);
            return (Path.GetFileName(filePath), contentType, fs);
        }

        _logger?.LogDebug("Serving streamed image for {AssetId}", id);
        return (fileName, contentType, data.Stream);
    }

    public Task SendWebhookNotification(IWebhookNotification notification)
    {
        _logger?.LogDebug("Sending webhook notification to {Webhook}", _generalSettings.Webhook);
        return WebhookHelper.SendWebhookNotification(notification, _generalSettings.Webhook);
    }

    public override string ToString() => $"Account Pool [{_immichApi.BaseUrl}]";
}
