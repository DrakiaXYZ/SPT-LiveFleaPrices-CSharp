namespace DrakiaXYZ.LiveFleaPrices;

internal class Config
{
    public long nextUpdate { get; set; }
    public int maxIncreaseMult { get; set; }
    public bool maxLimiter { get; set; }
    public bool pvePrices { get; set; }
    public bool disablePriceFetching { get; set; }
    public bool debug { get; set; }
}