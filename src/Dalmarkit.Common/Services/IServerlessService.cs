namespace Dalmarkit.Common.Services;

public interface IServerlessService
{
    Task<string> AddPermissionAsync(string functionName, string action, string principal, string source);

    Task<string> CreateFunctionAsync(
        string functionName,
        string runtimeValue,
        string bucketName,
        string objectName,
        string role,
        string handler,
        string description,
        int timeoutSeconds,
        List<string> architectures,
        Dictionary<string, string> environmentVariables);

    Task DeleteFunctionAsync(string functionName);

    Task<FunctionConfig> GetFunctionAsync(string functionName);

    Task<List<FunctionConfig>> ListFunctionsAsync();

    Task<string> UpdateFunctionCodeAsync(string functionName, string bucketName, string objectName);

    Task<string> UpdateFunctionConfigAsync(
        string functionName,
        string handler,
        Dictionary<string, string> environmentVariables);
}
