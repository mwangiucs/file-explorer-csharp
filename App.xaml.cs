using System;
using System.IO;
using System.Text;
using System.Windows;

namespace FileExplorerCS;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. UI Thread Exceptions
        this.DispatcherUnhandledException += (s, args) =>
        {
            bool recover = PromptAndLogException(args.Exception, "UI Dispatcher Unhandled Exception", canRecover: true);
            if (recover)
            {
                args.Handled = true;
            }
            else
            {
                args.Handled = true;
                Shutdown(1);
            }
        };

        // 2. Background Thread Exceptions
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                PromptAndLogException(ex, "AppDomain Unhandled Exception", canRecover: false);
            }
        };

        // 3. Task / Async Exceptions
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            PromptAndLogException(args.Exception, "TaskScheduler Unobserved Task Exception", canRecover: true);
            args.SetObserved();
        };
    }

    private static bool PromptAndLogException(Exception ex, string context, bool canRecover)
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string logFolder = Path.Combine(appData, "FileExplorerCS", "logs");
            Directory.CreateDirectory(logFolder);
            string logPath = Path.Combine(logFolder, "crash.log");

            StringBuilder sb = new();
            sb.AppendLine("================================================================================");
            sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Context: {context}");
            sb.AppendLine($"OS Version: {Environment.OSVersion}");
            sb.AppendLine($".NET Runtime: {Environment.Version}");
            sb.AppendLine($"Exception: {ex.GetType().FullName}");
            sb.AppendLine($"Message: {ex.Message}");
            sb.AppendLine("Stack Trace:");
            sb.AppendLine(ex.StackTrace);

            Exception? inner = ex.InnerException;
            while (inner != null)
            {
                sb.AppendLine("--------------------------------------------------------------------------------");
                sb.AppendLine($"Inner Exception: {inner.GetType().FullName}");
                sb.AppendLine($"Message: {inner.Message}");
                sb.AppendLine("Stack Trace:");
                sb.AppendLine(inner.StackTrace);
                inner = inner.InnerException;
            }
            sb.AppendLine();

            File.AppendAllText(logPath, sb.ToString());

            string userMessage;
            if (canRecover)
            {
                userMessage = $"An unexpected error occurred.\n\n" +
                              $"Error: {ex.Message}\n\n" +
                              $"A crash report has been saved to:\n" +
                              $"{logPath}\n\n" +
                              $"Do you want to continue using the application?";
                
                var result = System.Windows.MessageBox.Show(
                    userMessage, 
                    "Unexpected Error", 
                    System.Windows.MessageBoxButton.YesNo, 
                    System.Windows.MessageBoxImage.Warning);
                
                return result == System.Windows.MessageBoxResult.Yes;
            }
            else
            {
                userMessage = $"A fatal error occurred and the application must close.\n\n" +
                              $"Error: {ex.Message}\n\n" +
                              $"A crash report has been saved to:\n" +
                              $"{logPath}\n\n" +
                              $"Please restart the application.";
                
                System.Windows.MessageBox.Show(
                    userMessage, 
                    "Application Crash", 
                    System.Windows.MessageBoxButton.OK, 
                    System.Windows.MessageBoxImage.Error);
                
                return false;
            }
        }
        catch
        {
            return false;
        }
    }
}
