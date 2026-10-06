using ServiceMantle.Installation;

namespace ServiceMantle.ReferenceService.Tests;

internal static class ReferenceSetupCodeBanner
{
    internal static bool TryRead(string output, out string code)
    {
        code = string.Empty;
        var lines = output.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var banner = Array.IndexOf(lines, "one-time setup code:");
        if (banner < 0 || Array.IndexOf(lines, "one-time setup code:", banner + 1) >= 0)
            return false;

        // Console logging and stderr may arrive between the banner's stdout lines.
        // The expiry line closes the block; do not consume a partial captured banner.
        var end = Array.FindIndex(lines, banner + 1,
            line => line.StartsWith("expires at ", StringComparison.Ordinal));
        if (end < 0)
            return false;

        var candidates = lines[(banner + 1)..end]
            .Where(line => SetupCode.TryParse(line, out _)).ToArray();
        if (candidates.Length != 1)
            return false;

        code = candidates[0];
        return true;
    }
}
