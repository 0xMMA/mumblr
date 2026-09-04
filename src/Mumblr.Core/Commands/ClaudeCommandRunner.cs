using System.Diagnostics;
using System.Text;
using Mumblr.Core.Config;

namespace Mumblr.Core.Commands;

/// <summary>Runs one stateless <c>claude -p</c> call against the dictation file.</summary>
public interface IClaudeCommandRunner
{
    Task<CommandResult> RunAsync(string commandText, string absoluteFilePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Spawns the locally installed claude CLI. No multi-turn, no tool API of our own: Claude edits
/// the file with its native Read/Edit tools and reports back in one line.
/// </summary>
public sealed class ClaudeCommandRunner : IClaudeCommandRunner
{
    private readonly Func<ClaudeConfig> configFactory;

    public ClaudeCommandRunner(Func<ClaudeConfig> configFactory) => this.configFactory = configFactory;

    public async Task<CommandResult> RunAsync(string commandText, string absoluteFilePath, CancellationToken cancellationToken = default)
    {
        var config = configFactory();
        var stopwatch = Stopwatch.StartNew();

        var startInfo = new ProcessStartInfo
        {
            FileName = config.Executable,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(absoluteFilePath))!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in ClaudeArgsBuilder.Build(config, commandText, absoluteFilePath))
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return CommandResult.Failure($"Could not start '{config.Executable}': {ex.Message}", stopwatch.Elapsed);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return CommandResult.Failure($"claude timed out after {config.TimeoutSeconds}s", stopwatch.Elapsed);
        }

        var output = stdout.ToString();
        var error = stderr.ToString().Trim();

        if (process.ExitCode != 0)
            return CommandResult.Failure(
                $"claude exited with {process.ExitCode}: {(error.Length > 0 ? error : output.Trim())}",
                stopwatch.Elapsed);

        if (ClaudeArgsBuilder.IsErrorResult(output))
            return new CommandResult(false, ClaudeArgsBuilder.ExtractSummary(output), output, stopwatch.Elapsed,
                ClaudeArgsBuilder.ExtractModel(output));

        var summary = ClaudeArgsBuilder.ExtractSummary(output);
        return new CommandResult(true, summary.Length > 0 ? summary : "(no summary)", output, stopwatch.Elapsed,
            ClaudeArgsBuilder.ExtractModel(output));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process is already gone.
        }
    }
}
