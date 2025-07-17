using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Dalmarkit.Common.Validation;
using System.Net;
using System.Text.RegularExpressions;

namespace Dalmarkit.Cloud.Aws.Services;

public class AwsSystemsManagerService(AmazonSimpleSystemsManagementClient systemsManagerClient) : IAwsSystemsManagerService
{
    public const int ParameterNameMaxLength = 1011;
    public const string ParameterNameRegexPattern = "^(/?[a-zA-Z0-9_.-]+)(/[a-zA-Z0-9_.-]+){0,14}$";
    public static readonly string[] ParameterNameReservedPrefixes = ["aws", "ssm", "/aws", "/ssm"];
    public const int ParameterValueMaxLength = 4096;
    public const double RegexTimeoutIntervalMsec = 10000;

    private readonly AmazonSimpleSystemsManagementClient _systemsManagerClient =
        Guard.NotNull(systemsManagerClient, nameof(systemsManagerClient));

    public async Task DeleteParametersAsync(List<string> names)
    {
        if (names.Count < 1)
        {
            throw new ArgumentException("No names", nameof(names));
        }

        if (names.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentNullException(nameof(names));
        }

        if (names.Any(name => name.Length > ParameterNameMaxLength))
        {
            throw new ArgumentException($"Length of name exceed {ParameterNameMaxLength}", nameof(names));
        }

        if (names.Any(name => ParameterNameReservedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
        {
            throw new ArgumentException("Reserved name", nameof(names));
        }

        if (names.Any(name => !Regex.IsMatch(name, ParameterNameRegexPattern, RegexOptions.None, TimeSpan.FromMilliseconds(RegexTimeoutIntervalMsec))))
        {
            throw new ArgumentException("Invalid name", nameof(names));
        }

        DeleteParametersResponse response = await _systemsManagerClient.DeleteParametersAsync(
            new DeleteParametersRequest { Names = names }
        );
        if (response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException($"Error status for DeleteParameters request: {response.HttpStatusCode}", null, response.HttpStatusCode);
        }
    }

    public async Task<IDictionary<string, string>> GetParameterAsync(string name)
    {
        _ = Guard.NotNullOrWhiteSpace(name, nameof(name));

        if (name.Length > ParameterNameMaxLength)
        {
            throw new ArgumentException($"Length of name {name.Length} exceed {ParameterNameMaxLength}", nameof(name));
        }

        if (ParameterNameReservedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Reserved", nameof(name));
        }

        if (!Regex.IsMatch(name, ParameterNameRegexPattern, RegexOptions.None, TimeSpan.FromMilliseconds(RegexTimeoutIntervalMsec)))
        {
            throw new ArgumentException("Invalid", nameof(name));
        }

        GetParameterResponse response = await _systemsManagerClient.GetParameterAsync(
            new GetParameterRequest { Name = name, WithDecryption = true }
        );
        return response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous
            ? throw new HttpRequestException($"Error status for GetParameter({name}) request: {response.HttpStatusCode}", null, response.HttpStatusCode)
            : ProcessParameters([response.Parameter], name);
    }

    public async Task<IDictionary<string, string>> GetParametersByPathAsync(string path)
    {
        _ = Guard.NotNullOrWhiteSpace(path, nameof(path));

        if (path.Length > ParameterNameMaxLength)
        {
            throw new ArgumentException($"Length of path {path.Length} exceed {ParameterNameMaxLength}", nameof(path));
        }

        if (ParameterNameReservedPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Reserved", nameof(path));
        }

        if (!Regex.IsMatch(path, ParameterNameRegexPattern, RegexOptions.None, TimeSpan.FromMilliseconds(RegexTimeoutIntervalMsec)))
        {
            throw new ArgumentException("Invalid", nameof(path));
        }

        List<Parameter> parameters = [];
        string? nextToken = null;

        do
        {
            GetParametersByPathResponse response = await _systemsManagerClient.GetParametersByPathAsync(
                new GetParametersByPathRequest
                {
                    Path = path,
                    Recursive = true,
                    WithDecryption = true,
                    NextToken = nextToken
                });
            if (response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
            {
                throw new HttpRequestException($"Error status for GetParametersByPath({path}) request: {response.HttpStatusCode}", null, response.HttpStatusCode);
            }

            nextToken = response.NextToken;
            parameters.AddRange(response.Parameters);
        } while (!string.IsNullOrWhiteSpace(nextToken));

        return ProcessParameters(parameters, path);
    }

    public async Task SetSecretParameterAsync(string name, string value)
    {
        _ = Guard.NotNullOrWhiteSpace(name, nameof(name));
        _ = Guard.NotNullOrWhiteSpace(value, nameof(value));

        if (name.Length > ParameterNameMaxLength)
        {
            throw new ArgumentException($"Length of name {name.Length} exceed {ParameterNameMaxLength}", nameof(name));
        }

        if (value.Length > ParameterValueMaxLength)
        {
            throw new ArgumentException($"Length of value {value.Length} exceed {ParameterValueMaxLength}", nameof(value));
        }

        if (ParameterNameReservedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Reserved", nameof(name));
        }

        if (!Regex.IsMatch(name, ParameterNameRegexPattern, RegexOptions.None, TimeSpan.FromMilliseconds(RegexTimeoutIntervalMsec)))
        {
            throw new ArgumentException("Invalid", nameof(name));
        }

        PutParameterResponse response = await _systemsManagerClient.PutParameterAsync(
            new PutParameterRequest { Name = name, DataType = "text", Type = ParameterType.SecureString, Value = value }
        );
        if (response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException($"Error status for SetSecureParameter({name}, {value}) request: {response.HttpStatusCode}", null, response.HttpStatusCode);
        }
    }

    public async Task SetStringParameterAsync(string name, string value)
    {
        _ = Guard.NotNullOrWhiteSpace(name, nameof(name));
        _ = Guard.NotNullOrWhiteSpace(value, nameof(value));

        if (name.Length > ParameterNameMaxLength)
        {
            throw new ArgumentException($"Length of name {name.Length} exceed {ParameterNameMaxLength}", nameof(name));
        }

        if (value.Length > ParameterValueMaxLength)
        {
            throw new ArgumentException($"Length of value {value.Length} exceed {ParameterValueMaxLength}", nameof(value));
        }

        if (ParameterNameReservedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Reserved", nameof(name));
        }

        if (!Regex.IsMatch(name, ParameterNameRegexPattern, RegexOptions.None, TimeSpan.FromMilliseconds(RegexTimeoutIntervalMsec)))
        {
            throw new ArgumentException("Invalid", nameof(name));
        }

        PutParameterResponse response = await _systemsManagerClient.PutParameterAsync(
            new PutParameterRequest { Name = name, DataType = "text", Type = ParameterType.String, Value = value }
        );
        if (response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException($"Error status for SetSecureParameter({name}, {value}) request: {response.HttpStatusCode}", null, response.HttpStatusCode);
        }
    }

    public async Task UpdateSecretParameterAsync(string name, string value)
    {
        _ = Guard.NotNullOrWhiteSpace(name, nameof(name));
        _ = Guard.NotNullOrWhiteSpace(value, nameof(value));

        if (name.Length > ParameterNameMaxLength)
        {
            throw new ArgumentException($"Length of Name {name.Length} exceed {ParameterNameMaxLength}", nameof(name));
        }

        if (value.Length > ParameterValueMaxLength)
        {
            throw new ArgumentException($"Length of value {value.Length} exceed {ParameterValueMaxLength}", nameof(value));
        }

        if (ParameterNameReservedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Reserved", nameof(name));
        }

        if (!Regex.IsMatch(name, ParameterNameRegexPattern, RegexOptions.None, TimeSpan.FromMilliseconds(RegexTimeoutIntervalMsec)))
        {
            throw new ArgumentException("Invalid", nameof(name));
        }

        PutParameterResponse response = await _systemsManagerClient.PutParameterAsync(
            new PutParameterRequest { Name = name, DataType = "text", Overwrite = true, Type = ParameterType.SecureString, Value = value }
        );
        if (response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException($"Error status for UpdateSecureParameter({name}, {value}) request: {response.HttpStatusCode}", null, response.HttpStatusCode);
        }
    }

    public async Task UpdateStringParameterAsync(string name, string value)
    {
        _ = Guard.NotNullOrWhiteSpace(name, nameof(name));
        _ = Guard.NotNullOrWhiteSpace(value, nameof(value));

        if (name.Length > ParameterNameMaxLength)
        {
            throw new ArgumentException($"Length of name {name.Length} exceed {ParameterNameMaxLength}", nameof(name));
        }

        if (value.Length > ParameterValueMaxLength)
        {
            throw new ArgumentException($"Length of value {value.Length} exceed {ParameterValueMaxLength}", nameof(value));
        }

        if (ParameterNameReservedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Reserved", nameof(name));
        }

        if (!Regex.IsMatch(name, ParameterNameRegexPattern, RegexOptions.None, TimeSpan.FromMilliseconds(RegexTimeoutIntervalMsec)))
        {
            throw new ArgumentException("Invalid", nameof(name));
        }

        PutParameterResponse response = await _systemsManagerClient.PutParameterAsync(
            new PutParameterRequest { Name = name, DataType = "text", Overwrite = true, Type = ParameterType.String, Value = value }
        );
        if (response.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException($"Error status for UpdateSecureParameter({name}, {value}) request: {response.HttpStatusCode}", null, response.HttpStatusCode);
        }
    }

    internal virtual string GetKey(Parameter parameter)
    {
        return parameter.Name;
    }

    internal virtual string GetValue(Parameter parameter)
    {
        return parameter.Value;
    }

    internal virtual IDictionary<string, string> ProcessParameters(IEnumerable<Parameter> parameters, string path)
    {
        List<KeyValuePair<string, string>> result = [];

        foreach (Parameter parameter in parameters)
        {
            if (parameter.Type == ParameterType.StringList)
            {
                IEnumerable<KeyValuePair<string, string>> parameterList = ParseStringList(parameter);

                IEnumerable<string> stringListKeys = parameterList.Select(p => p.Key);
                IEnumerable<string> duplicateKeys =
                    result.Where(r => stringListKeys.Contains(r.Key, StringComparer.OrdinalIgnoreCase)).Select(r => r.Key);
                if (duplicateKeys.Any())
                {
                    throw new ArgumentException($"Duplicate parameters '{string.Join(";", duplicateKeys)}' found. Parameter keys are case-insensitive.");
                }

                result.AddRange(parameterList);
            }
            else
            {
                string parameterKey = GetKey(parameter);

                if (result.Any(r => string.Equals(r.Key, parameterKey, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ArgumentException($"Duplicate parameter '{parameterKey}' found. Parameter keys are case-insensitive.");
                }

                result.Add(new KeyValuePair<string, string>(parameterKey, GetValue(parameter)));
            }
        }

        return result.ToDictionary(parameter => parameter.Key, parameter => parameter.Value, StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<KeyValuePair<string, string>> ParseStringList(Parameter parameter)
    {
        // Items in a StringList must be separated by a comma (,).
        // You can't use other punctuation or special characters to escape items in the list.
        // If you have a parameter value that requires a comma, then use the String type.
        // https://docs.aws.amazon.com/systems-manager/latest/userguide/param-create-cli.html#param-create-cli-stringlist
        return parameter.Value.Split(',').Select((value, idx) =>
            new KeyValuePair<string, string>($"{GetKey(parameter)}:{idx}", value));
    }
}
