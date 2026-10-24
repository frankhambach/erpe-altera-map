// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;
using NetTopologySuite.Triangulate;
using NetTopologySuite.Triangulate.QuadEdge;

using Serilog;

// E1 to E3: the shores sampled at a fixed spacing, the Voronoi diagram of the samples on the
// sphere, and from it the two kinds of line that bound the zones. A median line is a Voronoi edge
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

    public IReadOnlyList<ZoneBoundary> GetBoundaries(IEnumerable<Shore> shores)
    {
        ArgumentNullException.ThrowIfNull(shores);

        Stopwatch stopwatch = Stopwatch.StartNew();
        List<Country> owners = [];
        List<SphereVector> points = [];
        List<int> labels = [];
        double length = Sample(shores, owners, points, labels);
        int sampleCount = points.Count;
        Log.Information(
            "E1: sampled {Length:F0} km of coast and lake shore every {Spacing} km, {Samples} points for {Countries} "
            + "countries, in {Elapsed} ms.",
            length,
            SampleSpacing,
            sampleCount,
            owners.Count,
            stopwatch.ElapsedMilliseconds);

        double reach = ZoneRadius / EarthRadius;
        (SphereVector pole, double clearance) = FindPole(points);
        AddSentinels(points, labels, pole, clearance, reach);
        Coordinate centre = pole.ToGeographic();
        Log.Information(
            "E2: projecting from {Longitude:F2}, {Latitude:F2}, {Clearance:F0} km from the nearest shore.",
            centre.X,
            centre.Y,
            clearance * EarthRadius);

        stopwatch.Restart();
        (List<int[]> triangles, int lost) = Triangulate(points, pole);
        SphereVector[] centres =
        [
            .. triangles.Select(triangle => GetCircumcentre(points[triangle[0]], points[triangle[1]], points[triangle[2]])),
        ];
        Dictionary<long, (int First, int Second)> edges = GetEdges(triangles, points.Count);
        Log.Information(
            "E2: triangulated {Points} points into {Triangles} triangles and {Edges} edges in {Elapsed} ms; {Lost} "
            + "points coincided with another and were left out.",
            points.Count,
            triangles.Count,
            edges.Count,
            stopwatch.ElapsedMilliseconds,
            lost);

        stopwatch.Restart();
        Diagram diagram = new Diagram(points, labels, sampleCount, centres, reach);
        diagram.AddMedians(edges);
        diagram.AddLimits(GetNeighbours(edges, points.Count));
        List<ZoneBoundary> boundaries = diagram.Chain(owners);
        Log.Information(
            "E3: {Medians} median lines of {MedianLength:F0} km and {Limits} stretches of limit of {LimitLength:F0} km, "
            + "meeting at {Junctions} points where three zones or more meet, in {Elapsed} ms.",
            boundaries.Count(boundary => boundary.Kind == ArcKind.Median),
            boundaries.Where(boundary => boundary.Kind == ArcKind.Median).Sum(GetLength),
            boundaries.Count(boundary => boundary.Kind == ArcKind.Limit),
            boundaries.Where(boundary => boundary.Kind == ArcKind.Limit).Sum(GetLength),
            diagram.CountJunctions(),
            stopwatch.ElapsedMilliseconds);
        if ((diagram.Unpaired > 0) || (diagram.Wide > 0))
        {
            Log.Warning(
                "E3: {Unpaired} edges within reach had a triangle on one side only and {Wide} spanned half the "
                + "sphere; both were left out.",
                diagram.Unpaired,
                diagram.Wide);
        }

        return boundaries;
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
    private static void AddSentinels(
        List<SphereVector> points,
        List<int> labels,
        SphereVector pole,
        double clearance,
        double reach)
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
            points.Add(
                pole.Scale(Math.Cos(radius))
                    .Add(east.Scale(Math.Cos(azimuth) * Math.Sin(radius)))
                    .Add(north.Scale(Math.Sin(azimuth) * Math.Sin(radius))));
            labels.Add(-1);
        }
    }

    // The point of a Fibonacci lattice furthest from every shore, measured against every so many
    // samples, and then its clearance measured against all of them.
    private static (SphereVector Pole, double Clearance) FindPole(List<SphereVector> points)
    {
        int stride = Math.Max(1, points.Count / CandidateSampleCount);
        double goldenAngle = Math.PI * (3.0 - Math.Sqrt(5.0));
        SphereVector best = default;
        double bestNearest = double.MaxValue;
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

            if (nearest < bestNearest)
            {
                bestNearest = nearest;
                best = candidate;
            }
        }

        return (best, points.Min(best.AngleTo));
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

    // Each shore's full length, from node to node, divided evenly into as many stretches as the
    // spacing asks for, and a sample put at every division. Not at the nodes of an open arc: a node
    // where a border meets the shore belongs to the countries either side of it both, and two
    // samples in one place are one point to the triangulation. An arc that closes on itself is an
    // island or a lake with nothing else on its shore, and its node is sampled like any other
    // point. Every shore gets one sample at least and every island three, however short, so that
    // no islet goes without a zone.
    private static double Sample(
        IEnumerable<Shore> shores,
        List<Country> owners,
        List<SphereVector> points,
        List<int> labels)
    {
        Dictionary<Country, int> indexes = [];
        double total = 0.0;
        foreach (Shore shore in shores)
        {
            if (!indexes.TryGetValue(shore.Owner, out int label))
            {
                label = owners.Count;
                owners.Add(shore.Owner);
                indexes.Add(shore.Owner, label);
            }

            SphereVector[] line = [.. shore.Coordinates.Select(SphereVector.FromGeographic)];
            double[] lengths = [.. line.Skip(1).Select((point, index) => line[index].AngleTo(point))];
            double angle = lengths.Sum();
            if (angle <= 0.0)
            {
                continue;
            }

            total += angle;
            bool isClosed = shore.Arc.IsClosed;
            int count = Math.Max(isClosed ? 3 : 2, (int)Math.Ceiling((angle * EarthRadius) / SampleSpacing));
            int segment = 0;
            double passed = 0.0;
            for (int division = isClosed ? 0 : 1; division < count; ++division)
            {
                double position = (angle * division) / count;
                while ((segment < (lengths.Length - 1)) && ((passed + lengths[segment]) < position))
                {
                    passed += lengths[segment];
                    ++segment;
                }

                double fraction = lengths[segment] > 0.0 ? (position - passed) / lengths[segment] : 0.0;
                points.Add(SphereVector.Interpolate(line[segment], line[segment + 1], Math.Clamp(fraction, 0.0, 1.0)));
                labels.Add(label);
            }
        }

        return total * EarthRadius;
    }

    // Every point projected stereographically from the pole and triangulated in the plane, and
    // the triangles given back as the indexes of their points. A point that lands exactly on
    // another is one site to the triangulation and is left out.
    private static (List<int[]> Triangles, int Lost) Triangulate(List<SphereVector> points, SphereVector pole)
    {
        (SphereVector east, SphereVector north) = GetTangents(pole);
        Dictionary<(double X, double Y), int> indexes = [];
        List<Coordinate> sites = [];
        int lost = 0;
        for (int index = 0; index < points.Count; ++index)
        {
            SphereVector point = points[index];
            double denominator = 1.0 - point.Dot(pole);
            (double X, double Y) site = (point.Dot(east) / denominator, point.Dot(north) / denominator);
            if (!indexes.TryAdd(site, index))
            {
                ++lost;
                continue;
            }

            sites.Add(new Coordinate(site.X, site.Y));
        }

        DelaunayTriangulationBuilder builder = new DelaunayTriangulationBuilder();
        builder.SetSites(sites);

        List<int[]> triangles = [];
        foreach (Vertex[] vertices in builder.GetSubdivision()
                     .GetTriangleVertices(false))
        {
            triangles.Add([.. vertices.Select(vertex => indexes[(vertex.X, vertex.Y)])]);
        }

        return (triangles, lost);
    }

    // The Voronoi diagram of the samples, cut down to the limit, as pieces between keyed points.
    // A key at zero or above is a triangle, whose circumcentre is a vertex of the diagram; a key
    // below zero is where an edge of the diagram crosses the limit. The pieces are chained into
    // boundaries at the end, through every key that two pieces of the same kind meet at and
    // nothing else does.
    private sealed class Diagram(
        List<SphereVector> points,
        List<int> labels,
        int sampleCount,
        SphereVector[] centres,
        double reach)
    {
        private readonly Dictionary<int, List<(long Key, SphereVector Point)>> crossings = [];

        private readonly Dictionary<long, List<int>> incident = [];

        private readonly Dictionary<long, SphereVector> keyed = [];

        private readonly List<Piece> pieces = [];

        private long nextKey = -1;

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
                if ((one >= sampleCount) || (other >= sampleCount))
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
                    this.Add(
                        new Piece(
                            ArcKind.Median,
                            Math.Min(labels[one], labels[other]),
                            Math.Max(labels[one], labels[other]),
                            start,
                            end,
                            [this.GetPoint(start), this.GetPoint(end)]));
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
            for (int sample = 0; sample < sampleCount; ++sample)
            {
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
                    this.Add(new Piece(ArcKind.Limit, labels[sample], -1, start.Key, end.Key, line));
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
                List<SphereVector> line = [.. piece.Points];
                foreach (SphereVector[] after in this.Walk(index, piece.End, visited))
                {
                    line.AddRange(after.Skip(1));
                }

                List<SphereVector[]> before = this.Walk(index, piece.Start, visited);
                before.Reverse();
                List<SphereVector> prefix = [];
                foreach (SphereVector[] stretch in before)
                {
                    prefix.AddRange(stretch.AsEnumerable().Reverse().SkipLast(1));
                }

                boundaries.Add(
                    new ZoneBoundary(
                        piece.Kind,
                        owners[piece.First],
                        piece.Second < 0 ? null : owners[piece.Second],
                        [.. prefix.Concat(line).Select(point => point.ToGeographic())]));
            }

            return boundaries;
        }

        public int CountJunctions()
        {
            return this.incident.Values.Count(
                at => (at.Count >= 3) && at.TrueForAll(index => this.pieces[index].Kind == ArcKind.Median));
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

        // The pieces that continue a chain past one end of a piece, in the order they follow on
        // from it, each turned to run away from it. Stops at a key where anything other than two
        // pieces of the same kind meet, and at a piece already taken, which is how a loop ends.
        private List<SphereVector[]> Walk(int index, long key, bool[] visited)
        {
            List<SphereVector[]> stretches = [];
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
                stretches.Add(isReversed ? [.. piece.Points.AsEnumerable().Reverse()] : [.. piece.Points]);
                key = isReversed ? piece.Start : piece.End;
                current = next;
            }

            return stretches;
        }
    }

    private sealed record Piece(ArcKind Kind, int First, int Second, long Start, long End, List<SphereVector> Points);
}
