// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Algorithm;
using NetTopologySuite.Algorithm.Distance;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Simplify;

using Serilog;

public sealed class TopologyService : ITopologyService
{
    // What two arcs on the same two nodes may enclose between them and still be one line
    // drawn twice, as an area over the length of the loop they make. The 4.4x gap the figure
    // sits in is measured in RemoveDuplicateArcs below, and it is the same hydraulic radius
    // 3c and 3d use to tell a gap from a lake and a slit from an inlet.
    private const double MaximumDuplicateAreaToLengthRatio = 0.15;

    // A loop of no perimeter has no shape to measure, and dividing by it would come out as
    // NaN, which compares false against every threshold.
    private const double DegenerateLength = 1E-9;

    // How many of the arcs that separate nothing to name individually. The longest first,
    // since a long one is a chain that was drawn to enclose something real.
    private const int MaximumBridgesReported = 20;

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
    public void BuildFaces(ArcGraph arcGraph, double enclosedArea, double dangleTolerance)
    {
        ArgumentNullException.ThrowIfNull(arcGraph);

        int crossings = this.NodeCrossings(arcGraph);

        // Two nodes at one coordinate are one node, however they got there, and an arc between
        // such a pair has no length and so no direction to leave the node in. That matters
        // because 5f chooses where to go next by departure angle, and the angle of a line of
        // no length is whatever Atan2 makes of two zeroes: GetDepartureAngle falls back to a
        // coordinate equal to the start when it can find no different one, so such an arc
        // claims to leave at angle 0 whatever lies around it. One of those in the order is
        // enough to send the traversal the wrong way, and a boundary walked the wrong way out
        // of its own region ends up in the outline of the landmass instead of closing into a
        // face.
        //
        // The two tests are not the same test, and the asymmetry is deliberate. Dropping an
        // arc asks whether the noding grid puts all of its coordinates at its node, because
        // an arc that small has no direction to be traced by whatever its coordinates say.
        // Merging two nodes asks whether their coordinates are equal exactly.
        //
        // Measured, because the obvious thing is wrong: merging nodes the grid puts at the
        // same point collapses 605 of them rather than 57 and takes the countries with no
        // geometry from 6 to 18. The arc geometry is not on the grid - only the crossing
        // points of 5a are ever made precise, and the drawn coordinates come off the SVG as
        // they are - so two nodes less than a grid step apart are two real points in the
        // data, and rounding them together joins chains that the map never joined. The grid
        // is a resolution, not a tolerance. Within one arc it says what is too small to have
        // a direction; between two nodes it says nothing at all.
        //
        // Widening the arc test alone drops 18 more arcs, all of them closed, and moves no
        // other number on this map: the land inside no face, the arcs separating nothing, the
        // fused faces and the boundary sharing cannot account for all stay where they were.
        // Hygiene rather than a repair.
        //
        // This was expected to be what left Maharath, Sanjan, Siam, Dumarune and Sulka with
        // no ground at all, and it is not: removing these recovers Tangeria and takes the
        // land lost to self-crossing rings from 18,816 square units to 5,699, but those five
        // are still missing. What accounted for them was a gap in the coast open to the sea
        // and, beside it, one line drawn twice.
        (int contracted, int collapsed) = ContractDegenerateArcs(arcGraph, this.geometryFactory.PrecisionModel);
        int merged = MergeCoincidentNodes(arcGraph);
        int degenerate = RemoveDegenerateArcs(arcGraph, this.geometryFactory.PrecisionModel);
        if ((contracted > 0) || (merged > 0) || (degenerate > 0))
        {
            Log.Information(
                "Brought the two ends of {Contracted} open arcs shorter than the noding grid together, taking "
                + "{Collapsed} nodes out, merged {Merged} more onto another at the same coordinate and dropped "
                + "{Degenerate} closed arcs the grid puts entirely at their own node, which have no direction to "
                + "be traced by.",
                contracted,
                collapsed,
                merged,
                degenerate);
        }

        (int duplicates, double duplicateLength, double widest) = RemoveDuplicateArcs(arcGraph);
        if (duplicates > 0)
        {
            Log.Information(
                "Dropped {Duplicates} arcs covering {Length:0.##} units that join the same two nodes as another "
                + "and run along it, the widest of the slivers between them measuring {Widest:0.#####}.",
                duplicates,
                duplicateLength,
                widest);
        }

        (int retraced, double retracedLength) = RemoveRetracedArcs(arcGraph);
        if (retraced > 0)
        {
            Log.Information(
                "Dropped {Retraced} closed arcs covering {Length:0.#####} units that leave their node and come "
                + "back along the same line, so that both of their half-edges claim the same direction out of "
                + "it and the order between them is arbitrary.",
                retraced,
                retracedLength);
        }

        (int dangles, double dangleLength, double longestDangle) = RemoveDanglingArcs(
            arcGraph,
            dangleTolerance);
        if (dangles > 0)
        {
            Log.Information(
                "Pruned {Dangles} arcs covering {Length:0.##} units that end on a node with nothing else on it "
                + "and are shorter than the {Tolerance} unit weld tolerance, the longest {Longest:0.##} units. "
                + "An overshoot past a crossing, which encloses nothing and divides nothing.",
                dangles,
                dangleLength,
                dangleTolerance,
                longestDangle);
        }

        List<(DirectedArc[] Ring, double SignedArea)> rings = TraceRings(arcGraph);
        int traced = rings.Sum(ring => ring.Ring.Length);
        if (traced != (arcGraph.Arcs.Count * 2))
        {
            Log.Warning(
                "Traced {Traced} half-edges into rings but the graph has {Expected}; the faces do not tile.",
                traced,
                arcGraph.Arcs.Count * 2);
        }

        ReportBridges(rings);

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

    // Dissolving a set of faces by walking their arcs rather than by unioning their geometry.
    // 7a used to hand the faces to UnaryUnionNG, which is a general overlay: it re-nodes
    // everything it is given, and where it decides two edges are not quite the same it writes a
    // new one. Measured over the finished output, the boundary it follows exactly is 267,758
    // segments and the boundary it does not is 5,565 - and removing the dissolve outright takes
    // that 5,565 to 2, so the overlay is not one cause among several, it is the whole of what is
    // left. Walking the arcs cannot have that failure mode, because it never computes a
    // coordinate: an arc either bounds the set or it does not, and the ones that do are strung
    // together as they are.
    //
    // An arc is interior when the faces either side of it are both in the set, and those drop
    // out in pairs. What is left is every half-edge whose other side lies outside, which is the
    // boundary, and it is chained by the same rule 5f traces faces with - the next way out of a
    // node, clockwise from the way back - except that a half-edge the set swallowed is stepped
    // over rather than taken. That is what makes the walk hug the outside of the set instead of
    // turning into it at the first internal border.
    public IReadOnlyList<IReadOnlyList<DirectedArc[]>> DissolveFaces(
        ArcGraph arcGraph,
        IReadOnlyList<IReadOnlyCollection<Face>> sets)
    {
        ArgumentNullException.ThrowIfNull(arcGraph);
        ArgumentNullException.ThrowIfNull(sets);

        // Once for every set between them. The order the half-edges leave a node in is a property
        // of the graph, and rebuilding it per set made this phase four times as slow as the union
        // it replaced - 44 seconds against 10 on the national layer.
        Dictionary<Node, DirectedArc[]> outgoing = BuildOutgoing(arcGraph);
        Dictionary<DirectedArc, int> positions = [];
        foreach ((Node _, DirectedArc[] arcs) in outgoing)
        {
            for (int i = 0; i < arcs.Length; ++i)
            {
                positions[arcs[i]] = i;
            }
        }

        return [.. sets.Select(faces => DissolveOneSet(faces, outgoing, positions))];
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
    // An arc with both of its sides in one ring divides no two regions from one another: the
    // ring walks out along it and straight back, so it lies on no cycle at all. For the
    // coast that is ordinary - an inlet one arc wide has sea on both sides - but a border
    // that separates nothing means the chain it belongs to never closes, and whatever that
    // chain was drawn to enclose is left open to whatever lies outside it. The region then
    // gets no face, and no country can be given ground that is inside no face.
    //
    // Reported rather than repaired, because the repair is a line in the source map: either
    // a border is missing where the chain should have closed, or the chain is drawn out into
    // the open and should not be there at all.
    // An arc with nothing on the far end of it. It bounds no region on either side, so a
    // traversal that reaches it walks out and straight back, and 5h reports it as separating
    // nothing - which is exactly what it is.
    //
    // These are overshoots. Where two borders that should meet at a point instead cross a little
    // before their ends, 5a nodes the crossing and the stubs beyond it are left over; 5c drops
    // one of the pair as a duplicate, and the survivor is a dangle. That is arc 11378 at
    // 4483.17, 1829.51, 0.19 units of the Kinar border past the point where it crosses the one it
    // was drawn to meet, with the three borders of that junction properly joined at the crossing
    // and this piece hanging off it.
    //
    // **Bounded by the weld tolerance, and the bound is the whole point.** Below 0.5 units an
    // overshoot is inside the distance at which phase 3 already declares two drawn ends to be the
    // same point, so there is nothing in it. Above that a dangle is a real failure - a chain that
    // never closed, a region that got no face - and pruning it would delete the evidence instead
    // of the defect. Prabang's border dangled for 44 units and reading that report is how it was
    // found. So the long ones stay and 5h goes on reporting them.
    //
    // Repeated to a fixpoint, because the arc behind a pruned one can be left dangling in its
    // turn. On this map it settles in one round: 2 arcs, 0.012 and 0.19 units, which are the last
    // two 5h had to report, and nothing behind them.
    private static (int Pruned, double Length, double Longest) RemoveDanglingArcs(
        ArcGraph arcGraph,
        double dangleTolerance)
    {
        int pruned = 0;
        double length = 0.0;
        double longest = 0.0;
        while (true)
        {
            Dictionary<Node, int> degrees = new Dictionary<Node, int>();
            foreach (Arc arc in arcGraph.Arcs)
            {
                degrees[arc.StartNode] = degrees.GetValueOrDefault(arc.StartNode) + 1;
                degrees[arc.EndNode] = degrees.GetValueOrDefault(arc.EndNode) + 1;
            }

            // A closed arc is left alone: its one node carries both of its ends, so a degree of
            // two there says nothing about whether anything else meets it.
            Arc[] dangling =
            [
                .. arcGraph.Arcs.Where(
                    arc => (arc.StartNode != arc.EndNode)
                        && ((degrees.GetValueOrDefault(arc.StartNode) == 1)
                            || (degrees.GetValueOrDefault(arc.EndNode) == 1))
                        && (GetLength(arc.GetFullCoordinates()) <= dangleTolerance)),
            ];
            if (dangling.Length == 0)
            {
                return (pruned, length, longest);
            }

            foreach (Arc arc in dangling)
            {
                double arcLength = GetLength(arc.GetFullCoordinates());
                length += arcLength;
                longest = Math.Max(longest, arcLength);
                arcGraph.Arcs.Remove(arc);
                ++pruned;
            }
        }
    }

    // An arc that reads the same forwards as backwards is one line drawn twice inside a single
    // arc: it leaves its node, runs out to a point and comes back over itself. 5c catches one
    // line drawn twice as two arcs; this is the same defect wearing one arc, which 5c cannot see
    // because it only looks at pairs sharing two nodes and this one shares its node with itself.
    //
    // It matters because of the order 5f sorts a node's half-edges into. Both half-edges of such
    // an arc leave along the same ray, so GetDepartureAngle gives them the same angle, the sort
    // falls through to ThenBy(arc.Id) - which cannot separate them either, both being the same
    // arc - and which of the two the traversal turns onto is left to the order they happened to
    // be added in. One of those orders sends a face onto a half-edge another ring has already
    // taken, which is the warning TraceRings carries and a face left unclosed.
    //
    // There is no ordering them, which is why they are removed rather than sorted. Comparing the
    // two further along the arc, which is the remedy for two different arcs leaving at one
    // angle, answers "equal" at every vertex here: the two sequences are the same sequence.
    //
    // The test is exact and needs no tolerance, which is what makes it safe where the enclosed
    // radius of 4b is not. Of the 10,032 arcs in the traced graph and the 5,599 closed ones
    // among them, two read the same both ways: arc 11183, 0.00024 units of substate border at
    // node 363, and arc 11589, 0.00057 units of coast at node 756. Both enclose exactly nothing.
    // The radius test would want a kind restriction to stay safe - 1,326 closed coast arcs score
    // under its threshold - and that restriction is what hides the coast one.
    //
    // It has to run here rather than in 2a or 4b, because neither arc exists when those
    // run: before 5a nodes the crossings the graph holds 9,824 arcs with 11,128 the highest
    // identifier, and afterwards 10,138 with 11,685, so 5a's own splitting is what makes them.
    // It runs before the dangles, so that an arc left with nothing on its far end by one of
    // these going is pruned in the same pass rather than in the next run.
    private static (int Dropped, double Length) RemoveRetracedArcs(ArcGraph arcGraph)
    {
        Arc[] retraced = [.. arcGraph.Arcs.Where(arc => (arc.StartNode == arc.EndNode) && IsRetraced(arc))];
        double length = 0.0;
        foreach (Arc arc in retraced)
        {
            length += GetLength(arc.GetFullCoordinates());
            arcGraph.Arcs.Remove(arc);
        }

        return (retraced.Length, length);
    }

    private static bool IsRetraced(Arc arc)
    {
        Coordinate[] coordinates = arc.GetFullCoordinates();
        for (int i = 0; i < (coordinates.Length / 2); ++i)
        {
            if (!coordinates[i].Equals2D(coordinates[coordinates.Length - 1 - i]))
            {
                return false;
            }
        }

        return true;
    }

    // Where two half-edges leave a node at the same angle, which of them 5f turns onto is
    // arbitrary: the sort falls through to ThenBy(arc.Id), and an identifier is not a direction.
    // Worse, 5a mints identifiers as it splits arcs at crossings, so one shape added elsewhere
    // on the map can renumber the two and silently swap the turn - the partition stops being a
    // function of the geometry.
    //
    // Nothing on this map ties any more. The 109 nodes this once reported were measured before
    // the needles of 2a, the collapsed stubs of 4b, the duplicates of 5c, the grid
    // contraction of 5b and the overshoots pruned above, every one of which removes a reason for
    // two arcs to leave a node along one ray. The two that outlasted all of them were one arc
    // with itself, and 5d above drops those. So this reports rather than repairs, and it is the
    // signal that the repair is wanted: two arcs that genuinely diverge can be ordered by
    // comparing them further along than their first vertex, which is work worth doing when
    // there is something to do it to.
    //
    // Reported for every tie and not only for two different arcs, because after 5d a tie of
    // either kind is the same hazard with the same remedy. A closed arc whose two ends leave
    // collinearly without retracing each other is orderable in exactly the same way, and
    // staying silent about it would leave a blind spot where a warning costs nothing.
    private static void ReportTiedDepartures(Dictionary<Node, DirectedArc[]> outgoing)
    {
        List<string> ties = [];
        foreach ((Node node, DirectedArc[] arcs) in outgoing.OrderBy(pair => pair.Key.Id))
        {
            for (int i = 0; i < (arcs.Length - 1); ++i)
            {
                if (!GetDepartureAngle(arcs[i]).Equals(GetDepartureAngle(arcs[i + 1])))
                {
                    continue;
                }

                ties.Add(
                    arcs[i].Arc == arcs[i + 1].Arc
                        ? string.Create(
                            CultureInfo.InvariantCulture,
                            $"arc {arcs[i].Arc.Id} with itself at node {node.Id}")
                        : string.Create(
                            CultureInfo.InvariantCulture,
                            $"arcs {arcs[i].Arc.Id} and {arcs[i + 1].Arc.Id} at node {node.Id}"));
            }
        }

        if (ties.Count == 0)
        {
            return;
        }

        Log.Warning(
            "{Ties} pairs of half-edges leave a node at the same angle, so which of them the traversal turns "
            + "onto falls to the arc identifier and is arbitrary: {Which}. A face there may come out traced the "
            + "wrong way round. Two different arcs can be put in order by comparing them further along than "
            + "their first vertex; an arc tied with itself runs back over its own line and should have gone in "
            + "5d.",
            ties.Count,
            string.Join(", ", ties.Take(10)) + (ties.Count > 10 ? ", and more" : string.Empty));
    }

    // The next half-edge of the boundary out of the node this one ends at. Clockwise from the way
    // back, as in 5f, and over the node's whole fan rather than the boundary's share of it, so
    // that an arc the set swallowed is stepped over in its proper place in the order instead of
    // being left out of the order altogether.
    private static DirectedArc? GetNextOnBoundary(
        DirectedArc current,
        HashSet<DirectedArc> boundary,
        Dictionary<Node, DirectedArc[]> outgoing,
        Dictionary<DirectedArc, int> positions)
    {
        DirectedArc back = new DirectedArc(current.Arc, !current.IsReversed);
        DirectedArc[] candidates = outgoing[GetEndNode(current)];
        for (int step = 1; step <= candidates.Length; ++step)
        {
            int index = (((positions[back] - step) % candidates.Length) + candidates.Length)
                % candidates.Length;
            DirectedArc candidate = candidates[index];
            if (boundary.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static List<DirectedArc[]> DissolveOneSet(
        IReadOnlyCollection<Face> faces,
        Dictionary<Node, DirectedArc[]> outgoing,
        Dictionary<DirectedArc, int> positions)
    {
        HashSet<DirectedArc> inside = [];
        foreach (Face face in faces)
        {
            foreach (DirectedArc directed in face.Holes.Prepend(face.Shell).SelectMany(ring => ring))
            {
                inside.Add(directed);
            }
        }

        HashSet<DirectedArc> boundary =
        [
            .. inside.Where(directed => !inside.Contains(new DirectedArc(directed.Arc, !directed.IsReversed))),
        ];

        List<DirectedArc[]> rings = [];
        HashSet<DirectedArc> walked = [];
        foreach (DirectedArc start in boundary.OrderBy(directed => directed.Arc.Id)
                     .ThenBy(directed => directed.IsReversed))
        {
            if (!walked.Add(start))
            {
                continue;
            }

            List<DirectedArc> ring = [start];
            DirectedArc current = start;
            while (true)
            {
                DirectedArc? next = GetNextOnBoundary(current, boundary, outgoing, positions);
                if ((next == null) || (next.Value == start))
                {
                    break;
                }

                if (!walked.Add(next.Value))
                {
                    // Every half-edge of the boundary lies on exactly one of its rings, so
                    // reaching one already spoken for means the walk is wrong, not finished.
                    Log.Warning(
                        "Dissolving reached an already walked half-edge on arc {ArcId}; the shape around it "
                        + "will be incomplete.",
                        next.Value.Arc.Id);
                    break;
                }

                ring.Add(next.Value);
                current = next.Value;
            }

            rings.Add([.. ring]);
        }

        return rings;
    }

    private static void ReportBridges(List<(DirectedArc[] Ring, double SignedArea)> rings)
    {
        Dictionary<Arc, int> ringsPerArc = new Dictionary<Arc, int>();
        foreach ((DirectedArc[] Ring, double SignedArea) ring in rings)
        {
            foreach (Arc arc in ring.Ring.Select(directed => directed.Arc).Distinct())
            {
                ringsPerArc[arc] = ringsPerArc.GetValueOrDefault(arc) + 1;
            }
        }

        Arc[] bridges =
        [
            .. ringsPerArc.Where(pair => (pair.Value == 1) && (pair.Key.Kind is ArcKind.Border or ArcKind.Substate))
                .Select(pair => pair.Key)
                .OrderByDescending(arc => arc.GetFullCoordinates().Length),
        ];
        if (bridges.Length == 0)
        {
            return;
        }

        Log.Warning(
            "{Count} border and substate arcs have both of their sides in one ring and so separate nothing. "
            + "Each is a chain that never closes, and the ground it was drawn to enclose gets no face.",
            bridges.Length);

        foreach (Arc arc in bridges.Take(MaximumBridgesReported))
        {
            Coordinate[] line = arc.GetFullCoordinates();
            Log.Warning(
                "Arc {ArcId}, {Kind}, {Points} points from {X0:0.##}, {Y0:0.##} to {X1:0.##}, {Y1:0.##} "
                + "separates nothing.",
                arc.Id,
                arc.Kind,
                line.Length,
                line[0].X,
                line[0].Y,
                line[^1].X,
                line[^1].Y);
        }
    }

    // An open arc that does not reach one step of the noding grid from its own start, whose two
    // nodes are therefore brought together rather than the arc being left in place. Returns how
    // many arcs were contracted and how many nodes that took out.
    //
    // This is the other half of the test below, and the reason the grid is safe to use here
    // where it is not safe for the nodes. Merging any two nodes a grid step apart joins chains
    // the map never joined, because two points that close together are still two points in the
    // data. The two nodes at the two ends of one arc are not two such points: they are already
    // connected by that arc, so bringing them together is an edge contraction, and contracting
    // an edge cannot change what is connected to what. Only the arc's own length goes, and the
    // grid has already said that length is below what it can represent.
    //
    // The arcs this reaches are made rather than drawn. 5a makes every crossing point precise
    // and nothing else ever passes through the model, so a crossing computed where a vertex was
    // already drawn comes out a fraction of a grid step away from it instead of on it: arc 11706
    // joins the noded node 9560 at 2440.3538, 857.6366 to the drawn node 633 at 2440.35376,
    // 857.636597, and is 0.00004 units long. 55 arcs on this map, 25 of them with their two
    // nodes at exactly one coordinate, which the exact node merge below was already putting
    // together; the other 30 are what is new here, the longest 0.000166 units.
    //
    // The test is metric rather than positional, which is the one place this does not mirror the
    // closed-arc test below. Asking instead whether the grid puts every coordinate at the same
    // point as the start node - the exact mirror - catches 53 of the 55 and misses two, because
    // whether two points a fraction of a step apart round together depends on where in the cell
    // they happen to fall. Measuring the distance says what was meant without that luck.
    //
    // A near-zero arc is what left an island shared between Avanarsuaq and Qausuittuq with no
    // face at all. Its coast is a circuit of three arcs, 2317 units of it, with a substate
    // border across the middle dividing the two countries, and it should trace as two faces.
    // Instead the traversal came down the 679-unit arc into node 9560, found the 0.00004-unit
    // stub first in the departure order, walked into node 633 - which has nothing else on it, so
    // the stub is a bridge by itself - and straight back, and from there retraced every arc it
    // had already walked in the other direction: one ring of ten half-edges with a signed area
    // of exactly zero, bounding nothing, and the whole landmass inside no face. That one island
    // was 16,851 of the 17,610 square units of land that no face covered.
    //
    // Seven of the contractions leave a sibling behind: another arc joined the same two nodes
    // and is longer than a grid step, so it becomes a closed ring rather than going. All seven
    // are tracer loops of 0.18 units or less on nodes of degree four or more, where two real
    // arcs already meet and nothing is hidden, so they are left as they are.
    private static (int Contracted, int Collapsed) ContractDegenerateArcs(
        ArcGraph arcGraph,
        PrecisionModel precisionModel)
    {
        double step = 1.0 / precisionModel.Scale;
        Arc[] degenerate =
        [
            .. arcGraph.Arcs.Where(
                arc => (arc.StartNode != arc.EndNode)
                    && Array.TrueForAll(
                        arc.GetFullCoordinates(),
                        coordinate => coordinate.Distance(arc.StartNode.Coordinate) <= step)),
        ];
        if (degenerate.Length == 0)
        {
            return (0, 0);
        }

        // Union-find, because two such arcs can meet: contracting them one pair at a time would
        // leave the result depending on the order they were taken in.
        Dictionary<Node, Node> groups = new Dictionary<Node, Node>();
        foreach (Arc arc in degenerate)
        {
            Node start = FindRoot(groups, arc.StartNode);
            Node end = FindRoot(groups, arc.EndNode);
            if (start != end)
            {
                groups[start.Id > end.Id ? start : end] = start.Id > end.Id ? end : start;
            }
        }

        // The node of lowest identifier in each group survives, with its own coordinate, as it
        // does when coincident nodes are merged below.
        Dictionary<Node, Node> replacements = new Dictionary<Node, Node>();
        foreach (Node node in groups.Keys)
        {
            Node survivor = FindRoot(groups, node);
            if (survivor != node)
            {
                replacements.Add(node, survivor);
            }
        }

        ReplaceNodes(arcGraph, replacements);

        return (degenerate.Length, replacements.Count);
    }

    private static Node FindRoot(Dictionary<Node, Node> groups, Node node)
    {
        Node root = node;
        while (groups.TryGetValue(root, out Node? parent))
        {
            root = parent;
        }

        return root;
    }

    // Nodes sharing a coordinate collapse onto the one with the lowest identifier, so that
    // the same graph always collapses the same way. They arise from both ends of the
    // pipeline: noding a crossing adds a node at the rounded intersection, which may land on
    // a node already there, and cutting a duplicate tracer in 2b adds nodes at the ends of
    // what it cut.
    private static int MergeCoincidentNodes(ArcGraph arcGraph)
    {
        // The node of lowest identifier at each coordinate survives, so that the same graph
        // always collapses the same way.
        Dictionary<(double X, double Y), Node> survivors = new Dictionary<(double X, double Y), Node>();
        Dictionary<Node, Node> replacements = new Dictionary<Node, Node>();
        foreach (Node node in arcGraph.Nodes.OrderBy(node => node.Id))
        {
            (double X, double Y) key = (node.Coordinate.X, node.Coordinate.Y);
            if (survivors.TryGetValue(key, out Node? survivor))
            {
                replacements.Add(node, survivor);
            }
            else
            {
                survivors.Add(key, node);
            }
        }

        ReplaceNodes(arcGraph, replacements);

        return replacements.Count;
    }

    // Points every arc end at the survivor of its group and drops the nodes that went.
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

        Node[] remaining = [.. arcGraph.Nodes.Where(node => !replacements.ContainsKey(node))];
        arcGraph.Nodes.Clear();
        foreach (Node node in remaining)
        {
            arcGraph.Nodes.Add(node);
        }
    }

    // Where the noding grid would put a coordinate. What follows from two coming out the same
    // is for the caller to decide, and is not the same thing in both places above: the drawn
    // geometry never passes through this model, so agreeing here is not evidence that two
    // points in the data are one.
    private static (double X, double Y) GetPoint(PrecisionModel precisionModel, Coordinate coordinate)
    {
        Coordinate point = coordinate.Copy();
        precisionModel.MakePrecise(point);
        return (point.X, point.Y);
    }

    // An arc that is all one point, as the noding grid sees it. Only a closed one goes, so
    // that removing it can never disconnect anything, and that condition is what makes the
    // grid safe to use here where it is not safe for the nodes: an open arc this small is
    // left in place rather than its two nodes being brought together. A closed arc with real
    // geometry - an island, a lake shore - is left alone however small, because it has a
    // direction to be traced by.
    //
    // 72 arcs on this map, 18 of which the exact test this replaces did not reach.
    private static int RemoveDegenerateArcs(ArcGraph arcGraph, PrecisionModel precisionModel)
    {
        Arc[] degenerate =
        [
            .. arcGraph.Arcs.Where(
                arc => (arc.StartNode == arc.EndNode)
                    && Array.TrueForAll(
                        arc.GetFullCoordinates(),
                        coordinate => GetPoint(precisionModel, coordinate)
                            == GetPoint(precisionModel, arc.StartNode.Coordinate))),
        ];
        foreach (Arc arc in degenerate)
        {
            arcGraph.Arcs.Remove(arc);
        }

        return degenerate.Length;
    }

    // Two arcs that join the same pair of nodes and run along each other the whole way are
    // one line drawn twice. Both copies leave each of the two nodes they share, the four
    // half-edges interleave in the order 5f sorts them into, and a traversal arriving along
    // one of them takes the other as its next turn and reverses: at node 819 in the sample
    // map the coast left forwards at -22.249 degrees, the border backwards at -18.471, the
    // coast backwards at 158.174 and the border forwards at 161.918. Whatever the reversed
    // traversal then walks into goes into the outline of the landmass rather than into a face
    // of its own, and the countries there get no ground. Siam, Dumarune and Sulka were left
    // with nothing by a border copying the coast, and Mauretania and Altavia came out as one
    // face because a border copied another border.
    //
    // Told apart by what the two enclose between them, over the length of the loop they make
    // - the hydraulic radius of 3d again, since one line drawn twice encloses nothing
    // while two routes enclose a lens. Measured over all 166 pairs of arcs that share both
    // their nodes on this map: 128 of them come to 0.1243 at the widest, the next is 0.2138,
    // and nothing lies between. The rest run from there to 12.78 and are alternative routes.
    // The gap is narrow, 1.7x, but it is the only break anywhere below 0.2, and the same 0.15
    // the holes and the slits are judged by sits inside it.
    //
    // Of those 128, two thirds are one border copying another: 67 border on border, 26 border
    // on coast, 11 substate on coast, 9 substate on substate, 6 border on substate, 4 coast on
    // coast, 4 lake on lake and 1 border on lake.
    //
    // Length is no part of the test and cannot be. Dumarune's duplicate is 0.21 units long
    // and Sulka's 0.0002, far below anything that could be told from a border merely touching
    // what it meets by how long it ran. What makes a pair provable is the two shared nodes,
    // which is noding having already found that the two lines meet at both ends.
    //
    // The coast is kept over a border where the two disagree, because a border cannot divide
    // land from sea; otherwise the lowest identifier is kept, so that the same graph always
    // reduces the same way.
    //
    // This runs after 3c has filled the slits in the coast, and the order matters: a
    // border running along the wall of a slit looks just like a copy of the shore, but it is
    // a real border and the slit is the error. Filled first, no such pair is left to find.
    private static (int Dropped, double Length, double Widest) RemoveDuplicateArcs(ArcGraph arcGraph)
    {
        Dictionary<(int Low, int High), List<Arc>> byPair = new Dictionary<(int Low, int High), List<Arc>>();
        foreach (Arc arc in arcGraph.Arcs.Where(arc => arc.StartNode != arc.EndNode))
        {
            (int Low, int High) key = (Math.Min(arc.StartNode.Id, arc.EndNode.Id),
                Math.Max(arc.StartNode.Id, arc.EndNode.Id));
            if (!byPair.TryGetValue(key, out List<Arc>? arcs))
            {
                arcs = [];
                byPair.Add(key, arcs);
            }

            arcs.Add(arc);
        }

        List<Arc> duplicates = [];
        double length = 0.0;
        double widest = 0.0;
        foreach (List<Arc> arcs in byPair.Values.Where(arcs => arcs.Count > 1))
        {
            List<Arc> kept = [];
            foreach (Arc arc in arcs.OrderBy(arc => arc.Kind is ArcKind.Coast or ArcKind.Lake ? 0 : 1)
                         .ThenBy(arc => arc.Id))
            {
                double radius = kept.Count == 0
                    ? double.PositiveInfinity
                    : kept.Min(other => GetSliverRadius(other, arc));
                if (radius > MaximumDuplicateAreaToLengthRatio)
                {
                    kept.Add(arc);
                    continue;
                }

                duplicates.Add(arc);
                length += GetLength(arc.GetFullCoordinates());
                widest = Math.Max(widest, radius);
            }
        }

        foreach (Arc arc in duplicates)
        {
            arcGraph.Arcs.Remove(arc);
        }

        return (duplicates.Count, length, widest);
    }

    // The sliver the two arcs enclose, as its area over its perimeter. The loop closes on
    // itself by construction, both arcs running between the same two nodes, so the shoelace
    // sum needs no closing term of its own.
    private static double GetSliverRadius(Arc one, Arc other)
    {
        Coordinate[] forwards = one.GetFullCoordinates();
        Coordinate[] backwards = other.GetFullCoordinates();

        // The second arc has to run back the way the first came, so that the two together
        // close a loop. Reversing it when the two set out from the same node does that
        // whichever way round it was stored.
        if (other.StartNode == one.StartNode)
        {
            Array.Reverse(backwards);
        }

        Coordinate[] loop = [.. forwards, .. backwards];
        double area = 0.0;
        double perimeter = 0.0;
        for (int i = 0; i < (loop.Length - 1); ++i)
        {
            area += (loop[i].X * loop[i + 1].Y) - (loop[i + 1].X * loop[i].Y);
            perimeter += loop[i].Distance(loop[i + 1]);
        }

        return perimeter < DegenerateLength ? 0.0 : Math.Abs(area / 2.0) / perimeter;
    }

    private static double GetLength(Coordinate[] coordinates)
    {
        double length = 0.0;
        for (int i = 0; i < (coordinates.Length - 1); ++i)
        {
            length += coordinates[i].Distance(coordinates[i + 1]);
        }

        return length;
    }

    private static List<(DirectedArc[] Ring, double SignedArea)> TraceRings(ArcGraph arcGraph)
    {
        Dictionary<Node, DirectedArc[]> outgoing = BuildOutgoing(arcGraph);

        // Here rather than in BuildOutgoing, which 7a calls again for the dissolve: the graph is
        // the same by then and a second report would say the same thing twice.
        ReportTiedDepartures(outgoing);
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
        return directed.StartNode;
    }

    private static Node GetEndNode(DirectedArc directed)
    {
        return directed.EndNode;
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
