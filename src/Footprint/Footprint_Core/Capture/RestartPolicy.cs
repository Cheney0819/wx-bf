namespace Footprint.Core.Capture;

public enum RestartPolicy
{
    AutoOnce,
    RemoteOnly,
    Disabled
}

public static class RestartPolicyParser
{
    public static bool TryParse(string? value, out RestartPolicy policy)
    {
        switch (value)
        {
            case nameof(RestartPolicy.AutoOnce):
                policy = RestartPolicy.AutoOnce;
                return true;
            case nameof(RestartPolicy.RemoteOnly):
                policy = RestartPolicy.RemoteOnly;
                return true;
            case nameof(RestartPolicy.Disabled):
                policy = RestartPolicy.Disabled;
                return true;
            default:
                policy = default;
                return false;
        }
    }
}
