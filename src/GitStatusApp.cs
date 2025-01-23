using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

public class GitStatusApp
{
    DynamicConsoleRegion consoleRegion = new()
    {
        SafeDraw = false,
    };
    Stopwatch timer = new();
    bool scanComplete;
    string? globalStatus;
    GitRoot[]? gitRoots;
    ILogger logger = Program.LoggerFactory.GetLogger<GitStatusApp>();


    public GitStatusApp(string[] args)
    {
        ArgsRaw = args;
        var flags = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            string? arg = args[i];

            if (arg.StartsWith("--"))
            {
                if (arg == "--exclude")
                {
                    var next = args[i+1];
                    if (next.StartsWith('-')) throw new InvalidDataException("--exclude must be followed with a path");
                    ArgExclude.AddRange(next.Split(','));
                    i++;
                }
                else if (arg == "--no-fetch")
                {
                    var next = args[i+1];
                    if (next.StartsWith('-')) throw new InvalidDataException("--no-fetch must be followed with a path");
                    ArgNoFetch.AddRange(next.Split(','));
                    i++;
                }
                else
                {
                    ArgAllParams.Add(arg);
                }
                continue;
            }

            if (arg.StartsWith("-"))
            {
                flags.Append(arg[1..]);
                continue;
            }

            ArgPath.Add(arg);
        }
        ArgAllFlags = flags.ToString();

        if (ArgPath.Count == 0)
        {
            ArgPath.Add(Environment.CurrentDirectory);
        }
    }

    public string[] ArgsRaw { get;  }
    public string ArgAllFlags { get; init; }
    public List<string> ArgAllParams { get; } = new();
    public List<string> ArgPath { get; } = new();
    public List<string> ArgExclude { get; } = new();
    public List<string> ArgNoFetch { get; } = new();
    public bool ArgRemote { get; set; }
    public bool ArgPull => ArgAllFlags.Contains('p') || ArgAllParams.Contains("--pull");
    public int ArgMaxDepth { get; } = 8;

    public IReadOnlyList<GitRoot> Roots => gitRoots ?? throw new NullReferenceException("gitRoots. Scan expected first");

    public bool ShouldFetch(GitRoot root)
    {
        if (ArgNoFetch.Contains("*")) return false;

        // TODO: Document this VV
        if (ArgNoFetch.Any(x=>root.PathRelative.EndsWith(x))) return false;
        return true;
    }

    /// <summary>Should not be async - run on main thread</summary>
    public int Run()
    {
        timer.Start();
        logger.Log("Run: Init");
        consoleRegion.Init(3);
        consoleRegion.WriteLine("[git-status] scanning...");

        var process = ScanAndQueryAllRoots();
        var frameRate = TimeSpan.FromSeconds(1 / 30f);
        var resize = false;
        while(!process.IsCompleted)
        {
            if (scanComplete && !resize)
            {
                logger.Log("Run: ReInit/Resize");
                // First draw after scanning resizes the dynamic console region
                consoleRegion.WriteLine($"[git-status] found {Roots.Count}, fetching...");
                consoleRegion.ReInit(Roots.Count);
                resize = true;
            }
            Render();
            Thread.Sleep(frameRate);
        }
        if (process.IsFaulted)
        {
            Console.Error.WriteLine(process.Exception);
            return 1;
        }

        // Final Render
        timer.Stop();
        consoleRegion.AllowOverflow = true;
        Render();

        var firstError = Roots.FirstOrDefault(x=>x.Error != null);
        if (firstError != null)
        {
            if (firstError.Error != null)
            {
                logger.Log(firstError.Error, "FistError");
                Console.Error.WriteLine(firstError.Error);
            }
        }

        return 0;
    }

    private async Task ScanAndQueryAllRoots()
    {
        try
        {
            logger.Log("IN: "+nameof(ScanAndQueryAllRoots));
            globalStatus = "Scanning";
            var scanResult = new ConcurrentBag<GitRoot>();
            await Parallel.ForEachAsync(ArgPath, async (path, ct) =>
            {
                var comp = new GitFolderScanner()
                {
                    Exclude = (path)=>
                    {
                        foreach(var ex in ArgExclude)
                        {
                            if (path.EndsWith(ex))
                            {
                                logger.Log($"Excluding: {path} (because {ex})");
                                return true;
                            }
                        }
                        return false;
                    }
                };
                await comp.Scan(path, ArgMaxDepth);
                foreach(var r in comp.Roots)
                {
                    scanResult.Add(r);
                }
            });
            gitRoots = scanResult.ToArray();
            scanComplete = true;

            globalStatus = "Processing";
            await Task.Run(() =>
            {
                var buckets = GeneralHelper.CollectInBuckets(Roots, Roots.Count / 4);
                Task.WaitAll(buckets.Select(x=>ProcessBucket(this, x)));
            });

            globalStatus = "Completed";
        }
        catch (Exception)
        {
            globalStatus = "Error";
            throw;
        }
        finally
        {
            logger.Log("OUT: "+nameof(ScanAndQueryAllRoots));
        }

        static async Task ProcessBucket(GitStatusApp app, GitRoot[] bucket)
        {
            foreach(var gitRoot in bucket)
            {
                await gitRoot.Process(app);
            }
        }
    }

    static Dictionary<ItemStatus, ConsoleColor> Colors = new()
    {
        {ItemStatus.Discover,   ConsoleColor.DarkBlue},
        {ItemStatus.Checking,   ConsoleColor.DarkCyan},
        {ItemStatus.Ignored,    ConsoleColor.DarkGray},
        {ItemStatus.Clean,      ConsoleColor.DarkGreen},
        {ItemStatus.Dirty,      ConsoleColor.Yellow},
        {ItemStatus.Behind,     ConsoleColor.Cyan},
        {ItemStatus.Pull,       ConsoleColor.Magenta},
    };

    /// <summary>Render to the console</summary>
    /// - General idea to render all items dynamically if there is space
    /// - If there is no space. Show progress, then output results
    private void Render()
    {
        consoleRegion.StartDraw();

        if (gitRoots == null) return;

        var maxPath = Roots.Max(x=>x.PathRelative.Length);
        var sizePath = Math.Min(maxPath, Math.Min(80, consoleRegion.Width / 2));
        int cc = 0;
        foreach(var item in Roots.OrderBy(x=>x.Path))
        {
            /* var path = "./" + item.PathRelative; */
            var path = item.PathRelative;
            var txtPath = StringHelper.ElipseAtStart(path, sizePath, "__").PadRight(sizePath);
            var txtStatusLine =  item.StatusLine();

            consoleRegion.ForegroundColor = Colors[item.Status];
            consoleRegion.Write(item.Status.ToString().PadRight(8));
            consoleRegion.ForegroundColor = consoleRegion.StartFg;
            consoleRegion.Write(" ");
            consoleRegion.Write(txtPath);
            consoleRegion.Write(" ");
            if (item.Status == ItemStatus.Dirty || item.Status == ItemStatus.Behind)
            {
                consoleRegion.ForegroundColor = Colors[item.Status];
            }
            consoleRegion.WriteLine(txtStatusLine, true);
            consoleRegion.ForegroundColor = consoleRegion.StartFg;
            cc++;
            if (!consoleRegion.AllowOverflow && cc >= consoleRegion.Height - 2) break;

        }

        // Status Line
        var donr = Roots.Count(x=>x.IsComplete);
        consoleRegion.WriteLine($"[{globalStatus,9}] Items {donr}/{Roots.Count} in {timer.Elapsed.TotalSeconds:0.0} sec");
    }
}

