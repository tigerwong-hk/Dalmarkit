using Dalmarkit.Blockchain.Constants;
using Newtonsoft.Json;

namespace Dalmarkit.Blockchain.Evm.Services;

public class EvmEventDto
{
    public BlockchainNetwork BlockchainNetwork { get; set; }

    public string BlockHash { get; set; } = null!;

    public string BlockNumber { get; set; } = null!;

    [JsonProperty("address")]
    public string ContractAddress { get; set; } = null!;

    [JsonProperty("data")]
    public string EventData { get; set; } = null!;

    public string EventJson { get; set; } = null!;

    public string EventName { get; set; } = null!;

    public string LogIndex { get; set; } = null!;

    public List<string> Topics { get; set; } = null!;

    public string TransactionHash { get; set; } = null!;

    public string TransactionIndex { get; set; } = null!;
}
