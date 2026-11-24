using System.Diagnostics;

namespace PCL;

/// <summary>
/// 自更新替换进程：由刚下载的新版本 exe 以
/// <c>update &lt;pid&gt; &lt;旧exe&gt; &lt;新exe&gt; &lt;是否重启&gt;</c>
/// 启动，等待旧进程退出后把自身复制到旧 exe 路径并重新启动。
/// 该逻辑必须在 Avalonia 与单例锁初始化之前执行。
/// </summary>
internal static class UpdateCommand
{
    public static bool TryRun(string[] args)
    {
        if (args.Length < 5
            || !args[0].Equals("update", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!int.TryParse(args[1], out var pid))
            return false;

        var target = args[2];
        var restart = args[4].Equals("true", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(target))
            return true;

        try
        {
            using var process = Process.GetProcessById(pid);
            process.WaitForExit(120_000);
        }
        catch (Exception)
        {
            // 旧进程已经退出或无法打开，继续替换。
        }

        var self = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(self))
            return true;

        var replaced = false;
        Exception? lastError = null;
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var staging = target + ".new";
            try
            {
                File.Copy(self, staging, true);
                File.Move(staging, target, true);
                replaced = true;
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                try
                {
                    if (File.Exists(staging))
                        File.Delete(staging);
                }
                catch (Exception)
                {
                    // 忽略清理失败。
                }

                Thread.Sleep(500);
            }
        }

        if (replaced)
        {
            if (restart)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(target)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(target) ?? string.Empty
                    });
                }
                catch (Exception ex)
                {
                    WriteError(target, ex);
                }
            }
        }
        else
        {
            WriteError(target, lastError ?? new IOException("替换启动器文件失败。"));
        }

        return true;
    }

    private static void WriteError(string target, Exception error)
    {
        try
        {
            File.WriteAllText(
                target + ".update-error.log",
                $"{DateTime.Now:O}{Environment.NewLine}{error}");
        }
        catch (Exception)
        {
            // 忽略日志写入失败。
        }
    }
}
