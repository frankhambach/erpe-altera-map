// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Linq;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.OverlayNG;

using Serilog;

public sealed class CoastlineService : ICoastlineService
{
    // A hole in the landmass is either a gap between countries or a real inland sea, and
    // area over perimeter separates them. That ratio is a hydraulic radius: it has units
    // of length and scales linearly with the size of a fixed shape, so it measures both
    // thinness and smallness at once and no separate area test is needed.
    //
    // Measured over the 3094 holes in the sample map, the ratio is sharply bimodal: 2632
    // gaps top out at 0.0955 units and the 462 lakes start again at 0.1965, a span of
    // 2.06x with nothing in it at all. This threshold sits near the middle of that empty
    // valley, so any value between the two bounds below classifies the sample identically.
    private const double MaximumGapAreaToLengthRatio = 0.15;

    // The valley bounds, rounded inwards from the measured extremes. A hole landing inside
    // them is reported, because the separation is what makes the threshold trustworthy
    // rather than a tuned guess: if a future export starts populating the valley, the ratio
    // has stopped being a clean discriminator and the figure needs rethinking, not nudging.
    private const double GapRatioUpperBound = 0.10;

    private const double LakeRatioLowerBound = 0.19;

    // How far a border tracer may run through a hole, against the hole's own half-perimeter -
    // its length, for a hole thin enough to have one - before the hole is a gap between two
    // country shapes rather than a lake. A gap along a border has the border running its whole
    // length; a lake has at most one poking into it.
    //
    // Measured over the 462 holes the ratio above keeps as lakes: two of them come to 18.88
    // and 8.19, and the rest top out at 1.88, a span of 4.4x with nothing in it. Those two are
    // the gap between Padun and Kunmin, 2.49 square units with 3.71 units of border running
    // along it, and a second of 0.98 square units with 1.92 units along it. Both are gaps.
    //
    // Known limitation: the measure is elongation-aware, which is the point - a tracer running
    // the length of a sliver scores far higher than one crossing a blob - but a compact lake
    // crossed straight through would score about 4, so the figure sits above that rather than
    // at the middle of the measured span. Nothing on this map comes near: the highest genuine
    // lake is 1.88.
    private const double MaximumLakeTracerCrossing = 6.0;

    private const double DegenerateLength = 1E-9;

    // Ten times the noding grid. Trimming a tracer against a lake leaves a line's worth of
    // rounding behind, a millimetre or two of it, which is not worth reporting; anything
    // above this is a real stretch of border still sitting in the water.
    private const double ResidueTolerance = 1E-3;

    // How far past the shore to clip, in SVG units: about 6 m, far below the drawing's own
    // resolution of roughly 1.75 units between points.
    private const double ClipMargin = 1E-3;

    // How much of a tracer has to lie within that margin of the shore before the run is a
    // border drawn along the water's edge rather than one that merely ends at it.
    //
    // A border reaching the coast crosses the margin at some angle and loses a sliver of its
    // last segment to it, and the shallower the approach the longer that sliver - the bound is
    // the margin over the sine of the angle, so it is a property of the drawing and not a
    // defect. Measured over the 321 tracers that lose anything at all to the margin on this
    // map: 304 lose under 0.01 units, a tail of 8 reaches 0.093, which is an approach of half a
    // degree, and then the nine runs that follow the shore start again at 0.159 and go to 1.415.
    //
    // So this sits in a gap of 1.7x rather than in an empty decade, and what makes it safe is
    // the geometry either side of it rather than the width of the gap: below it is one segment's
    // worth of crossing the shore, above it a stretch drawn along the shore. Clipping without a
    // minimum at all was measured and is not harmless - it cuts 350 tracers, and the welding
    // that re-attaches them then leaves Lebanon and Osurfine with no geometry and fuses Abeline
    // and Osurfine into one face.
    private const double MinimumShoreRun = 0.1;

    // How close a run of shoreline has to come back to where it started to count as having
    // doubled back on itself. It is also the most that filling one can move the shoreline,
    // since the run is replaced by the vertex it set out from, and half a unit is under a
    // third of the drawing's own resolution.
    private const double MaximumSlitWidth = 0.5;

    // How long the out and back has to be. This is what keeps the coastline's own jitter out.
    // Scanning for runs of a fifth of a unit and up finds 51,923 of them that enclose sea
    // with a small radius, and 51,541 of those are shorter than 0.6 units: two consecutive
    // drawn segments folding onto each other. The population is down to two by one unit,
    // which is where the gaps between shapes begin.
    private const double MinimumSlitLength = 1.0;

    // How far ahead to look for the way back, which is what keeps the scan quadratic in
    // practice rather than over the whole of a ring that can run to 118,800 points. The
    // longest gap filled is reported, so a figure creeping up towards this one is the sign
    // that the search is being cut short.
    private const double MaximumSlitSearch = 50.0;

    private readonly CoordinateSequenceFactory coordinateSequenceFactory;

    private readonly GeometryFactory geometryFactory;

    public CoastlineService(GeometryFactory geometryFactory)
    {
        ArgumentNullException.ThrowIfNull(geometryFactory);

        this.geometryFactory = geometryFactory;
        this.coordinateSequenceFactory = geometryFactory.CoordinateSequenceFactory;
    }

    // Returns the area the faces traced from this graph have to come to, which is what
    // Phase 4 checks itself against: the land, plus every hole that was discarded as a gap
    // since nothing bounds those any more, plus every lake, since a lake is a face of its
    // own as well as a hole in the land around it.
    public double AddArcs(IEnumerable<Polygon> polygons, ArcGraph arcGraph)
    {
        ArgumentNullException.ThrowIfNull(polygons);
        ArgumentNullException.ThrowIfNull(arcGraph);

        // Only the sea-facing boundary of the landmass is wanted. Internal borders are
        // governed by the tracer arcs, so unioning the countries is what discards the
        // shapes' own disagreeing versions of them.
        Polygon[] land = ExtractPolygons(
                UnaryUnionNG.Union(
                    polygons.ToArray(),
                    this.geometryFactory,
                    this.geometryFactory.PrecisionModel))
            .ToArray();

        // Built once, before the holes are judged, and from the tracers as drawn: 2c-bis has
        // not trimmed them yet, so a border that runs through a gap is still all there.
        STRtree<LineString> tracers = new STRtree<LineString>();
        foreach (Arc arc in arcGraph.Arcs.Where(arc => arc.Kind is ArcKind.Border or ArcKind.Substate))
        {
            LineString line = this.geometryFactory.CreateLineString(arc.GetFullCoordinates());
            tracers.Insert(line.EnvelopeInternal, line);
        }

        List<Polygon> lakes = [];
        List<Coordinate[]> shells = [];
        List<(double Radius, double Length)> slits = [];
        List<double> keptInlets = [];
        int lakeCount = 0;
        int gapCount = 0;
        double gapArea = 0.0;
        int ambiguousCount = 0;
        int crossedCount = 0;
        foreach (Polygon polygon in land)
        {
            // Only the shells. A gap enclosed in a lake is a hole of the lake's own and not
            // what this is for, and the sign that tells a slit from a spit is the other way
            // round on a ring that bounds water rather than land.
            Coordinate[] shell = FillSlits(polygon.Shell, slits, keptInlets);
            shells.Add(shell);
            arcGraph.AddArc(ArcKind.Coast, this.coordinateSequenceFactory.Create(shell));

            foreach (LinearRing hole in polygon.Holes)
            {
                double ratio = this.GetAreaToLengthRatio(hole);
                if ((ratio > GapRatioUpperBound) && (ratio < LakeRatioLowerBound))
                {
                    ++ambiguousCount;
                }

                // Either test is enough to call a hole a gap, and each catches what the other
                // cannot. The ratio alone misses a gap wide enough to look like water, which is
                // what left Padun and Kunmin sharing a face. The crossing alone misses the 868
                // gaps with no tracer anywhere near them.
                double crossing = ratio < MaximumGapAreaToLengthRatio
                    ? 0.0
                    : this.GetTracerCrossing(hole, tracers, ratio);
                if (crossing > MaximumLakeTracerCrossing)
                {
                    ++crossedCount;
                }

                if ((ratio < MaximumGapAreaToLengthRatio) || (crossing > MaximumLakeTracerCrossing))
                {
                    ++gapCount;
                    gapArea += this.geometryFactory.CreatePolygon(hole).Area;
                }
                else
                {
                    arcGraph.AddArc(ArcKind.Lake, hole.CoordinateSequence);
                    lakes.Add(this.geometryFactory.CreatePolygon(hole));
                    ++lakeCount;
                }
            }
        }

        (int clipped, double removed) = this.ClipTracers(arcGraph, lakes);
        (int shoreClipped, double shoreRemoved, int stranded, double discarded) =
            this.ClipShoreRuns(arcGraph, shells);

        Log.Information(
            "Built {ShellCount} coast shells, kept {LakeCount} lakes and discarded {GapCount} holes as gaps, "
            + "enclosing {Area:0.0} square units of land and {LakeArea:0.0} of lake. Trimmed {Clipped} "
            + "tracers back to the lake shore, "
            + "removing {Removed:0.00} units of line.",
            land.Length,
            lakeCount,
            gapCount,
            land.Sum(polygon => polygon.Area),
            lakes.Sum(lake => lake.Area),
            clipped,
            removed);

        if (crossedCount > 0)
        {
            Log.Information(
                "Discarded {CrossedCount} of those holes as gaps because a border runs through them rather than "
                + "merely into them, which the area to length ratio had taken for water.",
                crossedCount);
        }

        if (shoreClipped > 0)
        {
            Log.Information(
                "Cut {Clipped} tracers back off the shore, {Removed:0.00} units of line drawn along the water's "
                + "edge rather than up to it, and dropped {Stranded} pieces of {Discarded:0.00} units that the "
                + "cut left seaward of the coast.",
                shoreClipped,
                shoreRemoved,
                stranded,
                discarded);
        }

        if (slits.Count > 0)
        {
            // Both ends of the threshold, every run, because the threshold cuts through a
            // population rather than a gap in one. If these two ever stop bracketing it
            // closely the shape of that population has changed and the figure wants
            // re-measuring rather than nudging.
            Log.Information(
                "Filled {SlitCount} gaps between shapes that reach the sea rather than being enclosed as a hole, "
                + "{SlitLength:0.00} units of shoreline in all and the longest of them {Longest:0.00}. The "
                + "widest filled measures {Widest:0.#####} on the area to length ratio the holes are judged by, "
                + "the narrowest inlet left alone {Narrowest:0.#####}.",
                slits.Count,
                slits.Sum(slit => slit.Length),
                slits.Max(slit => slit.Length),
                slits.Max(slit => slit.Radius),
                keptInlets.Count == 0 ? MaximumGapAreaToLengthRatio : keptInlets.Min());
        }

        if (ambiguousCount > 0)
        {
            Log.Warning(
                "{AmbiguousCount} holes have an area to length ratio between {Lower} and {Upper}, a band that was "
                + "empty when the gap threshold was chosen. Lakes and gaps are no longer cleanly separated.",
                ambiguousCount,
                GapRatioUpperBound,
                LakeRatioLowerBound);
        }

        // The ground the filled slits used to take out of the land is land again now, and the
        // hydraulic radius times the length of a slit is the area it covered.
        return land.Sum(polygon => polygon.Area)
            + slits.Sum(slit => slit.Radius * slit.Length)
            + gapArea
            + lakes.Sum(lake => lake.Area);
    }

    // A gap between two country shapes is a hole in the union and the test above throws it
    // away, unless it happens to reach the sea: then it is not a hole at all but a slit in
    // the shell, which the union walks up one wall of and back down the other. Nothing
    // encloses the ground beside such a slit, so the border drawn along the same line gets
    // traced into the outline of the landmass rather than into a face, and the countries on
    // either side of it are left with no ground at all. That is what left Maharath and
    // Sanjan with nothing: Maharath's shape is drawn 0.03 units off Sanjan's for the first
    // 3.77 units inland from the coast they share, and the wedge between the two opens onto
    // the sea.
    //
    // Three tests, cheapest first. The run has to come back to within MaximumSlitWidth of
    // where it started, it has to be at least MinimumSlitLength long, and it has to enclose
    // sea rather than land. The last is what does most of the work: of the 9352 runs on this
    // map that come back on themselves at all, 8778 enclose land and are headlands, spits and
    // narrow capes that have to be left exactly as they are, and 574 enclose sea.
    //
    // What is left after those three is judged by the hydraulic radius of Phase 2, because a
    // gap is a gap whether it is enclosed as a hole or open to the sea. Here, though, that
    // figure is a judgement and not a measurement: those 574 runs have radii running all the
    // way from 0.0005 to 1.0, thickest between 0.07 and 0.12, and they thin out across the
    // threshold rather than stopping - 18 of them in the 0.02 below 0.15 and 6 in the 0.02
    // above, the widest filled measuring 0.1474 against the 0.1522 of the narrowest left
    // alone. There is no empty valley to put the line in, as there is for the holes.
    //
    // What makes the figure safe is that the answer does not depend on it. Run at 0.12, 0.15
    // and 0.20 the number of gaps filled moves from 460 to 505 to 508, and every number that
    // matters is identical: 47 arcs separating nothing, 0.301% of the land inside no face, 8
    // countries without geometry. The gaps that cost a country its ground are far below any
    // of those thresholds.
    //
    // Known limitation: a slit straddling the ring's own first vertex is cut in part rather
    // than whole, because the closing vertex is never removed and the scan does not wrap.
    // Nothing here detects that case, so it is not known whether this map has one.
    private static Coordinate[] FillSlits(
        LinearRing ring,
        List<(double Radius, double Length)> slits,
        List<double> kept)
    {
        List<Coordinate> coordinates = [.. ring.Coordinates];
        bool counterClockwise = Orientation.IsCCW(ring.Coordinates);
        int i = 0;
        while (i < (coordinates.Count - 2))
        {
            int found = -1;
            double radius = 0.0;
            double length = 0.0;
            double run = 0.0;
            double area = 0.0;
            for (int j = i + 1; j < (coordinates.Count - 1); ++j)
            {
                run += coordinates[j - 1].Distance(coordinates[j]);
                area += (coordinates[j - 1].X * coordinates[j].Y) - (coordinates[j].X * coordinates[j - 1].Y);
                if (run > MaximumSlitSearch)
                {
                    break;
                }

                if ((run < MinimumSlitLength) || (coordinates[i].Distance(coordinates[j]) > MaximumSlitWidth))
                {
                    continue;
                }

                // Closing the loop before taking its area is not optional. The shoelace
                // terms are twice the signed area of a triangle with the origin, so an
                // unclosed chain out at x = 4412 carries a residual of some 150 square
                // units where the slit itself covers 0.11.
                double enclosed = (area
                    + ((coordinates[j].X * coordinates[i].Y) - (coordinates[i].X * coordinates[j].Y))) / 2.0;
                double ratio = Math.Abs(enclosed) / run;
                if ((enclosed > 0.0) != counterClockwise)
                {
                    if (ratio <= MaximumGapAreaToLengthRatio)
                    {
                        found = j;
                        radius = ratio;
                        length = run;
                    }
                    else
                    {
                        kept.Add(ratio);
                    }
                }

                // The first return and no other. A slit's two walls are next to each other
                // in the ring, so the first place it comes back to where it started, once it
                // has gone anywhere at all, is the other wall; if what comes back first is
                // something else then this is not the mouth of a slit, and carrying on to
                // look for a thin loop further along would be looking for one that is not
                // there. On this map it makes no difference either way, both ways round
                // filling the same 505.
                break;
            }

            // A ring needs four coordinates to enclose anything, so a cut that would take it
            // below that is no cut at all.
            if ((found < 0) || ((coordinates.Count - (found - i)) < 4))
            {
                ++i;
                continue;
            }

            slits.Add((radius, length));
            coordinates.RemoveRange(i + 1, found - i);
        }

        return [.. coordinates];
    }

    // A piece that still ends where the arc did keeps the arc's node; a piece that ends at
    // the water's edge gets a new one, which welding then attaches to the lake's ring.
    private static Node GetNode(ArcGraph arcGraph, Arc arc, Coordinate coordinate)
    {
        if (arc.StartNode.Coordinate.Equals2D(coordinate))
        {
            return arc.StartNode;
        }

        return arc.EndNode.Coordinate.Equals2D(coordinate) ? arc.EndNode : arcGraph.AddNode(coordinate);
    }

    private static IEnumerable<LineString> GetLines(Geometry geometry)
    {
        return geometry switch
            {
                GeometryCollection collection => collection.Geometries.SelectMany(GetLines),
                LineString line => [line],
                _ => [],
            };
    }

    private static IEnumerable<Polygon> ExtractPolygons(Geometry geometry)
    {
        return geometry switch
            {
                GeometryCollection geometryCollection => geometryCollection.OfType<Polygon>(),
                Polygon polygon => [polygon],
                _ => [],
            };
    }

    // Borders are drawn over lakes rather than stopping at them: 136 tracers in the sample
    // map reach inside one, though never by more than 0.783 units and mostly by less than
    // 0.05. Left alone they shave slivers off the lake when the faces are traced, so a lake
    // ends up partly owned by the countries around it. Cutting each tracer back to the shore
    // here, before welding, means the trimmed end is welded onto the lake ring instead and
    // the lake stays a single face.
    private (int Clipped, double Removed) ClipTracers(ArcGraph arcGraph, List<Polygon> lakes)
    {
        if (lakes.Count == 0)
        {
            return (0, 0.0);
        }

        STRtree<Polygon> lakeIndex = new STRtree<Polygon>();
        foreach (Polygon lake in lakes)
        {
            lakeIndex.Insert(lake.EnvelopeInternal, lake);
        }

        Arc[] originals = arcGraph.Arcs.ToArray();
        arcGraph.Arcs.Clear();
        int clipped = 0;
        double removed = 0.0;
        foreach (Arc arc in originals)
        {
            if (arc.Kind is not (ArcKind.Border or ArcKind.Substate))
            {
                arcGraph.Arcs.Add(arc);
                continue;
            }

            LineString line = this.geometryFactory.CreateLineString(arc.GetFullCoordinates());
            Polygon[] overlapping = lakeIndex.Query(line.EnvelopeInternal).ToArray();
            if (overlapping.Length == 0)
            {
                arcGraph.Arcs.Add(arc);
                continue;
            }

            // Against a slightly grown lake, not the lake itself. A border that follows the
            // shore sits within rounding of it rather than cleanly inside, so differencing
            // the lake as drawn leaves it in place: it then appears twice over, once as the
            // lake shore and once as the border, and the face traversal can pick up either.
            // Growing by a hair takes those with it. A border that merely ends at the shore
            // is pulled back by the same hair and welded onto the ring again in Phase 3.
            Geometry trimmed = overlapping.Aggregate(
                (Geometry)line,
                (current, lake) => current.IsEmpty ? current : current.Difference(lake.Buffer(ClipMargin)));
            if (Math.Abs(trimmed.Length - line.Length) < DegenerateLength)
            {
                arcGraph.Arcs.Add(arc);
                continue;
            }

            ++clipped;
            removed += line.Length - trimmed.Length;
            foreach (LineString piece in GetLines(trimmed).Where(piece => piece.NumPoints >= 2))
            {
                arcGraph.AddArc(
                    arc.Kind,
                    GetNode(arcGraph, arc, piece.GetCoordinateN(0)),
                    GetNode(arcGraph, arc, piece.GetCoordinateN(piece.NumPoints - 1)),
                    this.coordinateSequenceFactory.Create(piece.Coordinates[1..^1]));
            }
        }

        // No tracer may be left inside a lake, or it will shave a sliver off when the faces
        // are traced and the lake will end up partly owned by a country.
        double[] residues = arcGraph.Arcs.Where(arc => arc.Kind is ArcKind.Border or ArcKind.Substate)
            .Select(arc => this.geometryFactory.CreateLineString(arc.GetFullCoordinates()))
            .Select(line => lakeIndex.Query(line.EnvelopeInternal)
                .Sum(lake => line.Intersection(lake).Length))
            .Where(residue => residue > ResidueTolerance)
            .OrderByDescending(residue => residue)
            .ToArray();

        if (residues.Length > 0)
        {
            Log.Warning(
                "{Remaining} tracers still run inside a lake after trimming, by {Total:0.####} units in all, "
                + "the worst by {Worst:0.####}.",
                residues.Length,
                residues.Sum(),
                residues[0]);
        }

        return (clipped, removed);
    }

    // A border drawn along the coast rather than up to it is the same defect as a border drawn
    // through a lake, and it arrives by a different route. 2c-ter fills the slits that open onto
    // the sea, but a slit the fill will not take survives in the shell as a hairline needle, and
    // where a tracer was drawn along the same line the shore and the border carry it twice over.
    // Both then bound the faces traced there, which is a segment three rings use at once: Khumer
    // and Siam over 1.4 units of one, Namiba over 0.5 of another. The two slits behind those
    // miss the fill for different reasons - one comes back after 0.64 units, under its minimum
    // of 1, and the other never comes back within 0.74 of its mouth, over its maximum of 0.5.
    //
    // Cut against the shore as the coast arcs carry it, which is after the slits are filled.
    // Cutting against the shells instead would take the borders that run up a filled slit, and
    // those are the real borders 2c-ter exists to give back.
    //
    // Unlike a lake, the water here is not forbidden ground: the sea's edge is a line a border
    // legitimately ends at, and the coast is where it should. So only a run along the edge is
    // cut, and only one longer than MinimumShoreRun, which is what separates a border drawn in
    // the water from one crossing the margin on its way to the shore.
    //
    // What the cut leaves seaward of the coast bounds nothing and goes with it. One piece does
    // on this map, 0.4766 units past the tip of the needle it was drawn along.
    private (int Clipped, double Removed, int Stranded, double Discarded) ClipShoreRuns(
        ArcGraph arcGraph,
        List<Coordinate[]> shells)
    {
        STRtree<(Coordinate Start, Coordinate End)> shore = new STRtree<(Coordinate, Coordinate)>();
        STRtree<IPreparedGeometry> land = new STRtree<IPreparedGeometry>();
        PreparedGeometryFactory factory = new PreparedGeometryFactory();
        foreach (Coordinate[] shell in shells)
        {
            Polygon polygon = this.geometryFactory.CreatePolygon(shell);
            land.Insert(polygon.EnvelopeInternal, factory.Create(polygon));
            for (int i = 0; i < (shell.Length - 1); ++i)
            {
                shore.Insert(new Envelope(shell[i], shell[i + 1]), (shell[i], shell[i + 1]));
            }
        }

        Arc[] originals = arcGraph.Arcs.ToArray();
        arcGraph.Arcs.Clear();
        int clipped = 0;
        double removed = 0.0;
        int stranded = 0;
        double discarded = 0.0;
        foreach (Arc arc in originals)
        {
            if (arc.Kind is not (ArcKind.Border or ArcKind.Substate))
            {
                arcGraph.Arcs.Add(arc);
                continue;
            }

            Coordinate[] coordinates = arc.GetFullCoordinates();
            LineString line = this.geometryFactory.CreateLineString(coordinates);
            Geometry margin = this.GetShoreMargin(coordinates, shore);
            Geometry trimmed = margin.IsEmpty ? line : line.Difference(margin);
            double run = line.Length - trimmed.Length;
            if (run < MinimumShoreRun)
            {
                arcGraph.Arcs.Add(arc);
                continue;
            }

            ++clipped;
            removed += run;

            // One by one, because a border drawn along the shore is a defect in the source map
            // rather than something this pipeline should have to carry: the slit the two share
            // is two shapes disagreeing about where the coast is, and the remedy is to draw them
            // the same.
            Coordinate at = line.Intersection(margin).Coordinates[0];
            Log.Warning(
                "{Kind} arc {ArcId} runs along the coast for {Run:0.####} units from {X:0.##}, {Y:0.##}. The "
                + "shore there is a slit the border was drawn along; the stretch in the water was cut out.",
                arc.Kind,
                arc.Id,
                run,
                at.X,
                at.Y);

            foreach (LineString piece in GetLines(trimmed).Where(piece => piece.NumPoints >= 2))
            {
                if (!this.IsOnLand(piece, land))
                {
                    ++stranded;
                    discarded += piece.Length;
                    continue;
                }

                arcGraph.AddArc(
                    arc.Kind,
                    GetNode(arcGraph, arc, piece.GetCoordinateN(0)),
                    GetNode(arcGraph, arc, piece.GetCoordinateN(piece.NumPoints - 1)),
                    this.coordinateSequenceFactory.Create(piece.Coordinates[1..^1]));
            }
        }

        return (clipped, removed, stranded, discarded);
    }

    // The shore near one tracer, grown into the margin the tracer is cut against. Gathered per
    // segment rather than over the tracer's whole envelope, so that a border running past a bay
    // pulls in the shore it actually runs along and not the far side of the water.
    private Geometry GetShoreMargin(
        Coordinate[] coordinates,
        STRtree<(Coordinate Start, Coordinate End)> shore)
    {
        HashSet<(Coordinate Start, Coordinate End)> near = [];
        for (int i = 0; i < (coordinates.Length - 1); ++i)
        {
            Envelope envelope = new Envelope(coordinates[i], coordinates[i + 1]);
            envelope.ExpandBy(ClipMargin);
            foreach ((Coordinate Start, Coordinate End) segment in shore.Query(envelope))
            {
                near.Add(segment);
            }
        }

        return near.Count == 0
            ? LineString.Empty
            : this.geometryFactory
                .CreateMultiLineString(
                    [.. near.Select(segment => this.geometryFactory.CreateLineString([segment.Start, segment.End]))])
                .Buffer(ClipMargin);
    }

    // Which side of the shore a piece of a cut tracer lies on. It cannot cross the margin it was
    // cut against, so one point on it answers for all of it - the midpoint of its first segment,
    // since an end of it sits on the margin's own edge where the answer is a coin toss.
    //
    // The shells, holes and all: a hole in the land is either a lake, which the tracers have
    // already been clipped against, or a gap 2c discarded, and the ground a discarded gap
    // covers is land again.
    private bool IsOnLand(LineString piece, STRtree<IPreparedGeometry> land)
    {
        Coordinate first = piece.GetCoordinateN(0);
        Coordinate second = piece.GetCoordinateN(1);
        Point point = this.geometryFactory.CreatePoint(
            new Coordinate((first.X + second.X) / 2.0, (first.Y + second.Y) / 2.0));

        return land.Query(point.EnvelopeInternal).Any(shell => shell.Contains(point));
    }

    // How far the tracers run through a hole, against the hole's own half-perimeter. For a
    // hole thin enough to be a gap that half-perimeter is its length, so the measure says what
    // fraction of the gap the border follows; dividing instead by the hydraulic radius, which
    // is the same thing scaled, keeps the shape of the hole in the answer, and that is what
    // separates a border following a sliver from one crossing a pool.
    private double GetTracerCrossing(LinearRing hole, STRtree<LineString> tracers, double ratio)
    {
        if (ratio < DegenerateLength)
        {
            return 0.0;
        }

        Polygon polygon = this.geometryFactory.CreatePolygon(hole);
        double inside = tracers.Query(polygon.EnvelopeInternal)
            .Sum(tracer => tracer.Intersection(polygon).Length);

        return inside / ratio;
    }

    private double GetAreaToLengthRatio(LinearRing hole)
    {
        // A degenerate ring would divide by zero and come out as NaN, which compares false
        // against the threshold and would be kept as a lake. Treat it as a gap instead.
        Polygon polygon = this.geometryFactory.CreatePolygon(hole);
        return polygon.Length < DegenerateLength ? 0.0 : polygon.Area / polygon.Length;
    }
}
