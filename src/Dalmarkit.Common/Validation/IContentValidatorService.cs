namespace Dalmarkit.Common.Validation;

public interface IContentValidatorService
{
    bool IsValid(Stream contentStream, string contentType, string fileExtension);
}
