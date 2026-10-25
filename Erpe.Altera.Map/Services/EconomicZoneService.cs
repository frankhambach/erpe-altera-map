// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Triangulate;
using NetTopologySuite.Triangulate.QuadEdge;

using Serilog;

// The exclusive economic zones, in two halves. GetBoundaries samples the shores at a fixed spacing,
// takes the Voronoi diagram of the samples on the sphere, and reads off it the two kinds of line
// that bound the zones; AddArcs takes those lines into the source map and cuts them back to the
// water. A median line is a Voronoi edge
// between two samples of different countries; an edge between two samples of the same country
// separates nothing and is never drawn. The outer limit is not a buffer but the identity that
// replaces one: a point within the limit of the shore lies in the cell of its nearest sample, and
// that sample is within the limit too, so the zones are the union of every cell cut down to the
// circle about its own sample, and the limit is the stretches of those circles left standing.
//
// Points rather than the segments of the shore, because nothing usable computes a segment Voronoi
// diagram on a sphere, and because it does not need to: at a spacing s the median line found from
// samples is off the true one by about s squared over eight times the distance from the shore,
// a metre at a hundred kilometres out for a spacing of one. Equidistance lines are drawn from
// discrete basepoints in practice in any case.
public sealed class EconomicZoneService : IEconomicZoneService
{
    // The map has no scale of its own, since the projection is rescaled onto the view box, so the
    // world is taken to be the size of the Earth. That is what the 0.65 m of the noding grid in
    // Program.cs assumes as well. A sphere and not the ellipsoid: the difference is under half a
    // percent of any distance here, on a world that was drawn rather than surveyed.
    private const double EarthRadius = 6371.0;

    // How far apart the samples are taken along a shore, in kilometres. A unit of the source map
    // is about 6.5 km at the equator, so this is finer than anything the map draws, and the error
    // it costs a median line is a metre a hundred kilometres out.
    private const double SampleSpacing = 1.0;

    // How long a stretch of the outer limit may run between two of its vertices, in kilometres.
    // Most stretches are a cell's width, under a kilometre, and need nothing in between; it is
    // the cell of a headland's last sample, which sweeps through a wide angle, that does.
    private const double LimitStep = 1.0;

    // How far in from either end of an open shore arc the first sample is put, in kilometres,
    // and the next twice as far, and so on until the spacing is reached. The end is a node, and
    // where it is the node at which a border meets the shore, the two arcs either side of it belong
    // to the two countries the border divides. The first sample along each is the same distance
    // from the node, so the Voronoi edge between them runs through the node itself, and the median
    // line leaves the shore where the border meets it. That is what lets the median be fixed to
    // the land graph's own node without moving it or splitting the coast.
    //
    // The doubling is what keeps it there. With a single sample a metre in, the median left the
    // node and within a few hundred metres was back among samples a kilometre apart, half a
    // kilometre off true, and 34 median lines clipped a jagged coast on the way and crossed it last
    // 0.06 to 0.56 units from their node. A sample at every doubling keeps the spacing near the
    // node a fraction of the distance from it, which is the accuracy the median has there, for ten
    // samples an end. Each pair of them is the same distance from the node, measured and not
    // assumed, so every one has its bisector through the node. Staggering the two sides instead,
    // to keep four samples off one circle, was tried and is worse: in a lake wedge of ten degrees
    // a small difference either side swings the bisector across the shore, and 39 median lines
    // reached it up to 0.7 units from their node.
    private const double NodeOffset = 0.001;

    // 200 nautical miles, exactly.
    private const double ZoneRadius = 370.4;

    // How many points of the sphere are tried in looking for the one furthest from every shore,
    // and how many samples each is measured against. A coarse search, because all it needs is a
    // point well out in the open ocean: the triangulation is projected from it, and nothing about
    // the answer depends on which such point it is.
    private const int CandidateCount = 4096;

    private const int CandidateSampleCount = 20000;

    // How many points ring the centre of the projection. Enough that no circle through three
    // samples can pass between two of them and contain the centre - see AddSentinels.
    private const int SentinelCount = 32;

    // The side of the grid the sites are placed on to order them along a Hilbert curve: a million
    // cells a side, so that two sites share a cell only where they are a few metres apart, and the
    // order among those few hardly matters.
    private const long HilbertSide = 1L << 20;

    // How many times a sample near a node is moved along the shore to bring it to its distance from
    // the node. Two or three do it, since the distance along the shore and from the node differ by
    // a fraction that hardly changes over a few metres.
    private const int MaximumLocateIterations = 8;

    // The refinement: a sample's spacing along its shore must be no more than this times the
    // distance to the nearest sample of another country, or the stretch about it is resampled at
    // RefinedSpacing times that distance. Half and a quarter: the comb a median line makes in a
    // channel narrower than the spacing is gone once the spacing is under the width.
    private const double ResolvedSpacing = 0.5;

    private const double RefinedSpacing = 0.25;

    // The narrowest water between two countries the refinement resolves, in kilometres. A unit of
    // the source map is about 6.5 km and its curves are flattened to a hundredth of one, so water
    // narrower than this is not a channel the map draws but two shapes disagreeing about where the
    // shore is, and a median line down it means nothing. Refined all the way, the five such places
    // on this map - a lake shore drawn out and back 1.3 m apart at Malva and Rovuma, two coasts
    // leaving a node 0.0006 radians apart at Siam and Khumer - took 150,000 samples and came out
    // as 3870 fragments of median line, each reaching the shore somewhere else. They are reported
    // instead, to be put right in the map.
    private const double MinimumChannel = 0.05;

    // How far from a node two countries' shores may come within MinimumChannel of each other before
    // it is reported, in kilometres. Nearer than this it is the two shores meeting at an acute
    // angle, which they are entitled to.
    private const double SliverNodeDistance = 1.0;

    // How many times the refinement is asked again before it stops.
    private const int MaximumRefinements = 8;

    // How near a node two samples of different countries must both be, as a multiple of their
    // distance from each other, for their closeness to be put down to the node. See FindUnresolved.
    private const double NodeVicinity = 2.0;

    // How close two vertices of a boundary may lie before the second is taken for the first, as an
    // angle: about 6 mm. See Diagram.Thin.
    private const double MinimumSeparation = 1e-9;

    // How far from where a median line crosses the shore the node it is fixed to may lie, in
    // units of the source map. NodeOffset puts the crossing on the node itself, so this only has
    // to absorb what projecting the line into the map does to it, and what is left of the error in
    // a median close to the shore.
    private const double AnchorTolerance = 0.05;

    // How far a median line that reaches the shore beyond AnchorTolerance may still be joined to the
    // node of its two countries, in units of the source map, provided the straight way from its last
    // point over water to the node touches no shore. At Namiba and Natalia the two coasts leave their
    // node along one line for 330 m before they part, and the median comes up the water between them
    // to where they part, 0.09 units short of the node; left loose there it divided nothing, and
    // Natalia was given Namiba's whole zone with its own.
    private const double ReachTolerance = 0.25;

    // How many of the median lines that reach the shore away from any node of their two countries,
    // and of the crossings left between arcs, are named in the log.
    private const int MaximumReported = 20;

    private readonly IProjectionService projectionService;

    public EconomicZoneService(IProjectionService projectionService)
    {
        this.projectionService = projectionService;
    }

    public IReadOnlyList<ZoneBoundary> GetBoundaries(IEnumerable<Shore> shores, Envelope sourceEnvelope)
    {
        ArgumentNullException.ThrowIfNull(shores);
        ArgumentNullException.ThrowIfNull(sourceEnvelope);

        Stopwatch stopwatch = Stopwatch.StartNew();
        List<Country> owners = [];
        Samples samples = new Samples();
        List<ShoreLine> lines = this.GetLines(shores, sourceEnvelope, owners);
        double length = Sample(lines, samples);
        Log.Information(
            "Sampled {Length:F0} km of coast and lake shore every {Spacing} km, {Samples} points for {Countries} "
            + "countries, in {Elapsed} ms.",
            length,
            SampleSpacing,
            samples.Points.Count,
            owners.Count,
            stopwatch.ElapsedMilliseconds);
        List<SphereVector> points = samples.Points;
        List<int> labels = samples.Labels;

        double reach = ZoneRadius / EarthRadius;
        (SphereVector pole, double clearance) = FindPole(points);
        AddSentinels(samples, pole, clearance, reach);
        Coordinate centre = pole.ToGeographic();
        Log.Information(
            "Triangulating the shore samples as projected from {Longitude:F2}, {Latitude:F2}, {Clearance:F0} km "
            + "from the nearest shore.",
            centre.X,
            centre.Y,
            clearance * EarthRadius);

        stopwatch.Restart();
        Triangulation triangulation = new Triangulation(points, pole);
        List<int[]> triangles = triangulation.GetTriangles();
        Dictionary<long, (int First, int Second)> edges = GetEdges(triangles, points.Count);
        Log.Information(
            "Triangulated {Points} points into {Triangles} triangles and {Edges} edges in {Elapsed} ms.",
            points.Count,
            triangles.Count,
            edges.Count,
            stopwatch.ElapsedMilliseconds);

        for (int round = 1; round <= MaximumRefinements; ++round)
        {
            stopwatch.Restart();
            Dictionary<int, List<(double From, double To, double Spacing)>> requests = FindUnresolved(
                edges,
                lines,
                samples);
            if (requests.Count == 0)
            {
                break;
            }

            int from = points.Count;
            Refine(requests, lines, samples);
            triangulation.Insert(points, from);
            triangles = triangulation.GetTriangles();
            edges = GetEdges(triangles, points.Count);
            Log.Information(
                "Refinement {Round} resampled {Stretches} stretches of {Shores} shores, adding {Added} points, in "
                + "{Elapsed} ms.",
                round,
                requests.Values.Sum(stretches => stretches.Count),
                requests.Count,
                points.Count - from,
                stopwatch.ElapsedMilliseconds);
        }

        List<string> slivers = FindSlivers(edges, lines, samples);
        if (slivers.Count > 0)
        {
            Log.Warning(
                "{Count} places where two countries' shores come within {Minimum} km of each other away from any "
                + "node they share, too close for a median line to mean anything; likely shapes that disagree about the "
                + "shore: {Which}.",
                slivers.Count,
                MinimumChannel,
                string.Join("; ", slivers.Take(MaximumReported)));
        }

        if (triangulation.Lost > 0)
        {
            Log.Warning("{Lost} shore samples coincided with another and were left out.", triangulation.Lost);
        }

        stopwatch.Restart();
        SphereVector[] centres =
        [
            .. triangles.Select(triangle => GetCircumcentre(points[triangle[0]], points[triangle[1]], points[triangle[2]])),
        ];
        Diagram diagram = new Diagram(points, labels, centres, reach);
        diagram.AddMedians(edges);
        diagram.AddLimits(GetNeighbours(edges, points.Count));
        List<ZoneBoundary> boundaries = diagram.Chain(owners);
        Log.Information(
            "Found {Medians} median lines of {MedianLength:F0} km and {Limits} stretches of limit of {LimitLength:F0} km, "
            + "meeting at {Junctions} points where three zones or more meet, in {Elapsed} ms.",
            boundaries.Count(boundary => boundary.Kind == ArcKind.Median),
            boundaries.Where(boundary => boundary.Kind == ArcKind.Median).Sum(GetLength),
            boundaries.Count(boundary => boundary.Kind == ArcKind.Limit),
            boundaries.Where(boundary => boundary.Kind == ArcKind.Limit).Sum(GetLength),
            diagram.CountJunctions(),
            stopwatch.ElapsedMilliseconds);
        Log.Information(
            "{Thinned} vertices of the zone boundaries left out for lying on top of the one before them.",
            diagram.Thinned);
        if (diagram.Inconsistent > 0)
        {
            Log.Warning(
                "{Inconsistent} pieces of the zone boundaries with a length to them put the countries the other way "
                + "round from the rest of the boundary they were chained into.",
                diagram.Inconsistent);
        }

        if ((diagram.Unpaired > 0) || (diagram.Wide > 0))
        {
            Log.Warning(
                "{Unpaired} edges of the zone triangulation within reach had a triangle on one side only and {Wide} spanned half the "
                + "sphere; both were left out.",
                diagram.Unpaired,
                diagram.Wide);
        }

        return boundaries;
    }

    // The boundaries projected into the source map, cut back to the water, and made arcs
    // of a graph of their own. Every vertex is asked whether it lies over water, by ShoreIndex, but
    // only where the segment before it touches the shore, since nowhere else can the answer change.
    // A run of vertices over water becomes an arc. Where a median line reaches the shore it ends
    // at the land graph's node at which its two countries' shores meet - the node itself, shared
    // and not copied, so that the zone and the land are bounded by the same point - and no coast
    // arc is split. A limit never reaches the shore at all: every point of it is the limit's
    // distance from the nearest shore, and a point of the shore is no distance from it.
    //
    // A boundary is cut where it crosses the antimeridian, at exactly 180 degrees either way, so
    // that the two pieces end on the rim at either edge of the map and nothing runs across it.
    public ZoneGraph AddArcs(
        ArcGraph arcGraph,
        IEnumerable<Shore> shores,
        IEnumerable<ZoneBoundary> boundaries,
        Envelope sourceEnvelope)
    {
        ArgumentNullException.ThrowIfNull(arcGraph);
        ArgumentNullException.ThrowIfNull(shores);
        ArgumentNullException.ThrowIfNull(boundaries);
        ArgumentNullException.ThrowIfNull(sourceEnvelope);

        Stopwatch stopwatch = Stopwatch.StartNew();
        ArcGraph water = new ArcGraph(arcGraph);
        WaterCut cut = new WaterCut(water, new ShoreIndex(arcGraph.Arcs), GetMeetings(shores));
        HashSet<Coordinate> antimeridian = [];
        foreach (ZoneBoundary boundary in boundaries)
        {
            Coordinate[][] lines =
            [
                .. SplitAtAntimeridian(boundary.Coordinates)
                    .Select(part => part.Select(coordinate => this.Project(coordinate, sourceEnvelope)).ToArray()),
            ];
            for (int index = 1; index < lines.Length; ++index)
            {
                antimeridian.Add(lines[index - 1][^1]);
                antimeridian.Add(lines[index][0]);
            }

            cut.Add(boundary, lines);
        }

        Log.Information(
            "{Arcs} zone arcs over water, {Medians} of them median lines; {OverLand} boundaries lie wholly over land. "
            + "{Tested} points asked whether they lie over water, in {Elapsed} ms.",
            water.Arcs.Count,
            water.Arcs.Count(arc => arc.Kind == ArcKind.Median),
            cut.OverLand,
            cut.Tested,
            stopwatch.ElapsedMilliseconds);
        Log.Information(
            "{Anchored} median line ends fixed to the node at which their countries' shores meet, the furthest "
            + "{Furthest:G3} units from where the line crosses the shore; {Wavering} runs out from a node and back to it "
            + "left out, and {Skipped} points passed over that the node could not reach without crossing the shore; "
            + "{Reached} more ends joined straight to their node over water from further away.",
            cut.Anchored,
            cut.Furthest,
            cut.Wavering,
            cut.Skipped,
            cut.Reached);

        // None is expected, since a median line, a limit and a shore never cross and every meeting
        // of a median line with the shore has been made a node.
        (int crossings, IReadOnlyList<string> where) = ArcCrossings.Count(
            water.Arcs,
            arcGraph.Arcs.Where(arc => arc.Kind is ArcKind.Coast or ArcKind.Lake),
            MaximumReported);
        (int onRim, int elsewhere) = CountDangling(water, antimeridian);
        Log.Information(
            "{Crossings} crossings left between the zone arcs and the shore or each other; {OnRim} zone arc ends on "
            + "the antimeridian wait for the rim, and {Elsewhere} meet nothing; {Grazing} segments crossing the shore "
            + "with water at either end cut there.",
            crossings,
            onRim,
            elsewhere,
            cut.Grazing);
        if (where.Count > 0)
        {
            Log.Warning("Zone arcs crossing, among them: {Which}.", string.Join("; ", where));
        }

        if (cut.Loose.Count > 0)
        {
            Log.Warning(
                "{Loose} median line ends reach the shore away from any node of their countries and are left loose, among "
                + "them: {Which}.",
                cut.Loose.Count,
                string.Join("; ", cut.Loose.Take(MaximumReported)));
        }

        return new ZoneGraph(
            water,
            cut.Boundaries,
            [.. water.Nodes.Where(node => antimeridian.Contains(node.Coordinate))]);
    }

    // The triangulation is planar Delaunay on
    // the stereographic image of the points, which is spherical Delaunay exactly, because the
    // projection takes circles to circles - except for the triangles whose circle contains the
    // centre of projection, which come out turned inside out and are missing. A ring of sentinels
    // about the centre makes those triangles theirs alone: any circle through three samples that
    // contains the centre is at least half the clearance across, so it contains a third of the
    // ring, and a ring of 32 cannot leave a third of itself empty. The ring sits at half what the
    // clearance has to spare over twice the limit, so a sentinel's own bisector with any sample
    // lies beyond the limit and cuts nothing off a zone.
    private static void AddSentinels(Samples samples, SphereVector pole, double clearance, double reach)
    {
        double radius = (clearance - (2.0 * reach)) / 2.0;
        if (radius <= 0.0)
        {
            throw new InvalidOperationException(
                "No point of the ocean is twice the limit from every shore, so there is nowhere to project from.");
        }

        (SphereVector east, SphereVector north) = GetTangents(pole);
        for (int index = 0; index < SentinelCount; ++index)
        {
            double azimuth = (2.0 * Math.PI * index) / SentinelCount;
            samples.Add(
                pole.Scale(Math.Cos(radius))
                    .Add(east.Scale(Math.Cos(azimuth) * Math.Sin(radius)))
                    .Add(north.Scale(Math.Sin(azimuth) * Math.Sin(radius))),
                -1,
                -1,
                0.0);
        }
    }

    // The point of a Fibonacci lattice furthest from every shore. Measured against every so many
    // samples first, which can only overstate how far a candidate is from the shore, since it
    // leaves samples out: an islet of fewer samples than the stride can be missed whole. So the
    // candidates are then measured against every sample in order of that first figure, and the
    // search stops at the first whose first figure is no better than the best measured, which
    // nothing after it can beat. It once picked a point 152 km from an islet at 124 W, 50 S that
    // every 65th sample happened to step over, where the lattice has points 1918 km from any shore.
    private static (SphereVector Pole, double Clearance) FindPole(List<SphereVector> points)
    {
        int stride = Math.Max(1, points.Count / CandidateSampleCount);
        double goldenAngle = Math.PI * (3.0 - Math.Sqrt(5.0));
        List<(SphereVector Candidate, double Bound)> candidates = [];
        for (int index = 0; index < CandidateCount; ++index)
        {
            double z = 1.0 - (((2.0 * index) + 1.0) / CandidateCount);
            double radius = Math.Sqrt(1.0 - (z * z));
            SphereVector candidate = new SphereVector(
                radius * Math.Cos(index * goldenAngle),
                radius * Math.Sin(index * goldenAngle),
                z);
            double nearest = double.MinValue;
            for (int point = 0; point < points.Count; point += stride)
            {
                nearest = Math.Max(nearest, candidate.Dot(points[point]));
            }

            candidates.Add((candidate, Math.Acos(Math.Clamp(nearest, -1.0, 1.0))));
        }

        SphereVector best = default;
        double clearance = 0.0;
        foreach ((SphereVector candidate, double bound) in candidates.OrderByDescending(pair => pair.Bound))
        {
            if (bound <= clearance)
            {
                break;
            }

            double exact = points.Min(candidate.AngleTo);
            if (exact > clearance)
            {
                (best, clearance) = (candidate, exact);
            }
        }

        return (best, clearance);
    }

    // The circumcentre of a triangle on the sphere, which is the pole of the plane through its
    // three points, taken on the side the points are on.
    private static SphereVector GetCircumcentre(SphereVector a, SphereVector b, SphereVector c)
    {
        SphereVector normal = b.Subtract(a).Cross(c.Subtract(a)).Normalize();

        return normal.Dot(a) < 0.0 ? normal.Scale(-1.0) : normal;
    }

    // Each edge of the triangulation, keyed by its two points, with the triangle on either side of
    // it. An edge with a triangle on one side only is on the outside of the planar triangulation,
    // which the sentinels see to is never an edge between two samples.
    private static Dictionary<long, (int First, int Second)> GetEdges(List<int[]> triangles, int count)
    {
        Dictionary<long, (int First, int Second)> edges = [];
        for (int index = 0; index < triangles.Count; ++index)
        {
            int[] triangle = triangles[index];
            for (int corner = 0; corner < 3; ++corner)
            {
                long key = GetKey(triangle[corner], triangle[(corner + 1) % 3], count);
                edges[key] = edges.TryGetValue(key, out (int First, int Second) sides)
                    ? (sides.First, index)
                    : (index, -1);
            }
        }

        return edges;
    }

    private static long GetKey(int one, int other, int count)
    {
        return ((long)Math.Min(one, other) * count) + Math.Max(one, other);
    }

    private static double GetLength(ZoneBoundary boundary)
    {
        double length = 0.0;
        for (int index = 1; index < boundary.Coordinates.Count; ++index)
        {
            length += SphereVector.FromGeographic(boundary.Coordinates[index - 1])
                .AngleTo(SphereVector.FromGeographic(boundary.Coordinates[index]));
        }

        return length * EarthRadius;
    }

    private static List<int>[] GetNeighbours(Dictionary<long, (int First, int Second)> edges, int count)
    {
        List<int>[] neighbours = new List<int>[count];
        for (int index = 0; index < count; ++index)
        {
            neighbours[index] = [];
        }

        foreach (long key in edges.Keys)
        {
            int one = (int)(key / count);
            int other = (int)(key % count);
            neighbours[one].Add(other);
            neighbours[other].Add(one);
        }

        return neighbours;
    }

    // Two directions along the sphere at a point, at right angles to each other and to it.
    private static (SphereVector East, SphereVector North) GetTangents(SphereVector point)
    {
        SphereVector axis = Math.Abs(point.Z) < 0.9 ? new SphereVector(0.0, 0.0, 1.0) : new SphereVector(1.0, 0.0, 0.0);
        SphereVector east = axis.Cross(point).Normalize();

        return (east, point.Cross(east));
    }

    // The ends of zone arcs that meet no other arc, those on the antimeridian apart, which will meet
    // the rim once it is added over water as well as over land. A median line's end on the shore is a node of the land graph
    // and not counted.
    private static (int OnRim, int Elsewhere) CountDangling(ArcGraph water, HashSet<Coordinate> antimeridian)
    {
        Dictionary<Node, int> degrees = [];
        foreach (Arc arc in water.Arcs)
        {
            degrees[arc.StartNode] = degrees.GetValueOrDefault(arc.StartNode) + 1;
            degrees[arc.EndNode] = degrees.GetValueOrDefault(arc.EndNode) + 1;
        }

        HashSet<Node> own = [.. water.Nodes];
        Node[] dangling = [.. degrees.Where(pair => (pair.Value == 1) && own.Contains(pair.Key)).Select(pair => pair.Key)];
        int onRim = dangling.Count(node => antimeridian.Contains(node.Coordinate));

        return (onRim, dangling.Length - onRim);
    }

    // Each shore's full length, from node to node, divided evenly into as many stretches as the
    // spacing asks for, and a sample put at every division. Not at the nodes of an open arc: a node
    // where a border meets the shore belongs to the countries either side of it both, and two
    // samples in one place are one point to the triangulation. An arc that closes on itself is an
    // island or a lake with nothing else on its shore, and its node is sampled like any other
    // point. Every shore gets one sample at least and every island three, however short, so that
    // no islet goes without a zone. And more samples near either end of an open arc, ever closer
    // to the node, and at the shore's corners there: see NodeOffset and GetCornerOffsets.
    //
    // Measured along the sphere, but placed on the shore as it is drawn: on the straight segment
    // between two of the source map's points, and then unprojected. The land the zones are cut back
    // to is that segment, and a sample placed on the great circle between its ends is off it. So is
    // one unprojected onto a grid of 0.0001 degrees, as Unproject did until it was made exact, and
    // that put the median line ten to twenty metres from the node it is fixed to.
    private static double Sample(List<ShoreLine> lines, Samples samples)
    {
        double total = 0.0;
        for (int shore = 0; shore < lines.Count; ++shore)
        {
            ShoreLine line = lines[shore];
            if (line.Length <= 0.0)
            {
                continue;
            }

            total += line.Length;
            bool isClosed = line.Arc.IsClosed;
            int count = Math.Max(isClosed ? 3 : 2, (int)Math.Ceiling((line.Length * EarthRadius) / SampleSpacing));
            foreach (int division in Enumerable.Range(isClosed ? 0 : 1, isClosed ? count : count - 1))
            {
                double position = (line.Length * division) / count;
                samples.Add(line.Locate(position), line.Label, shore, position);
                line.Remember(position);
            }

            if (!isClosed)
            {
                double step = line.Length / count;
                double[] offsets = [.. GetOffsets(step)];
                foreach (double offset in offsets.Concat(GetCornerOffsets(line.GetCorners(false, step), offsets)))
                {
                    (SphereVector start, double fromStart) = line.LocateFromStart(offset);
                    samples.Add(start, line.Label, shore, fromStart);
                }

                foreach (double offset in offsets.Concat(GetCornerOffsets(line.GetCorners(true, step), offsets)))
                {
                    (SphereVector end, double fromEnd) = line.LocateFromEnd(offset);
                    samples.Add(end, line.Label, shore, fromEnd);
                }
            }

            line.Sort();
        }

        return total * EarthRadius;
    }

    // The distances from a node at which the samples near it are put besides the doubling: one at
    // every corner of the shore within the doubling's reach, as far from the node as the corner is.
    //
    // Near a node the median line runs a few metres off the shore, and the doubling samples are as
    // far apart as they are from the node. Between two of them the shore is taken to be straight,
    // which it is not where it turns a corner: at Monache and Wintun the lake shore leaves the node
    // eastward for 270 m and then turns south, the corner falls between the samples at 256 and 512
    // m, and the median came up the lake and cut across it 2.6 m inside the land, crossing the shore
    // twice with water at both ends. With a sample at the corner the shore between samples is
    // straight, and the median cannot cut it.
    //
    // Only the shore's own corners. Putting every corner of every shore at a node on all of them, so
    // that the samples would still come in pairs the same distance from it, was tried and is worse:
    // the crossings went from 2 to 7, among them two stretches of limit off Antarctica, and the
    // median ends left loose from 9 to 14. Nor within a tenth of a doubling sample's distance, where
    // a corner adds two samples on top of each other and nothing the doubling did not have.
    private static IEnumerable<double> GetCornerOffsets(IEnumerable<double> corners, double[] offsets)
    {
        return corners.Where(corner => corner > 0.0)
            .Distinct()
            .Where(corner => !Array.Exists(offsets, offset => Math.Abs(offset - corner) <= (offset * 0.1)));
    }

    // The refinement: the stretches of shore where two countries' samples come closer to each
    // other than the samples of either are spaced along their own shores. There the samples cannot
    // say where the water between them is - two shores a metre apart sampled a kilometre apart are
    // one line with both countries' points strung along it - and the median line between them
    // comes out as a comb across both shores rather than a line down the middle. On this map that
    // left 23 median lines loose, every one in a channel or an inlet narrower than the spacing: a
    // lake shore drawn out and back 1.3 m apart at Malva and Rovuma, the coast at Siam and Khumer,
    // a wedge of sea a few hundred metres wide at Egypt and Katesh.
    //
    // Asked of the triangulation, since a sample's nearest neighbour of another country is always
    // a Delaunay neighbour of it. A stretch is resampled at a quarter of that distance, over the
    // spacing either side of the sample, and the triangulation asked again.
    //
    // Not where the two are close because their shores meet at a node and both are near it: there
    // the countries' shores touch, the distance between their samples goes to nothing, and asking
    // would resample towards the node for ever. The node is what NodeOffset takes care of, and a
    // pair is put down to it when both lie within NodeVicinity times their distance of it.
    private static Dictionary<int, List<(double From, double To, double Spacing)>> FindUnresolved(
        Dictionary<long, (int First, int Second)> edges,
        List<ShoreLine> lines,
        Samples samples)
    {
        int count = samples.Points.Count;
        Dictionary<int, double> nearest = [];
        foreach (long key in edges.Keys)
        {
            int one = (int)(key / count);
            int other = (int)(key % count);
            if ((samples.Labels[one] < 0)
                || (samples.Labels[other] < 0)
                || (samples.Labels[one] == samples.Labels[other]))
            {
                continue;
            }

            double distance = samples.Points[one].AngleTo(samples.Points[other]);
            if (IsAtNode(lines, samples, one, other, distance))
            {
                continue;
            }

            nearest[one] = Math.Min(nearest.GetValueOrDefault(one, double.MaxValue), distance);
            nearest[other] = Math.Min(nearest.GetValueOrDefault(other, double.MaxValue), distance);
        }

        double minimum = MinimumChannel / EarthRadius;
        Dictionary<int, List<(double From, double To, double Spacing)>> requests = [];
        foreach ((int sample, double distance) in nearest)
        {
            int shore = samples.Shores[sample];
            double position = samples.Positions[sample];
            double gap = lines[shore].GetGap(position);
            double spacing = distance * RefinedSpacing;
            if ((distance < minimum) || (gap <= (distance * ResolvedSpacing)))
            {
                continue;
            }

            if (!requests.TryGetValue(shore, out List<(double From, double To, double Spacing)>? stretches))
            {
                stretches = [];
                requests.Add(shore, stretches);
            }

            stretches.Add((position - gap, position + gap, spacing));
        }

        return requests;
    }

    // The pairs of countries whose shores come closer than MinimumChannel away from a node they
    // share, each named once with where it happens first.
    private static List<string> FindSlivers(
        Dictionary<long, (int First, int Second)> edges,
        List<ShoreLine> lines,
        Samples samples)
    {
        int count = samples.Points.Count;
        double minimum = MinimumChannel / EarthRadius;
        Dictionary<(int, int), SphereVector> found = [];
        foreach (long key in edges.Keys)
        {
            int one = (int)(key / count);
            int other = (int)(key % count);
            if ((samples.Labels[one] < 0)
                || (samples.Labels[other] < 0)
                || (samples.Labels[one] == samples.Labels[other]))
            {
                continue;
            }

            double distance = samples.Points[one].AngleTo(samples.Points[other]);
            if ((distance < minimum) && !IsAtNode(lines, samples, one, other, SliverNodeDistance / (EarthRadius * NodeVicinity)))
            {
                found.TryAdd(
                    (Math.Min(samples.Labels[one], samples.Labels[other]), Math.Max(samples.Labels[one], samples.Labels[other])),
                    samples.Points[one]);
            }
        }

        List<string> slivers = [];
        foreach (((int first, int second), SphereVector point) in found)
        {
            string countries = lines.First(line => line.Label == first).Owner.Id + "/"
                + lines.First(line => line.Label == second).Owner.Id;
            Coordinate where = point.ToGeographic();
            slivers.Add(string.Create(CultureInfo.InvariantCulture, $"{countries} at {where.X:F4}, {where.Y:F4}"));
        }

        return slivers;
    }

    // Whether two samples of different countries are close because their shores meet at a node
    // they both lie near.
    private static bool IsAtNode(List<ShoreLine> lines, Samples samples, int one, int other, double distance)
    {
        ShoreLine first = lines[samples.Shores[one]];
        ShoreLine second = lines[samples.Shores[other]];
        if (first.Arc.IsClosed || second.Arc.IsClosed)
        {
            return false;
        }

        double reach = NodeVicinity * distance;
        foreach ((Node node, double along) in first.GetNodeDistances(samples.Positions[one]))
        {
            if (along > reach)
            {
                continue;
            }

            foreach ((Node otherNode, double otherAlong) in second.GetNodeDistances(samples.Positions[other]))
            {
                if ((otherNode == node) && (otherAlong <= reach))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The samples the refinement asks for, added to the shores and to the list. A position is
    // taken on a grid of the spacing asked for, so that two requests over one stretch do not put
    // two samples a hair apart, and is left out where a sample already lies within half of it.
    private static void Refine(
        Dictionary<int, List<(double From, double To, double Spacing)>> requests,
        List<ShoreLine> lines,
        Samples samples)
    {
        foreach ((int shore, List<(double From, double To, double Spacing)> stretches) in requests)
        {
            ShoreLine line = lines[shore];
            foreach ((double from, double to, double spacing) in stretches)
            {
                for (double position = Math.Ceiling(from / spacing) * spacing; position <= to; position += spacing)
                {
                    double along = line.Arc.IsClosed
                        ? ((position % line.Length) + line.Length) % line.Length
                        : position;
                    if ((along <= 0.0) || (along >= line.Length) || (line.GetDistanceToNearest(along) < (spacing / 2.0)))
                    {
                        continue;
                    }

                    samples.Add(line.Locate(along), line.Label, shore, along);
                    line.Insert(along);
                }
            }
        }
    }

    // The distances from a node, as angles, at which the samples near it are put: NodeOffset, and
    // doubling from there for as long as they stay short of the spacing.
    private static IEnumerable<double> GetOffsets(double step)
    {
        for (double offset = NodeOffset / EarthRadius; offset < step; offset *= 2.0)
        {
            yield return offset;
        }
    }

    // The two countries of a median line in an order that does not depend on which was found first.
    private static (Country First, Country Second) GetPair(Country one, Country other)
    {
        return string.CompareOrdinal(one.Id, other.Id) <= 0 ? (one, other) : (other, one);
    }

    // For each pair of countries, the nodes of the land graph at which a shore of the one meets a
    // shore of the other: where a border reaches the coast or a lake shore. These are what the
    // median lines are fixed to.
    private static Dictionary<(Country First, Country Second), List<Node>> GetMeetings(IEnumerable<Shore> shores)
    {
        Dictionary<Node, List<Country>> owners = [];
        foreach (Shore shore in shores.Where(shore => !shore.Arc.IsClosed))
        {
            foreach (Node node in new[] { shore.Arc.StartNode, shore.Arc.EndNode })
            {
                if (!owners.TryGetValue(node, out List<Country>? at))
                {
                    at = [];
                    owners.Add(node, at);
                }

                if (!at.Contains(shore.Owner))
                {
                    at.Add(shore.Owner);
                }
            }
        }

        Dictionary<(Country First, Country Second), List<Node>> meetings = [];
        foreach ((Node node, List<Country> at) in owners)
        {
            for (int one = 0; one < at.Count; ++one)
            {
                for (int other = one + 1; other < at.Count; ++other)
                {
                    (Country First, Country Second) pair = GetPair(at[one], at[other]);
                    if (!meetings.TryGetValue(pair, out List<Node>? nodes))
                    {
                        nodes = [];
                        meetings.Add(pair, nodes);
                    }

                    nodes.Add(node);
                }
            }
        }

        return meetings;
    }

    // A boundary in pieces that each stay on one side of the antimeridian. Where it crosses, the
    // crossing is found on the great circle between the two points either side, and each piece
    // ends there at exactly 180 degrees, east on the one and west on the other.
    private static List<List<Coordinate>> SplitAtAntimeridian(IReadOnlyList<Coordinate> coordinates)
    {
        List<List<Coordinate>> parts = [];
        List<Coordinate> part = [coordinates[0]];
        for (int index = 1; index < coordinates.Count; ++index)
        {
            Coordinate previous = coordinates[index - 1];
            Coordinate current = coordinates[index];
            if (Math.Abs(current.X - previous.X) > 180.0)
            {
                SphereVector from = SphereVector.FromGeographic(previous);
                SphereVector to = SphereVector.FromGeographic(current);
                double fraction = from.Y / (from.Y - to.Y);
                double latitude = from.Scale(1.0 - fraction).Add(to.Scale(fraction)).Normalize().ToGeographic().Y;
                double side = previous.X > 0.0 ? 180.0 : -180.0;
                part.Add(new Coordinate(side, latitude));
                parts.Add(part);
                part = [new Coordinate(-side, latitude)];
            }

            part.Add(current);
        }

        parts.Add(part);

        return parts;
    }

    // Every shore as a line samples can be put on, with the country it belongs to numbered in the
    // order the countries are first met.
    private List<ShoreLine> GetLines(IEnumerable<Shore> shores, Envelope sourceEnvelope, List<Country> owners)
    {
        Dictionary<Country, int> indexes = [];
        List<ShoreLine> lines = [];
        foreach (Shore shore in shores)
        {
            if (!indexes.TryGetValue(shore.Owner, out int label))
            {
                label = owners.Count;
                owners.Add(shore.Owner);
                indexes.Add(shore.Owner, label);
            }

            lines.Add(new ShoreLine(this.projectionService, sourceEnvelope, shore, label));
        }

        return lines;
    }

    // Longitude and latitude to the source map's coordinates.
    private Coordinate Project(Coordinate coordinate, Envelope sourceEnvelope)
    {
        return this.projectionService.Unscale(this.projectionService.Project(coordinate), sourceEnvelope);
    }

    // The Voronoi diagram of the samples, cut down to the limit, as pieces between keyed points.
    // A key at zero or above is a triangle, whose circumcentre is a vertex of the diagram; a key
    // below zero is where an edge of the diagram crosses the limit. The pieces are chained into
    // boundaries at the end, through every key that two pieces of the same kind meet at and
    // nothing else does.
    private sealed class Diagram(
        List<SphereVector> points,
        List<int> labels,
        SphereVector[] centres,
        double reach)
    {
        private readonly Dictionary<int, List<(long Key, SphereVector Point)>> crossings = [];

        private readonly Dictionary<long, List<int>> incident = [];

        private readonly Dictionary<long, SphereVector> keyed = [];

        private readonly List<Piece> pieces = [];

        private long nextKey = -1;

        public int Inconsistent { get; private set; }

        public int Thinned { get; private set; }

        public int Unpaired { get; private set; }

        public int Wide { get; private set; }

        // The part of every Voronoi edge between two samples that lies within the limit of both.
        // The edge runs along the bisector of its two samples, and along the bisector the distance
        // to either is least at their midpoint and grows away from it, by the spherical Pythagoras
        // cos d = cos(h) cos(t) with h half the distance between them and t the way along. So the
        // part within the limit is the stretch of the bisector |t| <= acos(cos r / cos h) about
        // the midpoint, and it is cut to that with no search. Where it is cut, the cut is a point
        // on the limit circle of both samples, and is recorded as a crossing of each.
        public void AddMedians(Dictionary<long, (int First, int Second)> edges)
        {
            int count = points.Count;
            double cosReach = Math.Cos(reach);
            foreach ((long key, (int first, int second)) in edges)
            {
                int one = (int)(key / count);
                int other = (int)(key % count);
                if ((labels[one] < 0) || (labels[other] < 0))
                {
                    continue;
                }

                SphereVector a = points[one];
                SphereVector b = points[other];
                double half = a.AngleTo(b) / 2.0;
                if (half >= reach)
                {
                    continue;
                }

                if (second < 0)
                {
                    ++this.Unpaired;
                    continue;
                }

                SphereVector middle = a.Add(b).Normalize();
                SphereVector along = a.Cross(b).Normalize();
                double extent = Math.Acos(Math.Min(1.0, cosReach / Math.Cos(half)));
                (double Position, int Triangle) from = (GetPosition(centres[first], middle, along), first);
                (double Position, int Triangle) to = (GetPosition(centres[second], middle, along), second);
                if (from.Position > to.Position)
                {
                    (from, to) = (to, from);
                }

                if ((to.Position - from.Position) > Math.PI)
                {
                    ++this.Wide;
                    continue;
                }

                if ((from.Position >= extent) || (to.Position <= -extent))
                {
                    continue;
                }

                long start = this.GetEnd(from, -extent, middle, along, one, other);
                long end = this.GetEnd(to, extent, middle, along, one, other);
                if (labels[one] != labels[other])
                {
                    // Which country lies to the left of the piece as it runs, seen from outside the
                    // sphere: the one whose sample is on the side the cross product of its ends points
                    // to. The faces of the sea are given their owners by it.
                    SphereVector startPoint = this.GetPoint(start);
                    SphereVector endPoint = this.GetPoint(end);
                    bool isOneLeft = startPoint.Cross(endPoint).Dot(a) > 0.0;
                    this.Add(
                        new Piece(
                            ArcKind.Median,
                            Math.Min(labels[one], labels[other]),
                            Math.Max(labels[one], labels[other]),
                            isOneLeft ? labels[one] : labels[other],
                            isOneLeft ? labels[other] : labels[one],
                            start,
                            end,
                            [startPoint, endPoint]));
                }
            }
        }

        // The stretches of each sample's limit circle that lie inside its own cell, between the
        // crossings AddMedians recorded. Which of the stretches between two crossings is inside is
        // asked of its middle point, against every neighbour of the sample, since the cell is
        // exactly the points nearer the sample than any of its neighbours in the triangulation.
        public void AddLimits(List<int>[] neighbours)
        {
            double step = LimitStep / (EarthRadius * Math.Sin(reach));
            for (int sample = 0; sample < points.Count; ++sample)
            {
                if (labels[sample] < 0)
                {
                    continue;
                }

                SphereVector centre = points[sample];
                (SphereVector east, SphereVector north) = GetTangents(centre);
                if (!this.crossings.TryGetValue(sample, out List<(long Key, SphereVector Point)>? around))
                {
                    continue;
                }

                (long Key, SphereVector Point, double Azimuth)[] sorted =
                [
                    .. around.Select(
                            crossing => (crossing.Key, crossing.Point, Azimuth: GetAzimuth(crossing.Point, centre, east, north)))
                        .OrderBy(crossing => crossing.Azimuth)
                        .ThenBy(crossing => crossing.Key),
                ];
                for (int index = 0; index < sorted.Length; ++index)
                {
                    (long Key, SphereVector Point, double Azimuth) start = sorted[index];
                    (long Key, SphereVector Point, double Azimuth) end = sorted[(index + 1) % sorted.Length];
                    double sweep = end.Azimuth - start.Azimuth;
                    if (sweep <= 0.0)
                    {
                        sweep += 2.0 * Math.PI;
                    }

                    SphereVector halfway = this.GetOnCircle(centre, east, north, start.Azimuth + (sweep / 2.0));
                    if (!this.IsInside(halfway, sample, neighbours[sample]))
                    {
                        continue;
                    }

                    int steps = Math.Max(1, (int)Math.Ceiling(sweep / step));
                    List<SphereVector> line = [start.Point];
                    for (int division = 1; division < steps; ++division)
                    {
                        line.Add(this.GetOnCircle(centre, east, north, start.Azimuth + ((sweep * division) / steps)));
                    }

                    line.Add(end.Point);

                    // Round the sample by rising azimuth, which is anticlockwise seen from outside, so
                    // the zone is on the left and the high seas on the right.
                    this.Add(new Piece(ArcKind.Limit, labels[sample], -1, labels[sample], -1, start.Key, end.Key, line));
                }
            }
        }

        // The pieces joined end to end through every key that is the meeting of two pieces of the
        // same kind and the same countries and nothing else, so that a boundary runs between two
        // points where something changes, or round a loop where nothing does.
        public List<ZoneBoundary> Chain(List<Country> owners)
        {
            List<ZoneBoundary> boundaries = [];
            bool[] visited = new bool[this.pieces.Count];
            for (int index = 0; index < this.pieces.Count; ++index)
            {
                if (visited[index])
                {
                    continue;
                }

                visited[index] = true;
                Piece piece = this.pieces[index];

                // Every piece of the chain, turned to run the way the chain does: the ones walked
                // backwards from its start in reverse order and turned round once more.
                List<(int Piece, bool IsReversed)> before = this.Walk(index, piece.Start, visited);
                before.Reverse();
                List<(int Piece, bool IsReversed)> chain =
                [
                    .. before.Select(link => (link.Piece, !link.IsReversed)),
                    (index, false),
                    .. this.Walk(index, piece.End, visited),
                ];

                List<SphereVector> line = [];
                foreach ((int link, bool isReversed) in chain)
                {
                    IEnumerable<SphereVector> stretch = this.pieces[link].Points;
                    line.AddRange((isReversed ? stretch.Reverse() : stretch).Skip(line.Count == 0 ? 0 : 1));
                }

                (int left, int right) = this.GetSides(chain);
                boundaries.Add(
                    new ZoneBoundary(
                        piece.Kind,
                        owners[piece.First],
                        piece.Second < 0 ? null : owners[piece.Second],
                        owners[left],
                        right < 0 ? null : owners[right],
                        [.. this.Thin(line).Select(point => point.ToGeographic())]));
            }

            return boundaries;
        }

        public int CountJunctions()
        {
            return this.incident.Values.Count(
                at => (at.Count >= 3) && at.TrueForAll(index => this.pieces[index].Kind == ArcKind.Median));
        }

        private static double GetAngle(List<SphereVector> line)
        {
            double angle = 0.0;
            for (int index = 1; index < line.Count; ++index)
            {
                angle += line[index - 1].AngleTo(line[index]);
            }

            return angle;
        }

        private static double GetAzimuth(SphereVector point, SphereVector centre, SphereVector east, SphereVector north)
        {
            SphereVector offset = point.Subtract(centre.Scale(point.Dot(centre)));

            return Math.Atan2(offset.Dot(north), offset.Dot(east));
        }

        private static double GetPosition(SphereVector point, SphereVector middle, SphereVector along)
        {
            return Math.Atan2(point.Dot(along), point.Dot(middle));
        }

        private static bool IsSameKind(Piece one, Piece other)
        {
            return (one.Kind == other.Kind) && (one.First == other.First) && (one.Second == other.Second);
        }

        // The line without the vertices that lie on top of the one before them, its two ends always
        // kept. Four samples in a symmetric trapezoid lie on one circle, and the doubling samples of
        // NodeOffset make one at every node and every doubling, so two triangles share a circumcentre
        // there up to rounding. The Voronoi edge between them has no length and a direction made of
        // rounding, and the line stepped back over itself along it: 168 median lines on this map
        // crossed themselves that way.
        private List<SphereVector> Thin(List<SphereVector> line)
        {
            List<SphereVector> thinned = [line[0]];
            for (int index = 1; index < (line.Count - 1); ++index)
            {
                if (line[index].AngleTo(thinned[^1]) < MinimumSeparation)
                {
                    ++this.Thinned;
                    continue;
                }

                thinned.Add(line[index]);
            }

            if ((thinned.Count > 1) && (line[^1].AngleTo(thinned[^1]) < MinimumSeparation))
            {
                thinned.RemoveAt(thinned.Count - 1);
                ++this.Thinned;
            }

            thinned.Add(line[^1]);

            return thinned;
        }

        private void Add(Piece piece)
        {
            this.pieces.Add(piece);
            foreach (long key in new[] { piece.Start, piece.End })
            {
                if (!this.incident.TryGetValue(key, out List<int>? at))
                {
                    at = [];
                    this.incident.Add(key, at);
                }

                at.Add(this.pieces.Count - 1);
            }
        }

        // One end of the part of an edge within the limit: the circumcentre the edge runs to, if
        // it is within the limit, or else the point at the limit, which is new and is a crossing
        // of both samples' circles.
        private long GetEnd(
            (double Position, int Triangle) end,
            double limit,
            SphereVector middle,
            SphereVector along,
            int one,
            int other)
        {
            if (Math.Abs(end.Position) < Math.Abs(limit))
            {
                this.Remember(end.Triangle, centres[end.Triangle]);
                return end.Triangle;
            }

            long key = this.nextKey--;
            SphereVector point = middle.Scale(Math.Cos(limit)).Add(along.Scale(Math.Sin(limit)));
            this.Remember(key, point);
            foreach (int sample in new[] { one, other })
            {
                if (!this.crossings.TryGetValue(sample, out List<(long Key, SphereVector Point)>? around))
                {
                    around = [];
                    this.crossings.Add(sample, around);
                }

                around.Add((key, point));
            }

            return key;
        }

        private SphereVector GetOnCircle(SphereVector centre, SphereVector east, SphereVector north, double azimuth)
        {
            return centre.Scale(Math.Cos(reach))
                .Add(east.Scale(Math.Cos(azimuth) * Math.Sin(reach)))
                .Add(north.Scale(Math.Sin(azimuth) * Math.Sin(reach)));
        }

        private SphereVector GetPoint(long key)
        {
            return this.keyed[key];
        }

        private bool IsInside(SphereVector point, int sample, List<int> neighbours)
        {
            double own = point.Dot(points[sample]);

            return neighbours.TrueForAll(neighbour => own >= (point.Dot(points[neighbour]) - 1e-12));
        }

        private void Remember(long key, SphereVector point)
        {
            this.keyed.TryAdd(key, point);
        }

        // Which country is on either side of a chain as it runs, by the length of it each piece
        // speaks for. The pieces all ought to agree, and a piece of any length does, but a Voronoi
        // edge of no length - two triangles sharing a circumcentre, which the samples beside a node
        // make at every doubling - has a direction made of rounding and says either. Taking a chain's
        // sides from its first piece took them from one of those 2692 times on this map, and the
        // median lines so turned round gave Acuguay's sea to Sintaguay and Sintaguay's to Acuguay.
        // A piece of a length to have a direction and still disagreeing is counted.
        private (int Left, int Right) GetSides(List<(int Piece, bool IsReversed)> chain)
        {
            Dictionary<(int Left, int Right), double> lengths = [];
            foreach ((int link, bool isReversed) in chain)
            {
                Piece piece = this.pieces[link];
                (int Left, int Right) sides = isReversed ? (piece.Right, piece.Left) : (piece.Left, piece.Right);
                lengths[sides] = lengths.GetValueOrDefault(sides) + GetAngle(piece.Points);
            }

            (int Left, int Right) best = lengths.MaxBy(pair => pair.Value).Key;
            foreach ((int link, bool isReversed) in chain)
            {
                Piece piece = this.pieces[link];
                (int Left, int Right) sides = isReversed ? (piece.Right, piece.Left) : (piece.Left, piece.Right);
                if ((sides != best) && (GetAngle(piece.Points) >= MinimumSeparation))
                {
                    ++this.Inconsistent;
                }
            }

            return best;
        }

        // The pieces that continue a chain past one end of a piece, in the order they follow on
        // from it, each with whether it is turned round to run away from it. Stops at a key where
        // anything other than two pieces of the same kind meet, and at a piece already taken, which
        // is how a loop ends.
        private List<(int Piece, bool IsReversed)> Walk(int index, long key, bool[] visited)
        {
            List<(int Piece, bool IsReversed)> stretches = [];
            int current = index;
            while (this.incident.TryGetValue(key, out List<int>? at)
                && (at.Count == 2)
                && IsSameKind(this.pieces[at[0]], this.pieces[at[1]]))
            {
                int next = at[0] == current ? at[1] : at[0];
                if (visited[next])
                {
                    break;
                }

                visited[next] = true;
                Piece piece = this.pieces[next];
                bool isReversed = piece.Start != key;
                stretches.Add((next, isReversed));
                key = isReversed ? piece.Start : piece.End;
                current = next;
            }

            return stretches;
        }
    }

    // The runs over water of every boundary handed to it, as arcs of the water graph. Ends that meet
    // are one node, found by their coordinate in the source map: two boundaries that meet share the
    // point on the sphere they meet at, and so the coordinate it projects to.
    private sealed class WaterCut(
        ArcGraph water,
        ShoreIndex shore,
        Dictionary<(Country First, Country Second), List<Node>> meetings)
    {
        private readonly Dictionary<Coordinate, Node> ends = [];

        public int Anchored { get; private set; }

        public Dictionary<Arc, ZoneBoundary> Boundaries { get; } = [];

        public double Furthest { get; private set; }

        public int Grazing { get; private set; }

        public int Reached { get; private set; }

        public int Wavering { get; private set; }

        public int Skipped { get; private set; }

        public List<string> Loose { get; } = [];

        public int OverLand { get; private set; }

        public int Tested { get; private set; }

        public void Add(ZoneBoundary boundary, Coordinate[][] lines)
        {
            bool any = false;
            foreach (Coordinate[] line in lines.Where(line => line.Length >= 2))
            {
                (bool[] wet, bool[] grazed) = this.Classify(line);
                int index = 0;
                while (index < line.Length)
                {
                    if (!wet[index])
                    {
                        ++index;
                        continue;
                    }

                    int first = index++;
                    while ((index < line.Length) && wet[index] && !grazed[index])
                    {
                        ++index;
                    }

                    any = true;
                    this.AddRun(boundary, line, first, index - 1);
                }
            }

            if (!any)
            {
                ++this.OverLand;
            }
        }

        // The run from first to last, both over water. Each end is the line's own end, if the run
        // reaches it, and otherwise the node the line crosses the shore at.
        private void AddRun(ZoneBoundary boundary, Coordinate[] line, int first, int last)
        {
            (Node start, int from) = first == 0
                ? (this.GetEnd(line[0]), 1)
                : this.Anchor(boundary, line[first - 1], line[first], first, first + 1);
            (Node end, int to) = last == (line.Length - 1)
                ? (this.GetEnd(line[^1]), last - 1)
                : this.Anchor(boundary, line[last + 1], line[last], last, last - 1);

            // A node fixed in place of where the line crossed the shore is not quite where it
            // crossed, and the straight way from it to the first point over water can cut across a
            // hook of the shore beside it: 78 cm of coast, once, on this map. The arc starts from
            // the first point the node can reach without crossing the shore.
            while ((first > 0) && (from <= to) && shore.IsCrossedApartFrom(start.Coordinate, line[from]))
            {
                ++from;
                ++this.Skipped;
            }

            while ((last < (line.Length - 1)) && (to >= from) && shore.IsCrossedApartFrom(end.Coordinate, line[to]))
            {
                --to;
                ++this.Skipped;
            }

            Coordinate[] interior = to >= from ? line[from..(to + 1)] : [];
            if ((start == end) && ((interior.Length < 2) || ((first > 0) && (last < (line.Length - 1)))))
            {
                // Out from a node over the water and back to the same node: the median wavering
                // across the shore near the node it leaves from, not a boundary of anything.
                ++this.Wavering;
                return;
            }

            this.Boundaries.Add(
                water.AddArc(boundary.Kind, start, end, water.CoordinateSequenceFactory.Create(interior)),
                boundary);
        }

        // The node at which the line crosses the shore between a point over land and the next over
        // water, given with the index the arc's interior starts or ends at. Failing a node close
        // enough, the point over water is the arc's end and is left loose, and the interior starts
        // or ends past it.
        private (Node Node, int Index) Anchor(
            ZoneBoundary boundary,
            Coordinate outside,
            Coordinate inside,
            int anchored,
            int loose)
        {
            Node? best = null;
            double distance = double.MaxValue;
            if ((boundary.Second != null)
                && meetings.TryGetValue(GetPair(boundary.First, boundary.Second), out List<Node>? nodes))
            {
                foreach (Node node in nodes)
                {
                    double candidate = DistanceComputer.PointToSegment(node.Coordinate, outside, inside);
                    if (candidate < distance)
                    {
                        (best, distance) = (node, candidate);
                    }
                }
            }

            if ((best != null) && (distance <= AnchorTolerance))
            {
                ++this.Anchored;
                this.Furthest = Math.Max(this.Furthest, distance);
                return (best, anchored);
            }

            if ((best != null)
                && (best.Coordinate.Distance(inside) <= ReachTolerance)
                && !shore.IsCrossedApartFrom(best.Coordinate, inside))
            {
                ++this.Reached;
                return (best, anchored);
            }

            string where = string.Create(
                CultureInfo.InvariantCulture,
                $"{boundary.Kind} {boundary.First.Id}/{boundary.Second?.Id} at {inside.X:F2}, {inside.Y:F2}");
            this.Loose.Add(where + string.Create(CultureInfo.InvariantCulture, $", nearest node {distance:G3}"));
            return (this.GetEnd(inside), loose);
        }

        // Which points of the line lie over water, and which segments - each marked at the point it
        // ends at - cross the shore with water at both ends. Such a segment has passed over land on
        // the way, across a corner of the shore, and the run of water is cut there: at Orlamsquab and
        // Natalia, where the two coasts leave their node 2 cm apart and one of them zigzags back on
        // itself, the median's last segment cut across the zigzag twice and was joined to the node
        // through the land.
        private (bool[] Wet, bool[] Grazed) Classify(Coordinate[] line)
        {
            bool[] wet = new bool[line.Length];
            bool[] grazed = new bool[line.Length];
            wet[0] = shore.IsOverWater(line[0]);
            ++this.Tested;
            for (int index = 1; index < line.Length; ++index)
            {
                if (!shore.IsCrossed(line[index - 1], line[index]))
                {
                    wet[index] = wet[index - 1];
                    continue;
                }

                wet[index] = shore.IsOverWater(line[index]);
                ++this.Tested;
                if (wet[index] && wet[index - 1])
                {
                    grazed[index] = true;
                    ++this.Grazing;
                }
            }

            return (wet, grazed);
        }

        private Node GetEnd(Coordinate coordinate)
        {
            if (!this.ends.TryGetValue(coordinate, out Node? node))
            {
                node = water.AddNode(coordinate);
                this.ends.Add(coordinate, node);
            }

            return node;
        }
    }

    // A shore as the samples are put on it: its length along the sphere, and a point any distance
    // along it found on the segment of the source map that distance falls in.
    private sealed class ShoreLine
    {
        private readonly double[] cumulative;

        private readonly Coordinate[] drawn;

        private readonly SphereVector[] line;

        private readonly double longitudeOffset;

        private readonly IProjectionService projectionService;

        private readonly Envelope sourceEnvelope;

        private readonly List<double> positions = [];

        public ShoreLine(IProjectionService projectionService, Envelope sourceEnvelope, Shore shore, int label)
        {
            this.Arc = shore.Arc;
            this.Label = label;
            this.Owner = shore.Owner;
            this.projectionService = projectionService;
            this.sourceEnvelope = sourceEnvelope;
            this.longitudeOffset = shore.LongitudeOffset;
            this.drawn = shore.Arc.GetFullCoordinates();
            this.line = [.. shore.Coordinates.Select(SphereVector.FromGeographic)];
            this.cumulative = new double[this.line.Length];
            for (int index = 1; index < this.line.Length; ++index)
            {
                this.cumulative[index] = this.cumulative[index - 1] + this.line[index - 1].AngleTo(this.line[index]);
            }
        }

        public Arc Arc { get; }

        public int Label { get; }

        public Country Owner { get; }

        public double Length => this.cumulative[^1];

        // The distance along the shore between a sample and the next sample either side of it, the
        // larger of the two, a node counting as a sample.
        public double GetGap(double position)
        {
            int index = this.positions.BinarySearch(position);
            index = index < 0 ? ~index : index;
            double before = this.Arc.IsClosed ? this.positions[^1] - this.Length : 0.0;
            if (index > 0)
            {
                before = this.positions[index - 1];
            }

            int next = (index < this.positions.Count) && this.positions[index].Equals(position) ? index + 1 : index;
            double after = this.Arc.IsClosed ? this.positions[0] + this.Length : this.Length;
            if (next < this.positions.Count)
            {
                after = this.positions[next];
            }

            return Math.Max(position - before, after - position);
        }

        public double GetDistanceToNearest(double position)
        {
            int index = this.positions.BinarySearch(position);
            if (index >= 0)
            {
                return 0.0;
            }

            index = ~index;
            double distance = double.MaxValue;
            if (index > 0)
            {
                distance = position - this.positions[index - 1];
            }

            if (index < this.positions.Count)
            {
                distance = Math.Min(distance, this.positions[index] - position);
            }

            return distance;
        }

        // How far each vertex of the shore lies from the node the shore leaves, in a straight line,
        // for the vertices less than the limit along it from that node.
        public IEnumerable<double> GetCorners(bool fromEnd, double limit)
        {
            SphereVector node = fromEnd ? this.line[^1] : this.line[0];
            for (int step = 1; step < (this.line.Length - 1); ++step)
            {
                int index = fromEnd ? this.line.Length - 1 - step : step;
                double along = fromEnd ? this.Length - this.cumulative[index] : this.cumulative[index];
                if (along >= limit)
                {
                    yield break;
                }

                yield return node.AngleTo(this.line[index]);
            }
        }

        // How far along the shore a point is from each of its two nodes.
        public IEnumerable<(Node Node, double Along)> GetNodeDistances(double position)
        {
            yield return (this.Arc.StartNode, position);
            yield return (this.Arc.EndNode, this.Length - position);
        }

        public void Insert(double position)
        {
            int index = this.positions.BinarySearch(position);
            this.positions.Insert(index < 0 ? ~index : index, position);
        }

        // A position sampled before the shore is sorted; see Sort.
        public void Remember(double position)
        {
            this.positions.Add(position);
        }

        public void Sort()
        {
            this.positions.Sort();
        }

        // The point that far along the shore, as an angle. The position is remembered by Sample and
        // Insert, not here.
        public SphereVector Locate(double position)
        {
            int segment = Array.BinarySearch(this.cumulative, position);
            segment = Math.Clamp(segment < 0 ? ~segment - 1 : segment, 0, this.drawn.Length - 2);
            double length = this.cumulative[segment + 1] - this.cumulative[segment];
            double fraction = length > 0.0 ? Math.Clamp((position - this.cumulative[segment]) / length, 0.0, 1.0) : 0.0;
            Coordinate point = new Coordinate(
                this.drawn[segment].X + (fraction * (this.drawn[segment + 1].X - this.drawn[segment].X)),
                this.drawn[segment].Y + (fraction * (this.drawn[segment + 1].Y - this.drawn[segment].Y)));
            Coordinate geographic = this.projectionService.Unproject(
                this.projectionService.Rescale(point, this.sourceEnvelope));

            return SphereVector.FromGeographic(new Coordinate(geographic.X + this.longitudeOffset, geographic.Y));
        }

        // The point on the shore exactly that far from its first node in a straight line on the
        // sphere, rather than that far along it. Two samples the same distance from a node have their
        // bisector through it, and the distance along a shore is the distance from its node only
        // where the shore runs straight and the projection's scale is the same either side, so the
        // position along it is corrected until the two agree to a part in a million.
        public (SphereVector Point, double Position) LocateFromStart(double distance)
        {
            return this.LocateAt(distance, this.line[0], position => position);
        }

        public (SphereVector Point, double Position) LocateFromEnd(double distance)
        {
            return this.LocateAt(distance, this.line[^1], position => this.Length - position);
        }

        private (SphereVector Point, double Position) LocateAt(
            double distance,
            SphereVector node,
            Func<double, double> toPosition)
        {
            double along = distance;
            SphereVector point = this.Locate(toPosition(along));
            for (int iteration = 0; iteration < MaximumLocateIterations; ++iteration)
            {
                double reached = node.AngleTo(point);
                if ((reached <= 0.0) || (Math.Abs(reached - distance) <= (distance * 1e-6)))
                {
                    break;
                }

                along = Math.Min(along * (distance / reached), this.Length / 2.0);
                point = this.Locate(toPosition(along));
            }

            this.positions.Add(toPosition(along));

            return (point, toPosition(along));
        }
    }

    // The points of the triangulation and what each is: the country it belongs to, -1 for a
    // sentinel, and the shore and the position along it it was taken at.
    private sealed class Samples
    {
        public List<int> Labels { get; } = [];

        public List<SphereVector> Points { get; } = [];

        public List<double> Positions { get; } = [];

        public List<int> Shores { get; } = [];

        public void Add(SphereVector point, int label, int shore, double position)
        {
            this.Points.Add(point);
            this.Labels.Add(label);
            this.Shores.Add(shore);
            this.Positions.Add(position);
        }
    }

    // Every point projected stereographically from the pole and triangulated in the plane, and
    // the triangles given back as the indexes of their points. A point that lands exactly on
    // another is one site to the triangulation and is left out. Points can be added after the
    // first, which the refinement does, and go into the same subdivision.
    //
    // The sites go in along a Hilbert curve, not as DelaunayTriangulationBuilder would put them,
    // which is sorted by x. Each site is found by walking from the edge the last one left behind,
    // and in order of x two sites in a row lie a strip's height apart: on this map the walk was
    // what the time went on, and doubling the spacing cut it to a quarter rather than a half.
    // Along the curve the next site is nearly always beside the last, and the walk is a step or
    // two. Measured on the 22 October export, 1.3 million sites: 47.9 s in order of x and 13.0 s
    // along the curve, and the zone boundaries written from either are byte-identical.
    private sealed class Triangulation
    {
        private readonly SphereVector east;

        private readonly Envelope envelope;

        private readonly Dictionary<(double X, double Y), int> indexes = [];

        private readonly SphereVector north;

        private readonly SphereVector pole;

        private readonly QuadEdgeSubdivision subdivision;

        public Triangulation(List<SphereVector> points, SphereVector pole)
        {
            this.pole = pole;
            (this.east, this.north) = GetTangents(pole);
            this.envelope = new Envelope();
            foreach (SphereVector point in points)
            {
                (double x, double y) = this.Project(point);
                this.envelope.ExpandToInclude(x, y);
            }

            this.subdivision = new QuadEdgeSubdivision(this.envelope, 0.0);
            this.Insert(points, 0);
        }

        public int Lost { get; private set; }

        public List<int[]> GetTriangles()
        {
            List<int[]> triangles = [];
            foreach (Vertex[] vertices in this.subdivision.GetTriangleVertices(false))
            {
                triangles.Add([.. vertices.Select(vertex => this.indexes[(vertex.X, vertex.Y)])]);
            }

            return triangles;
        }

        public void Insert(List<SphereVector> points, int from)
        {
            List<(double X, double Y)> sites = [];
            for (int index = from; index < points.Count; ++index)
            {
                (double X, double Y) site = this.Project(points[index]);
                if (!this.indexes.TryAdd(site, index))
                {
                    ++this.Lost;
                    continue;
                }

                sites.Add(site);
            }

            double scale = (HilbertSide - 1) / Math.Max(this.envelope.Width, this.envelope.Height);
            Vertex[] ordered =
            [
                .. sites.Select(
                        site => (Site: site,
                            Index: GetHilbertIndex(
                                Math.Clamp((long)((site.X - this.envelope.MinX) * scale), 0, HilbertSide - 1),
                                Math.Clamp((long)((site.Y - this.envelope.MinY) * scale), 0, HilbertSide - 1))))
                    .OrderBy(pair => pair.Index)
                    .ThenBy(pair => this.indexes[pair.Site])
                    .Select(pair => new Vertex(pair.Site.X, pair.Site.Y)),
            ];

            new IncrementalDelaunayTriangulator(this.subdivision).InsertSites(ordered);
        }

        // Where a point lies along a Hilbert curve through the square, for a point given as its cell on
        // a grid of HilbertSide by HilbertSide. Points close along the curve are close in the plane.
        private static long GetHilbertIndex(long x, long y)
        {
            long index = 0;
            for (long side = HilbertSide / 2; side > 0; side /= 2)
            {
                long right = (x & side) > 0 ? 1 : 0;
                long up = (y & side) > 0 ? 1 : 0;
                index += side * side * ((3 * right) ^ up);
                if (up == 0)
                {
                    if (right == 1)
                    {
                        x = HilbertSide - 1 - x;
                        y = HilbertSide - 1 - y;
                    }

                    (x, y) = (y, x);
                }
            }

            return index;
        }

        private (double X, double Y) Project(SphereVector point)
        {
            double denominator = 1.0 - point.Dot(this.pole);

            return (point.Dot(this.east) / denominator, point.Dot(this.north) / denominator);
        }
    }

    // A stretch of boundary between two keyed points. First and Second are the two countries in
    // order, for telling one boundary from another; Left and Right are the same two as the piece
    // runs, seen from outside the sphere, -1 being the high seas.
    private sealed record Piece(
        ArcKind Kind,
        int First,
        int Second,
        int Left,
        int Right,
        long Start,
        long End,
        List<SphereVector> Points);
}
