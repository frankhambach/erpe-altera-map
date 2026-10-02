// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.OverlayNG;

using Serilog;

public sealed class CorrectionService : ICorrectionService
{
    // How far to look when reporting what an unresolved end nearly reached. Diagnostic
    // only: nothing is ever welded at this distance.
    private const double ReportingRadius = 50.0;

    private readonly GeometryFactory geometryFactory;

    public CorrectionService(GeometryFactory geometryFactory)
    {
        this.geometryFactory = geometryFactory;
    }

    public IEnumerable<Gap> FindGaps(IList<Country> countries)
    {
        IEnumerable<Polygon> world = ((GeometryCollection)UnaryUnionNG.Union(
            countries.SelectMany(country => country.Shapes).Select(shape => shape.Polygon).ToArray(),
            this.geometryFactory,
            this.geometryFactory.PrecisionModel)).OfType<Polygon>();

        IEnumerable<Polygon> gaps = FindHoles(world).Concat(FindOverlaps(countries));

        Geometry combinedGaps = UnaryUnionNG.Union(gaps, this.geometryFactory, this.geometryFactory.PrecisionModel);

        return ((GeometryCollection)combinedGaps).OfType<Polygon>()
            .Select(
                combinedIntersection => new Gap(
                    combinedIntersection.Shell,
                    [..FindCoasts(combinedIntersection.Shell, countries.SelectMany(country => country.Shapes))]));
    }

    // Attaches every loose arc end to whatever it was drawn to meet. The tolerance applies
    // to arc endpoints only and never to interior vertices: the linework interiors are not
    // defective, only the ends are, and the drawing's own resolution is coarser than the
    // tolerance, so snapping interiors would staircase genuine coastline.
    public void Weld(ArcGraph arcGraph, double weldTolerance)
    {
        ArgumentNullException.ThrowIfNull(arcGraph);

        int mergedCount = MergeLooseEnds(arcGraph, weldTolerance);

        // Built after merging, because merging moves nodes and so moves the arc ends with
        // them. Querying an index built before that would search stale positions.
        STRtree<Segment> index = BuildSegmentIndex(arcGraph);

        List<Split> splits = new List<Split>();
        List<Node> unresolved = new List<Node>();
        int coastCount = 0;
        foreach (Node node in GetLooseEnds(arcGraph))
        {
            Split? split = FindSplit(index, node, weldTolerance);
            if (split == null)
            {
                unresolved.Add(node);
                continue;
            }

            splits.Add(split.Value);
            if (split.Value.Arc.Kind == ArcKind.Coast)
            {
                ++coastCount;
            }
        }

        ApplySplits(arcGraph, splits);

        Log.Information(
            "Welded {MergedCount} ends onto each other and split {SplitCount} arcs to take the rest, "
            + "{CoastCount} of those on the coast. {UnresolvedCount} ends remain loose.",
            mergedCount,
            splits.Count,
            coastCount,
            unresolved.Count);

        // Reported rather than forced: an end that nothing lies near is a drawing problem,
        // and reaching further to find it would invent a border rather than find one. The
        // nearest arc beyond the tolerance is named so the report says how far short the
        // end fell and what it was probably meant to meet.
        foreach (Node node in unresolved)
        {
            Split? nearest = FindSplit(index, node, ReportingRadius);
            Log.Warning(
                "Arc end {NodeId} at {X:0.##}, {Y:0.##} found nothing within {Tolerance} units. "
                + "Nearest is {Kind} at {Distance:0.##} units.",
                node.Id,
                node.Coordinate.X,
                node.Coordinate.Y,
                weldTolerance,
                nearest?.Arc.Kind.ToString() ?? "nothing",
                nearest?.Distance ?? double.PositiveInfinity);
        }
    }

    public void Snap(IEnumerable<Coordinate> coordinates, double snapTolerance)
    {
        ArgumentNullException.ThrowIfNull(coordinates);

        Coordinate[] orderedCoordinates = coordinates
            .OrderBy(coordinate => coordinate.X)
            .ToArray();

        for (int index = 0; index < orderedCoordinates.Length; ++index)
        {
            int nextIndex = index + 1;
            while (nextIndex < orderedCoordinates.Length)
            {
                double deltaX = orderedCoordinates[nextIndex].X - orderedCoordinates[index].X;
                if (deltaX > snapTolerance)
                {
                    break;
                }

                double deltaY = orderedCoordinates[nextIndex].Y - orderedCoordinates[index].Y;
                if (((deltaX * deltaX) + (deltaY * deltaY)) <= (snapTolerance * snapTolerance))
                {
                    orderedCoordinates[nextIndex].X = orderedCoordinates[index].X;
                    orderedCoordinates[nextIndex].Y = orderedCoordinates[index].Y;
                    break;
                }

                ++nextIndex;
            }
        }
    }

    // An end is loose when exactly one arc end references it. A node referenced twice is
    // already a junction, or the root of a closed ring, and needs nothing.
    private static Node[] GetLooseEnds(ArcGraph arcGraph)
    {
        Dictionary<Node, int> degrees = new Dictionary<Node, int>();
        foreach (Arc arc in arcGraph.Arcs)
        {
            degrees[arc.StartNode] = degrees.GetValueOrDefault(arc.StartNode) + 1;
            degrees[arc.EndNode] = degrees.GetValueOrDefault(arc.EndNode) + 1;
        }

        return arcGraph.Nodes.Where(node => degrees.GetValueOrDefault(node) == 1).ToArray();
    }

    // Groups loose ends that lie within the tolerance of each other and collapses each
    // group to a single node at its centroid. Union-find rather than pairwise snapping
    // because proximity is not transitive: with A near B and B near C but A not near C,
    // pairwise snapping leaves the group half joined depending on the order it runs in.
    private static int MergeLooseEnds(ArcGraph arcGraph, double weldTolerance)
    {
        Node[] looseEnds = GetLooseEnds(arcGraph);
        Dictionary<Node, int> indexes = new Dictionary<Node, int>();
        STRtree<int> index = new STRtree<int>();
        for (int i = 0; i < looseEnds.Length; ++i)
        {
            indexes[looseEnds[i]] = i;
            index.Insert(new Envelope(looseEnds[i].Coordinate), i);
        }

        int[] parents = Enumerable.Range(0, looseEnds.Length).ToArray();
        for (int i = 0; i < looseEnds.Length; ++i)
        {
            Coordinate coordinate = looseEnds[i].Coordinate;
            Envelope envelope = new Envelope(coordinate);
            envelope.ExpandBy(weldTolerance);
            foreach (int j in index.Query(envelope))
            {
                if ((j > i) && (coordinate.Distance(looseEnds[j].Coordinate) <= weldTolerance))
                {
                    Union(parents, i, j);
                }
            }
        }

        Dictionary<int, List<Node>> groups = new Dictionary<int, List<Node>>();
        foreach ((Node node, int i) in indexes)
        {
            int root = Find(parents, i);
            if (!groups.TryGetValue(root, out List<Node>? group))
            {
                group = new List<Node>();
                groups.Add(root, group);
            }

            group.Add(node);
        }

        Dictionary<Node, Node> replacements = new Dictionary<Node, Node>();
        int mergedCount = 0;
        foreach (List<Node> group in groups.Values.Where(group => group.Count > 1))
        {
            Node survivor = group.MinBy(node => node.Id)!;
            survivor.Coordinate = new Coordinate(
                group.Average(node => node.Coordinate.X),
                group.Average(node => node.Coordinate.Y));
            foreach (Node node in group.Where(node => node != survivor))
            {
                replacements.Add(node, survivor);
            }

            mergedCount += group.Count;
        }

        ReplaceNodes(arcGraph, replacements);

        return mergedCount;
    }

    private static void ReplaceNodes(ArcGraph arcGraph, Dictionary<Node, Node> replacements)
    {
        if (replacements.Count == 0)
        {
            return;
        }

        foreach (Arc arc in arcGraph.Arcs)
        {
            if (replacements.TryGetValue(arc.StartNode, out Node? startNode))
            {
                arc.StartNode = startNode;
            }

            if (replacements.TryGetValue(arc.EndNode, out Node? endNode))
            {
                arc.EndNode = endNode;
            }
        }

        Node[] remaining = arcGraph.Nodes.Where(node => !replacements.ContainsKey(node)).ToArray();
        arcGraph.Nodes.Clear();
        foreach (Node node in remaining)
        {
            arcGraph.Nodes.Add(node);
        }
    }

    // The dominant case: an end that stops partway along another arc rather than at its
    // end. The arc it meets gets split there, so the junction becomes a real node.
    private static STRtree<Segment> BuildSegmentIndex(ArcGraph arcGraph)
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

        return index;
    }

    private static Split? FindSplit(STRtree<Segment> index, Node node, double weldTolerance)
    {
        Envelope envelope = new Envelope(node.Coordinate);
        envelope.ExpandBy(weldTolerance);

        Split? best = null;
        double bestDistance = weldTolerance;
        foreach (Segment segment in index.Query(envelope))
        {
            if ((segment.Arc.StartNode == node) || (segment.Arc.EndNode == node))
            {
                continue;
            }

            (double distance, double parameter) = GetDistance(
                node.Coordinate,
                segment.Coordinates[segment.Index],
                segment.Coordinates[segment.Index + 1]);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = new Split(segment.Arc, segment.Index, parameter, distance, node);
            }
        }

        return best;
    }

    private static (double Distance, double Parameter) GetDistance(
        Coordinate point,
        Coordinate start,
        Coordinate end)
    {
        double deltaX = end.X - start.X;
        double deltaY = end.Y - start.Y;
        double lengthSquared = (deltaX * deltaX) + (deltaY * deltaY);
        if (lengthSquared < 1E-18)
        {
            return (point.Distance(start), 0.0);
        }

        double parameter = Math.Clamp(
            (((point.X - start.X) * deltaX) + ((point.Y - start.Y) * deltaY)) / lengthSquared,
            0.0,
            1.0);
        double distanceX = point.X - (start.X + (parameter * deltaX));
        double distanceY = point.Y - (start.Y + (parameter * deltaY));
        return (Math.Sqrt((distanceX * distanceX) + (distanceY * distanceY)), parameter);
    }

    // Splits are applied in one pass at the end rather than as they are found, so that the
    // search above always sees whole arcs and several ends landing on one arc cannot
    // invalidate each other's segment indexes.
    private static void ApplySplits(ArcGraph arcGraph, IEnumerable<Split> splits)
    {
        Dictionary<Arc, List<Split>> splitsByArc = splits.GroupBy(split => split.Arc)
            .ToDictionary(
                grouping => grouping.Key,
                grouping => grouping.OrderBy(split => split.SegmentIndex)
                    .ThenBy(split => split.Parameter)
                    .ToList());

        Arc[] originals = arcGraph.Arcs.ToArray();
        arcGraph.Arcs.Clear();
        foreach (Arc arc in originals)
        {
            if (!splitsByArc.TryGetValue(arc, out List<Split>? arcSplits))
            {
                arcGraph.Arcs.Add(arc);
                continue;
            }

            Coordinate[] coordinates = arc.GetFullCoordinates();
            Node startNode = arc.StartNode;
            int start = 0;
            foreach (Split split in arcSplits)
            {
                AddPiece(arcGraph, arc, coordinates, start, split.SegmentIndex, startNode, split.Node);
                startNode = split.Node;
                start = split.SegmentIndex + 1;
            }

            AddPiece(arcGraph, arc, coordinates, start, coordinates.Length - 1, startNode, arc.EndNode);
        }
    }

    private static void AddPiece(
        ArcGraph arcGraph,
        Arc arc,
        Coordinate[] coordinates,
        int start,
        int end,
        Node startNode,
        Node endNode)
    {
        // A split landing on an existing vertex leaves an empty piece between two copies of
        // the same node, which carries no geometry and would only confuse polygonizing.
        if ((end <= start) && (startNode == endNode))
        {
            return;
        }

        arcGraph.AddArc(
            arc.Kind,
            startNode,
            endNode,
            arcGraph.CoordinateSequenceFactory.Create(coordinates[(start + 1)..Math.Max(start + 1, end + 1)]));
    }

    private static int Find(int[] parents, int index)
    {
        while (parents[index] != index)
        {
            parents[index] = parents[parents[index]];
            index = parents[index];
        }

        return index;
    }

    private static void Union(int[] parents, int first, int second)
    {
        int firstRoot = Find(parents, first);
        int secondRoot = Find(parents, second);
        if (firstRoot != secondRoot)
        {
            parents[Math.Max(firstRoot, secondRoot)] = Math.Min(firstRoot, secondRoot);
        }
    }

    private static IEnumerable<Polygon> ExtractPolygons(Geometry geometry)
    {
        return geometry switch
            {
                GeometryCollection geometryCollection => geometryCollection.OfType<Polygon>(),
                Polygon polygon => [polygon], _ => [],
            };
    }

    private static IEnumerable<Coast> FindCoasts(LinearRing boundary, IEnumerable<Shape> shapes)
    {
        foreach (Shape shape in shapes.Distinct())
        {
            Geometry intersection = shape.Polygon.Boundary.Intersection(boundary);
            if (!intersection.IsEmpty)
            {
                foreach (LineString lineString in intersection switch
                             {
                                 GeometryCollection geometryCollection => MergeLineStrings(
                                     geometryCollection.OfType<LineString>()),
                                 LineString lineString => [lineString], _ => [],
                             })
                {
                    yield return new Coast(lineString, shape);
                }
            }
        }
    }

    private static IEnumerable<Polygon> FindHoles(IEnumerable<Polygon> world)
    {
        return world.SelectMany(polygon => polygon.Holes)
            .Select(linearRing => new Polygon(linearRing))
            .Where(polygon => (polygon.Area < 0.007) || ((polygon.Area / polygon.Length) < 0.01));
    }

    private static IEnumerable<Polygon> FindOverlaps(IList<Country> countries)
    {
        List<(Polygon Overlap, Shape[] Shapes)> overlaps = countries.SelectMany(country => country.Shapes)
            .SelectMany(
                (firstShape, firstIndex) => countries.SelectMany(country => country.Shapes)
                    .Where((_, secondIndex) => secondIndex > firstIndex)
                    .Select(
                        secondShape => (Intersection: firstShape.Polygon.Intersection(secondShape.Polygon),
                            Shapes: new[] { firstShape, secondShape })))
            .Where(
                intersection => !intersection.Intersection.IsEmpty
                    && (intersection.Intersection.Dimension
                        == Dimension.Surface)) // check if intersection is tiny 1E-25
            .SelectMany(
                intersection => ExtractPolygons(intersection.Intersection)
                    .Select<Polygon, (Polygon Overlap, Shape[] Shapes)>(
                        polygon => (Overlap: polygon, intersection.Shapes)))
            .ToList();

        List<Polygon> exclaves = new List<Polygon>();
        foreach ((Shape shape, Polygon[] remainingPolygons) in overlaps
                     .SelectMany(overlap => overlap.Shapes.Select(shape => (Shape: shape, overlap.Overlap)))
                     .GroupBy(shape => shape.Shape)
                     .Select(
                         grouping => (Shape: grouping.Key,
                             RemainingPolygons: grouping.Aggregate(
                                 new[] { grouping.Key.Polygon },
                                 (remainingPolygons, shape) =>
                                     ExtractPolygons(new MultiPolygon(remainingPolygons).Difference(shape.Overlap))
                                         .ToArray())))
                     .ToArray())
        {
            shape.Polygon = remainingPolygons.MaxBy(polygon => polygon.Area) ?? Polygon.Empty;
            exclaves.AddRange(remainingPolygons.Except([shape.Polygon]));
        }

        return overlaps.Select(overlap => overlap.Overlap).Concat(exclaves);
    }

    private static IEnumerable<LineString> MergeLineStrings(IEnumerable<LineString> lineStrings)
    {
        LineMerger lineMerger = new LineMerger();
        lineMerger.Add(lineStrings);
        return lineMerger.GetMergedLineStrings().OfType<LineString>();
    }

    // Carries the arc's coordinates so that the search does not rebuild them per segment.
    private readonly record struct Segment(Arc Arc, Coordinate[] Coordinates, int Index);

    private readonly record struct Split(Arc Arc, int SegmentIndex, double Parameter, double Distance, Node Node);
}