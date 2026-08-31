using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;

namespace ServiceMantle.Logging;

/// <summary>
/// Reads exceptions, dictionaries, enumerables, and public object members while delegating
/// recursive dispatch, reference tracking, and budgets to the coordinator-owned traversal context.
/// </summary>
internal sealed class StructuredLogObjectDeconstructor
{
    internal bool TryDeconstruct(
        object value,
        int depth,
        StructuredLogTraversalCoordinator.TraversalContext context,
        out object? sanitized)
    {
        if (value is Exception exception)
        {
            sanitized = DeconstructException(exception, depth, context);
            return true;
        }

        if (value is IDictionary dictionary)
        {
            sanitized = DeconstructDictionary(dictionary, depth, context);
            return true;
        }

        if (IsGenericDictionary(value.GetType()))
        {
            sanitized = DeconstructGenericDictionary(value, depth, context);
            return true;
        }

        if (value is IEnumerable enumerable)
        {
            sanitized = DeconstructEnumerable(enumerable, depth, context);
            return true;
        }

        if (value is Delegate or Stream or Task)
        {
            sanitized = null;
            return false;
        }

        sanitized = DeconstructPublicObject(value, depth, context);
        return true;
    }

    private static IReadOnlyDictionary<string, object?> DeconstructException(
        Exception exception,
        int depth,
        StructuredLogTraversalCoordinator.TraversalContext context)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ExceptionType"] = exception.GetType().FullName ?? exception.GetType().Name
        };

        if (exception.InnerException is not null)
        {
            output["InnerException"] = context.SanitizeNestedValue(exception.InnerException, depth + 1);
        }

        return new ReadOnlyDictionary<string, object?>(output);
    }

    private static IReadOnlyDictionary<string, object?> DeconstructDictionary(
        IDictionary dictionary,
        int depth,
        StructuredLogTraversalCoordinator.TraversalContext context)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        var count = 0;
        foreach (DictionaryEntry entry in dictionary)
        {
            if (!context.TryAcceptCollectionItem(ref count))
            {
                output["CollectionTruncated"] = StructuredLogSanitizer.CollectionTruncated;
                break;
            }

            if (!context.TryGetFieldName(entry.Key, out var fieldName))
            {
                continue;
            }

            context.AddField(
                output,
                fieldName,
                entry.Value,
                depth + 1);
        }

        return new ReadOnlyDictionary<string, object?>(output);
    }

    private static bool IsGenericDictionary(Type type) =>
        type.GetInterfaces().Any(candidate =>
        {
            if (!candidate.IsGenericType)
            {
                return false;
            }

            var definition = candidate.GetGenericTypeDefinition();
            if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
            {
                return true;
            }

            if (definition != typeof(IEnumerable<>))
            {
                return false;
            }

            var elementType = candidate.GetGenericArguments()[0];
            return elementType.IsGenericType &&
                elementType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);
        });

    private static IReadOnlyDictionary<string, object?> DeconstructGenericDictionary(
        object value,
        int depth,
        StructuredLogTraversalCoordinator.TraversalContext context)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        var count = 0;
        foreach (var item in (IEnumerable)value)
        {
            if (!context.TryAcceptCollectionItem(ref count))
            {
                output["CollectionTruncated"] = StructuredLogSanitizer.CollectionTruncated;
                break;
            }

            if (item is null)
            {
                continue;
            }

            var itemType = item.GetType();
            var keyProperty = itemType.GetProperty("Key", BindingFlags.Instance | BindingFlags.Public);
            var valueProperty = itemType.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
            if (keyProperty is null || valueProperty is null)
            {
                throw new InvalidOperationException();
            }

            var key = keyProperty.GetValue(item);
            if (!context.TryGetFieldName(key, out var fieldName))
            {
                continue;
            }

            if (context.IsDeniedField(fieldName))
            {
                context.AddField(
                    output,
                    fieldName,
                    value: null,
                    depth + 1);
                continue;
            }

            context.AddField(
                output,
                fieldName,
                valueProperty.GetValue(item),
                depth + 1);
        }

        return new ReadOnlyDictionary<string, object?>(output);
    }

    private static IReadOnlyList<object?> DeconstructEnumerable(
        IEnumerable enumerable,
        int depth,
        StructuredLogTraversalCoordinator.TraversalContext context)
    {
        var output = new List<object?>();
        var count = 0;
        foreach (var item in enumerable)
        {
            if (!context.TryAcceptCollectionItem(ref count))
            {
                output.Add(StructuredLogSanitizer.CollectionTruncated);
                break;
            }

            output.Add(context.SanitizeNestedValue(item, depth + 1));
        }

        return output.AsReadOnly();
    }

    private static object DeconstructPublicObject(
        object value,
        int depth,
        StructuredLogTraversalCoordinator.TraversalContext context)
    {
        var type = value.GetType();
        var members = type
            .GetMembers(BindingFlags.Instance | BindingFlags.Public)
            .Where(member => member is PropertyInfo or FieldInfo)
            .OrderBy(member => member.Name, StringComparer.Ordinal)
            .ToArray();

        if (members.Length == 0)
        {
            return $"[OBJECT:{type.FullName ?? type.Name}]";
        }

        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        var count = 0;
        foreach (var member in members)
        {
            if (!context.TryAcceptCollectionItem(ref count))
            {
                output["CollectionTruncated"] = StructuredLogSanitizer.CollectionTruncated;
                break;
            }

            if (!context.TryClassifyField(
                    member.Name,
                    out var outputName,
                    out var denied,
                    out var allowed) ||
                !allowed)
            {
                continue;
            }

            if (denied)
            {
                context.AddOutput(
                    output,
                    outputName,
                    StructuredLogSanitizer.RedactedValue);
                continue;
            }

            object? memberValue;
            try
            {
                memberValue = member switch
                {
                    PropertyInfo property when
                        property.GetMethod is not null &&
                        !property.GetMethod.IsStatic &&
                        property.GetIndexParameters().Length == 0 => property.GetValue(value),
                    FieldInfo field when !field.IsStatic => field.GetValue(value),
                    _ => StructuredLogSanitizer.SanitizationFailed
                };
            }
            catch
            {
                memberValue = StructuredLogSanitizer.SanitizationFailed;
            }

            context.AddOutput(
                output,
                outputName,
                context.SanitizeNestedValue(memberValue, depth + 1));
        }

        return new ReadOnlyDictionary<string, object?>(output);
    }
}
