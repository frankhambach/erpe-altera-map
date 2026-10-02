// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Commands;

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
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

using Serilog;

using Svg;

internal sealed class ConvertCommand : Command
{
    // Correction happens in SVG coordinates, because the defects being corrected are
    // drawing errors: they are uniform in SVG units and vary by roughly a factor of
    // ten with latitude once unprojected. This is the SVG-unit equivalent of the
    // 0.001 degrees used before, and Phase 3 of docs/topology-pipeline.md replaces it
    // with the measured weld tolerance of 0.5 units.
    private const double SnapTolerance = 0.02;

    // How far a loose arc end may reach to find what it was drawn to meet, in SVG units.
    // Chosen by measurement: 0.5 leaves 147 ends loose, 0.75 leaves 44 and 1.0 leaves 7.
    // Going further does not pay - at 1.5 the two ends it recovers cost 30 further endpoint
    // merges and 25 fewer splits, meaning ends that should attach partway along an arc are
    // being merged with another endpoint instead. It stays below the drawing's own
    // resolution of about 1.75 units between consecutive points.
    private const double WeldTolerance = 1.0;

    private static readonly JsonSerializerOptions JsonSerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters =
                {
                    new GeoJsonConverterFactory(),
                },
        };

    private readonly ICoastlineService coastlineService;

    private readonly ICorrectionService correctionService;

    private readonly GeometryFactory geometryFactory;

    private readonly IProjectionService projectionService;

    private readonly ISimplificationService simplificationService;

    private readonly ISvgApproximationService svgApproximationService;

    private readonly ITopologyService topologyService;

    private readonly ITracerService tracerService;

    public ConvertCommand(
        ISvgApproximationService svgApproximationService,
        IProjectionService projectionService,
        ITracerService tracerService,
        ICoastlineService coastlineService,
        ITopologyService topologyService,
        ICorrectionService correctionService,
        ISimplificationService simplificationService,
        GeometryFactory geometryFactory)
        : base("convert", "Convert an SVG world map to GeoJson.")
    {
        this.svgApproximationService = svgApproximationService;
        this.projectionService = projectionService;
        this.tracerService = tracerService;
        this.coastlineService = coastlineService;
        this.topologyService = topologyService;
        this.correctionService = correctionService;
        this.simplificationService = simplificationService;
        this.geometryFactory = geometryFactory;

        Option<FileInfo> inOption = new("--in", "The SVG file to read country shapes from.");
        Option<FileInfo> outOption = new("--out", "The GeoJson file to write.");

        this.AddOption(inOption);
        this.AddOption(outOption);
        this.SetHandler(this.HandleAsync, inOption, outOption);
    }

    private static (string Name, CountryType Type)? GetName(SvgElement element, SvgElement[] shapesElements)
    {
        SvgElement? nameElement = element.ParentsAndSelf.TakeWhile(parent => !shapesElements.Contains(parent))
            .FirstOrDefault(parent => !string.IsNullOrEmpty(parent.ID)
                && !parent.ID.StartsWith("_x2A_", StringComparison.Ordinal)
                && !parent.ID.Equals("Islands", StringComparison.Ordinal)
                && !parent.ID.StartsWith("Islands_", StringComparison.Ordinal)
                && !parent.ID.Equals("Mainland", StringComparison.Ordinal)
                && !parent.ID.StartsWith("Mainland_", StringComparison.Ordinal)
                && !parent.ID.StartsWith("_x3C_Path_x3E", StringComparison.Ordinal)
                && !Regex.IsMatch(parent.ID, @"^(path|g)\d+$", RegexOptions.None, TimeSpan.FromSeconds(1.0)));
        if (nameElement == null)
        {
#pragma warning disable CA1303
            Console.WriteLine("No name element found for element with ID '{0}' {1}", element.ID, element.GetXML());
#pragma warning restore CA1303
            return null;
        }

        string? id = nameElement.ID;
        if (nameElement.TryGetAttribute("data-name", out string? dataName))
        {
            id = dataName;
        }

        if (id == null)
        {
            return null;
        }

        string name = Regex.Replace(id, @"[-_]\d+_?$", string.Empty)
            .Replace("_x27_", "'", StringComparison.Ordinal)
            .Replace("_x28_", "(", StringComparison.Ordinal)
            .Replace("_x29_", ")", StringComparison.Ordinal)
            .Replace("_x2A_", "*", StringComparison.Ordinal)
            .Replace("_x2B_", "+", StringComparison.Ordinal)
            .Replace("_", " ", StringComparison.Ordinal);
        int index = name.IndexOf(" (", StringComparison.InvariantCulture);
        if (index > 0)
        {
            name = name[..index];
        }

        index = name.IndexOf(" - ", StringComparison.InvariantCulture);
        if (index > 0)
        {
            name = name[..index];
        }

        if (name == $"USA")
        {
            name = "America+";
        }

        if (name.EndsWith('+'))
        {
            return (name[..^1], CountryType.FederatedState);
        }

        SvgElement? shapesElement = nameElement.ParentsAndSelf.FirstOrDefault(shapesElements.Contains);
        CountryType type = shapesElement?.ID switch
            {
                "Country_Shapes" => CountryType.UnitaryState,
                "Substate_Shapes" => CountryType.FederalState,
                _ => throw new InvalidOperationException($"Unexpected shapes element ID '{shapesElement?.ID}'."),
            };

        return (name, type);
    }

    private static FileInfo GetOutFile(FileInfo outFile, string suffix = "-no-substates")
    {
        return new FileInfo(
            outFile.DirectoryName
            + Path.DirectorySeparatorChar
            + Path.GetFileNameWithoutExtension(outFile.Name)
            + suffix
            + outFile.Extension);
    }

    private async Task WriteGeoJsonAsync(
        IEnumerable<Country> countries,
        FileInfo outFile,
        double? simplificationTolerance = null)
    {
        FeatureCollection featureCollection = new FeatureCollection();
        featureCollection.Clear();
        foreach (Country country in countries)
        {
            Feature feature = new Feature
                {
                    Attributes = new AttributesTable(),
                    Geometry = new MultiPolygon(
                        country.Shapes.Select(shape => shape.Polygon)
                            .Select(polygon =>
                                simplificationTolerance == null
                                    ? polygon
                                    : this.simplificationService.Simplify(polygon, simplificationTolerance.Value))
                            .ToArray()),
                };
            feature.Geometry.Apply(new RoundCoordinateSequenceFilter());

            feature.Attributes.Add("id", country.Id);
            featureCollection.Add(feature);
        }

        await File.WriteAllTextAsync(
                outFile.FullName,
                JsonSerializer.Serialize(featureCollection, JsonSerializerOptions))
            .ConfigureAwait(false);
    }

    // Unprojects every shape in place, turning SVG coordinates into longitude and
    // latitude. Memoizing on the SVG coordinate is what makes borders match: two
    // shapes that share a vertex in SVG space are guaranteed the same output value,
    // because Unproject is only ever evaluated once per distinct input.
    private void Unproject(IEnumerable<Country> countries, Envelope sourceEnvelope)
    {
        Dictionary<Coordinate, Coordinate> unprojected = new Dictionary<Coordinate, Coordinate>();
        foreach (Shape shape in countries.SelectMany(country => country.Shapes))
        {
            shape.Polygon = this.geometryFactory.CreatePolygon(
                this.Unproject(shape.Polygon.Shell, sourceEnvelope, unprojected),
                shape.Polygon.Holes.Select(hole => this.Unproject(hole, sourceEnvelope, unprojected)).ToArray());
        }
    }

    private LinearRing Unproject(
        LinearRing linearRing,
        Envelope sourceEnvelope,
        IDictionary<Coordinate, Coordinate> unprojected)
    {
        return this.geometryFactory.CreateLinearRing(
            linearRing.CoordinateSequence.ToCoordinateArray()
                .Select(coordinate => this.Unproject(coordinate, sourceEnvelope, unprojected))
                .ToArray());
    }

    private Coordinate Unproject(
        Coordinate coordinate,
        Envelope sourceEnvelope,
        IDictionary<Coordinate, Coordinate> unprojected)
    {
        if (!unprojected.TryGetValue(coordinate, out Coordinate? result))
        {
            result = this.projectionService.Unproject(this.projectionService.Rescale(coordinate, sourceEnvelope));
            unprojected.Add(coordinate, result);
        }

        return result.Copy();
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
        SvgDocument document = SvgDocument.Open<SvgDocument>(inFile.FullName);
        Envelope sourceEnvelope = this.GetEnvelope(document);

        // The arc graph is not yet consumed; shapes still drive the output. Phases 3 to 6
        // of docs/topology-pipeline.md build the partition on top of it.
        ArcGraph arcGraph = new ArcGraph(this.geometryFactory.CoordinateSequenceFactory);
        this.tracerService.AddArcs(document, arcGraph);
        Log.Information(
            "Read {ArcCount} tracer arcs ({BorderCount} border, {SubstateCount} substate) on {NodeCount} nodes.",
            arcGraph.Arcs.Count,
            arcGraph.Arcs.Count(arc => arc.Kind == ArcKind.Border),
            arcGraph.Arcs.Count(arc => arc.Kind == ArcKind.Substate),
            arcGraph.Nodes.Count);

        SvgElement[] shapesElements = document.Descendants()
            .Where(element => element.ID is "Country_Shapes" or "Substate_Shapes")
            .ToArray();

        (string Name, CountryType Type, CoordinateSequence[] CoordinateSequences)[] groupings = (await Task.WhenAll(
                shapesElements.SelectMany(shapesElement => shapesElement.Descendants())
                    .Where(element => element is SvgPath or SvgLine or SvgRectangle or SvgPolygon)
                    .GroupBy(element => GetName(element, shapesElements))
                    .Where(grouping => grouping.Key != null)
                    .Select(async grouping => (grouping.Key!.Value.Name, grouping.Key!.Value.Type,
                        Lines: await Task.Run(() =>
                                grouping.SelectMany(element => this.svgApproximationService.Approximate(element))

                                    // A ring needs at least three distinct points to enclose an area.
                                    .Where(coordinateSequence => coordinateSequence.Count >= 3)
                                    .Distinct(new CoordinateSequenceEqualityComparer())
                                    .ToArray())
                            .ConfigureAwait(false))))
            .ConfigureAwait(false)).ToArray();

        this.correctionService.Snap(
            groupings.SelectMany(grouping => grouping.CoordinateSequences)
                .SelectMany(coordinateSequence => coordinateSequence.ToCoordinateArray()),
            SnapTolerance);

        Country[] countries = groupings
            .Select(grouping => new Country(
                grouping.Name,
                grouping.Type,
                this.topologyService.Polygonize(
                        grouping.CoordinateSequences
                            .Select(coordinateSequence => this.topologyService.Close(coordinateSequence))
                            .Select(linearRing => this.topologyService.Unloop(linearRing))
                            .ToArray())
                    .Where(polygon => !polygon.IsEmpty)
                    .Select((polygon, index) => new Shape($"{grouping.Name}-{index}", polygon))))
            .ToArray();

        this.coastlineService.AddArcs(
            countries.SelectMany(country => country.Shapes).Select(shape => shape.Polygon),
            arcGraph);

        this.correctionService.Weld(arcGraph, WeldTolerance);

        this.topologyService.BuildFaces(arcGraph);

        this.Unproject(countries, sourceEnvelope);

        await this.WriteGeoJsonAsync(
                countries.Where(country => country.Type is CountryType.UnitaryState or CountryType.FederalState),
                outFile)
            .ConfigureAwait(false);
        await this.WriteGeoJsonAsync(
                countries.Where(country => country.Type is CountryType.UnitaryState or CountryType.FederalState),
                GetOutFile(outFile, "-low"),
                0.01)
            .ConfigureAwait(false);
        await this.WriteGeoJsonAsync(
                countries.Where(country => country.Type is CountryType.UnitaryState or CountryType.FederalState),
                GetOutFile(outFile, "-medium"),
                0.001)
            .ConfigureAwait(false);
        await this.WriteGeoJsonAsync(
                countries.Where(country => country.Type is CountryType.UnitaryState or CountryType.FederatedState),
                GetOutFile(outFile, "-no-substates"))
            .ConfigureAwait(false);
        await this.WriteGeoJsonAsync(
                countries.Where(country => country.Type is CountryType.FederalState),
                GetOutFile(outFile, "-only-substates"))
            .ConfigureAwait(false);
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

    private sealed class RoundCoordinateSequenceFilter : ICoordinateSequenceFilter
    {
        public bool Done => false;

        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence seq, int i)
        {
            Coordinate coordinate = seq.GetCoordinate(i);
            coordinate.X = Math.Round(coordinate.X, 4);
            coordinate.Y = Math.Round(coordinate.Y, 4);
            seq.SetOrdinate(i, Ordinate.X, coordinate.X);
            seq.SetOrdinate(i, Ordinate.Y, coordinate.Y);
        }
    }
}