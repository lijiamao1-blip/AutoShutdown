namespace AutoShutdown.TestTreeHelper;

/// <summary>
/// S19-D1 测试专属辅助进程。parent 模式派生一个 child 子进程后挂起；child 模式直接挂起。
/// 仅作为整树超时清理回归的「父子进程树」目标，绝不进入生产命令白名单默认配置，绝不执行真实电源。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "child";

        if (mode == "parent")
        {
            var childPath = Environment.ProcessPath!;
            var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = childPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "child" }
            });
            if (child is not null)
            {
                // 挂起前向父进程 stdout 打印子进程 PID，供测试断言「子进程确已派生」。
                Console.WriteLine("child-pid:" + child.Id);
                Console.Out.Flush();
            }
        }

        HangForever();
        return 0;
    }

    private static void HangForever()
    {
        while (true)
        {
            Thread.Sleep(1000);
        }
    }
}
