using System.Diagnostics;

public enum ItemStatus
{
    None,
    Found,
    Check,
    Ignore,
    UpToDate,
    Dirty,
    Behind,
    Ahead, // TODO
    Pull,
    Error,
}

public enum RunStatus
{
    Pending,
    Running,
    Complete,
    Error
}

public static class ProcessResultHelper
{
    public static string FirstLineOrError(this ProcessResult? result)
    {
        if (result is null) return "<ERR>";
        if (result.StdOut.Count == 0) return "<ERR>";
        return result.StdOut.First();
    }
}

public class RecoverableException : Exception
{
    public RecoverableException()
    {
    }

    public RecoverableException(string? message) : base(message)
    {
    }
}

public class GitRoot
{
    ProcessResult? gitStatus;
    ProcessResult? gitFetch;
    ProcessResult? gitRemote;
    ProcessResult? gitLog;
    ProcessResult? gitPull;
    ILogger logger;

    public GitRoot(string path, string relPath)
    {
        Path = path;
        PathRelative = relPath;
        logger = Program.LoggerFactory.GetLogger(nameof(GitRoot) + ":" + relPath);
    }

    public string Path { get; }
    public string PathRelative { get; }
    public ItemStatus Status { get; set; } = ItemStatus.Found;
    public RunStatus StatusRunning { get; set; } = RunStatus.Pending;
    public Exception? Error { get; private set; }
    public TimeSpan Duration { get; private set; }

    public bool IsComplete => StatusRunning == RunStatus.Complete || StatusRunning == RunStatus.Error;
    public string? LogFirstLine => gitLog?.StdOut.FirstOrDefault();

    public DateTime Started { get; private set; }

    private async Task<ProcessResult> RunYielding(string cmd, string args, bool checkStdOut = true)
    {
        var res = await ProcessRunner.RunYieldingProcessResult(cmd, args, Path, 50, TimeSpan.FromSeconds(30));
        logger.Log($"CMD: {cmd} {args} ==> ExitCode:{res.ExitCode} in {res.Duration} [std: {res.StdOut.Count}, err: {res.StdErr.Count}]");
        if (res.TimeOutBeforeComplete)
        {
            logger.Log("CMD-TIMEOUT!");
        }
        foreach(var err in res.StdErr)
        {
            logger.Log($"ERR: {err}");
        }
        foreach(var lin in res.StdOut)
        {
            logger.Log(lin);
        }
        if (checkStdOut && res.ExitCode == 0 && res.StdOut.Count == 0)
        {
            logger.Log("WARN: No error (ExitCode=0), but no std out. {cmd} {arg}");
        }

        return res;
    }

    public IEnumerable<ProcessResult> GetProcessResults()
    {
        if (gitFetch != null) yield return gitFetch;
        if (gitStatus != null) yield return gitStatus;
        if (gitLog != null) yield return gitLog;
        if (gitRemote != null) yield return gitRemote;
        if (gitPull != null) yield return gitPull;
    }

    public async Task GitStatus()
    {
        gitStatus = await RunYielding("git", "status -bs");
        gitStatus.ThrowOnBadExitCode(nameof(gitStatus)); // check after assignement so we still record erros
    }

    public async Task GitFetch()
    {
        gitFetch = await RunYielding("git", "fetch", false);
        gitFetch.ThrowOnBadExitCode(nameof(gitFetch)); // check after assignement so we still record erros
    }

    public async Task GitRemote()
    {
        gitRemote = await RunYielding("git", "remote -v");
        gitRemote.ThrowOnBadExitCode(nameof(gitRemote)); // check after assignement so we still record erros
    }

    public async Task GitLog()
    {
        gitLog = await RunYielding("git", "log --pretty=\"(%cd) %s\" --date=relative -10");
        gitLog.ThrowOnBadExitCode(nameof(gitLog)); // check after assignement so we still record erros
    }

    public async Task GitPull()
    {
        gitPull = await RunYielding("git", "pull");
        gitPull.ThrowOnBadExitCode(nameof(gitPull)); // check after assignement so we still record erros
    }

    public string StatusLine()
    {
        if (Status == ItemStatus.Error)
        {
            foreach(var proc in GetProcessResults())
            {
                if (proc.StdErr != null && proc.StdErr.FirstOrDefault() is {} firstError)
                {
                    return firstError;
                }
            }
            return "Unknown error";
        }
        if (StatusRunning == RunStatus.Error) return $"<ERROR> {Error?.Message}";
        if (Status == ItemStatus.Found) return "";
        if (Status == ItemStatus.Ignore)
        {
            if (gitLog != null)
            {
                return gitLog.FirstLineOrError();
            }
            if (gitStatus != null)
            {
                return gitStatus.FirstLineOrError();
            }
            return "";
        }
        if (gitStatus != null)
        {
            if (Status == ItemStatus.Behind) return gitStatus.FirstLineOrError();
            if (Status == ItemStatus.Ahead) return gitStatus.FirstLineOrError();
            if (Status == ItemStatus.Dirty && gitStatus.StdOut.Count > 1)
            {
                return $"[{gitStatus.StdOut.Count-1} files] {gitStatus.StdOut[1]}";

            }
        }
        if (Status == ItemStatus.Check) return "";
        if (Status == ItemStatus.UpToDate)
        {
            if (gitLog != null)
            {
                if (gitLog.StdOut.Count > 0)
                {
                    return gitLog.FirstLineOrError();
                }
                if (gitLog.StdErr.Count > 0)
                {
                    foreach(var ln in gitLog.StdErr)
                    {
                        Console.Error.WriteLine($"{PathRelative}|{ln}");
                    }
                    return gitLog.StdErr.First();
                }
                if (gitLog.ExitCode != 0) return $"exitcode: {gitLog.ExitCode}";
            }
            return "";
        }
        if (Status == ItemStatus.Pull)
        {
            if (gitPull != null)
            {
                return gitPull.FirstLineOrError();
            }
        }
        return $"{Status}";
    }

    public async Task Process(GitStatusApp app)
    {
        Started = DateTime.Now;
        var timer = new Stopwatch();
        try
        {
            timer.Start();
            StatusRunning = RunStatus.Running;
            if (Status == ItemStatus.Ignore)
            {
                StatusRunning = RunStatus.Complete;
                return;
            }

            Status = ItemStatus.Check;
            if (app.ArgRemote) await GitRemote();
            var fetch = app.ShouldFetch(this);
            if (fetch)
            {
                await GitFetch();
            }

            await GitStatus();
            if (gitStatus != null && gitStatus.StdOut.Count <= 1)
            {
                var lineOne = gitStatus.FirstLineOrError();
                if (lineOne.Contains("[behind "))
                {
                    Status = ItemStatus.Behind;
                    if (app.ArgPull)
                    {
                        Status = ItemStatus.Pull;
                        await GitPull();
                        return;
                    }
                    return;
                }
                else if (lineOne.Contains("[ahead "))
                {
                    Status = ItemStatus.Ahead;
                    return;
                }
                else
                {
                    await GitLog();
                    if (fetch)
                    {
                        Status = ItemStatus.UpToDate;
                        return;
                    }
                    else
                    {
                        // did not fetch, do not sure if we are up to date
                        Status = ItemStatus.Ignore;
                        return;
                    }
                }
            }
            else
            {
                Status = ItemStatus.Dirty;
            }
        }
        catch(RecoverableException)
        {
            Status = ItemStatus.Error;
            StatusRunning = RunStatus.Error;
        }
        catch(Exception ex)
        {
            Status = ItemStatus.Error;
            StatusRunning = RunStatus.Error;
            Error = ex;
        }
        finally
        {
            timer.Stop();
            Duration = timer.Elapsed;
            if (StatusRunning != RunStatus.Error) StatusRunning = RunStatus.Complete;
        }
    }
}

