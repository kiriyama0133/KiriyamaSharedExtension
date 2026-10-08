namespace KiriyamaShareBackend.Infrastructure.Logging;

public class LoggerSettings
{
    /// <summary>
    /// Default section name.
    /// </summary>
    public const string Position = "loggerSettings";

    public string AppName { get; set; } = "KiriyamaShareBackend.WebAPI";

    public bool ElasticEnabled { get; set; }
    public string ElasticSearchUrl { get; set; } = string.Empty;
    public bool WriteToFile { get; set; } = false;
    public bool StructuredConsoleLogging { get; set; } = false;
    public string MinimumLogLevel { get; set; } = "Information";
}
