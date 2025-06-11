using Amazon.Lambda;
using Amazon.Lambda.Model;
using AutoMapper;
using Dalmarkit.Common.Services;
using Dalmarkit.Common.Validation;
using System.Net;

namespace Dalmarkit.Cloud.Aws.Services;

public class AwsLambdaService(AmazonLambdaClient lambdaClient, IMapper mapper) : IAwsLambdaService
{
    private readonly AmazonLambdaClient _lambdaClient = Guard.NotNull(lambdaClient, nameof(lambdaClient));
    private readonly IMapper _mapper = Guard.NotNull(mapper, nameof(mapper));

    public static string GetCronScheduleExpression(string cronExpression)
    {
        return $"cron({cronExpression})";
    }

    public async Task<string> AddPermissionAsync(string functionName, string action, string principal, string source)
    {
        _ = Guard.NotNullOrWhiteSpace(functionName, nameof(functionName));
        _ = Guard.NotNullOrWhiteSpace(action, nameof(action));
        _ = Guard.NotNullOrWhiteSpace(principal, nameof(principal));
        _ = Guard.NotNullOrWhiteSpace(source, nameof(source));

        AddPermissionRequest request = new()
        {
            Action = action,
            FunctionName = functionName,
            Principal = principal,
            SourceArn = source,
            StatementId = Guid.NewGuid().ToString()
        };

        AddPermissionResponse response = await _lambdaClient.AddPermissionAsync(request);
        return response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous
            ? throw new HttpRequestException(
                $"Error status for AddPermission({functionName}, {action}, {principal}) request: {response.HttpStatusCode}",
                null,
                response.HttpStatusCode)
            : response.Statement;
    }

    public async Task<string> CreateFunctionAsync(
        string functionName,
        string runtimeValue,
        string bucketName,
        string objectName,
        string role,
        string handler,
        string description,
        int timeoutSeconds,
        List<string> architectures,
        Dictionary<string, string> environmentVariables)
    {
        _ = Guard.NotNullOrWhiteSpace(functionName, nameof(functionName));
        _ = Guard.NotNullOrWhiteSpace(runtimeValue, nameof(runtimeValue));
        _ = Guard.NotNullOrWhiteSpace(bucketName, nameof(bucketName));
        _ = Guard.NotNullOrWhiteSpace(objectName, nameof(objectName));
        _ = Guard.NotNullOrWhiteSpace(role, nameof(role));
        _ = Guard.NotNullOrWhiteSpace(handler, nameof(handler));
        _ = Guard.NotNullOrWhiteSpace(description, nameof(description));
        _ = Guard.NotNull(architectures, nameof(architectures));
        _ = Guard.NotNull(environmentVariables, nameof(environmentVariables));

        Runtime runtime = Runtime.FindValue(runtimeValue);

        FunctionCode functionCode = new()
        {
            S3Bucket = bucketName,
            S3Key = objectName,
        };

        CreateFunctionRequest request = new()
        {
            Architectures = architectures,
            Code = functionCode,
            Description = description,
            Environment = new Amazon.Lambda.Model.Environment { Variables = environmentVariables },
            FunctionName = functionName,
            Handler = handler,
            PackageType = PackageType.Zip,
            Publish = true,
            Role = role,
            Runtime = runtime,
            Timeout = timeoutSeconds,
        };

        CreateFunctionResponse response = await _lambdaClient.CreateFunctionAsync(request);
        return response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous
            ? throw new HttpRequestException(
                $"Error status for CreateFunction({functionName}, {runtimeValue}, {bucketName}, {objectName}) request: {response.HttpStatusCode}",
                null,
                response.HttpStatusCode)
            : response.FunctionArn;
    }

    public async Task DeleteFunctionAsync(string functionName)
    {
        _ = Guard.NotNullOrWhiteSpace(functionName, nameof(functionName));

        DeleteFunctionRequest request = new()
        {
            FunctionName = functionName,
        };

        DeleteFunctionResponse response = await _lambdaClient.DeleteFunctionAsync(request);
        if (response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException(
                $"Error status for DeleteFunction({functionName}) request: {response.HttpStatusCode}",
                null,
                response.HttpStatusCode);
        }
    }

    public async Task<FunctionConfig> GetFunctionAsync(string functionName)
    {
        _ = Guard.NotNullOrWhiteSpace(functionName, nameof(functionName));

        GetFunctionRequest request = new()
        {
            FunctionName = functionName,
        };

        GetFunctionResponse response = await _lambdaClient.GetFunctionAsync(request);
        return response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous
            ? throw new HttpRequestException(
                $"Error status for GetFunction({functionName}) request: {response.HttpStatusCode}",
                null,
                response.HttpStatusCode)
            : _mapper.Map<FunctionConfig>(response.Configuration);
    }

    public async Task<List<FunctionConfig>> ListFunctionsAsync()
    {
        List<FunctionConfig> functionList = [];

        IListFunctionsPaginator functionsPaginator =
            _lambdaClient.Paginators.ListFunctions(new ListFunctionsRequest());
        await foreach (FunctionConfiguration function in functionsPaginator.Functions)
        {
            FunctionConfig functionConfig = _mapper.Map<FunctionConfig>(function);
            functionList.Add(functionConfig);
        }

        return functionList;
    }

    public async Task<string> UpdateFunctionCodeAsync(string functionName, string bucketName, string objectName)
    {
        _ = Guard.NotNullOrWhiteSpace(functionName, nameof(functionName));
        _ = Guard.NotNullOrWhiteSpace(bucketName, nameof(bucketName));
        _ = Guard.NotNullOrWhiteSpace(objectName, nameof(objectName));

        UpdateFunctionCodeRequest request = new()
        {
            FunctionName = functionName,
            Publish = true,
            S3Bucket = bucketName,
            S3Key = objectName,
        };

        UpdateFunctionCodeResponse response = await _lambdaClient.UpdateFunctionCodeAsync(request);
        return response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous
            ? throw new HttpRequestException(
                $"Error status for UpdateFunctionCode({functionName}, {bucketName}, {objectName}) request: {response.HttpStatusCode}",
                null,
                response.HttpStatusCode)
            : response.LastModified;
    }

    public async Task<string> UpdateFunctionConfigAsync(
        string functionName,
        string handler,
        Dictionary<string, string> environmentVariables)
    {
        _ = Guard.NotNullOrWhiteSpace(functionName, nameof(functionName));
        _ = Guard.NotNullOrWhiteSpace(handler, nameof(handler));

        UpdateFunctionConfigurationRequest request = new()
        {
            Environment = new Amazon.Lambda.Model.Environment { Variables = environmentVariables },
            FunctionName = functionName,
            Handler = handler,
        };

        UpdateFunctionConfigurationResponse response = await _lambdaClient.UpdateFunctionConfigurationAsync(request);
        return response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous
            ? throw new HttpRequestException(
                $"Error status for UpdateFunctionConfig({functionName}, {handler}) request: {response.HttpStatusCode}",
                null,
                response.HttpStatusCode)
            : response.LastModified;
    }
}
