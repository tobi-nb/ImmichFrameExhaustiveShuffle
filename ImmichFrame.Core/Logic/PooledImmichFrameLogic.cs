using ImmichFrame.Core.Api;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic.Pool;
using System.Collections.Generic;
using System.Linq;
using ImmichFrame.Core.Logic.Rotation;
using Microsoft.Extensions.Logging;

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

        _logger?.LogInformation("PooledImmichFrameLogic initialized. ExhaustiveShuffle={Flag}, Pool={Pool}",
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
                _logger?.LogInformation("ExhaustiveShuffle selected asset {AssetId}", asset.Id);
                return asset;
            }
            catch (InvalidOperationException)
            {
                _logger?.LogInformation("ExhaustiveShuffle exhausted, falling back to pool random.");
                return (await _pool.GetAssets(1)).FirstOrDefault();
            }
        }

        if (_generalSettings.ExhaustiveShuffle && isAllAssets)
        {
            _logger?.LogInformation("ExhaustiveShuffle requested, but AllAssetsPool delivers SearchRandom → falling back.");
        }

        var random = (await _pool.GetAssets(1)).FirstOrDefault();
        _logger?.LogInformation("Default shuffle selected asset {AssetId}", random?.Id);
        return random;
    }

	public async Task<IEnumerable<AssetResponseDto>> GetAssets()
	{
		if (_generalSettings.ExhaustiveShuffle)
		{
			var batch = new List<AssetResponseDto>(25);

			// hole alle verfügbaren Kandidaten (einmalig)
			var total = await _pool.GetAssetCount();
			var allAssets = (await _pool.GetAssets((int)total)).ToList();

			if (!allAssets.Any())
				return Enumerable.Empty<AssetResponseDto>();

			// mische durch für Zufall
			var rng = new Random();
			for (int i = allAssets.Count - 1; i > 0; i--)
			{
				int j = rng.Next(i + 1);
				(allAssets[i], allAssets[j]) = (allAssets[j], allAssets[i]);
			}

			// jetzt auffüllen bis 25, indem wir durch die Liste "rotieren"
			int index = 0;
			while (batch.Count < 25)
			{
				var asset = allAssets[index % allAssets.Count];
				batch.Add(asset);
				_logger?.LogInformation("Batch ExhaustiveShuffle selected asset {AssetId}", asset.Id);
				index++;
			}

			return batch;
		}
		else
		{
			return await _pool.GetAssets(25);
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
                    _logger?.LogInformation("Serving cached image for {AssetId}", id);
                    return (Path.GetFileName(file), $"image/{ex}", fs);
                }

                _logger?.LogInformation("Cached image expired for {AssetId}, deleting.", id);
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
            _logger?.LogInformation("Downloaded and cached new image {AssetId}", id);
            return (Path.GetFileName(filePath), contentType, fs);
        }

        _logger?.LogInformation("Serving streamed image for {AssetId}", id);
        return (fileName, contentType, data.Stream);
    }

    public Task SendWebhookNotification(IWebhookNotification notification)
    {
        _logger?.LogInformation("Sending webhook notification to {Webhook}", _generalSettings.Webhook);
        return WebhookHelper.SendWebhookNotification(notification, _generalSettings.Webhook);
    }

    public override string ToString() => $"Account Pool [{_immichApi.BaseUrl}]";
}
