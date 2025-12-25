using Microsoft.Extensions.Logging;

namespace Dalmarkit.Common.Validation;

public class DocumentValidatorService(ILogger<DocumentValidatorService> logger) : ContentValidatorService(MimeDetective.Definitions.DefaultDefinitions.FileTypes.Documents.PDF(), logger), IDocumentValidatorService
{
}
