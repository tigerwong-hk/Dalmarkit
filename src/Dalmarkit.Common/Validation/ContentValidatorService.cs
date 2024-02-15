using Microsoft.Extensions.Logging;
using MimeDetective;
using MimeDetective.Storage;
using System.Collections.Immutable;

namespace Dalmarkit.Common.Validation;

public class ContentValidatorService : IContentValidatorService
{
    private readonly ContentInspector _contentInspector;
    private readonly ILogger _logger;

    public ContentValidatorService(ImmutableArray<Definition> supportedDefinitions, ILogger<ContentValidatorService> logger)
    {
        _ = Guard.NotNull(supportedDefinitions, nameof(supportedDefinitions));
        _logger = Guard.NotNull(logger, nameof(logger));

        if (supportedDefinitions.Length < 1)
        {
            throw new ArgumentException("No supported definitions specified", nameof(supportedDefinitions));
        }

        _contentInspector = new ContentInspectorBuilder()
        {
            Definitions = supportedDefinitions,
            Parallel = false,
        }.Build();
    }

    public bool IsValid(Stream contentStream, string contentType, string fileExtension)
    {
        return ContentValidator.IsValid(_contentInspector, contentStream, contentType, fileExtension, _logger);
    }
}
