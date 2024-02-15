namespace Dalmarkit.Blockchain.Evm.Services;

public class EvmBlockchainOptions
{
    public IDictionary<string, string>? ContractAbis { get; set; }
    public IDictionary<string, string>? RpcUrls { get; set; }
}
