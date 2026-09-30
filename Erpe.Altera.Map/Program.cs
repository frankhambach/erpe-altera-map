// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map;

using System.CommandLine;
using System.Globalization;
using System.Runtime.CompilerServices;
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
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
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
            services.AddTransient<ICorrectionService, CorrectionService>();
            services.AddTransient<ISimplificationService, VisvalingamSimplificationService>();
            services.AddTransient<Command, ConvertCommand>();

            ServiceProvider serviceProvider = services.BuildServiceProvider();
            await using ConfiguredAsyncDisposable disposable = serviceProvider.ConfigureAwait(false);

            Command rootCommand = new RootCommand();
            foreach (Command command in serviceProvider.GetServices<Command>())
            {
                rootCommand.AddCommand(command);
            }

            return await rootCommand.InvokeAsync(args).ConfigureAwait(false);
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }
}