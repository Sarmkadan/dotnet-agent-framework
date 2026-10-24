using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DotnetAgentFramework;

/// <summary>
/// Derives a JSON Schema document from a .NET type by reflection, honouring the
/// <c>System.Text.Json</c> attributes that control property names and serialization.
/// </summary>
internal static class JsonSchemaGenerator
{
    /// <summary>
    /// Generates a JSON Schema describing <paramref name="type"/>.
    /// </summary>
    /// <param name="type">Type to describe. Classes and records produce an object schema.</param>
    /// <returns>The schema as a JSON element.</returns>
    public static JsonElement Generate(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var schema = SchemaFor(type, new HashSet<Type>());
        return JsonSerializer.SerializeToElement(schema);
    }

    private static JsonObject SchemaFor(Type type, HashSet<Type> ancestors)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(string) || type == typeof(char))
        {
            return Typed("string");
        }

        if (type == typeof(bool))
        {
            return Typed("boolean");
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
            || type == typeof(sbyte) || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort))
        {
            return Typed("integer");
        }

        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
        {
            return Typed("number");
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        }

        if (type == typeof(Guid))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "uuid" };
        }

        if (type.IsEnum)
        {
            return new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(Enum.GetNames(type).Select(name => (JsonNode)name).ToArray()),
            };
        }

        if (type == typeof(object) || type == typeof(JsonElement))
        {
            // Any JSON value is acceptable, so the schema constrains nothing.
            return new JsonObject();
        }

        if (type.IsArray)
        {
            return ArraySchema(type.GetElementType()!, ancestors);
        }

        var dictionary = FindGenericInterface(type, typeof(IReadOnlyDictionary<,>))
            ?? FindGenericInterface(type, typeof(IDictionary<,>));
        if (dictionary is not null)
        {
            var typeArguments = dictionary.GetGenericArguments();
            if (typeArguments[0] != typeof(string))
            {
                throw new NotSupportedException($"Dictionary keys must be strings to generate a JSON Schema, but '{type}' uses '{typeArguments[0]}'.");
            }

            return new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = SchemaFor(typeArguments[1], ancestors),
            };
        }

        var enumerable = FindGenericInterface(type, typeof(IEnumerable<>));
        if (enumerable is not null)
        {
            return ArraySchema(enumerable.GetGenericArguments()[0], ancestors);
        }

        // A type already being expanded higher up the tree is a cycle; stop here instead of recursing forever.
        if (!ancestors.Add(type))
        {
            return Typed("object");
        }

        try
        {
            return ObjectSchema(type, ancestors);
        }
        finally
        {
            ancestors.Remove(type);
        }
    }

    private static JsonObject ObjectSchema(Type type, HashSet<Type> ancestors)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.GetMethod?.IsPublic != true)
            {
                continue;
            }

            var jsonIgnore = property.GetCustomAttribute<JsonIgnoreAttribute>();
            if (jsonIgnore?.Condition == JsonIgnoreCondition.Always)
            {
                continue;
            }

            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);

            var schema = SchemaFor(property.PropertyType, ancestors);
            var description = property.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (description is not null)
            {
                schema["description"] = description;
            }

            properties[name] = schema;

            if (IsRequired(property, jsonIgnore))
            {
                required.Add(name);
            }
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject ArraySchema(Type elementType, HashSet<Type> ancestors)
        => new()
        {
            ["type"] = "array",
            ["items"] = SchemaFor(elementType, ancestors),
        };

    private static bool IsRequired(PropertyInfo property, JsonIgnoreAttribute? jsonIgnore)
    {
        if (jsonIgnore?.Condition is JsonIgnoreCondition.WhenWritingDefault or JsonIgnoreCondition.WhenWritingNull)
        {
            return false;
        }

        if (Nullable.GetUnderlyingType(property.PropertyType) is not null)
        {
            return false;
        }

        if (!property.PropertyType.IsValueType
            && new NullabilityInfoContext().Create(property).WriteState == NullabilityState.Nullable)
        {
            return false;
        }

        return true;
    }

    private static Type? FindGenericInterface(Type type, Type genericDefinition)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == genericDefinition)
        {
            return type;
        }

        return type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == genericDefinition);
    }

    private static JsonObject Typed(string type) => new() { ["type"] = type };
}
