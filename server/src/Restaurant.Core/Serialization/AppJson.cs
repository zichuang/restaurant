using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Restaurant.Core.Serialization;

/// <summary>
/// 全项目唯一的 JSON 配置。API 出参、JSON 列读写、WebSocket 帧、小票模板
/// 全部用它，避免出现「接口是 camelCase、JSON 列是 PascalCase」这种两套字段名。
/// </summary>
public static class AppJson
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null, // 规格组的 key 是中文组名，不能改大小写
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 中文不转成 \uXXXX：JSON 列要人工看得懂，小票模板也要直接可读
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Default);

    /// <summary>
    /// 反序列化 JSON 列。字段为 NULL 或空串时返回空列表，
    /// 而不是抛异常——历史数据里 specs 为 NULL 是正常情况。
    /// </summary>
    public static T? DeserializeOrNull<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Default);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static List<T> DeserializeList<T>(string? json)
        => DeserializeOrNull<List<T>>(json) ?? [];
}
