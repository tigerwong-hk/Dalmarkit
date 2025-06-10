using Dalmarkit.Common.Validation;

namespace Dalmarkit.Blockchain.Evm.Services;

public class EvmBlockchainOptions
{
    public IDictionary<string, string>? ContractAbis { get; set; }
    public IDictionary<string, string>? RpcUrls { get; set; }

    public void Validate()
    {
        _ = Guard.NotNull(RpcUrls, nameof(RpcUrls));

        if (ContractAbis?.Count < 1)
        {
            throw new ArgumentException("At least one contract ABI must be provided.", nameof(ContractAbis));
        }

        if (RpcUrls!.Count < 1)
        {
            throw new ArgumentException("At least one RPC URL must be provided.", nameof(RpcUrls));
        }
    }
}
