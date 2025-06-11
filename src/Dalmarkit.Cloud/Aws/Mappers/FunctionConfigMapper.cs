using System.Globalization;
using Amazon.Lambda.Model;
using AutoMapper;
using Dalmarkit.Cloud.Aws.Dtos.Outputs;
using Dalmarkit.Common.Services;

namespace Dalmarkit.Cloud.Aws.Mappers;

public static class FunctionConfigMapper
{
    /// <summary>
    /// Maps a function configuration to a function config model.
    /// </summary>
    /// <param name="config">Function configuration.</param>
    /// <returns>Function config model.</returns>
    public static void CreateMap(IMapperConfigurationExpression config)
    {
        _ = config.CreateMap<FunctionConfiguration, FunctionConfig>()
            .ForMember(d => d.EnvironmentVariables, opt => opt.MapFrom(src => src.Environment.Variables))
            .ForMember(d => d.MemoryMbSize, opt => opt.MapFrom(src => src.MemorySize))
            .ForMember(d => d.Runtime, opt => opt.MapFrom(src => src.Runtime.ToString(CultureInfo.InvariantCulture)))
            .ForMember(d => d.TimeoutSeconds, opt => opt.MapFrom(src => src.Timeout));

        _ = config.CreateMap<FunctionConfig, FunctionConfigOutputDto>();
    }
}
