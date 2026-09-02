using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace AECS.Application.Parsing;

internal static class TaskContractYamlShapeValidator
{
    public static void Validate(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            throw new InvalidOperationException("TaskContract YAML cannot be empty.");

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"TaskContract YAML is invalid: {exception.Message}",
                exception);
        }

        if (stream.Documents.Count != 1)
            throw new InvalidOperationException("TaskContract YAML must contain exactly one document.");

        ValidateNode(stream.Documents[0].RootNode, typeof(TaskYamlRoot), "$", false);
    }

    private static void ValidateNode(
        YamlNode node,
        Type expectedType,
        string path,
        bool sequenceElement)
    {
        expectedType = Nullable.GetUnderlyingType(expectedType) ?? expectedType;
        if (TryGetSequenceElement(expectedType, out var elementType))
        {
            if (node is not YamlSequenceNode sequence)
                throw ShapeError(path, "sequence");
            for (var index = 0; index < sequence.Children.Count; index++)
                ValidateNode(sequence.Children[index], elementType, $"{path}[{index}]", true);
            return;
        }

        if (IsScalar(expectedType))
        {
            if (node is not YamlScalarNode)
                throw ShapeError(path, "scalar");
            return;
        }

        if (node is not YamlMappingNode mapping)
            throw ShapeError(path, sequenceElement ? "object" : "mapping");

        var properties = expectedType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .ToDictionary(property => property.Name, StringComparer.Ordinal);
        var canonicalKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in mapping.Children)
        {
            if (child.Key is not YamlScalarNode scalarKey ||
                string.IsNullOrWhiteSpace(scalarKey.Value))
            {
                throw new InvalidOperationException(
                    $"TaskContract property keys at '{path}' must be non-empty scalars.");
            }

            var key = scalarKey.Value;
            if (!properties.TryGetValue(key, out var property))
            {
                throw new InvalidOperationException(
                    $"Unknown TaskContract property '{Append(path, key)}'.");
            }

            var canonicalKey = ToSnakeCase(property.Name);
            if (!canonicalKeys.Add(canonicalKey))
            {
                throw new InvalidOperationException(
                    $"TaskContract property '{Append(path, canonicalKey)}' is declared more than once or through conflicting aliases.");
            }

            ValidateNode(
                child.Value,
                property.PropertyType,
                Append(path, canonicalKey),
                false);
        }
    }

    private static InvalidOperationException ShapeError(string path, string expected) =>
        new($"TaskContract property '{path}' must be a YAML {expected}.");

    private static bool TryGetSequenceElement(Type type, out Type elementType)
    {
        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type) &&
            type.IsGenericType)
        {
            elementType = type.GetGenericArguments()[0];
            return true;
        }

        elementType = typeof(object);
        return false;
    }

    private static bool IsScalar(Type type) =>
        type == typeof(string) || type == typeof(bool) || type == typeof(int) ||
        type == typeof(long) || type == typeof(decimal) || type.IsEnum;

    private static string Append(string path, string key) =>
        path == "$" ? $"$.{key}" : $"{path}.{key}";

    private static string ToSnakeCase(string value) => Regex.Replace(
        value,
        "([a-z0-9])([A-Z])",
        "$1_$2",
        RegexOptions.CultureInvariant).ToLowerInvariant();
}
