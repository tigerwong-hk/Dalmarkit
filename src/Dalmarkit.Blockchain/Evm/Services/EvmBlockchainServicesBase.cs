using Dalmarkit.Blockchain.Constants;
using Dalmarkit.Common.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Contracts.ContractHandlers;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Dalmarkit.Blockchain.Evm.Services;

public abstract partial class EvmBlockchainServiceBase
{
    public const string RegexTransactionHash = "^0x[0-9A-Fa-f]{64}$";
    private readonly EvmBlockchainOptions _blockchainOptions;
    private readonly ILogger _logger;

    protected EvmBlockchainServiceBase(IOptions<EvmBlockchainOptions> blockchainOptions,
        ILogger<EvmBlockchainServiceBase> logger)
    {
        _blockchainOptions = Guard.NotNull(blockchainOptions, nameof(blockchainOptions)).Value;
        _ = Guard.NotNull(_blockchainOptions.RpcUrls, nameof(_blockchainOptions.RpcUrls));
        _logger = Guard.NotNull(logger, nameof(logger));
    }

    protected async Task<TFunctionOutputDto?> CallContractFunctionAsync<TFunctionHandler, TFunctionOutputDto>(
        TFunctionHandler contractFunction,
        string contractAddress,
        BlockchainNetwork blockchainNetwork)
        where TFunctionHandler : FunctionMessage, new()
        where TFunctionOutputDto : IFunctionOutputDTO, new()
    {
        _ = Guard.NotNullOrWhiteSpace(contractAddress, nameof(contractAddress));

        if (!_blockchainOptions.RpcUrls!.TryGetValue(blockchainNetwork.ToString(), out string? rpcUrl))
        {
            _logger.RpcUrlNotFoundForError(blockchainNetwork);
            return default;
        }

        if (string.IsNullOrWhiteSpace(rpcUrl))
        {
            _logger.RpcUrlNullOrWhitespaceForError(blockchainNetwork);
            return default;
        }

        Web3 web3Client = new(rpcUrl);

        IContractQueryHandler<TFunctionHandler> functionHandler = web3Client.Eth.GetContractQueryHandler<TFunctionHandler>();
        return await functionHandler.QueryAsync<TFunctionOutputDto>(contractAddress, contractFunction);
    }

    protected async Task<T?> GetEventAsync<T>(string contractAddress, string transactionHash, BlockchainNetwork blockchainNetwork, bool disableExactEventLogCountCheck = false, int expectedEventLogsCount = 1) where T : new()
    {
        _ = Guard.NotNullOrWhiteSpace(contractAddress, nameof(contractAddress));
        _ = Guard.NotNullOrWhiteSpace(transactionHash, nameof(transactionHash));

        if (!_blockchainOptions.RpcUrls!.TryGetValue(blockchainNetwork.ToString(), out string? rpcUrl))
        {
            _logger.RpcUrlNotFoundForError(blockchainNetwork);
            return default;
        }

        if (string.IsNullOrWhiteSpace(rpcUrl))
        {
            _logger.RpcUrlNullOrWhitespaceForError(blockchainNetwork);
            return default;
        }

        if (!TransactionHashRegex().IsMatch(transactionHash))
        {
            _logger.InvalidTransactionHashForError(blockchainNetwork, transactionHash);
            return default;
        }

        Web3 web3Client = new(rpcUrl);

        TransactionReceipt transactionReceipt = await web3Client.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(transactionHash);
        if (transactionReceipt == null)
        {
            _logger.NullTransactionReceiptForError(blockchainNetwork, transactionHash);
            return default;
        }

        if (!transactionReceipt.Succeeded(true))
        {
            _logger.TransactionReceiptIndicatesFailedForError(blockchainNetwork, transactionHash);
            return default;
        }

        if (transactionReceipt.BlockNumber?.HexValue == null)
        {
            _logger.NullBlockNumberForError(blockchainNetwork, transactionHash);
            return default;
        }

        if (transactionReceipt.BlockNumber.Value < 1)
        {
            _logger.BlockNumberLessThanOneForError(blockchainNetwork, transactionHash, transactionReceipt.BlockNumber.Value);
            return default;
        }

        if (!string.Equals(transactionReceipt.To, contractAddress, StringComparison.OrdinalIgnoreCase))
        {
            _logger.UnexpectedContractAddressForError(blockchainNetwork, transactionHash, transactionReceipt.To, contractAddress);
            return default;
        }

        if (!transactionReceipt.HasLogs())
        {
            _logger.NoEventLogsForError(blockchainNetwork, transactionHash);
            return default;
        }

        List<EventLog<T>> eventLogs = transactionReceipt.DecodeAllEvents<T>();
        if (disableExactEventLogCountCheck)
        {
            if (eventLogs.Count < 1)
            {
                _logger.NoEventLogsFoundForError(blockchainNetwork, transactionHash, eventLogs.Count);
                return default;
            }
        }
        else if (eventLogs.Count != expectedEventLogsCount)
        {
            _logger.UnexpectedEventLogsCountForError(blockchainNetwork, transactionHash, eventLogs.Count, expectedEventLogsCount);
            return default;
        }

        return eventLogs[0].Event;
    }

    [GeneratedRegex(RegexTransactionHash)]
    private static partial Regex TransactionHashRegex();
}

public static partial class EvmBlockchainServiceBaseLogs
{
    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Information,
        Message = "Block number less than one on blockchain network `{BlockchainNetwork}` for transaction hash `{TransactionHash}`: {BlockNumber}")]
    public static partial void BlockNumberLessThanOneForError(
        this ILogger logger, BlockchainNetwork blockchainNetwork, string transactionHash, BigInteger blockNumber);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Invalid transaction hash for blockchain network `{BlockchainNetwork}`: {TransactionHash}")]
    public static partial void InvalidTransactionHashForError(
        this ILogger logger, BlockchainNetwork blockchainNetwork, string transactionHash);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Information,
        Message = "No event logs on blockchain network `{BlockchainNetwork}` for transaction hash: {TransactionHash}")]
    public static partial void NoEventLogsForError(
        this ILogger logger, BlockchainNetwork blockchainNetwork, string transactionHash);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Information,
        Message = "No event logs found on blockchain network `{BlockchainNetwork}` for transaction hash `{TransactionHash}`: {EventLogsCount}")]
    public static partial void NoEventLogsFoundForError(
        this ILogger logger, BlockchainNetwork blockchainNetwork, string transactionHash, int eventLogsCount);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "Null block number on blockchain network `{BlockchainNetwork}` for transaction hash: {TransactionHash}")]
    public static partial void NullBlockNumberForError(
        this ILogger logger, BlockchainNetwork blockchainNetwork, string transactionHash);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Null transaction receipt on blockchain network `{BlockchainNetwork}` for transaction hash: {TransactionHash}")]
    public static partial void NullTransactionReceiptForError(
        this ILogger logger, BlockchainNetwork blockchainNetwork, string transactionHash);

    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Error,
        Message = "RPC URL not found for blockchain network `{BlockchainNetwork}`")]
    public static partial void RpcUrlNotFoundForError(
        this ILogger logger, BlockchainNetwork blockchainNetwork);

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "RPC URL null or whitespace for blockchain network `{BlockchainNetwork}`")]
    public static partial void RpcUrlNullOrWhitespaceForError(
        this ILogger logger, BlockchainNetwork blockchainNetwork);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Transaction receipt on blockchain network `{BlockchainNetwork}` indicates failed for transaction hash: {TransactionHash}")]
    public static partial void TransactionReceiptIndicatesFailedForError(
        this ILogger logger, BlockchainNetwork blockchainNetwork, string transactionHash);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Information,
        Message = "Unexpected contract address on blockchain network `{BlockchainNetwork}` for transaction hash `{TransactionHash}`: `{OnchainContractAddress}` instead of `{ExpectedContractAddress}`")]
    public static partial void UnexpectedContractAddressForError(
        this ILogger logger, BlockchainNetwork blockchainNetwork, string transactionHash, string onchainContractAddress, string expectedContractAddress);

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Information,
        Message = "Unexpected event logs count on blockchain network `{BlockchainNetwork}` for transaction hash `{TransactionHash}`: `{OnchainEventLogsCount}` instead of `{ExpectedEventLogsCount}`")]
    public static partial void UnexpectedEventLogsCountForError(
        this ILogger logger, BlockchainNetwork blockchainNetwork, string transactionHash, int onchainEventLogsCount, int expectedEventLogsCount);
}
