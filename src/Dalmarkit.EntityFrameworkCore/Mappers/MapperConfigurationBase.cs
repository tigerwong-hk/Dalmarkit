using AutoMapper;

namespace Dalmarkit.EntityFrameworkCore.Mappers;

public abstract class MapperConfigurationBase
{
    public IMapper CreateMapper()
    {
        MapperConfiguration mapperConfiguration = new(Configure);
        return mapperConfiguration.CreateMapper();
    }

    protected virtual void DtoToDtoMappingConfigure(IMapperConfigurationExpression config)
    {
    }

    protected virtual void DtoToEntityMappingConfigure(IMapperConfigurationExpression config)
    {
    }

    protected virtual void EntityToDtoMappingConfigure(IMapperConfigurationExpression config)
    {
    }

    protected virtual void EnumToDtoMappingConfigure(IMapperConfigurationExpression config)
    {
    }

    private void Configure(IMapperConfigurationExpression config)
    {
        DtoToDtoMappingConfigure(config);
        DtoToEntityMappingConfigure(config);
        EnumToDtoMappingConfigure(config);
        EntityToDtoMappingConfigure(config);
    }
}
