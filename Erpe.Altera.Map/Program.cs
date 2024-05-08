// <copyright file="Program.cs" company="Frank Hambach">
// Copyright (c) Frank Hambach. All rights reserved.
// </copyright>

namespace Erpe.Altera.Map;

using System.CommandLine;
using System.IO;
using System.Threading.Tasks;

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
            Option<FileInfo> inOption = new Option<FileInfo>("--in", "The SVG file to read country shapes from.");
            Option<FileInfo> outOption = new Option<FileInfo>("--out", "The GeoJson file to write.");

            Command convertCommand = new Command("convert", "Convert an SVG world map to GeoJson.")
                {
                    inOption,
                    outOption,
                };
            convertCommand.SetHandler(
                (inFile, outFile) => new ConvertCommand().HandleAsync(inFile, outFile),
                inOption,
                outOption);

            return await convertCommand.InvokeAsync(args);
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}