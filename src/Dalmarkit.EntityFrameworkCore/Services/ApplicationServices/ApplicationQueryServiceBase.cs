using AutoMapper;

namespace Dalmarkit.EntityFrameworkCore.Services.ApplicationServices;

public abstract class ApplicationQueryServiceBase(IMapper mapper) : ApplicationServiceBase(mapper)
{
    protected virtual async Task<string[]> GetEnumNamesAsync(Type enumType)
    {
        return await Task.FromResult(Enum.GetNames(enumType));
    }
}
