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
    // Below this, two arcs running together are not a duplicate but two borders leaving the
    // same junction side by side, which keep company for a segment or two before parting.
    //
    // Measured over the sample map at a tolerance of 0.25 units: 1551 runs come to 1.75
    // units or less, ten more to between 2.0 and 2.8, and then nothing at all until the
    // shortest genuine repeat at 8.6. The short runs are one and two drawn segments - the
    // tracers sit on a lattice whose steps are 1.75 and 2.0 units - so the apparent gap just
    // under 2 units is an artifact of that lattice and the real empty span is the one this
    // figure sits in.
    //
    // Cutting the short runs out is not merely pointless but harmful: their ends are around
    // 1.4 units from the arc they supposedly repeat, too far for welding to put back, so at
    // 2.0 this left seven ends loose that nothing could resolve.
    private const double MinimumDuplicateRun = 4.0;

    // How far to look when reporting what an unresolved end nearly reached. Diagnostic
    // only: nothing is ever welded at this distance.
    private const double ReportingRadius = 50.0;

    private readonly GeometryFactory geometryFactory;

    public CorrectionService(GeometryFactory geometryFactory)
    {
        this.geometryFactory = geometryFactory;
    }

    // Cuts out stretches of tracer that only repeat another tracer. A border drawn twice is
    // not two boundaries: the two copies leave a node at the same angle, so a face
    // traversal has nothing to tell them apart by. It takes the twin and walks straight
    // back along it, which encloses nothing on that side, so the two faces that should lie
    // either side of the border come out as one - bounded by a ring that crosses itself
    // wherever the copies cross, which is then rejected as geometry and dropped, taking
    // both countries with it.
    //
    // What is cut out is the repeat, not the border: the copy that was kept still carries
    // it. The pieces either side of the cut are left as loose ends on purpose, so that
    // welding attaches them to the surviving copy. Cutting without that would strand the
    // borders that ended on the duplicate and fuse a different pair of countries instead.
    public void Collapse(ArcGraph arcGraph, double duplicateTolerance)
    {
        ArgumentNullException.ThrowIfNull(arcGraph);

        Dictionary<Arc, double> lengths = arcGraph.Arcs.ToDictionary(arc => arc, GetLength);
        Duplicate[] duplicates = [.. FindDuplicates(arcGraph, BuildSegmentIndex(arcGraph), duplicateTolerance)];

        // Longest first, so that where several copies lie on top of each other it is the
        // longest that survives. An arc kept as an original is never itself removed, and a
        // stretch is passed over when the arc has already given up an overlapping one.
        Dictionary<Arc, List<Duplicate>> removals = [];
        HashSet<Arc> originals = [];
        int contested = 0;
        foreach (Duplicate duplicate in duplicates
                     .Where(duplicate => (duplicate.Length >= MinimumDuplicateRun) && IsRedundant(duplicate, lengths))
                     .OrderByDescending(duplicate => duplicate.Length))
        {
            if (originals.Contains(duplicate.Arc)
                || removals.ContainsKey(duplicate.Twin)
                || Overlaps(removals, duplicate))
            {
                ++contested;
                continue;
            }

            if (!removals.TryGetValue(duplicate.Arc, out List<Duplicate>? taken))
            {
                taken = [];
                removals.Add(duplicate.Arc, taken);
            }

            taken.Add(duplicate);
            originals.Add(duplicate.Twin);
        }

        Duplicate[] collapsed = [.. removals.Values.SelectMany(list => list)];
        ApplyRemovals(arcGraph, removals);

        Duplicate[] tooShort = [.. duplicates.Where(duplicate => duplicate.Length < MinimumDuplicateRun)];
        Log.Information(
            "Collapsed {Collapsed} duplicated tracer stretches covering {Length:0.0} units onto the copy that was "
            + "kept. {Contested} further stretches were left alone because what they repeat is a duplicate itself, "
            + "and {Short} runs stayed under the {Minimum} unit minimum, the longest of them {Longest:0.00} units.",
            collapsed.Length,
            collapsed.Sum(duplicate => duplicate.Length),
            contested,
            tooShort.Length,
            MinimumDuplicateRun,
            tooShort.Length == 0 ? 0.0 : tooShort.Max(duplicate => duplicate.Length));

        // Reported one by one, because the repeat is a defect in the source map rather than
        // something this pipeline should have to carry: the border is drawn twice and only
        // one of the copies can be the boundary.
        foreach (Duplicate duplicate in collapsed.OrderByDescending(duplicate => duplicate.Length))
        {
            Log.Warning(
                "Arc {ArcId} repeats arc {TwinId} for {Length:0.0} units from {X:0.##}, {Y:0.##}. The border is "
                + "drawn twice in the source map; the repeat was cut out.",
                duplicate.Arc.Id,
                duplicate.Twin.Id,
                duplicate.Length,
                duplicate.Start.X,
                duplicate.Start.Y);
        }
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

    // Attaches every loose arc end to whatever it was drawn to meet. Both tolerances apply
    // to arc endpoints only and never to interior vertices: the linework interiors are not
    // defective, only the ends are, and the drawing's own resolution is coarser than either
    // tolerance, so snapping interiors would staircase genuine coastline.
    //
    // The two are separate because the operations are not equally safe. Merging two ends
    // moves both of them, to the middle of the group, so a wrong merge displaces linework
    // and - being transitive, by union-find - can chain a whole group of ends together that
    // were never meant to meet. Splitting leaves every existing vertex where it is and only
    // threads a node into the arc that was met, so a wrong split costs a kink in that arc
    // and nothing else. Merging runs first and consumes the ends it resolves, so the lower
    // the merge tolerance the more of the work falls to the safer of the two.
    public void Weld(ArcGraph arcGraph, double mergeTolerance, double splitTolerance)
    {
        ArgumentNullException.ThrowIfNull(arcGraph);

        int mergedCount = MergeLooseEnds(arcGraph, mergeTolerance);

        // Built after merging, because merging moves nodes and so moves the arc ends with
        // them. Querying an index built before that would search stale positions.
        STRtree<Segment> index = BuildSegmentIndex(arcGraph);

        List<Split> splits = new List<Split>();
        List<Node> unresolved = new List<Node>();
        int coastCount = 0;
        foreach (Node node in GetLooseEnds(arcGraph))
        {
            Split? split = FindSplit(index, node, splitTolerance);
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
            "Welded {MergedCount} ends onto each other within {MergeTolerance} units and split {SplitCount} arcs "
            + "within {SplitTolerance} to take the rest, {CoastCount} of those on the coast. "
            + "{UnresolvedCount} ends remain loose.",
            mergedCount,
            mergeTolerance,
            splits.Count,
            splitTolerance,
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
                splitTolerance,
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

            AddPiece(arcGraph, arc, coordinates, start, coordinates.Length - 2, startNode, arc.EndNode);
        }
    }

    // The nodes stand for the vertices either side of the piece: startNode for vertex
    // start, endNode for vertex end + 1, and only the vertices between them are carried as
    // the piece's own geometry. The last piece of a split arc therefore ends at vertex
    // end + 1 as well, so its end is passed as the second to last vertex and not the last.
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

    // Walks each arc segment by segment, asking which other arc is nearest, and groups
    // consecutive segments that answer the same arc into one run.
    //
    // The midpoint stands for its segment. Segments on this map run about 1.75 units, seven
    // times the tolerance, so requiring both ends as well would break a run wherever one
    // end of one segment happened to stray outside.
    private static List<Duplicate> FindDuplicates(
        ArcGraph arcGraph,
        STRtree<Segment> index,
        double duplicateTolerance)
    {
        List<Duplicate> duplicates = new List<Duplicate>();
        foreach (Arc arc in arcGraph.Arcs)
        {
            Coordinate[] coordinates = arc.GetFullCoordinates();
            Arc? twin = null;
            int first = 0;
            double length = 0.0;
            for (int i = 0; i < (coordinates.Length - 1); ++i)
            {
                Arc? nearest = FindNearestArc(index, arc, coordinates[i], coordinates[i + 1], duplicateTolerance);
                if (nearest != twin)
                {
                    if (twin != null)
                    {
                        duplicates.Add(new Duplicate(arc, twin, first, i - 1, length, coordinates[first]));
                    }

                    twin = nearest;
                    first = i;
                    length = 0.0;
                }

                length += coordinates[i].Distance(coordinates[i + 1]);
            }

            if (twin != null)
            {
                duplicates.Add(new Duplicate(arc, twin, first, coordinates.Length - 2, length, coordinates[first]));
            }
        }

        return duplicates;
    }

    private static Arc? FindNearestArc(
        STRtree<Segment> index,
        Arc arc,
        Coordinate start,
        Coordinate end,
        double duplicateTolerance)
    {
        Coordinate middle = new Coordinate((start.X + end.X) / 2.0, (start.Y + end.Y) / 2.0);
        Envelope envelope = new Envelope(middle);
        envelope.ExpandBy(duplicateTolerance);

        Arc? nearest = null;
        double bestDistance = duplicateTolerance;
        foreach (Segment segment in index.Query(envelope))
        {
            if (segment.Arc == arc)
            {
                continue;
            }

            (double distance, double _) = GetDistance(
                middle,
                segment.Coordinates[segment.Index],
                segment.Coordinates[segment.Index + 1]);

            // The lower identifier wins a tie, so that the same map always collapses the
            // same way however the index happens to hand the segments back.
            if ((distance < bestDistance)
                || ((distance <= bestDistance) && ((nearest == null) || (segment.Arc.Id < nearest.Id))))
            {
                bestDistance = distance;
                nearest = segment.Arc;
            }
        }

        return nearest;
    }

    // The shorter arc is the one to give up, and an equal pair is settled by identifier so
    // that four copies of one border leave the same survivor every time.
    private static bool IsRedundant(Duplicate duplicate, Dictionary<Arc, double> lengths)
    {
        double length = lengths[duplicate.Arc];
        double twinLength = lengths[duplicate.Twin];

        return (length < twinLength)
            || ((length <= twinLength) && (duplicate.Arc.Id > duplicate.Twin.Id));
    }

    private static bool Overlaps(Dictionary<Arc, List<Duplicate>> removals, Duplicate duplicate)
    {
        return removals.TryGetValue(duplicate.Arc, out List<Duplicate>? taken)
            && taken.Exists(other => (duplicate.First <= other.Last) && (other.First <= duplicate.Last));
    }

    // Rebuilds each arc as the stretches the removals leave behind. The ends of those
    // stretches become nodes of their own, loose until welding attaches them to the copy
    // that was kept.
    private static void ApplyRemovals(ArcGraph arcGraph, Dictionary<Arc, List<Duplicate>> removals)
    {
        Arc[] originals = arcGraph.Arcs.ToArray();
        arcGraph.Arcs.Clear();
        foreach (Arc arc in originals)
        {
            if (!removals.TryGetValue(arc, out List<Duplicate>? intervals))
            {
                arcGraph.Arcs.Add(arc);
                continue;
            }

            Coordinate[] coordinates = arc.GetFullCoordinates();
            List<(int First, int Last)> kept = new List<(int First, int Last)>();
            int cursor = 0;
            foreach (Duplicate interval in intervals.OrderBy(duplicate => duplicate.First))
            {
                if (interval.First > cursor)
                {
                    kept.Add((cursor, interval.First));
                }

                cursor = interval.Last + 1;
            }

            if (cursor < (coordinates.Length - 1))
            {
                kept.Add((cursor, coordinates.Length - 1));
            }

            foreach ((int first, int last) in kept)
            {
                AddStretch(
                    arcGraph,
                    arc,
                    coordinates,
                    first,
                    last,
                    first == 0 ? arc.StartNode : arcGraph.AddNode(coordinates[first].Copy()),
                    last == (coordinates.Length - 1) ? arc.EndNode : arcGraph.AddNode(coordinates[last].Copy()));
            }
        }
    }

    // First and last are vertex indexes, and the two nodes stand for those vertices, so
    // only what lies strictly between them is carried as the arc's own geometry.
    private static void AddStretch(
        ArcGraph arcGraph,
        Arc arc,
        Coordinate[] coordinates,
        int first,
        int last,
        Node firstNode,
        Node lastNode)
    {
        if (last <= first)
        {
            return;
        }

        arcGraph.AddArc(
            arc.Kind,
            firstNode,
            lastNode,
            arcGraph.CoordinateSequenceFactory.Create(coordinates[(first + 1)..last]));
    }

    private static double GetLength(Arc arc)
    {
        Coordinate[] coordinates = arc.GetFullCoordinates();
        double length = 0.0;
        for (int i = 1; i < coordinates.Length; ++i)
        {
            length += coordinates[i].Distance(coordinates[i - 1]);
        }

        return length;
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

    // A stretch of one arc that repeats another. First and Last are segment indexes, so the
    // stretch runs from vertex First to vertex Last + 1.
    private readonly record struct Duplicate(
        Arc Arc,
        Arc Twin,
        int First,
        int Last,
        double Length,
        Coordinate Start);

    // Carries the arc's coordinates so that the search does not rebuild them per segment.
    private readonly record struct Segment(Arc Arc, Coordinate[] Coordinates, int Index);

    private readonly record struct Split(Arc Arc, int SegmentIndex, double Parameter, double Distance, Node Node);
}