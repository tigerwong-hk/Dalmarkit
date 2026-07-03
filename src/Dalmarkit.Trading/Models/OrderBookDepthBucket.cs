namespace Dalmarkit.Trading.Models;

public class OrderBookDepthBucket
{
    public decimal Price { get; set; }
    public decimal HighPrice { get; set; }
    public decimal LowPrice { get; set; }
    public decimal Quantity { get; set; }
    public int NumPriceLevels { get; set; }
    public decimal CumulativeQuantity { get; set; }
}
