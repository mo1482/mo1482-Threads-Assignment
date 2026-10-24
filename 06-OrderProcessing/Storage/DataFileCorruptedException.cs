namespace OrderProcessingSystem.Storage;

public sealed class DataFileCorruptedException : Exception
{
    public string FileName { get; }

    public DataFileCorruptedException(string fileName, Exception innerException)
        : base($"JSON data file '{fileName}' is corrupted or contains invalid JSON.", innerException)
    {
        FileName = fileName;
    }
}
