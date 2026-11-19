using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Ces.Spot;

namespace Ces.SpotUi;

/// <summary>
/// JSON for <see cref="AnalysisResult"/> and everything inside it (results cache, saved Spot results, per-file
/// details). The analysis types keep their setters internal, so a resolver fills them (reflection: this lives in the
/// app, not in the trimmable Ces.Spot library): non-public setters are used,
/// get-only collections are populated and fixed-size arrays copied into; computed properties are left out.
/// </summary>
public static class SpotJson
{
    /// <summary>Bumped whenever the analysis output changes, so cached results from older versions are ignored.</summary>
    public const int FormatVersion = 2;

    public static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { IncludeInternals } },
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static JsonTypeInfo<AnalysisResult> Result => (JsonTypeInfo<AnalysisResult>)Options.GetTypeInfo(typeof(AnalysisResult));

    /// <summary>Indented, for files people read.</summary>
    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static string Serialize(AnalysisResult result, bool indented = false) =>
        JsonSerializer.Serialize(result, indented ? Indented : Options);

    public static AnalysisResult? Deserialize(string json) => JsonSerializer.Deserialize<AnalysisResult>(json, Options);

    private static void IncludeInternals(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || info.Type.Assembly != typeof(AnalysisResult).Assembly)
            return;
        foreach (var prop in info.Properties.ToList())
        {
            if (prop.AttributeProvider is not PropertyInfo pi)
                continue;
            if (prop.Set != null)
                continue;
            var setter = pi.GetSetMethod(nonPublic: true);
            if (setter != null)
            {
                prop.Set = (obj, value) => pi.SetValue(obj, value);
                continue;
            }
            if (pi.PropertyType.IsArray)
            {
                // fixed-size histograms: copy into the existing array
                prop.Set = (obj, value) =>
                {
                    if (pi.GetValue(obj) is Array target && value is Array source)
                        Array.Copy(source, target, Math.Min(source.Length, target.Length));
                };
                continue;
            }
            if (!pi.PropertyType.IsValueType && pi.PropertyType != typeof(string))
                continue; // get-only collection or nested object (Layer3Stats): populated in place
            // computed (Artist, Bitrate of a header, ...) unless a constructor parameter takes it
            if (!IsConstructorParameter(info, prop))
                info.Properties.Remove(prop);
        }
    }

    private static bool IsConstructorParameter(JsonTypeInfo info, JsonPropertyInfo prop) =>
        info.Type.GetConstructors().Any(c => c.GetParameters().Length > 0
            && c.GetParameters().Any(p => string.Equals(p.Name, prop.Name, StringComparison.OrdinalIgnoreCase)));
}
