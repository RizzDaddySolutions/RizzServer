namespace RizzServer.Core;

/// <summary>Naming rules shared by the UI, the wizard and the REST API. Names become part of OPC UA NodeIds (Channel.Device.Tag).</summary>
public static class NameRules
{
    /// <returns>An error message, or null when the name is acceptable.</returns>
    public static string? Validate(string name, string what, IEnumerable<string> otherNames)
    {
        name = name.Trim();
        if (name.Length == 0) return $"Enter a {what} name.";
        if (name.Contains('.')) return $"The {what} name cannot contain a period.";
        if (otherNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            return $"A {what} named '{name}' already exists.";
        return null;
    }
}
