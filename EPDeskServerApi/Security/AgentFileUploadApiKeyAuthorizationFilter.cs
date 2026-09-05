using System.Security.Cryptography;
using System.Text;
using EPDeskServerApi.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace EPDeskServerApi.Security;

/// <summary>
/// Restricts automatic-upload lifecycle calls to configured desktop agents.
/// Comparing fixed-size SHA-256 digests avoids revealing the configured key's
/// length through the comparison path.
/// </summary>
public sealed class AgentFileUploadApiKeyAuthorizationFilter(
    IOptions<AgentFileUploadSecurityOptions> options
) : IAuthorizationFilter
{
    private readonly byte[] _expectedDigest = SHA256.HashData(
        Encoding.UTF8.GetBytes(options.Value.AgentFileUploadApiKey)
    );

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var providedApiKey = context.HttpContext.Request.Headers[
            AgentFileUploadSecurityOptions.HeaderName
        ].ToString();

        var providedDigest = SHA256.HashData(
            Encoding.UTF8.GetBytes(providedApiKey)
        );

        if (!CryptographicOperations.FixedTimeEquals(
                _expectedDigest,
                providedDigest
            ))
        {
            context.Result = new UnauthorizedResult();
        }
    }
}
