using Microsoft.Extensions.Logging;

namespace Dalmarkit.Common.Validation;

public class ImageValidatorService(ILogger<ImageValidatorService> logger) : ContentValidatorService(MimeDetective.Definitions.DefaultDefinitions.FileTypes.Images.JPEG().AddRange(MimeDetective.Definitions.DefaultDefinitions.FileTypes.Images.PNG()), logger), IImageValidatorService
{
}
