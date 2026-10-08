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
    // Six, not four. Four decimal degrees is about 0.0017 SVG units, some seventeen times
    // coarser than the grid the graph is noded on, so distinct nodes would land on top of
    // each other and leave degenerate rings behind. The file grows; the low and medium
    // outputs are where size is managed.
    private const int OutputDecimals = 6;

    // How close two tracers have to run, in SVG units, before one of them is taken for a
    // copy of the other. Chosen by measurement: of the 131,850 units of tracer on the
    // sample map, 1490.8 lie within 0.01 units of another tracer and those are the
    // duplicates. The answer is the same anywhere between 0.10 and 0.50, and two genuinely
    // distinct borders 0.25 units apart would be a corridor a seventh as wide as the gap
    // between consecutive drawn points.
    private const double DuplicateTolerance = 0.25;

    // How many points to leave an arc that closes on itself, whatever the tolerance says.
    // An island is one closed arc, and simplifying one down to a triangle loses it; this is
    // the floor the ring-based simplifier used before, kept so that the coarse outputs lose
    // no more of the small islands than they did.
    private const int ClosedArcMinimumPoints = 6;

    private const double LowTolerance = 0.01;

    private const double MediumTolerance = 0.001;

    private const double SnapTolerance = 0.02;

    // How far two loose arc ends may be apart and still be taken for the same point, in SVG
    // units. Merging is the riskier of the two welds: it moves both ends, to the middle of
    // the group, and it chains transitively through union-find, so one generous tolerance
    // can gather up a whole group of ends that were never meant to meet. Merging also runs
    // first and consumes the ends it resolves, so the lower this is, the more of the work
    // falls to splitting, which is both commoner and safer.
    //
    // Measured by running the map at 1.0, 0.75, 0.5 and 0.25 with splitting at 3.0, and
    // counting the countries that come out with any geometry at all: 1201, 1205, 1209,
    // 1206. The 39 merges given up between 1.0 and 0.5 were wrong ones - ends that should
    // have attached partway along an arc, merged with another endpoint instead, which is
    // the failure the old comment here predicted at 1.5 and which was already happening at
    // 1.0. Below 0.5 it turns: at 0.25 three pairs that genuinely meet can no longer find
    // each other, and the enclosed land area rises by 2430 units, which is a boundary
    // closing where it should not. So this one is a peak rather than a plateau.
    private const double MergeTolerance = 0.5;

    // How far a loose arc end may reach to find the arc it was drawn to meet partway along,
    // in SVG units. The dominant case by better than ten to one, and the safer operation:
    // the end is threaded into that arc as a node and no existing vertex moves, so a wrong
    // split costs a kink in one arc and nothing else.
    //
    // It can therefore afford to reach much further than MergeTolerance, and the data says
    // exactly how far. Of the eight ends left loose at 1.0, seven have their nearest arc
    // between 1.0 and 2.79 units away and the eighth at 12.48, so anything between 2.8 and
    // 12.4 resolves the same seven and leaves the same one. Confirmed by running it: 3.0
    // and 5.0 produce identical output down to the face count. The remaining one is a
    // drawing error to be fixed in the map rather than reached for.
    private const double SplitTolerance = 3.0;

    private static readonly JsonSerializerOptions JsonSerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters =
                {
                    new GeoJsonConverterFactory(),
                },
        };

    private readonly IAssemblyService assemblyService;

    private readonly ICoastlineService coastlineService;

    private readonly ICorrectionService correctionService;

    private readonly IDiagnosticsService diagnosticsService;

    private readonly IFaceAssignmentService faceAssignmentService;

    private readonly GeometryFactory geometryFactory;

    private readonly IProjectionService projectionService;

    private readonly ISimplificationService simplificationService;

    private readonly ISvgApproximationService svgApproximationService;

    private readonly ITopologyService topologyService;

    private readonly ITracerService tracerService;

    private readonly ITopoJsonService topoJsonService;

    private readonly IVerificationService verificationService;

    public ConvertCommand(
        ISvgApproximationService svgApproximationService,
        IProjectionService projectionService,
        ITracerService tracerService,
        ICoastlineService coastlineService,
        IAssemblyService assemblyService,
        ITopologyService topologyService,
        ICorrectionService correctionService,
        IFaceAssignmentService faceAssignmentService,
        ISimplificationService simplificationService,
        IDiagnosticsService diagnosticsService,
        ITopoJsonService topoJsonService,
        IVerificationService verificationService,
        GeometryFactory geometryFactory)
        : base("convert", "Convert an SVG world map to GeoJson.")
    {
        this.svgApproximationService = svgApproximationService;
        this.projectionService = projectionService;
        this.tracerService = tracerService;
        this.coastlineService = coastlineService;
        this.assemblyService = assemblyService;
        this.topologyService = topologyService;
        this.correctionService = correctionService;
        this.faceAssignmentService = faceAssignmentService;
        this.simplificationService = simplificationService;
        this.diagnosticsService = diagnosticsService;
        this.topoJsonService = topoJsonService;
        this.verificationService = verificationService;
        this.geometryFactory = geometryFactory;

        Option<FileInfo> inOption = new("--in", "The SVG file to read country shapes from.");
        Option<FileInfo> outOption = new("--out", "The GeoJson file to write.");
        Option<bool> debugOption = new(
            "--debug-svg",
            "Write the coastline, the welded borders, the faces and the dropped faces beside the output as SVG.");

        this.AddOption(inOption);
        this.AddOption(outOption);
        this.AddOption(debugOption);
        this.SetHandler(this.HandleAsync, inOption, outOption, debugOption);
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

    // The debug files sit beside the output and carry the same stem, so a run's stages stay
    // together and a second run with another output name does not overwrite them.
    private static FileInfo GetDebugFile(FileInfo outFile, string suffix)
    {
        return new FileInfo(
            outFile.DirectoryName
            + Path.DirectorySeparatorChar
            + Path.GetFileNameWithoutExtension(outFile.Name)
            + suffix
            + ".svg");
    }

    // Beside the GeoJson and named after it, with the extension consumers expect.
    private static FileInfo GetTopoJsonFile(FileInfo outFile, string suffix)
    {
        return new FileInfo(
            outFile.DirectoryName
            + Path.DirectorySeparatorChar
            + Path.GetFileNameWithoutExtension(outFile.Name)
            + suffix
            + ".topojson");
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

    // The document's own view box, used verbatim so that a debug file lands on exactly the
    // coordinates the source map uses and keeps the whitespace around the rim.
    private static Envelope GetView(SvgDocument document)
    {
        SvgViewBox viewBox = document.ViewBox;

        return new Envelope(
            viewBox.MinX,
            viewBox.MinX + viewBox.Width,
            viewBox.MinY,
            viewBox.MinY + viewBox.Height);
    }

    private static async Task WriteGeoJsonAsync(IEnumerable<Country> countries, FileInfo outFile)
    {
        FeatureCollection featureCollection = new FeatureCollection();
        featureCollection.Clear();
        foreach (Country country in countries)
        {
            Feature feature = new Feature
                {
                    Attributes = new AttributesTable(),
                    Geometry = new MultiPolygon(country.Shapes.Select(shape => shape.Polygon).ToArray()),
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

    private static int CountRings(IEnumerable<Country> countries)
    {
        return countries.SelectMany(country => country.Shapes)
            .Sum(shape => 1 + shape.Polygon.NumInteriorRings);
    }

    // Every stretch of boundary that is allowed to have only one side to it: the sea coast
    // and the lake shores, and the boundary of each face 6a had to leave out, since the
    // hole a dropped face leaves is bounded from the outside only.
    private static Coordinate[][] GetOneSided(
        Dictionary<Arc, Coordinate[]> arcs,
        HashSet<(double X, double Y)>? removed,
        IEnumerable<Face> dropped)
    {
        HashSet<Arc> oneSided =
        [
            .. arcs.Keys.Where(arc => arc.Kind is ArcKind.Coast or ArcKind.Lake),
            .. dropped.SelectMany(face => face.Holes.Prepend(face.Shell))
                .SelectMany(ring => ring)
                .Select(directed => directed.Arc),
        ];

        return
        [
            .. oneSided.Where(arcs.ContainsKey)
                .Select(arc => removed == null
                    ? arcs[arc]
                    : arcs[arc].Where(coordinate => !removed.Contains((coordinate.X, coordinate.Y))).ToArray()),
        ];
    }

    // The coordinates simplification takes out, decided arc by arc and collected into one
    // set. Dropping by coordinate rather than by position is what carries the decision to
    // both sides of a border: each side holds the same coordinates, so each side loses the
    // same ones.
    private HashSet<(double X, double Y)> SelectRemoved(Dictionary<Arc, Coordinate[]> arcs, double tolerance)
    {
        HashSet<(double X, double Y)> removed = [];
        foreach ((Arc arc, Coordinate[] line) in arcs)
        {
            foreach (Coordinate coordinate in this.simplificationService.SelectRemoved(
                         line,
                         tolerance,
                         arc.IsClosed ? ClosedArcMinimumPoints : 2))
            {
                removed.Add((coordinate.X, coordinate.Y));
            }
        }

        // A node is where arcs meet, and the arc it belongs to never offers one up. Another
        // arc may still carry the same coordinate somewhere in its interior though, and
        // dropping it there would take the junction with it.
        foreach (Coordinate[] line in arcs.Values)
        {
            removed.Remove((line[0].X, line[0].Y));
            removed.Remove((line[^1].X, line[^1].Y));
        }

        return removed;
    }

    private Country[] Filter(IEnumerable<Country> countries, HashSet<(double X, double Y)> removed)
    {
        return
        [
            .. countries.Select(
                country => new Country(
                    country.Id,
                    country.Type,
                    country.Shapes
                        .Select(shape => new Shape(shape.Id, this.Filter(shape.Polygon, removed), shape.Rings))
                        .Where(shape => !shape.Polygon.IsEmpty))),
        ];
    }

    private Polygon Filter(Polygon polygon, HashSet<(double X, double Y)> removed)
    {
        LinearRing? shell = this.Filter(polygon.Shell, removed);

        return shell == null
            ? this.geometryFactory.CreatePolygon()
            : this.geometryFactory.CreatePolygon(
                shell,
                [.. polygon.Holes.Select(hole => this.Filter(hole, removed)).OfType<LinearRing>()]);
    }

    // A ring that comes out of this with too little left to close is left out rather than
    // kept unsimplified: keeping it would be the one thing that breaks sharing, since its
    // neighbour would have dropped those coordinates and it would not.
    private LinearRing? Filter(LinearRing ring, HashSet<(double X, double Y)> removed)
    {
        Coordinate[] coordinates = ring.CoordinateSequence.ToCoordinateArray();
        List<Coordinate> kept = new List<Coordinate>();
        for (int i = 0; i < (coordinates.Length - 1); ++i)
        {
            if (!removed.Contains((coordinates[i].X, coordinates[i].Y)))
            {
                kept.Add(coordinates[i]);
            }
        }

        if (kept.Count < 3)
        {
            return null;
        }

        kept.Add(kept[0]);

        return this.geometryFactory.CreateLinearRing(kept.ToArray());
    }

    // Unprojects every shape in place, turning SVG coordinates into longitude and
    // latitude. Memoizing on the SVG coordinate is what makes borders match: two
    // shapes that share a vertex in SVG space are guaranteed the same output value,
    // because Unproject is only ever evaluated once per distinct input.
    private void Unproject(
        IEnumerable<Country> countries,
        Envelope sourceEnvelope,
        Dictionary<Coordinate, Coordinate> unprojected)
    {
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

    // Every drawn element of the rim, not only the paths: the two long curves are paths but the
    // top and bottom edges are lines, and a frame missing its top and bottom is worse than none.
    private CoordinateSequence[] GetRim(SvgDocument document)
    {
        return
        [
            .. document.Descendants()
                .First(element => element.ID == "Rim")
                .Descendants()
                .Where(element => element is SvgPath or SvgLine or SvgRectangle or SvgPolygon)
                .SelectMany(element => this.svgApproximationService.Approximate(element)),
        ];
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

    private async Task HandleAsync(FileInfo inFile, FileInfo outFile, bool debugSvg)
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

        // Before the coast is added and before welding: the duplicates are a defect of the
        // drawn tracer layers, which is also the only place they can arise, since the coast
        // comes out of a union and cannot repeat itself. Doing it first means the loose ends
        // left by the cut are resolved by the welding that follows rather than by a second
        // mechanism of its own.
        this.correctionService.Collapse(arcGraph, DuplicateTolerance);

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

        double enclosedArea = this.coastlineService.AddArcs(
            countries.SelectMany(country => country.Shapes).Select(shape => shape.Polygon),
            arcGraph);

        this.correctionService.Weld(arcGraph, MergeTolerance, SplitTolerance);

        // One file per stage rather than one per kind, and both of them framed on the source
        // document's own view box so they open on top of the map and on each other.
        //
        // Two stages, because the identifiers do not survive the one in between. 4a is the first
        // thing BuildFaces does and it splits arcs at their crossings, minting new identifiers as
        // it goes: on this map the highest goes from 11,125 to 11,717, so every arc the reports
        // after it name - the bridges of 4d above all - is absent from a file written before it.
        // The five bridges 4d reports are 11204, 11271, 11273, 11395 and 11421, and not one of
        // them could ever have been found in the file that used to be the only one written.
        CoordinateSequence[] rim = debugSvg ? this.GetRim(document) : [];
        Envelope? view = debugSvg ? GetView(document) : null;
        if (debugSvg)
        {
            await this.diagnosticsService.WriteArcsAsync(
                    arcGraph.Arcs,
                    rim,
                    view,
                    GetDebugFile(outFile, "-arcs-before"))
                .ConfigureAwait(false);
        }

        this.topologyService.BuildFaces(arcGraph, enclosedArea, MergeTolerance);

        if (debugSvg)
        {
            await this.diagnosticsService.WriteArcsAsync(
                    arcGraph.Arcs,
                    rim,
                    view,
                    GetDebugFile(outFile, "-arcs-after"))
                .ConfigureAwait(false);
        }

        // One pass per layer, because a substate shape lies inside its country's shape and
        // passing both at once would report every substate face as belonging to two
        // countries. The national pass is assembled before the substate pass runs, since
        // assigning a face sets the one owner it carries.
        Country[] national = countries
            .Where(country => country.Type is CountryType.UnitaryState or CountryType.FederatedState)
            .ToArray();
        Country[] substates = countries.Where(country => country.Type is CountryType.FederalState).ToArray();

        this.faceAssignmentService.Assign(arcGraph, national, true);
        IReadOnlyList<Face> droppedNational = this.assemblyService.Assemble(arcGraph, national);
        IReadOnlyList<Face> dropped = droppedNational;

        if (debugSvg)
        {
            // Between the two passes, because a face inside a substate belongs to a country
            // and to a substate both and the second pass overwrites the owner with the
            // substate. This is the only point at which the faces carry the national owner.
            await this.diagnosticsService.WriteFacesAsync(arcGraph.Faces, view, GetDebugFile(outFile, "-faces"))
                .ConfigureAwait(false);
        }

        this.faceAssignmentService.Assign(arcGraph, substates, false);
        IReadOnlyList<Face> substateDropped = this.assemblyService.Assemble(arcGraph, substates);

        if (debugSvg)
        {
            await this.diagnosticsService.WriteFacesAsync(
                    dropped.Union(substateDropped),
                    view,
                    GetDebugFile(outFile, "-dropped"))
                .ConfigureAwait(false);
        }

        Dictionary<Coordinate, Coordinate> unprojected = new Dictionary<Coordinate, Coordinate>();
        this.Unproject(countries, sourceEnvelope, unprojected);

        // The arcs in the coordinates the output is written in. Simplification is measured
        // in the units of the tolerances, which are degrees, and taking the arcs through
        // the same memo as the shapes means an arc coordinate and the shape coordinate it
        // became are literally the same value.
        Dictionary<Arc, Coordinate[]> arcs = arcGraph.Arcs.ToDictionary(
            arc => arc,
            arc => arc.GetFullCoordinates()
                .Select(coordinate => this.Unproject(coordinate, sourceEnvelope, unprojected))
                .ToArray());

        Country[] written = countries
            .Where(country => country.Type is CountryType.UnitaryState or CountryType.FederalState)
            .ToArray();

        // After unprojecting and before writing, so that what is checked is as close to
        // what is written as it can be. Rounding to the output decimals happens as the file
        // is written and can only bring two coordinates together, never separate them, so
        // sharing here is sharing in the file.
        //
        // One pass per layer, because each is a partition in its own right. The two
        // together are not: a border between two countries is also the edge of a substate,
        // so segments would legitimately come up three times.
        this.verificationService.VerifySharing(national, GetOneSided(arcs, null, droppedNational));
        this.verificationService.VerifySharing(substates, null);

        await WriteGeoJsonAsync(written, outFile).ConfigureAwait(false);
        await WriteGeoJsonAsync(national, GetOutFile(outFile, "-no-substates")).ConfigureAwait(false);
        await WriteGeoJsonAsync(substates, GetOutFile(outFile, "-only-substates")).ConfigureAwait(false);

        // One topology rather than the three files the GeoJson needs, because the two layers go
        // into it as two objects over one set of arcs. That is the whole point of the format: a
        // national border is also the edge of a substate, and here it is one arc with two
        // references instead of the same line written twice. It is also why out.json carries
        // segments used three times and this does not.
        await this.topoJsonService.WriteAsync(
                [("countries", national), ("substates", substates)],
                arcs,
                GetTopoJsonFile(outFile, string.Empty))
            .ConfigureAwait(false);

        // The coarse outputs are simplified by arc and not by polygon: which vertices go is
        // decided once for each arc, and then those coordinates are dropped wherever they
        // appear. Simplifying each polygon on its own, as this did before, gives the two
        // countries either side of a border different geometry and undoes the one guarantee
        // the whole pipeline is built for.
        foreach ((string Suffix, double Tolerance) output in new[]
                     {
                         (Suffix: "-medium", Tolerance: MediumTolerance),
                         (Suffix: "-low", Tolerance: LowTolerance),
                     })
        {
            HashSet<(double X, double Y)> removed = this.SelectRemoved(arcs, output.Tolerance);
            Log.Information(
                "Simplifying at {Tolerance} removes {Removed} of the {Total} distinct coordinates in the arcs.",
                output.Tolerance,
                removed.Count,
                arcs.Values.Sum(line => line.Length - 1));

            // Checked on the national layer rather than on what is written, because that is
            // the layer covering the land and so the only one with a coast to be compared
            // against. The coordinates dropped are the same for either set.
            Country[] simplified = this.Filter(national, removed);
            Log.Information(
                "Simplifying leaves {Rings} of the {Total} rings of the national layer standing.",
                CountRings(simplified),
                CountRings(national));
            this.verificationService.VerifySharing(simplified, GetOneSided(arcs, removed, droppedNational));
            await WriteGeoJsonAsync(this.Filter(written, removed), GetOutFile(outFile, output.Suffix))
                .ConfigureAwait(false);

            // The same rings over shorter arcs. Which vertices went was decided once per arc, so
            // dropping them from the arcs is the whole of simplifying the topology: no reference
            // changes and the two sides of a border stay the same line.
            await this.topoJsonService.WriteAsync(
                    [
                        ("countries", this.Filter(national, removed)),
                        ("substates", this.Filter(substates, removed)),
                    ],
                    arcs.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value
                            .Where(coordinate => !removed.Contains((coordinate.X, coordinate.Y)))
                            .ToArray()),
                    GetTopoJsonFile(outFile, output.Suffix))
                .ConfigureAwait(false);
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

    private sealed class RoundCoordinateSequenceFilter : ICoordinateSequenceFilter
    {
        public bool Done => false;

        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence seq, int i)
        {
            Coordinate coordinate = seq.GetCoordinate(i);
            coordinate.X = Math.Round(coordinate.X, OutputDecimals);
            coordinate.Y = Math.Round(coordinate.Y, OutputDecimals);
            seq.SetOrdinate(i, Ordinate.X, coordinate.X);
            seq.SetOrdinate(i, Ordinate.Y, coordinate.Y);
        }
    }
}