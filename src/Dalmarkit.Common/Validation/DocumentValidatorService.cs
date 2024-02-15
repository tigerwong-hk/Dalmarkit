using Microsoft.Extensions.Logging;

namespace Dalmarkit.Common.Validation;

public class DocumentValidatorService(ILogger<DocumentValidatorService> logger) : ContentValidatorService(MimeDetective.Definitions.Default.FileTypes.Documents.PDF(), logger), IDocumentValidatorService
{
}
