// <copyright file="Program.cs" company="Frank Hambach">
// Copyright (c) Frank Hambach. All rights reserved.
// </copyright>

namespace Erpe.Altera.Map;

using System.CommandLine;
using System.Threading.Tasks;

using Erpe.Altera.Map.Commands;
using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Services;

using Microsoft.Extensions.DependencyInjection;

using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Implementation;

using Serilog;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.Debug()
            .CreateLogger();

        try
        {
            ServiceCollection services = [];

            NtsGeometryServices currentInstance = NtsGeometryServices.Instance;
            NtsGeometryServices.Instance = new NtsGeometryServices(
                CoordinateArraySequenceFactory.Instance,
                currentInstance.DefaultPrecisionModel,
                currentInstance.DefaultSRID,
                GeometryOverlay.NG,
                currentInstance.CoordinateEqualityComparer);

            services.AddSingleton(NtsGeometryServices.Instance.DefaultCoordinateSequenceFactory);
            services.AddSingleton(NtsGeometryServices.Instance.CreateGeometryFactory());
            services.AddTransient<ISvgApproximationService, SvgApproximationService>();
            services.AddTransient<IProjectionService, WinkelTripelProjectionService>();
            services.AddTransient<ITopologyService, TopologyService>();
            services.AddTransient<Command, ConvertCommand>();

            await using ServiceProvider serviceProvider = services.BuildServiceProvider();

            Command rootCommand = new RootCommand();
            foreach (Command command in serviceProvider.GetServices<Command>())
            {
                rootCommand.AddCommand(command);
            }

            return await rootCommand.InvokeAsync(args);
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}