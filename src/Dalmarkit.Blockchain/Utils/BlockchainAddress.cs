using Dalmarkit.Blockchain.Constants;
using Nethereum.Web3;
using System.Text.RegularExpressions;

namespace Dalmarkit.Blockchain.Utils;

public static partial class BlockchainAddress
{
    /// <summary>
    /// https://github.com/web3/web3.js/blob/v4.4.0/packages/web3-validator/src/validation/address.ts#L50
    /// </summary>
    public const string RegexEvmAddress = "^(0x|0X)[0-9a-fA-F]{40}$";
    /// <summary>
    /// https://github.com/near/near-sdk-js/blob/v1.0.0/packages/near-sdk-js/lib/utils.js#L14
    /// </summary>
    public const string RegexNearAddress = @"^(([a-z\d]+[-_])*[a-z\d]+\.)*([a-z\d]+[-_])*[a-z\d]+$";
    /// <summary>
    /// https://docs.solana.com/integrations/exchange#basic-verfication
    /// </summary>
    public const string RegexSolanaAddress = "[1-9A-HJ-NP-Za-km-z]{32,44}";

    public static string Format(string blockchainAddress, BlockchainNetwork blockchainNetwork)
    {
        return blockchainNetwork switch
        {
            BlockchainNetwork.ArbitrumOne or
            BlockchainNetwork.Aurora or
            BlockchainNetwork.AvalancheCChain or
            BlockchainNetwork.Base or
            BlockchainNetwork.Bsc or
            BlockchainNetwork.Core or
            BlockchainNetwork.Cronos or
            BlockchainNetwork.Ethereum or
            BlockchainNetwork.FantomOpera or
            BlockchainNetwork.Fuse or
            BlockchainNetwork.LightlinkPhoenix or
            BlockchainNetwork.MantaPacific or
            BlockchainNetwork.Mantle or
            BlockchainNetwork.Okt or
            BlockchainNetwork.Optimism or
            BlockchainNetwork.PolygonPos or
            BlockchainNetwork.PolygonZkEvm or
            BlockchainNetwork.ZkSyncEra => Web3.ToChecksumAddress(blockchainAddress.Trim()),
            BlockchainNetwork.Algorand => blockchainAddress.Trim().ToUpperInvariant(),
            BlockchainNetwork.Aptos => blockchainAddress.Trim(),
            BlockchainNetwork.Cardano => blockchainAddress.Trim(),
            BlockchainNetwork.Near => blockchainAddress.Trim().ToLowerInvariant(),
            BlockchainNetwork.Sei => blockchainAddress.Trim(),
            BlockchainNetwork.Solana => blockchainAddress.Trim(),
            BlockchainNetwork.Sui => blockchainAddress.Trim(),
            BlockchainNetwork.Ton => blockchainAddress.Trim(),
            _ => blockchainAddress.Trim(),
        };
    }

    public static bool IsValid(string blockchainAddress, BlockchainNetwork blockchainNetwork)
    {
        return blockchainNetwork switch
        {
            BlockchainNetwork.ArbitrumOne or
            BlockchainNetwork.Aurora or
            BlockchainNetwork.AvalancheCChain or
            BlockchainNetwork.Base or
            BlockchainNetwork.Bsc or
            BlockchainNetwork.Core or
            BlockchainNetwork.Cronos or
            BlockchainNetwork.Ethereum or
            BlockchainNetwork.FantomOpera or
            BlockchainNetwork.Fuse or
            BlockchainNetwork.LightlinkPhoenix or
            BlockchainNetwork.MantaPacific or
            BlockchainNetwork.Mantle or
            BlockchainNetwork.Okt or
            BlockchainNetwork.Optimism or
            BlockchainNetwork.PolygonPos or
            BlockchainNetwork.PolygonZkEvm or
            BlockchainNetwork.ZkSyncEra => EvmAddressRegex().Match(blockchainAddress).Success,
            BlockchainNetwork.Algorand => false,
            BlockchainNetwork.Aptos => false,
            BlockchainNetwork.Cardano => false,
            BlockchainNetwork.Near => blockchainAddress.Length >= 2 && blockchainAddress.Length <= 64 && NearAddressRegex().Match(blockchainAddress).Success,
            BlockchainNetwork.Sei => false,
            BlockchainNetwork.Solana => SolanaAddressRegex().Match(blockchainAddress).Success,
            BlockchainNetwork.Sui => false,
            BlockchainNetwork.Ton => false,
            _ => false,
        };
    }

    [GeneratedRegex(RegexEvmAddress)]
    private static partial Regex EvmAddressRegex();
    [GeneratedRegex(RegexNearAddress)]
    private static partial Regex NearAddressRegex();
    [GeneratedRegex(RegexSolanaAddress)]
    private static partial Regex SolanaAddressRegex();
}
