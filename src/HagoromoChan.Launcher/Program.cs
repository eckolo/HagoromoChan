using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using HagoromoChan.Contracts;

namespace HagoromoChan.Launcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var root = RuntimeControl.DataDirectory();
            if (args.Length == 1 && args[0] is "--stop" or "--restart" or "--restart-desktop" or "--restart-tunnel")
            {
                var command = args[0] switch
                {
                    "--stop" => RuntimeCommand.Stop,
                    "--restart" => RuntimeCommand.Restart,
                    "--restart-desktop" => RuntimeCommand.RestartDesktop,
                    _ => RuntimeCommand.RestartTunnel
                };
                return RuntimeControl.SendAsync(RuntimeControl.PipeName(root), command).GetAwaiter().GetResult() ? 0 : 2;
            }
            if (args.Length != 0) return 2;
            Directory.CreateDirectory(root);
            FileStream deploymentGuard;
            try
            {
                deploymentGuard = new FileStream(Path.Combine(root, "deployment.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
            {
                throw new StartupException("更新・配置中です。処理の完了後に起動してください。");
            }
            using var guard = deploymentGuard;
            FileStream instance;
            try
            {
                instance = new FileStream(Path.Combine(root, RuntimeControl.LockFile),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
            {
                // Already running. A second ordinary launch is a no-op, not a restart.
                return 0;
            }

            using (instance)
            {
                // Lock order: deployment, then launcher. The updater holds the
                // deployment lock while requesting shutdown and switching app trees.
                guard.Dispose();
                using var supervisor = new Supervisor(root, AppContext.BaseDirectory);
                supervisor.RunAsync().GetAwaiter().GetResult();
            }
            return 0;
        }
        catch (Exception error)
        {
            // Never include command lines, environment values or arbitrary transport responses.
            if (args.Length != 0) return 3;
            System.Windows.Forms.MessageBox.Show(
                "羽衣ちゃんを起動できません。起動・移行手順と logs/launcher.log を確認してください。\n"
                + (error is StartupException ? error.Message : error.GetType().Name),
                "羽衣ちゃん", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
            return 1;
        }
    }
}

internal sealed class StartupException(string message) : Exception(message);
