using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using LockScreenDemo.Service;

var builder = Host.CreateApplicationBuilder(args);

// Configures the app to run as a Windows Service
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "LockScreenDemoService";
});

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
