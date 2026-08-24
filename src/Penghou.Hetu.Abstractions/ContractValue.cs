namespace Penghou.Hetu;

internal static class ContractValue
{
    public static string Required(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    public static string Identifier(string? value, string parameterName)
    {
        var result = Required(value, parameterName);
        if (!string.Equals(result, result.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Identifiers cannot have leading or trailing whitespace.",
                parameterName);
        }

        if (result.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Identifiers cannot contain control characters.",
                parameterName);
        }

        return result;
    }

    public static string RelativePath(string? value, string parameterName)
    {
        var result = Identifier(value, parameterName)
            .Replace('\\', '/');
        if (Path.IsPathRooted(result) ||
            result.Split('/').Any(segment => segment == ".."))
        {
            throw new ArgumentException(
                "Source paths must be repository-relative and cannot traverse parents.",
                parameterName);
        }

        return result;
    }
}
