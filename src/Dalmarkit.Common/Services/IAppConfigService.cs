namespace Dalmarkit.Common.Services;

public interface IAppConfigService
{
    Task DeleteParametersAsync(List<string> names);
    Task<IDictionary<string, string>> GetParameterAsync(string name);
    Task<IDictionary<string, string>> GetParametersByPathAsync(string path);
    Task SetSecretParameterAsync(string name, string value);
    Task SetStringParameterAsync(string name, string value);
    Task UpdateSecretParameterAsync(string name, string value);
    Task UpdateStringParameterAsync(string name, string value);
}
