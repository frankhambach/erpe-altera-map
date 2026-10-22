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
using Erpe.Altera.Map.Services;

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
    // 0.001 degrees used before, and the weld in phase 3 replaces it with the measured
    // tolerance of 0.5 units.
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

    // How many points to leave an arc that does not close on itself, whatever the tolerance
    // says. Two is its own two nodes and nothing in between, so it is no floor at all: it is
    // what the simplifier already refuses to go below, since an arc's ends are the nodes it
    // meets its neighbours at.
    //
    // That is deliberate, and it reads oddly only until the ring guarantee in SelectRemoved is
    // taken into account. A ring of k arcs at this floor comes out as a k-gon over its nodes,
    // and at k of two that is a line: Gibraltar is one border arc and one coast arc and once
    // vanished whole at 0.01, taking the country out of the file with it. What keeps it now is
    // not a floor but the guarantee, which raises the floors of the arcs of any ring that comes
    // out enclosing nothing and simplifies again.
    //
    // Three was measured first and works, and it was the wrong instrument. It buys a vertex on
    // all 4,433 open arcs to avoid the guarantee raising one on four - Gibraltar's two and
    // Macau's two, which are the only arcs on this map that need it - and costs 19 KB of
    // out-low.json, 0.8%, for nothing the guarantee does not already promise. A blanket floor
    // is a guess about every ring made from no ring in particular; the guarantee asks the rings.
    //
    // So this is where simplification is allowed to start, and the guarantee is what it is not
    // allowed to finish below. ClosedArcMinimumPoints above is a different kind of thing and
    // stays: an island simplified to a triangle still encloses ground, so no guarantee would
    // save it, and 6 is about how much of an island is left rather than whether it is left.
    private const int OpenArcMinimumPoints = 2;

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
                    // DoNotModify, because Orient below has already turned the rings and this
                    // would turn some of them back. Its default is to enforce RFC 7946 itself,
                    // and handed 0 clockwise shells it writes 3 of them - Marmoria, Chukcha and
                    // one of Anian's - so orienting before it is not enough on its own.
                    new GeoJsonConverterFactory(
                        false,
                        GeoJsonConverterFactory.DefaultIdPropertyName,
                        RingOrientationOption.DoNotModify),
                },
        };

    private readonly IAssemblyService assemblyService;

    private readonly ICoastlineService coastlineService;

    private readonly ICorrectionService correctionService;

    private readonly IDiagnosticsService diagnosticsService;

    private readonly IFaceAssignmentService faceAssignmentService;

    private readonly GeometryFactory geometryFactory;

    private readonly IProjectionService projectionService;

    private readonly IRimService rimService;

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
        IRimService rimService,
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
        this.rimService = rimService;
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

    // RFC 7946 asks for counter-clockwise shells and clockwise holes, and in memory every ring is
    // wound the other way: 6480 of 6481 in the national layer, the odd one out being a ring of no
    // area. Not a defect, and not arbitrary either - 5f and 7a sort rings in the source map's
    // coordinates, where y runs down the page, and unprojecting turns y the other way up and every
    // ring with it. So something has to turn them all back on the way out.
    //
    // It used to be the serializer and the serializer got it wrong. It is told DoNotModify above,
    // because handed 0 clockwise shells it still writes 3, and the turning is done here instead
    // where it is a shoelace and a reversal rather than a question of what it makes of an awkward
    // ring.
    //
    // Measured over the national layer: one shell reached the file clockwise before this,
    // Marmoria, 961 points and 32.30 square degrees, and its borders are what made 807 segments
    // look as though two countries walked them the same way round. Both are 0 now, and of the 87
    // same-direction segments left every one is of zero length.
    //
    // A ring of no area is left alone, because there is nothing to turn: Namaquab's hole encloses
    // nothing, and reversing it changes no coordinate. Anian's islet beyond the rim was another
    // until it was taken round the rim rather than clamped onto it. It is the degenerate spike 8
    // reports, and a different thing to fix.
    private static Polygon Orient(Polygon polygon)
    {
        return polygon.Factory.CreatePolygon(
            Orient(polygon.Shell, false),
            [.. polygon.Holes.Select(hole => Orient(hole, true))]);
    }

    private static LinearRing Orient(LinearRing ring, bool isHole)
    {
        int winding = ring.Coordinates.GetWinding();
        if ((winding == 0) || ((winding > 0) != isHole))
        {
            return ring;
        }

        // The same coordinate objects in the other order. Both sides of a border share those
        // objects, which is what 7b set up, and turning a ring must not touch them.
        return ring.Factory.CreateLinearRing([.. ring.Coordinates.Reverse()]);
    }

    // A ring as the file will carry it rather than as it is held. Both writers round to six
    // decimals on the way out, and a ring thin enough loses the last of its area in that
    // rounding, so this is the only version of it worth asking which way round it runs.
    private static Coordinate[] Round(LinearRing ring)
    {
        return
        [
            .. ring.CoordinateSequence.ToCoordinateArray()
                .Select(
                    coordinate => new Coordinate(
                        Math.Round(coordinate.X, OutputDecimals),
                        Math.Round(coordinate.Y, OutputDecimals))),
        ];
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
                    Geometry = new MultiPolygon(
                        [.. country.Shapes.Select(shape => Orient(shape.Polygon))]),
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
    // and the lake shores, and the boundary of each face 7a had to leave out, since the
    // hole a dropped face leaves is bounded from the outside only. The rim as well, which
    // has its two sides 360 degrees apart once what lies beyond it is wrapped, so that each
    // copy of it is walked once - and walked once too where the far side was left out.
    private static Coordinate[][] GetOneSided(
        Dictionary<Arc, Coordinate[]> arcs,
        HashSet<(double X, double Y)>? removed,
        IEnumerable<Face> dropped)
    {
        HashSet<Arc> oneSided =
        [
            .. arcs.Keys.Where(arc => arc.Kind is ArcKind.Coast or ArcKind.Lake or ArcKind.Rim),
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

    // The rings a tolerance could cost their ground, each with the arcs whose floors are the only
    // way to keep it. Taken once for the whole run rather than once per tolerance, since it
    // depends on the geometry and not on the tolerance; per tolerance it cost 20 seconds of a
    // 120 second run and this way it costs nothing measurable.
    //
    // Only the rings that enclose something to begin with. Namaquab's lake of 1.3e-7 square units
    // encloses nothing before a vertex is touched, so no floor can save it and there is no point
    // asking.
    private static (LinearRing Ring, DirectedArc[] Arcs)[] GetProtectable(IEnumerable<Country> countries)
    {
        return
        [
            .. countries.SelectMany(country => country.Shapes)
                .SelectMany(
                    shape => shape.Polygon.Holes.Prepend<LinearRing>(shape.Polygon.Shell)
                        .Select((ring, index) => (Ring: ring, Index: index))
                        .Where(pair => shape.Rings.Count > pair.Index)
                        .Select(pair => (pair.Ring, Arcs: shape.Rings[pair.Index])))
                .Where(pair => Round(pair.Ring).GetWinding() != 0),
        ];
    }

    // The coordinates simplification takes out, decided arc by arc and collected into one
    // set. Dropping by coordinate rather than by position is what carries the decision to
    // both sides of a border: each side holds the same coordinates, so each side loses the
    // same ones.
    //
    // All the arcs in one call, because the rule that settles a shared coordinate needs to see
    // every arc that carries it, and because the floors it starts from are raised until no ring
    // comes out enclosing nothing. Both of those belong to the simplifier; what belongs here is
    // only what counts as a ring still being one, which is Survives below. There used to be a
    // second pass here putting the node coordinates back afterwards, and the shared-coordinate
    // rule covers them: no arc offers up its own ends, so some carrier always declines them.
    //
    // On this map it raises 4 arcs at 0.01 and none at 0.001, in one round: Gibraltar's border
    // and coast and Macau's, which are the two rings that come out as a line otherwise.
    private HashSet<(double X, double Y)> SelectRemoved(
        Dictionary<Arc, Coordinate[]> arcs,
        (LinearRing Ring, DirectedArc[] Arcs)[] rings,
        double tolerance)
    {
        Arc[] ordered = [.. arcs.Keys];
        Dictionary<Arc, int> indexes = ordered.Select((arc, index) => (arc, index))
            .ToDictionary(pair => pair.arc, pair => pair.index);
        (IReadOnlySet<(double X, double Y)> removed, int raised, int rounds) =
            this.simplificationService.SelectRemoved(
                [
                    .. ordered.Select(
                        arc => (arcs[arc], arc.IsClosed ? ClosedArcMinimumPoints : OpenArcMinimumPoints)),
                ],
                [.. rings.Select(pair => pair.Arcs.Select(directed => indexes[directed.Arc]).Distinct().ToArray())],
                (ring, dropped) => this.Survives(rings[ring].Ring, dropped),
                tolerance);
        if (rounds > 0)
        {
            Log.Information(
                "Raised the floor on {Raised} arcs over {Rounds} rounds to keep every ring that enclosed "
                + "ground still enclosing it.",
                raised,
                rounds);
        }

        return [.. removed];
    }

    // Whether a ring is still a ring once those coordinates are gone, asked with the two tests
    // that actually drop one in Filter below, so that the floors cannot come to protect
    // something different from what is at risk.
    private bool Survives(LinearRing ring, IReadOnlySet<(double X, double Y)> removed)
    {
        LinearRing? kept = this.Filter(ring, removed);

        return (kept != null) && (Round(kept).GetWinding() != 0);
    }

    // The countries with those coordinates taken out of them, and with whatever that leaves
    // unwritable left out as well. Two things can stop a ring being writable and both are
    // decided here, in one place, so that the GeoJson, the topology and the sharing check are
    // handed the same rings and cannot disagree about which exist.
    private Country[] Filter(IEnumerable<Country> countries, HashSet<(double X, double Y)> removed, string layer)
    {
        List<string> degenerate = [];
        List<string> lost = [];
        List<Country> filtered = [];
        foreach (Country country in countries)
        {
            List<Shape> shapes = [];
            foreach (Shape shape in country.Shapes)
            {
                Shape? kept = this.Filter(shape, removed, degenerate);
                if (kept != null)
                {
                    shapes.Add(kept);
                }
            }

            if (shapes.Count == 0)
            {
                lost.Add(country.Id);
                continue;
            }

            filtered.Add(new Country(country.Id, country.Type, shapes));
        }

        if (degenerate.Count > 0)
        {
            Log.Information(
                "Left {Rings} of {Layer}'s rings out because they enclose nothing in the coordinates they are "
                + "written in: {Which}.",
                degenerate.Count,
                layer,
                string.Join(", ", degenerate));
        }

        if (lost.Count > 0)
        {
            Log.Information(
                "{Countries} of {Layer}'s countries have no ring left to stand for them and are left out of it "
                + "rather than written with an empty geometry: {Which}.",
                lost.Count,
                layer,
                string.Join(", ", lost));
        }

        return [.. filtered];
    }

    // The shell and then the holes, each carrying the arcs it is made of beside it, because
    // Shape.Rings matches Polygon's rings one for one and that pairing is what lets the topology
    // write the same rings as the GeoJson. A ring dropped from one of them has to go from the
    // other, and a shape whose shell goes takes its holes with it.
    private Shape? Filter(Shape shape, HashSet<(double X, double Y)> removed, List<string> degenerate)
    {
        LinearRing[] rings = [shape.Polygon.Shell, .. shape.Polygon.Holes];
        List<LinearRing> kept = [];
        List<DirectedArc[]> arcs = [];
        for (int index = 0; index < rings.Length; ++index)
        {
            LinearRing? ring = this.Filter(rings[index], removed);

            // A ring with no inside is not a ring. A consumer asked to fill it has nothing to
            // fill, it runs its own line twice over, and which way round it goes says nothing.
            // Two things on this map leave one: a lake of 1.3e-7 square units, which is a line
            // by the time it is rounded, and simplification thinning a shell down to a single
            // there and back. An islet drawn beyond the rim was a third while every longitude
            // past 180 was clamped onto the antimeridian; IRimService takes it round instead.
            if ((ring != null) && (Round(ring).GetWinding() == 0))
            {
                string role = index == 0
                    ? "shell"
                    : string.Create(CultureInfo.InvariantCulture, $"hole {index}");
                degenerate.Add($"{shape.Id}'s {role}");
                ring = null;
            }

            if (ring == null)
            {
                if (index == 0)
                {
                    return null;
                }

                continue;
            }

            kept.Add(ring);
            if (shape.Rings.Count > index)
            {
                arcs.Add(shape.Rings[index]);
            }
        }

        return new Shape(shape.Id, this.geometryFactory.CreatePolygon(kept[0], [.. kept.Skip(1)]), arcs);
    }

    // A ring that comes out of this with too little left to close is left out rather than
    // kept unsimplified: keeping it would be the one thing that breaks sharing, since its
    // neighbour would have dropped those coordinates and it would not.
    private LinearRing? Filter(LinearRing ring, IReadOnlySet<(double X, double Y)> removed)
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
    //
    // A shape wrapped round the rim takes the offset of the arcs it is made of, which IRimService
    // has made the same for all of them, and adds it after the memo rather than in it. The memo
    // stays one longitude per coordinate, and the offset is the same addition on the same value
    // wherever that coordinate is written, in the shape or in the arc.
    private void Unproject(
        IEnumerable<Country> countries,
        Envelope sourceEnvelope,
        Dictionary<Coordinate, Coordinate> unprojected,
        IReadOnlyDictionary<Arc, double> offsets)
    {
        foreach (Shape shape in countries.SelectMany(country => country.Shapes))
        {
            double offset = shape.Rings.Count == 0 ? 0.0 : offsets.GetValueOrDefault(shape.Rings[0][0].Arc);
            shape.Polygon = this.geometryFactory.CreatePolygon(
                this.Unproject(shape.Polygon.Shell, sourceEnvelope, unprojected, offset),
                shape.Polygon.Holes.Select(hole => this.Unproject(hole, sourceEnvelope, unprojected, offset))
                    .ToArray());
        }
    }

    private LinearRing Unproject(
        LinearRing linearRing,
        Envelope sourceEnvelope,
        IDictionary<Coordinate, Coordinate> unprojected,
        double offset)
    {
        return this.geometryFactory.CreateLinearRing(
            linearRing.CoordinateSequence.ToCoordinateArray()
                .Select(coordinate => this.Unproject(coordinate, sourceEnvelope, unprojected, offset))
                .ToArray());
    }

    private Coordinate Unproject(
        Coordinate coordinate,
        Envelope sourceEnvelope,
        IDictionary<Coordinate, Coordinate> unprojected,
        double offset)
    {
        if (!unprojected.TryGetValue(coordinate, out Coordinate? result))
        {
            result = this.projectionService.Unproject(this.projectionService.Rescale(coordinate, sourceEnvelope));
            unprojected.Add(coordinate, result);
        }

        return offset.Equals(0.0) ? result.Copy() : new Coordinate(result.X + offset, result.Y);
    }

    // The rim goes into the memo first and at exactly 180 degrees, east or west, whatever Newton's
    // method makes of it. Its nodes are where the coast and the borders cross it, and those are
    // put on the noding grid in the source map's units, so they come back a hair either side of
    // the meridian. A coast ending at 180.0001 inside the rim would be past it, and once wrapped
    // the far side would meet it at -179.9999 instead of where it is. Exactly 180 is where both
    // sides meet, and offset by 360 it is exactly -180.
    private void UnprojectRim(
        ArcGraph arcGraph,
        Envelope sourceEnvelope,
        Dictionary<Coordinate, Coordinate> unprojected)
    {
        foreach (Coordinate coordinate in arcGraph.Arcs.Where(arc => arc.Kind == ArcKind.Rim)
                     .SelectMany(arc => arc.GetFullCoordinates()))
        {
            if (!unprojected.ContainsKey(coordinate))
            {
                Coordinate result = this.projectionService.Unproject(
                    this.projectionService.Rescale(coordinate, sourceEnvelope));
                unprojected.Add(coordinate, new Coordinate(result.X < 0.0 ? -180.0 : 180.0, result.Y));
            }
        }
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
                .Where(element => element.IsDrawnLinework())
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

        // The graph every later phase works on. The tracers go in here, the coast and the lake
        // shores join them in 3, and the faces traced from the lot of them in 5 are what the
        // output is assembled from - the drawn shapes only say which country each face is.
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
                    .Where(element => element.IsDrawnLinework())
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
            countries.Where(country => country.Type != CountryType.FederalState)
                .SelectMany(country => country.Shapes)
                .Select(shape => shape.Polygon),
            arcGraph);

        this.correctionService.Weld(arcGraph, MergeTolerance, SplitTolerance);
        this.rimService.AddArcs(arcGraph, sourceEnvelope);

        // One file per stage rather than one per kind, and both of them framed on the source
        // document's own view box so they open on top of the map and on each other.
        //
        // Two stages, because the identifiers do not survive the one in between. 5a is the first
        // thing BuildFaces does and it splits arcs at their crossings, minting new identifiers as
        // it goes: on this map the highest goes from 11,125 to 11,717, so every arc the reports
        // after it name - the bridges of 5h above all - is absent from a file written before it.
        // The five bridges 5h reports are 11204, 11271, 11273, 11395 and 11421, and not one of
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

        // Before the faces are assigned, so that a face left out here is claimed by nobody and
        // absorbed into nobody, exactly as though it had never been drawn.
        IReadOnlyList<Face> beyondRim = this.rimService.Wrap(arcGraph, sourceEnvelope);

        // One pass per layer, because a substate shape lies inside its country's shape and
        // passing both at once would report every substate face as belonging to two
        // countries. The national pass is assembled before the substate pass runs, since
        // assigning a face sets the one owner it carries.
        Country[] national = countries
            .Where(country => country.Type is CountryType.UnitaryState or CountryType.FederatedState)
            .ToArray();
        Country[] substates = countries.Where(country => country.Type is CountryType.FederalState).ToArray();

        this.faceAssignmentService.Assign(arcGraph, national, true);
        IReadOnlyList<Face> droppedNational = [.. beyondRim, .. this.assemblyService.Assemble(arcGraph, national)];
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

        // After both layers are assembled, since a stretch of rim is shared between them and
        // the copy a wrapped shape gets has to be the same copy in either.
        IReadOnlyDictionary<Arc, double> offsets = this.rimService.Twin(arcGraph, countries);

        Dictionary<Coordinate, Coordinate> unprojected = new Dictionary<Coordinate, Coordinate>();
        this.UnprojectRim(arcGraph, sourceEnvelope, unprojected);
        this.Unproject(countries, sourceEnvelope, unprojected, offsets);

        // The arcs in the coordinates the output is written in. Simplification is measured
        // in the units of the tolerances, which are degrees, and taking the arcs through
        // the same memo as the shapes means an arc coordinate and the shape coordinate it
        // became are literally the same value.
        Dictionary<Arc, Coordinate[]> arcs = arcGraph.Arcs.ToDictionary(
            arc => arc,
            arc => arc.GetFullCoordinates()
                .Select(
                    coordinate => this.Unproject(
                        coordinate,
                        sourceEnvelope,
                        unprojected,
                        offsets.GetValueOrDefault(arc)))
                .ToArray());

        Country[] written = countries
            .Where(country => country.Type is CountryType.UnitaryState or CountryType.FederalState)
            .ToArray();

        // Nothing is thinned at full detail, so nothing is taken out by coordinate here; what
        // this pass is for is the rings that are not rings, which the same method decides. It
        // runs before the sharing check as well as before the writers, because a ring that is
        // not written should not be counted either.
        HashSet<(double X, double Y)> keepEveryCoordinate = [];
        national = this.Filter(national, keepEveryCoordinate, "the national layer");
        substates = this.Filter(substates, keepEveryCoordinate, "the substate layer");
        written = this.Filter(written, keepEveryCoordinate, "the combined output");

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

        (LinearRing Ring, DirectedArc[] Arcs)[] protectable = GetProtectable(countries);

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
            HashSet<(double X, double Y)> removed = this.SelectRemoved(arcs, protectable, output.Tolerance);
            Log.Information(
                "Simplifying at {Tolerance} removes {Removed} of the {Total} distinct coordinates in the arcs.",
                output.Tolerance,
                removed.Count,
                arcs.Values.Sum(line => line.Length - 1));

            // Checked on the national layer rather than on what is written, because that is
            // the layer covering the land and so the only one with a coast to be compared
            // against. The coordinates dropped are the same for either set.
            Country[] simplified = this.Filter(national, removed, "the national layer");
            Country[] simplifiedSubstates = this.Filter(substates, removed, "the substate layer");
            Log.Information(
                "Simplifying leaves {Rings} of the {Total} rings of the national layer standing.",
                CountRings(simplified),
                CountRings(national));
            this.verificationService.VerifySharing(simplified, GetOneSided(arcs, removed, droppedNational));
            await WriteGeoJsonAsync(
                    this.Filter(written, removed, "the combined output"),
                    GetOutFile(outFile, output.Suffix))
                .ConfigureAwait(false);

            // The same rings over shorter arcs. Which vertices went was decided once per arc, so
            // dropping them from the arcs is the whole of simplifying the topology: no reference
            // changes and the two sides of a border stay the same line.
            await this.topoJsonService.WriteAsync(
                    [
                        ("countries", simplified),
                        ("substates", simplifiedSubstates),
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