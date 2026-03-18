using Amazon.Lambda.Model;
using Dalmarkit.Cloud.Aws.Dtos.Outputs;
using Dalmarkit.Common.Services;
using Riok.Mapperly.Abstractions;

namespace Dalmarkit.Cloud.Aws.Mappers;

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class FunctionConfigMapper
{
    [MapProperty(nameof(FunctionConfiguration.Environment.Variables), nameof(FunctionConfig.EnvironmentVariables))]
    [MapProperty(nameof(FunctionConfiguration.MemorySize), nameof(FunctionConfig.MemoryMbSize))]
    [MapProperty(nameof(FunctionConfiguration.Runtime), nameof(FunctionConfig.Runtime))]
    [MapProperty(nameof(FunctionConfiguration.Timeout), nameof(FunctionConfig.TimeoutSeconds))]
    public static partial FunctionConfig ToTarget(FunctionConfiguration source);

    public static partial FunctionConfigOutputDto ToTarget(FunctionConfig source);
}
