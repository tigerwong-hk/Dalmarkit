using Dalmarkit.Common.Validation;

namespace Dalmarkit.Blockchain.Evm.Services;

public class EvmWalletOptions
{
    public string? PrivateKey { get; set; }

    public void Validate()
    {
        _ = Guard.NotNullOrWhiteSpace(PrivateKey, nameof(PrivateKey));
    }
}
