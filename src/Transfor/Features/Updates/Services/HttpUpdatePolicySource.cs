using System.Text.Json;

namespace Transfor;

// HTTP 策略源：从 GitHub raw 静态 update-policy.json 获取远程策略；
// 任何网络错误 / 非成功状态 / JSON 损坏都抛异常（→ CheckFailed），不产出策略
internal sealed class HttpUpdatePolicySource : IUpdatePolicySource
{
    // 发布走 Release 分支：update-policy.json 与版本发布同分支维护
    public const string DefaultPolicyUrl = "https://raw.githubusercontent.com/Flyhr/Transfor/Release/update-policy.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient client;
    private readonly SafeUriValidator validator;
    private readonly string policyUrl;

    public HttpUpdatePolicySource(HttpClient client, SafeUriValidator validator, string? policyUrl = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.policyUrl = policyUrl ?? DefaultPolicyUrl;
    }

    public async Task<UpdatePolicy?> FetchAsync(CancellationToken cancellationToken)
    {
        var uri = new Uri(policyUrl);
        var validation = await validator.ValidateAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!validation.IsAllowed)
        {
            throw new InvalidOperationException($"更新策略地址不合法：{validation.Error}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", "Transfor-Updater");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"更新策略请求失败：HTTP {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<UpdatePolicy>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("更新策略 JSON 损坏。", ex);
        }
    }
}
