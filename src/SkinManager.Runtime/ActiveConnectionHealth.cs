using System.Net;

namespace SkinManager.Runtime;

public enum ActiveConnectionHealthState
{
    Verified,
    ProcessExited,
    IdentityChanged,
    EndpointOwnershipChanged,
    Unverifiable
}

public sealed record ActiveConnectionHealthResult(
    ActiveConnectionHealthState State,
    string Message)
{
    public bool ShouldDisconnect => State is
        ActiveConnectionHealthState.ProcessExited or
        ActiveConnectionHealthState.IdentityChanged or
        ActiveConnectionHealthState.EndpointOwnershipChanged;

    public static ActiveConnectionHealthResult Verified(string message = "活动 Codex 连接的进程身份和监听归属仍有效。") =>
        new(ActiveConnectionHealthState.Verified, message);

    public static ActiveConnectionHealthResult Unverifiable(string message) =>
        new(ActiveConnectionHealthState.Unverifiable, message);
}

internal static class ActiveConnectionEndpoint
{
    public static bool TryParse(string? value, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttp || !parsed.IsLoopback || parsed.Port is < 1 or > 65535)
            return false;

        endpoint = parsed;
        return true;
    }

    public static bool SameAddress(Uri left, Uri right)
    {
        if (left.Port != right.Port || left.Scheme != right.Scheme)
            return false;

        if (IPAddress.TryParse(left.DnsSafeHost, out var leftAddress) &&
            IPAddress.TryParse(right.DnsSafeHost, out var rightAddress))
            return leftAddress.Equals(rightAddress);

        return string.Equals(left.DnsSafeHost, right.DnsSafeHost, StringComparison.OrdinalIgnoreCase);
    }
}
