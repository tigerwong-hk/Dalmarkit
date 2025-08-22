namespace Dalmarkit.Common.Errors;

public static class ErrorMessages
{
    public const string BadRequest = "Bad request";
    public const string BadRequestDetails = "Bad request: {0}";
    public const string ContentTypeUnsupported = "Unsupported content type: {0}";
    public const string DomainInvalid = "Invalid domain: {0}";
    public const string FileExtensionUnsupported = "Unsupported file extension: {0}";
    public const string FileSizeExceeded = "Exceeded maximum file size of {0} bytes: {1} bytes";
    public const string ObjectExists = "Object exists";
    public const string ObjectInvalid = "Invalid object";
    public const string ParameterNameMissing = "Missing parameter name";
    public const string ResourceConflict = "Resource conflict";
    public const string ResourceNotFound = "Resource not found";
    public const string ResourceNotFoundFor = "Resource not found for {0}: {1}";
    public const string ResourceNotFoundInParent = "Resource not found for {0} in parent {1}: {2}";
    public const string ServerError = "Server error";
    public const string ServiceUnavailable = "Service unavailable";
    public const string StreamNull = "Null stream";
    public const string TooManyRequests = "Too many requests";
    public const string TransactionHashInvalid = "Invalid transaction hash: {0}";
    public const string ValueIsEmpty = "Value is empty";
    public const string ValueIsNullOrWhiteSpace = "Value is null or whitespace";

    public static class ModelStateErrors
    {
        public const string ElementsTooFew = "{0} has too few elements, require at least {1} element(s)";
        public const string ElementsTooMany = "{0} has too many elements, accept at most {1} element(s)";
        public const string FieldRequired = "Required field unspecified: {0}";
        public const string LengthExceeded = "{0} exceeded maximum length of {1}";
        public const string RangeInclusiveExceeded = "{0} must be between {1} and {2} inclusive";
        public const string UrlInvalid = "Invalid URL: {0}";
        public const string ValueInvalid = "Invalid value: {0}";
        public const string ValueNotDefault = "Default value not allowed: {0}";
    }
}
