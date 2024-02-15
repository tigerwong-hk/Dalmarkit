using Microsoft.Extensions.Logging;

namespace Dalmarkit.Common.Validation;

public class ImageValidatorService(ILogger<ImageValidatorService> logger) : ContentValidatorService(MimeDetective.Definitions.Default.FileTypes.Images.JPEG().AddRange(MimeDetective.Definitions.Default.FileTypes.Images.PNG()), logger), IImageValidatorService
{
}
