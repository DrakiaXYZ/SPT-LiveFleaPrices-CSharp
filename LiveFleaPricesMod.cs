using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using System.Reflection;

namespace DrakiaXYZ.LiveFleaPrices;

// Load right before the ragfair does, so we can set the prices first
[Injectable(TypePriority = OnLoadOrder.RagfairCallbacks - 2)]
public class LiveFleaPricesMod(
	ISptLogger<LiveFleaPricesMod> logger,
	DatabaseService database,
    RagfairOfferService ragfairOfferService,
    RagfairOfferGenerator ragfairOfferGenerator,
    ConfigServer configServer,
    ModHelper modHelper,
    TraderHelper traderHelper,
    RagfairOfferHolder ragfairOfferHolder,
    FileUtil fileUtil,
    JsonUtil jsonUtil,
    ICloner cloner) : IOnLoad
{
    private Config config = new();
    private HashSet<MongoId> blacklist = new();
    private Dictionary<MongoId, double> originalPrices = new();

    private string configFolderPath = Path.Join(modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly()), "config");

    private readonly RagfairConfig ragfairConfig = configServer.GetConfig<RagfairConfig>();

    public async Task OnLoad()
	{
        try
        {
            config = modHelper.GetJsonDataFromFile<Config>(configFolderPath, "config.json");
            blacklist = modHelper.GetJsonDataFromFile<List<MongoId>>(configFolderPath, "blacklist.json").ToHashSet();
        }
        catch (Exception ex)
        {
            logger.Error("Error loading Live Flea Prices config data. Disabling LiveFleaPrices", ex);
            return;
        }

        // Disable SPT's built in flea price calculations
        ragfairConfig.Dynamic.GenerateBaseFleaPrices.UseHandbookPrice = false;

        // Store a clone of the original prices table, so we can make sure things don't go too crazy
        var priceTable = database.GetTables().Templates.Prices;
        originalPrices = cloner.Clone(priceTable)!;

        // Update prices on startup
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fetchPrices = false;
        if (!config.disablePriceFetching && currentTime > config.nextUpdate)
        {
            fetchPrices = true;
        }

        if (!await UpdatePrices(fetchPrices, false))
        {
            return;
        }

        // Setup a refresh interval to update once every hour if updates aren't disabled
        if (!config.disablePriceFetching)
        {
            var updateTask = new Task(async () =>
            {
                while (true)
                {
                    Thread.Sleep(60 * 60 * 1000);
                    await UpdatePrices(true, true);
                }
            });
            updateTask.Start();
        }

        return;
	}

    private async Task<bool> UpdatePrices(bool fetchPrices = true, bool regenerateRagfair = false)
    {
        var priceTable = database.GetTables().Templates.Prices;
        var handbookTable = database.GetTables().Templates.Handbook;
        var itemTable = database.GetTables().Templates.Items;
        var gameMode = config.pvePrices ? "pve" : "regular";

        var pricesPath = Path.Join(configFolderPath, $"prices-{gameMode}.json");
        Dictionary<string, int>? newPrices = null;

        // Fetch the latest prices-{gameMode}.json if we're triggered with fetch enabled, or the prices file doesn't exist
        if (fetchPrices || !fileUtil.FileExists(pricesPath))
        {
            logger.Info($"[LiveFleaPrices] Fetching Flea Prices for gamemode {gameMode}...");
            var response = await GetWithRetries($"https://raw.githubusercontent.com/DrakiaXYZ/SPT-LiveFleaPriceDB/main/prices-{gameMode}.json", 5);
            if (response != null)
            {
                newPrices = jsonUtil.Deserialize<Dictionary<string, int>>(response);

                // Store the prices to disk for next time
                if (newPrices != null)
                {
                    await fileUtil.WriteFileAsync(pricesPath, response);

                    // Update config file with the next update time
                    config.nextUpdate = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
                    var configPath = Path.Join(configFolderPath, "config.json");
                    var configJson = jsonUtil.Serialize(config, true);
                    if (configJson != null)
                    {
                        await fileUtil.WriteFileAsync(configPath, configJson);
                    }
                }
            }

            // If we failed to retrieve or parse the prices, fall back to the on-disk prices file
            if (newPrices == null)
            {
                logger.Error("[LiveFleaPrices] Error fetching or parsing flea prices");
                logger.Error("[LiveFleaPrices] This is unlikely due to the mod, and more likely due to a system configuration issue");

                if (fileUtil.FileExists(pricesPath))
                {
                    logger.Success("[LiveFleaPrices] Falling back to existing prices file");
                    newPrices = modHelper.GetJsonDataFromFile<Dictionary<string, int>>(configFolderPath, $"prices-{gameMode}.json");
                }
                else
                {
                    logger.Error("[LiveFleaPrices] Unable to fetch prices, and no local prices file. Skipping LiveFleaPrices");
                    return false;
                }
            }
        }
        else
        {
            // Otherwise, read the file from disk
            newPrices = modHelper.GetJsonDataFromFile<Dictionary<string, int>>(configFolderPath, $"prices-{gameMode}.json");
        }

        // Loop through the new prices file, updating all prices present
        foreach (var item in newPrices)
        {
            var itemId = item.Key;
            var itemPrice = item.Value;

            // Skip any price that doesn't exist in the item table
            if (!itemTable.ContainsKey(itemId))
            {
                continue;
            }

            // Skip any item that's blacklisted
            if (blacklist.Contains(itemId))
            {
                if (config.debug)
                {
                    logger.Debug($"[LiveFleaPrices] Item {itemId} was skipped due to it being blacklisted.");
                }
                continue;
            }

            if (!originalPrices.TryGetValue(itemId, out var basePrice))
            {
                basePrice = handbookTable.Items.SingleOrDefault(x => x.Id == itemId)?.Price ?? 0;
                originalPrices[itemId] = basePrice;
            }

            var maxPrice = basePrice * config.maxIncreaseMult;
            if (maxPrice != 0 && (!config.maxLimiter || itemPrice <= maxPrice))
            {
                priceTable[itemId] = itemPrice;
            }
            else
            {
                if (config.debug)
                {
                    logger.Debug($"[LiveFleaPrices] Setting {itemId} to {maxPrice} instead of {itemPrice} due to over inflation");
                }
                priceTable[itemId] = maxPrice;
            }

            // Special handling in the event `UseTraderPriceForOffersIfHigher` is enabled, to fix issues selling items
            if (ragfairConfig.Dynamic.UseTraderPriceForOffersIfHigher)
            {
                // If the trader price is greater than the flea price, set the flea price to 10% higher than the trader price
                var traderPrice = traderHelper.GetHighestSellToTraderPrice(itemId);
                if (traderPrice > priceTable[itemId])
                {
                    var newPrice = Math.Floor(traderPrice * 1.1);
                    if (config.debug)
                    {
                        logger.Debug($"[LiveFleaPrices] Setting {itemId} to {newPrice} instead of {itemPrice} due to trader price");
                    }
                    priceTable[itemId] = newPrice;
                }
            }
        }

        if (regenerateRagfair)
        {
            // Purge the flea and re-generate all offers, so they use the new prices
            var expiredOfferIds = ragfairOfferHolder.GetStaleOfferIds();
            foreach (var offer in ragfairOfferHolder.GetOffers())
            {
                // Skip trader, or real player offers, as well as already expired offers
                if (offer.IsTraderOffer() || offer.IsPlayerOffer() || expiredOfferIds.Contains(offer.Id))
                {
                    continue;
                }

                ragfairOfferHolder.FlagOfferAsExpired(offer.Id);
            }

            // Remove all expired offers, and trigger a GC to clean up newly freed memory
            ragfairOfferService.RemoveExpiredOffers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, true, true);

            // Re-generate the full player ragfair
            ragfairOfferGenerator.GenerateDynamicOffers();
        }

        return true;
    }

    private async Task<string?> GetWithRetries(string url, int retries)
    {
        for (var i = 0; i <= retries; i++)
        {
            using (HttpClient client = new HttpClient())
            {
                try
                {
                    return await client.GetStringAsync(url);
                }
                catch (Exception e)
                {
                    logger.Error($"[LiveFleaPrices] Error downloading JSON, attempt {i+1}/{retries+1}: {e.Message}");
                }
            }
        }

        return null;
    }
}
