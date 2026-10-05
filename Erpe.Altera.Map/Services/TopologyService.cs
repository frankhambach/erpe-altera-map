// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Simplify;

using Serilog;

public sealed class TopologyService : ITopologyService
{
    // How far the faces may fall short of the coastline before a face has failed to close.
    // It is not zero because the signed area of a shell that crosses itself understates the
    // ground it covers, and four of them do.
    //
    // This fires as it stands, at 0.382%, and it is meant to: that is Maharath, Sanjan,
    // Siam and a few more, whose whole territory is inside no face. The figure to watch
    // after that is fixed is whatever the four self-crossing shells leave behind.
    private const double MaximumAreaShortfall = 0.001;

    private readonly GeometryFactory geometryFactory;

    public TopologyService(GeometryFactory geometryFactory)
    {
        this.geometryFactory = geometryFactory;
    }

    // Turns the welded arc graph into faces. Faces are traced through the graph rather
    // than rebuilt with Polygonizer, because polygonizing returns fresh geometry and the
    // arcs would have to be recovered afterwards by comparing coordinates. Tracing keeps
    // every face referring to the very same Arc objects, which is what makes neighbouring
    // countries share a border exactly instead of merely agreeing to within rounding.
    public void BuildFaces(ArcGraph arcGraph, double enclosedArea)
    {
        ArgumentNullException.ThrowIfNull(arcGraph);

        int crossings = this.NodeCrossings(arcGraph);

        List<(DirectedArc[] Ring, double SignedArea)> rings = TraceRings(arcGraph);
        int traced = rings.Sum(ring => ring.Ring.Length);
        if (traced != (arcGraph.Arcs.Count * 2))
        {
            Log.Warning(
                "Traced {Traced} half-edges into rings but the graph has {Expected}; the faces do not tile.",
                traced,
                arcGraph.Arcs.Count * 2);
        }

        (DirectedArc[] Ring, double SignedArea)[] shells = rings.Where(ring => ring.SignedArea > 0.0).ToArray();
        (DirectedArc[] Ring, double SignedArea)[] outlines = rings.Where(ring => ring.SignedArea <= 0.0).ToArray();

        int unenclosed = this.AddFaces(arcGraph, shells, outlines);

        // Faces tile the landmass when their areas, less the holes they carry, come to the
        // area the coastline enclosed. The total runs over by the area of any lake that a
        // border crosses: splitting the lake's ring means the land around it routes through
        // the lake instead of carrying it as a hole, so the lake's halves become faces of
        // their own. A total running under instead means a face failed to close.
        double area = arcGraph.Faces.Sum(
            face => GetSignedArea(face.Shell) - face.Holes.Sum(hole => Math.Abs(GetSignedArea(hole))));

        Log.Information(
            "Noded {Crossings} crossings and traced {ShellCount} faces with {HoleCount} holes, covering "
            + "{Area:0.0} of the {Enclosed:0.0} square units the coastline encloses. {Unenclosed} outlines "
            + "bound no face and belong to the ocean.",
            crossings,
            shells.Length,
            outlines.Length - unenclosed,
            area,
            enclosedArea,
            unenclosed);

        // The comment above has been there since this was written and nothing ever checked
        // it, which is how 0.382% of the land went missing without a word: a region whose
        // boundary is walked into the outline of the landmass around it never becomes a
        // face, and its ground belongs to nobody afterwards.
        // An expectation of zero or less means the caller has nothing to compare against,
        // which is how the tests build faces from a handful of rings without a coastline.
        double shortfall = enclosedArea - area;
        if ((enclosedArea > 0.0) && (shortfall > (enclosedArea * MaximumAreaShortfall)))
        {
            Log.Error(
                "The faces come to {Shortfall:0.0} square units less than the coastline encloses, "
                + "{Share:0.000%} of it. That much land is inside no face at all, so no country can be given "
                + "it: a boundary somewhere is being walked into the outline of the landmass instead of into a "
                + "face of its own.",
                shortfall,
                shortfall / enclosedArea);
        }
    }

    public LinearRing Close(CoordinateSequence coordinateSequence)
    {
        ArgumentNullException.ThrowIfNull(coordinateSequence);

        return this.geometryFactory.CreateLinearRing(
            coordinateSequence.GetCoordinate(0).Equals2D(coordinateSequence.GetCoordinate(coordinateSequence.Count - 1))
                ? coordinateSequence.ToCoordinateArray()
                : coordinateSequence.ToCoordinateArray().Append(coordinateSequence.GetCoordinateCopy(0)).ToArray());
    }

    public IEnumerable<Polygon> Polygonize(IReadOnlyCollection<LinearRing> linearRings)
    {
        Dictionary<LinearRing, LinearRing?> smallestEnclosingLinearRings = new Dictionary<LinearRing, LinearRing?>();

        FindSmallestEnclosingLinearRings(linearRings, smallestEnclosingLinearRings);
        return smallestEnclosingLinearRings.Where(keyValuePair => keyValuePair.Value is null)
            .SelectMany(keyValuePair => this.CreatePolygons(keyValuePair.Key, smallestEnclosingLinearRings));
    }

    public LinearRing Unloop(LinearRing linearRing)
    {
        ArgumentNullException.ThrowIfNull(linearRing);

        Polygonizer polygonizer = new Polygonizer();
        LineString lineString = this.geometryFactory.CreateLineString(linearRing.CoordinateSequence);
        Point point = this.geometryFactory.CreatePoint(lineString.GetCoordinateN(0));
        polygonizer.Add(lineString.Union(point));
        Polygon result = polygonizer.GetPolygons().OfType<Polygon>().MaxBy(polygon => polygon.Area) ?? Polygon.Empty;
        return result.Shell;
    }

    // Walks the graph once, turning every half-edge into part of exactly one ring. Arriving
    // at a node along a half-edge, the ring continues along the next half-edge clockwise
    // from the way back, which keeps the enclosed area on the left throughout. Bounded
    // faces therefore come out anticlockwise with a positive area, and the outer boundary
    // of each separate piece of land comes out the other way round with a negative one.
    private static List<(DirectedArc[] Ring, double SignedArea)> TraceRings(ArcGraph arcGraph)
    {
        Dictionary<Node, DirectedArc[]> outgoing = BuildOutgoing(arcGraph);
        Dictionary<DirectedArc, int> positions = new Dictionary<DirectedArc, int>();
        foreach ((Node _, DirectedArc[] arcs) in outgoing)
        {
            for (int i = 0; i < arcs.Length; ++i)
            {
                positions[arcs[i]] = i;
            }
        }

        List<(DirectedArc[] Ring, double SignedArea)> rings = [];
        HashSet<DirectedArc> visited = new HashSet<DirectedArc>();
        foreach (Arc arc in arcGraph.Arcs)
        {
            foreach (DirectedArc start in new[]
                         {
                             new DirectedArc(arc, false), new DirectedArc(arc, true),
                         })
            {
                if (!visited.Add(start))
                {
                    continue;
                }

                List<DirectedArc> ring = [start];
                DirectedArc current = start;
                while (true)
                {
                    DirectedArc back = new DirectedArc(current.Arc, !current.IsReversed);
                    DirectedArc[] candidates = outgoing[GetEndNode(current)];
                    DirectedArc next = candidates[
                        ((positions[back] - 1) + candidates.Length) % candidates.Length];
                    if (next == start)
                    {
                        break;
                    }

                    if (!visited.Add(next))
                    {
                        // Every half-edge belongs to exactly one ring, so reaching one that
                        // is already spoken for means the traversal is wrong, not finished.
                        Log.Warning(
                            "Ring traversal reached an already visited half-edge on arc {ArcId}; "
                            + "the face around it will be incomplete.",
                            next.Arc.Id);
                        break;
                    }

                    ring.Add(next);
                    current = next;
                }

                rings.Add((ring.ToArray(), GetSignedArea(ring)));
            }
        }

        return rings;
    }

    // Outgoing half-edges at every node, sorted by the direction they leave in.
    private static Dictionary<Node, DirectedArc[]> BuildOutgoing(ArcGraph arcGraph)
    {
        Dictionary<Node, List<DirectedArc>> outgoing = new Dictionary<Node, List<DirectedArc>>();
        foreach (Arc arc in arcGraph.Arcs)
        {
            foreach (DirectedArc directed in new[]
                         {
                             new DirectedArc(arc, false), new DirectedArc(arc, true),
                         })
            {
                Node node = GetStartNode(directed);
                if (!outgoing.TryGetValue(node, out List<DirectedArc>? arcs))
                {
                    arcs = [];
                    outgoing.Add(node, arcs);
                }

                arcs.Add(directed);
            }
        }

        return outgoing.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OrderBy(GetDepartureAngle).ThenBy(directed => directed.Arc.Id).ToArray());
    }

    private static double GetDepartureAngle(DirectedArc directed)
    {
        Coordinate[] coordinates = GetCoordinates(directed);
        Coordinate start = coordinates[0];
        Coordinate next = coordinates.FirstOrDefault(
            coordinate => !coordinate.Equals2D(start),
            coordinates[^1]);
        return Math.Atan2(next.Y - start.Y, next.X - start.X);
    }

    private static Coordinate[] GetCoordinates(DirectedArc directed)
    {
        Coordinate[] coordinates = directed.Arc.GetFullCoordinates();
        if (directed.IsReversed)
        {
            Array.Reverse(coordinates);
        }

        return coordinates;
    }

    private static Node GetStartNode(DirectedArc directed)
    {
        return directed.IsReversed ? directed.Arc.EndNode : directed.Arc.StartNode;
    }

    private static Node GetEndNode(DirectedArc directed)
    {
        return directed.IsReversed ? directed.Arc.StartNode : directed.Arc.EndNode;
    }

    private static double GetSignedArea(IEnumerable<DirectedArc> ring)
    {
        double area = 0.0;
        Coordinate? previous = null;
        foreach (Coordinate coordinate in ring.SelectMany(GetCoordinates))
        {
            if (previous != null)
            {
                area += (previous.X * coordinate.Y) - (coordinate.X * previous.Y);
            }

            previous = coordinate;
        }

        return area / 2.0;
    }

    private static void FindSmallestEnclosingLinearRings(
        IReadOnlyCollection<LinearRing> linearRings,
        IDictionary<LinearRing, LinearRing?> smallestEnclosingLinearRings)
    {
        foreach (LinearRing linearRing in linearRings.Where(
                     linearRing => !smallestEnclosingLinearRings.ContainsKey(linearRing)))
        {
            LinearRing[] enclosingLinearRings = linearRings.Where(
                    enclosingLinearRing => (enclosingLinearRing != linearRing)
                        && new Polygon(enclosingLinearRing).Contains(new Polygon(linearRing)))
                .ToArray();
            switch (enclosingLinearRings.Length)
            {
                case 0:
                    smallestEnclosingLinearRings[linearRing] = null;
                    break;
                case 1:
                    smallestEnclosingLinearRings[linearRing] = enclosingLinearRings[0];
                    break;
                default:
                    FindSmallestEnclosingLinearRings(enclosingLinearRings, smallestEnclosingLinearRings);
                    smallestEnclosingLinearRings[linearRing] = enclosingLinearRings.Single(
                        enclosingLinearRing => !Array.Exists(
                            enclosingLinearRings,
                            otherEnclosingLinearRing => smallestEnclosingLinearRings.Contains(
                                new KeyValuePair<LinearRing, LinearRing?>(
                                    otherEnclosingLinearRing,
                                    enclosingLinearRing))));
                    break;
            }
        }
    }

    private static void Add(
        Dictionary<Arc, List<(int Index, double Parameter, Node Node)>> splits,
        Arc arc,
        int index,
        double parameter,
        Node node)
    {
        if (!splits.TryGetValue(arc, out List<(int Index, double Parameter, Node Node)>? list))
        {
            list = [];
            splits.Add(arc, list);
        }

        list.Add((index, parameter, node));
    }

    private static double GetParameter(Coordinate point, Coordinate start, Coordinate end)
    {
        double deltaX = end.X - start.X;
        double deltaY = end.Y - start.Y;
        double lengthSquared = (deltaX * deltaX) + (deltaY * deltaY);
        return lengthSquared < 1E-18
            ? 0.0
            : (((point.X - start.X) * deltaX) + ((point.Y - start.Y) * deltaY)) / lengthSquared;
    }

    private static void ApplySplits(
        ArcGraph arcGraph,
        Dictionary<Arc, List<(int Index, double Parameter, Node Node)>> splits)
    {
        Arc[] originals = arcGraph.Arcs.ToArray();
        arcGraph.Arcs.Clear();
        foreach (Arc arc in originals)
        {
            if (!splits.TryGetValue(arc, out List<(int Index, double Parameter, Node Node)>? arcSplits))
            {
                arcGraph.Arcs.Add(arc);
                continue;
            }

            Coordinate[] coordinates = arc.GetFullCoordinates();
            Node startNode = arc.StartNode;
            int start = 0;
            foreach ((int index, double _, Node node) in arcSplits.OrderBy(split => split.Index)
                         .ThenBy(split => split.Parameter))
            {
                arcGraph.AddArc(
                    arc.Kind,
                    startNode,
                    node,
                    arcGraph.CoordinateSequenceFactory.Create(
                        coordinates[(start + 1)..Math.Max(start + 1, index + 1)]));
                startNode = node;
                start = index;
            }

            arcGraph.AddArc(
                arc.Kind,
                startNode,
                arc.EndNode,
                arcGraph.CoordinateSequenceFactory.Create(coordinates[(start + 1)..^1]));
        }
    }

    // Splits arcs wherever two of them cross away from a node. A face traversal has no way
    // to represent such a crossing: the two arcs would each be traced as though the other
    // were not there, and the faces around the crossing would overlap.
    private int NodeCrossings(ArcGraph arcGraph)
    {
        STRtree<Segment> index = new STRtree<Segment>();
        foreach (Arc arc in arcGraph.Arcs)
        {
            Coordinate[] coordinates = arc.GetFullCoordinates();
            for (int i = 0; i < (coordinates.Length - 1); ++i)
            {
                index.Insert(new Envelope(coordinates[i], coordinates[i + 1]), new Segment(arc, coordinates, i));
            }
        }

        RobustLineIntersector intersector = new RobustLineIntersector();
        Dictionary<Arc, List<(int Index, double Parameter, Node Node)>> splits = [];
        foreach (Arc arc in arcGraph.Arcs)
        {
            Coordinate[] coordinates = arc.GetFullCoordinates();
            for (int i = 0; i < (coordinates.Length - 1); ++i)
            {
                foreach (Segment other in index.Query(new Envelope(coordinates[i], coordinates[i + 1])))
                {
                    // Each crossing is found twice, once from each side. Taking it only in
                    // one direction gives both arcs the same node rather than two. An arc
                    // crossing itself counts: it breaks a face traversal exactly as badly as
                    // two arcs crossing, so only the adjacent segment pair is skipped.
                    bool duplicate = other.Arc == arc
                        ? (other.Index <= (i + 1)) && (other.Index >= (i - 1))
                        : other.Arc.Id < arc.Id;
                    if (duplicate || ((other.Arc == arc) && (other.Index < i)))
                    {
                        continue;
                    }

                    intersector.ComputeIntersection(
                        coordinates[i],
                        coordinates[i + 1],
                        other.Coordinates[other.Index],
                        other.Coordinates[other.Index + 1]);
                    if (!intersector.IsInteriorIntersection())
                    {
                        continue;
                    }

                    Coordinate point = intersector.GetIntersection(0).Copy();
                    this.geometryFactory.PrecisionModel.MakePrecise(point);
                    Node node = arcGraph.AddNode(point);
                    Add(splits, arc, i, GetParameter(point, coordinates[i], coordinates[i + 1]), node);
                    Add(
                        splits,
                        other.Arc,
                        other.Index,
                        GetParameter(point, other.Coordinates[other.Index], other.Coordinates[other.Index + 1]),
                        node);
                }
            }
        }

        ApplySplits(arcGraph, splits);

        return splits.Values.Sum(list => list.Count) / 2;
    }

    // Each outline is the outside of one piece of land, so it belongs to whichever face
    // encloses that piece, as a hole. An outline enclosed by nothing is an island in the
    // open sea and belongs to the ocean.
    private int AddFaces(
        ArcGraph arcGraph,
        (DirectedArc[] Ring, double SignedArea)[] shells,
        (DirectedArc[] Ring, double SignedArea)[] outlines)
    {
        STRtree<int> index = new STRtree<int>();
        Polygon[] polygons = new Polygon[shells.Length];
        for (int i = 0; i < shells.Length; ++i)
        {
            polygons[i] = this.CreatePolygon(shells[i].Ring);
            index.Insert(polygons[i].EnvelopeInternal, i);
        }

        List<DirectedArc[]>[] holes = [.. shells.Select(_ => new List<DirectedArc[]>())];
        int unenclosed = 0;
        foreach ((DirectedArc[] ring, double _) in outlines)
        {
            // Strict containment, not Covers. An island's outline is its own shell traversed
            // the other way round, so the two share every vertex: Covers would report the
            // shell as enclosing its own outline and no island would ever reach the ocean.
            // Contains excludes that, while still holding for a lake outline lying inside
            // the island around it.
            Coordinate point = GetCoordinates(ring[0])[0];
            Point test = this.geometryFactory.CreatePoint(point);
            int best = -1;
            foreach (int i in index.Query(new Envelope(point)))
            {
                if (polygons[i].Contains(test) && ((best < 0) || (shells[i].SignedArea < shells[best].SignedArea)))
                {
                    best = i;
                }
            }

            if (best < 0)
            {
                ++unenclosed;
            }
            else
            {
                holes[best].Add(ring);
            }
        }

        for (int i = 0; i < shells.Length; ++i)
        {
            arcGraph.Faces.Add(
                new Face(
                    i,
                    [.. shells[i].Ring],
                    [.. holes[i].Select(hole => ImmutableArray.Create(hole))]));
        }

        return unenclosed;
    }

    private Polygon CreatePolygon(IEnumerable<DirectedArc> ring)
    {
        List<Coordinate> coordinates = [];
        foreach (Coordinate coordinate in ring.SelectMany(GetCoordinates))
        {
            if ((coordinates.Count == 0) || !coordinates[^1].Equals2D(coordinate))
            {
                coordinates.Add(coordinate);
            }
        }

        if (!coordinates[0].Equals2D(coordinates[^1]))
        {
            coordinates.Add(coordinates[0]);
        }

        return this.geometryFactory.CreatePolygon(coordinates.ToArray());
    }

    private IEnumerable<Polygon> CreatePolygons(
        LinearRing enclosingLinearRing,
        IDictionary<LinearRing, LinearRing?> smallestEnclosingLinearRings)
    {
        LinearRing[] enclosedLinearRings = smallestEnclosingLinearRings
            .Where(keyValuePair => keyValuePair.Value == enclosingLinearRing)
            .Select(keyValuePair => keyValuePair.Key)
            .ToArray();
        return new[] { this.geometryFactory.CreatePolygon(enclosingLinearRing, enclosedLinearRings) }.Concat(
            smallestEnclosingLinearRings
                .Where(
                    keyValuePair => Array.Exists(
                        enclosedLinearRings,
                        enclosedLinearRing => keyValuePair.Value == enclosedLinearRing))
                .SelectMany(keyValuePair => this.CreatePolygons(keyValuePair.Key, smallestEnclosingLinearRings)));
    }

    private readonly record struct Segment(Arc Arc, Coordinate[] Coordinates, int Index);
}
