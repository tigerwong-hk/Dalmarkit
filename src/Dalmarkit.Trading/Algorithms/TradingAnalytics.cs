using Dalmarkit.Trading.Models;

namespace Dalmarkit.Trading.Algorithms;

public static class TradingAnalytics
{
    public const decimal BucketSizeMax = 5000m;
    public const decimal BucketSizeMin = 0.000000001m;
    public const int MarketDepthBucketsMax = 30;
    public const decimal PriceMax = 100000000m;
    public const decimal TargetNotionalValueMax = 1000000000m;
    public const decimal TargetVolumeMax = 1000000000m;

    public static decimal? GetBestAsk(IEnumerable<OrderBookPriceLevel> asksSortedAscending)
    {
        OrderBookPriceLevel priceLevel = asksSortedAscending.FirstOrDefault();
        return priceLevel == default ? null : priceLevel.Price;
    }

    public static decimal? GetBestBid(IEnumerable<OrderBookPriceLevel> bidsSortedDescending)
    {
        OrderBookPriceLevel priceLevel = bidsSortedDescending.FirstOrDefault();
        return priceLevel == default ? null : priceLevel.Price;
    }

    public static decimal? GetCumulativeVolumeWithinPriceLimit(SortedDictionary<decimal, OrderBookPriceLevel> priceLevelsSortedBySide, decimal priceLimit, bool isBid)
    {
#pragma warning disable IDE0046 // Convert to conditional expression
        if (priceLimit is <= 0m or > PriceMax)
        {
            return null;
        }
#pragma warning restore IDE0046 // Convert to conditional expression

        return priceLevelsSortedBySide.Count > 0 && priceLimit > 0m
            ? priceLevelsSortedBySide.TakeWhile(kvp => isBid
                        ? kvp.Key >= priceLimit
                        : kvp.Key <= priceLimit).Sum(kvp => kvp.Value.Quantity)
            : null;
    }

    public static OrderBookDepthBucket[] GetMarketDepthBuckets(IEnumerable<OrderBookPriceLevel> priceLevelsSortedBySide, decimal bucketSize, int numBuckets, bool isBid)
    {
        if (bucketSize is < BucketSizeMin or > BucketSizeMax)
        {
            return [];
        }

        if (numBuckets is <= 0 or > MarketDepthBucketsMax)
        {
            return [];
        }

        decimal? bestPrice = isBid ? GetBestBid(priceLevelsSortedBySide) : GetBestAsk(priceLevelsSortedBySide);
        if (bestPrice == null)
        {
            return [];
        }

        decimal roundedBestPrice = (isBid ? decimal.Ceiling(bestPrice.Value / bucketSize) : decimal.Floor(bestPrice.Value / bucketSize)) * bucketSize;

        decimal startPrice = isBid ? roundedBestPrice - (numBuckets * bucketSize) : roundedBestPrice;
        decimal endPrice = isBid ? roundedBestPrice : roundedBestPrice + (numBuckets * bucketSize);

        OrderBookDepthBucket[] depthBuckets = new OrderBookDepthBucket[numBuckets];
        for (int i = 0; i < numBuckets; i++)
        {
            OrderBookDepthBucket depthBucket;
            if (isBid)
            {
                decimal highPrice = endPrice - (i * bucketSize);
                decimal lowPrice = highPrice - bucketSize;
                depthBucket = new()
                {
                    HighPrice = highPrice,
                    LowPrice = lowPrice,
                    Price = lowPrice,
                };
            }
            else
            {
                decimal lowPrice = startPrice + (i * bucketSize);
                decimal highPrice = lowPrice + bucketSize;
                depthBucket = new()
                {
                    HighPrice = highPrice,
                    LowPrice = lowPrice,
                    Price = highPrice,
                };
            }

            depthBuckets[i] = depthBucket;
        }

        foreach (OrderBookPriceLevel priceLevel in priceLevelsSortedBySide)
        {
            decimal price = priceLevel.Price;
            if (isBid && price < startPrice)
            {
                break;
            }
            if (!isBid && price > endPrice)
            {
                break;
            }

            int bucketIndex = GetMarketDepthBucketIndex(price, startPrice, endPrice, bucketSize, isBid);
            if (bucketIndex >= numBuckets)
            {
                continue;
            }

            OrderBookDepthBucket currentBucket = depthBuckets[bucketIndex];
            currentBucket.Quantity += priceLevel.Quantity;
            currentBucket.NumPriceLevels++;
        }

        decimal cumulativeQuantity = 0m;
        for (int i = 0; i < numBuckets; i++)
        {
            OrderBookDepthBucket currentBucket = depthBuckets[i];
            cumulativeQuantity += currentBucket.Quantity;
            currentBucket.CumulativeQuantity = cumulativeQuantity;
        }

        return depthBuckets;
    }

    public static decimal? GetMidPrice(IEnumerable<OrderBookPriceLevel> bidsSortedDescending, IEnumerable<OrderBookPriceLevel> asksSortedAscending)
    {
        decimal? bestBid = GetBestBid(bidsSortedDescending);
        decimal? bestAsk = GetBestAsk(asksSortedAscending);

        return bestBid.HasValue && bestAsk.HasValue ? (bestBid + bestAsk) / 2m : null;
    }

    public static decimal? GetSpread(IEnumerable<OrderBookPriceLevel> bidsSortedDescending, IEnumerable<OrderBookPriceLevel> asksSortedAscending)
    {
        decimal? bestBid = GetBestBid(bidsSortedDescending);
        decimal? bestAsk = GetBestAsk(asksSortedAscending);

        return bestBid.HasValue && bestAsk.HasValue ? bestAsk - bestBid : null;
    }

    public static decimal? GetVolumeAtPrice(SortedDictionary<decimal, OrderBookPriceLevel> priceLevels, decimal price)
    {
        if (priceLevels.Count == 0)
        {
            return null;
        }

        if (price is <= 0m or > PriceMax)
        {
            return null;
        }

        bool hasLevel = priceLevels.TryGetValue(price, out OrderBookPriceLevel priceLevel);
        return hasLevel ? priceLevel.Quantity : null;
    }

    public static (decimal? Vwap, decimal? LastPrice, decimal? CumulativeVolume) GetVwapByVolume(IEnumerable<OrderBookPriceLevel> priceLevelsSortedBySide, decimal targetVolume)
    {
        if (targetVolume is <= 0m or > TargetVolumeMax)
        {
            return (null, null, null);
        }

        decimal cumulativeNotionalValue = 0m;
        decimal cumulativeVolume = 0m;
        decimal lastPrice = 0m;

        foreach (OrderBookPriceLevel priceLevel in priceLevelsSortedBySide)
        {
            decimal remainingVolume = targetVolume - cumulativeVolume;
            if (remainingVolume <= priceLevel.Quantity)
            {
                cumulativeNotionalValue += priceLevel.Price * remainingVolume;
                cumulativeVolume += remainingVolume;
                lastPrice = priceLevel.Price;
                break;
            }

            cumulativeNotionalValue += priceLevel.Price * priceLevel.Quantity;
            cumulativeVolume += priceLevel.Quantity;
            lastPrice = priceLevel.Price;
        }

        return (cumulativeVolume > 0m ? cumulativeNotionalValue / cumulativeVolume : null,
            cumulativeVolume > 0m ? lastPrice : null, cumulativeVolume > 0m ? cumulativeVolume : null);
    }

    public static (decimal? Vwap, decimal? LastPrice, decimal? CumulativeNotionalValue) GetVwapByNotionalValue(IEnumerable<OrderBookPriceLevel> priceLevelsSortedBySide, decimal targetNotionalValue)
    {
        if (targetNotionalValue is <= 0m or > TargetNotionalValueMax)
        {
            return (null, null, null);
        }

        decimal cumulativeNotionalValue = 0m;
        decimal cumulativeVolume = 0m;
        decimal lastPrice = 0m;

        foreach (OrderBookPriceLevel priceLevel in priceLevelsSortedBySide)
        {
            decimal remainingNotionalValue = targetNotionalValue - cumulativeNotionalValue;
            decimal priceLevelNotionalValue = priceLevel.Price * priceLevel.Quantity;
            if (remainingNotionalValue <= priceLevelNotionalValue)
            {
                cumulativeNotionalValue += remainingNotionalValue;
                cumulativeVolume += remainingNotionalValue / priceLevel.Price;
                lastPrice = priceLevel.Price;
                break;
            }

            cumulativeNotionalValue += priceLevelNotionalValue;
            cumulativeVolume += priceLevel.Quantity;
            lastPrice = priceLevel.Price;
        }

        return (cumulativeVolume > 0m ? cumulativeNotionalValue / cumulativeVolume : null,
            cumulativeVolume > 0m ? lastPrice : null, cumulativeVolume > 0m ? cumulativeNotionalValue : null);
    }

    private static int GetMarketDepthBucketIndex(decimal price, decimal startPrice, decimal endPrice, decimal bucketSize, bool isBid)
    {
#pragma warning disable IDE0046 // Convert to conditional expression
        if (isBid)
        {
            return price > endPrice ? 0 : (int)((endPrice - price) / bucketSize);
        }
#pragma warning restore IDE0046 // Convert to conditional expression

        return price < startPrice ? 0 : (int)((price - startPrice) / bucketSize);
    }
}
