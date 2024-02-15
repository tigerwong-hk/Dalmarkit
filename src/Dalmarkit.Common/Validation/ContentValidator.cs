using Dalmarkit.Common.Errors;
using Microsoft.Extensions.Logging;
using MimeDetective;
using System.Collections.Immutable;

namespace Dalmarkit.Common.Validation;

public static class ContentValidator
{
    public static bool IsValid(ContentInspector contentInspector, Stream contentStream, string contentType, string fileExtension, ILogger logger)
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

        HashSet<string> fileExtensionResults = new(StringComparer.InvariantCultureIgnoreCase);

        long fileExtensionMaxPoints = allResultsByFileExtension[0].Points;
        fileExtensionResults.UnionWith(
            from x in allResultsByFileExtension
            where x.Points == fileExtensionMaxPoints
            select x.Extension
        );

        bool isFileExtensionValid = fileExtensionResults.Contains(fileExtension!.ToLowerInvariant());
        if (!isFileExtensionValid)
        {
            logger.FileExtensionNotFound(string.Join(',', fileExtensionResults), fileExtension);
            return false;
        }

        HashSet<string> mimeTypeResults = new(StringComparer.InvariantCultureIgnoreCase);

        long mimeTypeMaxPoints = allResultsByMimeType[0].Points;
        mimeTypeResults.UnionWith(
            from x in allResultsByMimeType
            where x.Points == mimeTypeMaxPoints
            select x.MimeType
        );

        bool isMimeTypeValid = mimeTypeResults.Contains(contentType!.ToLowerInvariant());
        if (isMimeTypeValid)
        {
            return true;
        }

        logger.ContentTypeNotFound(string.Join(',', mimeTypeResults), contentType);
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
        Message = "File extension not found in [{FileExtensionResults}]: {FileExtension}")]
    public static partial void FileExtensionNotFound(this ILogger logger, string fileExtensionResults, string fileExtension);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Content type not found in [{MimeTypeResults}]: {ContentType} ")]
    public static partial void ContentTypeNotFound(this ILogger logger, string mimeTypeResults, string contentType);
}
