namespace Dalmarkit.Cloud.Aws.Dtos.Outputs;

public class FunctionConfigOutputDto
{
    public List<string> Architectures { get; set; } = null!;
    public Dictionary<string, string> EnvironmentVariables { get; set; } = null!;
    public string FunctionName { get; set; } = null!;
    public string Handler { get; set; } = null!;
    public int MemoryMbSize { get; set; }
    public string Role { get; set; } = null!;
    public string Runtime { get; set; } = null!;
    public int TimeoutSeconds { get; set; }
}
