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

    // How much a tracer may enclose where it comes back to a coordinate it has already
    // visited, as an area over the length of the loop, before that loop is a needle: a
    // stretch drawn out and back along itself rather than a boundary with a side to it.
    //
    // Measured over the 26 such loops on this map, the two populations are nine million apart.
    // 22 enclose exactly nothing; three come to 4.0e-10, 5.6e-10 and 7.7e-10, which is the
    // residue of summing a shoelace along a collinear run and not an area; and one comes to
    // 0.00758, which is a real loop - 0.19 units of border drawn as a closed speck. This figure
    // sits at the middle of that span on a log scale, 1300x above the largest residue and 7600x
    // below the one real loop, so it is not a line drawn through a cluster but a line drawn
    // across an empty decade and a half.
    private const double MaximumNeedleAreaToLengthRatio = 1E-6;

    // How far from a straight line the two legs of a reversal may wander, in units, before the
    // run is a shape rather than one line walked twice. A grid step: below that the noding grid
    // cannot tell the vertex off the line in the first place.
    //
    // The reversals this is applied to are exact. Of the 169 vertices where a tracer turns by
    // more than 150 degrees, 14 turn by 180.0000 and the next turns by 179.8903; all 14 have a
    // straight run either side that deviates from the line through its ends by 0.0000000 units,
    // and the other 155 have no straight run at all - they are sharp corners, which are drawn
    // boundary and stay. So this tolerance is a guard rather than a threshold, and nothing on
    // this map comes near it.
    private const double MaximumReversalDeviation = 1E-4;

    // The sine of the angle by which a run may depart from straight and still count as part of
    // the same line, used only to decide how far a reversal reaches. One degree. What keeps this
    // from mattering is the deviation guard above, which is measured against the line itself.
    private const double MaximumStraightSine = 0.0175;

    private const double DegenerateLength = 1E-9;

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

        // First, because a needle is a stretch an arc repeats of itself, and leaving it in makes
        // every step after this harder: 4a nodes the arc against its own other leg and the
        // thicket of sub-unit pieces that comes out is what no later rule can route through.
        RemoveNeedles(arcGraph);

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

        // Before the index and before the splitting, which is the whole point of doing it
        // here: an end that one of these arcs was holding becomes a loose end again, and the
        // splitting below then welds it onto something real.
        int collapsedCount = RemoveCollapsedArcs(arcGraph, mergeTolerance);

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
            "Welded {MergedCount} ends onto each other within {MergeTolerance} units, dropped {CollapsedCount} "
            + "tracers the merge closed onto nothing wider than itself, and split {SplitCount} arcs within "
            + "{SplitTolerance} to take the rest, {CoastCount} of those on the coast. {UnresolvedCount} ends "
            + "remain loose.",
            mergedCount,
            mergeTolerance,
            collapsedCount,
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

    // A tracer that comes back to a coordinate it has already visited, enclosing nothing in
    // between, has drawn a line and not a boundary. Nothing can be on one side of it, because
    // both of its sides are the same side. 26 such loops on this map over 19 arcs: 16 are a
    // vertex repeated where the drawing stopped and started again, and the other 10 are
    // excursions of 0.64 to 1.66 units that leave a point, run out, and come back to that exact
    // point along the way they came.
    //
    // The nine long ones are not thin lenses but straight lines. Every vertex of the excursion
    // on arc 1181 lies on one line to within 9e-07, so the return leg is the outbound leg with
    // its vertices spaced differently. That is why this needs no tolerance to speak of: the loop
    // encloses nothing at all rather than nearly nothing.
    //
    // Left in, a needle does its damage through 4a. The two legs lie on top of each other, so
    // noding cuts the arc at every vertex of the overlap, and 44 units of border between
    // Kalimantan and Daha came out as thirteen pieces, ten of them under 0.3 units. A traversal
    // cannot divide anything with that: it walks down the border, into the needle and back out,
    // on to the next needle and back out, and then retraces the whole 27 units it has already
    // walked in the other direction. One ring of 33 half-edges with both sides of the border in
    // it, so the two countries came out as one face, with three faces of 0.000003 square units
    // and less left over beside it.
    //
    // Cutting the needle out rather than splitting the arc, because there is nothing here to
    // keep: the stretch removed is the second copy of a line the arc already carries. The arc
    // keeps both of its nodes, since the coordinate the needle returns to is the one it left.
    //
    // **A needle does not have to come back to a vertex.** The test above looks for a coordinate
    // the arc visits twice, and a cusp left by a badly drawn bezier often does not oblige: it
    // runs out, reverses, and comes back to a point part way along the leg it went out on,
    // between two of that leg's vertices rather than on one of them. Arc 412 runs 1.85 units out
    // from 5428.35, 2824.64, turns through exactly 180 degrees at 5426.15, 2825.74, and comes
    // 0.17 units back to 5426.35, 2825.64 - which lies on the outbound leg but is no vertex of
    // it. Nothing repeats, so the first test sees nothing.
    //
    // So a reversal is looked for as well, and the whole straight run around it is replaced by
    // the segment from the point where it was entered to the point where it was left. That loses
    // nothing, because every vertex of the run is on one line: what the run draws is that
    // segment, walked forwards, backwards, and forwards again.
    //
    // These are what the three bridges 4d could not account for were made of - arc 232 at
    // 3110.99, 2200.27 and arc 412 at 5426.15, 2825.74 - and what the last two faces rejected
    // for crossing themselves were made of, both on arc 1337 at 3458.22, 2393.30.
    private static void RemoveNeedles(ArcGraph arcGraph)
    {
        Arc[] originals = [.. arcGraph.Arcs];
        arcGraph.Arcs.Clear();

        int arcs = 0;
        int needles = 0;
        int dropped = 0;
        double removed = 0.0;
        double longest = 0.0;
        foreach (Arc arc in originals)
        {
            List<Coordinate> coordinates = [.. arc.GetFullCoordinates()];
            int found = 0;
            while (TryFindNeedle(coordinates, out int from, out int count, out double run))
            {
                coordinates.RemoveRange(from, count);
                ++found;
                removed += run;
                longest = Math.Max(longest, run);
            }

            if (found == 0)
            {
                arcGraph.Arcs.Add(arc);
                continue;
            }

            ++arcs;
            needles += found;

            // One point left means the whole arc was the needle, and there is no line to keep.
            if (coordinates.Count < 2)
            {
                ++dropped;
                continue;
            }

            arcGraph.AddArc(
                arc.Kind,
                arc.StartNode,
                arc.EndNode,
                arcGraph.CoordinateSequenceFactory.Create([.. coordinates[1..^1]]));
        }

        if (needles == 0)
        {
            return;
        }

        Log.Information(
            "Cut {Needles} needles out of {Arcs} tracer arcs, {Removed:0.00} units of line that each arc had "
            + "already drawn once, the longest of them {Longest:0.00} units. {Dropped} arcs were nothing but a "
            + "needle and went entirely.",
            needles,
            arcs,
            removed,
            longest,
            dropped);
    }

    // The vertices to take out, as a start and a count: the earliest point the coordinates come
    // back to with the loop it closes enclosing nothing, or failing that the earliest reversal.
    // Earliest rather than longest, so that a needle inside a needle is taken from the inside out
    // and the result does not depend on which was found first.
    //
    // The two cases differ in what they keep. Where a coordinate repeats, one of the two copies
    // stays and everything between them goes. Where the arc reverses, the points it entered and
    // left the line at both stay and only what lies between them goes.
    private static bool TryFindNeedle(
        List<Coordinate> coordinates,
        out int from,
        out int count,
        out double run)
    {
        if (TryFindRepeat(coordinates, out int first, out int last))
        {
            from = first + 1;
            count = last - first;
            run = GetRemovedLength(coordinates, first, last);
            return true;
        }

        if (TryFindReversal(coordinates, out int low, out int high))
        {
            from = low;
            count = (high - low) + 1;
            run = GetRemovedLength(coordinates, low - 1, high + 1);
            return true;
        }

        from = 0;
        count = 0;
        run = 0.0;
        return false;
    }

    // The line the arc gives up: what it walked between the two points that stay, less the one
    // segment that replaces it. For a repeat the two are the same point and nothing is subtracted.
    private static double GetRemovedLength(List<Coordinate> coordinates, int first, int last)
    {
        double walked = 0.0;
        for (int k = first; k < last; ++k)
        {
            walked += coordinates[k].Distance(coordinates[k + 1]);
        }

        return walked - coordinates[first].Distance(coordinates[last]);
    }

    // A vertex the run turns back on itself at, with every vertex of the straight run around it
    // on one line. Returns the run as the pair of points it is to be replaced by, so that the
    // caller removes what lies between them exactly as it does for a repeated coordinate.
    private static bool TryFindReversal(List<Coordinate> coordinates, out int low, out int high)
    {
        for (int i = 1; i < (coordinates.Count - 1); ++i)
        {
            if (!IsReversal(coordinates, i))
            {
                continue;
            }

            // Out to both sides for as long as the line holds, which a reversal counts towards
            // as much as going straight on does: both mean the three points are collinear.
            low = i;
            while ((low - 1) >= 1 && IsStraight(coordinates, low - 1))
            {
                --low;
            }

            high = i;
            while ((high + 1) <= (coordinates.Count - 2) && IsStraight(coordinates, high + 1))
            {
                ++high;
            }

            if (IsOnOneLine(coordinates, low - 1, high + 1))
            {
                return true;
            }
        }

        low = 0;
        high = 0;
        return false;
    }

    // The two segments meeting at this vertex point along one line and in opposite directions.
    // Measured as a sine rather than an angle, which is what makes it exact here: arc cosine
    // loses its precision at the very place this is asking about.
    private static bool IsReversal(List<Coordinate> coordinates, int index)
    {
        (double X, double Y) before = (
            coordinates[index].X - coordinates[index - 1].X,
            coordinates[index].Y - coordinates[index - 1].Y);
        (double X, double Y) after = (
            coordinates[index + 1].X - coordinates[index].X,
            coordinates[index + 1].Y - coordinates[index].Y);

        return IsStraight(coordinates, index) && (((before.X * after.X) + (before.Y * after.Y)) < 0.0);
    }

    private static bool IsStraight(List<Coordinate> coordinates, int index)
    {
        (double X, double Y) before = (
            coordinates[index].X - coordinates[index - 1].X,
            coordinates[index].Y - coordinates[index - 1].Y);
        (double X, double Y) after = (
            coordinates[index + 1].X - coordinates[index].X,
            coordinates[index + 1].Y - coordinates[index].Y);
        double lengths = Math.Sqrt(((before.X * before.X) + (before.Y * before.Y))
            * ((after.X * after.X) + (after.Y * after.Y)));

        return (lengths < DegenerateLength)
            || (Math.Abs((before.X * after.Y) - (before.Y * after.X)) <= (MaximumStraightSine * lengths));
    }

    // Every vertex between the two ends within a grid step of the line joining them. The guard
    // the rule actually rests on: the walk out from the reversal uses an angle, which says
    // nothing about how far the far end of a long run has drifted.
    private static bool IsOnOneLine(List<Coordinate> coordinates, int first, int last)
    {
        double lineX = coordinates[last].X - coordinates[first].X;
        double lineY = coordinates[last].Y - coordinates[first].Y;
        double length = Math.Sqrt((lineX * lineX) + (lineY * lineY));
        if (length < DegenerateLength)
        {
            return true;
        }

        for (int k = first + 1; k < last; ++k)
        {
            double deviation = Math.Abs(
                    (lineX * (coordinates[k].Y - coordinates[first].Y))
                    - (lineY * (coordinates[k].X - coordinates[first].X)))
                / length;
            if (deviation > MaximumReversalDeviation)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryFindRepeat(List<Coordinate> coordinates, out int first, out int last)
    {
        Dictionary<(double X, double Y), int> seen = [];
        for (int j = 0; j < coordinates.Count; ++j)
        {
            (double X, double Y) key = (coordinates[j].X, coordinates[j].Y);
            if (seen.TryGetValue(key, out int i))
            {
                double area = 0.0;
                double length = 0.0;
                for (int k = i; k < j; ++k)
                {
                    area += (coordinates[k].X * coordinates[k + 1].Y)
                        - (coordinates[k + 1].X * coordinates[k].Y);
                    length += coordinates[k].Distance(coordinates[k + 1]);
                }

                double ratio = length < DegenerateLength
                    ? 0.0
                    : Math.Abs(area / 2.0) / length;
                if (ratio <= MaximumNeedleAreaToLengthRatio)
                {
                    first = i;
                    last = j;
                    return true;
                }
            }

            seen[key] = j;
        }

        first = 0;
        last = 0;
        return false;
    }

    // A tracer whose own two ends the merge welded to each other has joined nothing: it has
    // collapsed to a point. 68 of them on this map, 65 border and 3 substate.
    //
    // Left in, such an arc does real harm, because of what else was in the group its two ends
    // merged as. A neighbouring border reaching for the same place joins that group too, and
    // its end comes to rest on the collapsed arc's node. The node is then referenced three
    // times, twice by the collapsed arc and once by the border, so it is no longer a loose end
    // and the splitting below never looks at it. The border is left hanging off a node with
    // nothing else on it: an arc that divides nothing, so the two countries either side of it
    // come out as one face. That is Ligury and Tuscany, where the border between them, 10.74
    // units of it, ends on the node of a stub 0.3 units long rather than on the border it was
    // drawn to meet 0.88 units further on, and it is Prabang and Vantong, where 44.13 units of
    // border end on the tip of a 0.12-unit scratch rather than on the border 0.06 units on.
    //
    // Collapsed is measured by what the arc encloses, not by where its vertices are. The test
    // used to ask whether every coordinate was exactly its node's, which caught the stubs the
    // merge had reduced to a single point and missed the ones that still had a vertex of their
    // own: arc 789 is 0.12 units of scratch beside Prabang, and after its ends merged it ran
    // out 0.034 units and straight back, which is a point for every purpose that matters and
    // no coordinate of which equals the node.
    //
    // So the measure is the hydraulic radius of the loop the closed arc makes, area over
    // perimeter, which for a long thin region is half its width. A spike doubling back
    // encloses nothing and scores 0; a ring encloses something and scores on the order of its
    // own size. The threshold is half the merge tolerance, which says the enclosed region is
    // narrower than the distance the merge is allowed to close - so the merge could have made
    // it, and there is nothing in it to keep.
    //
    // Measured, the two populations do not touch: of the 70 closed tracer arcs, 68 run from 0
    // up to 0.0578 and the other two are 1.1139 and 3.2849, a span of 19x with nothing in it.
    // Those two are rings, 25.6 and 82.7 units round, drawn as loops whose ends the merge
    // closed, and they stay. The threshold is derived rather than fitted, and it lands within
    // 2% of the middle of that span on a log scale, 4.3x above the widest spike and 4.5x
    // below the narrowest ring.
    //
    // Only tracers, and that restriction is doing the work rather than tidying up after it.
    // The coast carries 5241 closed arcs and 1326 of them score under this threshold - tiny
    // islands, the smallest 0.045 units from its node - and their size says nothing about
    // whether they belong. They are never loose ends, since they come out of the union
    // already closed, so the merge never closes one, and a measure of what the merge did has
    // no business reading them. The 460 lakes would all survive the measure anyway, the
    // narrowest of them 1.97 units out, but they are left out on the same grounds.
    //
    // Removing the arc cannot disconnect anything. Its two ends are one node already, so
    // everything else that meets there still meets there; only the stub itself goes.
    private static int RemoveCollapsedArcs(ArcGraph arcGraph, double weldTolerance)
    {
        Arc[] collapsed =
        [
            .. arcGraph.Arcs.Where(
                arc => (arc.StartNode == arc.EndNode)
                    && (arc.Kind is ArcKind.Border or ArcKind.Substate)
                    && (GetEnclosedRadius(arc) <= (weldTolerance / 2.0))),
        ];
        foreach (Arc arc in collapsed)
        {
            arcGraph.Arcs.Remove(arc);
        }

        return collapsed.Length;
    }

    // Area over perimeter for the loop a closed arc makes. The shoelace sum is closed by the
    // arc's own geometry, which starts and ends at the same node, so no closing term is
    // needed here.
    private static double GetEnclosedRadius(Arc arc)
    {
        Coordinate[] coordinates = arc.GetFullCoordinates();
        double area = 0.0;
        double perimeter = 0.0;
        for (int i = 0; i < (coordinates.Length - 1); ++i)
        {
            area += (coordinates[i].X * coordinates[i + 1].Y) - (coordinates[i + 1].X * coordinates[i].Y);
            perimeter += coordinates[i].Distance(coordinates[i + 1]);
        }

        return perimeter < DegenerateLength ? 0.0 : Math.Abs(area / 2.0) / perimeter;
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

    // How far a segment lies from a node along the arc they share, against the tolerance. False
    // for an arc that does not end at the node at all, which is the common case and costs
    // nothing. The cumulative lengths are worked out once per arc and handed back through
    // the dictionary, which in practice holds the single arc that ends at this node.
    private static bool IsNearAlongTheArc(
        Segment segment,
        Node node,
        double weldTolerance,
        Dictionary<Arc, double[]> cumulative)
    {
        bool fromStart = segment.Arc.StartNode == node;
        bool fromEnd = segment.Arc.EndNode == node;
        if (!fromStart && !fromEnd)
        {
            return false;
        }

        if (!cumulative.TryGetValue(segment.Arc, out double[]? lengths))
        {
            lengths = new double[segment.Coordinates.Length];
            for (int i = 1; i < lengths.Length; ++i)
            {
                lengths[i] = lengths[i - 1] + segment.Coordinates[i - 1].Distance(segment.Coordinates[i]);
            }

            cumulative.Add(segment.Arc, lengths);
        }

        // The nearer of the segment's two vertices, from whichever end of the arc the node is.
        // Both, for a closed arc, where the node is each of them.
        double along = double.PositiveInfinity;
        if (fromStart)
        {
            along = lengths[segment.Index];
        }

        if (fromEnd)
        {
            along = Math.Min(along, lengths[^1] - lengths[segment.Index + 1]);
        }

        return along <= weldTolerance;
    }

    // A loose end may weld onto its own arc, but not onto the stretch of it that it has just
    // come from - and "just come from" is measured along the arc, not across the gap, by the
    // same tolerance that bounds the weld.
    //
    // Excluding the whole arc instead, as this did, means a tracer that loops round and comes
    // back to where it began can never be closed. The border that surrounds Bopedi is such a
    // loop: it starts at the coast, goes right round the country and ends 0.18 units from an
    // earlier part of itself, with nothing else within 12.48. So it was reported as an
    // unresolved end, the loop never closed, and the border divided nothing - which is why
    // Kovenda and Bopedi came out as one face. Excluding nothing would be no better: an end
    // would find the segment beside itself at no distance at all and split its own arc next to
    // its own end, which is how stubs are made.
    //
    // Measured over the 2669 loose ends of the sample map, allowing this changes exactly one of
    // them, which is that one.
    private static Split? FindSplit(STRtree<Segment> index, Node node, double weldTolerance)
    {
        Envelope envelope = new Envelope(node.Coordinate);
        envelope.ExpandBy(weldTolerance);
        Dictionary<Arc, double[]> cumulative = new Dictionary<Arc, double[]>();

        Split? best = null;
        double bestDistance = weldTolerance;
        foreach (Segment segment in index.Query(envelope))
        {
            if (IsNearAlongTheArc(segment, node, weldTolerance, cumulative))
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