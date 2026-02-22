using Dalmarkit.Common.Errors;
using Microsoft.Extensions.Logging;
using MimeDetective;
using System.Collections.Immutable;

namespace Dalmarkit.Common.Validation;

public static class ContentValidator
{
    public static bool IsValid(IContentInspector contentInspector, Stream contentStream, string contentType, string fileExtension, ILogger logger)
    {
        _ = Guard.NotNull(contentInspector, nameof(contentInspector));
        _ = Guard.NotNull(contentStream, nameof(contentStream));
        _ = Guard.NotNullOrWhiteSpace(contentType, nameof(contentType));
        _ = Guard.NotNullOrWhiteSpace(fileExtension, nameof(fileExtension));
        _ = Guard.NotNull(logger, nameof(logger));

        if (contentStream == Stream.Null)
        {
            throw new ArgumentException(ErrorMessages.StreamNull, nameof(contentStream));
        }

        ImmutableArray<MimeDetective.Engine.DefinitionMatch> allResults = contentInspector.Inspect(contentStream);
        if (allResults.Length < 1)
        {
            logger.NoResultsFound();
            return false;
        }

        ImmutableArray<MimeDetective.Engine.FileExtensionMatch> allResultsByFileExtension = allResults.ByFileExtension();
        if (allResultsByFileExtension.Length < 1)
        {
            logger.NoResultsByFileExtensionFound();
            return false;
        }

        ImmutableArray<MimeDetective.Engine.MimeTypeMatch> allResultsByMimeType = allResults.ByMimeType();
        if (allResultsByMimeType.Length < 1)
        {
            logger.NoResultsByMimeTypeFound();
            return false;
        }

#pragma warning disable IDE0028 // Simplify collection initialization
        HashSet<string> fileExtensionResults = new(StringComparer.InvariantCultureIgnoreCase);
#pragma warning restore IDE0028 // Simplify collection initialization

        long fileExtensionMaxPoints = allResultsByFileExtension[0].Points;
        fileExtensionResults.UnionWith(
            from x in allResultsByFileExtension
            where x.Points == fileExtensionMaxPoints
            select x.Extension
        );

        bool isFileExtensionValid = fileExtensionResults.Contains(fileExtension.ToLowerInvariant());
        if (!isFileExtensionValid)
        {
            logger.FileExtensionNotFound(fileExtension);
            return false;
        }

#pragma warning disable IDE0028 // Simplify collection initialization
        HashSet<string> mimeTypeResults = new(StringComparer.InvariantCultureIgnoreCase);
#pragma warning restore IDE0028 // Simplify collection initialization

        long mimeTypeMaxPoints = allResultsByMimeType[0].Points;
        mimeTypeResults.UnionWith(
            from x in allResultsByMimeType
            where x.Points == mimeTypeMaxPoints
            select x.MimeType
        );

        bool isMimeTypeValid = mimeTypeResults.Contains(contentType.ToLowerInvariant());
        if (isMimeTypeValid)
        {
            return true;
        }

        logger.ContentTypeNotFound(contentType);
        return false;
    }
}

public static partial class ContentInspectorHelperLogs
{
    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Information,
        Message = "No results found")]
    public static partial void NoResultsFound(this ILogger logger);

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "No results by file extension found")]
    public static partial void NoResultsByFileExtensionFound(this ILogger logger);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "No results by mime type found")]
    public static partial void NoResultsByMimeTypeFound(this ILogger logger);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "File extension not found: {FileExtension}")]
    public static partial void FileExtensionNotFound(this ILogger logger, string fileExtension);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Content type not found: {ContentType} ")]
    public static partial void ContentTypeNotFound(this ILogger logger, string contentType);
}
