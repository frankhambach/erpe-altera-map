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
    // A fixed precision model, so that noding and overlay are reproducible and exact.
    // The topology pipeline works in SVG coordinates, where the source map spans
    // 6128.3 units for 360 degrees of longitude, so this grid is 0.0001 units, about
    // 0.65 m. That is 5000 times finer than the weld tolerance the pipeline relies on,
    // and was chosen over a coarser 0.001 units because the coarser grid moved 52
    // countries' areas by more than 0.1 percent, against one at this scale.
    private const double PrecisionScale = 10000.0;

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
                new PrecisionModel(PrecisionScale),
                currentInstance.DefaultSRID,
                GeometryOverlay.NG,
                currentInstance.CoordinateEqualityComparer);

            services.AddSingleton(NtsGeometryServices.Instance.DefaultCoordinateSequenceFactory);
            services.AddSingleton(NtsGeometryServices.Instance.CreateGeometryFactory());
            services.AddTransient<ISvgApproximationService, SvgApproximationService>();
            services.AddTransient<IProjectionService, WinkelTripelProjectionService>();
            services.AddTransient<ITracerService, TracerService>();
            services.AddTransient<ICoastlineService, CoastlineService>();
            services.AddTransient<ITopologyService, TopologyService>();
            services.AddTransient<ICorrectionService, CorrectionService>();
            services.AddTransient<IFaceAssignmentService, FaceAssignmentService>();
            services.AddTransient<IAssemblyService, AssemblyService>();
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