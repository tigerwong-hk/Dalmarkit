using Microsoft.AspNetCore.Builder;

namespace Dalmarkit.AspNetCore.Exceptions;

public static class ExceptionsApplicationBuilderExtensions
{
    public static IApplicationBuilder UseExceptionsMiddleware(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ExceptionsMiddleware>();
    }
}
