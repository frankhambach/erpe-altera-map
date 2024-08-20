// <copyright file="ConvertCommand.cs" company="Frank Hambach">
// Copyright (c) Frank Hambach. All rights reserved.
// </copyright>

namespace Erpe.Altera.Map.Commands;

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Converters;

using Svg;

public class ConvertCommand : Command
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters =
                {
                    new GeoJsonConverterFactory(),
                },
        };

    private readonly IProjectionService projectionService;

    private readonly ISvgApproximationService svgApproximationService;

    private readonly ITopologyService topologyService;

    public ConvertCommand(
        ISvgApproximationService svgApproximationService,
        IProjectionService projectionService,
        ITopologyService topologyService)
        : base("convert", "Convert an SVG world map to GeoJson.")
    {
        this.svgApproximationService = svgApproximationService;
        this.projectionService = projectionService;
        this.topologyService = topologyService;

        Option<FileInfo> inOption = new Option<FileInfo>("--in", "The SVG file to read country shapes from.");
        Option<FileInfo> outOption = new Option<FileInfo>("--out", "The GeoJson file to write.");

        this.AddOption(inOption);
        this.AddOption(outOption);
        this.SetHandler(this.HandleAsync, inOption, outOption);
    }

    private static string? GetName(SvgElement element, SvgElement[] shapesElements)
    {
        string? id = element.ParentsAndSelf.TakeWhile(parent => !shapesElements.Contains(parent))
            .First(
                parent => !string.IsNullOrEmpty(parent.ID)
                    && !parent.ID.StartsWith("_x2A_")
                    && !parent.ID.StartsWith("Islands_")
                    && !parent.ID.StartsWith("Mainland_")
                    && !parent.ID.StartsWith("_x3C_Path_x3E"))
            ?.ID;
        if ((id == null) || id.EndsWith("_x2B_"))
        {
            return null;
        }

        string name = Regex.Replace(id, @"_\d+_$", string.Empty)
            .Replace("_x27_", "'")
            .Replace("_x28_", "(")
            .Replace("_x29_", ")")
            .Replace("_x2A_", "*")
            .Replace("_x2B_", "+")
            .Replace("_", " ");
        int index = name.IndexOf(" (", StringComparison.InvariantCulture);
        if (index > 0)
        {
            name = name[..index];
        }

        return name;
    }

    private Envelope GetEnvelope(SvgDocument document)
    {
        List<Coordinate> rimPoints = document.Descendants()
            .First(element => element.ID == "Rim")
            .Descendants()
            .OfType<SvgPath>()
            .SelectMany(path => this.svgApproximationService.GetCoordinates(path))
            .ToList();
        return new Envelope(
            rimPoints.Min(point => point.X),
            rimPoints.Max(point => point.X),
            rimPoints.Min(point => point.Y),
            rimPoints.Max(point => point.Y));
    }

    private async Task HandleAsync(FileInfo inFile, FileInfo outFile)
    {
        SvgDocument document = SvgDocument.Open<SvgDocument>(inFile.FullName, null);
        Envelope sourceEnvelope = this.GetEnvelope(document);

        SvgElement[] shapesElements = document.Descendants()
            .Where(element => element.ID is "Country_Shapes" or "Substate_Shapes")
            .ToArray();

        Country[] countries = (await Task.WhenAll(
                shapesElements.SelectMany(shapesElement => shapesElement.Descendants())
                    .Where(element => element is SvgPath or SvgRectangle or SvgPolygon)
                    .GroupBy(element => GetName(element, shapesElements))
                    .Where(grouping => grouping.Key != null)
                    .Select(
                        async grouping => await Task.Run(
                            () => new Country(
                                grouping.Key!,
                                this.topologyService.Polygonize(
                                        grouping.SelectMany(
                                                element => this.svgApproximationService.Approximate(element))
                                            .Distinct(new CoordinateSequenceEqualityComparer())
                                            .Select(
                                                coordinateSequence => this.projectionService.Unproject(
                                                    coordinateSequence,
                                                    sourceEnvelope))
                                            .Select(lineString => this.topologyService.Close(lineString))
                                            .Select(linearRing => this.topologyService.Unloop(linearRing))
                                            .ToArray())
                                    .Select((polygon, index) => new Shape($"{grouping.Key}-{index}", polygon)))))))
            .Where(country => country.Shapes.Count > 0)
            .ToArray();

        FeatureCollection featureCollection = new FeatureCollection();
        foreach (Country country in countries)
        {
            Feature feature = new Feature
                {
                    Attributes = new AttributesTable(),
                    Geometry = new MultiPolygon(country.Shapes.Select(shape => shape.Polygon).ToArray()),
                };
            feature.Attributes.Add("id", country.Id);
            featureCollection.Add(feature);
        }

        string convertedOutFileName = Path.GetRandomFileName();
        try
        {
            await File.WriteAllTextAsync(
                convertedOutFileName,
                JsonSerializer.Serialize(featureCollection, JsonSerializerOptions));

            Process? process = Process.Start(
                new ProcessStartInfo
                    {
                        FileName = "mapshaper",
                        Arguments =
                            $"\"{convertedOutFileName}\" -clean allow-empty rewind gap-fill-area=0 snap-interval=0.005 -o \"{outFile.FullName}\" format=geojson",
                        UseShellExecute = true,
                    });
            if (process != null)
            {
                await process.WaitForExitAsync();
            }
        }
        finally
        {
            File.Delete(convertedOutFileName);
        }
    }

    private sealed class CoordinateSequenceEqualityComparer : EqualityComparer<CoordinateSequence>
    {
        public override bool Equals(CoordinateSequence? x, CoordinateSequence? y)
        {
            return ((x == null) && (y == null))
                || ((x != null)
                    && (y != null)
                    && (x.Count == y.Count)
                    && Enumerable.Range(0, x.Count)
                        .All(index => x.GetCoordinate(index).Equals(y.GetCoordinate(index))));
        }

        public override int GetHashCode(CoordinateSequence obj)
        {
            return Enumerable.Range(0, obj.Count)
                .Aggregate(23, (hashCode, index) => HashCode.Combine(hashCode, obj.GetCoordinate(index).GetHashCode()));
        }
    }
}