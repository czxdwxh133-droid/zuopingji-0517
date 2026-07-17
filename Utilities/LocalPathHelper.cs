namespace NewsBriefingAssistant.Utilities;

public static class LocalPathHelper
{
    public static string GetDataDirectory()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
    }
}
