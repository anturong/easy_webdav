using System.Diagnostics;

namespace WebDAVConfigurator;

public static class FirewallHelper
{
    private const string HttpRuleName = "WebDAV Service (HTTP)";
    private const string HttpsRuleName = "WebDAV Service (HTTPS)";
    private const string LegacyRuleName = "WebDAV Service";

    public static List<(bool success, string message)> EnsureRules(WebDAVConfigData config)
    {
        var results = new List<(bool, string)>();

        var legacy = RunNetsh($"advfirewall firewall delete rule name=\"{LegacyRuleName}\"");
        if (legacy.exitCode == 0)
            results.Add((true, "已清理旧版防火墙规则"));

        ApplyRule(HttpRuleName, config.EnableHttp, config.Port, results);
        ApplyRule(HttpsRuleName, config.EnableHttps, config.HttpsPort, results);
        return results;
    }

    public static List<(bool success, string message)> RemoveRules()
    {
        var results = new List<(bool, string)>();
        foreach (var name in new[] { HttpRuleName, HttpsRuleName, LegacyRuleName })
        {
            var result = RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
            if (result.exitCode == 0)
                results.Add((true, $"已删除防火墙规则: {name}"));
        }
        return results;
    }

    private static void ApplyRule(string ruleName, bool enabled, int port, List<(bool, string)> results)
    {
        RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
        if (!enabled)
        {
            results.Add((true, $"{ruleName}: 未启用，规则已清理"));
            return;
        }

        var result = RunNetsh(
            $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port} profile=any");
        if (result.exitCode == 0)
            results.Add((true, $"防火墙已放行 {ruleName} 端口 {port}"));
        else
            results.Add((false, $"防火墙规则添加失败 ({ruleName} 端口 {port}): {GetError(result)}"));
    }

    private static string GetError((int exitCode, string stdout, string stderr) result)
    {
        var text = string.IsNullOrWhiteSpace(result.stderr) ? result.stdout : result.stderr;
        return text.Trim();
    }

    private static (int exitCode, string stdout, string stderr) RunNetsh(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(10000);
        return (process.ExitCode, stdout, stderr);
    }
}
