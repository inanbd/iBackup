using iBackup.Client.Core;
using iBackup.Client.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

var dataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iBackup");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(dataDirectory, "logs", "service-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true)
    .CreateLogger();

try
{
    // Install with:
    //   sc create iBackup binPath="C:\path\to\iBackup.Service.exe" start=auto
    // The service resumes the session persisted by the desktop app (DPAPI-protected),
    // so sign in once with the app before enabling the service.
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog();
    builder.Services.AddBackupClientCore(options => options.DataDirectory = dataDirectory);
    builder.Services.AddHostedService<BackupWorker>();
    builder.Services.AddWindowsService(options => options.ServiceName = "iBackup");

    await builder.Build().RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "iBackup service terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
