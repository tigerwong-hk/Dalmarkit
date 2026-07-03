namespace Dalmarkit.Trading.Models;

public readonly record struct OrderBookPriceLevel(decimal Price, decimal Quantity, long UpdateId, long UpdateTimestamp)
{
    public bool IsEmpty => Quantity == 0;
}
