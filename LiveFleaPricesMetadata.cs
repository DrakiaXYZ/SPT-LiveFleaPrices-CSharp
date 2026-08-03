using SPTarkov.Server.Core.Models.Spt.Mod;

namespace DrakiaXYZ.LiveFleaPrices;

public record LiveFleaPricesMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "xyz.drakia.livefleaprices";
    public string Name { get; init; } = "Live Flea Prices";
    public string Author { get; init; } = "DrakiaXYZ";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("2.1.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public bool? IsBundleMod { get; init; }
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; }
}