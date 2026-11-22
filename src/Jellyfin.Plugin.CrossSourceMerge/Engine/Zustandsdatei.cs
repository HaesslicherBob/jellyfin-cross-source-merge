using System.Text.Json;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>
/// Checks a state file before it replaces the running state.
/// Deliberately free of any Jellyfin dependency so it can be tested alone.
/// </summary>
public static class Zustandsdatei
{
    /// <summary>Is the file readable and a JSON object?</summary>
    public static bool Lesbar(string datei, out string meldung)
    {
        meldung = string.Empty;

        try
        {
            using var strom = File.OpenRead(datei);
            using var dokument = JsonDocument.Parse(strom);
            if (dokument.RootElement.ValueKind != JsonValueKind.Object)
            {
                meldung = "not a JSON object";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            meldung = "not valid JSON: " + ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            meldung = ex.Message;
            return false;
        }
    }
}
