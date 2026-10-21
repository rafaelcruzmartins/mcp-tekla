using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace TeklaMcp.Infrastructure;

/// <summary>Opções JSON de entrada (schema das ferramentas) e de saída.</summary>
public static class Fmt
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        // O SDK faz MakeReadOnly() nas opções: o resolver tem que estar definido antes.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static JsonSerializerOptions OutputOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Out(object? value) => JsonSerializer.Serialize(value, OutputOptions);

    public static double R(double v, int digits = 3) =>
        double.IsNaN(v) || double.IsInfinity(v) ? v : Math.Round(v, digits);
}
