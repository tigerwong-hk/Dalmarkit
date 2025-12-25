using Nethereum.ABI.Model;
using Nethereum.Contracts;
using Nethereum.RPC.Eth.DTOs;
using Newtonsoft.Json.Linq;
using System.Globalization;

namespace Dalmarkit.Blockchain.Evm.Services;

public static class EvmBlockchainEventExtensions
{
    public static List<EventABI> ExtractEventABIWithName(this Contract contract, string eventName)
    {
        return [.. contract.ContractBuilder.ContractABI.Events.Where(e => e.Name == eventName)];
    }

    public static EventABI? ExtractEventABIWithSignature(this Contract contract, string eventSha3Signature)
    {
        string sha3Signature = eventSha3Signature.StartsWith("0x", true, CultureInfo.InvariantCulture) ? eventSha3Signature[2..] : eventSha3Signature;
        IEnumerable<EventABI> eventABIs = contract.ContractBuilder.ContractABI.Events.Where(e => e.Sha3Signature == sha3Signature);
        return eventABIs.SingleOrDefault();
    }

    public static IEnumerable<string> ExtractSignaturesWithName(this Contract contract, string eventName)
    {
        return contract.ContractBuilder.ContractABI.Events.Where(e => e.Name == eventName).Select(e => e.Sha3Signature);
    }

    public static List<JObject>? DecodeAllEventsToJObjectsWithName(this FilterLog[] logs, string eventName, Contract contract)
    {
        List<EventABI> eventAbis = contract.ExtractEventABIWithName(eventName);

        List<JObject> result = [];
        foreach (EventABI eventAbi in eventAbis)
        {
            List<JObject> eventObjects = eventAbi.DecodeAllEventsToJObjects(logs);
            result.AddRange(eventObjects);
        }

        return result;
    }

    public static List<JObject>? DecodeAllEventsToJObjectsWithSignature(this FilterLog[] logs, string eventSha3Signature, Contract contract)
    {
        EventABI? eventABI = contract.ExtractEventABIWithSignature(eventSha3Signature);
        return eventABI?.DecodeAllEventsToJObjects(logs);
    }

    public static List<JObject>? DecodeAllEventsToJObjectsWithName(
        this TransactionReceipt transactionReceipt,
        string eventName,
        Contract contract
    )
    {
        return transactionReceipt.Logs.DecodeAllEventsToJObjectsWithName(eventName, contract);
    }

    public static List<JObject>? DecodeAllEventsToJObjectsWithSha3Signature(
        this TransactionReceipt transactionReceipt,
        string eventSha3Signature,
        Contract contract
    )
    {
        return transactionReceipt.Logs.DecodeAllEventsToJObjectsWithSignature(eventSha3Signature, contract);
    }

    public static List<JObject> DecodeAllEventsToJObjects(this EventABI eventABI, JArray logs)
    {
        return DecodeAllEventsToJObjects(eventABI, eventABI.GetLogsForEvent(logs));
    }

    public static List<JObject> DecodeAllEventsToJObjects(this EventABI eventABI, FilterLog[] logs)
    {
        List<JObject> result = [];

        if (logs == null)
        {
            return result;
        }

        foreach (FilterLog log in logs)
        {
            JObject eventDecoded = eventABI.DecodeEventToJObject(log);
            if (eventDecoded != null)
            {
                result.Add(eventDecoded);
            }
        }
        return result;
    }
}
