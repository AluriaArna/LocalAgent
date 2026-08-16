using System.Diagnostics;

namespace AgentUi;

public static class RunAs
{
    public static async Task<(int exitCode, string output)> ExecAsync(
        string user, string? workingDir, string command, string? input = null)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input != null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (user == Environment.UserName)
        {
            // текущий пользователь — sudo не нужен
            psi.FileName = "bash";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            // -n = не спрашивать пароль (если прав нет — упадёт с понятной ошибкой)
            psi.FileName = "sudo";
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(user);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        if (workingDir != null) psi.WorkingDirectory = workingDir;

        using var p = Process.Start(psi)!;
        if (input != null)
        {
            await p.StandardInput.WriteAsync(input);
            p.StandardInput.Close();
        }

        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();

        var combined = stdout;
        if (!string.IsNullOrWhiteSpace(stderr))
            combined += "\n[stderr] " + stderr;

        return (p.ExitCode, combined);
    }
}